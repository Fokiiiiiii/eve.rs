using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Space;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class CharacterHostTests
{
    [PgFact]
    public async Task Registered_ship_FitShips_checks_station_service_and_persists_hull_and_notifications()
    {
        await using var simulation = IdleSimulationLink.Start();
        await using var rig = await CharacterHostRig.CreateAsync(simSocket: simulation.Endpoint);
        await simulation.Connected;
        await rig.WaitForSimulationConnectionAsync();
        var character = await rig.CreateCharacterAsync();
        var activeShip = await GiveItemAsync(rig, character, CharacterShips.CapsuleTypeId,
            rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, activeShip);
        var packageId = await PutItemAsync(rig, character, 601, rig.Station, ItemFlags.Hangar,
            quantity: 2, singleton: false);

        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("charid", new PyLong(character));
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(activeShip));
        var ship = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("ship"));
        var kwargs = new PyDict([
            new(new PyStr("shipTypeID"), new PyInt(601)),
            new(new PyStr("fitInfo"), Py.KeyVal(("modulesByFlag", new PyDict([])))),
            new(new PyStr("itemLocationID"), new PyLong(rig.Station)),
            new(new PyStr("cargoItemsByType"), new PyDict([])),
            new(new PyStr("fitRigs"), PyBool.True),
            new(new PyStr("name"), new PyStr("Host FitShips Ibis")),
            new(new PyStr("numToFit"), new PyInt(1)),
            new(new PyStr("itemContainerID"), new PyLong(0)),
        ]);

        var response = Assert.IsType<PyTuple>(await CallAsync(ship, "MachoBindObject", session,
            Py.Tuple(new PyLong(rig.Station), new PyInt(15)),
            Py.Tuple(new PyStr("FitShips"), Py.Tuple([]), kwargs)));

        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Same(PyNone.Instance, response.Items[1]);
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var packageAfter = Assert.IsType<Eve.Inventory.Item>(await items.GetAsync(packageId));
        Assert.Equal((1L, false), (packageAfter.Quantity, packageAfter.Singleton));
        var assembled = Assert.Single(await items.InLocationAsync(rig.Station, character),
            item => item.TypeId == 601 && item.Id != packageId);
        Assert.True(assembled.Singleton);
        Assert.Equal("Host FitShips Ibis", assembled.CustomName);
        Assert.Equal(
            ["OnItemChange", "OnItemChange", "OnItemChange"],
            Notifications(sink).Select(notification => notification.Name));
    }

    private sealed class IdleSimulationLink : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serve;

        private IdleSimulationLink()
        {
            _listener.Start();
            _serve = ServeAsync();
        }

        public string Endpoint => $"tcp:127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public Task Connected => _connected.Task;

        public static IdleSimulationLink Start() => new();

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var length = new byte[sizeof(uint)];
                await stream.ReadExactlyAsync(length, _stop.Token);
                var payload = new byte[BinaryPrimitives.ReadUInt32LittleEndian(length)];
                await stream.ReadExactlyAsync(payload, _stop.Token);
                Assert.IsType<SimHello>(SimCodec.Decode(payload));
                await stream.WriteAsync(SimCodec.Encode(new SimHelloAck(SimCodec.ProtocolVersion, 30)), _stop.Token);
                _connected.TrySetResult();

                while (!_stop.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(length, _stop.Token);
                    var command = new byte[BinaryPrimitives.ReadUInt32LittleEndian(length)];
                    await stream.ReadExactlyAsync(command, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (IOException) when (_connected.Task.IsCompleted)
            {
            }
            catch (Exception error)
            {
                _connected.TrySetException(error);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serve;
            _stop.Dispose();
        }
    }
}
