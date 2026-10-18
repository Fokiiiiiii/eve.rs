using System.Globalization;
using System.Text.Json;

namespace Eve.Host.PublicGateway;

/// <summary>
/// Who a public gateway request is for. The identity the client puts in the request (the active character) is not trusted on its own: the
/// account is the subject of the bearer token the client sends (the login JWT, <c>USER:EVE:&lt;account&gt;</c>, the subject the client's own
/// token decoder reads), and the active character must belong to that account. The token's signature is not checked: the identity
/// service's signing key is not in this repository (DEVIATIONS, character phase S9).
/// </summary>
internal sealed class GatewayAuthority(Func<long, CancellationToken, Task<long?>> accountOfCharacter, Func<DateTimeOffset> now)
{
    private const string SubjectPrefix = "USER:EVE:";

    /// <summary>The account a bearer token belongs to, or null when the token is missing, not a JWT, not a user's, or expired.</summary>
    public long? AccountOf(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
            return null;
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : authorization.Trim();
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;
        try
        {
            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var claims = payload.RootElement;
            if (!claims.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String)
                return null;
            var text = subject.GetString() ?? "";
            if (!text.StartsWith(SubjectPrefix, StringComparison.Ordinal)
                || !long.TryParse(text.AsSpan(SubjectPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var account)
                || account <= 0)
                return null;
            if (claims.TryGetProperty("exp", out var expiry) && expiry.ValueKind == JsonValueKind.Number
                && expiry.GetInt64() < now().ToUnixTimeSeconds())
                return null;
            return account;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The refusal of a request for a character: 401 without a user's token, 403 when the character is not on the token's account (or does not
    /// exist). Null when the request may proceed.
    /// </summary>
    public async Task<GatewayAnswer?> RefusalAsync(long? account, long? character, CancellationToken cancellationToken)
    {
        if (account is null)
            return new GatewayAnswer(401, "a user token is required", "", []);
        if (character is { } id && await accountOfCharacter(id, cancellationToken) != account)
            return new GatewayAnswer(403, "the character is not on the token's account", "", []);
        return null;
    }

    /// <summary>Whether a character belongs to an account (for the notice stream, which only routes what the identity may see).</summary>
    public async Task<bool> OwnsAsync(long account, long character, CancellationToken cancellationToken) =>
        await accountOfCharacter(character, cancellationToken) == account;

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
