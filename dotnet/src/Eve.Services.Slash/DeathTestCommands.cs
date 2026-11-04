using System.Globalization;
using System.Text.RegularExpressions;
using Eve.Services;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/deathtest [ship] [count]</c>: hulls that nobody flies come into space around the pilot and blow up a moment later, so that what a client sees when a ship
/// dies can be looked at. The words the reference answers with are its own.
/// </summary>
public sealed partial class DeathTestCommands(ISpaceHulls space, ShipTypeNames names)
{
    private const int DefaultCount = 6;

    /// <summary><c>parseOptionalTypeAndCount</c> of the reference's <c>commandArguments.js</c>: only digits are a count; otherwise a trailing amount is the count and the rest a name.</summary>
    public static (string TypeName, int? Count) Parse(string argument)
    {
        var trimmed = argument.Trim();
        if (trimmed.Length == 0)
        {
            return ("", null);
        }
        if (Digits().IsMatch(trimmed))
        {
            return ("", PositiveInteger(double.Parse(trimmed, CultureInfo.InvariantCulture)));
        }
        var parts = Whitespace().Split(trimmed);
        if (parts.Length > 1 && ParseAmount(parts[^1]) is { } amount)
        {
            var lookup = string.Join(' ', parts[..^1]).Trim();
            if (lookup.Length > 0)
            {
                return (lookup, PositiveInteger(Math.Truncate(amount)));
            }
        }
        return (trimmed, null);
    }

    private static int? PositiveInteger(double value) => value is > 0 and < int.MaxValue && value == Math.Floor(value) ? (int)value : null;

    /// <summary><c>parseAmount</c>: a number with an optional k, m, b or t suffix; commas and underscores are ignored.</summary>
    internal static double? ParseAmount(string value)
    {
        var match = Amount().Match(value.Trim().Replace(",", "").Replace("_", ""));
        if (!match.Success)
        {
            return null;
        }
        var multiplier = match.Groups[2].Value.ToLowerInvariant() switch { "k" => 1e3, "m" => 1e6, "b" => 1e9, "t" => 1e12, _ => 1.0 };
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * multiplier;
    }

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?)([kmbt])?$", RegexOptions.IgnoreCase)]
    private static partial Regex Amount();

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.GetInt("solarsystemid") is null || session.GetInt("stationid") is not null)
        {
            return "You must be in space before using /deathtest.";
        }
        var (typeName, count) = Parse(argument);
        int? typeId = null;
        if (typeName.Length > 0)
        {
            var found = await names.FindAsync(typeName, cancellationToken);
            if (found.TypeId is not { } id)
            {
                var suggestions = found.Suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", found.Suggestions)}?" : "";
                return found.Error == "SHIP_NOT_FOUND"
                    ? $"Ship type not found: {typeName}.{suggestions}".Trim()
                    : $"Ship type is ambiguous: {typeName}.{suggestions}".Trim();
            }
            typeId = id;
        }
        var field = await space.DeathTestAsync(session, typeId, count ?? DefaultCount, cancellationToken);
        if (field is null)
        {
            return "Death-test hull spawning failed.";
        }
        return $"Spawned {field.Spawned} {field.ShipName} hulls across {field.RadiusMeters / 1000:0} km. Detonation in {field.DelaySeconds.ToString("0.0", CultureInfo.InvariantCulture)}s sim time.";
    }
}
