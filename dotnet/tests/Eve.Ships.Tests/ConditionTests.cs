using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>What a ship and its modules have taken: kept in PostgreSQL, changed by the presets of <c>/dmg</c> and by a heal.</summary>
public class ConditionTests
{
    [PgFact]
    public async Task A_new_ship_has_taken_nothing_and_a_module_nobody_touched_has_none_of_the_shield_a_mended_one_has()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var snapshot = (await rig.Ships.LoadAsync(c.CharacterId))!;
        Assert.Equal(ShipCondition.Full, snapshot.Condition);
        Assert.All(snapshot.Fitted, m => Assert.Equal(ModuleCondition.Untouched, snapshot.ConditionOf(m.Id)));
    }

    [PgFact]
    public async Task What_a_ship_and_a_module_have_taken_is_kept_and_a_ship_that_has_taken_nothing_has_no_row()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var hurt = new ShipCondition(0.35, 0.18, 0.22, 0.6, true);
        await rig.Modules.SetShipConditionAsync(c.ShipId, hurt);
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [c.ShipId + 1] = new(0.3, 0.18, 0.42, true) });

        Assert.Equal(hurt, await rig.Modules.ShipConditionAsync(c.ShipId));
        Assert.Equal(new ModuleCondition(0.3, 0.18, 0.42, true), (await rig.Modules.ModuleConditionsAsync([c.ShipId + 1, c.ShipId + 2]))[c.ShipId + 1]);
        Assert.False((await rig.Modules.ModuleConditionsAsync([c.ShipId + 1, c.ShipId + 2])).ContainsKey(c.ShipId + 2));

        // hurt again: the row is replaced; whole again: the row goes
        await rig.Modules.SetShipConditionAsync(c.ShipId, hurt with { Damage = 0.5 });
        Assert.Equal(0.5, (await rig.Modules.ShipConditionAsync(c.ShipId)).Damage);
        await rig.Modules.SetShipConditionAsync(c.ShipId, ShipCondition.Full);
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [c.ShipId + 1] = ModuleCondition.Untouched });
        Assert.Equal(ShipCondition.Full, await rig.Modules.ShipConditionAsync(c.ShipId));
        Assert.Empty(await rig.Modules.ModuleConditionsAsync([c.ShipId + 1]));
        await using var command = rig.Source.CreateCommand($"SELECT count(*) FROM {rig.FittingSchema}.ship_condition");
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [PgFact]
    public async Task A_fraction_that_is_out_of_range_is_kept_within_it()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await rig.Modules.SetShipConditionAsync(c.ShipId, new ShipCondition(1.5, -0.2, 2, 0.5, false));
        Assert.Equal(new ShipCondition(1, 0, 1, 0.5, false), await rig.Modules.ShipConditionAsync(c.ShipId));
    }

    [PgFact]
    public async Task A_preset_damages_the_ship_and_every_fitted_module_and_says_what_each_was()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();

        var changed = (await rig.Ships.DamageDockedAsync(c.CharacterId, HealthPresets.Light))!;

        Assert.Equal((c.ShipId, ShipCondition.Full, new ShipCondition(0.1, 0.65, 0.7, 0.18, false)), (changed.Ship.Id, changed.Before, changed.After));
        Assert.Equal([c.ShipId + 1, c.ShipId + 2, c.ShipId + 3], changed.Modules.Select(m => m.Module.Id));
        Assert.All(changed.Modules, m => Assert.Equal((ModuleCondition.Untouched, new ModuleCondition(0.08, 0.04, 0.8, false)), (m.Before, m.After)));
        var snapshot = (await rig.Ships.LoadAsync(c.CharacterId))!;
        Assert.Equal(changed.After, snapshot.Condition);
        Assert.All(snapshot.Fitted, m => Assert.Equal(new ModuleCondition(0.08, 0.04, 0.8, false), snapshot.ConditionOf(m.Id)));

        // a heavier one on top: what it was is what the light one left
        var heavier = (await rig.Ships.DamageDockedAsync(c.CharacterId, HealthPresets.Heavy))!;
        Assert.Equal(new ShipCondition(0.1, 0.65, 0.7, 0.18, false), heavier.Before);
        Assert.Equal(new ShipCondition(0.35, 0.18, 0.22, 0.6, false), heavier.After);
    }

    [PgFact]
    public async Task The_presets_are_the_references_and_are_found_by_name_in_any_case()
    {
        Assert.Equal(new[] { HealthPresets.Light, HealthPresets.Medium, HealthPresets.Heavy }, new[] { "LIGHT", "Medium", "heavy" }.Select(n => HealthPresets.Find(n)!));
        Assert.Null(HealthPresets.Find("fatal"));
        Assert.Equal(new ShipCondition(0.2, 0.35, 0.45, 0.35, false), HealthPresets.Medium.Ship);
        Assert.Equal(new ModuleCondition(0.18, 0.1, 0.6, false), HealthPresets.Medium.Module);
        await Task.CompletedTask;
    }

    [PgFact]
    public async Task A_ship_that_is_not_in_a_hangar_cannot_be_damaged_and_one_that_is_not_there_cannot_be_healed()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        Assert.Null(await rig.Ships.DamageDockedAsync(999_999_999, HealthPresets.Light));
        Assert.Null(await rig.Ships.HealAsync(999_999_999));

        await rig.Ships.UndockAsync(c.CharacterId);
        Assert.Null(await rig.Ships.DamageDockedAsync(c.CharacterId, HealthPresets.Light));
        // in space it can be mended all the same
        Assert.NotNull(await rig.Ships.HealAsync(c.CharacterId));
    }

    [PgFact]
    public async Task A_heal_makes_the_ship_whole_and_mends_the_modules_that_are_not_and_says_which()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await rig.Ships.DamageDockedAsync(c.CharacterId, HealthPresets.Heavy);

        var healed = (await rig.Ships.HealAsync(c.CharacterId))!;

        Assert.Equal((new ShipCondition(0.35, 0.18, 0.22, 0.6, false), ShipCondition.Full), (healed.Before, healed.After));
        Assert.Equal([c.ShipId + 1, c.ShipId + 2, c.ShipId + 3], healed.Modules.Select(m => m.Module.Id));
        Assert.All(healed.Modules, m => Assert.Equal((new ModuleCondition(0.3, 0.18, 0.42, false), ModuleCondition.Mended), (m.Before, m.After)));
        var snapshot = (await rig.Ships.LoadAsync(c.CharacterId))!;
        Assert.Equal(ShipCondition.Full, snapshot.Condition);
        Assert.All(snapshot.Fitted, m => Assert.Equal(ModuleCondition.Mended, snapshot.ConditionOf(m.Id)));

        // mended already: the ship is made whole again and no module is said to have changed
        var again = (await rig.Ships.HealAsync(c.CharacterId))!;
        Assert.Empty(again.Modules);
    }

    [PgFact]
    public async Task A_heal_keeps_the_incapacitated_flag_as_the_reference_does_and_a_module_nobody_touched_is_mended_too()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await rig.Modules.SetShipConditionAsync(c.ShipId, new ShipCondition(0.5, 0.5, 0.5, 0.5, true));

        var healed = (await rig.Ships.HealAsync(c.CharacterId))!;

        Assert.True(healed.After.Incapacitated);
        Assert.Equal(3, healed.Modules.Count);
        Assert.All(healed.Modules, m => Assert.Equal(ModuleCondition.Untouched, m.Before));
    }

    [PgFact]
    public async Task What_a_ship_that_docked_hurt_has_taken_is_set_by_the_host()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await rig.Ships.SetShipConditionAsync(c.ShipId, new ShipCondition(0.1, 0.5, 0.75, 0.4, false));
        Assert.Equal(new ShipCondition(0.1, 0.5, 0.75, 0.4, false), (await rig.Ships.LoadAsync(c.CharacterId))!.Condition);
    }
}
