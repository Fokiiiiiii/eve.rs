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
                    var methods = service.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
                    var generated = methods
                        .Where(method => method.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(ServiceCall))
                        .Where(method => method.ReturnType == typeof(Eve.Marshal.PyValue) ||
                            method.ReturnType == typeof(Task<Eve.Marshal.PyValue>) ||
                            method.ReturnType == typeof(ValueTask<Eve.Marshal.PyValue>))
                        .Select(method => method.GetCustomAttribute<MethodAttribute>()?.Name ?? method.Name)
                        .Where(service.HasMethod);
                    var candidates = generated.Concat(ManualMethodNames(service))
                        .Where(service.HasMethod)
                        .Distinct(StringComparer.Ordinal)
                        .Select(method => $"{serviceName}.{method}");
                    return candidates;
                })
                .Order(StringComparer.Ordinal)
                .ToArray();
            var missingServices = host.Registry.ServiceNames.Distinct(StringComparer.Ordinal)
                .Where(serviceName => rows.All(row => !row.StartsWith($"{serviceName}.", StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.True(missingServices.Length == 0, $"RPC methods were not enumerated for: {string.Join(", ", missingServices)}");
            Assert.Equal(1399, rows.Length);
            Console.WriteLine($"RPC_INVENTORY_COUNT={rows.Length}");
            foreach (var row in rows) Console.WriteLine($"RPC_INVENTORY={row}");
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    private static IEnumerable<string> ManualMethodNames(IService service)
    {
        var type = service.GetType();
        if (type.GetCustomAttribute<ServiceAttribute>() is not null)
            return [];

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            for (var i = 0; i + sizeof(int) < il.Length; i++)
            {
                if (il[i] != 0x72) continue; // ldstr
                try
                {
                    var token = BitConverter.ToInt32(il, i + 1);
                    var candidate = method.Module.ResolveString(token);
                    if (service.HasMethod(candidate)) names.Add(candidate);
                }
                catch (ArgumentException)
                {
                    // Other instruction bytes can share the ldstr opcode value.
                }
            }
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        AddCatalogFields(service, names, visited, depth: 0);
        return names;
    }

    private static void AddCatalogFields(object target, HashSet<string> names, HashSet<object> visited, int depth)
    {
        if (depth > 2 || !visited.Add(target)) return;
        foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? value;
            try { value = field.GetValue(field.IsStatic ? null : target); }
            catch (TargetException) { continue; }
            if (value is IEnumerable<string> candidates)
            {
                foreach (var candidate in candidates) names.Add(candidate);
            }
            else if (value is IService nested && !ReferenceEquals(nested, target))
            {
                foreach (var candidate in nested.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(method => method.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(ServiceCall))
                    .Select(method => method.GetCustomAttribute<MethodAttribute>()?.Name ?? method.Name)
                    .Concat(ManualMethodNames(nested)))
                    if (nested.HasMethod(candidate)) names.Add(candidate);
            }
            else if (value is System.Collections.IDictionary dictionary)
            {
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                    if (entry.Value is IEnumerable<string> entries)
                        foreach (var candidate in entries) names.Add(candidate);
            }
        }
    }
}
