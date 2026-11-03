using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eve.Characters;
using Eve.Services;
using Npgsql;

namespace Eve.Host.ProjectDiscovery;

public sealed record RemoteDiscoveryOptions
{
    public bool Enabled { get; init; }
    public string Game { get; init; } = "";
    public string Project { get; init; } = "";
    public string Protocol { get; init; } = "covid";
}
public sealed class DiscoveryAssignmentException(string message) : Exception(message);
public sealed class DiscoverySubmissionUnknownException() : Exception("The classification outcome is unknown; do not resend it.");

// Standalone foundation. The caller must supply an authenticated server-owned session.
// Remote classifications are never retried after a send has been claimed.
public sealed class RemoteDiscoveryProvider
{
    private readonly NpgsqlDataSource source;
    private readonly ProjectDiscoveryTaskPinStore pins;
    private readonly MmosApiClient client;
    private readonly RemoteDiscoveryOptions options;
    private readonly string schema;
    private readonly ProjectDiscoveryBonusPolicy? bonus;
    private readonly string characterSchema;
    public RemoteDiscoveryProvider(NpgsqlDataSource source, ProjectDiscoveryTaskPinStore pins, MmosApiClient client, RemoteDiscoveryOptions options, string schema = "project_discovery", ProjectDiscoveryBonusPolicy? bonus = null, string characterSchema = "characters")
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(pins); ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Game) || string.IsNullOrWhiteSpace(options.Project) || options.Protocol is not ("covid" or "exoplanet" or "subcellular")) throw new ArgumentException("Remote discovery requires explicit enabled game and project.", nameof(options));
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid schema name.", nameof(schema));
        this.source = source; this.pins = pins; this.client = client; this.options = options; this.schema = schema; this.bonus = bonus;
        if (!Regex.IsMatch(characterSchema, "^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid character schema.", nameof(characterSchema));
        this.characterSchema = characterSchema;
    }
    // IF NOT EXISTS adopts the earlier versionless provider tables without dropping rows.
    public Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, schema,
        [new Eve.Data.Migration("0001_remote_discovery", """
            CREATE TABLE IF NOT EXISTS assignments (
                actor bigint NOT NULL, project text NOT NULL, task_id text NOT NULL,
                task jsonb NOT NULL, active boolean NOT NULL DEFAULT true,
                PRIMARY KEY(actor, project, task_id));
            CREATE UNIQUE INDEX IF NOT EXISTS assignments_one_active ON assignments(actor, project) WHERE active;
            CREATE TABLE IF NOT EXISTS submissions (
                actor bigint NOT NULL, project text NOT NULL, task_id text NOT NULL, call_id text NOT NULL,
                fingerprint text NOT NULL, state text NOT NULL CHECK(state IN ('prepared','sending','completed','unknown')),
                response jsonb, PRIMARY KEY(actor, project, call_id), UNIQUE(actor, project, task_id),
                FOREIGN KEY(actor, project, task_id) REFERENCES assignments(actor, project, task_id));
            """), new Eve.Data.Migration("0003_remote_assignment_account", """
            ALTER TABLE assignments ADD COLUMN IF NOT EXISTS account bigint;
            """)], ct);
    public async ValueTask<Eve.Services.Core.ProjectDiscoveryPlayerStatistics?> ReadStatisticsAsync(long actor, CancellationToken ct)
    {
        if (actor <= 0) return null;
        var player = await client.GetPlayerAsync(options.Game, actor.ToString(CultureInfo.InvariantCulture), options.Project, ct);
        if (player?.Project is not { ValueKind: JsonValueKind.Object } project ||
            !project.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number ||
            !score.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 1) return null;
        return new("", 0, options.Project, value);
    }
    public async Task<JsonElement> GetTaskAsync(ClientSession session, CancellationToken ct = default)
    {
        long actor = Actor(session); string identity = actor.ToString(CultureInfo.InvariantCulture);
        long accountId = Account(session); string account = accountId.ToString(CultureInfo.InvariantCulture);
        long? pin = await pins.ReadAsync(actor, ct);
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAsync(conn, tx, actor, ct);
        await using (var find = new NpgsqlCommand($"SELECT task::text,account FROM {schema}.assignments WHERE actor=$1 AND project=$2 AND " + (pin is null ? "active" : "active AND task_id=$3"), conn, tx))
        {
            find.Parameters.AddWithValue(actor); find.Parameters.AddWithValue(options.Project);
            if (pin is not null) find.Parameters.AddWithValue(pin.Value.ToString(CultureInfo.InvariantCulture));
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(1) || reader.GetInt64(1) != accountId) throw new DiscoveryAssignmentException("Assignment account mismatch.");
                string saved = reader.GetString(0); await reader.DisposeAsync(); await tx.CommitAsync(ct); return Parse(saved);
            }
        }
        if (pin is not null) throw new DiscoveryAssignmentException("Pinned task is not a cached assignment for this character and project.");
        var allocation = await client.AllocateTaskAsync(options.Game, identity, options.Project, account, ct);
        JsonElement task = NormalizeTask(allocation.Task, options.Protocol);
        string taskId = RequiredTaskId(task); ValidateTask(task, options.Protocol);
        await using (var insert = new NpgsqlCommand($"INSERT INTO {schema}.assignments(actor,project,task_id,task,account) VALUES($1,$2,$3,$4::jsonb,$5)", conn, tx))
        { insert.Parameters.AddWithValue(actor); insert.Parameters.AddWithValue(options.Project); insert.Parameters.AddWithValue(taskId); insert.Parameters.AddWithValue(task.GetRawText()); insert.Parameters.AddWithValue(accountId); await insert.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct); return task;
    }
    public async Task<JsonElement> SubmitAsync(ClientSession session, string callId, JsonElement result, JsonElement circumstances, CancellationToken ct = default, JsonElement? submittedTask = null)
    {
        long actor = Actor(session); long accountId = Account(session); if (string.IsNullOrWhiteSpace(callId)) throw new ArgumentException("Call ID is required.", nameof(callId));
        ct.ThrowIfCancellationRequested();
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(submittedTask is null ? JsonSerializer.SerializeToUtf8Bytes(new { result, circumstances }) : JsonSerializer.SerializeToUtf8Bytes(new { result, circumstances, submittedTask })));
        string taskId;
        await using (var conn = await source.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await LockAsync(conn, tx, actor, ct);
            await using (var existing = new NpgsqlCommand($"SELECT s.fingerprint,s.state,s.response::text,a.account FROM {schema}.submissions s JOIN {schema}.assignments a ON a.actor=s.actor AND a.project=s.project AND a.task_id=s.task_id WHERE s.actor=$1 AND s.project=$2 AND s.call_id=$3", conn, tx))
            {
                existing.Parameters.AddWithValue(actor); existing.Parameters.AddWithValue(options.Project); existing.Parameters.AddWithValue(callId);
                await using var reader = await existing.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(3) || reader.GetInt64(3) != accountId) throw new DiscoveryAssignmentException("Assignment account mismatch.");
                    if (reader.GetString(0) != fingerprint) throw new DiscoveryAssignmentException("Call ID reused with different payload.");
                    if (reader.GetString(1) == "completed") { string saved = reader.GetString(2); await reader.DisposeAsync(); await tx.CommitAsync(ct); return Parse(saved); }
                    throw new DiscoverySubmissionUnknownException();
                }
            }
            await using (var active = new NpgsqlCommand($"SELECT task_id,task::text FROM {schema}.assignments WHERE actor=$1 AND project=$2 AND active AND account=$3", conn, tx))
            {
                active.Parameters.AddWithValue(actor); active.Parameters.AddWithValue(options.Project); active.Parameters.AddWithValue(accountId);
                await using var reader = await active.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new DiscoveryAssignmentException("No active assignment.");
                taskId = reader.GetString(0);
                if (submittedTask is { } expected && !JsonElement.DeepEquals(expected, Parse(reader.GetString(1)))) throw new DiscoveryAssignmentException("Submitted task differs from the assignment.");
            }
            await using (var insert = new NpgsqlCommand($"INSERT INTO {schema}.submissions(actor,project,task_id,call_id,fingerprint,state) VALUES($1,$2,$3,$4,$5,'prepared') ON CONFLICT DO NOTHING", conn, tx))
            { insert.Parameters.AddWithValue(actor); insert.Parameters.AddWithValue(options.Project); insert.Parameters.AddWithValue(taskId); insert.Parameters.AddWithValue(callId); insert.Parameters.AddWithValue(fingerprint); if (await insert.ExecuteNonQueryAsync(ct) != 1) throw new DiscoverySubmissionUnknownException(); }
            await using (var sending = new NpgsqlCommand($"UPDATE {schema}.submissions SET state='sending' WHERE actor=$1 AND project=$2 AND call_id=$3", conn, tx))
            { sending.Parameters.AddWithValue(actor); sending.Parameters.AddWithValue(options.Project); sending.Parameters.AddWithValue(callId); await sending.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
        }
        try
        {
            string identity = actor.ToString(CultureInfo.InvariantCulture);
            var response = await client.SubmitClassificationAsync(options.Game, identity, null, taskId, result, circumstances, ct);
            if (RequiredTaskId(response.Task) != taskId) throw new MmosResponseException("MMOS response task does not match assignment.");
            if (options.Protocol != "subcellular" && (response.Player.ValueKind != JsonValueKind.Object || !response.Player.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 1)) throw new MmosResponseException("Response lacks a valid player score.");
            await using var conn = await source.OpenConnectionAsync(CancellationToken.None);
            await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);
            bool isTraining = (response.Task.TryGetProperty("isTrainingSet", out var trainingFlag) && trainingFlag.ValueKind == JsonValueKind.True) ||
                (response.Raw.TryGetProperty("isTraining", out var trainingResult) && trainingResult.ValueKind == JsonValueKind.True);
            long awardedXp = bonus is null || isTraining ? 0 : await bonus.AwardAsync(conn, tx, actor, $"remote:{options.Project}:{taskId}", CancellationToken.None);
            long totalXp = 0; int remaining = 0; bool finished = false; int tutorialLevel = 0;
            if (options.Protocol != "covid")
            {
                await using var progress = new NpgsqlCommand($"SELECT experience FROM {characterSchema}.project_discovery_progress WHERE character_id=$1", conn, tx);
                progress.Parameters.AddWithValue(actor); totalXp = await progress.ExecuteScalarAsync() is long current ? current : 0;
                await using (var tutorial = new NpgsqlCommand($"SELECT finished,tutorial_level FROM {characterSchema}.project_discovery_tutorials WHERE character_id=$1", conn, tx))
                {
                    tutorial.Parameters.AddWithValue(actor); await using var reader = await tutorial.ExecuteReaderAsync();
                    if (await reader.ReadAsync()) { finished = reader.GetBoolean(0); tutorialLevel = reader.GetInt32(1); }
                }
                if (bonus is not null) remaining = await bonus.ReadRemainingAsync(conn, tx, actor, CancellationToken.None);
                if (options.Protocol == "exoplanet" && (!response.Task.TryGetProperty("isTrainingSet", out var training) || training.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) throw new MmosResponseException("Exoplanet response lacks task training flag.");
            }
            var finalResponse = ProjectDiscoveryRewardResponse.Decorate(response.Raw, awardedXp, options.Protocol, totalXp, remaining, finished, tutorialLevel);
            await using (var done = new NpgsqlCommand($"UPDATE {schema}.submissions SET state='completed',response=$4::jsonb WHERE actor=$1 AND project=$2 AND call_id=$3 AND state='sending'", conn, tx))
            { done.Parameters.AddWithValue(actor); done.Parameters.AddWithValue(options.Project); done.Parameters.AddWithValue(callId); done.Parameters.AddWithValue(finalResponse.GetRawText()); await done.ExecuteNonQueryAsync(); }
            await using (var deactivate = new NpgsqlCommand($"UPDATE {schema}.assignments SET active=false WHERE actor=$1 AND project=$2 AND task_id=$3", conn, tx))
            { deactivate.Parameters.AddWithValue(actor); deactivate.Parameters.AddWithValue(options.Project); deactivate.Parameters.AddWithValue(taskId); await deactivate.ExecuteNonQueryAsync(); }
            await tx.CommitAsync(); return finalResponse;
        }
        catch (Exception ex) when (ex is not DiscoverySubmissionUnknownException)
        {
            try { await using var update = source.CreateCommand($"UPDATE {schema}.submissions SET state='unknown' WHERE actor=$1 AND project=$2 AND call_id=$3 AND state='sending'"); update.Parameters.AddWithValue(actor); update.Parameters.AddWithValue(options.Project); update.Parameters.AddWithValue(callId); await update.ExecuteNonQueryAsync(CancellationToken.None); } catch (NpgsqlException) { }
            throw new DiscoverySubmissionUnknownException();
        }
    }
    private async Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long actor, CancellationToken ct)
    { await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", conn, tx); cmd.Parameters.AddWithValue($"pd:{schema}:{options.Project}:{actor}"); await cmd.ExecuteNonQueryAsync(ct); }
    private static long Account(ClientSession session) => session.UserId is > 0 and var id ? id : throw new DiscoveryAssignmentException("Authenticated account required.");
    private static long Actor(ClientSession session) => session.CharacterId is > 0 and var id ? id : throw new DiscoveryAssignmentException("Authenticated character required.");
    private static JsonElement Parse(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    private static string RequiredTaskId(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object || (!task.TryGetProperty("id", out var id) && !task.TryGetProperty("taskId", out id)) || (id.ValueKind != JsonValueKind.String && id.ValueKind != JsonValueKind.Number)) throw new MmosResponseException("MMOS task lacks an ID.");
        return id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
    }
    private static JsonElement NormalizeTask(JsonElement raw, string protocol)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(raw.GetRawText()) as System.Text.Json.Nodes.JsonObject ?? throw new MmosResponseException("Invalid task.");
        if (protocol == "subcellular" && node["taskId"] is null && node["id"] is { } id) node["taskId"] = id.DeepClone();
        if (node["id"] is null && node["taskId"] is { } taskId) node["id"] = taskId.DeepClone();
        return JsonSerializer.SerializeToElement(node);
    }
    private static void ValidateTask(JsonElement task, string protocol)
    {
        JsonElement url;
        if (protocol == "exoplanet")
        {
            if (!task.TryGetProperty("url", out url)) throw new MmosResponseException("Exoplanet task lacks URL.");
        }
        else if (!task.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object || !assets.TryGetProperty("url", out url))
            throw new MmosResponseException("Task lacks asset URL.");
        if (url.ValueKind != JsonValueKind.String || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new MmosResponseException("Task requires an absolute HTTP(S) URL.");
    }
}
