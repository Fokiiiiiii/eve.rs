using Eve.Inventory;

namespace Eve.Ships.Tests;

public class IceMiningHoldTests
{
    [PgFact]
    public async Task Ice_yield_is_persisted_in_the_general_mining_hold_when_the_hull_has_no_ice_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var endurance = Assert.Single(types, type => type.Published && type.Name == "Endurance").TypeId;
        var harvester = Assert.Single(types, type => type.Published && type.Name == "Ice Mining Laser I").TypeId;
        var ice = Assert.Single(types, type => type.Published && type.Name == "Clear Icicle").TypeId;
        await rig.Statics.EnsureTypesAsync([endurance, harvester, ice]);
        var character = await rig.NewCharacterAsync(shipTypeId: endurance);
        await rig.Ships.UndockAsync(character.CharacterId);
        var system = (int)(await rig.Items.GetAsync(character.ShipId))!.LocationId;
        var module = await rig.GiveAsync(character.CharacterId, harvester, singleton: true, quantity: 1,
            location: character.ShipId, flag: ItemFlags.HiSlot0);
        var schema = $"sp_{Guid.NewGuid():N}"[..14];
        rig.KeepSchemas(schema);
        await PgSpaceItemStore.MigrateAsync(rig.Source, schema);
        var spaceItems = new PgSpaceItemStore(rig.Source, schema);
        var ledger = new PgWorkCycleLedger(rig.Source, schema);
        var rock = (await rig.Ships.CreateAsteroidAsync(ice, system, 0, 10, [1000, 2000, 3000], [1, 0, 0], 25, spaceItems)).Item.Id;
        var unitVolume = rig.Statics.Type(ice)!.Attributes[rig.Statics.AttributeId("volume")!.Value];

        var mined = await rig.Ships.MineAsteroidAsync(
            character.CharacterId, character.ShipId, system, module, rock, MiningSource.Module,
            new MiningNumbers(unitVolume, 0, 0, 0, 0), new MiningRolls(0.5, 0.5, 0.5, 0.5, 0.5),
            1, ledger, spaceItems, rig.Facts, 0);

        Assert.True(mined.Accepted, mined.Reason);
        var output = Assert.Single(await rig.Items.InContainerAsync(character.ShipId), item => item.TypeId == ice);
        Assert.Equal((1L, 134), (output.Quantity, output.Flag));
        Assert.Equal(9, (await rig.Items.GetAsync(rock))!.Quantity);
    }
}
