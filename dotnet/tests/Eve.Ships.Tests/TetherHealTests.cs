using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships.Tests;

public sealed class TetherHealTests
{
    [PgFact]
    public async Task An_expected_ship_mismatch_leaves_the_active_ship_and_modules_damaged()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        await rig.Ships.DamageDockedAsync(character.CharacterId, HealthPresets.Heavy);
        var before = (await rig.Ships.LoadAsync(character.CharacterId))!;

        Assert.Null(await rig.Ships.HealAsync(character.CharacterId, character.ShipId + 100, CancellationToken.None));
        var after = (await rig.Ships.LoadAsync(character.CharacterId))!;
        Assert.Equal(before.Condition, after.Condition);
        Assert.All(before.Fitted, module => Assert.Equal(before.ConditionOf(module.Id), after.ConditionOf(module.Id)));
    }

    [PgFact]
    public async Task Boarding_after_preflight_cannot_redirect_a_tether_heal_to_the_new_active_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        await rig.Ships.DamageDockedAsync(character.CharacterId, HealthPresets.Heavy);
        var preflight = (await rig.Ships.LoadAsync(character.CharacterId))!;
        var nextShip = await rig.GiveAsync(character.CharacterId, Rig.Cockroach);
        var nextModule = await rig.GiveAsync(character.CharacterId, Rig.Railgun, location: nextShip, flag: ItemFlags.HiSlot0);
        var hurt = new ShipCondition(0.5, 0.25, 0.25, 0.2, false);
        var burnt = new ModuleCondition(1, 0.5, 0, true);
        await rig.Modules.SetShipConditionAsync(nextShip, hurt);
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [nextModule] = burnt });

        Assert.True(await rig.Ships.BoardAsync(character.CharacterId, Rig.Station, character.ShipId, nextShip));
        Assert.Null(await rig.Ships.HealAsync(character.CharacterId, preflight.Ship.Id, CancellationToken.None));

        Assert.Equal(nextShip, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Equal(hurt, await rig.Modules.ShipConditionAsync(nextShip));
        Assert.Equal(burnt, (await rig.Modules.ModuleConditionsAsync([nextModule]))[nextModule]);
        Assert.Equal(preflight.Condition, await rig.Modules.ShipConditionAsync(preflight.Ship.Id));
        var originalModules = await rig.Modules.ModuleConditionsAsync(preflight.Fitted.Select(module => module.Id));
        Assert.All(preflight.Fitted, module => Assert.Equal(preflight.ConditionOf(module.Id), originalModules[module.Id]));
    }

    [PgFact]
    public async Task A_matching_expected_ship_heals_that_ship_and_its_fitted_modules()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        await rig.Ships.DamageDockedAsync(character.CharacterId, HealthPresets.Heavy);

        var changed = Assert.IsType<ConditionChanged>(
            await rig.Ships.HealAsync(character.CharacterId, character.ShipId, CancellationToken.None));
        Assert.Equal(character.ShipId, changed.Ship.Id);
        Assert.Equal(ShipCondition.Full, changed.After);
        var after = (await rig.Ships.LoadAsync(character.CharacterId))!;
        Assert.All(changed.Modules, module => Assert.Equal(ModuleCondition.Mended, after.ConditionOf(module.Module.Id)));
    }
}
