using System.Text.RegularExpressions;
using Eve.Sde;

namespace Eve.Universe;

/// <summary>
/// One wormhole system effect at one system class, from the pinned SDE (build 3396210, group 920 "Effect Beacon" types named
/// <c>Class N &lt;Effect&gt; Effects</c>, 36 types). <see cref="Modifiers"/> are the dogma attributes the beacon sets on the ships
/// of the system, as the SDE states them (multipliers, or percentage bonuses for resistances).
/// </summary>
public sealed record WormholeEffect(int TypeId, string Name, int SystemClass, IReadOnlyDictionary<string, double> Modifiers);

public static partial class WormholeEffects
{
    private const int EffectBeaconGroupId = 920;

    [GeneratedRegex("^Class ([1-6]) (.+) Effects$")]
    private static partial Regex BeaconName();

    /// <summary>The effect beacons by name (for example "Red Giant") and class (1 to 6).</summary>
    public static async Task<IReadOnlyDictionary<(string Name, int SystemClass), WormholeEffect>> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var effects = new Dictionary<(string, int), WormholeEffect>();
        var beacons = new Dictionary<int, (string Name, int SystemClass)>();
        foreach (var type in await sde.ReadByFieldAsync("types", "groupID", [EffectBeaconGroupId], cancellationToken))
        {
            var english = type.Doc.GetProperty("name").GetProperty("en").GetString() ?? string.Empty;
            if (BeaconName().Match(english) is { Success: true } match)
            {
                beacons[(int)type.Key] = (match.Groups[2].Value, int.Parse(match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        var attributeNames = new Dictionary<int, string>();
        foreach (var attribute in await sde.ReadAsync("dogmaAttributes", null, cancellationToken))
        {
            attributeNames[(int)attribute.Key] = attribute.Doc.GetProperty("name").GetString() ?? string.Empty;
        }
        foreach (var row in await sde.ReadAsync("typeDogma", beacons.Keys.Select(k => (long)k), cancellationToken))
        {
            var (name, systemClass) = beacons[(int)row.Key];
            var modifiers = new Dictionary<string, double>();
            foreach (var attribute in row.Doc.GetProperty("dogmaAttributes").EnumerateArray())
            {
                modifiers[attributeNames[attribute.GetProperty("attributeID").GetInt32()]] = attribute.GetProperty("value").GetDouble();
            }
            effects[(name, systemClass)] = new WormholeEffect((int)row.Key, name, systemClass, modifiers);
        }
        return effects.ToDictionary(pair => (pair.Key.Item1, pair.Key.Item2), pair => pair.Value);
    }
}
