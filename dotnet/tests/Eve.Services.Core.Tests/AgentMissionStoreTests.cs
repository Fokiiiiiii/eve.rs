using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class AgentsPgFactAttribute : FactAttribute
{
    public AgentsPgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

public class AgentMissionStoreTests
{
    private const long Pilot = 140000011;
    private const long Agent = 3008416;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentMissionContent Courier = new(5001, 9100, "", "courier", true);

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static AgentMissionRecord Record(AgentMissionState state, long agentId = Agent) => new(
        Pilot, agentId, state, Courier,
        OfferedAt: Now, ExpiresAt: Now.AddDays(7), AcceptedAt: state == AgentMissionState.Accepted ? Now : null,
        CompletedAt: null, DeclineUntil: state == AgentMissionState.Declined ? Now.AddHours(4) : null, QuitAt: null);

    [Fact]
    public void The_store_missions_are_laid_over_the_game_store_record_and_only_the_journalled_states_stay()
    {
        var legacy = JsonNode.Parse("""
        { "missionsByAgentID": { "3008417": { "runtimeStatus": "accepted", "missionNameID": 77, "missionTypeLabel": "courier" },
                                 "3008416": { "runtimeStatus": "accepted", "missionNameID": 1, "missionTypeLabel": "old" } } }
        """)!.AsObject();

        var merged = AgentMissionStoreRecords.Overlay(legacy, [Record(AgentMissionState.Offered), Record(AgentMissionState.Declined, 3008418)]);

        var missions = merged["missionsByAgentID"]!.AsObject();
        Assert.Equal("offered", missions[Agent.ToString()]!["runtimeStatus"]!.GetValue<string>());
        Assert.Equal(5001, missions[Agent.ToString()]!["missionID"]!.GetValue<long>());
        Assert.Equal(9100, missions[Agent.ToString()]!["missionNameID"]!.GetValue<long>());
        Assert.Equal("courier", missions[Agent.ToString()]!["missionTypeLabel"]!.GetValue<string>());
        Assert.Equal(Now.AddDays(7).UtcDateTime.ToFileTimeUtc(), missions[Agent.ToString()]!["expiresAtFileTime"]!.GetValue<long>());
        Assert.Equal("accepted", missions["3008417"]!["runtimeStatus"]!.GetValue<string>());
        Assert.False(missions.ContainsKey("3008418"));
        Assert.Equal(2, missions.Count);
        Assert.Equal(legacy["missionsByAgentID"]!["3008417"]!.ToJsonString(), missions["3008417"]!.ToJsonString());
    }

    [Fact]
    public async Task The_journal_shows_the_offer_the_store_holds()
    {
        var sink = new Sink();
        var session = new ClientSession(sink) { UserId = 7, CharacterId = Pilot };
        session.Set("charid", new PyInt(Pilot));
        var service = new AgentMissionService(
            (characterId, _) => ValueTask.FromResult<JsonObject?>(
                AgentMissionStoreRecords.Overlay(null, [Record(AgentMissionState.Offered)])),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null));

        var result = await service.CallAsync(new ServiceCall
        {
            Method = "GetMyJournalDetails", Args = [], Session = session, CallId = new PyInt(1), Kwargs = PyNone.Instance,
        });

        var missions = Assert.IsType<PyList>(Assert.IsType<PyTuple>(result).Items[0]);
        var row = Assert.IsType<PyTuple>(Assert.Single(missions.Items));
        Assert.Equal(1L, ((PyInt)row.Items[0]).Value);
        Assert.Equal(Agent, ((PyInt)row.Items[4]).Value);
    }

    [AgentsPgFact]
    public async Task The_character_is_told_once_for_each_committed_change_and_never_for_a_refusal()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "agt_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await AgentMissionStore.MigrateAsync(source, schema);
            var store = new AgentMissionStore(source, schema);
            var sink = new Sink();
            var pilot = new ClientSession(sink) { UserId = 7, CharacterId = Pilot };
            var sessions = new CharacterSessions();
            sessions.Enter(Pilot, pilot);
            using var announcer = new AgentMissionAnnouncer(store, sessions);

            await store.OfferAsync(Pilot, Agent, Courier, Now, "offer:1");
            await Assert.ThrowsAsync<AgentMissionRefusedException>(() => store.AcceptAsync(Pilot, 3008417, Now, "accept:none"));
            await store.AcceptAsync(Pilot, Agent, Now, "accept:1");
            await store.AcceptAsync(Pilot, Agent, Now, "accept:1");

            var expectedOffer = ClientNotification.Build(7, pilot.NodeId, "OnAgentMissionChange", "clientID", [new PyStr("offered"), new PyInt(Agent)], 0);
            var expectedAccept = ClientNotification.Build(7, pilot.NodeId, "OnAgentMissionChange", "clientID", [new PyStr("accepted"), new PyInt(Agent)], 1);
            Assert.Equal(2, sink.Packets.Count);
            Assert.Equal(MarshalCodec.Encode(expectedOffer), MarshalCodec.Encode(sink.Packets[0]));
            Assert.Equal(MarshalCodec.Encode(expectedAccept), MarshalCodec.Encode(sink.Packets[1]));
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
