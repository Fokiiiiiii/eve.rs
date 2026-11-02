using Npgsql;

namespace Eve.Host.ProjectDiscovery;

// Fixed XP per accepted classification, with an operator-chosen UTC reset hour.
// This policy is independent of scientific score and does not claim to be a CCP rule.
public sealed class ProjectDiscoveryBonusPolicy(NpgsqlDataSource source, string schema, int maximum, long xp, int resetHourUtc, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly int validatedMaximum = maximum > 0 && xp > 0 && resetHourUtc is >= 0 and <= 23 && System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$")
        ? maximum : throw new ArgumentException("Invalid Project Discovery bonus policy.");
    private DateOnly Day() => DateOnly.FromDateTime(clock().UtcDateTime.AddHours(-resetHourUtc));
    public async ValueTask<int> ReadAsync(long actor, string method, CancellationToken ct)
    {
        if (method == "get_maximum_bonus_samples") return validatedMaximum;
        await using var command = source.CreateCommand($"SELECT used FROM {schema}.project_discovery_bonus_days WHERE character_id=$1 AND day=$2");
        command.Parameters.AddWithValue(actor); command.Parameters.AddWithValue(Day());
        return Math.Max(0, maximum - (await command.ExecuteScalarAsync(ct) is int used ? used : 0));
    }
    public async Task<int> ReadRemainingAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT used FROM {schema}.project_discovery_bonus_days WHERE character_id=$1 AND day=$2", connection, tx);
        command.Parameters.AddWithValue(actor); command.Parameters.AddWithValue(Day());
        return Math.Max(0, maximum - (await command.ExecuteScalarAsync(ct) is int used ? used : 0));
    }
    public async Task<long> AwardAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long actor, string assignment, CancellationToken ct)
    {
        var day = Day();
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, tx))
        { gate.Parameters.AddWithValue($"pd-bonus:{schema}:{actor}:{day:yyyy-MM-dd}"); await gate.ExecuteNonQueryAsync(ct); }
        await using (var seen = new NpgsqlCommand($"SELECT 1 FROM {schema}.project_discovery_bonus_receipts WHERE character_id=$1 AND assignment=$2", connection, tx))
        { seen.Parameters.AddWithValue(actor); seen.Parameters.AddWithValue(assignment); if (await seen.ExecuteScalarAsync(ct) is not null) return 0; }
        await using (var budget = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_bonus_days(character_id,day,used) VALUES($1,$2,1) ON CONFLICT(character_id,day) DO UPDATE SET used={schema}.project_discovery_bonus_days.used+1 WHERE {schema}.project_discovery_bonus_days.used<$3 RETURNING used", connection, tx))
        { budget.Parameters.AddWithValue(actor); budget.Parameters.AddWithValue(day); budget.Parameters.AddWithValue(maximum); if (await budget.ExecuteScalarAsync(ct) is null) return 0; }
        await using (var receipt = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_bonus_receipts(character_id,assignment,day,experience) VALUES($1,$2,$3,$4)", connection, tx))
        { receipt.Parameters.AddWithValue(actor); receipt.Parameters.AddWithValue(assignment); receipt.Parameters.AddWithValue(day); receipt.Parameters.AddWithValue(xp); await receipt.ExecuteNonQueryAsync(ct); }
        await using var progress = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_progress(character_id,experience) VALUES($1,$2) ON CONFLICT(character_id) DO UPDATE SET experience={schema}.project_discovery_progress.experience+EXCLUDED.experience", connection, tx);
        progress.Parameters.AddWithValue(actor); progress.Parameters.AddWithValue(xp); await progress.ExecuteNonQueryAsync(ct);
        return xp;
    }
}
