using Eve.Inventory;

namespace Eve.Ships.Tests;

public class PilotLearningBonusTests
{
    private const int Memory = 166;
    private const int Perception = 167;
    private const int ImplantSlot = 89;

    /// <summary>Memory Augmentation - Basic (SDE 9941): memoryBonus 3 through the ModAdd effect on memory (EveJS applyActiveImplantAttributeBonuses).</summary>
    private const int MemoryAugmentationBasic = 9941;

    [PgFact]
    public async Task The_bonus_of_a_plugged_in_implant_is_what_the_training_rate_adds_and_it_is_gone_once_it_is_removed()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var bonus = new PilotLearningBonus(rig.Statics, rig.Characters, rig.Items, rig.Skills, () => 1_700_000_000_000L);
        await bonus.RefreshAsync(c.CharacterId);
        Assert.Equal(0, bonus.Of(c.CharacterId, Memory));

        var implant = await rig.GiveAsync(c.CharacterId, MemoryAugmentationBasic, location: c.CharacterId, flag: ImplantSlot);
        await bonus.RefreshAsync(c.CharacterId);
        Assert.Equal(3, bonus.Of(c.CharacterId, Memory), 9);
        Assert.Equal(0, bonus.Of(c.CharacterId, Perception), 9);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, implant, Rig.Station, ItemFlags.Hangar, CancellationToken.None);
            await transaction.CommitAsync();
        }
        await bonus.RefreshAsync(c.CharacterId);
        Assert.Equal(0, bonus.Of(c.CharacterId, Memory), 9);
    }

    [PgFact]
    public void An_attribute_that_is_not_a_learning_attribute_has_no_bonus()
    {
        var bonus = new PilotLearningBonus(new Eve.Dogma.DogmaStatic([], [], []), null!, null!, null!, () => 0);
        Assert.Equal(0, bonus.Of(1, 9999));
    }
}
