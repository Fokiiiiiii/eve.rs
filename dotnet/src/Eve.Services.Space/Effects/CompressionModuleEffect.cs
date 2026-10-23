using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The ship compressors (group 4174, effect <c>industrialItemCompression</c>, 8364): a high-slot module of a Porpoise, Orca or Rorqual that, while it runs, makes the
/// Compression). It is not a structure module: in-space compression needs no structure. The effect cycles for its duration (73) and costs nothing else; the work is done
/// when a fleet member asks the facility to compress an item (<see cref="CompressionFacilities"/>, <c>inSpaceCompressionMgr.CompressItemInSpace</c>). The industrial core
/// it needs (3265) is enforced by the simulation as for any module (<see cref="ShipEffect"/>), and a compressor stops with its core.
/// </summary>
internal sealed class CompressionModuleEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["industrialItemCompression"];

    // no body for a compressor the static data gives no type list to compress
    protected override string? Refusal(ModuleEffectContext module) =>
        module.Value(ModuleAttributes.CompressibleItemsTypeList, 0) > 0 ? null : PendingModuleEffect.NotImplemented;

    protected override SimEffect Build(ModuleEffectContext module) => DogmaModifierEffect.BuildModifier(module);
}

/// <summary>What a ship offers as a compression facility: the type lists its running compressors compress and how far each reaches.</summary>
internal static class CompressionFacilities
{
    private const string EffectName = "industrialItemCompression";

    /// <summary>The farthest a facility reaches, metres (the client's <c>MAX_FACILITY_ACTIVATION_RANGE</c>, itemcompression/__init__.py).</summary>
    public const int MaxRange = 250_000;

    /// <summary>
    /// The type lists (3255) of the ship's running compressors, each with the farthest range (54, after dogma: Fleet Compression Logistics adds to it) of the
    /// compressors that work it, kept within one metre to <see cref="MaxRange"/>. Empty when no compressor of the snapshot is active. The snapshot's module
    /// states are the pilot's (<c>SpaceSessions.FlownShipAsync</c>).
    /// </summary>
    public static IReadOnlyDictionary<int, int> Of(ShipSnapshot snapshot, DogmaStatic statics)
    {
        var lists = new SortedDictionary<int, int>();
        foreach (var module in snapshot.Fitted)
        {
            if (snapshot.StateOf(module.Id) < ItemState.Active
                || statics.Type(module.TypeId) is not { } type
                || !type.EffectIds.Select(statics.Effect).Any(effect => effect is { } found && found.Name.Equals(EffectName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var list = (int)snapshot.World.Get(module.Id, ModuleAttributes.CompressibleItemsTypeList);
            var range = snapshot.World.Get(module.Id, ModuleAttributes.MaxRange);
            if (list <= 0 || !double.IsFinite(range) || range <= 0)
            {
                continue;
            }
            var reach = (int)Math.Clamp(Math.Round(range), 1, MaxRange);
            lists[list] = Math.Max(lists.GetValueOrDefault(list), reach);
        }
        return lists;
    }
}
