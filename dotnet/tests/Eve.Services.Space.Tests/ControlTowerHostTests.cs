using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Inventory;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// It is anchored when its anchoring time is over (the SDE's: 30 minutes for the Amarr Control Tower), never ends by time, and only its launching corporation
/// </summary>
public class ControlTowerHostTests
{
    /// <summary>The Amarr Control Tower (SDE group 365, radius 3394 m, anchoring delay 1 800 000 ms).</summary>
    private const int ControlTower = 12235, Observatory = 58904, Anchoring = 11584, AnchoringLevel = 3;
    /// <summary>Asabona (Derelik, security 0.32): below the cutoff, so an observatory may be dropped there.</summary>
    private const long LowSecSystem = 30000012;
    private const long TestCorporation = 98000001;
    private const long AnchoringMs = 1_800_000;
    private const long OnliningMs = 1_800_000;
    private const long HourMs = 3_600_000;

    /// <summary>A real Host socket wired to the registered posMgr service and this test's PostgreSQL-backed SpaceContainers provider.</summary>
    private sealed class PosMgrHostWire : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Socket _listener;
        private readonly Socket _client;
        private readonly NetworkStream _stream;
        private readonly Task _serving;
        private long _callId;

        private PosMgrHostWire(Socket listener, Socket client, NetworkStream stream, Task serving,
            CancellationTokenSource cancellation, ServiceRegistry registry, SessionOpen open)
        {
            (_listener, _client, _stream, _serving, _cancellation, Registry, Open) =
                (listener, client, stream, serving, cancellation, registry, open);
        }

        public ServiceRegistry Registry { get; }
        public SessionOpen Open { get; }
        public ClientSession Session { get; private set; } = null!;
        public List<MachoPacket> Notifications { get; } = [];

        public static async Task<PosMgrHostWire> StartAsync(PosMgrService service, ClientSession sourceSession,
            Func<long> nowUnixMs, params IService[] additionalServices)
        {
            var registry = new ServiceRegistry();
            registry.Register(service);
            foreach (var extra in additionalServices) registry.Register(extra);
            ClientSession? hostSession = null;
            var dispatcher = new PacketDispatcher(registry, nowUnixMs);
            var server = new HostServer(new HostOptions
            {
                SessionOpened = opened =>
                {
                    hostSession = opened;
                    opened.CharacterId = sourceSession.CharacterId;
                    foreach (var key in new[] { "corpid", "solarsystemid2", "shipid", "isCEO", "corprole", "role", "allianceid" })
                        if (sourceSession.Get(key) is { } value) opened.Set(key, value);
                },
            }, dispatcher, new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
                () => MarshalCodec.Encode(new PyDict([])));
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(4);
            var cancellation = new CancellationTokenSource();
            var serving = server.ServeAsync(listener, cancellation.Token);
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
            var stream = new NetworkStream(client);
            var userId = checked((uint)sourceSession.UserId!.Value);
            var open = new SessionOpen(checked((ulong)sourceSession.SessionId), checked((ulong)sourceSession.ClientId), userId,
                0, "127.0.0.1", "EN", "GB");
            var wire = new PosMgrHostWire(listener, client, stream, serving, cancellation, registry, open);
            await wire.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
            _ = Assert.IsType<HelloAck>(await wire.ReceiveAsync());
            _ = Assert.IsType<ClusterConfig>(await wire.ReceiveAsync());
            await wire.SendAsync(open);
            _ = await wire.ReceiveAsync();
            wire.Session = hostSession ?? throw new InvalidOperationException("Host session was not opened.");
            return wire;
        }

        public async Task<PyValue> CallAsync(string method, params PyValue[] args)
            => await CallServiceAsync("posMgr", method, args);

        public async Task<PyValue> CallServiceAsync(string service, string method, params PyValue[] args)
        {
            var body = MarshalCodec.Encode(Py.Tuple(new PyInt(1), new PyStr(method), new PyTuple(args), PyNone.Instance));
            var source = Py.Tuple(new PyInt(2), new PyLong((long)Open.ClientId), new PyInt(checked((int)++_callId)), PyNone.Instance);
            var destination = Py.Tuple(new PyInt(8), new PyStr(service), PyNone.Instance);
            var payload = Py.Tuple(Py.Tuple(new PyInt(0), new PySubStream(body)));
            PyValue[] head =
            [
                new PyInt(MachoMessage.CallReq), source, destination, new PyInt(Open.UserId), payload, PyNone.Instance,
            ];
            var packet = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
                new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
            await SendAsync(new Packet(Open.SessionId, MarshalCodec.Encode(packet)));
            while (true)
            {
                var message = await ReceiveAsync();
                if (message is not Send response) continue;
                var macho = MachoPacket.Decode(MarshalCodec.Decode(response.Payload))
                    ?? throw new InvalidOperationException("Host returned a non-Macho packet for posMgr.");
                if (macho.Type == MachoMessage.Notification)
                {
                    Notifications.Add(macho);
                    continue;
                }
                if (macho.Type == MachoMessage.ErrorResponse)
                {
                    var error = Assert.IsType<PyTuple>(macho.Payload);
                    var errorCode = checked((int)Assert.IsType<PyInt>(error.Items[1]).Value);
                    var wrapped = Assert.IsType<PyTuple>(error.Items[2]).Items[0];
                    var errorPayload = wrapped is PySubStream substream ? substream.Decoded ?? PyNone.Instance : wrapped;
                    throw new MachoWrappedException(new MachoError(errorCode, errorPayload));
                }
                Assert.Equal(MachoMessage.CallRsp, macho.Type);
                var result = Assert.IsType<PyTuple>(macho.Payload).Items[0];
                return result is PySubStream substreamResult ? substreamResult.Decoded ?? PyNone.Instance : result;
            }
        }

        public async Task<PyValue> BoundCallAsync(string oid, string method, params PyValue[] args)
        {
            var body = MarshalCodec.Encode(Py.Tuple(new PyStr(oid), new PyStr(method), new PyTuple(args), PyNone.Instance));
            var source = Py.Tuple(new PyInt(2), new PyLong((long)Open.ClientId), new PyInt(checked((int)++_callId)), PyNone.Instance);
            var destination = Py.Tuple(new PyInt(8), PyNone.Instance, PyNone.Instance);
            var payload = Py.Tuple(Py.Tuple(new PyInt(0), new PySubStream(body)));
            PyValue[] head =
            [
                new PyInt(MachoMessage.CallReq), source, destination, new PyInt(Open.UserId), payload, PyNone.Instance,
            ];
            var packet = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
                new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
            await SendAsync(new Packet(Open.SessionId, MarshalCodec.Encode(packet)));
            while (true)
            {
                var message = await ReceiveAsync();
                if (message is not Send response) continue;
                var macho = MachoPacket.Decode(MarshalCodec.Decode(response.Payload))
                    ?? throw new InvalidOperationException("Host returned a non-Macho packet for a bound inventory call.");
                if (macho.Type == MachoMessage.Notification)
                {
                    Notifications.Add(macho);
                    continue;
                }
                if (macho.Type == MachoMessage.ErrorResponse)
                {
                    var error = Assert.IsType<PyTuple>(macho.Payload);
                    var errorCode = checked((int)Assert.IsType<PyInt>(error.Items[1]).Value);
                    var wrapped = Assert.IsType<PyTuple>(error.Items[2]).Items[0];
                    var errorPayload = wrapped is PySubStream substream ? substream.Decoded ?? PyNone.Instance : wrapped;
                    throw new MachoWrappedException(new MachoError(errorCode, errorPayload));
                }
                Assert.Equal(MachoMessage.CallRsp, macho.Type);
                var result = Assert.IsType<PyTuple>(macho.Payload).Items[0];
                return result is PySubStream substreamResult ? substreamResult.Decoded ?? PyNone.Instance : result;
            }
        }

        private Task SendAsync(HostMessage message) => _stream.WriteAsync(HostCodec.Encode(message)).AsTask();

        private async Task<HostMessage> ReceiveAsync()
        {
            var header = new byte[4];
            await _stream.ReadExactlyAsync(header);
            var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
            await _stream.ReadExactlyAsync(body);
            return HostCodec.Decode(body);
        }

        public async ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _client.Dispose();
            _cancellation.Cancel();
            _listener.Dispose();
            await _serving;
            _cancellation.Dispose();
        }
    }

    /// <summary>The fuel blocks of the Amarr tower's race (Helium) and of the Caldari (Nitrogen), SDE 4247 and 4051.</summary>
    private const int HeliumFuelBlock = 4247, NitrogenFuelBlock = 4051;

    /// <summary>Puts fuel blocks in a tower's fuel bay: its container, the items at its id.</summary>
    private static async Task FuelAsync(Rig rig, long tower, int fuelType, long quantity, long owner)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction, new Eve.Inventory.NewItem(fuelType, owner, tower, 0, quantity));
        await transaction.CommitAsync();
    }

    /// <summary>The quantity of one fuel block type in a tower's fuel bay.</summary>
    private static async Task<long> BayAsync(Rig rig, long tower, int fuelType) =>
        (await rig.Items.InContainerAsync(tower)).Where(stack => stack.TypeId == fuelType).Sum(stack => stack.Quantity);

    /// <summary>A pilot with the given types in its hold, in space in <paramref name="system"/>, with Anchoring III.</summary>
    private static async Task<(ClientSession Session, long[] Items)> PilotWithItemsAsync(Rig rig, int[] typeIds, long system)
    {
        var character = await rig.NewCharacterAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(Anchoring, AnchoringLevel, 0)]);
            await transaction.CommitAsync();
        }
        var items = new long[typeIds.Length];
        for (var index = 0; index < typeIds.Length; index++)
        {
            var granted = (await rig.Ships.GrantAsync(character.CharacterId, typeIds[index], 1, singleton: true))!.Single().After;
            var moved = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, CharacterShips.CargoFlag, 1);
            Assert.True(moved is Eve.Ships.ShipChanged, moved.ToString());
            items[index] = granted.Id;
        }
        await PlaceCharacterAtSystemStationAsync(rig, character, (int)system);

        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        session.Set("solarsystemid2", new PyInt(system));
        session.Set("corpid", new PyLong(TestCorporation));
        session.Set("isCEO", PyBool.True);
        session.Set("corprole", new PyLong(1));
        return (session, items);
    }

    private static async Task PlaceCharacterAtSystemStationAsync(Rig rig, Eve.Characters.Created character, int systemId)
    {
        long stationId;
        int constellationId;
        int regionId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var stationCommand = new NpgsqlCommand(
                "SELECT key FROM sde.\"npcStations\" WHERE (doc->>'solarSystemID')::int = $1 ORDER BY key LIMIT 1", connection);
            stationCommand.Parameters.Add(new NpgsqlParameter { Value = systemId });
            var stationKey = await stationCommand.ExecuteScalarAsync() as string
                ?? throw new InvalidOperationException($"SDE has no NPC station in system {systemId}");
            stationId = long.Parse(stationKey, System.Globalization.CultureInfo.InvariantCulture);

            await using var systemCommand = new NpgsqlCommand(
                "SELECT (doc->>'constellationID')::int, (doc->>'regionID')::int FROM sde.\"mapSolarSystems\" WHERE key = $1", connection);
            systemCommand.Parameters.Add(new NpgsqlParameter { Value = systemId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            await using var reader = await systemCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException($"SDE has no solar system {systemId}");
            constellationId = reader.GetInt32(0);
            regionId = reader.GetInt32(1);
        }

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Characters.SetStationAsync(connection, transaction, character.CharacterId, stationId);
            await rig.Characters.SetLocationAsync(connection, transaction, character.CharacterId, systemId, constellationId, regionId);
            await rig.Items.MoveToOwnerAsync(connection, transaction, character.ShipId, character.CharacterId, stationId, Eve.Inventory.ItemFlags.Hangar);
            await transaction.CommitAsync();
        }
    }

    /// <summary>Places the unanchored test tower at a real SDE moon warp-in point and sends the explicit posMgr anchor request.</summary>
    private static async Task AnchorAtMoonAsync(Rig rig, ClientSession session, long towerId)
    {
        var systemId = (int)(session.GetInt("solarsystemid2") ?? 0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT key, (doc->>'radius')::float8, (doc->'position'->>'x')::float8, (doc->'position'->>'y')::float8, (doc->'position'->>'z')::float8 " +
            "FROM sde.\"mapMoons\" WHERE (doc->>'solarSystemID')::int = $1 ORDER BY key LIMIT 1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = systemId });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"SDE has no moon in system {systemId}");
        var radius = reader.GetDouble(1);
        var x = reader.GetDouble(2);
        var y = reader.GetDouble(3);
        var z = reader.GetDouble(4);
        var warpRadius = radius + 5_000_000;
        var warpPoint = new[]
        {
            x + warpRadius * Math.Cos(radius),
            y + 1.3 * radius - 7_500,
            z - warpRadius * Math.Sin(radius),
        };
        await reader.DisposeAsync();

        var record = Assert.IsType<DeployedRecord>(await rig.Deployed.GetAsync(towerId));
        await rig.Deployed.SaveAsync(record with { Position = warpPoint });
        rig.Sim.PositionAnswers[towerId] = warpPoint;
        rig.Sim.PositionAnswers[session.GetInt("shipid")!.Value] = warpPoint;
        await rig.Containers.AnchorControlTowerAsync(session, towerId, [0, 0, 0], CancellationToken.None);
        var anchoredRecord = Assert.IsType<DeployedRecord>(await rig.Deployed.GetAsync(towerId));
        rig.Sim.PositionAnswers[session.GetInt("shipid")!.Value] = anchoredRecord.Position;
        rig.Sim.DropPointAnswer = (_, distance) =>
            [anchoredRecord.Position[0] + distance, anchoredRecord.Position[1], anchoredRecord.Position[2]];
        AssertControlTowerSlimState(rig, towerId, 1, rig.Now);
    }

    private static void AssertControlTowerSlimState(Rig rig, long towerId, int state, long? startedAtMs)
    {
        var fields = rig.Sim.Heard.OfType<SimSetSlimField>().Where(field => field.ItemId == towerId).ToArray();
        var stateField = fields.Last(field => field.FieldName == "posState");
        Assert.Equal(state, Assert.IsType<SimSlimInt>(stateField.Value).Value);
        var timestampField = fields.Last(field => field.FieldName == "posTimestamp");
        if (startedAtMs is { } started)
            Assert.Equal(Py.FileTimeFromUnixMs(started), Assert.IsType<SimSlimInt>(timestampField.Value).Value);
        else
            Assert.IsType<SimSlimNone>(timestampField.Value);
    }

    [PgFact]
    public async Task PosMgr_control_tower_calls_roundtrip_client_shapes_persist_configuration_and_notify_after_commit()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Containers.CharacterSessions = rig.CharacterSessions;
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, tower);
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();

        ValueTask<IReadOnlyList<ControlTowerRow>> ReadTowers(ClientSession pilot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ControlTowerRow>>(
                pilot.GetInt("corpid") == TestCorporation
                    ? [new ControlTowerRow(ControlTower, tower, (int)LowSecSystem)]
                    : []);
        }

        await using var posMgrHost = await PosMgrHostWire.StartAsync(new PosMgrService(readControlTowers: ReadTowers,
            unanchorControlTower: (pilot, itemId, token) => new ValueTask(rig.Containers.UnanchorControlTowerAsync(pilot, itemId, token)),
            anchorControlTower: (pilot, itemId, position, token) => new ValueTask(rig.Containers.AnchorControlTowerAsync(pilot, itemId, position, token)),
            operation: rig.Containers.PosMgrControlTowerOperationAsync), session, () => rig.Now);

        async Task<PyValue> Call(string method, params PyValue[] args) => await posMgrHost.CallAsync(method, args);
        void SetBothSessions(string key, PyValue value)
        {
            session.Set(key, value);
            posMgrHost.Session.Set(key, value);
        }

        var towerRows = Assert.IsType<PyList>(await Call("GetControlTowers"));
        var towerRow = Assert.IsType<PyList>(Assert.Single(towerRows.Items));
        Assert.Equal(new long[] { ControlTower, tower, LowSecSystem }, towerRow.Items.Select(Events.Number));

        Assert.Equal(1.0, Assert.IsType<PyReal>(await Call("GetConsumptionFactor", new PyLong(tower))).Value);
        var moon = Assert.IsType<PyTuple>(await Call("GetMoonForTower", new PyLong(tower)));
        Assert.True(Events.Number(moon.Items[0]) > 0);
        Assert.IsType<PyNone>(moon.Items[1]);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "SELECT key FROM sde.\"types\" WHERE (doc->>'groupID')::int = 404 AND doc->>'published' = 'true' ORDER BY key LIMIT 1", connection))
        {
            var typeId = int.Parse((string)(await command.ExecuteScalarAsync())!);
            var silo = (await rig.Ships.GrantAsync(session.CharacterId!.Value, typeId, 1, singleton: true))!.Single().After;
            await rig.Statics.EnsureTypesAsync([typeId]);
            var capacityAttribute = Assert.IsType<int>(rig.Statics.AttributeId("capacity"));
            var expectedCapacity = rig.Statics.Type(typeId)!.Attributes[capacityAttribute];
            var capacity = Assert.IsType<PyReal>(await Call("GetSiloCapacityByItemID", new PyLong(silo.Id))).Value;
            Assert.Equal(expectedCapacity, capacity, 8);
        }

        await Call("SetTowerSentrySettings", new PyLong(tower), new PyReal(5.5), new PyReal(-2.5),
            PyBool.True, PyBool.False, PyBool.False);
        var sentry = Assert.IsType<PyObject>(await Call("GetTowerSentrySettings", new PyLong(tower)));
        var sentryFields = Assert.IsType<PyDict>(sentry.Args);
        var sentryHeader = Assert.IsType<PyList>(Field(sentryFields, "header"));
        Assert.Equal(new[] { "standing", "status", "statusDrop", "war", "standingOwnerID" },
            sentryHeader.Items.Select(JsSemantics.StringValue));
        var sentryLine = Assert.IsType<PyList>(Field(sentryFields, "line"));
        Assert.Equal(5.5, Assert.IsType<PyReal>(sentryLine.Items[0]).Value);
        Assert.Equal(-2.5, Assert.IsType<PyReal>(sentryLine.Items[1]).Value);
        Assert.Equal(PyBool.True, sentryLine.Items[2]);
        Assert.Equal(PyBool.False, sentryLine.Items[3]);
        Assert.Equal(TestCorporation, Events.Number(sentryLine.Items[4]));

        await Call("SetTowerNotifications", new PyLong(tower), PyBool.True, PyBool.False);
        var notifications = Assert.IsType<PyObject>(await Call("GetTowerNotificationSettings", new PyLong(tower)));
        var notificationFields = Assert.IsType<PyDict>(notifications.Args);
        Assert.Equal(PyBool.True, Field(notificationFields, "showInCalendar"));
        Assert.Equal(PyBool.False, Field(notificationFields, "sendFuelNotifications"));

        // playerOwned.EnterTowerPassword uses the two-argument form; omitted
        // access flags must retain the tower's saved policy (V24.01 playerOwned.py:173-180).
        await Call("SetTowerPassword", new PyLong(tower), new PyStr("entry-password"));
        using (var entryConfig = System.Text.Json.JsonDocument.Parse(await ReadConfigurationAsync(rig, tower)))
        {
            Assert.True(entryConfig.RootElement.GetProperty("allowCorp").GetBoolean());
            Assert.True(entryConfig.RootElement.GetProperty("allowAlliance").GetBoolean());
            Assert.NotNull(entryConfig.RootElement.GetProperty("passwordHash").GetString());
        }

        await Call("SetTowerPassword", new PyLong(tower), new PyStr("tower-secret"), PyBool.True, PyBool.False);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        using (var json = System.Text.Json.JsonDocument.Parse(
            (await rig.Deployed.TowerConfigurationAsync(connection, null, tower))!))
        {
            var saved = json.RootElement.GetProperty("passwordHash").GetString();
            Assert.NotNull(saved);
            Assert.NotEqual("tower-secret", saved);
            Assert.Equal(64, saved.Length);
            Assert.Equal(32, json.RootElement.GetProperty("passwordSalt").GetString()!.Length);
        }
        var passwordConfigBeforeDisable = await ReadConfigurationAsync(rig, tower);
        using (var passwordConfigJson = System.Text.Json.JsonDocument.Parse(passwordConfigBeforeDisable))
        {
            var existingHash = passwordConfigJson.RootElement.GetProperty("passwordHash").GetString();
            await Call("SetTowerPassword", new PyLong(tower), PyBool.False, PyBool.False, PyBool.False);
            using var passwordConfigAfterDisable = System.Text.Json.JsonDocument.Parse(await ReadConfigurationAsync(rig, tower));
            Assert.Equal(existingHash, passwordConfigAfterDisable.RootElement.GetProperty("passwordHash").GetString());
            Assert.False(passwordConfigAfterDisable.RootElement.GetProperty("allowCorp").GetBoolean());
            Assert.False(passwordConfigAfterDisable.RootElement.GetProperty("allowAlliance").GetBoolean());
        }

        var deployFlags = Py.Dict(("anchor", new PyInt(1)), ("unanchor", new PyInt(1)),
            ("online", new PyInt(1)), ("offline", new PyInt(0)));
        var usageFlags = Py.Rowset(["structureID", "viewput", "viewputtake", "use"],
            [Py.List(new PyLong(tower), new PyInt(1), new PyInt(2), new PyInt(3))]);
        await Call("SetStarbasePermissions", new PyLong(tower), deployFlags, usageFlags);
        var permissions = Assert.IsType<PyTuple>(await Call("GetStarbasePermissions", new PyLong(tower)));
        var savedDeployFlags = Assert.IsType<PyDict>(permissions.Items[0]);
        Assert.Equal(new PyInt(1), savedDeployFlags.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == "unanchor").Value);
        var permissionRowset = Assert.IsType<PyObject>(permissions.Items[1]);
        var rowsetFields = Assert.IsType<PyDict>(permissionRowset.Args);
        Assert.Equal(new[] { "structureID", "viewput", "viewputtake", "use" },
            Assert.IsType<PyList>(Field(rowsetFields, "header")).Items.Select(JsSemantics.StringValue));
        var permissionRows = Assert.IsType<PyList>(Field(rowsetFields, "lines"));
        var permissionRow = Assert.IsType<PyList>(Assert.Single(permissionRows.Items));
        Assert.Equal(new long[] { tower, 1, 2, 3 }, permissionRow.Items.Select(Events.Number));

        var priorConfig = await ReadConfigurationAsync(rig, tower);
        var invalidProvisionError = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(
            "ChangeStructureProvisionType", new PyLong(tower), new PyLong(tower + 100), new PyInt(34)));
        Assert.Equal("CustomNotify", invalidProvisionError.Error.UserErrorMessage);
        Assert.Equal(priorConfig, await ReadConfigurationAsync(rig, tower));

        _ = Events.Of(session);
        var malformedConfig = await ReadConfigurationAsync(rig, tower);
        var malformedLinks = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(
            "LinkResourceForTower", new PyLong(tower), new PyStr("invalid-links")));
        Assert.Equal("CustomNotify", malformedLinks.Error.UserErrorMessage);
        Assert.Equal(malformedConfig, await ReadConfigurationAsync(rig, tower));
        Assert.Empty(Events.Of(session));

        var seededLinks = System.Text.Json.Nodes.JsonNode.Parse(malformedConfig)!;
        seededLinks["links"] = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["fromItemId"] = 980_000_101,
                ["toItemId"] = 980_000_102,
                ["typeId"] = 34,
            },
        };
        await using (var linkConnection = await rig.Source.OpenConnectionAsync())
        await using (var linkTransaction = await linkConnection.BeginTransactionAsync())
        {
            Assert.True(await rig.Deployed.SetTowerConfigurationAsync(linkConnection, linkTransaction, tower,
                seededLinks.ToJsonString()));
            await linkTransaction.CommitAsync();
        }

        var observer = new ClientSession(new Sink()) { SessionId = 42, ClientId = 42, UserId = 43, NodeId = 1 };
        observer.CharacterId = 980_000_042;
        observer.Set("corpid", new PyLong(TestCorporation));
        observer.Set("solarsystemid2", new PyInt((int)LowSecSystem));
        rig.CharacterSessions.Enter(observer.CharacterId.Value, observer);
        var linkResult = Assert.IsType<PyBool>(await Call("LinkResourceForTower", new PyLong(tower), new PyList([])));
        Assert.True(linkResult.Value);
        var wireNotification = Assert.Single(posMgrHost.Notifications);
        Assert.Equal("OnMoonProcessChange", wireNotification.Dest.BroadcastId);
        Assert.Equal("clientID", wireNotification.Dest.IdType);
        var linkEvent = Assert.Single(Events.Of(session));
        Assert.Equal("OnMoonProcessChange", linkEvent.Name);
        Assert.Equal("clientID", linkEvent.IdType);
        Assert.Equal(2, linkEvent.Args.Length);
        Assert.Equal(tower, Events.Number(linkEvent.Args[0]));
        Assert.Empty(Assert.IsType<PyList>(linkEvent.Args[1]).Items);
        using (var clearedLinks = System.Text.Json.JsonDocument.Parse(await ReadConfigurationAsync(rig, tower)))
            Assert.Empty(clearedLinks.RootElement.GetProperty("links").EnumerateArray());
        var observerEvent = Assert.Single(Events.Of(observer));
        Assert.Equal("OnMoonProcessChange", observerEvent.Name);
        Assert.Equal(tower, Events.Number(observerEvent.Args[0]));

        foreach (var method in new[] { "RunMoonProcessCycleforTower", "GetMoonProcessInfoForTower" })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(method, new PyLong(tower)));
            Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
            if (method == "GetMoonProcessInfoForTower")
                Assert.Contains("Use /pos production status <towerID> for server diagnostics", Texts(error.Error.Payload));
        }
        SetBothSessions("role", new PyLong(1L << 54));
        var gmCycleError = await Assert.ThrowsAsync<MachoWrappedException>(() => Call("RunMoonProcessCycleforTower", new PyLong(tower)));
        Assert.Equal("CustomNotify", gmCycleError.Error.UserErrorMessage);
        Assert.Contains("No production links are configured", Texts(gmCycleError.Error.Payload));
        SetBothSessions("role", new PyInt(0));

        var rebuilt = new PosMgrService(readControlTowers: ReadTowers,
            unanchorControlTower: (pilot, itemId, token) => new ValueTask(rig.Containers.UnanchorControlTowerAsync(pilot, itemId, token)),
            anchorControlTower: (pilot, itemId, position, token) => new ValueTask(rig.Containers.AnchorControlTowerAsync(pilot, itemId, position, token)),
            operation: rig.Containers.PosMgrControlTowerOperationAsync);
        posMgrHost.Registry.Register(rebuilt);
        var persistedNotifications = Assert.IsType<PyObject>(await Call("GetTowerNotificationSettings", new PyLong(tower)));
        var persistedNotificationFields = Assert.IsType<PyDict>(persistedNotifications.Args);
        Assert.Equal(PyBool.True, Field(persistedNotificationFields, "showInCalendar"));
        Assert.Equal(PyBool.False, Field(persistedNotificationFields, "sendFuelNotifications"));

        await Call("SetStarbasePermissions", new PyLong(tower),
            Py.Dict(("anchor", new PyInt(1)), ("unanchor", new PyInt(0)), ("online", new PyInt(1)), ("offline", new PyInt(0))),
            usageFlags);
        SetBothSessions("isCEO", PyBool.False);
        SetBothSessions("corprole", new PyLong(0));
        var deniedUnanchor = await Assert.ThrowsAsync<MachoWrappedException>(() => Call("UnanchorStructure", new PyLong(tower)));
        Assert.Equal("CrpAccessDenied", deniedUnanchor.Error.UserErrorMessage);
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));

        SetBothSessions("isCEO", PyBool.True);
        await Call("SetStarbasePermissions", new PyLong(tower), deployFlags, usageFlags);
        session.Set("isCEO", PyBool.False);
        Assert.IsType<PyNone>(await Call("UnanchorStructure", new PyLong(tower)));
        Assert.Equal("unanchoring", await rig.Deployed.TowerStateAsync(tower));
    }

    [PgFact]
    public async Task PosMgr_anchors_POS_modules_and_roundtrips_module_permissions_and_production_links_over_host_wire()
    {
        await using var rig = await Rig.CreateAsync();
        int siloTypeId;
        int materialTypeId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var siloCommand = new NpgsqlCommand(
                "SELECT key FROM sde.\"types\" WHERE (doc->>'groupID')::int = 404 AND doc->>'published' = 'true' ORDER BY key LIMIT 1", connection);
            siloTypeId = int.Parse((string)(await siloCommand.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
            await using var materialCommand = new NpgsqlCommand(
                "SELECT key FROM sde.\"types\" WHERE (doc->>'groupID')::int = 427 AND doc->>'published' = 'true' ORDER BY key LIMIT 1", connection);
            materialTypeId = int.Parse((string)(await materialCommand.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
        }

        rig.Containers.CharacterSessions = rig.CharacterSessions;
        int moonHarvesterTypeId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var harvesterCommand = new NpgsqlCommand(
                "SELECT key FROM sde.\"types\" WHERE (doc->>'groupID')::int = 416 AND doc->>'published' = 'true' ORDER BY key LIMIT 1", connection);
            moonHarvesterTypeId = int.Parse((string)(await harvesterCommand.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
        }
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower, siloTypeId, siloTypeId, moonHarvesterTypeId], LowSecSystem);
        var (tower, sourceSilo, destinationSilo, moonHarvester) = (items[0], items[1], items[2], items[3]);
        Assert.Equal(tower, await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None));
        await AnchorAtMoonAsync(rig, session, tower);
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();

        var towerRecord = Assert.IsType<DeployedRecord>(await rig.Deployed.GetAsync(tower));
        var posMgr = new PosMgrService(
            anchorControlTower: (pilot, itemId, position, token) => new ValueTask(rig.Containers.AnchorControlTowerAsync(pilot, itemId, position, token)),
            operation: rig.Containers.PosMgrControlTowerOperationAsync);
        await using var host = await PosMgrHostWire.StartAsync(posMgr, session, () => rig.Now);
        await using var concurrentHost = await PosMgrHostWire.StartAsync(posMgr, session, () => rig.Now);
        async Task<PyValue> Call(string method, params PyValue[] args) => await host.CallAsync(method, args);

        // The production UI manages modules attached by the starbase anchoring flow.
        // This must create authoritative space state before posMgr accepts module IDs.
        Assert.Equal(sourceSilo, await rig.Containers.LaunchFromShipAsync(session, sourceSilo, CancellationToken.None));
        Assert.Equal(destinationSilo, await rig.Containers.LaunchFromShipAsync(session, destinationSilo, CancellationToken.None));
        var sourcePosition = new[] { towerRecord.Position[0] + 10_000, towerRecord.Position[1], towerRecord.Position[2] };
        var destinationPosition = new[] { towerRecord.Position[0] + 20_000, towerRecord.Position[1], towerRecord.Position[2] };
        rig.Sim.PositionAnswers[sourceSilo] = sourcePosition;
        rig.Sim.PositionAnswers[destinationSilo] = destinationPosition;
        rig.Sim.PositionAnswers[session.GetInt("shipid")!.Value] = sourcePosition;
        await Call("AnchorStructure", new PyLong(sourceSilo), new PyTuple(sourcePosition.Select(value => (PyValue)new PyReal(value)).ToArray()));
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        rig.Sim.PositionAnswers[session.GetInt("shipid")!.Value] = destinationPosition;
        await Call("AnchorStructure", new PyLong(destinationSilo), new PyTuple(destinationPosition.Select(value => (PyValue)new PyReal(value)).ToArray()));
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();

        Assert.Equal(moonHarvester, await rig.Containers.LaunchFromShipAsync(session, moonHarvester, CancellationToken.None));
        var harvesterPosition = new[] { towerRecord.Position[0] + 30_000, towerRecord.Position[1], towerRecord.Position[2] };
        rig.Sim.PositionAnswers[moonHarvester] = harvesterPosition;
        rig.Sim.PositionAnswers[session.GetInt("shipid")!.Value] = harvesterPosition;
        await Call("AnchorStructure", new PyLong(moonHarvester), new PyTuple(harvesterPosition.Select(value => (PyValue)new PyReal(value)).ToArray()));
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();

        // V24 can clear a moon harvester provision even when no historical abundance profile exists.
        await Call("ChangeStructureProvisionType", new PyLong(tower), new PyLong(moonHarvester), PyNone.Instance);
        using (var clearedProvision = System.Text.Json.JsonDocument.Parse(await ReadConfigurationAsync(rig, tower)))
            Assert.False(clearedProvision.RootElement.GetProperty("provisionTypes").TryGetProperty(moonHarvester.ToString(), out _));

        var beforeInvalidProvision = await ReadConfigurationAsync(rig, tower);
        var invalidProvision = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(
            "ChangeStructureProvisionType", new PyLong(tower), new PyLong(sourceSilo), new PyInt(moonHarvesterTypeId)));
        Assert.Equal("CustomNotify", invalidProvision.Error.UserErrorMessage);
        Assert.Equal(beforeInvalidProvision, await ReadConfigurationAsync(rig, tower));

        await Task.WhenAll(
            host.CallAsync("ChangeStructureProvisionType", new PyLong(tower), new PyLong(sourceSilo), new PyInt(materialTypeId)),
            concurrentHost.CallAsync("ChangeStructureProvisionType", new PyLong(tower), new PyLong(destinationSilo), new PyInt(materialTypeId)));
        // A retried request is idempotent and preserves the other module's committed provision.
        await Call("ChangeStructureProvisionType", new PyLong(tower), new PyLong(sourceSilo), new PyInt(materialTypeId));
        using (var concurrentProvisions = System.Text.Json.JsonDocument.Parse(await ReadConfigurationAsync(rig, tower)))
        {
            var provisions = concurrentProvisions.RootElement.GetProperty("provisionTypes");
            Assert.Equal(materialTypeId, provisions.GetProperty(sourceSilo.ToString()).GetInt32());
            Assert.Equal(materialTypeId, provisions.GetProperty(destinationSilo.ToString()).GetInt32());
        }
        var deployFlags = Py.Dict(("anchor", new PyInt(1)), ("unanchor", new PyInt(1)),
            ("online", new PyInt(1)), ("offline", new PyInt(1)));
        var usageFlags = Py.Rowset(["structureID", "viewput", "viewputtake", "use"],
        [
            Py.List(new PyLong(tower), new PyInt(1), new PyInt(0), new PyInt(0)),
            Py.List(new PyLong(sourceSilo), new PyInt(2), new PyInt(1), new PyInt(1)),
            Py.List(new PyLong(destinationSilo), new PyInt(1), new PyInt(2), new PyInt(3)),
        ]);
        var duplicateUsageFlags = Py.Rowset(["structureID", "viewput", "viewputtake", "use"],
        [
            Py.List(new PyLong(sourceSilo), new PyInt(2), new PyInt(1), new PyInt(1)),
            Py.List(new PyLong(sourceSilo), new PyInt(1), new PyInt(0), new PyInt(0)),
        ]);
        var duplicatePermissions = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(
            "SetStarbasePermissions", new PyLong(tower), deployFlags, duplicateUsageFlags));
        Assert.Equal("CustomNotify", duplicatePermissions.Error.UserErrorMessage);
        var unassociatedUsageFlags = Py.Rowset(["structureID", "viewput", "viewputtake", "use"],
            [Py.List(new PyLong(tower + 100), new PyInt(1), new PyInt(0), new PyInt(0))]);
        var beforeUnassociatedPermissions = await ReadConfigurationAsync(rig, tower);
        var unassociatedPermissions = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(
            "SetStarbasePermissions", new PyLong(tower), deployFlags, unassociatedUsageFlags));
        Assert.Equal("CustomNotify", unassociatedPermissions.Error.UserErrorMessage);
        Assert.Equal(beforeUnassociatedPermissions, await ReadConfigurationAsync(rig, tower));
        await Call("SetStarbasePermissions", new PyLong(tower), deployFlags, usageFlags);
        var permissions = Assert.IsType<PyTuple>(await Call("GetStarbasePermissions", new PyLong(tower)));
        var permissionFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(permissions.Items[1]).Args);
        var rows = Assert.IsType<PyList>(Field(permissionFields, "lines"));
        Assert.Equal(new[] { tower, sourceSilo, destinationSilo, moonHarvester }, rows.Items
            .Select(row => Events.Number(Assert.IsType<PyList>(row).Items[0])).ToArray());
        Assert.Equal(new long[][]
        {
            [tower, 1, 0, 0], [sourceSilo, 2, 1, 1], [destinationSilo, 1, 2, 3], [moonHarvester, 3, 0, 0],
        }, rows.Items.Select(row => Assert.IsType<PyList>(row).Items.Select(Events.Number).ToArray()).ToArray());

        var beforeInvalidLink = await ReadConfigurationAsync(rig, tower);
        var mismatchedLink = await Assert.ThrowsAsync<MachoWrappedException>(() => Call("LinkResourceForTower", new PyLong(tower), new PyList
        ([Py.List(new PyLong(sourceSilo), new PyLong(destinationSilo), new PyInt(moonHarvesterTypeId))])));
        Assert.Equal("CustomNotify", mismatchedLink.Error.UserErrorMessage);
        Assert.Equal(beforeInvalidLink, await ReadConfigurationAsync(rig, tower));
        var cycleError = await Assert.ThrowsAsync<MachoWrappedException>(() => Call("LinkResourceForTower", new PyLong(tower), new PyList
        ([Py.List(new PyLong(sourceSilo), new PyLong(destinationSilo), new PyInt(materialTypeId)),
            Py.List(new PyLong(destinationSilo), new PyLong(sourceSilo), new PyInt(materialTypeId))])));
        Assert.Equal("CustomNotify", cycleError.Error.UserErrorMessage);
        host.Notifications.Clear();
        Assert.Equal(PyBool.True, await Call("LinkResourceForTower", new PyLong(tower), new PyList
        ([Py.List(new PyLong(sourceSilo), new PyLong(destinationSilo), new PyInt(materialTypeId))])));
        var notification = Assert.Single(host.Notifications, notice => notice.Dest.BroadcastId == "OnMoonProcessChange");
        Assert.Equal("clientID", notification.Dest.IdType);
        var notificationArguments = NotificationArguments(notification);
        Assert.Equal(tower, Events.Number(notificationArguments[0]));
        Assert.Empty(Assert.IsType<PyList>(notificationArguments[1]).Items);
        using var persisted = System.Text.Json.JsonDocument.Parse(await ReadConfigurationAsync(rig, tower));
        Assert.Equal(materialTypeId, persisted.RootElement.GetProperty("provisionTypes").GetProperty(sourceSilo.ToString()).GetInt32());
        Assert.Equal(1, persisted.RootElement.GetProperty("links").GetArrayLength());
        Assert.Equal(sourceSilo, persisted.RootElement.GetProperty("links")[0].GetProperty("fromItemId").GetInt64());
        Assert.Equal(tower, (await rig.Deployed.GetAsync(sourceSilo))!.ControlTowerId);
        Assert.Equal(tower, (await rig.Deployed.GetAsync(destinationSilo))!.ControlTowerId);
        Assert.Equal(tower, (await rig.Deployed.GetAsync(moonHarvester))!.ControlTowerId);

        rig.Sim.Heard.Clear();
        await rig.Containers.RestoreDeployedAsync((int)LowSecSystem);
        var restoredModules = rig.Sim.Heard.OfType<SimPlaceControlTower>().Where(ball =>
            ball.DeployableId == sourceSilo || ball.DeployableId == destinationSilo || ball.DeployableId == moonHarvester).ToArray();
        Assert.Equal(3, restoredModules.Length);
        Assert.All(restoredModules, ball => Assert.True(ball.Active));
        AssertControlTowerSlimState(rig, sourceSilo, 1, null);
        AssertControlTowerSlimState(rig, destinationSilo, 1, null);
        AssertControlTowerSlimState(rig, moonHarvester, 1, null);
    }

    private static PyValue? Field(PyDict fields, string name) => fields.Entries
        .Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

    private static string BoundOid(PyValue value)
    {
        var substruct = Assert.IsType<PySubStruct>(value);
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(substruct.Value).Decoded);
        return JsSemantics.StringValue(fields.Items[0]) ?? throw new Xunit.Sdk.XunitException("The bound inventory has no object ID.");
    }

    [PgFact]
    public async Task PosMgr_saved_usage_permissions_control_POS_inventory_over_registered_host_wire()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Containers.CharacterSessions = rig.CharacterSessions;
        int siloTypeId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var typeCommand = new NpgsqlCommand(
                "SELECT key FROM sde.\"types\" WHERE (doc->>'groupID')::int = 404 AND doc->>'published' = 'true' ORDER BY key LIMIT 1", connection);
            siloTypeId = int.Parse((string)(await typeCommand.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
        }
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower, siloTypeId], LowSecSystem);
        var (tower, silo) = (items[0], items[1]);
        Assert.Equal(tower, await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None));
        await AnchorAtMoonAsync(rig, session, tower);
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        var towerRecord = (await rig.Deployed.GetAsync(tower))!;

        var posMgr = new PosMgrService(
            anchorControlTower: (pilot, itemId, position, token) => new ValueTask(rig.Containers.AnchorControlTowerAsync(pilot, itemId, position, token)),
            operation: rig.Containers.PosMgrControlTowerOperationAsync);
        await using var host = await PosMgrHostWire.StartAsync(posMgr, session, () => rig.Now, rig.Inventory);
        async Task<PyValue> PosCall(string method, params PyValue[] args) => await host.CallAsync(method, args);

        Assert.Equal(silo, await rig.Containers.LaunchFromShipAsync(session, silo, CancellationToken.None));
        var modulePosition = new[] { towerRecord.Position[0] + 10_000, towerRecord.Position[1], towerRecord.Position[2] };
        rig.Sim.PositionAnswers[silo] = modulePosition;
        rig.Sim.PositionAnswers[session.GetInt("shipid")!.Value] = modulePosition;
        await PosCall("AnchorStructure", new PyLong(silo), new PyTuple(modulePosition.Select(value => (PyValue)new PyReal(value)).ToArray()));
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        await rig.Deployed.SetTowerStateAsync(tower, "online");
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(silo));

        await PosCall("ChangeStructureProvisionType", new PyLong(tower), new PyLong(silo), new PyInt(34));

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.CreateAsync(connection, transaction, new Eve.Inventory.NewItem(34, TestCorporation, silo, 0, 3));
            var cargo = new Eve.Inventory.NewItem(34, session.CharacterId!.Value, session.GetInt("shipid")!.Value,
                CharacterShips.CargoFlag, 2);
            await rig.Items.CreateAsync(connection, transaction, cargo);
            var wrongProvisionCargo = new Eve.Inventory.NewItem(35, session.CharacterId!.Value, session.GetInt("shipid")!.Value,
                CharacterShips.CargoFlag, 1);
            await rig.Items.CreateAsync(connection, transaction, wrongProvisionCargo);
            await transaction.CommitAsync();
        }

        var deployFlags = Py.Dict(("anchor", new PyInt(0)), ("unanchor", new PyInt(0)),
            ("online", new PyInt(0)), ("offline", new PyInt(0)));
        var usageFlags = Py.Rowset(["structureID", "viewput", "viewputtake", "use"],
            [Py.List(new PyLong(silo), new PyInt(1), new PyInt(0), new PyInt(0))]);
        await PosCall("SetStarbasePermissions", new PyLong(tower), deployFlags, usageFlags);

        host.Session.Set("isCEO", PyBool.False);
        host.Session.Set("corprole", new PyLong(0));
        await rig.Deployed.SetTowerStateAsync(silo, "online");
        var onlineSilo = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            host.CallServiceAsync("invbroker", "GetInventoryFromId", new PyLong(silo)));
        Assert.Equal("STARBASE_PRODUCTION_MUST_BE_OFFLINE", onlineSilo.Error.UserErrorMessage);
        await rig.Deployed.SetTowerStateAsync(silo, "offline");
        var siloInventory = BoundOid(await host.CallServiceAsync("invbroker", "GetInventoryFromId", new PyLong(silo)));
        var shipInventory = BoundOid(await host.CallServiceAsync("invbroker", "GetInventoryFromId", new PyLong(session.GetInt("shipid")!.Value)));
        var siloContents = Assert.IsType<PyObject>(await host.BoundCallAsync(siloInventory, "GetContainerContents"));
        var siloLines = Assert.IsType<PyList>(Field(Assert.IsType<PyDict>(siloContents.Args), "lines"));
        Assert.Single(siloLines.Items);

        var cargoItem = Assert.Single(await rig.Items.InContainerAsync(session.GetInt("shipid")!.Value), item => item.TypeId == 34);
        host.Notifications.Clear();
        Assert.Equal(cargoItem.Id, Events.Number(await host.BoundCallAsync(siloInventory, "Add",
            new PyLong(cargoItem.Id), new PyLong(session.GetInt("shipid")!.Value))));
        var deposited = (await rig.Items.GetAsync(cargoItem.Id))!;
        Assert.Equal(silo, deposited.LocationId);
        Assert.Equal(session.CharacterId, deposited.OwnerId);
        Assert.Equal("OnItemChange", Assert.Single(host.Notifications).Dest.BroadcastId);

        var wrongProvisionStack = Assert.Single(await rig.Items.InContainerAsync(session.GetInt("shipid")!.Value), item => item.TypeId == 35);
        host.Notifications.Clear();
        var invalidDeposit = await Assert.ThrowsAsync<MachoWrappedException>(() => host.BoundCallAsync(siloInventory, "Add",
            new PyLong(wrongProvisionStack.Id), new PyLong(session.GetInt("shipid")!.Value)));
        Assert.Equal("STARBASE_PRODUCTION_PROVISION_INVALID", invalidDeposit.Error.UserErrorMessage);
        Assert.Equal(session.GetInt("shipid"), (await rig.Items.GetAsync(wrongProvisionStack.Id))!.LocationId);
        Assert.Empty(host.Notifications);

        var storedCargo = Assert.Single(await rig.Items.InContainerAsync(silo), item => item.Id == cargoItem.Id);
        host.Notifications.Clear();
        var prohibitedTake = await Assert.ThrowsAsync<MachoWrappedException>(() => host.BoundCallAsync(shipInventory, "Add",
            new PyLong(storedCargo.Id), new PyLong(silo)));
        Assert.Equal("STARBASE_PRODUCTION_ACCESS_DENIED", prohibitedTake.Error.UserErrorMessage);
        var prohibitedMultiTake = await Assert.ThrowsAsync<MachoWrappedException>(() => host.BoundCallAsync(shipInventory, "MultiAdd",
            new PyList([new PyLong(storedCargo.Id)]), new PyLong(silo)));
        Assert.Equal("STARBASE_PRODUCTION_ACCESS_DENIED", prohibitedMultiTake.Error.UserErrorMessage);
        Assert.Equal(2, (await rig.Items.GetAsync(cargoItem.Id))!.Quantity);
        Assert.Empty(host.Notifications);

        host.Session.Set("isCEO", PyBool.True);
        var caretakerTakeFlags = Py.Rowset(["structureID", "viewput", "viewputtake", "use"],
            [Py.List(new PyLong(silo), new PyInt(1), new PyInt(3), new PyInt(0))]);
        await PosCall("SetStarbasePermissions", new PyLong(tower), deployFlags, caretakerTakeFlags);
        host.Session.Set("isCEO", PyBool.False);
        host.Session.Set("corprole", new PyLong(288_230_376_151_711_744L));
        Assert.Equal(cargoItem.Id, Events.Number(await host.BoundCallAsync(shipInventory, "Add",
            new PyLong(storedCargo.Id), new PyLong(silo))));
        var taken = (await rig.Items.GetAsync(cargoItem.Id))!;
        Assert.Equal(session.GetInt("shipid"), taken.LocationId);
        Assert.Equal(session.CharacterId, taken.OwnerId);
        Assert.Equal(2, taken.Quantity);
        Assert.Equal(new[] { "OnItemsChanged", "OnClientEvent_MoveFromCargoToHangar" },
            host.Notifications.Select(notification => notification.Dest.BroadcastId).ToArray());
    }

    private static IReadOnlyList<PyValue> NotificationArguments(MachoPacket packet)
    {
        var envelope = Assert.IsType<PyTuple>(packet.Payload).Items[0];
        var buffer = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(envelope).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(buffer.Value));
        var eventCall = Assert.IsType<PyTuple>(decoded.Items[1]);
        return Assert.IsType<PyTuple>(eventCall.Items[1]).Items;
    }

    private static async Task<string> ReadConfigurationAsync(Rig rig, long tower)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        return (await rig.Deployed.TowerConfigurationAsync(connection, null, tower))!;
    }

    /// <summary>Every text inside a wrapped error's payload, joined (the refusal's words).</summary>
    private static string Texts(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyBytes text => System.Text.Encoding.UTF8.GetString(text.Value.Span),
        PyTuple tuple => string.Join("|", tuple.Items.Select(Texts)),
        PyList list => string.Join("|", list.Items.Select(Texts)),
        PyDict dict => string.Join("|", dict.Entries.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) })),
        PyObjectEx ex => string.Join("|", new[] { Texts(ex.Header) }.Concat(ex.List.Select(Texts)).Concat(ex.Dict.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) }))),
        PyObject obj => Texts(obj.Args),
        _ => "",
    };

    [PgFact]
    public async Task A_dropped_control_tower_stays_unanchored_until_the_pilot_requests_anchor()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        var launchedAt = rig.Now;

        Assert.Equal(tower, await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None));

        var ball = Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>());
        Assert.False(ball.Active, "a control tower is dropped unanchored");
        Assert.Equal(new[] { 2_500.0, 0.0, 0.0 }, ball.Position.ToArray());
        Assert.Equal(3_394.0, ball.Radius);
        var record = (await rig.Deployed.GetAsync(tower))!;
        Assert.Equal("control_tower", record.Kind);
        Assert.Equal(long.MaxValue, record.ActivateAtMs);
        Assert.Equal(long.MaxValue, record.ExpiresAtMs);
        Assert.Equal("unanchored", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 0, null);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs + 100_000;
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive anchored && anchored.DeployableId == tower && anchored.Active);

        // no time takes it out: a sweep a hundred days later leaves it in space
        rig.Sim.Heard.Clear();
        rig.Now = launchedAt + 100L * 24 * 60 * 60 * 1000;
        await rig.Containers.SweepAsync();
        Assert.NotNull(await rig.Items.GetAsync(tower));
        Assert.Empty(rig.Sim.Heard.OfType<SimRemoveDeployed>());
    }

    [PgFact]
    public async Task A_control_tower_refuses_a_mobile_observatory_within_100_km_until_its_corporation_removes_it()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower, Observatory], LowSecSystem);
        var (tower, observatory) = (items[0], items[1]);
        await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, tower);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, observatory, CancellationToken.None));
        Assert.Contains("control tower", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(observatory))!.Flag);

        // the requested anchor completes after the SDE delay; it stays offline until explicitly onlined. Another corporation may not unanchor it.
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        var launching = session.GetInt("corpid") ?? 0;
        session.Set("corpid", new PyInt(launching + 1));
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.UnanchorControlTowerAsync(session, tower));
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        session.Set("corpid", new PyInt(launching));
        rig.Sim.Heard.Clear();
        await rig.Containers.UnanchorControlTowerAsync(session, tower);
        Assert.Equal("unanchoring", await rig.Deployed.TowerStateAsync(tower));

        // the unanchoring runs for the SDE's hour (attribute 676): the tower is still in space until then
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimRemoveDeployed);
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == tower);
        Assert.Null(await rig.Deployed.GetAsync(tower));

        // the item is not deleted: it is dropped where the tower was, as an unanchor drop (the structure rule), marked for its corporation
        Assert.Equal(StructureUnanchorDrop.HullMarker(tower), (await rig.Items.GetAsync(tower))!.CustomInfo);
        Assert.Contains(rig.Sim.Heard, m => m is SimPlaceWreck wreck && wreck.WreckId == tower);

        // with the tower gone, the observatory drops
        Assert.Equal(observatory, await rig.Containers.LaunchFromShipAsync(session, observatory, CancellationToken.None));
    }

    [PgFact]
    public async Task A_control_tower_in_space_comes_back_after_a_restart_unanchored_or_anchored_as_its_record_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, items[0]);
        rig.Sim.Heard.Clear();

        rig.Now += 1_000;
        await rig.Containers.RestoreDeployedAsync(system);
        Assert.False(Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>()).Active);
        AssertControlTowerSlimState(rig, items[0], 1, rig.Now - 1_000);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        await rig.Containers.RestoreDeployedAsync(system);
        Assert.True(Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>()).Active);
        AssertControlTowerSlimState(rig, items[0], 1, null);

        var tower = items[0];
        var corporationId = session.GetInt("corpid")!.Value;
        await FuelAsync(rig, tower, HeliumFuelBlock, 40, corporationId);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, tower, "onlineForStructures", 0, 1, CancellationToken.None));
        var onlineStartedAtMs = rig.Now;
        Assert.Equal(onlineStartedAtMs, await rig.Deployed.TowerStateStartedAtMsAsync(tower));

        await using var restartedScheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, null, rig.SchedulerSchema);
        using var restartedContainers = new SpaceContainers(rig.Sim, rig.Ships, rig.Items, rig.SpaceItems,
            new PgWorkCycleLedger(rig.Source, rig.SpaceSchema), new EventBusFacts(rig.Events), rig.Statics, rig.Space,
            () => rig.Now, deployed: rig.Deployed, scheduler: restartedScheduler, source: rig.Source);
        restartedScheduler.Register(SpaceContainers.ControlTowerOnlineJob, async (context, token) =>
        {
            var due = context.Payload<SpaceContainers.ControlTowerOnlineDue>()!;
            if (await restartedContainers.CompleteControlTowerOnlineAsync(context.Connection, context.Transaction, due.DeployableId, due.StartedAtMs, token))
                context.AfterCommit(_ => { rig.Events.Wake(); return Task.CompletedTask; });
        });
        restartedScheduler.Register(SpaceContainers.ControlTowerFuelJob, async (context, token) =>
        {
            if (await restartedContainers.ControlTowerFuelAsync(context.Connection, context.Transaction,
                context.Payload<SpaceContainers.ControlTowerFuelDue>()!.DeployableId, context.ScheduledForMs, token))
                context.AfterCommit(_ => { rig.Events.Wake(); return Task.CompletedTask; });
        });

        rig.Sim.Heard.Clear();
        rig.Now += 1_000;
        await restartedContainers.RestoreDeployedAsync(system);
        Assert.True(Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>()).Active);
        AssertControlTowerSlimState(rig, tower, 2, onlineStartedAtMs);
        Assert.Equal("onlining", await rig.Deployed.TowerStateAsync(tower));

        rig.Now += OnliningMs - 1_000;
        await restartedScheduler.RunDueAsync();
        Assert.Equal("online", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 4, null);
        Assert.Equal(0, await BayAsync(rig, tower, HeliumFuelBlock));
    }

    [PgFact]
    public async Task Tower_eligibility_events_follow_deadline_and_transactional_removal()
    {
        await using var rig = await Rig.CreateAsync();
        var bus = new Eve.Events.EventBus(rig.Source, rig.Scheduler,
            new Eve.Events.EventBusOptions { MaxAttempts = 3 }, rig.EventsSchema, _ => { });
        var seen = new List<long>();
        bus.Subscribe("test.tower.eligibility", [SpaceContainers.ControlTowerEligibilityChangedTopic], (context, _) =>
        {
            var payload = context.Event.Payload<SpaceContainers.ControlTowerEligibilityChanged>()!;
            context.AfterCommit(_ => { seen.Add(payload.CorporationId); return Task.CompletedTask; });
            return Task.CompletedTask;
        });
        await bus.PumpAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        const long corporation = 98_000_001;
        session.Set("corpid", new PyLong(corporation));
        await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, tower);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.PublishControlTowerAnchoredAsync(connection, transaction, bus, tower);
            await transaction.CommitAsync();
        }
        await bus.PumpAsync();
        Assert.Empty(seen);
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.PublishControlTowerAnchoredAsync(connection, transaction, bus, tower);
            await transaction.CommitAsync();
        }
        await bus.PumpAsync();
        Assert.Equal(corporation, Assert.Single(seen));
        await rig.Containers.UnanchorControlTowerAsync(session, tower);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.DeleteAsync(connection, transaction, tower);
            await transaction.CommitAsync();
        }
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.CompleteControlTowerUnanchorAsync(tower, connection, transaction, bus);
            await transaction.RollbackAsync();
        }
        await bus.PumpAsync();
        Assert.NotNull(await rig.Deployed.GetAsync(tower));
        Assert.Single(seen);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.CompleteControlTowerUnanchorAsync(tower, connection, transaction, bus);
            await transaction.CommitAsync();
        }
        await bus.PumpAsync();
        Assert.Null(await rig.Deployed.GetAsync(tower));
        Assert.Equal(new[] { corporation, corporation }, seen);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.CompleteControlTowerUnanchorAsync(tower, connection, transaction, bus);
            await transaction.CommitAsync();
        }
        await bus.PumpAsync();
        Assert.Equal(2, seen.Count);
    }

    [PgFact]
    public async Task A_control_tower_burns_one_period_of_its_race_fuel_and_goes_offline_when_the_bay_cannot_pay()
    {
        await using var rig = await Rig.CreateAsync();
        _ = await rig.Events.PumpAsync(); // establish the subscriber's StartAt.Now cursor before publishing the fuel delta
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        var owner = session.GetInt("corpid") ?? 0;

        // the Amarr Control Tower is large: 40 Helium Fuel Blocks an hour (SDE attribute 1031 = 3). The bay holds 39 of its race's block and 40 of Nitrogen,
        // the Caldari block, which does not fuel an Amarr tower
        await FuelAsync(rig, tower, HeliumFuelBlock, 39, owner);
        await FuelAsync(rig, tower, NitrogenFuelBlock, 40, owner);
        await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, tower);

        // Anchoring completes offline and does not debit fuel. An explicit online request refuses the 39-block bay.
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 1, null);
        Assert.Equal(39, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(40, await BayAsync(rig, tower, NitrogenFuelBlock));
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.ActivateAsync(session, tower, "onlineForStructures", 0, 1, CancellationToken.None));

        // One explicit Online request starts the SDE onlining delay. Startup fuel is paid only when the transition completes.
        await FuelAsync(rig, tower, HeliumFuelBlock, 2, owner);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, tower, "onlineForStructures", 0, 1, CancellationToken.None));
        Assert.Equal("onlining", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 2, rig.Now);
        Assert.Equal(41, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(ModuleResult.Done, await rig.Space.DeactivateAsync(session, tower, "onlineForStructures", CancellationToken.None));
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 1, null);
        rig.Now += OnliningMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        Assert.Equal(41, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, tower, "onlineForStructures", 0, 1, CancellationToken.None));
        Assert.Equal("onlining", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 2, rig.Now);
        _ = Events.Of(session);
        rig.Now += OnliningMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("online", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 4, null);
        Assert.Equal(1, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(40, await BayAsync(rig, tower, NitrogenFuelBlock));
        var pumped = await rig.Events.PumpAsync();
        Assert.Equal(1, pumped.Delivered);
        var fuelChanges = Events.Of(session);
        Assert.Equal(2, fuelChanges.Count(change => change.Name == "OnItemChange"));
        var changedQuantities = new List<long>();
        Assert.All(fuelChanges, change =>
        {
            Assert.Equal("OnItemChange", change.Name);
            var row = Assert.IsType<PyPackedRow>(change.Args[0]);
            changedQuantities.Add(Events.Number(row.Fields().Single(field => field.Key == "quantity").Value));
            var previous = Assert.IsType<PyDict>(change.Args[1]);
            Assert.Contains(previous.Entries, field => field.Key.Equals(new PyInt(9)) && Events.Number(field.Value) > 0);
        });
        Assert.Equal(new long[] { 0, 1 }, changedQuantities.OrderBy(quantity => quantity));

        // an online tower cannot be unanchored
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.UnanchorControlTowerAsync(session, tower));
        Assert.Contains("offline", Texts(refused.Error.Payload));
        Assert.Equal("online", await rig.Deployed.TowerStateAsync(tower));

        // the next hourly period finds the bay empty: offline again, and the unanchoring may be asked
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 1, null);
        await rig.Containers.UnanchorControlTowerAsync(session, tower);
        Assert.Equal("unanchoring", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 0, rig.Now);

        // an unanchoring tower burns no more fuel: its next period job does nothing
        await FuelAsync(rig, tower, HeliumFuelBlock, 40, owner);
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(41, await BayAsync(rig, tower, HeliumFuelBlock));
    }
}
