using System.Text.Json;
using Eve.Agents;
using Eve.Data;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>
/// The epic arc offers through the dialogue on the PostgreSQL store: request offers the arc's current step, accept and completion
/// move the store, and the next request offers the next step (EpicArcOffers with AgentDialogueMachine, the real store).
/// </summary>
public class EpicArcDialogueTests
{
    private const long Pilot = 140000021;
    private const long Agent = 5001;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    // 9101 (agent 5001, a generic site, switched on for this test) leads to 9102 (agent 5001), which leads to the terminal 9104 (agent 5001).
    private const string Arc = """
        {"_key": 9001, "name": {"en": "Fixture Arc"}, "arcRestartInterval": 129600,
         "missions": [
           {"_key": 9101, "agentID": 5001, "nextMissions": [9102]},
           {"_key": 9102, "agentID": 5001, "nextMissions": [9104]},
           {"_key": 9104, "agentID": 5001, "nextMissions": []}
         ]}
        """;

    [AgentsPgFact]
    public async Task The_arc_offers_its_current_step_and_the_next_after_completion_through_the_dialogue()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "arcdlg_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var catalog = MissionStaticCatalog.FromRows(
            3405148,
            new long[] { 9101, 9102, 9104 }.Select(id => new SdeRow(id, JsonDocument.Parse($$"""{"_key": {{id}}, "name": {"en": "Step {{id}}"}, "nameID": {{id + 1000}}}""").RootElement.Clone())).ToArray(),
            [new SdeRow(9001, JsonDocument.Parse(Arc).RootElement.Clone())],
            includeUnavailableArcs: true,
            siteStatus: new Dictionary<long, string> { [9101] = MissionSiteStatus.Generic, [9102] = MissionSiteStatus.Generic },
            includeGenericArcs: true);
        var now = Start;
        var sources = new AgentDialogueSources(
            store,
            (_, _, _, _, _) => ValueTask.FromResult(new AgentStandings(0, 0, 0)),
            (_, _, _) => ValueTask.FromResult(0),
            _ => 0,
            async (agent, characterId, ct) =>
            {
                var completed = await store.ReadCompletedAsync(characterId, ct);
                return EpicArcOffers.Pick(catalog, agent.AgentId, completed, now) is { } missionId
                    ? new AgentMissionContent(missionId, catalog.Mission(missionId)!.NameId, catalog.Mission(missionId)!.NameEn, "", false)
                    : null;
            },
            () => now);
        var machine = new AgentDialogueMachine(sources);
        var agentData = new AgentMissionAgentData(
            AgentId: Agent, AgentTypeId: 2, DivisionId: 22, Level: 1, StationId: 60000001, CorporationId: 1000001,
            IsLocatorAgent: false, IsInSpace: false, CareerId: null, ImportantMission: false, MissionKind: "courier",
            MissionTypeLabel: "courier", SpecialityId: null, StationTypeId: null, BloodlineId: null, SolarSystemId: 30000001,
            DungeonId: null, SpawnPointId: null, TypeId: null);

        await machine.DoActionAsync(Pilot, agentData, new PyInt(AgentDialogueToken.Request), true, default);
        Assert.Equal(9101, (await store.ReadAsync(Pilot, Agent))!.Content.MissionId);

        await machine.DoActionAsync(Pilot, agentData, new PyInt(AgentDialogueToken.Accept), true, default);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);

        now = Start + TimeSpan.FromHours(1);
        await store.CompleteAsync(Pilot, Agent, now, "complete:9101");
        await machine.DoActionAsync(Pilot, agentData, new PyInt(AgentDialogueToken.Request), true, default);
        Assert.Equal(9102, (await store.ReadAsync(Pilot, Agent))!.Content.MissionId);
    }

    private sealed class Cleanup(NpgsqlDataSource source, string schema) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
            {
                await drop.ExecuteNonQueryAsync();
            }
            await source.DisposeAsync();
        }
    }
}
