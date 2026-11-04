using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eve.Host.ProjectDiscovery;

// Protocol: https://github.com/MassivelyMultiplayerOnlineScience/mmos-sdk-csharp
// doc/authentication/api-hmac-authentication.md and MMOS.SDK/V2/Authentication.cs.
public sealed record MmosApiOptions
{
    public bool Enabled { get; init; }
    public Uri? BaseUri { get; init; }
    public string? Credential { get; init; }
    public string? Secret { get; init; }
    public int MaxResponseBytes { get; init; } = 1024 * 1024;
}

public sealed class MmosHttpException(HttpStatusCode statusCode, JsonElement? error) : Exception($"MMOS returned HTTP {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public JsonElement? Error { get; } = error;
}

// Once transmission begins, cancellation or transport/response failure cannot establish
// whether a classification was committed remotely. Callers must reconcile, not retry blindly.
public sealed class MmosSubmissionOutcomeUnknownException(Exception inner) : Exception("MMOS classification outcome is unknown.", inner);
public sealed class MmosResponseException(string message) : Exception(message);
public sealed record MmosTaskResponse(string Uid, string Game, JsonElement Player, JsonElement Task);
public sealed record MmosPlayerResponse(string Uid, string Game, JsonElement Player, JsonElement? Project, JsonElement Raw);
public sealed record MmosClassificationResponse(string Uid, JsonElement Player, JsonElement Task, JsonElement Raw);

// This client does not retry; callers must not inject an HTTP retry handler for submissions.
public sealed class MmosApiClient
{
    private const string Algorithm = "MMOS1-HMAC-SHA256";
    private readonly HttpClient http;
    private readonly MmosApiOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<string> nonce;
    public MmosApiClient(HttpClient http, MmosApiOptions options, Func<DateTimeOffset>? clock = null, Func<string>? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) throw new InvalidOperationException("MMOS is disabled.");
        if (options.BaseUri is null || !options.BaseUri.IsAbsoluteUri || (options.BaseUri.Scheme != Uri.UriSchemeHttps && !options.BaseUri.IsLoopback) || options.BaseUri.Query.Length != 0 || options.BaseUri.Fragment.Length != 0 || options.BaseUri.AbsolutePath != "/")
            throw new ArgumentException("MMOS base URI must be an HTTPS origin (HTTP loopback is allowed for fixtures).", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Credential) || string.IsNullOrWhiteSpace(options.Secret) || options.MaxResponseBytes <= 0)
            throw new ArgumentException("MMOS credentials and a positive response limit are required.", nameof(options));
        this.http = http; this.options = options;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.nonce = nonce ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async Task<MmosTaskResponse> AllocateTaskAsync(string game, string player, string project, string accountCode, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { projects = new[] { project }, player = new { accountCode } });
        var json = await SendAsync(HttpMethod.Post, $"/games/{Part(game)}/players/{Part(player)}/tasks", body, false, cancellationToken);
        return new(RequiredString(json, "uid"), RequiredString(json, "game"), Required(json, "player"), Required(json, "task"));
    }
    public async Task<MmosPlayerResponse?> GetPlayerAsync(string game, string player, string project, CancellationToken cancellationToken = default)
    {
        var json = await SendAsync(HttpMethod.Get, $"/games/{Part(game)}/players/{Part(player)}?project={Part(project)}", null, false, cancellationToken, true);
        if (json.ValueKind == JsonValueKind.Undefined) return null;
        return new(RequiredString(json, "uid"), RequiredString(json, "game"), Required(json, "player"), json.TryGetProperty("project", out var p) ? p.Clone() : null, json);
    }
    public async Task<MmosClassificationResponse> SubmitClassificationAsync(string game, string player, string? playerGroup, string taskId, JsonElement result, JsonElement circumstances, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { game, player, playergroup = playerGroup, task = new { id = taskId, result }, circumstances });
        var json = await SendAsync(HttpMethod.Post, "/classifications", body, true, cancellationToken);
        try { return new(RequiredString(json, "uid"), Required(json, "player"), Required(json, "task"), json); }
        catch (MmosResponseException ex) { throw new MmosSubmissionOutcomeUnknownException(ex); }
    }
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, byte[]? body, bool classification, CancellationToken ct, bool allowNotFound = false)
    {
        ct.ThrowIfCancellationRequested();
        var uri = new Uri(options.BaseUri!, path);
        string timestamp = clock().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        string n = nonce();
        string canonical = string.Join("|", Algorithm, options.Credential!, timestamp, n, method.Method, uri.PathAndQuery, body is null ? "{}" : Encoding.UTF8.GetString(body));
        byte[] key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(timestamp), Encoding.UTF8.GetBytes(options.Secret!));
        string hex = Convert.ToHexStringLower(key);
        string signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hex), Encoding.UTF8.GetBytes(canonical)));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-MMOS-Algorithm", Algorithm);
        request.Headers.Add("X-MMOS-Credential", options.Credential!);
        request.Headers.Add("X-MMOS-Timestamp", timestamp);
        request.Headers.Add("X-MMOS-Nonce", n);
        request.Headers.Add("X-MMOS-Signature", signature);
        if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" }; }
        ct.ThrowIfCancellationRequested();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return default;
            byte[] bytes = await ReadBoundedAsync(response.Content, options.MaxResponseBytes, ct);
            JsonElement parsed = default;
            if (bytes.Length != 0)
            {
                try { using var doc = JsonDocument.Parse(bytes); parsed = doc.RootElement.Clone(); }
                catch (JsonException) { if (response.IsSuccessStatusCode) throw new MmosResponseException("Invalid MMOS JSON response."); }
            }
            if (!response.IsSuccessStatusCode) throw new MmosHttpException(response.StatusCode, parsed.ValueKind == JsonValueKind.Undefined ? null : parsed);
            if (parsed.ValueKind != JsonValueKind.Object) throw new MmosResponseException("Invalid MMOS response envelope.");
            return parsed;
        }
        catch (MmosHttpException) { throw; }
        catch (Exception ex) when (classification && (ex is HttpRequestException or IOException or OperationCanceledException or MmosResponseException))
        { throw new MmosSubmissionOutcomeUnknownException(ex); }
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long n && n > limit) throw new MmosResponseException("MMOS response exceeds the configured limit.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        byte[] buffer = new byte[Math.Min(limit, 8192)];
        while (true) { int read = await stream.ReadAsync(buffer, ct); if (read == 0) break; if (output.Length + read > limit) throw new MmosResponseException("MMOS response exceeds the configured limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static string Part(string value) => Uri.EscapeDataString(value);
    private static JsonElement Required(JsonElement json, string key) => json.TryGetProperty(key, out var value) ? value.Clone() : throw new MmosResponseException($"MMOS response lacks {key}.");
    private static string RequiredString(JsonElement json, string key) { var value = Required(json, key); return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new MmosResponseException($"MMOS response has invalid {key}."); }
}
