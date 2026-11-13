using System.Security.Cryptography;
using System.Text.Json;
using Eve.Characters;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.ProjectDiscovery;

/// <summary>Durable assignments and submissions for the client-authored COVID tutorial steps.</summary>
internal sealed class CovidTutorialProvider(NpgsqlDataSource source, LocalProjectDiscoveryAssetServer assets, string schema,
    string characterSchema, Func<long>? clock = null)
{
    private const long CompletionExperience = 100;
    private const long CompletionKredits = 10;
    private readonly Func<long> now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly ProjectDiscoveryTutorialStore progress = new(source, characterSchema);

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid Project Discovery schema.", nameof(schema));
        if (!System.Text.RegularExpressions.Regex.IsMatch(characterSchema, "^[a-z_][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid character schema.", nameof(characterSchema));
        return await Eve.Data.Migrations.ApplyAsync(source, schema,
        [new Eve.Data.Migration("0003_covid_tutorial_training", """
            CREATE TABLE IF NOT EXISTS covid_tutorial_assignments (
                actor bigint NOT NULL,
                account bigint NOT NULL,
                tutorial_code text NOT NULL,
                sequence bigint NOT NULL CHECK (sequence > 0),
                state text NOT NULL CHECK (state IN ('active', 'completed', 'skipped')),
                PRIMARY KEY (actor, tutorial_code)
            );
            CREATE TABLE IF NOT EXISTS covid_tutorial_current_tasks (
                actor bigint PRIMARY KEY,
                account bigint NOT NULL,
                tutorial_code text NOT NULL,
                sequence bigint NOT NULL CHECK (sequence > 0)
            );
            CREATE TABLE IF NOT EXISTS covid_tutorial_submissions (
                actor bigint NOT NULL,
                tutorial_code text NOT NULL,
                sequence bigint NOT NULL CHECK (sequence > 0),
                call_id text NOT NULL,
                fingerprint text NOT NULL,
                score double precision NOT NULL CHECK (score >= 0 AND score <= 1),
                response jsonb NOT NULL,
                created_ms bigint NOT NULL,
                PRIMARY KEY (actor, tutorial_code, sequence),
                UNIQUE (actor, call_id),
                FOREIGN KEY (actor, tutorial_code) REFERENCES covid_tutorial_assignments(actor, tutorial_code)
            );
            CREATE TABLE IF NOT EXISTS covid_tutorial_completion_rewards (
                actor bigint PRIMARY KEY,
                account bigint NOT NULL,
                created_ms bigint NOT NULL
            );
            """)], ct);
    }

    public async Task<bool> TryGrantCompletionRewardAsync(ClientSession session, CancellationToken ct)
    {
        (long actor, long account) = Identity(session);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, actor, ct);
        await using (var existing = new NpgsqlCommand($"SELECT account FROM {schema}.covid_tutorial_completion_rewards WHERE actor=$1", connection, tx))
        {
            existing.Parameters.AddWithValue(actor);
            if (await existing.ExecuteScalarAsync(ct) is long savedAccount)
            {
                if (savedAccount != account) throw new DiscoveryAssignmentException("Tutorial reward account mismatch.");
                await tx.CommitAsync(ct);
                return false;
            }
        }
        await using (var proof = new NpgsqlCommand($"""
            SELECT count(DISTINCT s.tutorial_code)
            FROM {schema}.covid_tutorial_submissions s
            JOIN {schema}.covid_tutorial_assignments a USING(actor, tutorial_code)
            WHERE s.actor=$1 AND a.account=$2 AND s.score > 0.5 AND s.tutorial_code=ANY($3)
            """, connection, tx))
        {
            proof.Parameters.AddWithValue(actor);
            proof.Parameters.AddWithValue(account);
            proof.Parameters.AddWithValue(CovidTutorialCorpus.TutorialCodes.ToArray());
            if (await proof.ExecuteScalarAsync(ct) is not long codeCount || codeCount != CovidTutorialCorpus.TutorialCodes.Count)
            {
                await tx.CommitAsync(ct);
                return false;
            }
        }
        await using (var receipt = new NpgsqlCommand($"INSERT INTO {schema}.covid_tutorial_completion_rewards(actor,account,created_ms) VALUES($1,$2,$3)", connection, tx))
        {
            receipt.Parameters.AddWithValue(actor);
            receipt.Parameters.AddWithValue(account);
            receipt.Parameters.AddWithValue(now());
            await receipt.ExecuteNonQueryAsync(ct);
        }
        await progress.CreditTrainingRewardAsync(connection, tx, actor, CompletionExperience, CompletionKredits, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<JsonElement> GetTaskAsync(ClientSession session, string code, CancellationToken ct)
    {
        if (!CovidTutorialCorpus.Contains(code)) throw new DiscoveryAssignmentException("Unknown COVID tutorial sample.");
        (long actor, long account) = Identity(session);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, actor, ct);
        await using (var insert = new NpgsqlCommand($"INSERT INTO {schema}.covid_tutorial_assignments(actor,account,tutorial_code,sequence,state) VALUES($1,$2,$3,1,'active') ON CONFLICT DO NOTHING", connection, tx))
        {
            insert.Parameters.AddWithValue(actor); insert.Parameters.AddWithValue(account); insert.Parameters.AddWithValue(code);
            await insert.ExecuteNonQueryAsync(ct);
        }
        long sequence;
        await using (var select = new NpgsqlCommand($"SELECT account,sequence,state FROM {schema}.covid_tutorial_assignments WHERE actor=$1 AND tutorial_code=$2 FOR UPDATE", connection, tx))
        {
            select.Parameters.AddWithValue(actor); select.Parameters.AddWithValue(code);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetInt64(0) != account)
                throw new DiscoveryAssignmentException("Tutorial assignment identity mismatch.");
            sequence = reader.GetInt64(1);
            if (reader.GetString(2) != "active")
            {
                sequence = checked(sequence + 1);
                await reader.DisposeAsync();
                await using var advance = new NpgsqlCommand($"UPDATE {schema}.covid_tutorial_assignments SET sequence=$3,state='active' WHERE actor=$1 AND tutorial_code=$2", connection, tx);
                advance.Parameters.AddWithValue(actor); advance.Parameters.AddWithValue(code); advance.Parameters.AddWithValue(sequence);
                await advance.ExecuteNonQueryAsync(ct);
            }
        }
        await using (var active = new NpgsqlCommand($"INSERT INTO {schema}.covid_tutorial_current_tasks(actor,account,tutorial_code,sequence) VALUES($1,$2,$3,$4) ON CONFLICT(actor) DO UPDATE SET account=EXCLUDED.account,tutorial_code=EXCLUDED.tutorial_code,sequence=EXCLUDED.sequence", connection, tx))
        {
            active.Parameters.AddWithValue(actor); active.Parameters.AddWithValue(account); active.Parameters.AddWithValue(code); active.Parameters.AddWithValue(sequence);
            await active.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return CovidTutorialCorpus.Task(actor, code, sequence, assets.TutorialAssetUri(code, sequence));
    }

    public async Task ClearActiveAsync(ClientSession session, CancellationToken ct)
    {
        if (session.CharacterId is not > 0) return;
        long actor = session.CharacterId.Value;
        long account = session.UserId.GetValueOrDefault();
        if (account <= 0) return;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, actor, ct);
        await using var remove = new NpgsqlCommand($"DELETE FROM {schema}.covid_tutorial_current_tasks WHERE actor=$1 AND account=$2", connection, tx);
        remove.Parameters.AddWithValue(actor); remove.Parameters.AddWithValue(account);
        await remove.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<JsonElement?> SubmitIfActiveAsync(ClientSession session, string callId, JsonElement answer, CancellationToken ct)
    {
        (long actor, long account) = Identity(session);
        if (string.IsNullOrWhiteSpace(callId)) throw new DiscoveryAssignmentException("Tutorial call ID is required.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, actor, ct);
        await using (var receipt = new NpgsqlCommand($"SELECT tutorial_code,fingerprint,response::text FROM {schema}.covid_tutorial_submissions WHERE actor=$1 AND call_id=$2", connection, tx))
        {
            receipt.Parameters.AddWithValue(actor); receipt.Parameters.AddWithValue(callId);
            await using var reader = await receipt.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                string replayCode = reader.GetString(0);
                string replayFingerprint = reader.GetString(1);
                string saved = reader.GetString(2);
                await reader.DisposeAsync();
                if (replayFingerprint != Fingerprint(replayCode, answer))
                    throw new DiscoveryAssignmentException("Tutorial call ID reused with another answer.");
                await tx.CommitAsync(ct);
                return Parse(saved);
            }
        }
        string code;
        long sequence;
        await using (var active = new NpgsqlCommand($"SELECT account,tutorial_code,sequence FROM {schema}.covid_tutorial_current_tasks WHERE actor=$1 FOR UPDATE", connection, tx))
        {
            active.Parameters.AddWithValue(actor);
            await using var reader = await active.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            if (reader.GetInt64(0) != account) throw new DiscoveryAssignmentException("Tutorial assignment account mismatch.");
            code = reader.GetString(1);
            sequence = reader.GetInt64(2);
        }
        if (!CovidTutorialCorpus.TryScore(code, answer, out double score))
            throw new ArgumentException("Invalid tutorial classification.", nameof(answer));
        string fingerprint = Fingerprint(code, answer);
        await using (var prior = new NpgsqlCommand($"SELECT fingerprint,response::text FROM {schema}.covid_tutorial_submissions WHERE actor=$1 AND tutorial_code=$2 AND sequence=$3", connection, tx))
        {
            prior.Parameters.AddWithValue(actor); prior.Parameters.AddWithValue(code); prior.Parameters.AddWithValue(sequence);
            await using var reader = await prior.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(0) != fingerprint) throw new DiscoveryAssignmentException("Tutorial task already has another answer.");
                string saved = reader.GetString(1);
                await reader.DisposeAsync();
                await tx.CommitAsync(ct);
                return Parse(saved);
            }
        }
        long classificationCount;
        double playerScore;
        await using (var totals = new NpgsqlCommand($"SELECT count(*)::bigint,coalesce(avg(score),0)::float8 FROM {schema}.covid_tutorial_submissions WHERE actor=$1", connection, tx))
        {
            totals.Parameters.AddWithValue(actor);
            await using var reader = await totals.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            classificationCount = checked(reader.GetInt64(0) + 1);
            playerScore = (reader.GetDouble(1) * (classificationCount - 1) + score) / classificationCount;
        }
        JsonElement task = CovidTutorialCorpus.Task(actor, code, sequence, assets.TutorialAssetUri(code, sequence), includeSolution: true);
        JsonElement response = JsonSerializer.SerializeToElement(new
        {
            isSolved = true,
            score,
            classificationCount,
            XP_Reward = 0,
            ISK_Reward = 0,
            AK_Reward = 0,
            gotBonusXP = false,
            player = new { score = playerScore },
            task,
            isTraining = true,
            localMode = CovidTutorialCorpus.LocalMode,
        });
        await using (var save = new NpgsqlCommand($"INSERT INTO {schema}.covid_tutorial_submissions(actor,tutorial_code,sequence,call_id,fingerprint,score,response,created_ms) VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,$8)", connection, tx))
        {
            save.Parameters.AddWithValue(actor); save.Parameters.AddWithValue(code); save.Parameters.AddWithValue(sequence); save.Parameters.AddWithValue(callId);
            save.Parameters.AddWithValue(fingerprint); save.Parameters.AddWithValue(score); save.Parameters.AddWithValue(response.GetRawText());
            save.Parameters.AddWithValue(now());
            await save.ExecuteNonQueryAsync(ct);
        }
        await using (var done = new NpgsqlCommand($"UPDATE {schema}.covid_tutorial_assignments SET state=$3 WHERE actor=$1 AND tutorial_code=$2 AND sequence=$4 AND state='active'", connection, tx))
        {
            done.Parameters.AddWithValue(actor); done.Parameters.AddWithValue(code); done.Parameters.AddWithValue(answer.TryGetProperty("skip", out var skip) && skip.GetBoolean() ? "skipped" : "completed"); done.Parameters.AddWithValue(sequence);
            if (await done.ExecuteNonQueryAsync(ct) != 1) throw new DiscoveryAssignmentException("Tutorial assignment changed during submission.");
        }
        await tx.CommitAsync(ct);
        return response;
    }

    public async ValueTask<ProjectDiscoveryPlayerStatistics?> ReadStatisticsAsync(long actor, CancellationToken ct)
    {
        if (actor <= 0) return null;
        await using var cmd = source.CreateCommand($"SELECT (SELECT avg(score)::float8 FROM {schema}.covid_tutorial_submissions WHERE actor=$1) WHERE EXISTS (SELECT 1 FROM {schema}.covid_tutorial_current_tasks WHERE actor=$1)");
        cmd.Parameters.AddWithValue(actor);
        if (await cmd.ExecuteScalarAsync(ct) is not double score) return null;
        return new("", 0, CovidTutorialCorpus.ProjectId, score);
    }

    private async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, tx);
        command.Parameters.AddWithValue($"pd-covid-tutorial:{schema}:{actor}");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static (long Actor, long Account) Identity(ClientSession session) =>
        session.CharacterId is > 0 and var actor && session.UserId is > 0 and var account
            ? (actor, account)
            : throw new DiscoveryAssignmentException("Authenticated character and account required.");

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string Fingerprint(string code, JsonElement answer) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { code, answer })));
}
