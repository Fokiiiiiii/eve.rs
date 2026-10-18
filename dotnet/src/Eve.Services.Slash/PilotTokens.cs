using System.Globalization;
using System.Text.RegularExpressions;
using Eve.Characters;

namespace Eve.Services.Slash;

/// <summary>The character a game master's command names: <c>me</c> (the caller), a character ID, or an exact character name.</summary>
public static class PilotTokens
{
    private static readonly Regex Operand = new("\"([^\"]*)\"|'([^']*)'|(\\S+)", RegexOptions.Compiled);

    /// <summary>The operands of a command: a quoted operand keeps its spaces, any other ends at a space.</summary>
    public static IReadOnlyList<string> Operands(string argument) =>
        Operand.Matches(argument).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value)
            .Where(operand => operand.Length > 0).ToList();

    public static async Task<(long? Id, string? Error)> ResolveAsync(PgCharacterStore characters, ClientSession session, string token, CancellationToken cancellationToken)
    {
        if (token.Equals("me", StringComparison.OrdinalIgnoreCase))
            return session.CharacterId is { } self ? (self, null) : (null, "Select a character before using me.");
        if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            return await characters.GetAsync(id, cancellationToken) is null ? (null, $"Character not found: {token}.") : (id, null);
        return await characters.FindIdByNameAsync(token, cancellationToken) is { } named
            ? (named, null)
            : (null, $"Character not found: {token}. Use me, a character ID, or an exact character name.");
    }
}
