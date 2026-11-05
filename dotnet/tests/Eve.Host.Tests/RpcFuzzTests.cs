using System.Reflection;
using System.Collections.Concurrent;
using System.Diagnostics;
using Eve.Marshal;
using Eve.Macho;
using Eve.Identity;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using Xunit.Abstractions;

namespace Eve.Host.Tests;

[CollectionDefinition("rpc-fuzz", DisableParallelization = true)]
public sealed class RpcFuzzCollection;

[Collection("rpc-fuzz")]
public sealed class RpcFuzzTests
{
    private const int Seed = 0x455645;
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(10);
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
            var casesPerMethod = longMode ? 64 : 1;
        var elapsed = Stopwatch.StartNew();
        long compositionElapsed = 0;
        long snapshotBeforeElapsed = 0;
        long dispatchElapsed = 0;
        var saved = new Dictionary<string, string?>();
        var schemas = new List<string>();
        HostRuntime? runtime = null;
        NpgsqlDataSource? snapshotSource = null;
        try
        {
            var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
            Assert.False(string.IsNullOrWhiteSpace(pgUrl), "RPC fuzzing needs EVE_PG_URL for an isolated Host composition run.");
            saved["EVE_PG_URL"] = pgUrl;
            var taggedConnectionString = new NpgsqlConnectionStringBuilder(Eve.Data.PgConnection.ToConnectionString(pgUrl!))
            {
                ApplicationName = $"RpcFuzz/{Environment.ProcessId}",
            }.ConnectionString;
            Environment.SetEnvironmentVariable("EVE_PG_URL", taggedConnectionString);
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
            compositionElapsed = elapsed.ElapsedMilliseconds;
            var registry = runtime.Registry;
            var services = registry.RegisteredServices;

            var dispatcher = new PacketDispatcher(registry, () => 1_800_000_000_000);
            var random = new Random(Seed);
            var total = 0;
            var methodCount = 0;
            var overviewPresetWriteAccepted = false;
            snapshotSource = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var databaseBefore = await SnapshotAsync(snapshotSource, schemas);
            snapshotBeforeElapsed = elapsed.ElapsedMilliseconds - compositionElapsed;
            if (services.FirstOrDefault(service => service.Name == "fwWarzoneSolarsystem") is { } fwWarzone &&
                fwWarzone.HasMethod("GetAllWarzonesOccupationStates"))
            {
                // Warm its one-time SDE adjacency cache before measuring hostile RPC allocations.
                var warmCapture = new PacketCapture();
                var warmSession = new ClientSession(warmCapture) { SessionId = 9_876_543, ClientId = 9_876_544 };
                Assert.True(await dispatcher.DispatchAsync(
                    CallPacket("fwWarzoneSolarsystem", "GetAllWarzonesOccupationStates", []), warmSession)
                    .WaitAsync(DispatchTimeout));
                var warmResponses = warmCapture.Packets.Select(MachoPacket.Decode).ToArray()
                    .Where(packet => packet!.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse);
                Assert.Equal(MachoMessage.CallRsp, Assert.Single(warmResponses)!.Type);
            }
            var dispatchStarted = elapsed.ElapsedMilliseconds;
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
                        var argumentIndex = longMode ? index : methodCount % 7;
                        IReadOnlyList<PyValue> arguments = Arguments(argumentIndex, random, longMode);
                        if (service.Name == "structureAssetSafety" && method == "GetItemsInSafetyForCharacter" && index == 0)
                            arguments = [];
                        if (service.Name == "facilityManager" && method == "GetFacilitiesByID" &&
                            (longMode ? index == 3 : index == 0))
                            arguments = [new PyList(Enumerable.Repeat<PyValue>(new PyLong(long.MaxValue), 16_384).ToArray())];
                        bool handled;
                        try
                        {
                            handled = await dispatcher.DispatchAsync(
                                CallPacket(service.Name, method, arguments), session)
                                .WaitAsync(DispatchTimeout);
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
            dispatchElapsed = elapsed.ElapsedMilliseconds - dispatchStarted;

            Assert.True(total > 0);
            _output.WriteLine($"seed={Seed}; services={services.Count}; methods={methodCount}; dispatches={total}; long={longMode}; compositionMs={compositionElapsed}; snapshotBeforeMs={snapshotBeforeElapsed}; dispatchMs={dispatchElapsed}");
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
            try
            {
                if (snapshotSource is not null)
                    await snapshotSource.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (runtime is not null)
                        await runtime.ShutdownAsync();
                }
                finally
                {
                    // These overrides are process-wide; restore them before potentially slow schema teardown.
                    foreach (var (name, value) in saved)
                        Environment.SetEnvironmentVariable(name, value);
                }
            }
            if (schemas.Count > 0 && Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
            {
                var cleanupConnectionString = new NpgsqlConnectionStringBuilder(Eve.Data.PgConnection.ToConnectionString(url))
                {
                    ApplicationName = $"RpcFuzzCleanup/{Environment.ProcessId}",
                }.ConnectionString;
                await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(cleanupConnectionString));
                var activeSessions = await ScratchSchemaActivityAsync(source, schemas);
                Assert.True(activeSessions.Length == 0,
                    $"RPC fuzz teardown found sessions using its scratch schemas: {string.Join("; ", activeSessions)}");
                foreach (var schema in schemas)
                    await DropScratchSchemaAsync(source, schema);
            }
        }
    }

    [PgFact]
    public async Task Scratch_schema_drop_reports_the_blocking_session_and_recovers_after_it_exits()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(url), "Scratch-schema cleanup diagnostics need EVE_PG_URL.");
        var schema = $"rfguard_{Guid.NewGuid():N}";
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url!));
        await using (var createSchema = source.CreateCommand())
        {
            createSchema.CommandText = $"CREATE SCHEMA {schema}";
            await createSchema.ExecuteNonQueryAsync();
        }
        await using (var createTable = source.CreateCommand())
        {
            createTable.CommandText = $"CREATE TABLE {schema}.held (id integer)";
            await createTable.ExecuteNonQueryAsync();
        }

        await using var blocker = await source.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        var transactionOpen = true;
        var schemaDropped = false;
        try
        {
            await using (var holdLock = blocker.CreateCommand())
            {
                holdLock.CommandText = $"SELECT count(*) FROM {schema}.held";
                holdLock.Transaction = transaction;
                await holdLock.ExecuteScalarAsync();
            }

            var error = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => DropScratchSchemaAsync(source, schema));
            Assert.Contains($"pid={blocker.ProcessID}", error.Message);
            Assert.Contains(schema, error.Message);

            await transaction.RollbackAsync();
            transactionOpen = false;
            Assert.Empty(await ScratchSchemaActivityAsync(source, [schema]));
            await DropScratchSchemaAsync(source, schema);
            schemaDropped = true;
        }
        finally
        {
            if (transactionOpen)
                await transaction.RollbackAsync();
            if (!schemaDropped)
            {
                await using var drop = source.CreateCommand();
                drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE";
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<string[]> ScratchSchemaActivityAsync(NpgsqlDataSource source, IReadOnlyList<string> schemas)
    {
        if (schemas.Count == 0)
            return [];

        await using var command = source.CreateCommand();
        command.CommandText = """
            SELECT format('pid=%s app=%s state=%s xact_age=%s wait=%s/%s query=%s',
                activity.pid,
                coalesce(activity.application_name, ''),
                coalesce(activity.state, ''),
                coalesce(age(now(), activity.xact_start)::text, '-'),
                coalesce(activity.wait_event_type, ''),
                coalesce(activity.wait_event, ''),
                left(coalesce(activity.query, ''), 240))
            FROM pg_stat_activity AS activity
            WHERE activity.datname = current_database()
              AND activity.pid <> pg_backend_pid()
              AND (
                (activity.state IS DISTINCT FROM 'idle' AND EXISTS (
                    SELECT 1 FROM unnest($1::text[]) AS target(schema_name)
                    WHERE position(target.schema_name IN coalesce(activity.query, '')) > 0
                ))
                OR EXISTS (
                    SELECT 1
                    FROM pg_locks AS held
                    JOIN pg_class AS relation ON relation.oid = held.relation
                    JOIN pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                    WHERE held.pid = activity.pid
                      AND held.granted
                      AND namespace.nspname = ANY($1::text[])
                )
              )
            ORDER BY activity.pid
            """;
        command.Parameters.AddWithValue(schemas.ToArray());
        var activity = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            activity.Add(reader.GetString(0));
        return activity.ToArray();
    }

    private static async Task DropScratchSchemaAsync(NpgsqlDataSource source, string schema)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "SET LOCAL lock_timeout = '5s'";
            timeout.Transaction = transaction;
            await timeout.ExecuteNonQueryAsync();
        }

        try
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE";
            drop.Transaction = transaction;
            await drop.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch (PostgresException error) when (error.SqlState == "55P03")
        {
            await transaction.RollbackAsync();
            var blockers = await ScratchSchemaActivityAsync(source, [schema]);
            var detail = blockers.Length == 0
                ? "No other session with a scratch-schema query or relation lock remained in pg_stat_activity."
                : string.Join("; ", blockers);
            throw new Xunit.Sdk.XunitException(
                $"DROP SCHEMA {schema} remained lock-blocked for 5 seconds (SQLSTATE {error.SqlState}): {detail}");
        }
    }

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_CONTRACTS_SCHEMA", "EVE_LOGIN_SCHEMA", "EVE_RAFFLE_SCHEMA", "EVE_SKINR_SCHEMA",
        "EVE_PROJECT_DISCOVERY_SCHEMA",
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
