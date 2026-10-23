using System.Globalization;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;

namespace Eve.Services.Inventory.Tests;

public class CargoCapacityInventoryTests
{
    [PgFact]
    public async Task MultiAdd_refuses_a_batch_unfit_before_moving_any_capacity_module()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        const int expandedCargoholdI = 1319;
        await rig.Statics.EnsureTypesAsync([expandedCargoholdI, Rig.Tritanium]);
        var moduleType = Assert.IsType<TypeDef>(rig.Statics.Type(expandedCargoholdI));
        await rig.Skills.InTransactionAsync(async (connection, transaction, token) =>
        {
            var skills = FittingRules.RequiredSkills(moduleType)
                .Select(skill => new Eve.Skills.CharacterSkill(skill.Skill, skill.Level, 0))
                .ToArray();
            await rig.Skills.GrantAsync(connection, transaction, Rig.Character, skills, token);
            return true;
        });

        var shipId = (await rig.Items.OwnedByAsync(Rig.Character))
            .Single(item => item.LocationId == Rig.Station && item.TypeId == Rig.Cockroach).Id;
        var modules = new long[2];
        for (var index = 0; index < modules.Length; index++)
        {
            modules[index] = await rig.AddAsync(expandedCargoholdI, Rig.Character, Rig.Station);
            Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(
                Rig.Character, modules[index], Rig.Station, shipId, null, null));
            Assert.IsType<ShipChanged>(await rig.Ships.SetOnlineAsync(
                Rig.Character, shipId, modules[index], online: true));
        }

        var capacityAttribute = Assert.IsType<int>(rig.Statics.AttributeId("capacity"));
        var before = (await rig.Ships.LoadAsync(Rig.Character))!;
        var capacityBefore = before.World.Get(shipId, capacityAttribute);
        before.World.SetState(modules[1], ItemState.Offline);
        var capacityAfterOne = before.World.Get(shipId, capacityAttribute);
        before.World.SetState(modules[0], ItemState.Offline);
        var capacityAfterBoth = before.World.Get(shipId, capacityAttribute);
        Assert.True(capacityAfterBoth < capacityAfterOne && capacityAfterOne < capacityBefore);

        var volumeAttribute = Assert.IsType<int>(rig.Statics.AttributeId("volume"));
        var itemVolume = Assert.IsType<TypeDef>(rig.Statics.Type(Rig.Tritanium)).Attributes[volumeAttribute];
        var quantity = (long)Math.Floor((capacityAfterBoth + capacityAfterOne) / (2 * itemVolume)) + 1;
        var cargo = await rig.AddAsync(
            Rig.Tritanium, Rig.Character, shipId, CharacterShips.CargoFlag, quantity, singleton: false);
        var cargoVolume = quantity * itemVolume;
        Assert.True(cargoVolume > capacityAfterBoth && cargoVolume <= capacityAfterOne);

        var session = Rig.Session();
        session.Set("shipid", new PyLong(shipId));
        var hangar = await rig.HangarAsync(session);
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await ((IService)rig.Service).CallAsync(new ServiceCall
            {
                Method = "MultiAdd",
                BoundObjectId = hangar,
                Session = session,
                Args = [new PyList([new PyLong(modules[0]), new PyLong(modules[1])]), new PyLong(shipId)],
            }));

        var errorArgs = ((PyTuple)((PyTuple)((PyObjectEx)refusal.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", JsSemantics.StringValue(errorArgs[0]));
        var notify = (PyDict)errorArgs[1];
        Assert.Equal(
            $"That cannot be removed while the cargo hold is this full. The hold carries {cargoVolume.ToString("F2", CultureInfo.InvariantCulture)} m3 and the ship would be left with room for {capacityAfterBoth.ToString("F2", CultureInfo.InvariantCulture)} m3. Move some cargo out first.",
            JsSemantics.StringValue(notify.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == "notify").Value));

        foreach (var module in modules)
        {
            var item = await rig.Items.GetAsync(module);
            Assert.Equal((shipId, ItemState.Online),
                (item!.LocationId, (await rig.Modules.StatesAsync([module]))[module]));
        }
        var storedCargo = (await rig.Items.GetAsync(cargo))!;
        Assert.Equal((shipId, CharacterShips.CargoFlag), (storedCargo.LocationId, storedCargo.Flag));
    }
}
