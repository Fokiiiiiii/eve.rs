using System.Text.Json;
using Eve.Host;
using Eve.Services.Core;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The agent catalog (GetAgents rows) built from the pinned SDE in PostgreSQL by the real catalog function (AgentCatalog.Build,
/// the seam of CharacterServicesComposition.NpcAgents). The expected numbers are the SDE's own: npcCharacters with an agent
/// object (10,966), agents per agentTypeID, locators (1,773) and agentsInSpace (360).
/// </summary>
public sealed class AgentCatalogSdeTests
{
    private static readonly (long Type, int Count)[] AgentsPerType =
    [
        (2, 8775), (3, 14), (4, 244), (5, 143), (6, 651), (7, 7), (8, 696), (9, 260), (10, 52), (11, 12), (12, 100), (13, 12),
    ];

    [SdeAgentPgFact]
    public async Task The_catalog_from_the_pinned_sde_has_the_sde_counts()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var agents = await CatalogFromSdeAsync(source);

        Assert.Equal(10966, agents.Count);
        Assert.Equal(AgentsPerType, agents.GroupBy(agent => agent.AgentTypeId)
            .Select(group => (group.Key, group.Count()))
            .OrderBy(pair => pair.Key)
            .Select(pair => (pair.Key, pair.Item2)).ToArray());
        Assert.Equal(1773, agents.Count(agent => agent.IsLocatorAgent));
        Assert.Equal(360, agents.Count(agent => agent.IsInSpace));
        Assert.All(agents.Where(agent => agent.IsInSpace), agent =>
        {
            Assert.Null(agent.StationId);
            Assert.NotNull(agent.SolarSystemId);
            Assert.NotNull(agent.DungeonId);
        });
        Assert.All(agents.Where(agent => !agent.IsInSpace), agent => Assert.NotNull(agent.StationId));
    }

    [SdeAgentPgFact]
    public async Task Concrete_agents_have_the_sde_station_system_and_dungeon()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var agents = await CatalogFromSdeAsync(source);
        var byId = agents.ToDictionary(agent => agent.AgentId);

        // A basic distribution agent in Jita-area station 60000004 (system 30002780): npcCharacters 3008416.
        var basic = byId[3008416];
        Assert.Equal((2L, 22L, 1L, 60000004L, 1000002L, false, false), (basic.AgentTypeId, basic.DivisionId, basic.Level,
            basic.StationId!.Value, basic.CorporationId, basic.IsLocatorAgent, basic.IsInSpace));
        Assert.Equal((30002780L, 1531L, 14L), (basic.SolarSystemId!.Value, basic.StationTypeId!.Value, basic.CareerId!.Value));

        // The in-space agent 3019369: system 30005199, dungeon 3750, spawn point 5038, type 30887 (agentsInSpace).
        var inSpace = byId[3019369];
        Assert.True(inSpace.IsInSpace);
        Assert.Null(inSpace.StationId);
        Assert.Equal((10L, 23L, 30005199L, 3750L, 5038L, 30887L),
            (inSpace.AgentTypeId, inSpace.DivisionId, inSpace.SolarSystemId!.Value, inSpace.DungeonId!.Value, inSpace.SpawnPointId!.Value, inSpace.TypeId!.Value));
    }

    private static async Task<IReadOnlyList<AgentMissionAgentData>> CatalogFromSdeAsync(NpgsqlDataSource source)
    {
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var npcCharacters = await sde.ReadAsync("npcCharacters");
        var inSpace = await sde.ReadAsync("agentsInSpace");
        var stations = (await sde.ReadAsync("npcStations")).ToDictionary(row => row.Key);
        var systems = (await sde.ReadAsync("mapSolarSystems")).Select(row => row.Key).ToHashSet();
        return AgentCatalog.Build(npcCharacters, inSpace,
            id => stations.TryGetValue(id, out var station)
                ? new AgentStationPlace(Number(station.Doc, "typeID"), Number(station.Doc, "solarSystemID"))
                : null,
            systems.Contains);
    }

    private static long Number(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}

/// <summary>A fact that needs the pinned SDE in PostgreSQL: skipped (not passed) when EVE_PG_URL is not set.</summary>
public sealed class SdeAgentPgFactAttribute : FactAttribute
{
    public SdeAgentPgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
    }
}
