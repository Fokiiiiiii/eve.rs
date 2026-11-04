using Eve.Sde;
using Eve.Structures;

namespace Eve.Host;

/// <summary>
/// The moon rules from the SDE (pinned build): a refinery's auto-fracture delay (attribute 2698, seconds), the field's decay (2728 hours, times 2706) and the
/// systems of the Pochven region (<c>mapSolarSystems</c>). EVE.js moonExtractionRules reads the same attributes with the same defaults (10800 s, 48 hours, 1).
/// </summary>
public sealed class MoonRulesSource : IMoonRulesSource
{
    private const int AutoFractureDelayAttribute = 2698;
    private const int FieldDecayHoursAttribute = 2728;
    private const int DecayMultiplierAttribute = 2706;
    private const long PochvenRegionId = 10000070;
    private const double DefaultAutoFractureDelaySeconds = 10_800;
    private const double DefaultFieldDecayHours = 48;

    private readonly IReadOnlyDictionary<int, (double? DelaySeconds, double? DecayHours, double? DecayMultiplier)> _byType;
    private readonly IReadOnlySet<long> _pochven;

    private MoonRulesSource(IReadOnlyDictionary<int, (double? DelaySeconds, double? DecayHours, double? DecayMultiplier)> byType, IReadOnlySet<long> pochven)
    {
        _byType = byType;
        _pochven = pochven;
    }

    public static async Task<MoonRulesSource> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var byType = new Dictionary<int, (double? DelaySeconds, double? DecayHours, double? DecayMultiplier)>();
        foreach (var row in await sde.ReadAsync("typeDogma", null, cancellationToken))
        {
            double? delay = null, hours = null, multiplier = null;
            foreach (var attribute in row.Doc.GetProperty("dogmaAttributes").EnumerateArray())
            {
                var id = attribute.GetProperty("attributeID").GetInt32();
                var value = attribute.GetProperty("value").GetDouble();
                if (id == AutoFractureDelayAttribute) delay = value;
                else if (id == FieldDecayHoursAttribute) hours = value;
                else if (id == DecayMultiplierAttribute) multiplier = value;
            }
            if (delay is not null || hours is not null || multiplier is not null)
                byType[(int)row.Key] = (delay, hours, multiplier);
        }
        var pochven = new HashSet<long>();
        foreach (var row in await sde.ReadAsync("mapSolarSystems", null, cancellationToken))
        {
            if (row.Doc.GetProperty("regionID").GetInt64() == PochvenRegionId)
                pochven.Add(row.Key);
        }
        return new MoonRulesSource(byType, pochven);
    }

    /// <summary>The rules of a structure type: the fracture delay and the field's lifetime, each with the SDE's value or EVE.js's default.</summary>
    public MoonRules RulesOf(int typeId)
    {
        var (delay, hours, multiplier) = _byType.GetValueOrDefault(typeId);
        var delayMs = (long)Math.Round((delay ?? DefaultAutoFractureDelaySeconds) * 1000);
        var decayMs = (long)Math.Round((hours ?? DefaultFieldDecayHours) * 3_600_000 * (multiplier ?? 1));
        return new MoonRules(delayMs, decayMs);
    }

    /// <summary>Moon mining is refused in Pochven (EVE.js POCHVEN_MOON_MINING_DENIED).</summary>
    public bool IsPochven(long solarSystemId) => _pochven.Contains(solarSystemId);
}
