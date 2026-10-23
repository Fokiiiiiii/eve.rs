using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships.Tests;

public class CargoCapacityPersistenceTests
{
    [PgFact]
    public async Task Successful_capacity_module_offline_notifies_in_order_and_survives_a_ship_reload()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        const int expandedCargoholdI = 1319;
        await rig.Statics.EnsureTypesAsync([expandedCargoholdI]);
        var moduleType = Assert.IsType<TypeDef>(rig.Statics.Type(expandedCargoholdI));
        foreach (var (skill, level) in FittingRules.RequiredSkills(moduleType))
            await rig.SetSkillAsync(character.CharacterId, skill, level);

        var module = await rig.GiveAsync(character.CharacterId, expandedCargoholdI);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, module, Rig.Station, character.ShipId, null, null));
        Assert.IsType<ShipChanged>(await rig.Ships.SetOnlineAsync(
            character.CharacterId, character.ShipId, module, online: true));

        var capacityAttribute = Assert.IsType<int>(rig.Statics.AttributeId("capacity"));
        var before = (await rig.Ships.LoadAsync(character.CharacterId))!;
        var capacityBefore = before.World.Get(character.ShipId, capacityAttribute);
        before.World.SetState(module, ItemState.Offline);
        var capacityAfter = before.World.Get(character.ShipId, capacityAttribute);
        Assert.True(capacityAfter < capacityBefore);

        var offline = Assert.IsType<ShipChanged>(await rig.Ships.SetOnlineAsync(
            character.CharacterId, character.ShipId, module, online: false));
        var capacityEventIndex = offline.Events.Select((value, index) => (value, index))
            .Single(entry => entry.value is AttributeChanged change
                && change.ItemId == character.ShipId && change.AttributeId == capacityAttribute).index;
        var effectEventIndex = offline.Events.Select((value, index) => (value, index))
            .Single(entry => entry.value is EffectChanged change
                && change.ItemId == module && !change.Started).index;
        Assert.True(capacityEventIndex < effectEventIndex);
        var capacityEvent = Assert.IsType<AttributeChanged>(offline.Events[capacityEventIndex]);
        Assert.Equal((capacityBefore, capacityAfter), (capacityEvent.Old, capacityEvent.New));

        var restartedShips = new CharacterShips(
            rig.Source, rig.Statics, rig.Characters, rig.Items, rig.Skills, rig.Modules);
        var reloaded = (await restartedShips.LoadAsync(character.CharacterId))!;
        Assert.Equal(ItemState.Offline, reloaded.World.Item(module)!.State);
        Assert.Equal(capacityAfter, reloaded.World.Get(character.ShipId, capacityAttribute));
    }
}
