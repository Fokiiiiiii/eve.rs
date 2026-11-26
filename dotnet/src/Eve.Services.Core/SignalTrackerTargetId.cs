namespace Eve.Services.Core;

/// <summary>
/// The scan target id of a signal tracker site, encoded as EVE.js does (targetIdRuntime.encodeTargetID): a hash of the "family:system:seed" text, written as
/// three capital letters, a dash and three digits.
/// </summary>
public static class SignalTrackerTargetId
{
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const uint LetterSpan = 26 * 26 * 26;

    public static string Encode(string family, long systemId, long seedId)
    {
        var normalizedFamily = string.IsNullOrWhiteSpace(family) ? "site" : family.Trim().ToLowerInvariant();
        var seed = $"{normalizedFamily}:{systemId}:{seedId}";
        uint state = 0;
        foreach (var character in seed)
            state = unchecked(state * 31 + character);

        var value = state % (LetterSpan * 1000);
        var letterValue = value / 1000;
        var digits = value % 1000;
        return $"{Letters[(int)(letterValue / (26 * 26))]}{Letters[(int)(letterValue / 26 % 26)]}{Letters[(int)(letterValue % 26)]}-{digits:000}";
    }
}
