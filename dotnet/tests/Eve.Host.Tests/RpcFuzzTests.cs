using System.Reflection;
using System.Collections.Concurrent;
using Eve.Marshal;
using Eve.Macho;
using Eve.Identity;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using Xunit.Abstractions;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class RpcFuzzTests
{
    private const int Seed = 0x455645;
    private static readonly ConcurrentDictionary<System.Reflection.Assembly, string[]> MethodNamesByAssembly = new();
    private readonly ITestOutputHelper _output;

    public RpcFuzzTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Registered_service_instances_collapse_aliases()
    {
        var registry = new ServiceRegistry();
        var service = new PingService();
        registry.Register(service);
        registry.RegisterAlias("pingAlias", ((IService)service).Name);

        Assert.Same(service, Assert.Single(registry.RegisteredServices));
    }

    [PgFact]
    public async Task Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher()
    {
        var longMode = string.Equals(Environment.GetEnvironmentVariable("EVE_RPC_FUZZ_LONG"), "1", StringComparison.Ordinal);
        var casesPerMethod = longMode ? 64 : 7;
        var saved = new Dictionary<string, string?>();
        var schemas = new List<string>();
        HostRuntime? runtime = null;
        NpgsqlDataSource? snapshotSource = null;
        try
        {
            var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
            Assert.False(string.IsNullOrWhiteSpace(pgUrl), "RPC fuzzing needs EVE_PG_URL for an isolated Host composition run.");
            foreach (var variable in SchemaVariables)
            {
                saved[variable] = Environment.GetEnvironmentVariable(variable);
                var schema = "rf" + Guid.NewGuid().ToString("N")[..10] + variable[4..^7].ToLowerInvariant();
                schemas.Add(schema);
                Environment.SetEnvironmentVariable(variable, schema);
            }
            foreach (var (name, value) in new[]
            {
                ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off"), ("EVE_XMPP_ENABLED", "0"),
            })
            {
                saved[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
            runtime = await HostComposition.BuildAsync(new HostSettings("", "disabled",
                new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
                new AccountOptions { AutoCreate = true }, StrictRpc: true));
            var registry = runtime.Registry;
            var services = registry.RegisteredServices;

            var dispatcher = new PacketDispatcher(registry, () => 1_800_000_000_000);
            var random = new Random(Seed);
            var total = 0;
            var methodCount = 0;
            var overviewPresetWriteAccepted = false;
            snapshotSource = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var databaseBefore = await SnapshotAsync(snapshotSource, schemas);
            foreach (var service in services)
            {
                var methods = RegisteredMethods(service);
                Assert.True(methods.Count > 0, $"No registered method names found for {service.Name} ({service.GetType().FullName}).");
                foreach (var method in methods)
                {
                    methodCount++;
                    for (var index = 0; index < casesPerMethod; index++)
                    {
                        var capture = new PacketCapture();
                        var session = new ClientSession(capture) { SessionId = 9_876_543, ClientId = 9_876_544 };
                        if (service.Name == "structureAssetSafety" && method == "GetItemsInSafetyForCharacter" && index == 0)
                            session.Set("charid", new PyLong(9_876_546));
                        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
                        IReadOnlyList<PyValue> arguments = Arguments(index, random, longMode);
                        if (service.Name == "facilityManager" && method == "GetFacilitiesByID" && index == 3)
                            arguments = [new PyList(Enumerable.Repeat<PyValue>(new PyLong(long.MaxValue), 16_384).ToArray())];
                        bool handled;
                        try
                        {
                            handled = await dispatcher.DispatchAsync(
                                CallPacket(service.Name, method, arguments), session)
                                .WaitAsync(TimeSpan.FromSeconds(2));
                        }
                        catch (TimeoutException error)
                        {
                            throw new Xunit.Sdk.XunitException($"{service.Name}.{method} input {index} timed out: {error.Message}");
                        }

                        Assert.True(handled, $"{service.Name}.{method} was not handled");
                        var packets = capture.Packets.Select(MachoPacket.Decode).ToArray();
                        Assert.All(packets, packet => Assert.NotNull(packet));
                        var responses = packets.Where(packet => packet!.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse).ToArray();
                        Assert.Single(responses);
                        Assert.All(packets.Where(packet => packet!.Type is not (MachoMessage.CallRsp or MachoMessage.ErrorResponse)),
                            packet => Assert.True(packet!.Type is MachoMessage.Notification or MachoMessage.SessionChangeNotification,
                                $"{service.Name}.{method} input {index} emitted unexpected packet type {packet.TypeName} ({packet.Type})."));
                        if (service.Name == "overviewPresetMgr" && method == "StoreLinkAndGetID" &&
                            responses[0]!.Type == MachoMessage.CallRsp)
                            overviewPresetWriteAccepted = true;
                        var allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
                        Assert.True(allocated <= 64L * 1024 * 1024,
                            $"{service.Name}.{method} input {index} allocated {allocated} bytes during dispatch.");
                        total++;
                    }
                }
            }

            Assert.True(total > 0);
            _output.WriteLine($"seed={Seed}; services={services.Count}; methods={methodCount}; dispatches={total}; long={longMode}");
            var databaseAfter = await SnapshotAsync(snapshotSource, schemas);
            var changedTables = ChangedTables(databaseBefore, databaseAfter);
            var unexpectedChanges = changedTables.Where(table =>
            {
                var tableName = table[(table.LastIndexOf('.') + 1)..];
                if (tableName == "overviewSharedPresets") return !overviewPresetWriteAccepted;
                return tableName is not ("_persistence_owners" or "industryRuntime" or "industry_cost_index_state" or
                    "industry_cost_indices" or "jobs");
            }).ToArray();
            Assert.True(unexpectedChanges.Length == 0,
                $"Hostile RPC dispatch changed persistent game data on refusal: {string.Join(", ", unexpectedChanges)}");
            _output.WriteLine($"scratch database tables changed by accepted writes or Host lifecycle: {string.Join(", ", changedTables)}");
        }
        finally
        {
            if (snapshotSource is not null)
                await snapshotSource.DisposeAsync();
            if (runtime is not null)
                await runtime.ShutdownAsync();
            if (schemas.Count > 0 && Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
            {
                await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
                foreach (var schema in schemas)
                {
                    await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                    await command.ExecuteNonQueryAsync();
                }
            }
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_CONTRACTS_SCHEMA", "EVE_LOGIN_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA",
        "EVE_ACCESS_GROUPS_SCHEMA", "EVE_AGENTS_SCHEMA", "EVE_FACTIONWARFARE_SCHEMA", "EVE_INSURGENCY_SCHEMA", "EVE_PLANETS_SCHEMA",
        "EVE_CONTRABAND_SCHEMA", "EVE_WORMHOLE_SCHEMA",
        "EVE_XMPP_SCHEMA",
    ];

    private static async Task<Dictionary<string, string>> SnapshotAsync(NpgsqlDataSource source, IReadOnlyList<string> schemas)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await source.OpenConnectionAsync();
        foreach (var schema in schemas)
        {
            await using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = $1 AND table_type = 'BASE TABLE' ORDER BY table_name";
            tables.Parameters.AddWithValue(schema);
            await using var reader = await tables.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            await reader.DisposeAsync();
            foreach (var table in names)
            {
                var qualified = $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                await using var digest = connection.CreateCommand();
                digest.CommandText = $"SELECT md5(coalesce(string_agg(to_jsonb(row_data)::text, E'\\n' ORDER BY to_jsonb(row_data)::text), '')) FROM {qualified} AS row_data";
                snapshot[$"{schema}.{table}"] = (string?)await digest.ExecuteScalarAsync() ?? "";
            }
        }
        return snapshot;
    }

    private static string[] ChangedTables(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(table => !before.TryGetValue(table, out var oldValue) ||
                !after.TryGetValue(table, out var newValue) || !StringComparer.Ordinal.Equals(oldValue, newValue))
            .ToArray();

    private static IReadOnlyList<string> RegisteredMethods(IService service)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<IService>();
        var visited = new HashSet<IService>(ReferenceEqualityComparer.Instance);
        pending.Push(service);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            for (var type = current.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                candidates.UnionWith(MethodNamesByAssembly.GetOrAdd(type.Assembly, AssemblyMethodNames));

                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var value = field.GetValue(field.IsStatic ? null : current);
                    if (value is IService nested)
                        pending.Push(nested);
                }
            }
        }
        return candidates.Where(service.HasMethod).Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] AssemblyMethodNames(System.Reflection.Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types.OfType<Type>().ToArray();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<Eve.Services.MethodAttribute>())
                    names.Add(attribute.Name);
                byte[]? body;
                try
                {
                    body = method.GetMethodBody()?.GetILAsByteArray();
                }
                catch (InvalidOperationException)
                {
                    body = null;
                }
                if (body is null)
                    continue;
                for (var offset = 0; offset + 4 < body.Length; offset++)
                {
                    if (body[offset] != 0x72)
                        continue;
                    try
                    {
                        names.Add(method.Module.ResolveString(BitConverter.ToInt32(body, offset + 1)));
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }
        return names.ToArray();
    }

    private static IReadOnlyList<PyValue> Arguments(int index, Random random, bool longMode) => (index % 7) switch
    {
        0 => [],
        1 => [PyNone.Instance],
        2 => [new PyStr(new string('x', longMode ? 1_048_576 : 65_536))],
        3 => [new PyList(Enumerable.Range(0, longMode ? 16_384 : 1_024)
            .Select(i => (PyValue)new PyLong(i % 2 == 0 ? -i - 1L : long.MaxValue)).ToArray())],
        4 => [new PyList(Enumerable.Range(0, longMode ? 1_024 : 64).Select(i => (PyValue)new PyTuple([
            new PyLong(i % 2 == 0 ? -i - 1L : long.MaxValue), new PyStr("🛰️e\0")
        ])).ToArray())],
        5 => [new PyStr("🛰️�\0é"), new PyDict([
            new(new PyStr("ids"), new PyTuple([new PyLong(long.MinValue), new PyInt(0), new PyInt(-1)])),
            new(new PyStr("value"), new PyReal(random.NextDouble())),
        ]), PyBool.True, new PyTuple([]), new PyInt(0), new PyLong(-1)],
        _ => [new PyDict([
            new(new PyStr("nested"), new PyDict([
                new(new PyStr("ids"), new PyTuple([new PyLong(long.MinValue), new PyInt(0), new PyInt(-1)])),
                new(new PyStr("value"), new PyReal(random.NextDouble())),
            ])),
        ])],
    };

    private static PyValue CallPacket(string service, string method, IReadOnlyList<PyValue> args)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(7), new PyInt(9), PyNone.Instance]);
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple(args), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[]
        {
            new PyInt(MachoMessage.CallReq), source, MachoAddress.Any(service).Encode(), new PyInt(9_876_544), payload, PyNone.Instance,
        };
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

}
