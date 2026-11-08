using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private const long ResearchPilot = 140000301;
    private const long ResearchAgent = 3008416;
    private const long ResearchSkill = 11433;

    [PgFact]
    public async Task Agent_mgr_journal_projects_the_persisted_research_project_through_host_registration()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "agent_research_journal_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await AgentMissionStore.MigrateAsync(source, schema);
            await ResearchProjectStore.MigrateAsync(source, schema);
            var projects = new ResearchProjectStore(source, schema);
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(FixedNowMs) - TimeSpan.FromDays(1);
            await projects.StartAsync(new ResearchProject(ResearchPilot, ResearchAgent, ResearchSkill, startedAt, 24.5, 3), 1);

            var agent = new AgentMissionAgentData(
                AgentId: ResearchAgent, AgentTypeId: 2, DivisionId: 22, Level: 3, StationId: 60000111,
                CorporationId: 1000001, IsLocatorAgent: false, IsInSpace: false, CareerId: null,
                ImportantMission: false, MissionKind: "", MissionTypeLabel: "", SpecialityId: null,
                StationTypeId: 1531, BloodlineId: null, SolarSystemId: 30000001, DungeonId: null,
                SpawnPointId: null, TypeId: null);
            var service = new AgentMissionService(
                (_, _) => ValueTask.FromResult<JsonObject?>(null),
                (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
                (characterId, ct) => AgentResearchJournalRows.ReadAsync(
                    projects, new Dictionary<long, AgentMissionAgentData> { [ResearchAgent] = agent },
                    characterId, DateTimeOffset.FromUnixTimeMilliseconds(FixedNowMs), ct),
                rpcCallbacks: new AgentMissionRpcCallbacks(
                    ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent])));

            Start(
                strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.Set("charid", new PyInt(ResearchPilot));
                    session.Set("userid", new PyInt(7));
                });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            await front.ReceiveAsync();

            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.Call(open, 1, "agentMgr", "GetMyJournalDetails"))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var journal = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
            var researchRows = Assert.IsType<PyList>(journal.Items[1]);
            var row = Assert.IsType<PyTuple>(Assert.Single(researchRows.Items));

            Assert.Equal(new PyTuple([
                new PyInt(ResearchAgent), new PyInt(ResearchSkill), new PyReal(24.5), new PyReal(27.5),
                new PyInt(3), new PyInt(0), new PyInt(60000111),
            ]), row);

            projects = new ResearchProjectStore(source, schema);
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.Call(open, 2, "agentMgr", "GetMyJournalDetails"))));
            reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var reloadedJournal = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
            var reloadedRow = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(reloadedJournal.Items[1]).Items));
            Assert.Equal(row, reloadedRow);
        }
        finally
        {
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }
}
