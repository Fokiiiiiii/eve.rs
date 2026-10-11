using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>A harvestable gas cloud is mined as a rock is (M, gas harvesters): a cycle delivers the cloud's compressed gas into the hold.</summary>
public class GasCloudMiningTests
{
    private const int GasCloudHarvesterI = 60313;
    private const int AmberCytoserocin = 25268;

    [PgFact]
    public async Task A_gas_cloud_yields_its_compressed_gas_into_the_hold_and_its_units_go_down()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([GasCloudHarvesterI, AmberCytoserocin]);
        var gas = await rig.Statics.GasYieldTypeAsync(AmberCytoserocin);
        Assert.NotNull(gas);
        var c = await rig.NewCharacterAsync();
        await rig.Ships.UndockAsync(c.CharacterId);
        var system = (int)(await rig.Items.GetAsync(c.ShipId))!.LocationId;
        var harvester = await rig.GiveAsync(c.CharacterId, GasCloudHarvesterI, singleton: true, quantity: 1, location: c.ShipId, flag: ItemFlags.HiSlot0);
        var schema = $"sp_{Guid.NewGuid():N}"[..14];
        rig.KeepSchemas(schema);
        await PgSpaceItemStore.MigrateAsync(rig.Source, schema);
        var spaceItems = new PgSpaceItemStore(rig.Source, schema);
        var ledger = new PgWorkCycleLedger(rig.Source, schema);
        var cloud = (await rig.Ships.CreateAsteroidAsync(AmberCytoserocin, system, 0, 1000, [1000, 2000, 3000], [1, 0, 0], 25, spaceItems)).Item.Id;

        var mined = await rig.Ships.MineAsteroidAsync(
            c.CharacterId, c.ShipId, system, harvester, cloud, MiningSource.Module,
            new MiningNumbers(10, 0, 0, 0, 0), new MiningRolls(0.5, 0.5, 0.5, 0.5, 0.5), 1, ledger, spaceItems, rig.Facts, 0);

        Assert.True(mined.Accepted, mined.Reason);
        Assert.Equal(gas!.Value, mined.OreTypeId);
        Assert.True(mined.DeliveredUnits > 0);
        Assert.Contains(await rig.Items.InContainerAsync(c.ShipId), item => item.TypeId == gas.Value && item.Quantity == mined.DeliveredUnits);
        Assert.Equal(1000 - mined.DeliveredUnits, (await rig.Items.GetAsync(cloud))!.Quantity);
    }
}
