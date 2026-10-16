using Eve.Crimewatch;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Crimewatch;

/// <summary>The payout job's payload: the payout to credit.</summary>
public sealed record PlayerBountyPayoutDue(string PayoutId);

/// <summary>
/// A pilot's kill claims the bounties on the victim (EveJS <c>claimBountyPayout</c>, wiki "Bounty Hunting System"): a fifth of the loss, paid from the
/// pilot's pool, then its corporation's, then its alliance's. The payout is pending in the claim's transaction and is credited to the hunter's wallet by
/// its job, once (the credit carries the payout's command id as its operation key).
/// </summary>
public sealed class PlayerBountyKills(
    PlayerBountyStore store,
    string schema,
    WalletTransactions wallets,
    Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyDictionary<long, decimal>>> basePrices,
    Func<long, CancellationToken, Task<(long CorporationId, long AllianceId)?>> characterAffiliation)
{
    /// <summary>The durable job that credits a payout (its key is the payout id).</summary>
    public const string PayoutJobKind = "player-bounty.payout";

    /// <summary>The journal entry type of a bounty prize (EveJS <c>JOURNAL_ENTRY_TYPE.NPC_BOUNTY_PRIZE</c>).</summary>
    private const int BountyPrizeJournalEntry = 17;

    /// <summary>The payout a kill of a ship is recorded under.</summary>
    public static string PayoutIdOf(long shipId) => $"kill:{shipId}";

    /// <summary>
    /// Claims the bounties on a pilot killed by another pilot. <paramref name="contents"/> is what the ship held (its modules and cargo); the loss is the ship's base
    /// price and theirs. Answers the payout id, or null when no bounty was due or the hunter is not a pilot.
    /// </summary>
    public async Task<string?> ClaimShipLossAsync(long victimCharacterId, long shipId, int shipTypeId, IReadOnlyList<(int TypeId, long Quantity)> contents,
        long hunterCharacterId, long nowMs, Func<NpgsqlConnection, NpgsqlTransaction, Task> schedule, CancellationToken cancellationToken = default)
    {
        if (hunterCharacterId <= 0 || hunterCharacterId == victimCharacterId)
            return null;
        if (await characterAffiliation(hunterCharacterId, cancellationToken) is not { } hunter)
            return null;

        var typeIds = contents.Select(item => (long)item.TypeId).Append(shipTypeId).Distinct().ToArray();
        var prices = await basePrices(typeIds, cancellationToken);
        var loss = Math.Round(
            prices.GetValueOrDefault(shipTypeId) + contents.Sum(item => prices.GetValueOrDefault(item.TypeId) * item.Quantity),
            2, MidpointRounding.AwayFromZero);

        // the victim's pilot, then its corporation and alliance (the order of the pools drained)
        var victim = await characterAffiliation(victimCharacterId, cancellationToken);
        var targets = new List<long> { victimCharacterId };
        if (victim is { } affiliation)
        {
            if (affiliation.CorporationId > 0) targets.Add(affiliation.CorporationId);
            if (affiliation.AllianceId > 0) targets.Add(affiliation.AllianceId);
        }

        var payoutId = PayoutIdOf(shipId);
        return await store.ClaimKillAsync(new PlayerBountyClaim(
                payoutId, $"player-bounty:{payoutId}:credit", hunterCharacterId, hunter.CorporationId, hunter.AllianceId,
                targets, loss, nowMs),
            schedule, cancellationToken);
    }

    /// <summary>Credits a pending payout to its hunter's wallet and marks it paid, in the job's transaction. A payout that is not pending is left as it is.</summary>
    public async Task SettlePayoutAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string payoutId, long nowMs, CancellationToken cancellationToken = default)
    {
        if (await PlayerBountyStore.PendingPayoutAsync(connection, transaction, schema, payoutId, cancellationToken) is not { } due)
            return;
        await wallets.AdjustAsync(connection, transaction, new WalletAccount(due.HunterId), due.Amount, 0, BountyPrizeJournalEntry,
            "Player bounty payout", nowMs, due.CommandId, $"player-bounty:{payoutId}", cancellationToken);
        await store.MarkPaidAsync(connection, transaction, payoutId, nowMs, cancellationToken);
    }
}
