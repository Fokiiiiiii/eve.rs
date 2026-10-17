namespace Eve.FactionWarfare;

/// <summary>The outcome of an enlistment request: accepted (a change is pending or applied) or refused with the reason the client shows.</summary>
public enum EnlistOutcome
{
    Accepted,
    NotAnOccupier,
    AlreadyEnlisted,
    BelowStanding,
    NothingToWithdraw,
}

/// <summary>The outcome of an LP donation.</summary>
public enum DonateOutcome
{
    Accepted,
    BelowMinimum,
    NotOccupied,
    NotEnlisted,
}

/// <summary>
/// The factional warfare rules applied in transactions: LP donations, corporation and alliance enlistment with the downtime-deferred changes,
/// and the system state reads. The enlistment changes are evaluated lazily: a change whose <c>effective_ms</c> has passed is applied on the
/// next read, so no job is needed for it.
/// </summary>
public sealed class FwService
{
    private readonly FwStore _store;

    public FwService(FwStore store) => _store = store;

    public FwStore Store => _store;

    /// <summary>The LP pool of a system (0 when it is not occupied): the client's <c>GetSolarSystemLPs</c>.</summary>
    public async Task<long> SolarSystemLpsAsync(long solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        return (await _store.SystemAsync(connection, null, solarSystemId, false, cancellationToken))?.LpPool ?? 0;
    }

    /// <summary>
    /// Donates LP to an occupied system. The donor must be enlisted in an occupier faction and donate at least <see cref="FwRules.MinLpDonation"/>.
    /// </summary>
    public async Task<(DonateOutcome Outcome, long Pool)> DonateLpAsync(long solarSystemId, long characterId, long corporationId, long donorFactionId, long amount, long nowMs,
        CancellationToken cancellationToken = default)
    {
        if (amount < FwRules.MinLpDonation)
            return (DonateOutcome.BelowMinimum, 0);
        if (!FwFactions.IsOccupier(donorFactionId))
            return (DonateOutcome.NotEnlisted, 0);
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await _store.SystemAsync(connection, transaction, solarSystemId, true, cancellationToken) is null)
            return (DonateOutcome.NotOccupied, 0);
        var pool = await _store.AddLpAsync(connection, transaction, solarSystemId, characterId, corporationId, donorFactionId, amount, nowMs, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (DonateOutcome.Accepted, pool);
    }

    /// <summary>The enlistment of a corporation (kind 1) or an alliance (kind 2) at <paramref name="nowMs"/>, with a change now due applied.</summary>
    public async Task<FwEnlistment> EnlistmentAsync(int ownerKind, long ownerId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var resolved = await ResolveAsync(connection, transaction, ownerKind, ownerId, nowMs, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return resolved;
    }

    /// <summary>
    /// Requests that a corporation or an alliance join an occupier faction at <paramref name="standing"/>. The join takes effect at the next downtime.
    /// </summary>
    public async Task<EnlistOutcome> JoinAsync(int ownerKind, long ownerId, long factionId, double standing, long nowMs, CancellationToken cancellationToken = default)
    {
        if (!FwFactions.IsOccupier(factionId))
            return EnlistOutcome.NotAnOccupier;
        if (!FwRules.MeetsEnlistmentStanding(standing))
            return EnlistOutcome.BelowStanding;
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ResolveAsync(connection, transaction, ownerKind, ownerId, nowMs, cancellationToken);
        if (current.FactionId != 0)
            return EnlistOutcome.AlreadyEnlisted;
        await _store.SaveEnlistmentAsync(connection, transaction,
            current with { NextFactionId = factionId, EffectiveMs = FwRules.NextDowntimeMs(nowMs) }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EnlistOutcome.Accepted;
    }

    /// <summary>Requests that a corporation or an alliance leave its faction at the next downtime.</summary>
    public async Task<EnlistOutcome> LeaveAsync(int ownerKind, long ownerId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ResolveAsync(connection, transaction, ownerKind, ownerId, nowMs, cancellationToken);
        if (current.FactionId == 0)
            return EnlistOutcome.NothingToWithdraw;
        await _store.SaveEnlistmentAsync(connection, transaction,
            current with { NextFactionId = 0, EffectiveMs = FwRules.NextDowntimeMs(nowMs) }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EnlistOutcome.Accepted;
    }

    /// <summary>Withdraws a pending join (the enlistment keeps its faction). Refused when no join is pending.</summary>
    public Task<EnlistOutcome> WithdrawJoinAsync(int ownerKind, long ownerId, long nowMs, CancellationToken cancellationToken = default) =>
        WithdrawAsync(ownerKind, ownerId, nowMs, pendingJoin: true, cancellationToken);

    /// <summary>Withdraws a pending leave (the enlistment keeps its faction). Refused when no leave is pending.</summary>
    public Task<EnlistOutcome> WithdrawLeaveAsync(int ownerKind, long ownerId, long nowMs, CancellationToken cancellationToken = default) =>
        WithdrawAsync(ownerKind, ownerId, nowMs, pendingJoin: false, cancellationToken);

    private async Task<EnlistOutcome> WithdrawAsync(int ownerKind, long ownerId, long nowMs, bool pendingJoin, CancellationToken cancellationToken)
    {
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ResolveAsync(connection, transaction, ownerKind, ownerId, nowMs, cancellationToken);
        var pending = current.EffectiveMs > 0;
        var isJoin = current.NextFactionId != 0;
        if (!pending || isJoin != pendingJoin)
            return EnlistOutcome.NothingToWithdraw;
        await _store.SaveEnlistmentAsync(connection, transaction, current with { NextFactionId = 0, EffectiveMs = 0 },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EnlistOutcome.Accepted;
    }

    /// <summary>Reads the enlistment and applies a pending change that is due at <paramref name="nowMs"/> (written back in the same transaction).</summary>
    private async Task<FwEnlistment> ResolveAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, int ownerKind, long ownerId, long nowMs,
        CancellationToken cancellationToken)
    {
        var row = await _store.EnlistmentAsync(connection, transaction, ownerKind, ownerId, true, cancellationToken)
            ?? new FwEnlistment(ownerKind, ownerId, 0, 0, 0);
        if (row.EffectiveMs <= 0 || row.EffectiveMs > nowMs)
            return row;
        var applied = row with { FactionId = row.NextFactionId, NextFactionId = 0, EffectiveMs = 0 };
        await _store.SaveEnlistmentAsync(connection, transaction, applied, cancellationToken);
        return applied;
    }
}
