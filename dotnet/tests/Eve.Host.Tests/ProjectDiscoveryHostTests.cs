using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Marshal;
using Eve.Services;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The Project Discovery tutorial and the season reads through the host's own registry (CH-4): the tutorial answer comes from the character's stored
/// tutorial, a character with none has not started, and no season is active.
/// </summary>
[Collection("host-composition")]
public sealed class ProjectDiscoveryHostTests : IAsyncLifetime
{
    /// <summary>The environment the host reads: every schema on its own scratch name, plus the no-simulation switch.</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
        "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA", "EVE_CONTRABAND_SCHEMA",
    ];

    private readonly string _prefix = "pd" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

    private static string Url => Environment.GetEnvironmentVariable("EVE_PG_URL")!;

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL");
        if (url is not null)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    [PgFact]
    public async Task Initialize_tutorial_status_answers_the_stored_tutorial_through_the_host_registry()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var finished = await InsertPilotAsync(source, characters, "Tutorial Finished");
            var fresh = await InsertPilotAsync(source, characters, "Tutorial Fresh");
            await new ProjectDiscoveryTutorialStore(source, Schema("EVE_CHARACTERS_SCHEMA"))
                .WriteAsync(finished, new ProjectDiscoveryTutorial(true, 8), 1_000);

            var projectDiscovery = host.Registry.Lookup("ProjectDiscovery")!;
            Assert.Same(PyBool.True, await projectDiscovery.CallAsync(new ServiceCall
            {
                Args = [],
                Method = "initialize_tutorial_status",
                Session = new ClientSession { CharacterId = finished },
            }));
            Assert.Same(PyBool.False, await projectDiscovery.CallAsync(new ServiceCall
            {
                Args = [],
                Method = "initialize_tutorial_status",
                Session = new ClientSession { CharacterId = fresh },
            }));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Season_reads_answer_no_active_season_through_the_host_registry()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var seasons = host.Registry.Lookup("seasonManager")!;
            var session = new ClientSession { CharacterId = 98000001 };
            Assert.Same(PyNone.Instance, await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_season_data_for_character", Session = session }));
            Assert.Same(PyNone.Instance, await seasons.CallAsync(new ServiceCall { Method = "select_season", Args = [new PyInt(1)], Session = session }));
            Assert.IsType<PyLong>(await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_season_end_time", Session = session }));
            Assert.Equal(new PyInt(0), await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_max_active_challenges", Session = session }));
            Assert.Equal(
                Py.KeyVal(
                    ("character_id", new PyLong(98000001)),
                    ("season_id", PyNone.Instance),
                    ("seasonal_points", new PyInt(0)),
                    ("max_seasonal_points", new PyInt(1)),
                    ("seasonal_goals", new PyDict([])),
                    ("next_seasonal_goal", PyNone.Instance),
                    ("last_seasonal_goal", PyNone.Instance),
                    ("challenges", new PyDict([]))),
                await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_character_progress_by_character_id", Session = session }));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    private static async Task<long> InsertPilotAsync(NpgsqlDataSource source, PgCharacterStore characters, string name)
    {
        var pilot = new NewCharacter(1, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: 1000006, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: null, PortraitInfo: null);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await characters.InsertAsync(connection, transaction, pilot);
        await transaction.CommitAsync();
        return id;
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);
}
