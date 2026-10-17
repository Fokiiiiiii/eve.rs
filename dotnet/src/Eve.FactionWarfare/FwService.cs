using Eve.Wallet;
using Npgsql;

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

/// <summary>The outcome of an LP donation. <c>TaxChanged</c> names the tax now in percent, <c>PoolOverloaded</c> the LP that still fit, <c>BelowMinimum</c> the minimum.</summary>
public enum DonateOutcome
{
    Accepted,
    BelowMinimum,
    NotEnlisted,
    NotOccupied,
    TaxChanged,
    PoolOverloaded,
    InsufficientLp,
}

/// <summary>The result of a donation: the outcome, the system's pool after it, and the figure the outcome names.</summary>
public sealed record DonateResult(DonateOutcome Outcome, long Pool, long Figure);

/// <summary>A corporation's or alliance's faction status as the client reads it: 0 joining, 1 active, 2 leaving (the client's facwarCorporation* constants).</summary>
public sealed record FwStatus(int Status, long FactionId, long StartMs);

/// <summary>
/// The factional warfare rules applied in transactions: LP donations into system pools (debited from the militia's LP), corporation and alliance
/// enlistment with the downtime-deferred changes, and the system reads. Due enlistment changes are applied when they are read, so no job runs them.
/// </summary>
public sealed class FwService
{
    public const int JoiningStatus = 0;
    public const int ActiveStatus = 1;
    public const int LeavingStatus = 2;

    private readonly FwStore _store;
    private readonly LoyaltyPointStore _loyalty;

    public FwService(FwStore store, LoyaltyPointStore loyalty)
    {
        _store = store;
        _loyalty = loyalty;
    }

    public FwStore Store => _store;

    /// <summary>The LP pool of a system (0 when it is not occupied): the client's <c>GetSolarSystemLPs</c>.</summary>
    public async Task<long> SolarSystemLpsAsync(long solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        return (await _store.SystemAsync(connection, null, solarSystemId, false, cancellationToken))?.LpPool ?? 0;
    }

    /// <summary>
    /// Donates LP to an occupied system. <paramref name="donatedLp"/> is debited from the donor's LP with the faction's militia corporation and
    /// <paramref name="hubLp"/> (what the hub's donation tax leaves) enters the pool. The tax is the client's formula on the faction's share of its
    /// warzone, and the caller must quote it: a stale quote is refused with the tax now.
    /// </summary>
    public async Task<DonateResult> DonateAsync(long solarSystemId, long characterId, long corporationId, long donorFactionId, long donatedLp, long hubLp, long nowMs,
        CancellationToken cancellationToken = default)
    {
        if (donatedLp < FwRules.MinLpDonation)
            return new DonateResult(DonateOutcome.BelowMinimum, 0, FwRules.MinLpDonation);
        if (!FwFactions.IsOccupier(donorFactionId))
            return new DonateResult(DonateOutcome.NotEnlisted, 0, 0);
        var militiaCorporationId = FwFactions.MilitiaCorporationOf(donorFactionId);
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var system = await _store.SystemAsync(connection, transaction, solarSystemId, true, cancellationToken);
        if (system is null)
            return new DonateResult(DonateOutcome.NotOccupied, 0, 0);
        var taxRate = await DonationTaxAsync(connection, transaction, donorFactionId, cancellationToken);
        var expectedHubLp = (long)(donatedLp - donatedLp * taxRate);
        if (hubLp != expectedHubLp)
            return new DonateResult(DonateOutcome.TaxChanged, system.LpPool, (long)Math.Round(taxRate * 100, MidpointRounding.AwayFromZero));
        if (system.LpPool + hubLp > FwRules.MaxLpPool)
            return new DonateResult(DonateOutcome.PoolOverloaded, system.LpPool, FwRules.MaxLpPool - system.LpPool);
        try
        {
            await _loyalty.ApplyAsync(connection, transaction, new LoyaltyHolder(LoyaltyHolderKind.Character, characterId), militiaCorporationId, -donatedLp,
                "fw.donation", $"fw-donation:{solarSystemId}:{nowMs}:{donatedLp}", cancellationToken);
        }
        catch (InsufficientLoyaltyPointsException)
        {
            return new DonateResult(DonateOutcome.InsufficientLp, system.LpPool, 0);
        }
        var pool = await _store.AddLpAsync(connection, transaction, solarSystemId, characterId, corporationId, donorFactionId, hubLp, nowMs, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DonateResult(DonateOutcome.Accepted, pool, 0);
    }

    /// <summary>Every occupied system of the two warzones: the state the client reads as the warzone occupation.</summary>
    public async Task<IReadOnlyList<FwSystem>> OccupiedSystemsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        return await _store.SystemsAsync(connection, null, cancellationToken);
    }

    /// <summary>The donation tax of an occupier now: the client's formula on the faction's systems of its warzone, out of the warzone's systems.</summary>
    public async Task<double> DonationTaxAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long factionId, CancellationToken cancellationToken = default)
    {
        var warzoneId = FwWarzones.Ids.FirstOrDefault(id => FwWarzones.FactionsOf(id).Contains(factionId));
        if (warzoneId == 0)
            return 0.0;
        var systems = await _store.SystemsAsync(connection, transaction, cancellationToken);
        var held = systems.Count(system => system.WarzoneId == warzoneId && system.OccupierId == factionId);
        return FwRules.DonationTax(factionId, held, FwWarzones.SystemsOf(warzoneId).Count);
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

    /// <summary>The faction status of a corporation or alliance, or null when it is in no faction and no change is pending.</summary>
    public async Task<FwStatus?> StatusAsync(int ownerKind, long ownerId, long nowMs, CancellationToken cancellationToken = default)
    {
        var enlistment = await EnlistmentAsync(ownerKind, ownerId, nowMs, cancellationToken);
        if (enlistment.EffectiveMs > 0 && enlistment.NextFactionId != 0)
            return new FwStatus(JoiningStatus, enlistment.NextFactionId, enlistment.ChangedMs);
        if (enlistment.EffectiveMs > 0 && enlistment.FactionId != 0)
            return new FwStatus(LeavingStatus, enlistment.FactionId, enlistment.ChangedMs);
        if (enlistment.FactionId != 0)
            return new FwStatus(ActiveStatus, enlistment.FactionId, enlistment.ChangedMs);
        return null;
    }

    /// <summary>The faction a corporation or alliance is enlisted in at <paramref name="nowMs"/> (0: none).</summary>
    public async Task<long> FactionOfAsync(int ownerKind, long ownerId, long nowMs, CancellationToken cancellationToken = default) =>
        (await EnlistmentAsync(ownerKind, ownerId, nowMs, cancellationToken)).FactionId;

    /// <summary>The corporations enlisted in a faction at <paramref name="nowMs"/>.</summary>
    public async Task<IReadOnlyList<long>> CorporationsInFactionAsync(long factionId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        return await _store.CorporationsInFactionAsync(connection, null, factionId, nowMs, cancellationToken);
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
            current with { NextFactionId = factionId, EffectiveMs = FwRules.NextDowntimeMs(nowMs), ChangedMs = nowMs }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EnlistOutcome.Accepted;
    }

    /// <summary>Requests that a corporation or an alliance leave its faction at the next downtime.</summary>
    public async Task<EnlistOutcome> LeaveAsync(int ownerKind, long ownerId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ResolveAsync(connection, transaction, ownerKind, ownerId, nowMs, cancellationToken);
        if (current.FactionId == 0 || current.EffectiveMs > 0)
            return EnlistOutcome.NothingToWithdraw;
        await _store.SaveEnlistmentAsync(connection, transaction,
            current with { NextFactionId = 0, EffectiveMs = FwRules.NextDowntimeMs(nowMs), ChangedMs = nowMs }, cancellationToken);
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
        await _store.SaveEnlistmentAsync(connection, transaction, current with { NextFactionId = 0, EffectiveMs = 0, ChangedMs = nowMs }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EnlistOutcome.Accepted;
    }

    /// <summary>Reads the enlistment and applies a pending change that is due at <paramref name="nowMs"/> (written back in the same transaction).</summary>
    private async Task<FwEnlistment> ResolveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int ownerKind, long ownerId, long nowMs,
        CancellationToken cancellationToken)
    {
        var row = await _store.EnlistmentAsync(connection, transaction, ownerKind, ownerId, true, cancellationToken)
            ?? new FwEnlistment(ownerKind, ownerId, 0, 0, 0, 0);
        if (row.EffectiveMs <= 0 || row.EffectiveMs > nowMs)
            return row;
        var applied = row with { FactionId = row.NextFactionId, NextFactionId = 0, EffectiveMs = 0, ChangedMs = row.EffectiveMs };
        await _store.SaveEnlistmentAsync(connection, transaction, applied, cancellationToken);
        return applied;
    }
}
