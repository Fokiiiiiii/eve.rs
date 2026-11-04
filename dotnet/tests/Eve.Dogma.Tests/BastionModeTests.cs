namespace Eve.Dogma.Tests;

/// <summary>
/// The bastion module's activation block on micro jump drives (WP16): the client's table (<c>bastionMode.py</c>) adds the module's strength to the
/// activation block of what needs the Micro Jump Drive Operation skill on the ship. Synthetic statics: the table and the engine's skill-scoped reach.
/// </summary>
public class BastionModeTests
{
    private const long CharId = 500, Ship = 700, Skill = 900, Bastion = 701, Drive = 702, OtherModule = 703;
    private const int CharType = 1373, SkillType = BuiltInEffects.MicroJumpDriveOperation, BastionType = 33400, DriveType = 4000;
    private const int BastionEffect = BuiltInEffects.BastionMode, ShipType = 40010, SolarSystem = 800, OtherType = 4001;

    private static DogmaStatic Statics() => new(
        [
            new AttributeDef(182, "requiredSkill1", 0, true, true, 4),
            new AttributeDef(BuiltInEffects.ActivationBlocked, "activationBlocked", 0, true, true, 4),
            new AttributeDef(BuiltInEffects.ActivationBlockedStrength, "activationBlockedStrenght", 0, true, true, 4),
        ],
        [
            new EffectDef(BastionEffect, "bastionMode", EffectDef.Activation, BuiltInEffects.ClientModifiers(BastionEffect).ToList()),
        ],
        [
            new TypeDef(SkillType, 275, 16, new Dictionary<int, double>(), []),
            new TypeDef(BastionType, 1, 66, new Dictionary<int, double> { [BuiltInEffects.ActivationBlockedStrength] = 1 }, [BastionEffect]),
            new TypeDef(DriveType, 1, 7, new Dictionary<int, double> { [182] = SkillType }, []),
            new TypeDef(OtherType, 1, 7, new Dictionary<int, double>(), []),
            new TypeDef(ShipType, 25, 6, new Dictionary<int, double>(), []),
        ]);

    [Fact]
    public void The_client_table_blocks_what_needs_the_micro_jump_drive_skill_by_the_bastions_strength()
    {
        var rows = BuiltInEffects.ClientModifiers(BuiltInEffects.BastionMode);
        var row = Assert.Single(rows);
        Assert.Equal(ModifierFunc.LocationRequiredSkill, row.Func);
        Assert.Equal("shipID", row.Domain);
        Assert.Equal(Operation.ModAdd, row.Operation);
        Assert.Equal(BuiltInEffects.ActivationBlocked, row.ModifiedAttributeId);
        Assert.Equal(BuiltInEffects.ActivationBlockedStrength, row.ModifyingAttributeId);
        Assert.Equal(SkillType, row.SkillTypeId);
        Assert.Empty(BuiltInEffects.ClientModifiers(1));
    }

    [Fact]
    public void A_running_bastion_blocks_a_micro_jump_drive_of_a_pilot_who_has_the_skill_and_only_while_it_runs()
    {
        var running = Ship1(ItemState.Active, withSkill: true);
        Assert.Equal(1, running.Get(Drive, BuiltInEffects.ActivationBlocked), 9);

        var stopped = Ship1(ItemState.Online, withSkill: true);
        Assert.Equal(0, stopped.Get(Drive, BuiltInEffects.ActivationBlocked), 9);
    }

    [Fact]
    public void A_module_that_does_not_need_the_skill_is_not_blocked_by_a_running_bastion()
    {
        // the modifier reaches what requires the skill (the drive does); a module of another kind on the ship does not need it
        var world = Ship1(ItemState.Active, withSkill: true);
        world.Add(new DogmaItem(OtherModule, OtherType, 1, Ship, 0, ItemState.Online));
        Assert.Equal(0, world.Get(OtherModule, BuiltInEffects.ActivationBlocked), 9);
    }

    private static DogmaWorld Ship1(ItemState bastionState, bool withSkill)
    {
        var world = new DogmaWorld(Statics(), CharId, shipId: Ship);
        world.Add(new DogmaItem(CharId, CharType, CharId, SolarSystem, 57));
        world.Add(new DogmaItem(Ship, ShipType, 1, SolarSystem, 4));
        // the skill of this pilot, or the same skill of someone else (who carries nothing here)
        world.Add(withSkill
            ? new DogmaItem(Skill, SkillType, CharId, CharId, 7)
            : new DogmaItem(Skill, SkillType, 999, 999, 7));
        world.Add(new DogmaItem(Drive, DriveType, 1, Ship, 0, ItemState.Online));
        world.Add(new DogmaItem(Bastion, BastionType, 1, Ship, 0, bastionState));
        return world;
    }
}
