using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Services;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The host starts with no client directory: its client-data loaders read the embedded copies, and none reports a missing directory. The real
/// host composition (HostComposition.BuildAsync, the path eve-host runs) on scratch PostgreSQL schemas, with EVE_CLIENT_SDE_DIR unset.
/// </summary>
[Collection("host-composition")]
public sealed class ClientStaticDataHostTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_INSURGENCY_SCHEMA", "EVE_WORMHOLE_SCHEMA",
    ];

    private readonly string _prefix = "cs" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new (string Name, string? Value)[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), (ClientStaticData.DirectoryVariable, null) })
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

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    [PgFact]
    public async Task The_host_starts_without_the_client_directory_and_its_loaders_read_the_embedded_copies()
    {
        var original = Console.Error;
        var log = new StringWriter();
        Console.SetError(log);
        try
        {
            await HostComposition.BuildAsync(Settings());
        }
        finally
        {
            Console.SetError(original);
        }

        var output = log.ToString();
        Assert.DoesNotContain(ClientStaticData.DirectoryVariable, output);
        Assert.Contains("eve-host: missions: client texts 2886 missions", output);
    }
}
