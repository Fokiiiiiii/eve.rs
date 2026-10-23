using System.Reflection;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class RpcSecurityInventoryTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA",
    ];

    private readonly string _prefix = "ri" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = $"{_prefix}_{variable[4..^7].ToLowerInvariant()}";
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

    [Fact]
    public async Task Real_host_registry_methods_are_enumerable()
    {
        var host = await HostComposition.BuildAsync(new HostSettings(
            "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
            new AccountOptions { AutoCreate = true }, StrictRpc: true));
        try
        {
            var rows = host.Registry.ServiceNames
                .Distinct(StringComparer.Ordinal)
                .SelectMany(serviceName =>
                {
                    var service = host.Registry.Lookup(serviceName)!;
                    var candidates = service.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .Where(method => method.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(ServiceCall))
                        .Where(method => method.ReturnType == typeof(Eve.Marshal.PyValue) ||
                            method.ReturnType == typeof(Task<Eve.Marshal.PyValue>) ||
                            method.ReturnType == typeof(ValueTask<Eve.Marshal.PyValue>))
                        .Select(method => method.GetCustomAttribute<MethodAttribute>()?.Name ?? method.Name)
                        .Where(service.HasMethod)
                        .Distinct(StringComparer.Ordinal)
                        .Select(method => $"{serviceName}.{method}");
                    return candidates;
                })
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(955, rows.Length);
            Console.WriteLine($"RPC_INVENTORY_COUNT={rows.Length}");
            foreach (var row in rows) Console.WriteLine($"RPC_INVENTORY={row}");
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }
}
