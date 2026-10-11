using Eve.Sde;
using Eve.Structures;
using Eve.Universe;

namespace Eve.Host;

/// <summary>
/// The timeline's rules for a structure, from the SDE (pinned build): the size from the type's <c>rigSize</c> (attribute 1547, <c>typeDogma</c>), the band from the
/// system (<c>mapSolarSystems</c>: its security class by <see cref="Security.ClassFromLevel"/>, Wormhole for J-space, Pochven for the Pochven region). A type or a
/// system without static data is an error, not a guess: the timer job fails and is retried.
/// </summary>
public sealed class StructureRulesSource : IStructureRulesSource
{
    private const int RigSizeAttribute = 1547;
    private const long PochvenRegionId = 10000070;
    private const int FirstWormholeSystemId = 31000000;
    private const int WormholeSystemIdLimit = 32000000;

    private readonly IReadOnlySet<int> _types;
    private readonly IReadOnlyDictionary<int, double> _rigSizes;
    private readonly IReadOnlyDictionary<int, (double Security, int RegionId)> _systems;

    private StructureRulesSource(IReadOnlySet<int> types, IReadOnlyDictionary<int, double> rigSizes, IReadOnlyDictionary<int, (double Security, int RegionId)> systems)
    {
        _types = types;
        _rigSizes = rigSizes;
        _systems = systems;
    }

    public static async Task<StructureRulesSource> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var types = new HashSet<int>();
        var rigSizes = new Dictionary<int, double>();
        foreach (var row in await sde.ReadAsync("typeDogma", null, cancellationToken))
        {
            var typeId = (int)row.Key;
            types.Add(typeId);
            foreach (var attribute in row.Doc.GetProperty("dogmaAttributes").EnumerateArray())
            {
                if (attribute.GetProperty("attributeID").GetInt32() == RigSizeAttribute)
                    rigSizes[typeId] = attribute.GetProperty("value").GetDouble();
            }
        }
        var systems = new Dictionary<int, (double Security, int RegionId)>();
        foreach (var row in await sde.ReadAsync("mapSolarSystems", null, cancellationToken))
        {
            systems[(int)row.Key] = (row.Doc.GetProperty("securityStatus").GetDouble(), row.Doc.GetProperty("regionID").GetInt32());
        }
        return new StructureRulesSource(types, rigSizes, systems);
    }

    public StructureRules RulesOf(int typeId, int solarSystemId)
    {
        if (!_types.Contains(typeId))
            throw new InvalidOperationException($"structure type {typeId} has no static data");
        if (!_systems.TryGetValue(solarSystemId, out var system))
            throw new InvalidOperationException($"solar system {solarSystemId} has no static data");
        var size = StructureRules.SizeOfRigSize(_rigSizes.GetValueOrDefault(typeId));
        var band = solarSystemId is >= FirstWormholeSystemId and < WormholeSystemIdLimit ? SecurityBand.Wormhole
            : system.RegionId == PochvenRegionId ? SecurityBand.Pochven
            : Security.ClassFromLevel(system.Security) switch
            {
                SecurityClass.ZeroSec => SecurityBand.NullSec,
                SecurityClass.LowSec => SecurityBand.LowSec,
                _ => SecurityBand.HighSec,
            };
        return StructureRules.Of(size, band);
    }
}
