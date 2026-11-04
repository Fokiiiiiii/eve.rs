using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;

namespace Eve.Services.Dogma.Tests;

public sealed class StructureControlInfoTests
{
    [PgFact]
    public async Task GetAllInfo_uses_the_controlled_structure_identity_and_denies_a_missing_projection_while_personal_ship_reads_stay_unchanged()
    {
        await using var rig = await Rig.CreateAsync();
        var made = await rig.NewCharacterAsync();
        var personal = Assert.IsType<ShipSnapshot>(await rig.Ships.LoadAsync(made.CharacterId));
        const long structureId = 8_300_001, corporation = 1_000_001;
        const int system = 30_000_142, astrahus = 35_832;
        await rig.Statics.EnsureTypesAsync([astrahus]);
        var ship = new Item(structureId, astrahus, corporation, system, 0, 1, true, "Controlled Astrahus");
        var world = new DogmaWorld(rig.Statics, made.CharacterId, structureId, structureId);
        world.Add(ItemDogma.Of(ship, made.CharacterId, system, 0));
        world.Add(new DogmaItem(made.CharacterId, personal.Character.TypeId, made.CharacterId, structureId, ShipSnapshot.PilotFlag, ItemState.Online));
        foreach (var attribute in ShipSnapshot.LearningAttributes)
            world.SetValue(made.CharacterId, attribute, personal.World.Get(made.CharacterId, attribute));
        foreach (var skill in personal.Skills)
            if (personal.World.Item(ShipSnapshot.SkillItemId(made.CharacterId, skill.TypeId)) is { } item)
                world.Add(item);
        var projected = new ShipSnapshot
        {
            Character = personal.Character, Ship = ship, Fitted = [], Skills = personal.Skills, World = world,
        };
        var allowed = true;
        var callbackCalls = 0;
        var service = new DogmaIMService(rig.Statics, rig.Ships, () => Rig.ClockMs,
            (character, item, effect, station, activeShip, token) => ValueTask.FromResult<bool?>(null),
            space: rig.Targeting,
            controlledStructureSnapshot: (session, snapshot, token) =>
            {
                callbackCalls++;
                Assert.Equal(personal.Ship.Id, snapshot.Ship.Id);
                return ValueTask.FromResult<ShipSnapshot?>(allowed ? projected : null);
            });
        var session = Rig.Session(made.CharacterId, station: null);
        session.Set("solarsystemid", new PyInt(system));
        session.Set("shipid", new PyLong(structureId));
        session.Set("structureid", new PyLong(structureId));
        var location = session.DockedLocationId ?? system;
        var bind = Assert.IsType<PyTuple>(await Call(service, session, "MachoBindObject", null,
            Py.Tuple(new PyLong(location), new PyInt(5)), PyNone.Instance));
        var oid = Oid(bind.Items[0]);
        var answer = await Call(service, session, "GetAllInfo", oid);
        Assert.Equal(structureId, Id(Field(answer, "activeShipID")));
        var shipInfo = Assert.IsType<PyDict>(Field(answer, "shipInfo"));
        var structure = shipInfo.Entries.Single(entry => Id(entry.Key) == structureId).Value;
        var row = Assert.IsType<PyPackedRow>(Field(structure, "invItem"));
        Assert.Equal(astrahus, Id(row.Values[1]));
        Assert.Equal(corporation, Id(row.Values[2]));
        Assert.Equal(system, Id(row.Values[3]));
        Assert.DoesNotContain(shipInfo.Entries, entry => Id(entry.Key) == personal.Ship.Id);
        Assert.Contains(Assert.IsType<PyDict>(Field(answer, "structureInfo")).Entries, entry => Id(entry.Key) == structureId);
        Assert.Equal(Math.Max(0L, (long)world.Get(structureId, BuiltInEffects.TurretSlotsLeft)),
            Id(await Call(service, session, "GetAvailableTurretSlots", oid)));
        allowed = false;
        Assert.Equal(PyNone.Instance, await Call(service, session, "GetAllInfo", oid));
        var before = callbackCalls;
        var normal = Rig.Session(made.CharacterId);
        normal.Set("shipid", new PyLong(personal.Ship.Id));
        var normalBind = Assert.IsType<PyTuple>(await Call(service, normal, "MachoBindObject", null,
            Py.Tuple(new PyLong(normal.DockedLocationId!.Value), new PyInt(15)), PyNone.Instance));
        Assert.Equal(personal.Ship.Id, Id(Field(await Call(service, normal, "GetAllInfo", Oid(normalBind.Items[0])), "activeShipID")));
        Assert.Equal(before, callbackCalls);
    }

    private static async Task<PyValue> Call(DogmaIMService service, ClientSession session, string method, string? oid, params PyValue[] args) =>
        await ((IService)service).CallAsync(new ServiceCall { Method = method, Session = session, BoundObjectId = oid, Args = args });

    private static string Oid(PyValue value) =>
        JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)value).Value).Decoded!).Items[0])!;
    private static PyValue Field(PyValue value, string name) =>
        ((PyDict)((PyObject)value).Args).Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
    private static long Id(PyValue value) => value switch { PyInt item => item.Value, PyLong item => item.Value, _ => throw new InvalidOperationException() };
}
