using Eve.Sde;

namespace Eve.Universe;

/// <summary>
/// One wormhole type from the pinned SDE (build 3396210, <c>typeDogma</c>). Masses are kilograms; the lifetime is minutes
/// (<c>wormholeMaxStableTime</c>, 960 = 16 hours). <see cref="TargetSystemClass"/> is the class of the system the wormhole
/// leads to (1 to 6 = C1 to C6, 7 high-sec, 8 low-sec, 9 null-sec, 12 Thera, 13 shattered, 14 to 18 Drifter, 25 Pochven),
/// or -1 when the target is drawn from <see cref="TargetDistribution"/> instead (27 types, the Pochven-side C729 family).
/// </summary>
public sealed record WormholeType(
    int TypeId,
    int TargetSystemClass,
    int MaxStableMinutes,
    double MaxStableMassKg,
    double MassRegenerationKg,
    double MaxJumpMassKg,
    int TargetDistribution);

/// <summary>
/// The wormhole types of the static data. A type is a wormhole when it carries <c>wormholeMaxStableMass</c> (attribute 1383):
/// 129 types in build 3396210. K162, the generic exit, carries no such attribute and is not in the catalog.
/// </summary>
public static class WormholeTypeCatalog
{
    public const int TargetSystemClassAttribute = 1381;
    public const int MaxStableTimeAttribute = 1382;
    public const int MaxStableMassAttribute = 1383;
    public const int MassRegenerationAttribute = 1384;
    public const int MaxJumpMassAttribute = 1385;
    public const int TargetDistributionAttribute = 1457;

    private const string WormholeContainment = "{\"dogmaAttributes\":[{\"attributeID\":1383}]}";

    public static async Task<IReadOnlyDictionary<int, WormholeType>> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var types = new Dictionary<int, WormholeType>();
        foreach (var row in await sde.ReadContainingAsync("typeDogma", WormholeContainment, cancellationToken))
        {
            var typeId = (int)row.Key;
            var values = new Dictionary<int, double>();
            foreach (var attribute in row.Doc.GetProperty("dogmaAttributes").EnumerateArray())
            {
                values[attribute.GetProperty("attributeID").GetInt32()] = attribute.GetProperty("value").GetDouble();
            }
            types[typeId] = new WormholeType(
                typeId,
                (int)Required(values, TargetSystemClassAttribute, typeId),
                (int)Required(values, MaxStableTimeAttribute, typeId),
                Required(values, MaxStableMassAttribute, typeId),
                values.GetValueOrDefault(MassRegenerationAttribute),
                Required(values, MaxJumpMassAttribute, typeId),
                (int)values.GetValueOrDefault(TargetDistributionAttribute));
        }
        return types;
    }

    private static double Required(IReadOnlyDictionary<int, double> values, int attributeId, int typeId) =>
        values.TryGetValue(attributeId, out var value)
            ? value
            : throw new InvalidOperationException($"wormhole type {typeId} has no dogma attribute {attributeId}");
}
