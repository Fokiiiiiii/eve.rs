using Eve.Services;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches : ICharacterEntryHook
{
    public async ValueTask OnCharacterLeavingAsync(ClientSession session, long characterId, CancellationToken ct)
    {
        if (session.SessionId > 0) await ReleaseSessionAsync(session.SessionId, ct);
    }

    public async ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken ct)
    {
        if (session.CharacterId != characterId || scheduler is null) return;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), connection, tx))
            await gate.ExecuteNonQueryAsync(ct);
        var matches = new List<long>();
        await using (var query = new NpgsqlCommand(Q("""
            SELECT p.match_id FROM {schema}.match_participants p JOIN {schema}.matches m ON m.id=p.match_id
            WHERE p.character_id=$1 AND m.closing
            """), connection, tx))
        {
            query.Parameters.AddWithValue(characterId);
            await using var rows = await query.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) matches.Add(rows.GetInt64(0));
        }
        foreach (var matchId in matches) await ScheduleParticipantRecoveryAsync(connection, tx, matchId, ct);
        await tx.CommitAsync(ct);
        if (matches.Count != 0) scheduler.Wake();
    }
}
