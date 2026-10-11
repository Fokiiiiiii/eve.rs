using Eve.Characters;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>A preset of damage for testing repairs: what it leaves of the ship and of each fitted module.</summary>
public sealed record HealthPreset(string Name, ShipCondition Ship, ModuleCondition Module);

/// <summary>The presets of <c>/dmg</c>.</summary>
public static class HealthPresets
{
    public static readonly HealthPreset Light = new("light", new ShipCondition(0.1, 0.65, 0.7, 0.18, false), new ModuleCondition(0.08, 0.04, 0.8, false));
    public static readonly HealthPreset Medium = new("medium", new ShipCondition(0.2, 0.35, 0.45, 0.35, false), new ModuleCondition(0.18, 0.1, 0.6, false));
    public static readonly HealthPreset Heavy = new("heavy", new ShipCondition(0.35, 0.18, 0.22, 0.6, false), new ModuleCondition(0.3, 0.18, 0.42, false));

    /// <summary>The preset with this name (any case), or null.</summary>
    public static HealthPreset? Find(string name) => new[] { Light, Medium, Heavy }.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A fitted module whose condition changed.</summary>
public sealed record ModuleConditionChanged(Item Module, ModuleCondition Before, ModuleCondition After);

/// <summary>What a change of condition did to the ship and its fitted modules (the modules whose condition changed, in the order of their slots).</summary>
public sealed record ConditionChanged(Item Ship, ShipCondition Before, ShipCondition After, IReadOnlyList<ModuleConditionChanged> Modules, int SolarSystemId);

public sealed partial class CharacterShips
{
    private async Task<(Item Ship, IReadOnlyList<Item> Fitted, Character Character)?> ActiveShipAsync(long characterId, CancellationToken cancellationToken)
    {
        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return null;
        }
        var contents = await items.InLocationAsync(shipId, characterId, cancellationToken);
        var (fitted, _) = await ShipSnapshot.SlotsOfAsync(statics, contents, cancellationToken);
        return (ship, fitted, character);
    }

    /// <summary>
    /// The character's ship in its hangar is damaged as the preset says, and so is each module fitted to it (the shield and capacitor as well as
    /// the armor and hull). Null when the character has no ship in a hangar.
    /// </summary>
    public async Task<ConditionChanged?> DamageDockedAsync(long characterId, HealthPreset preset, CancellationToken cancellationToken = default)
    {
        if (await ActiveShipAsync(characterId, cancellationToken) is not var (ship, fitted, character) || ship.LocationId != character.StationId || ship.Flag != ItemFlags.Hangar)
        {
            return null;
        }
        var before = await modules.ShipConditionAsync(ship.Id, cancellationToken);
        var beforeModules = await modules.ModuleConditionsAsync(fitted.Select(m => m.Id), cancellationToken);
        var after = preset.Ship with { Incapacitated = before.Incapacitated };
        await modules.SetShipConditionAsync(ship.Id, after, cancellationToken);
        var changes = new List<ModuleConditionChanged>();
        var kept = new Dictionary<long, ModuleCondition>();
        foreach (var module in fitted)
        {
            var was = beforeModules.GetValueOrDefault(module.Id, ModuleCondition.Untouched);
            var now = preset.Module with { Incapacitated = was.Incapacitated };
            kept[module.Id] = now;
            changes.Add(new ModuleConditionChanged(module, was, now));
        }
        await modules.SetModuleConditionsAsync(kept, cancellationToken);
        return new ConditionChanged(ship, before, after, changes, character.SolarSystemId);
    }

    /// <summary>
    /// The character's ship is mended: full shield, armor, hull and capacitor, and each fitted module that is not whole (one nobody has mended has
    /// not a full shield) made whole. Null when the character has no ship.
    /// </summary>
    public Task<ConditionChanged?> HealAsync(long characterId, CancellationToken cancellationToken = default) =>
        HealActiveShipAsync(characterId, null, cancellationToken);

    /// <summary>Mends only the notified ship while it remains the character's active ship.</summary>
    public Task<ConditionChanged?> HealAsync(long characterId, long expectedShipId, CancellationToken cancellationToken) =>
        expectedShipId > 0 ? HealActiveShipAsync(characterId, expectedShipId, cancellationToken) : Task.FromResult<ConditionChanged?>(null);

    private async Task<ConditionChanged?> HealActiveShipAsync(long characterId, long? expectedShipId, CancellationToken cancellationToken)
    {
        if (await ActiveShipAsync(characterId, cancellationToken) is not var (ship, fitted, owner)
            || expectedShipId is { } expected && ship.Id != expected)
        {
            return null;
        }
        var before = await modules.ShipConditionAsync(ship.Id, cancellationToken);
        var beforeModules = await modules.ModuleConditionsAsync(fitted.Select(m => m.Id), cancellationToken);
        if (expectedShipId is { } boundShip && (await characters.GetAsync(characterId, cancellationToken))?.ShipId != boundShip)
            return null;
        var after = ShipCondition.Full with { Incapacitated = before.Incapacitated };
        await modules.SetShipConditionAsync(ship.Id, after, cancellationToken);
        var changes = new List<ModuleConditionChanged>();
        var kept = new Dictionary<long, ModuleCondition>();
        foreach (var module in fitted)
        {
            var was = beforeModules.GetValueOrDefault(module.Id, ModuleCondition.Untouched);
            if (was.Damage <= 0 && was.ArmorDamage <= 0 && was.ShieldCharge >= 1 && !was.Incapacitated)
            {
                continue;
            }
            kept[module.Id] = ModuleCondition.Mended;
            changes.Add(new ModuleConditionChanged(module, was, ModuleCondition.Mended));
        }
        if (expectedShipId is { } boundModulesShip && (await characters.GetAsync(characterId, cancellationToken))?.ShipId != boundModulesShip)
            return null;
        await modules.SetModuleConditionsAsync(kept, cancellationToken);
        return new ConditionChanged(ship, before, after, changes, owner.SolarSystemId);
    }

    /// <summary>Keeps what the ship has taken (the ship has docked: what the simulation says it has taken is what it has).</summary>
    public Task SetShipConditionAsync(long shipId, ShipCondition condition, CancellationToken cancellationToken = default) =>
        modules.SetShipConditionAsync(shipId, condition, cancellationToken);
}
