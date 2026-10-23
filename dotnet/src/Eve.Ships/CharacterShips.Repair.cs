using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>
/// Nanite repair: the paste that mends a fitted module's damage in space. The rules are the reference's (EVE.js <c>dogmaService/moduleRepair.js</c>, the
/// V24.01 client's <c>godma.RepairModule</c>): a repair starts when the module is damaged and not burned out, it repairs at the character's module repair
/// rate, and it costs paste for the whole damage at the character's paste cost. The paste is taken as the repair is ended, for the part it mended
/// </summary>
public sealed partial class CharacterShips
{
    /// <summary>The type of Nanite Repair Paste, the stack a repair takes its units from.</summary>
    public const int NaniteRepairPasteTypeId = 28668;

    private const int ModuleRepairRateAttribute = 1267;
    private const int ShipBrokenModuleRepairCostMultiplierAttribute = 1277;
    private const int StructureHitPointsAttribute = 9;
    private const int TypeRepairCostMultiplierAttribute = 187;
    private const int TypeRepairCostPercentAttribute = 396;
    private const int DamageAttribute = 3;
    private const int ModuleCategory = 7;
    private const int StructureModuleCategory = 66;

    /// <summary>A repair under way, as the host began it: the module and the ship it is in, when it began (ms since the epoch), its rate (hit points a minute), its damage then and its paste.</summary>
    public sealed record RepairTerms(long ModuleId, long ShipId, long StartedMs, double RateHpPerMinute, double DamageHp, long PasteUnits);

    /// <summary>What a repair of one module would cost and take: its hit points, its damage, the rate it repairs at, and the paste for the whole damage.</summary>
    public sealed record RepairQuote(long ModuleId, double HitPoints, double DamageHp, double RateHpPerMinute, long PasteUnits, bool Incapacitated);

    /// <summary>What a repair of a fitted module of the character's ship costs; null when the module is not one of its fitted modules, or has no damage to repair.</summary>
    public async Task<RepairQuote?> QuoteRepairAsync(long characterId, long moduleId, CancellationToken cancellationToken = default)
    {
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot
            || snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is not { } module
            || statics.Type(module.TypeId) is not { } type
            || type.CategoryId is not (ModuleCategory or StructureModuleCategory))
        {
            return null;
        }
        var hitPoints = type.Attributes.GetValueOrDefault(StructureHitPointsAttribute, 0);
        var condition = snapshot.ModuleConditions.GetValueOrDefault(moduleId, ModuleCondition.Untouched);
        var damageHp = hitPoints * condition.Damage;
        if (hitPoints <= 0 || damageHp <= 0)
        {
            return null;
        }
        // the character's rate and cost, as its skills have them (Nanite Interfacing, Nanite Operation), and the type's own cost (repairCostMultiplier, repairCostPercent)
        var rate = Math.Max(0.01, snapshot.World.Get(characterId, ModuleRepairRateAttribute));
        var costMultiplier = Math.Max(0.01, snapshot.World.Get(characterId, ShipBrokenModuleRepairCostMultiplierAttribute));
        var typeMultiplier = type.Attributes.GetValueOrDefault(TypeRepairCostMultiplierAttribute, 1);
        var typePercent = type.Attributes.GetValueOrDefault(TypeRepairCostPercentAttribute, 100);
        var paste = Math.Max(1, (long)Math.Ceiling(damageHp * costMultiplier * typeMultiplier * (typePercent / 100)));
        return new RepairQuote(moduleId, hitPoints, damageHp, rate, paste, condition.Incapacitated);
    }

    /// <summary>The units of nanite repair paste in the ship's cargo hold.</summary>
    public async Task<long> PasteInCargoAsync(long characterId, long shipId, CancellationToken cancellationToken = default) =>
        (await items.InLocationAsync(shipId, characterId, cancellationToken))
        .Where(item => item.TypeId == NaniteRepairPasteTypeId && item.Flag == CargoFlag)
        .Sum(item => item.Quantity);

    /// <summary>
    /// Ends a repair: the hit points it mended by now (its rate times the minutes since it started, and no more than the damage left) are taken off the module,
    /// and the paste for them is taken from the cargo hold (no more than is there). The answer is what changed, for the client to be told.
    /// </summary>
    public async Task<ShipOutcome> FinishRepairAsync(long characterId, RepairTerms repair, long nowMs, CancellationToken cancellationToken = default)
    {
        if (await LoadShipAsync(characterId, repair.ShipId, cancellationToken) is not { } snapshot
            || snapshot.Fitted.FirstOrDefault(m => m.Id == repair.ModuleId) is not { } module
            || statics.Type(module.TypeId) is not { } type)
        {
            return new NothingHappened();
        }
        var hitPoints = type.Attributes.GetValueOrDefault(StructureHitPointsAttribute, 0);
        var condition = snapshot.ModuleConditions.GetValueOrDefault(repair.ModuleId, ModuleCondition.Untouched);
        var currentDamage = hitPoints * condition.Damage;
        var minutes = Math.Max(0, (nowMs - repair.StartedMs) / 60000.0);
        var mended = Math.Min(currentDamage, minutes * repair.RateHpPerMinute);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stacks = new List<Item>();
        foreach (var stack in (await items.InLocationAsync(repair.ShipId, characterId, cancellationToken))
            .Where(item => item.TypeId == NaniteRepairPasteTypeId && item.Flag == CargoFlag).OrderBy(item => item.Id))
        {
            if (await items.LockAndGetAsync(connection, transaction, stack.Id, cancellationToken) is { } locked)
            {
                stacks.Add(locked);
            }
        }
        var available = stacks.Sum(stack => stack.Quantity);
        var wanted = mended >= repair.DamageHp ? repair.PasteUnits : (long)Math.Ceiling(repair.PasteUnits * mended / repair.DamageHp);
        var used = Math.Min(wanted, available);
        if (used < wanted)
        {
            // the paste that is gone covers only part of what was mended: the repair stops where the paste ends
            mended = repair.PasteUnits > 0 ? repair.DamageHp * used / repair.PasteUnits : 0;
        }
        mended = Math.Min(mended, currentDamage);

        var events = new List<AmmoEvent>();
        var remaining = used;
        foreach (var stack in stacks)
        {
            if (remaining <= 0)
            {
                break;
            }
            var take = Math.Min(remaining, stack.Quantity);
            remaining -= take;
            if (take == stack.Quantity)
            {
                await items.DeleteAsync(connection, transaction, stack.Id, cancellationToken);
                events.Add(new ItemChanged(stack with { LocationId = JunkLocation, Quantity = 0 }, stack.LocationId, stack.Flag, stack.Quantity));
            }
            else
            {
                await items.SetQuantityAsync(connection, transaction, stack.Id, stack.Quantity - take, cancellationToken);
                events.Add(new ItemChanged(stack with { Quantity = stack.Quantity - take }, stack.LocationId, stack.Flag, stack.Quantity));
            }
        }
        await transaction.CommitAsync(cancellationToken);

        var left = currentDamage - mended;
        var ratio = hitPoints > 0 ? Math.Clamp(left / hitPoints, 0, 1) : 0;
        var repaired = condition with
        {
            Damage = ratio,
            Incapacitated = ratio >= 1 || (condition.Incapacitated && ratio > 0),
        };
        await modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [repair.ModuleId] = repaired }, cancellationToken);
        if (mended > 0)
        {
            events.Add(new ModuleAttributesChanged([new AttributeChanged(repair.ModuleId, DamageAttribute, currentDamage, left)]));
        }
        return new AmmoChanged(repair.ShipId, events);
    }
}
