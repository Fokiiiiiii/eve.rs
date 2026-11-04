using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eve.Services;
using Npgsql;

namespace Eve.Host.ProjectDiscovery;

public sealed record ScientificReplayOptions
{
    // FRDR DOI 10.20383/103.01043, Brinkman & Yokosawa (2025), CC BY 4.0.
    // Analysis 50 data and the 30-reference cohort are bundled without alteration.
    public static ScientificReplayOptions Bundled => new()
    {
        Enabled = true, DatasetKey = "frdr-1038-analysis-50",
        CsvGzipSha256 = "a458f60d4e299a8c5ca31626452be1f46e82bc2b05fde6611fae3ab87e905425",
        CohortSha256 = "9810f2bb0c77eb4f8f17e7ddb624f12a0f2218b94f690d8b6e1833fe23ff1539",
        MinX = 23.201007270813, MinY = -30.203549385070783,
        MaxX = 128.337999916077, MaxY = 196.826071739197
    };
    public System.Net.IPAddress? ListenAddress { get; init; }
    public int ListenPort { get; init; }
    public bool AllowNonLoopback { get; init; }
    public Uri? AdvertisedOrigin { get; init; }
    public ProjectDiscoveryBonusPolicy? Bonus { get; init; }
    public bool Enabled { get; init; }
    public string DatasetKey { get; init; } = "";
    public string CsvGzipPath { get; init; } = "";
    public string CsvGzipSha256 { get; init; } = "";
    public string CohortPath { get; init; } = "";
    public string CohortSha256 { get; init; } = "";
    public Uri? AssetUri { get; init; }
    public string AssetSha256 { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    // Must match the coordinate bounds used to render AssetUri.
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }
}
public sealed class ScientificReplayExhaustedException() : Exception("This scientific replay dataset has no remaining tasks.");

// Local research replay. This is not MMOS or live Project Discovery scoring or rewards.
public sealed class ScientificReplayProvider
{
    private readonly NpgsqlDataSource source;
    private readonly ScientificReplayOptions options;
    private readonly string schema;
    private readonly ScientificPoint[] points;
    private readonly int[][] cohort;
    private readonly string datasetRevision;
    private readonly int gateCount;
    private ScientificReplayProvider(NpgsqlDataSource source, ScientificReplayOptions options, string schema, ScientificPoint[] points, int[][] cohort, int gateCount)
    { this.source = source; this.options = options; this.schema = schema; this.points = points; this.cohort = cohort; this.gateCount = gateCount; datasetRevision = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { csv = options.CsvGzipSha256.ToLowerInvariant(), cohort = options.CohortSha256.ToLowerInvariant(), asset = options.AssetSha256.ToLowerInvariant(), options.Width, options.Height, options.MinX, options.MinY, options.MaxX, options.MaxY }))); }
    public static async Task<ScientificReplayProvider> CreateAsync(NpgsqlDataSource source, ScientificReplayOptions options, string schema = "project_discovery", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.DatasetKey) || !Regex.IsMatch(options.AssetSha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant) || options.AssetUri is null || !options.AssetUri.IsAbsoluteUri || (options.AssetUri.Scheme != Uri.UriSchemeHttp && options.AssetUri.Scheme != Uri.UriSchemeHttps) || options.Width <= 0 || options.Height <= 0 || !(options.MaxX > options.MinX) || !(options.MaxY > options.MinY)) throw new ArgumentException("Explicit valid scientific replay configuration is required.", nameof(options));
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid schema.", nameof(schema));
        byte[] compressed = await ReadDatasetAsync(options.CsvGzipPath, "covid-analysis50.csv.gz", ct);
        byte[] annotations = await ReadDatasetAsync(options.CohortPath, "covid-analysis50-gates.json", ct);
        if (!HashMatches(compressed, options.CsvGzipSha256) || !HashMatches(annotations, options.CohortSha256)) throw new InvalidDataException("Scientific replay dataset hash mismatch.");
        var points = new List<ScientificPoint>();
        using (var input = new MemoryStream(compressed, writable: false))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
        {
            if (await reader.ReadLineAsync(ct) != "\"GATA3\",\"CD4\"") throw new InvalidDataException("Unexpected scientific dataset columns.");
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                string[] cells = line.Split(',');
                if (cells.Length != 2 || !double.TryParse(cells[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) || !double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidDataException("Invalid scientific point.");
                points.Add(new(x, y));
            }
        }
        if (points.Count == 0) throw new InvalidDataException("Scientific dataset has no points.");
        using var doc = JsonDocument.Parse(annotations); var records = doc.RootElement;
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() < 2) throw new InvalidDataException("Scientific dataset needs a cohort.");
        var pointArray = points.ToArray(); var cohort = new List<int[]>(); int gateCount = 0;
        foreach (var record in records.EnumerateArray())
        {
            var bounds = record.GetProperty("minMax").EnumerateArray().Select(n => n.GetDouble()).ToArray();
            if (bounds.Length != 4) throw new InvalidDataException("Invalid reference bounds.");
            var polygons = ParsePolygons(record.GetProperty("polygons"), false);
            if (gateCount == 0) gateCount = polygons.Count;
            if (polygons.Count != gateCount) throw new InvalidDataException("Mixed reference gate counts are not supported.");
            cohort.Add(ScientificReplayScorer.Label(pointArray, new(bounds[0], bounds[1], bounds[2], bounds[3], polygons)));
        }
        return new(source, options, schema, pointArray, cohort.ToArray(), gateCount);
    }
    public Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default) => Eve.Data.Migrations.ApplyAsync(source, schema,
    [new Eve.Data.Migration("0002_scientific_replay", """
        CREATE TABLE IF NOT EXISTS scientific_assignments (
            actor bigint NOT NULL, account bigint NOT NULL, dataset_key text NOT NULL, revision text NOT NULL,
            state text NOT NULL CHECK(state IN ('active','completed','skipped')),
            PRIMARY KEY(actor, dataset_key));
        CREATE TABLE IF NOT EXISTS scientific_submissions (
            actor bigint NOT NULL, dataset_key text NOT NULL, call_id text NOT NULL,
            fingerprint text NOT NULL, response jsonb NOT NULL,
            PRIMARY KEY(actor, dataset_key, call_id), UNIQUE(actor, dataset_key),
            FOREIGN KEY(actor, dataset_key) REFERENCES scientific_assignments(actor, dataset_key));
        """)], ct);
    public async ValueTask<Eve.Services.Core.ProjectDiscoveryPlayerStatistics?> ReadStatisticsAsync(long actor, CancellationToken ct)
    {
        if (actor <= 0) return null;
        await using var cmd = source.CreateCommand($"SELECT s.response::text FROM {schema}.scientific_submissions s JOIN {schema}.scientific_assignments a ON a.actor=s.actor AND a.dataset_key=s.dataset_key WHERE s.actor=$1 AND s.dataset_key=$2 AND a.revision=$3 AND a.state='completed'");
        cmd.Parameters.AddWithValue(actor); cmd.Parameters.AddWithValue(options.DatasetKey); cmd.Parameters.AddWithValue(datasetRevision);
        if (await cmd.ExecuteScalarAsync(ct) is not string json) return null;
        var value = Parse(json);
        if (!value.TryGetProperty("player", out var player) || !player.TryGetProperty("score", out var score)) return null;
        return new("", 0, "mcgill-polyseg", score.GetDouble());
    }
    public async Task<JsonElement> GetTaskAsync(ClientSession session, CancellationToken ct = default)
    {
        (long actor, long account) = Identity(session);
        await using var conn = await source.OpenConnectionAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAsync(conn, tx, actor, ct);
        await using (var insert = new NpgsqlCommand($"INSERT INTO {schema}.scientific_assignments(actor,account,dataset_key,revision,state) VALUES($1,$2,$3,$4,'active') ON CONFLICT DO NOTHING", conn, tx))
        { insert.Parameters.AddWithValue(actor); insert.Parameters.AddWithValue(account); insert.Parameters.AddWithValue(options.DatasetKey); insert.Parameters.AddWithValue(datasetRevision); await insert.ExecuteNonQueryAsync(ct); }
        await using (var cmd = new NpgsqlCommand($"SELECT account,revision,state FROM {schema}.scientific_assignments WHERE actor=$1 AND dataset_key=$2", conn, tx))
        {
            cmd.Parameters.AddWithValue(actor); cmd.Parameters.AddWithValue(options.DatasetKey); await using var reader = await cmd.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            if (reader.GetInt64(0) != account || reader.GetString(1) != datasetRevision) throw new DiscoveryAssignmentException("Scientific assignment identity or revision mismatch.");
            if (reader.GetString(2) != "active") throw new ScientificReplayExhaustedException();
        }
        await tx.CommitAsync(ct);
        return JsonSerializer.SerializeToElement(new { assets = new { url = options.AssetUri!.AbsoluteUri, dimensions = new[] { options.Width, options.Height } } });
    }
    public async Task<JsonElement> SubmitAsync(ClientSession session, string callId, JsonElement result, JsonElement circumstances, CancellationToken ct = default)
    {
        (long actor, long account) = Identity(session);
        if (string.IsNullOrWhiteSpace(callId)) throw new ArgumentException("Call ID is required.", nameof(callId));
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("skip", out var skip) || (skip.ValueKind != JsonValueKind.True && skip.ValueKind != JsonValueKind.False)) throw new ArgumentException("A skip flag is required.", nameof(result));
        bool isSkip = skip.GetBoolean();
        IReadOnlyList<IReadOnlyList<ScientificPoint>>? polygons = null;
        if (!isSkip)
        {
            if (!result.TryGetProperty("polygons", out var jsonPolygons)) throw new ArgumentException("Polygons are required.", nameof(result));
            polygons = ParsePolygons(jsonPolygons, true); if (polygons.Count != gateCount) throw new ArgumentException("Submission gate count differs from the fixed research cohort.", nameof(result));
        }
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { result, circumstances })));
        await using var conn = await source.OpenConnectionAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAsync(conn, tx, actor, ct);
        await using (var find = new NpgsqlCommand($"SELECT s.fingerprint,s.response::text FROM {schema}.scientific_submissions s JOIN {schema}.scientific_assignments a ON a.actor=s.actor AND a.dataset_key=s.dataset_key WHERE s.actor=$1 AND s.dataset_key=$2 AND s.call_id=$3 AND a.account=$4 AND a.revision=$5", conn, tx))
        {
            find.Parameters.AddWithValue(actor); find.Parameters.AddWithValue(options.DatasetKey); find.Parameters.AddWithValue(callId); find.Parameters.AddWithValue(account); find.Parameters.AddWithValue(datasetRevision); await using var reader = await find.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) { if (reader.GetString(0) != fingerprint) throw new DiscoveryAssignmentException("Call ID reused with different payload."); string saved = reader.GetString(1); await reader.DisposeAsync(); await tx.CommitAsync(ct); return Parse(saved); }
        }
        await using (var current = new NpgsqlCommand($"SELECT account,revision,state FROM {schema}.scientific_assignments WHERE actor=$1 AND dataset_key=$2", conn, tx))
        {
            current.Parameters.AddWithValue(actor); current.Parameters.AddWithValue(options.DatasetKey); await using var reader = await current.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new DiscoveryAssignmentException("No scientific assignment.");
            if (reader.GetInt64(0) != account || reader.GetString(1) != datasetRevision) throw new DiscoveryAssignmentException("Scientific assignment identity or revision mismatch.");
            if (reader.GetString(2) != "active") throw new ScientificReplayExhaustedException();
        }
        ct.ThrowIfCancellationRequested();
        JsonElement response;
        if (isSkip) response = JsonSerializer.SerializeToElement(new { skipped = true });
        else
        {
            int[] answer = ScientificReplayScorer.Label(points, new(options.MinX, options.MinY, options.MaxX, options.MaxY, polygons!));
            double score = ScientificReplayScorer.ScoreNew(answer, cohort, gateCount);
            response = JsonSerializer.SerializeToElement(new { player = new { score }, task = new { } });
        }
        long awardedXp = !isSkip && options.Bonus is { } bonus
            ? await bonus.AwardAsync(conn, tx, actor, $"scientific:{options.DatasetKey}:{datasetRevision}", ct) : 0;
        response = ProjectDiscoveryRewardResponse.Decorate(response, awardedXp);
        await using (var insert = new NpgsqlCommand($"INSERT INTO {schema}.scientific_submissions(actor,dataset_key,call_id,fingerprint,response) VALUES($1,$2,$3,$4,$5::jsonb)", conn, tx))
        { insert.Parameters.AddWithValue(actor); insert.Parameters.AddWithValue(options.DatasetKey); insert.Parameters.AddWithValue(callId); insert.Parameters.AddWithValue(fingerprint); insert.Parameters.AddWithValue(response.GetRawText()); await insert.ExecuteNonQueryAsync(ct); }
        await using (var done = new NpgsqlCommand($"UPDATE {schema}.scientific_assignments SET state=$3 WHERE actor=$1 AND dataset_key=$2", conn, tx))
        { done.Parameters.AddWithValue(actor); done.Parameters.AddWithValue(options.DatasetKey); done.Parameters.AddWithValue(isSkip ? "skipped" : "completed"); await done.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct); return response;
    }
    private static IReadOnlyList<IReadOnlyList<ScientificPoint>> ParsePolygons(JsonElement json, bool objects)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() == 0 || json.GetArrayLength() > 32) throw new ArgumentException("Invalid polygon collection.");
        var result = new List<IReadOnlyList<ScientificPoint>>();
        foreach (var item in json.EnumerateArray())
        {
            JsonElement vertices = objects ? item.GetProperty("vertices") : item;
            if (vertices.ValueKind != JsonValueKind.Array || vertices.GetArrayLength() < 3 || vertices.GetArrayLength() > 4096) throw new ArgumentException("Invalid polygon vertices.");
            var points = new List<ScientificPoint>();
            foreach (var point in vertices.EnumerateArray())
            { if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2) throw new ArgumentException("Invalid vertex."); double x = point[0].GetDouble(), y = point[1].GetDouble(); if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Non-finite vertex."); points.Add(new(x,y)); }
            result.Add(points);
        }
        return result;
    }
    private async Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long actor, CancellationToken ct)
    { await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", conn, tx); cmd.Parameters.AddWithValue($"pd-science:{schema}:{options.DatasetKey}:{actor}"); await cmd.ExecuteNonQueryAsync(ct); }
    private static (long Actor, long Account) Identity(ClientSession session) => session.CharacterId is > 0 and var actor && session.UserId is > 0 and var account ? (actor, account) : throw new DiscoveryAssignmentException("Authenticated character and account required.");
    private static JsonElement Parse(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    private static async Task<byte[]> ReadDatasetAsync(string path, string resource, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(path)) return await File.ReadAllBytesAsync(path, ct);
        await using var stream = typeof(ScientificReplayProvider).Assembly.GetManifestResourceStream(
            "Eve.Host.Assets.ProjectDiscovery." + resource) ?? throw new InvalidDataException("Bundled scientific dataset is missing.");
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    }
    private static bool HashMatches(byte[] bytes, string expected) => string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase);
}
