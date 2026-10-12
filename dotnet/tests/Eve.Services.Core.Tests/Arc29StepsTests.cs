using System.Text.Json;
using Eve.Agents;
using Eve.Data;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>
/// Arc 29 steps 1 and 2 through the real store, the dialogue and the runtime (fixture rows shaped like the official records): the kill step 14117
/// at Alitura (station, Arnon) starts its dungeon when the pilot is in space there and pays once; the talk step 14118 is accepted from Alitura and
/// completed by talking to Tevis Jak (in space, Tar); 14119 is then offered by Tevis. Generic sites are offered by the switch.
/// </summary>
public class Arc29StepsTests
{
    private const long Pilot = 140000041;
    private const long Alitura = 3019356;
    private const long Tevis = 3019369;
    private const long ArnonSystem = 30005001;
    private const long TarSystem = 30005199;
    private const long Corporation = 1000129;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static SdeRow Row(long id, string json) => new(id, JsonDocument.Parse(json).RootElement.Clone());

    private const string KillStep = """
        {"_key": 14117, "name": {"en": "A Beacon Beckons"}, "nameID": 57289,
         "killMission": {"dungeonID": 3509, "objectiveQuantity": 0},
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 100000}}}
        """;

    private const string TalkStep = """
        {"_key": 14118, "name": {"en": "Agent Inquiry"}, "nameID": 57269,
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 100000}}}
        """;

    private const string LaterKill = """
        {"_key": 14119, "name": {"en": "Of Interest"}, "nameID": 57287,
         "killMission": {"dungeonID": 3660, "objectiveQuantity": 1, "objectiveTypeID": 30755}}
        """;

    private const string Arc = """
        {"_key": 29, "name": {"en": "The Blood-Stained Stars"}, "arcRestartInterval": 129600,
         "missions": [
           {"_key": 14117, "agentID": 3019356, "nextMissions": [14118]},
           {"_key": 14118, "agentID": 3019356, "nextMissions": [14119]},
           {"_key": 14119, "agentID": 3019369, "nextMissions": []}]}
        """;

    private static MissionStaticCatalog Catalog() => MissionStaticCatalog.FromRows(
        3405148,
        [Row(14117, KillStep), Row(14118, TalkStep), Row(14119, LaterKill)],
        [Row(29, Arc)],
        includeUnavailableArcs: true,
        siteStatus: new Dictionary<long, string> { [14117] = MissionSiteStatus.Generic, [14119] = MissionSiteStatus.Generic },
        includeGenericArcs: true);

    private sealed class Host : IMissionDungeonHost
    {
        public bool InTar { get; set; }
        public List<(long Character, int System, long Dungeon)> Starts { get; } = [];
        public List<(long Character, long Amount, string Key)> Credits { get; } = [];
        public bool IsPilotInSystem(long characterId, int solarSystemId) => solarSystemId == ArnonSystem || (solarSystemId == TarSystem && InTar);
        public Task<string?> StartDungeonAsync(long characterId, int solarSystemId, long dungeonId, CancellationToken cancellationToken)
        {
            Starts.Add((characterId, solarSystemId, dungeonId));
            return Task.FromResult<string?>(null);
        }
        public Task CreditIskAsync(long characterId, long amount, long counterpartyId, int refTypeId, string reason, string operationKey, CancellationToken cancellationToken)
        {
            Credits.Add((characterId, amount, operationKey));
            return Task.CompletedTask;
        }
        public AgentMissionStore? Store { get; set; }

        public async Task<bool> CompleteMissionAsync(long characterId, long agentId, DateTimeOffset now, string reference,
            IReadOnlyList<MissionStandingGain> standings, IReadOnlyList<(int TypeId, long Quantity)> consume, CancellationToken cancellationToken) =>
            // the items of a fetch step are not modelled here: the arc's first steps consume nothing
            consume.Count == 0 && Store is not null && await Store.CompleteAsync(characterId, agentId, now, reference, cancellationToken) is not null;

        public Task GrantObjectiveItemAsync(long characterId, int typeId, long quantity, long stationId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<long> HeldQuantityAsync(long characterId, int typeId, CancellationToken cancellationToken) => Task.FromResult(0L);

        public Task GrantCargoAsync(long characterId, int typeId, long quantity, bool singleton, long stationId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static AgentMissionAgentData Agent(long id, bool inSpace, long? system, long? dungeon) => new(
        AgentId: id, AgentTypeId: 10, DivisionId: 23, Level: 1, StationId: inSpace ? null : 60012607, CorporationId: Corporation,
        IsLocatorAgent: false, IsInSpace: inSpace, CareerId: null, ImportantMission: false, MissionKind: "epicArc",
        MissionTypeLabel: "", SpecialityId: null, StationTypeId: 1531, BloodlineId: null, SolarSystemId: system,
        DungeonId: dungeon, SpawnPointId: inSpace ? 5038 : null, TypeId: inSpace ? 30887 : null);

    [AgentsPgFact]
    public async Task Arc_29_steps_one_and_two_run_end_to_end()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "arc29_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var catalog = Catalog();
        var host = new Host { Store = store };
        var agents = new Dictionary<long, AgentMissionAgentData>
        {
            [Alitura] = Agent(Alitura, false, ArnonSystem, null),
            [Tevis] = Agent(Tevis, true, TarSystem, 3750),
        };
        var now = Start;
        var runtime = new MissionDungeonRuntime(store, catalog, host,
            id => agents.TryGetValue(id, out var agent) && agent.SolarSystemId is { } system ? new MissionAgentPlace(system, Corporation) : null,
            () => Start);
        var machine = new AgentDialogueMachine(new AgentDialogueSources(
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
            () => now,
            (characterId, agent, atLocation, ct) => new ValueTask(runtime.OnConversationAsync(characterId, agent.AgentId,
                agent.IsInSpace ? host.IsPilotInSystem(characterId, (int)agent.SolarSystemId!.Value) : atLocation, ct))));

        // step 1: the kill mission at Alitura, the dungeon in Arnon, the kills (the outcome), the reward once
        now = now.AddMinutes(1);
        await machine.DoActionAsync(Pilot, agents[Alitura], new PyInt(AgentDialogueToken.Request), true, default);
        Assert.Equal(14117, (await store.ReadAsync(Pilot, Alitura))!.Content.MissionId);
        now = now.AddMinutes(1);
        await machine.DoActionAsync(Pilot, agents[Alitura], new PyInt(AgentDialogueToken.Accept), true, default);
        Assert.Equal(1, await runtime.SweepAsync());
        Assert.Equal((Pilot, (int)ArnonSystem, 3509L), Assert.Single(host.Starts));
        now = now.AddMinutes(1);
        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 1, default);
        Assert.Equal(100000, Assert.Single(host.Credits).Amount);
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Alitura))!.State);

        // step 2: the talk step 14118 from Alitura; the talk with Tevis Jak in space completes it, and pays once
        now = now.AddMinutes(1);
        await machine.DoActionAsync(Pilot, agents[Alitura], new PyInt(AgentDialogueToken.Request), true, default);
        Assert.Equal(14118, (await store.ReadAsync(Pilot, Alitura))!.Content.MissionId);
        now = now.AddMinutes(1);
        await machine.DoActionAsync(Pilot, agents[Alitura], new PyInt(AgentDialogueToken.Accept), true, default);
        now = now.AddMinutes(1);
        await machine.DoActionAsync(Pilot, agents[Tevis], new PyInt(AgentDialogueToken.Request), false, default);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Alitura))!.State);
        host.InTar = true;
        now = now.AddMinutes(1);
        await machine.DoActionAsync(Pilot, agents[Tevis], new PyInt(AgentDialogueToken.Request), false, default);
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Alitura))!.State);
        Assert.Equal(2, host.Credits.Count);
        now = now.AddMinutes(1);
        await machine.DoActionAsync(Pilot, agents[Tevis], new PyInt(AgentDialogueToken.Request), false, default);
        Assert.Equal(2, host.Credits.Count);

        // then 14119 is offered by Tevis
        now = now.AddMinutes(1);
        await machine.DoActionAsync(Pilot, agents[Tevis], new PyInt(AgentDialogueToken.Request), false, default);
        Assert.Equal(14119, (await store.ReadAsync(Pilot, Tevis))!.Content.MissionId);
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
