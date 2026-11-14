using System.Buffers.Binary;
using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Data;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class CharacterHostTests
{
    [RealAbyssalSimPgFact]
    public async Task Space_login_binds_a_ballpark_that_contains_the_ego_ship()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        try
        {
            string? endpoint = null;
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal))
                {
                    endpoint = line["eve-sim: listening on ".Length..];
                    break;
                }
            Assert.NotNull(endpoint);
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            await using var rig = await CharacterHostRig.CreateAsync(simSocket: $"tcp:{endpoint}");
            await rig.WaitForSimulationConnectionAsync();
            var account = await rig.CreateAccountAsync();

            var selection = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
            var inventory = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("invbroker"));
            var dockedCharacter = await rig.CreateCharacterAsync(accountId: account);
            var dockedShipId = await GiveItemAsync(rig, dockedCharacter, 587, rig.Station, ItemFlags.Hangar, singleton: true);
            await SetShipAsync(rig, dockedCharacter, dockedShipId);
            var dockedPackets = new ConcurrentQueue<PyValue>();
            var dockedSession = new ClientSession(new RecordingPacketSink(packet => dockedPackets.Enqueue(packet)))
            {
                SessionId = 90_003,
                ClientId = 90_004,
                UserId = account,
            };
            var dockedAttributes = await rig.Space.GetSessionAttributesAsync(dockedSession, dockedCharacter, CancellationToken.None);
            Assert.Equal(new PyInt(rig.Station), dockedAttributes.Single(attribute => attribute.Name == "stationid").Value);
            await CallAsync(selection, "SelectCharacterID", dockedSession, new PyLong(dockedCharacter));
            dockedSession.RunAfterResponse();
            Assert.Equal(dockedShipId, dockedSession.GetInt("shipid"));
            Assert.Equal(rig.Station, dockedSession.GetInt("stationid"));
            var hangarList = Py.Tuple(new PyStr("List"), Py.Tuple([]), new PyDict([]));
            var hangarBinding = Assert.IsType<PyTuple>(await CallAsync(inventory, "MachoBindObject", dockedSession,
                Py.Tuple(new PyLong(rig.Station), new PyInt(ItemFlags.Hangar)), hangarList));
            var hangarOid = BoundObjects.ExtractName(hangarBinding.Items[0]);
            Assert.NotNull(hangarOid);
            rig.Registry.RegisterBoundObjectForSession(hangarOid, inventory, dockedSession);
            dockedSession.RunAfterResponse();
            Assert.Equal(dockedShipId, dockedSession.GetInt("shipid"));

            const int systemId = 30000142;
            var shipService = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("ship"));
            var undockPacketsBefore = dockedPackets.Count;
            var undock = Assert.IsType<PyTuple>(await CallAsync(shipService, "MachoBindObject", dockedSession,
                Py.Tuple(new PyLong(rig.Station), new PyInt(15)),
                Py.Tuple(new PyStr("Undock"), Py.Tuple(new PyLong(dockedShipId), PyBool.False), new PyDict([]))));
            Assert.IsType<PySubStruct>(undock.Items[0]);
            Assert.Equal(PyNone.Instance, undock.Items[1]);
            dockedSession.RunAfterResponse();
            Assert.Null(dockedSession.GetInt("stationid"));
            Assert.Equal(systemId, dockedSession.GetInt("solarsystemid"));
            var undockPackets = dockedPackets.Skip(undockPacketsBefore).ToArray();
            Assert.Equal(
                ["OnItemsChanged", "OnMachoObjectDisconnect", "$sessionchange", "OnInvulunOnUndockingUpdated"],
                undockPackets.Select(HostPacketName));
            var undockedShip = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(dockedShipId);
            Assert.NotNull(undockedShip);
            Assert.Equal((systemId, CharacterShips.InSpaceFlag), (undockedShip.LocationId, undockedShip.Flag));
            var undockBindStart = dockedPackets.Count;
            var beyonce = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("beyonce"));
            Assert.NotSame(PyNone.Instance, await CallAsync(beyonce, "GetFormations", dockedSession));
            Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), await CallAsync(beyonce, "MachoResolveObject", dockedSession, new PyInt(systemId)));
            var undockBallparkBind = Assert.IsType<PyTuple>(await CallAsync(beyonce, "MachoBindObject", dockedSession,
                Py.Tuple(new PyInt(systemId), new PyInt(5)), PyNone.Instance));
            Assert.IsType<PySubStruct>(undockBallparkBind.Items[0]);
            Assert.Equal(PyNone.Instance, undockBallparkBind.Items[1]);
            var undockBindPackets = dockedPackets.Skip(undockBindStart).ToArray();
            var undockBindNames = undockBindPackets.Select(NotificationName).ToArray();
            var undockDilation = Array.IndexOf(undockBindNames, "OnSetTimeDilation");
            var undockRebase = Array.IndexOf(undockBindNames, "DoSimClockRebase");
            var undockDestiny = Array.IndexOf(undockBindNames, "DoDestinyUpdate");
            Assert.True(undockDilation >= 0 && undockDilation < undockRebase && undockRebase < undockDestiny,
                $"Undock ballpark clock setup precedes Destiny updates: {string.Join(" | ", undockBindNames)}");
            var undockActions = DestinyActions(undockBindPackets).ToArray();
            Assert.Equal("AddBalls2", undockActions[0].Name);
            Assert.Equal("SetState", undockActions[^1].Name);
            var undockState = Assert.IsType<PyObject>(Assert.Single(undockActions, action => action.Name == "SetState").Arguments.Items[0]);
            Assert.Equal(dockedShipId, IntegerValue(Field(undockState, "ego")));
            var undockStateBuffer = Assert.IsType<PyBuffer>(Field(undockState, "state")).Value.ToArray();
            var undockShipIdBytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(undockShipIdBytes, dockedShipId);
            Assert.True(undockStateBuffer.AsSpan().IndexOf(undockShipIdBytes) >= 0,
                "The undock SetState contains the ship that just left the station.");

            var character = await rig.CreateCharacterAsync(accountId: account);
            var shipId = await GiveItemAsync(rig, character, 587, systemId, CharacterShips.InSpaceFlag, singleton: true);
            await SetShipAsync(rig, character, shipId);
            var pilot = await rig.Characters.GetAsync(character) ?? throw new InvalidOperationException("Character was not persisted.");
            var shipType = await new ItemTypeCatalog(new SdeReader(rig.Source)).GetAsync(587)
                ?? throw new InvalidOperationException("The SDE has no type 587.");
            var shipRadius = (await new SdeReader(rig.Source).ReadAsync("types", [587])).Single().Doc
                .GetProperty("radius").GetDouble();
            await new PgSpaceItemStore(rig.Source, rig.SpaceSchema).SaveAsync(new SpaceItem(
                shipId,
                systemId,
                [100_000, 200_000, 300_000],
                [0, 0, 1],
                shipRadius,
                shipType.GroupId,
                0,
                character,
                pilot.CorporationId,
                pilot.AllianceId ?? 0,
                0,
                rig.NowUnixMs + 3_600_000));

            var packets = new ConcurrentQueue<PyValue>();
            var session = new ClientSession(new RecordingPacketSink(packet => packets.Enqueue(packet)))
            {
                SessionId = 90_001,
                ClientId = 90_002,
                UserId = account,
            };
            Assert.Equal(shipId, pilot.ShipId);
            Assert.Equal(systemId, pilot.SolarSystemId);
            var persistedShip = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(shipId);
            Assert.NotNull(persistedShip);
            Assert.Equal((systemId, CharacterShips.InSpaceFlag), (persistedShip.LocationId, persistedShip.Flag));
            var savedPose = await new PgSpaceItemStore(rig.Source, rig.SpaceSchema).GetAsync(shipId);
            Assert.NotNull(savedPose);
            Assert.Equal(systemId, savedPose.SolarSystemId);
            Assert.True(savedPose.ExpiresAtMs > rig.NowUnixMs);
            Assert.Equal(3, savedPose.Position.Count);
            Assert.Equal(3, savedPose.Direction.Count);
            var entryAttributes = await rig.Space.GetSessionAttributesAsync(session, character, CancellationToken.None);
            Assert.Equal(new PyInt(systemId), entryAttributes.Single(attribute => attribute.Name == "solarsystemid").Value);
            await CallAsync(selection, "SelectCharacterID", session, new PyLong(character));
            session.RunAfterResponse();
            Assert.Equal(systemId, session.GetInt("solarsystemid"));
            Assert.Null(session.GetInt("stationid"));
            var sessionChangeIndex = Array.FindIndex(packets.ToArray(), packet =>
                PacketType(packet) == MachoMessage.SessionChangeNotification);
            Assert.True(sessionChangeIndex >= 0, "Character selection sends its space session change before ballpark setup.");

            // The active-ship inventory request precedes Michelle's ballpark bind, so it must not emit ballpark bootstrap notifications.
            var inventoryList = Py.Tuple(new PyStr("List"), Py.Tuple([]), new PyDict([]));
            var beforeInventoryCount = packets.Count;
            Assert.True(sessionChangeIndex < beforeInventoryCount,
                "The space session change is sent before the active-ship inventory request can race ballpark binding.");
            await CallAsync(inventory, "MachoBindObject", session,
                Py.Tuple(new PyLong(shipId)), inventoryList);
            session.RunAfterResponse();
            var afterInventory = packets.Skip(beforeInventoryCount).ToArray();
            Assert.DoesNotContain(afterInventory, packet => NotificationName(packet) is "OnSetTimeDilation" or "DoSimClockRebase" or "DoDestinyUpdate");
            Assert.DoesNotContain(DestinyActions(afterInventory), action => action.Name == "AddBalls2");
            var beforeBindCount = packets.Count;
            var entryBeyonce = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("beyonce"));
            Assert.NotSame(PyNone.Instance, await CallAsync(entryBeyonce, "GetFormations", session));
            Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), await CallAsync(entryBeyonce, "MachoResolveObject", session, new PyInt(systemId)));
            var bind = Assert.IsType<PyTuple>(await CallAsync(entryBeyonce, "MachoBindObject", session,
                Py.Tuple(new PyInt(systemId), new PyInt(5)), PyNone.Instance));
            Assert.IsType<PySubStruct>(bind.Items[0]);
            var afterBindPackets = packets.Skip(beforeBindCount).ToArray();
            Assert.Contains(DestinyActions(afterBindPackets), action => action.Name == "SetState");
            var afterBindActions = DestinyActions(afterBindPackets).ToArray();
            var setState = Assert.Single(afterBindActions, action => action.Name == "SetState");
            var stateBag = Assert.IsType<PyObject>(Assert.Single(setState.Arguments.Items));
            Assert.Equal(shipId, IntegerValue(Field(stateBag, "ego")));
            var stateBuffer = Assert.IsType<PyBuffer>(Field(stateBag, "state")).Value.ToArray();
            Assert.Equal(0, stateBuffer[0]);
            var shipIdBytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(shipIdBytes, shipId);
            Assert.True(stateBuffer.AsSpan().IndexOf(shipIdBytes) >= 0, "SetState's full ball state contains the ego ship's Destiny ball.");
            Assert.Contains(Assert.IsType<PyList>(Field(stateBag, "slims")).Items,
                slim => IntegerValue(Field(slim, "itemID")) == shipId);
            Assert.DoesNotContain(afterBindActions, action => action.Name == "AddBalls2");
            var notificationNames = afterBindPackets.Select(NotificationName).ToArray();
            var dilation = Array.IndexOf(notificationNames, "OnSetTimeDilation");
            var rebase = Array.IndexOf(notificationNames, "DoSimClockRebase");
            var firstDestiny = Array.IndexOf(notificationNames, "DoDestinyUpdate");
            Assert.True(dilation >= 0 && dilation < rebase && rebase < firstDestiny,
                $"Clock setup precedes the bound SetState: {string.Join(" | ", notificationNames)}");
            Assert.Equal("SetState", afterBindActions.First().Name);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private static IEnumerable<(string Name, PyTuple Arguments)> DestinyActions(IEnumerable<PyValue> packets)
    {
        foreach (var packet in packets)
        {
            if (NotificationName(packet) != "DoDestinyUpdate")
                continue;
            var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
            var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(state[4]).Items[0]).Items[1]);
            var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray()));
            var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(notification.Items[1]).Items[1]);
            var actions = Assert.IsType<PyList>(args.Items[0]);
            foreach (var action in actions.Items)
            {
                var pair = Assert.IsType<PyTuple>(action);
                var payload = Assert.IsType<PyTuple>(pair.Items[1]);
                var name = payload.Items[0] switch
                {
                    PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
                    PyStr text => text.Value,
                    _ => throw new Xunit.Sdk.XunitException($"Unexpected Destiny action name type {payload.Items[0].GetType().Name}."),
                };
                yield return (name, Assert.IsType<PyTuple>(payload.Items[1]));
            }
        }
    }

    private static long IntegerValue(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an integer, got {value.GetType().Name}."),
    };

    private static long? PacketType(PyValue packet) => packet is PyObject { Args: PyTuple state } &&
        state.Items.FirstOrDefault() is PyInt type
        ? type.Value
        : null;

    private static string? HostPacketName(PyValue packet) => PacketType(packet) == MachoMessage.SessionChangeNotification
        ? "$sessionchange"
        : NotificationName(packet);

    private static string? Text(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        _ => null,
    };
}
