using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eve.Identity;

/// <summary>Settings for verifying EVE SSO access tokens.</summary>
public sealed record SsoTokenVerifierOptions(Uri MetadataAddress, string Issuer, IReadOnlyList<string> Audiences);

/// <summary>
/// Verifies RS256 EVE SSO access tokens using the issuer's OAuth discovery document and JWKS.
/// The raw token and its claims are never included in exceptions or diagnostics.
/// </summary>
public sealed class SsoTokenVerifier : ISsoTokenVerifier
{
    private const int MaximumTokenLength = 32_768;
    private static readonly TimeSpan KeyCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _httpClient;
    private readonly SsoTokenVerifierOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private SigningKeyCache? _cache;

    private sealed record SigningKeyCache(DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, RSAParameters> Keys);

    public SsoTokenVerifier(SsoTokenVerifierOptions options, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Issuer) || options.Issuer.Length > 512)
            throw new ArgumentException("An SSO issuer is required.", nameof(options));
        if (options.Audiences.Count < 2 || options.Audiences.Any(string.IsNullOrWhiteSpace) ||
            !options.Audiences.Contains("EVE Online", StringComparer.Ordinal) ||
            options.Audiences.Distinct(StringComparer.Ordinal).Count() != options.Audiences.Count)
            throw new ArgumentException("SSO audiences must include EVE Online and the configured application audience.", nameof(options));
        ValidateEndpoint(options.MetadataAddress);
        _options = options;
        _httpClient = httpClient ?? SharedHttpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates the production verifier when EVE_SSO_AUDIENCES is configured. The configured list must include
    /// the static EVE Online audience and the client ID assigned to the EVE client application.
    /// </summary>
    public static SsoTokenVerifier? FromEnvironment()
    {
        var configuredAudiences = Environment.GetEnvironmentVariable("EVE_SSO_AUDIENCES");
        if (string.IsNullOrWhiteSpace(configuredAudiences))
            return null;

        var audiences = configuredAudiences.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        var issuer = Environment.GetEnvironmentVariable("EVE_SSO_ISSUER");
        issuer = string.IsNullOrWhiteSpace(issuer) ? "https://login.eveonline.com/" : issuer;
        var metadata = Environment.GetEnvironmentVariable("EVE_SSO_METADATA_URL");
        var metadataAddress = string.IsNullOrWhiteSpace(metadata)
            ? new Uri("https://login.eveonline.com/.well-known/oauth-authorization-server", UriKind.Absolute)
            : Uri.TryCreate(metadata, UriKind.Absolute, out var configuredAddress)
                ? configuredAddress
                : throw new InvalidOperationException("EVE_SSO_METADATA_URL must be an absolute URL.");
        return new SsoTokenVerifier(new SsoTokenVerifierOptions(metadataAddress, issuer, audiences));
    }

    public async ValueTask<VerifiedSsoIdentity?> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
            return null;

        var parts = token.Split('.');
        if (parts.Length != 3 || !TryDecode(parts[0], out var headerBytes) || !TryDecode(parts[1], out var payloadBytes) ||
            !TryDecode(parts[2], out var signature))
            return null;

        string? keyId;
        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            var root = header.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TrySingleProperty(root, "alg", out var algorithm) || algorithm.ValueKind != JsonValueKind.String ||
                algorithm.GetString() != "RS256" ||
                !TrySingleProperty(root, "kid", out var keyIdValue) || keyIdValue.ValueKind != JsonValueKind.String)
                return null;
            keyId = keyIdValue.GetString();
            if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 256)
                return null;
        }
        catch (JsonException)
        {
            return null;
        }

        var keys = await GetSigningKeysAsync(forceRefresh: false, cancellationToken);
        if (!keys.TryGetValue(keyId, out var signingKey))
        {
            keys = await GetSigningKeysAsync(forceRefresh: true, cancellationToken);
            if (!keys.TryGetValue(keyId, out signingKey))
                return null;
        }

        bool signatureValid;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(signingKey);
            signatureValid = rsa.VerifyData(
                Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return null;
        }
        if (!signatureValid)
            return null;

        try
        {
            using var payload = JsonDocument.Parse(payloadBytes);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TrySingleProperty(root, "iss", out var issuerValue) || issuerValue.ValueKind != JsonValueKind.String ||
                !IssuerMatches(issuerValue.GetString()!) ||
                !TrySingleProperty(root, "sub", out var subjectValue) || subjectValue.ValueKind != JsonValueKind.String ||
                !TrySingleProperty(root, "aud", out var audienceValue) ||
                !HasRequiredAudiences(audienceValue) ||
                !TrySingleProperty(root, "exp", out var expiresValue) || !expiresValue.TryGetInt64(out var expiresAt))
                return null;

            var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (expiresAt <= now)
                return null;
            var notBeforeCount = root.EnumerateObject().Count(property => property.NameEquals("nbf"));
            if (notBeforeCount > 1)
                return null;
            if (notBeforeCount == 1 &&
                (!TrySingleProperty(root, "nbf", out var notBeforeValue) ||
                 !notBeforeValue.TryGetInt64(out var notBefore) || notBefore > now))
                return null;

            var identity = new VerifiedSsoIdentity(_options.Issuer, subjectValue.GetString()!);
            return SsoIdentityRules.IsAcceptable(identity) ? identity : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, RSAParameters>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var current = _cache;
        if (!forceRefresh && current is not null && current.ExpiresAt > _timeProvider.GetUtcNow())
            return current.Keys;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            current = _cache;
            if (!forceRefresh && current is not null && current.ExpiresAt > _timeProvider.GetUtcNow())
                return current.Keys;

            var metadataJson = await _httpClient.GetStringAsync(_options.MetadataAddress, cancellationToken);
            using var metadata = JsonDocument.Parse(metadataJson);
            if (!TrySingleProperty(metadata.RootElement, "issuer", out var metadataIssuer) ||
                metadataIssuer.ValueKind != JsonValueKind.String ||
                !IssuerMatches(metadataIssuer.GetString()!) ||
                !TrySingleProperty(metadata.RootElement, "jwks_uri", out var jwksValue) ||
                jwksValue.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(jwksValue.GetString(), UriKind.Absolute, out var jwksAddress))
                throw new InvalidOperationException("The SSO discovery document is invalid for the configured issuer.");
            ValidateEndpoint(jwksAddress);

            var jwksJson = await _httpClient.GetStringAsync(jwksAddress, cancellationToken);
            using var jwks = JsonDocument.Parse(jwksJson);
            if (!TrySingleProperty(jwks.RootElement, "keys", out var keysValue) || keysValue.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The SSO signing-key document is invalid.");

            var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
            foreach (var key in keysValue.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object ||
                    !TrySingleProperty(key, "kid", out var kidValue) || kidValue.ValueKind != JsonValueKind.String ||
                    !TrySingleProperty(key, "kty", out var typeValue) || typeValue.ValueKind != JsonValueKind.String ||
                    typeValue.GetString() != "RSA" ||
                    (TrySingleProperty(key, "use", out var useValue) && useValue.ValueKind == JsonValueKind.String && useValue.GetString() != "sig") ||
                    (TrySingleProperty(key, "alg", out var algValue) && algValue.ValueKind == JsonValueKind.String && algValue.GetString() != "RS256") ||
                    !TrySingleProperty(key, "n", out var modulusValue) || modulusValue.ValueKind != JsonValueKind.String ||
                    !TrySingleProperty(key, "e", out var exponentValue) || exponentValue.ValueKind != JsonValueKind.String)
                    continue;

                var kid = kidValue.GetString();
                if (string.IsNullOrWhiteSpace(kid) || kid.Length > 256 ||
                    !TryDecode(modulusValue.GetString()!, out var modulus) ||
                    !TryDecode(exponentValue.GetString()!, out var exponent) ||
                    modulus.Length < 256 || exponent.Length == 0)
                    continue;
                if (!keys.TryAdd(kid, new RSAParameters { Modulus = modulus, Exponent = exponent }))
                    throw new InvalidOperationException("The SSO signing-key document contains duplicate key identifiers.");
            }

            if (keys.Count == 0)
                throw new InvalidOperationException("The SSO signing-key document contains no usable RSA keys.");
            var cache = new SigningKeyCache(_timeProvider.GetUtcNow() + KeyCacheDuration, keys);
            _cache = cache;
            return cache.Keys;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private bool IssuerMatches(string issuer)
    {
        if (string.Equals(issuer, _options.Issuer, StringComparison.Ordinal))
            return true;
        return string.Equals(_options.Issuer, "https://login.eveonline.com/", StringComparison.Ordinal) &&
               issuer is "https://login.eveonline.com" or "login.eveonline.com";
    }

    private bool HasRequiredAudiences(JsonElement audience)
    {
        IEnumerable<string> values = audience.ValueKind switch
        {
            JsonValueKind.String => [audience.GetString()!],
            JsonValueKind.Array when audience.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String) =>
                audience.EnumerateArray().Select(value => value.GetString()!),
            _ => [],
        };
        var actual = values.ToHashSet(StringComparer.Ordinal);
        return _options.Audiences.All(actual.Contains);
    }

    private static bool TrySingleProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal))
                continue;
            if (found)
                return false;
            value = property.Value;
            found = true;
        }
        return found;
    }

    private static bool TryDecode(string value, out byte[] decoded)
    {
        decoded = [];
        if (value.Length == 0 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        try
        {
            decoded = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address))))
            throw new ArgumentException("SSO metadata and signing-key URLs must use HTTPS (loopback HTTP is reserved for tests).", nameof(endpoint));
    }
}
