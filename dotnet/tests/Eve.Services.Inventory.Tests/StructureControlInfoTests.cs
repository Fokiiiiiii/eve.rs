using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Ships;

namespace Eve.Services.Inventory.Tests;

public sealed class StructureControlInfoTests
{
    [PgFact]
    public async Task Controlled_structure_item_list_and_capacity_reads_are_owner_scoped_and_the_binding_cannot_mutate_inventory()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        const long structureId = 8_300_001;
        const int astrahus = 35_832, moduleType = 35_925, system = 30_000_142;
        var moduleId = await rig.AddAsync(moduleType, Rig.Corporation, structureId, flag: 27, singleton: true);
        var cargoId = await rig.AddAsync(Rig.Tritanium, Rig.Corporation, structureId, flag: CharacterShips.CargoFlag, quantity: 20);
        var foreignId = await rig.AddAsync(moduleType, Rig.Character, structureId, flag: 28, singleton: true);
        await rig.Statics.EnsureTypesAsync([astrahus, moduleType, Rig.Tritanium]);
        var root = new Item(structureId, astrahus, Rig.Corporation, system, 0, 1, true, "Controlled Astrahus");
        var view = new ControlledStructureInventory(root, await rig.Items.InLocationAsync(structureId, Rig.Corporation));
        var callbackCalls = 0;
        var allowed = true;
        var service = new InvBrokerService(rig.Items, new ItemTypeCatalog(new SdeReader(rig.Source)), Rig.Stations,
            rig.Ships, rig.Statics, () => Rig.ClockMs,
            controlledStructureInventory: (session, id, token) =>
            {
                callbackCalls++;
                return ValueTask.FromResult<ControlledStructureInventory?>(session.SessionId == 11 && allowed ? view : null);
            });
        var session = Rig.Session(station: null, id: 11);
        session.Set("shipid", new PyLong(structureId));
        session.Set("structureid", new PyLong(structureId));
        session.Set("solarsystemid", new PyInt(system));
        var oid = Rig.OidOf(await Call(service, session, "GetInventoryFromId", null, new PyLong(structureId)));
        Assert.Equal("util.Row", JsSemantics.StringValue(Assert.IsType<PyObject>(await Call(service, session, "GetItem", oid)).Name));
        var self = Assert.IsType<PyPackedRow>(await Call(service, session, "GetSelfInvItem", oid));
        Assert.Equal((structureId, (long)astrahus, Rig.Corporation, (long)system),
            (Id(self.Values[0]), Id(self.Values[1]), Id(self.Values[2]), Id(self.Values[3])));
        var list = Assert.IsType<PyList>(await Call(service, session, "List", oid, PyNone.Instance));
        Assert.Equal(new[] { moduleId, cargoId }, list.Items.Select(item => Id(Assert.IsType<PyPackedRow>(item).Values[0])));
        Assert.DoesNotContain(list.Items, item => Id(((PyPackedRow)item).Values[0]) == foreignId);
        var capacity = await Call(service, session, "GetCapacity", oid, new PyInt(CharacterShips.CargoFlag));
        Assert.Equal(1_000_000d, Number(Field(capacity, "capacity")));
        var volume = rig.Statics.Type(Rig.Tritanium)!.Attributes[rig.Statics.AttributeId("volume")!.Value];
        Assert.Equal(volume * 20, Number(Field(capacity, "used")), 9);
        await Assert.ThrowsAsync<MachoWrappedException>(() => Call(service, session, "Add", oid, new PyLong(cargoId), new PyLong(structureId)));
        await Assert.ThrowsAsync<MachoWrappedException>(() => Call(service, session, "StackAll", oid));
        await Assert.ThrowsAsync<MachoWrappedException>(() => Call(service, session, "MultiAdd", oid, new PyList([new PyLong(cargoId)]), new PyLong(structureId)));
        await Assert.ThrowsAsync<MachoWrappedException>(() => Call(service, session, "FitFitting", oid));
        Assert.Equal((structureId, 20L), ((await rig.Items.GetAsync(cargoId))!.LocationId, (await rig.Items.GetAsync(cargoId))!.Quantity));
        var stranger = Rig.Session(station: null, id: 12);
        stranger.Set("shipid", new PyLong(structureId));
        stranger.Set("structureid", new PyLong(structureId));
        Assert.Equal(PyNone.Instance, await Call(service, stranger, "GetInventoryFromId", null, new PyLong(structureId)));
        Assert.Equal(PyNone.Instance, await Call(service, stranger, "GetItem", oid));
        var personal = Assert.IsType<ShipSnapshot>(await rig.Ships.LoadAsync(Rig.Character));
        var normal = Rig.Session();
        normal.Set("shipid", new PyLong(personal.Ship.Id));
        var before = callbackCalls;
        var personalOid = Rig.OidOf(await Call(service, normal, "GetInventoryFromId", null, new PyLong(personal.Ship.Id)));
        var personalRow = Assert.IsType<PyPackedRow>(await Call(service, normal, "GetSelfInvItem", personalOid));
        Assert.Equal(personal.Ship.Id, Id(personalRow.Values[0]));
        Assert.Equal(before, callbackCalls);
        var docked = Rig.Session(station: structureId, id: 11);
        docked.Set("structureid", new PyLong(structureId));
        docked.Set("shipid", new PyLong(personal.Ship.Id));
        var retained = Rig.OidOf(await Call(service, docked, "GetInventory", null, new PyInt(InvBrokerService.ContainerHangar)));
        docked.Set("shipid", new PyLong(structureId));
        var retainedList = Assert.IsType<PyList>(await Call(service, docked, "List", retained, PyNone.Instance));
        Assert.Equal(new[] { moduleId, cargoId }, retainedList.Items.Select(item => Id(((PyPackedRow)item).Values[0])));
        allowed = false;
        Assert.Equal(PyNone.Instance, await Call(service, session, "GetSelfInvItem", oid));
    }

    private static async Task<PyValue> Call(InvBrokerService service, ClientSession session, string method, string? oid, params PyValue[] args) =>
        await ((IService)service).CallAsync(new ServiceCall { Method = method, Session = session, BoundObjectId = oid, Args = args });
    private static PyValue Field(PyValue value, string name) =>
        ((PyDict)((PyObject)value).Args).Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
    private static long Id(PyValue value) => value switch { PyInt item => item.Value, PyLong item => item.Value, _ => throw new InvalidOperationException() };
    private static double Number(PyValue value) => value switch { PyInt item => item.Value, PyLong item => item.Value, PyReal item => item.Value, _ => throw new InvalidOperationException() };
}
