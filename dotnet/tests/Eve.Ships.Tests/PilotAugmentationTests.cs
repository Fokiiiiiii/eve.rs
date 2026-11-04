using Eve.Inventory;
using Npgsql;

namespace Eve.Ships.Tests;

public class PilotAugmentationTests
{
    private const int Memory = 166;
    private const int ImplantSlot = 89;

    /// <summary>Memory Augmentation - Basic (SDE 9941): memoryBonus 3, an ItemModifier on the pilot's memory.</summary>
    private const int MemoryAugmentationBasic = 9941;

    [PgFact]
    public async Task An_implant_plugged_into_the_pilot_changes_the_attribute_dogma_reads_until_it_is_removed()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var memoryBefore = (await rig.Ships.LoadAsync(c.CharacterId))!.World.Get(c.CharacterId, Memory);

        var implant = await rig.GiveAsync(c.CharacterId, MemoryAugmentationBasic, location: c.CharacterId, flag: ImplantSlot);
        var plugged = await rig.Ships.LoadAsync(c.CharacterId);
        Assert.Equal(memoryBefore + 3, plugged!.World.Get(c.CharacterId, Memory), 9);
        Assert.Contains(plugged.World.Items, item => item.Id == implant && item.FlagId == ImplantSlot);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, implant, Rig.Station, ItemFlags.Hangar, CancellationToken.None);
            await transaction.CommitAsync();
        }
        var removed = await rig.Ships.LoadAsync(c.CharacterId);
        Assert.Equal(memoryBefore, removed!.World.Get(c.CharacterId, Memory), 9);
    }

    [PgFact]
    public async Task An_item_in_the_implant_slot_that_is_not_an_implant_does_not_act_on_the_pilot()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var memoryBefore = (await rig.Ships.LoadAsync(c.CharacterId))!.World.Get(c.CharacterId, Memory);

        await rig.GiveAsync(c.CharacterId, Rig.Railgun, location: c.CharacterId, flag: ImplantSlot);
        var snapshot = await rig.Ships.LoadAsync(c.CharacterId);
        Assert.Equal(memoryBefore, snapshot!.World.Get(c.CharacterId, Memory), 9);
    }
}
