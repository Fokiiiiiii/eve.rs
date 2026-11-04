using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eve.Host.PublicGateway;

internal readonly record struct GatewayTokenIdentity(long AccountId, long? CharacterId);

internal static class GatewayTokens
{
    private static readonly byte[] SigningKey = LoadSigningKey();
    private const string Header = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
    private const string SubjectPrefix = "USER:EVE:";
    private const string Issuer = "eve.rs";
    private const string Audience = "eve-public-gateway";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static string Issue(long accountId, long? characterId, DateTimeOffset now)
    {
        if (accountId <= 0)
            throw new ArgumentOutOfRangeException(nameof(accountId));
        if (characterId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        var issuedAt = now.ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = Issuer,
            aud = Audience,
            sub = SubjectPrefix + accountId.ToString(CultureInfo.InvariantCulture),
            character_id = characterId,
            iat = issuedAt,
            exp = now.Add(Lifetime).ToUnixTimeSeconds(),
            jti = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        });
        var unsigned = Header + "." + Base64UrlEncode(payload);
        var signature = HMACSHA256.HashData(SigningKey, Encoding.ASCII.GetBytes(unsigned));
        return unsigned + "." + Base64UrlEncode(signature);
    }

    public static bool TryReadIdentity(string? authorization, DateTimeOffset now, out GatewayTokenIdentity identity)
    {
        identity = default;
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
            if (!claims.TryGetProperty("iss", out var issuer) || issuer.ValueKind != JsonValueKind.String
                || issuer.GetString() != Issuer
                || !claims.TryGetProperty("aud", out var audience) || audience.ValueKind != JsonValueKind.String
                || audience.GetString() != Audience
                || !claims.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String
                || !claims.TryGetProperty("iat", out var issuedAt) || !issuedAt.TryGetInt64(out var iat)
                || !claims.TryGetProperty("exp", out var expiry) || !expiry.TryGetInt64(out var exp))
                return false;

            var nowSeconds = now.ToUnixTimeSeconds();
            if (iat > nowSeconds || exp <= nowSeconds || exp <= iat)
                return false;
            var subjectText = subject.GetString() ?? "";
            if (!subjectText.StartsWith(SubjectPrefix, StringComparison.Ordinal)
                || !long.TryParse(subjectText.AsSpan(SubjectPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
                || accountId <= 0)
                return false;

            long? characterId = null;
            if (claims.TryGetProperty("character_id", out var characterClaim) && characterClaim.ValueKind != JsonValueKind.Null)
            {
                if (!characterClaim.TryGetInt64(out var parsedCharacterId) || parsedCharacterId <= 0)
                    return false;
                characterId = parsedCharacterId;
            }

            if (claims.TryGetProperty("nbf", out var notBefore)
                && (!notBefore.TryGetInt64(out var nbf) || nbf > nowSeconds))
                return false;

            identity = new GatewayTokenIdentity(accountId, characterId);
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public static bool TryReadAccount(string? authorization, DateTimeOffset now, out long accountId)
    {
        accountId = 0;
        if (!TryReadIdentity(authorization, now, out var identity))
            return false;
        accountId = identity.AccountId;
        return true;
    }

    private static byte[] LoadSigningKey()
    {
        var encoded = Environment.GetEnvironmentVariable("EVE_PUBLIC_GATEWAY_TOKEN_KEY");
        if (string.IsNullOrWhiteSpace(encoded))
            return RandomNumberGenerator.GetBytes(32);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException("EVE_PUBLIC_GATEWAY_TOKEN_KEY must be base64 encoded.", error);
        }
        if (key.Length < 32)
            throw new InvalidOperationException("EVE_PUBLIC_GATEWAY_TOKEN_KEY must contain at least 32 bytes.");
        return key;
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
