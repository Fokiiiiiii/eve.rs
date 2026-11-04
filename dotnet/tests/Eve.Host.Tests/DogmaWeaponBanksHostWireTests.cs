using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Dogma;
using Eve.Ships;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Weapon_bank_rpcs_are_registered_and_return_the_shapes_the_client_consumes()
    {
        const long characterId = 42;
        const long shipId = 9988400002200;
        const long masterId = 9988400002220;
        const long slaveId = 9988400002221;
        const long secondMasterId = 9988400002222;
        const long secondSlaveId = 9988400002223;
        const long freeModuleId = 9988400002224;
        const long stationId = 600;
        var store = new RecordingWeaponBankStore();
        const int weaponTypeId = 900001;
        const int shipTypeId = 900002;
        var statics = new DogmaStatic([], [],
        [
            new TypeDef(weaponTypeId, 53, 7, new Dictionary<int, double>(), [], "Fixture weapon"),
            new TypeDef(shipTypeId, 25, 6, new Dictionary<int, double>(), [], "Fixture ship"),
        ]);
        var character = new Character(characterId, 1, "Pilot", 0, 1, 1, 1, 1, 1373, 1, null, null,
            stationId, stationId, 30000142, 20000002, 10000002, shipId, DateTimeOffset.UnixEpoch, null, null);
        ShipSnapshot Snapshot()
        {
            var ship = new Item(shipId, shipTypeId, characterId, stationId, ItemFlags.Hangar, 1, true, null);
            var master = new Item(masterId, weaponTypeId, characterId, shipId, 27, 1, true, null);
            var slave = new Item(slaveId, weaponTypeId, characterId, shipId, 28, 1, true, null);
            var secondMaster = new Item(secondMasterId, weaponTypeId, characterId, shipId, 29, 1, true, null);
            var secondSlave = new Item(secondSlaveId, weaponTypeId, characterId, shipId, 30, 1, true, null);
            var freeModule = new Item(freeModuleId, weaponTypeId, characterId, shipId, 31, 1, true, null);
            var world = new DogmaWorld(statics, characterId, shipId);
            world.Add(new DogmaItem(shipId, shipTypeId, characterId, stationId));
            world.Add(new DogmaItem(masterId, weaponTypeId, characterId, shipId, 27, ItemState.Online));
            world.Add(new DogmaItem(slaveId, weaponTypeId, characterId, shipId, 28, ItemState.Online));
            world.Add(new DogmaItem(secondMasterId, weaponTypeId, characterId, shipId, 29, ItemState.Online));
            world.Add(new DogmaItem(secondSlaveId, weaponTypeId, characterId, shipId, 30, ItemState.Online));
            world.Add(new DogmaItem(freeModuleId, weaponTypeId, characterId, shipId, 31, ItemState.Online));
            return new ShipSnapshot
            {
                Character = character, Ship = ship, Fitted = [master, slave, secondMaster, secondSlave, freeModule], Skills = [], World = world,
                WeaponBanks = store.Banks,
            };
        }

        var service = new DogmaIMService(statics, null!, () => FixedNowMs, null!, snapshotProvider: (_, _) => ValueTask.FromResult<ShipSnapshot?>(Snapshot()), weaponBankStore: store);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(characterId));
                session.Set("stationid", new PyInt(stationId));
                session.Set("shipid", new PyLong(shipId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "dogmaIM", "MachoBindObject",
            new PyTuple([
                new PyTuple([new PyLong(stationId), new PyInt(15)]),
                new PyTuple([new PyStr("GetRequiredSkillLevels"), new PyTuple([new PyInt(weaponTypeId)]), PyNone.Instance]),
            ]),
            MachoVersion()))));
        var bind = Assert.IsType<Send>(await front.ReceiveAsync());
        var bound = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bind.Payload))).Items[0];
        var boundTuple = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(Assert.IsType<PySubStruct>(bound).Value).Decoded);
        var oid = System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundTuple.Items[0]).Value.Span);

        async Task<PyValue> Call(uint id, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(open, id, oid, method, new PyTuple(args), MachoVersion()))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Envelope(reply).Type);
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        var linked = Assert.IsType<PyDict>(await Call(2, "LinkWeapons", new PyLong(shipId), new PyLong(masterId), new PyLong(slaveId), PyBool.True));
        var bank = Assert.Single(linked.Entries);
        Assert.Equal(new PyLong(masterId), bank.Key);
        Assert.Equal(new PyList([new PyLong(slaveId)]), bank.Value);
        var allInfo = Assert.IsType<PyObject>(new DogmaInfo(statics).GetAllInfo(Snapshot(), false, true, false, FixedNowMs, docked: false));
        var info = Assert.IsType<PyDict>(allInfo.Args);
        var shipState = Assert.IsType<PyTuple>(info.Entries.Single(entry => entry.Key == new PyStr("shipState")).Value);
        var stateBanks = Assert.IsType<PyDict>(shipState.Items[2]);
        Assert.Equal(linked, stateBanks);
        Assert.Equal(new PyLong(slaveId), await Call(3, "UnlinkModule", new PyLong(shipId), new PyLong(masterId)));
        Assert.Empty(store.Banks);

        _ = await Call(4, "LinkWeapons", new PyLong(shipId), new PyLong(masterId), new PyLong(slaveId), PyBool.True);
        _ = await Call(5, "LinkWeapons", new PyLong(shipId), new PyLong(secondMasterId), new PyLong(secondSlaveId), PyBool.True);
        var merged = Assert.IsType<PyDict>(await Call(6, "MergeModuleGroups", new PyLong(shipId), new PyLong(masterId), new PyLong(secondMasterId)));
        Assert.Equal([masterId], merged.Entries.Select(entry => ((PyLong)entry.Key).Value));
        Assert.Equal(3, Assert.IsType<PyList>(Assert.Single(merged.Entries).Value).Items.Count);

        var peeledMap = Assert.IsType<PyDict>(await Call(7, "PeelAndLink", new PyLong(shipId), new PyLong(freeModuleId), new PyLong(masterId)));
        Assert.Contains(peeledMap.Entries, entry => entry.Key == new PyLong(freeModuleId) && Assert.IsType<PyList>(entry.Value).Items.Single() == new PyLong(slaveId));
        Assert.Equal(new PyLong(slaveId), await Call(8, "UnlinkModule", new PyLong(shipId), new PyLong(freeModuleId)));

        var allLinked = Assert.IsType<PyDict>(await Call(9, "LinkAllWeapons", new PyLong(shipId)));
        Assert.Equal([masterId], allLinked.Entries.Select(entry => ((PyLong)entry.Key).Value));
        Assert.Equal(4, Assert.IsType<PyList>(Assert.Single(allLinked.Entries).Value).Items.Count);
        Assert.Empty(Assert.IsType<PyDict>(await Call(10, "UnlinkAllModules", new PyLong(shipId))).Entries);
        Assert.Empty(store.Banks);

        _ = await Call(11, "LinkAllWeapons", new PyLong(shipId));
        Assert.Equal(PyNone.Instance, await Call(12, "DestroyWeaponBank", new PyLong(shipId), new PyLong(masterId)));
        Assert.Empty(store.Banks);
    }

    private sealed class RecordingWeaponBankStore : IWeaponBankStore
    {
        public IReadOnlyDictionary<long, IReadOnlyList<long>> Banks { get; private set; } = new Dictionary<long, IReadOnlyList<long>>();

        public Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> WeaponBanksAsync(long shipId, CancellationToken cancellationToken = default) => Task.FromResult(Banks);

        public Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> UpdateWeaponBanksAsync(
            long shipId,
            Func<IReadOnlyDictionary<long, IReadOnlyList<long>>, IReadOnlyDictionary<long, IReadOnlyList<long>>> update,
            CancellationToken cancellationToken = default)
        {
            Banks = update(Banks);
            return Task.FromResult(Banks);
        }
    }
}
