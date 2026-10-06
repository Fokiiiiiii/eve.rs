using Eve.Data;
using Npgsql;

namespace Eve.Crimewatch;

/// <summary>What crimewatch keeps in PostgreSQL: the ship safety level of each character, and the combat timers and flags that must outlast a session.</summary>
/// <summary>Security-status event ids from the pinned eve-logic appLogConst.py.</summary>
public static class SecurityStatusEventTypeIds
{
    public const int KillPirateNpc = 378;
    public const int IllegalAggression = 379;
    public const int GmModification = 380;
    public const int HandInTags = 385;
    public const int GmRollback = 386;

    /// <summary>Known persisted relation reasons only; unrecognized reasons have no invented event id.</summary>
    public static int? FromHistoryReason(string? reason) => reason switch
    {
        "aggression" => IllegalAggression,
        "npc_bounty_recovery" or "npc-kill-security-status-recovery" => KillPirateNpc,
        _ => null,
    };
}

/// <summary>A real relations.relation_history row projected to the security-status client fields.</summary>
public sealed class PgCrimewatchStore
{
    public const string DefaultSchema = "crimewatch";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgCrimewatchStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgCrimewatchStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>The character's safety level; full when it has never set one.</summary>
    public async Task<int> SafetyOfAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var level = await Sql.ScalarAsync(connection, null, Q("SELECT level FROM {schema}.safety WHERE character_id = $1"), cancellationToken, characterId);
        return level is short stored ? stored : SafetyLevel.Full;
    }

    private const string StateColumns = "weapons_until_ms, pvp_until_ms, npc_until_ms, flag_until_ms, flag, disapproval_until_ms, solar_system_id";

    private static CombatState ReadState(NpgsqlDataReader row) =>
        new(row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3), (Flag)row.GetInt16(4), row.GetInt64(5), row.GetInt64(6));

    /// <summary>What crimewatch keeps for the character (idle when it keeps nothing).</summary>
    public async Task<CombatState> StateOfAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q($"SELECT {StateColumns} FROM {{schema}}.combat_state WHERE character_id = $1"), characterId);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        return await row.ReadAsync(cancellationToken) ? ReadState(row) : CombatState.Idle;
    }

    /// <summary>
    /// Changes what crimewatch keeps for the character as one step no other step of the character overlaps (the row is locked while <paramref name="change"/> works on what it was).
    /// Returns what it was and what it is now.
    /// </summary>
    public async Task<(CombatState Before, CombatState After)> UpdateAsync(long characterId, Func<CombatState, CombatState> change, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Sql.ExecuteAsync(connection, transaction, Q("INSERT INTO {schema}.combat_state (character_id, updated_ms) VALUES ($1, $2) ON CONFLICT (character_id) DO NOTHING"), cancellationToken, characterId, nowMs);
        CombatState before;
        await using (var command = Sql.Command(connection, transaction, Q($"SELECT {StateColumns} FROM {{schema}}.combat_state WHERE character_id = $1 FOR UPDATE"), characterId))
        await using (var row = await command.ExecuteReaderAsync(cancellationToken))
        {
            await row.ReadAsync(cancellationToken);
            before = ReadState(row);
        }
        var after = change(before);
        await Sql.ExecuteAsync(connection, transaction, Q("""
            UPDATE {schema}.combat_state
            SET weapons_until_ms = $2, pvp_until_ms = $3, npc_until_ms = $4, flag_until_ms = $5, flag = $6, disapproval_until_ms = $7, solar_system_id = $8, updated_ms = $9
            WHERE character_id = $1
            """), cancellationToken, characterId, after.WeaponsUntilMs, after.PvpUntilMs, after.NpcUntilMs, after.FlagUntilMs, (short)after.Flag, after.DisapprovalUntilMs, after.SolarSystemId, nowMs);
        await transaction.CommitAsync(cancellationToken);
        return (before, after);
    }

    /// <summary>Who is flagged in a system at this time: the criminals and the suspects, by character id.</summary>
    public async Task<(IReadOnlyList<long> Criminals, IReadOnlyList<long> Suspects)> FlaggedInAsync(long solarSystemId, long nowMs, CancellationToken cancellationToken = default)
    {
        List<long> criminals = [], suspects = [];
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null,
            Q("SELECT character_id, flag FROM {schema}.combat_state WHERE solar_system_id = $1 AND flag <> 0 AND flag_until_ms > $2 ORDER BY character_id"), solarSystemId, nowMs);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        while (await row.ReadAsync(cancellationToken))
        {
            (row.GetInt16(1) == (short)Flag.Criminal ? criminals : suspects).Add(row.GetInt64(0));
        }
        return (criminals, suspects);
    }

    /// <summary>The characters with something running at this time (what a process that starts must wake up for).</summary>
    public async Task<IReadOnlyList<(long CharacterId, CombatState State)>> ActiveAsync(long nowMs, CancellationToken cancellationToken = default)
    {
        List<(long, CombatState)> active = [];
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q("""
            SELECT character_id, {columns} FROM {schema}.combat_state
            WHERE GREATEST(weapons_until_ms, pvp_until_ms, npc_until_ms, disapproval_until_ms, flag_until_ms) > $1
            """).Replace("{columns}", StateColumns), nowMs);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        while (await row.ReadAsync(cancellationToken))
        {
            active.Add((row.GetInt64(0), new CombatState(row.GetInt64(1), row.GetInt64(2), row.GetInt64(3), row.GetInt64(4), (Flag)row.GetInt16(5), row.GetInt64(6), row.GetInt64(7))));
        }
        return active;
    }

    /// <summary>Sets the level (brought to 0, 1 or 2) and returns what is kept.</summary>
    public async Task<int> SetSafetyAsync(long characterId, double? requested, long nowMs, CancellationToken cancellationToken = default)
    {
        var level = SafetyLevel.Normalize(requested);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await Sql.ExecuteAsync(connection, null, Q("""
            INSERT INTO {schema}.safety (character_id, level, updated_ms) VALUES ($1, $2, $3)
            ON CONFLICT (character_id) DO UPDATE SET level = EXCLUDED.level, updated_ms = EXCLUDED.updated_ms
            """), cancellationToken, characterId, (short)level, nowMs);
        return level;
    }

    /// <summary>
    /// Reads the real security_status relation history, including attrs that RelationStore.HistoryAsync
    /// intentionally omits. The order is newest first, matching RelationStore.HistoryAsync.
    /// </summary>
}
