using System.Collections.Immutable;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private readonly SemaphoreSlim _restrictionRefresh = new(1, 1);
    private readonly object _restrictionSync = new();
    private ImmutableDictionary<long, int>? _restrictionSnapshot;
    private ImmutableDictionary<long, int> _restrictionPending = ImmutableDictionary<long, int>.Empty;
    private ImmutableDictionary<long, long> _restrictionRepair = ImmutableDictionary<long, long>.Empty;
    private long _restrictionRepairVersion;
    private const string RestrictionRepairJob = "tournament.restrictions.reconcile";

    public bool IsTournamentRestricted(long characterId, TournamentRestriction restriction)
    {
        if (Volatile.Read(ref _restrictionPending).ContainsKey(characterId) ||
            Volatile.Read(ref _restrictionRepair).ContainsKey(characterId)) return true;
        var snapshot = Volatile.Read(ref _restrictionSnapshot);
        if (snapshot is null) return true;
        var mask = restriction switch { TournamentRestriction.Move => 1, TournamentRestriction.Warp => 2,
            TournamentRestriction.Lock => 4, _ => throw new ArgumentOutOfRangeException(nameof(restriction)) };
        return snapshot.TryGetValue(characterId, out var flags) && (flags & mask) != 0;
    }

    private void RestrictionFailClosed(IEnumerable<long> characters)
    {
        lock (_restrictionSync)
        {
            var pending = _restrictionPending;
            foreach (var character in characters.Distinct())
                pending = pending.SetItem(character, pending.TryGetValue(character, out var count) ? count + 1 : 1);
            Volatile.Write(ref _restrictionPending, pending);
        }
    }

    private void RestrictionNeedsRepair(IEnumerable<long> characters)
    {
        lock (_restrictionSync)
        {
            var pending = _restrictionPending;
            var repair = _restrictionRepair;
            foreach (var character in characters.Distinct())
            {
                if (pending.TryGetValue(character, out var count))
                    pending = count <= 1 ? pending.Remove(character) : pending.SetItem(character, count - 1);
                repair = repair.SetItem(character, ++_restrictionRepairVersion);
            }
            // Publish the repair mask before releasing this operation's pending mask.
            // Lock-free readers can therefore never observe both masks absent.
            Volatile.Write(ref _restrictionRepair, repair);
            Volatile.Write(ref _restrictionPending, pending);
        }
    }

    public async Task InitializeTournamentRestrictionsAsync(CancellationToken ct = default)
    {
        await RefreshTournamentRestrictionsAsync(ct);
        if (scheduler is not null)
            await scheduler.EnsureRecurringAsync(RestrictionRepairJob, "projection", TimeSpan.FromSeconds(5));
    }

    private async Task ReconcileTournamentRestrictionsAsync(Eve.Scheduler.JobContext job, CancellationToken ct)
    {
        var repair = Volatile.Read(ref _restrictionRepair);
        if (repair.Count == 0) return;
        await RefreshTournamentRestrictionsAsync(ct);
        lock (_restrictionSync)
        {
            var current = _restrictionRepair;
            foreach (var pair in repair)
                if (current.TryGetValue(pair.Key, out var version) && version == pair.Value) current = current.Remove(pair.Key);
            Volatile.Write(ref _restrictionRepair, current);
        }
    }

    private async Task RefreshTournamentRestrictionsAsync(CancellationToken ct, IEnumerable<long>? finalized = null)
    {
        await _restrictionRefresh.WaitAsync(ct);
        try
        {
            var builder = ImmutableDictionary.CreateBuilder<long, int>();
            await using var c = await source.OpenConnectionAsync(ct);
            await using var query = new NpgsqlCommand(Q("""
                SELECT p.character_id,m.restrict_move,m.restrict_warp,m.restrict_lock
                FROM {schema}.match_participants p JOIN {schema}.matches m ON m.id=p.match_id
                """), c);
            await using var rows = await query.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
                builder[rows.GetInt64(0)] = (rows.GetBoolean(1) ? 1 : 0) | (rows.GetBoolean(2) ? 2 : 0) | (rows.GetBoolean(3) ? 4 : 0);
            lock (_restrictionSync)
            {
                Volatile.Write(ref _restrictionSnapshot, builder.ToImmutable());
                var pending = _restrictionPending;
                foreach (var character in finalized?.Distinct() ?? [])
                    if (pending.TryGetValue(character, out var count))
                        pending = count <= 1 ? pending.Remove(character) : pending.SetItem(character, count - 1);
                Volatile.Write(ref _restrictionPending, pending);
            }
        }
        finally { _restrictionRefresh.Release(); }
    }
}
