using System.Security.Cryptography;
using System.Text.Json;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.ProjectDiscovery;

// Operator-supplied authentic task bundle. This format contains provenance and all 24 client task numbers.
internal sealed class SubcellTrainingCatalog
{
    private static readonly int[][] Levels = [[4,9,22], [20,19,15], [13,3,2], [7,23,0], [8,12,14], [16,17,18], [10,11,5], [1,6,21]];
    private static readonly int[] Required = [2,2,1,1,1,1,1,1];
    private readonly Dictionary<long, JsonElement> tasks;
    private readonly string revision;
    public int ProjectId { get; }
    private readonly NpgsqlDataSource source;
    private readonly string schema;
    private readonly Func<long> now;
    private SubcellTrainingCatalog(NpgsqlDataSource source, string schema, Dictionary<long, JsonElement> tasks, string revision,
        int projectId, Func<long> now)
    { this.source = source; this.schema = schema; this.tasks = tasks; this.revision = revision; ProjectId = projectId; this.now = now; }
    public static async Task<SubcellTrainingCatalog> LoadAsync(NpgsqlDataSource source, string schema, string path,
        CancellationToken ct = default, Func<long>? now = null)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Training catalog too large.");
        using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
        if (!root.TryGetProperty("projectId", out var projectIdValue) || !projectIdValue.TryGetInt32(out int projectId) || projectId <= 0 ||
            !root.TryGetProperty("source", out var provenance) || provenance.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(provenance.GetString()) ||
            !root.TryGetProperty("license", out var license) || license.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(license.GetString()) ||
            !root.TryGetProperty("tasks", out var all) || all.ValueKind != JsonValueKind.Array || all.GetArrayLength() != 24)
            throw new InvalidDataException("Training catalog requires source, license and 24 tasks.");
        var values = new Dictionary<long,JsonElement>();
        foreach (var entry in all.EnumerateArray())
        {
            if (!entry.TryGetProperty("taskNumber", out var number) || !number.TryGetInt64(out long id) || id < (long)projectId * 10000000 || id > (long)projectId * 10000000 + 23 ||
                !entry.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.Object ||
                !task.TryGetProperty("taskId", out var taskId) || (taskId.ValueKind != JsonValueKind.String && taskId.ValueKind != JsonValueKind.Number) ||
                (taskId.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(taskId.GetString())) ||
                !task.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object || !assets.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !task.TryGetProperty("solution", out var solution) || solution.ValueKind != JsonValueKind.Array || solution.GetArrayLength() is < 1 or > 5)
                throw new InvalidDataException("Invalid Subcellular training task.");
            var categories = new HashSet<int>();
            foreach (var category in solution.EnumerateArray())
                if (category.ValueKind != JsonValueKind.Number || !category.TryGetInt32(out var n) || n is < 9 or > 35 || !categories.Add(n)) throw new InvalidDataException("Invalid training solution.");
            if (!values.TryAdd(id, task.Clone())) throw new InvalidDataException("Duplicate training task number.");
        }
        return new(source, schema, values, Convert.ToHexStringLower(SHA256.HashData(bytes)), projectId,
            now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
    public async ValueTask<PyValue> IssueAsync(ServiceCall call)
    {
        long actor = call.Session.CharacterId.GetValueOrDefault(), account = call.Session.UserId.GetValueOrDefault();
        long id = call.Arg(0) switch { PyInt n => n.Value, PyLong n => n.Value, _ => -1 };
        if (actor <= 0 || account <= 0 || !tasks.TryGetValue(id, out var task)) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TRAINING_UNAVAILABLE");
        await using var conn = await source.OpenConnectionAsync(call.Cancellation); await using var tx = await conn.BeginTransactionAsync(call.Cancellation);
        await EnsureTutorialAsync(conn, tx, actor, call.Cancellation);
        int level = await LockLevelAsync(conn, tx, actor, call.Cancellation);
        if (level is < 0 or >= 8 || !Levels[level].Contains((int)(id - (long)ProjectId * 10000000))) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TRAINING_UNAVAILABLE");
        await using var insert = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_training_issuances(character_id,account_id,level,task_number,catalog_revision) VALUES($1,$2,$3,$4,$5) ON CONFLICT(character_id,level,task_number) DO UPDATE SET account_id=EXCLUDED.account_id WHERE {schema}.project_discovery_training_issuances.account_id=EXCLUDED.account_id AND {schema}.project_discovery_training_issuances.catalog_revision=EXCLUDED.catalog_revision", conn, tx);
        insert.Parameters.AddWithValue(actor); insert.Parameters.AddWithValue(account); insert.Parameters.AddWithValue(level); insert.Parameters.AddWithValue(id); insert.Parameters.AddWithValue(revision);
        if (await insert.ExecuteNonQueryAsync(call.Cancellation) != 1) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TRAINING_UNAVAILABLE");
        await tx.CommitAsync(call.Cancellation); return ScientificReplayRpc.ToPy(task);
    }
    public async Task AdvanceAsync(long actor, long account, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct);
        await EnsureTutorialAsync(conn, tx, actor, ct); int level = await LockLevelAsync(conn, tx, actor, ct);
        if (level is < 0 or >= 8) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TRAINING_UNAVAILABLE");
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {schema}.project_discovery_training_issuances WHERE character_id=$1 AND level=$2 AND catalog_revision=$3 AND account_id=$4", conn, tx);
        count.Parameters.AddWithValue(actor); count.Parameters.AddWithValue(level); count.Parameters.AddWithValue(revision); count.Parameters.AddWithValue(account);
        if ((long)(await count.ExecuteScalarAsync(ct))! < Required[level]) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TRAINING_UNAVAILABLE");
        await using var update = new NpgsqlCommand($"UPDATE {schema}.project_discovery_tutorials SET tutorial_level=$2+1,finished=($2=7),participation_steps=participation_steps+1,updated_ms=$3 WHERE character_id=$1 AND NOT participation_skipped", conn, tx);
        update.Parameters.AddWithValue(actor); update.Parameters.AddWithValue(level); update.Parameters.AddWithValue(now());
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TRAINING_UNAVAILABLE");
        await tx.CommitAsync(ct);
    }
    public async Task ResetAsync(long actor, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct);
        await EnsureTutorialAsync(conn, tx, actor, ct); _ = await LockLevelAsync(conn, tx, actor, ct);
        await using var delete = new NpgsqlCommand($"DELETE FROM {schema}.project_discovery_training_issuances WHERE character_id=$1", conn, tx);
        delete.Parameters.AddWithValue(actor); await delete.ExecuteNonQueryAsync(ct);
        await using var update = new NpgsqlCommand($"UPDATE {schema}.project_discovery_tutorials SET finished=false,tutorial_level=0,participation_steps=0,participation_skipped=false,updated_ms=$2 WHERE character_id=$1", conn, tx);
        update.Parameters.AddWithValue(actor); update.Parameters.AddWithValue(now()); await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
    private async Task EnsureTutorialAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long actor, CancellationToken ct)
    { await using var cmd = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_tutorials(character_id,finished,tutorial_level,updated_ms) VALUES($1,false,0,0) ON CONFLICT DO NOTHING", conn, tx); cmd.Parameters.AddWithValue(actor); await cmd.ExecuteNonQueryAsync(ct); }
    private async Task<int> LockLevelAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long actor, CancellationToken ct)
    { await using var cmd = new NpgsqlCommand($"SELECT tutorial_level FROM {schema}.project_discovery_tutorials WHERE character_id=$1 FOR UPDATE", conn, tx); cmd.Parameters.AddWithValue(actor); return (int)(await cmd.ExecuteScalarAsync(ct))!; }
}
