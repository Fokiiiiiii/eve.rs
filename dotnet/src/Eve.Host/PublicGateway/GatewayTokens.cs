using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eve.Host.PublicGateway;

internal static class GatewayTokens
{
    private static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);
    private const string Header = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
    private const string SubjectPrefix = "USER:EVE:";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static string Issue(long accountId, long? characterId, DateTimeOffset now)
    {
        if (accountId <= 0)
            throw new ArgumentOutOfRangeException(nameof(accountId));
        if (characterId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = SubjectPrefix + accountId.ToString(CultureInfo.InvariantCulture),
            character_id = characterId,
            iat = now.ToUnixTimeSeconds(),
            exp = now.Add(Lifetime).ToUnixTimeSeconds(),
            jti = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        });
        var unsigned = Header + "." + Base64UrlEncode(payload);
        var signature = HMACSHA256.HashData(SigningKey, Encoding.ASCII.GetBytes(unsigned));
        return unsigned + "." + Base64UrlEncode(signature);
    }

    public static bool TryReadAccount(string? authorization, DateTimeOffset now, out long accountId)
    {
        accountId = 0;
        if (string.IsNullOrWhiteSpace(authorization))
            return false;
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : authorization.Trim();
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Header)
            return false;
        try
        {
            var expected = HMACSHA256.HashData(SigningKey, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            var supplied = Base64UrlDecode(parts[2]);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
                return false;
            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var claims = payload.RootElement;
            if (!claims.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String
                || !claims.TryGetProperty("exp", out var expiry) || !expiry.TryGetInt64(out var exp)
                || exp <= now.ToUnixTimeSeconds())
                return false;
            var text = subject.GetString() ?? "";
            if (!text.StartsWith(SubjectPrefix, StringComparison.Ordinal)
                || !long.TryParse(text.AsSpan(SubjectPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id <= 0)
                return false;
            accountId = id;
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
