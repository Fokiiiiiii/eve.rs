using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>Nanite paste repair of a fitted module in space: what a repair asks, what it mends and what paste it uses (PostgreSQL).</summary>
public class RepairTests
{
    private const int Paste = CharacterShips.NaniteRepairPasteTypeId;
    private const int Ibis = 601;
    private const int Hitpoints = 9;

    [PgFact]
    public async Task A_damaged_module_is_quoted_at_the_character_rate_and_the_paste_for_its_whole_damage()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Ibis);
        var module = c.ShipId + 1;
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [module] = new(0.5, 0, 1, false) });
        var moduleType = (await rig.Ships.LoadAsync(c.CharacterId))!.Fitted.First(m => m.Id == module).TypeId;
        var hp = rig.Statics.Type(moduleType)!.Attributes[Hitpoints];

        var quote = (await rig.Ships.QuoteRepairAsync(c.CharacterId, module))!;

        // no skills: the SDE's module repair rate (10 hit points a minute) and paste cost (0.5 a hit point, the type's own cost is the SDE's default)
        Assert.Equal(hp * 0.5, quote.DamageHp, 9);
        Assert.Equal(10, quote.RateHpPerMinute, 9);
        Assert.Equal((long)Math.Ceiling(hp * 0.5 * 0.5), quote.PasteUnits);
        Assert.False(quote.Incapacitated);
    }

    [PgFact]
    public async Task A_module_that_is_undamaged_or_not_fitted_has_no_repair_to_quote()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Ibis);
        Assert.Null(await rig.Ships.QuoteRepairAsync(c.CharacterId, c.ShipId + 1));
        Assert.Null(await rig.Ships.QuoteRepairAsync(c.CharacterId, 999_999_999));
    }

    [PgFact]
    public async Task Ending_a_repair_takes_the_paste_it_used_and_leaves_the_damage_it_did_not_mend()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Ibis);
        var module = c.ShipId + 1;
        var stack = await rig.GiveAsync(c.CharacterId, Paste, singleton: false, quantity: 1000, location: c.ShipId, flag: CharacterShips.CargoFlag);
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [module] = new(0.5, 0, 1, false) });
        var quote = (await rig.Ships.QuoteRepairAsync(c.CharacterId, module))!;
        var hp = quote.DamageHp / 0.5;
        var start = 1_000_000L;
        var repair = new CharacterShips.RepairTerms(module, c.ShipId, start, 2.0, quote.DamageHp, quote.PasteUnits);

        // one minute at 2 hit points a minute
        var outcome = (AmmoChanged)await rig.Ships.FinishRepairAsync(c.CharacterId, repair, start + 60_000);

        var used = (long)Math.Ceiling(quote.PasteUnits * 2.0 / quote.DamageHp);
        Assert.Equal(1000 - used, (await rig.Items.GetAsync(stack))!.Quantity);
        var left = quote.DamageHp - 2.0;
        var condition = (await rig.Modules.ModuleConditionsAsync([module]))[module];
        Assert.Equal(left / hp, condition.Damage, 9);
        Assert.Contains(outcome.Events, e => e is ModuleAttributesChanged a && a.Changes.Single().AttributeId == 3 && Math.Abs(a.Changes.Single().New - left) < 1e-9);
        Assert.Contains(outcome.Events, e => e is ItemChanged i && i.After.Id == stack && i.After.Quantity == 1000 - used);
    }

    [PgFact]
    public async Task A_repair_that_runs_to_the_end_mends_the_whole_damage_and_uses_all_its_paste()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Ibis);
        var module = c.ShipId + 1;
        var stack = await rig.GiveAsync(c.CharacterId, Paste, singleton: false, quantity: 500, location: c.ShipId, flag: CharacterShips.CargoFlag);
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [module] = new(0.5, 0, 1, false) });
        var quote = (await rig.Ships.QuoteRepairAsync(c.CharacterId, module))!;
        var repair = new CharacterShips.RepairTerms(module, c.ShipId, 0, quote.RateHpPerMinute, quote.DamageHp, quote.PasteUnits);

        // long enough for the whole damage at the rate
        await rig.Ships.FinishRepairAsync(c.CharacterId, repair, (long)(quote.DamageHp / quote.RateHpPerMinute * 60_000) + 60_000);

        Assert.Equal(500 - quote.PasteUnits, (await rig.Items.GetAsync(stack))!.Quantity);
        Assert.Equal(0, (await rig.Modules.ModuleConditionsAsync([module]))[module].Damage, 9);
        Assert.Null(await rig.Ships.QuoteRepairAsync(c.CharacterId, module));
    }

    [PgFact]
    public async Task A_repair_stops_where_the_paste_in_the_hold_ends_and_the_stack_that_is_used_up_goes()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: Ibis);
        var module = c.ShipId + 1;
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [module] = new(0.5, 0, 1, false) });
        var quote = (await rig.Ships.QuoteRepairAsync(c.CharacterId, module))!;
        var half = quote.PasteUnits / 2;
        var stack = await rig.GiveAsync(c.CharacterId, Paste, singleton: false, quantity: half, location: c.ShipId, flag: CharacterShips.CargoFlag);
        var repair = new CharacterShips.RepairTerms(module, c.ShipId, 0, quote.RateHpPerMinute, quote.DamageHp, quote.PasteUnits);

        // the whole damage would take longer than the paste covers: the repair ends where the paste does
        await rig.Ships.FinishRepairAsync(c.CharacterId, repair, (long)(quote.DamageHp / quote.RateHpPerMinute * 60_000) + 60_000);

        Assert.Null(await rig.Items.GetAsync(stack));
        var damage = (await rig.Modules.ModuleConditionsAsync([module]))[module].Damage;
        Assert.True(damage > 0 && damage < 0.5, $"the module is part repaired, damage {damage}");
        Assert.Equal(0, await rig.Ships.PasteInCargoAsync(c.CharacterId, c.ShipId));
    }
}
