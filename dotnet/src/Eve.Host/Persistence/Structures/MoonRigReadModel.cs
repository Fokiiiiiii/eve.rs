using Eve.Dogma;
using Eve.Inventory;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>Resolves a refinery's start-time moon rules from its fitted, corporation-owned rigs.</summary>
public sealed class MoonRigReadModel
{
    private const int AutoFractureDelayAttributeId = 2698;
    private const int MoonYieldMultiplierAttributeId = 2704;
    private const int MoonAsteroidDecayTimeMultiplierAttributeId = 2706;
    private const int MoonAsteroidDecayDisplayHoursAttributeId = 2728;
    private const int FirstRigSlotFlag = 92;
    private const int MaximumRigSlots = 8;
    private static readonly int[] MoonAttributes =
        [AutoFractureDelayAttributeId, MoonYieldMultiplierAttributeId, MoonAsteroidDecayTimeMultiplierAttributeId, MoonAsteroidDecayDisplayHoursAttributeId];

    private readonly PgItemStore _items;
    private readonly DogmaStatic _dogma;
    private readonly IMoonRulesSource _rules;
    private readonly StructureStore _structures;

    // The structure's own record gives its type, owner and system: an anchored structure has no item row (the anchor consumes the item it came from).
    public MoonRigReadModel(PgItemStore items, DogmaStatic dogma, IMoonRulesSource rules, StructureStore structures)
    {
        _items = items;
        _dogma = dogma;
        _rules = rules;
        _structures = structures;
    }

    public async Task<MoonRules> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken = default)
    {
        var record = await _structures.GetAsync(connection, transaction, structureId, true, cancellationToken)
            ?? throw new InvalidOperationException("Moon rig rules require the structure record.");
        var structure = new Item(record.Snapshot.StructureId, record.TypeId, record.OwnerCorporationId, record.SolarSystemId, 0, 1, true, record.Name);
        var rules = _rules.RulesOf(structure.TypeId);
        var contents = await _items.LockContainerAsync(connection, transaction, structureId, cancellationToken);
        var fitted = contents.Where(item =>
            structure.OwnerId > 0 && item.OwnerId == structure.OwnerId && item.Flag >= FirstRigSlotFlag && item.Flag < FirstRigSlotFlag + MaximumRigSlots).ToArray();
        await _dogma.EnsureTypesAsync(fitted.Select(item => item.TypeId).Append(structure.TypeId), cancellationToken);
        var structureType = _dogma.Type(structure.TypeId);
        var rigSizeAttributeId = _dogma.AttributeId("rigSize");
        var rigSlotsAttributeId = _dogma.AttributeId("rigSlots");
        var structureCategoryId = _dogma.CategoryId("Structure");
        var structureModuleCategoryId = _dogma.CategoryId("Structure Module");
        if (structureType is null || structureCategoryId is null || structureType.CategoryId != structureCategoryId ||
            !_dogma.GroupName(structureType.GroupId).Equals("Refinery", StringComparison.OrdinalIgnoreCase) ||
            structureModuleCategoryId is null || rigSizeAttributeId is null || rigSlotsAttributeId is null ||
            !structureType.Attributes.TryGetValue(rigSizeAttributeId.Value, out var structureRigSize) || !double.IsFinite(structureRigSize) || structureRigSize <= 0 ||
            !structureType.Attributes.TryGetValue(rigSlotsAttributeId.Value, out var rigSlotsValue) || !double.IsFinite(rigSlotsValue))
            throw new InvalidOperationException("Moon rig rules require a refinery type with SDE rig data.");
        if (fitted.Length == 0)
            return rules;

        var rigSlots = (int)Math.Clamp(Math.Truncate(rigSlotsValue), 0, MaximumRigSlots);
        var rigs = fitted.Where(item => item.Flag - FirstRigSlotFlag < rigSlots)
            .Where(item => _dogma.Type(item.TypeId) is { } type &&
                IsMoonRig(type, structureRigSize, rigSizeAttributeId.Value, structureModuleCategoryId.Value))
            .ToArray();
        if (rigs.Length == 0)
            return rules;

        var world = new DogmaWorld(_dogma, structureId: structure.Id);
        world.Add(new DogmaItem(structure.Id, structure.TypeId, structure.OwnerId, structure.LocationId, structure.Flag));
        var baseYield = world.Get(structure.Id, MoonYieldMultiplierAttributeId);
        var baseDecayMultiplier = world.Get(structure.Id, MoonAsteroidDecayTimeMultiplierAttributeId);
        foreach (var rig in rigs)
            world.Add(new DogmaItem(rig.Id, rig.TypeId, rig.OwnerId, rig.LocationId, rig.Flag));

        var riggedYield = world.Get(structure.Id, MoonYieldMultiplierAttributeId);
        var yieldScale = double.IsFinite(baseYield) && baseYield > 0 ? riggedYield / baseYield : double.NaN;
        var yieldMultiplier = double.IsFinite(yieldScale) ? Math.Max(0, yieldScale) : 1;
        var autoFractureDelayMs = ToMilliseconds(world.Get(structure.Id, AutoFractureDelayAttributeId), rules.AutoFractureDelayMs);
        // The display-hours attribute already includes rig bonuses; apply only the functional multiplier's ratio.
        var decayMultiplier = world.Get(structure.Id, MoonAsteroidDecayTimeMultiplierAttributeId);
        var decayScale = double.IsFinite(baseDecayMultiplier) && baseDecayMultiplier > 0
            ? decayMultiplier / baseDecayMultiplier
            : double.NaN;
        var fieldDecaySeconds = rules.FieldDecayMs / 1000d * decayScale;
        var fieldDecayMs = ToMilliseconds(fieldDecaySeconds, rules.FieldDecayMs);
        return rules with { YieldMultiplier = yieldMultiplier, AutoFractureDelayMs = autoFractureDelayMs, FieldDecayMs = fieldDecayMs };
    }

    private bool IsMoonRig(TypeDef type, double structureRigSize, int rigSizeAttributeId, int structureModuleCategoryId) =>
        type.CategoryId == structureModuleCategoryId &&
        type.Attributes.TryGetValue(rigSizeAttributeId, out var rigSize) &&
        double.IsFinite(rigSize) && rigSize == structureRigSize &&
        type.EffectIds.Any(effectId => _dogma.Effect(effectId) is { Category: EffectDef.Passive } effect &&
            effect.Modifiers.Any(modifier => modifier.Func == ModifierFunc.Item && modifier.Domain == "structureID" &&
                MoonAttributes.Contains(modifier.ModifiedAttributeId)));

    private static long ToMilliseconds(double seconds, long fallback)
    {
        if (!double.IsFinite(seconds))
            return fallback;
        var milliseconds = Math.Max(0, seconds) * 1000;
        return double.IsFinite(milliseconds) && milliseconds < long.MaxValue
            ? (long)Math.Round(milliseconds, MidpointRounding.AwayFromZero)
            : fallback;
    }
}
