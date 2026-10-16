using Eve.Data;
using Npgsql;

namespace Eve.Crimewatch;

/// <summary>What a player bounty can name (EveJS <c>playerBountyState</c> OWNER_KIND): a pilot, a corporation or an alliance.</summary>
public enum BountyOwnerKind : short
{
    Unknown = 0,
    Character = 1,
    Corporation = 2,
    Alliance = 3,
}

/// <summary>The bounty on one target: the total placed, and the target's affiliation as it was when the first bounty was placed (0 when none).</summary>
public sealed record BountyPool(long TargetId, BountyOwnerKind Kind, long CorporationId, long AllianceId, decimal Bounty);

/// <summary>One placement of a bounty: who placed how much on which target.</summary>
public sealed record BountyContribution(long ContributionId, BountyPool Pool, long ContributorId, decimal Amount);

/// <summary>A bounty hunter's claimed bounties and kills (written by the payouts).</summary>
public sealed record BountyHunterStat(BountyOwnerKind Kind, long HunterId, long CorporationId, long AllianceId, decimal BountiesClaimed, int NumberOfKills);

/// <summary>
/// The player bounties in PostgreSQL (tables <c>player_bounty_pools</c>, <c>player_bounty_contributions</c> and <c>player_bounty_hunters</c>, in the
/// crimewatch schema). The wallet debit is the caller's; <see cref="RecordPlacementAsync"/> takes the caller's transaction so both commit together.
/// </summary>
public sealed class PlayerBountyStore(NpgsqlDataSource source, string schema)
{
    private readonly string _schema = Sql.CheckSchema(schema);

    private string Q(string text) => Sql.In(_schema, text);

    private const string PoolColumns = "p.target_id, p.owner_kind, p.corporation_id, p.alliance_id, p.bounty";

    private static BountyPool ReadPool(NpgsqlDataReader row) => new(
        row.GetInt64(0), (BountyOwnerKind)row.GetInt16(1), row.GetInt64(2), row.GetInt64(3), row.GetDecimal(4));

    /// <summary>Adds a placement to the target's pool and records it under <paramref name="operationKey"/>, in the caller's transaction.</summary>
    public async Task<BountyPool> RecordPlacementAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BountyPool affiliation,
        long contributorId, decimal amount, string operationKey, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var pool = Sql.Command(connection, transaction, Q(
            "INSERT INTO {schema}.player_bounty_pools (target_id, owner_kind, corporation_id, alliance_id, bounty, updated_ms) " +
            "VALUES ($1, $2, $3, $4, $5, $6) " +
            "ON CONFLICT (target_id) DO UPDATE SET bounty = {schema}.player_bounty_pools.bounty + EXCLUDED.bounty, updated_ms = EXCLUDED.updated_ms " +
            "RETURNING target_id, owner_kind, corporation_id, alliance_id, bounty"),
            affiliation.TargetId, (short)affiliation.Kind, affiliation.CorporationId, affiliation.AllianceId, amount, nowMs);
        BountyPool result;
        await using (var row = await pool.ExecuteReaderAsync(cancellationToken))
        {
            await row.ReadAsync(cancellationToken);
            result = ReadPool(row);
        }
        await Sql.ExecuteAsync(connection, transaction, Q(
            "INSERT INTO {schema}.player_bounty_contributions (target_id, contributor_id, amount, created_ms, operation_key) VALUES ($1, $2, $3, $4, $5)"),
            cancellationToken, affiliation.TargetId, contributorId, amount, nowMs, operationKey);
        return result;
    }

    /// <summary>The placement that was recorded under <paramref name="operationKey"/>, if one was (a retried placement is not debited or recorded twice).</summary>
    public async Task<BountyPool?> PlacementPoolAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string operationKey, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            $"SELECT {PoolColumns} FROM {{schema}}.player_bounty_contributions c JOIN {{schema}}.player_bounty_pools p ON p.target_id = c.target_id WHERE c.operation_key = $1"),
            operationKey);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        return await row.ReadAsync(cancellationToken) ? ReadPool(row) : null;
    }

    /// <summary>The pools of the targets (a target with no bounty is absent).</summary>
    public async Task<IReadOnlyList<BountyPool>> PoolsOfAsync(IReadOnlyCollection<long> targetIds, CancellationToken cancellationToken = default)
    {
        if (targetIds.Count == 0)
            return [];
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q(
            $"SELECT {PoolColumns} FROM {{schema}}.player_bounty_pools p WHERE p.target_id = ANY($1)"), targetIds.ToArray());
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        var pools = new List<BountyPool>();
        while (await row.ReadAsync(cancellationToken))
            pools.Add(ReadPool(row));
        return pools;
    }

    /// <summary>The largest pools of one owner kind, the biggest first (ties by target id), at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<BountyPool>> TopPoolsAsync(BountyOwnerKind kind, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q(
            $"SELECT {PoolColumns} FROM {{schema}}.player_bounty_pools p WHERE p.owner_kind = $1 AND p.bounty > 0 ORDER BY p.bounty DESC, p.target_id LIMIT $2"),
            (short)kind, limit);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        var pools = new List<BountyPool>();
        while (await row.ReadAsync(cancellationToken))
            pools.Add(ReadPool(row));
        return pools;
    }

    /// <summary>The position of the target among the pools of its kind (0 is the largest), or null when it has no bounty of that kind.</summary>
    public async Task<int?> RankOfAsync(BountyOwnerKind kind, long targetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q(
            "SELECT (SELECT count(*) FROM {schema}.player_bounty_pools o WHERE o.owner_kind = t.owner_kind AND o.bounty > 0 AND " +
            "(o.bounty > t.bounty OR (o.bounty = t.bounty AND o.target_id < t.target_id))) " +
            "FROM {schema}.player_bounty_pools t WHERE t.target_id = $1 AND t.owner_kind = $2 AND t.bounty > 0"),
            targetId, (short)kind);
        var rank = await command.ExecuteScalarAsync(cancellationToken);
        return rank is long position ? (int)position : null;
    }

    /// <summary>The placements a character made, the newest first.</summary>
    public async Task<IReadOnlyList<BountyContribution>> ContributionsOfAsync(long contributorId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q(
            $"SELECT c.contribution_id, {PoolColumns}, c.contributor_id, c.amount FROM {{schema}}.player_bounty_contributions c " +
            "JOIN {schema}.player_bounty_pools p ON p.target_id = c.target_id WHERE c.contributor_id = $1 ORDER BY c.contribution_id DESC"),
            contributorId);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        var contributions = new List<BountyContribution>();
        while (await row.ReadAsync(cancellationToken))
            contributions.Add(new BountyContribution(row.GetInt64(0), new BountyPool(row.GetInt64(1), (BountyOwnerKind)row.GetInt16(2), row.GetInt64(3), row.GetInt64(4), row.GetDecimal(5)), row.GetInt64(6), row.GetDecimal(7)));
        return contributions;
    }

    /// <summary>The hunters of one owner kind with claimed bounties, the largest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<BountyHunterStat>> TopHuntersAsync(BountyOwnerKind kind, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q(
            "SELECT owner_kind, hunter_id, corporation_id, alliance_id, bounties_claimed, number_of_kills FROM {schema}.player_bounty_hunters " +
            "WHERE owner_kind = $1 AND bounties_claimed > 0 ORDER BY bounties_claimed DESC, number_of_kills DESC, hunter_id LIMIT $2"),
            (short)kind, limit);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        var hunters = new List<BountyHunterStat>();
        while (await row.ReadAsync(cancellationToken))
            hunters.Add(new BountyHunterStat((BountyOwnerKind)row.GetInt16(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3), row.GetDecimal(4), row.GetInt32(5)));
        return hunters;
    }

    /// <summary>
    /// The claim of a kill: the bounty pools of the victim (its pilot, then its corporation, then its alliance) pay up to a fifth of the loss, the hunter's
    /// claimed bounties and kills move, and the payout is recorded pending and its job scheduled (<paramref name="schedule"/>), all in one transaction. A
    /// payout id that was claimed before is answered as it was and nothing moves again. Answers the payout id, or null when no bounty was due.
    /// </summary>
    public async Task<string?> ClaimKillAsync(PlayerBountyClaim claim, Func<NpgsqlConnection, NpgsqlTransaction, Task> schedule, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await Sql.ScalarAsync(connection, transaction, Q("SELECT payout_id FROM {schema}.player_bounty_payouts WHERE payout_id = $1"), cancellationToken, claim.PayoutId) is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return claim.PayoutId;
        }

        var pools = new Dictionary<long, (BountyOwnerKind Kind, long Corporation, long Alliance, decimal Bounty)>();
        await using (var command = Sql.Command(connection, transaction, Q(
            "SELECT target_id, owner_kind, corporation_id, alliance_id, bounty FROM {schema}.player_bounty_pools WHERE target_id = ANY($1) FOR UPDATE"), claim.Targets.ToArray()))
        {
            await using var row = await command.ExecuteReaderAsync(cancellationToken);
            while (await row.ReadAsync(cancellationToken))
                pools[row.GetInt64(0)] = ((BountyOwnerKind)row.GetInt16(1), row.GetInt64(2), row.GetInt64(3), row.GetDecimal(4));
        }
        // the targets in their claim order, each with a bounty
        var due = claim.Targets.Where(id => pools.TryGetValue(id, out var pool) && pool.Bounty > 0).ToArray();
        var total = due.Sum(id => pools[id].Bounty);
        var cap = Math.Round(claim.LossValue * PayoutRatio, 2, MidpointRounding.AwayFromZero);
        var amount = Math.Min(cap, total);
        if (amount <= 0)
            return null;

        var remaining = amount;
        foreach (var id in due)
        {
            if (remaining <= 0)
                break;
            var taken = Math.Min(pools[id].Bounty, remaining);
            remaining -= taken;
            await Sql.ExecuteAsync(connection, transaction, Q(
                "UPDATE {schema}.player_bounty_pools SET bounty = bounty - $2, updated_ms = $3 WHERE target_id = $1"),
                cancellationToken, id, taken, claim.NowMs);
        }

        await AddHunterStatAsync(connection, transaction, BountyOwnerKind.Character, claim.HunterId, claim.HunterCorporationId, claim.HunterAllianceId, amount, claim.NowMs, cancellationToken);
        if (claim.HunterCorporationId > 0)
            await AddHunterStatAsync(connection, transaction, BountyOwnerKind.Corporation, claim.HunterCorporationId, claim.HunterCorporationId, claim.HunterAllianceId, amount, claim.NowMs, cancellationToken);
        if (claim.HunterAllianceId > 0)
            await AddHunterStatAsync(connection, transaction, BountyOwnerKind.Alliance, claim.HunterAllianceId, claim.HunterCorporationId, claim.HunterAllianceId, amount, claim.NowMs, cancellationToken);

        await Sql.ExecuteAsync(connection, transaction, Q(
            "INSERT INTO {schema}.player_bounty_payouts (payout_id, hunter_id, amount, loss_value, cap_amount, status, command_id, created_ms) " +
            "VALUES ($1, $2, $3, $4, $5, 'pending', $6, $7)"),
            cancellationToken, claim.PayoutId, claim.HunterId, amount, claim.LossValue, cap, claim.CommandId, claim.NowMs);
        await schedule(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return claim.PayoutId;
    }

    /// <summary>The payout's hunter, amount and credit command, when it is still pending (null when it is missing or paid).</summary>
    public static async Task<(long HunterId, decimal Amount, string CommandId)?> PendingPayoutAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string payoutId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(connection, transaction, Sql.In(schema,
            "SELECT hunter_id, amount, command_id FROM {schema}.player_bounty_payouts WHERE payout_id = $1 AND status = 'pending' FOR UPDATE"), payoutId);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        return await row.ReadAsync(cancellationToken) ? (row.GetInt64(0), row.GetDecimal(1), row.GetString(2)) : null;
    }

    /// <summary>Marks the payout paid, in the caller's transaction (the credit is in the same one).</summary>
    public async Task MarkPaidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string payoutId, long nowMs, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q(
            "UPDATE {schema}.player_bounty_payouts SET status = 'paid', paid_ms = $2 WHERE payout_id = $1"), cancellationToken, payoutId, nowMs);

    /// <summary>The share of a kill's loss a hunter is paid from the bounties on the victim (EveJS <c>PLAYER_BOUNTY_PAYOUT_LOSS_RATIO</c>).</summary>
    public const decimal PayoutRatio = 0.2m;

    private async Task AddHunterStatAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BountyOwnerKind kind, long hunterId,
        long corporationId, long allianceId, decimal amount, long nowMs, CancellationToken cancellationToken) =>
        await Sql.ExecuteAsync(connection, transaction, Q(
            "INSERT INTO {schema}.player_bounty_hunters (owner_kind, hunter_id, corporation_id, alliance_id, bounties_claimed, number_of_kills, updated_ms) " +
            "VALUES ($1, $2, $3, $4, $5, 1, $6) " +
            "ON CONFLICT (owner_kind, hunter_id) DO UPDATE SET bounties_claimed = {schema}.player_bounty_hunters.bounties_claimed + EXCLUDED.bounties_claimed, " +
            "number_of_kills = {schema}.player_bounty_hunters.number_of_kills + 1, corporation_id = EXCLUDED.corporation_id, alliance_id = EXCLUDED.alliance_id, updated_ms = EXCLUDED.updated_ms"),
            cancellationToken, (short)kind, hunterId, corporationId, allianceId, amount, nowMs);
}

/// <summary>A kill's claim: the pools of the victim (in claim order), the loss, and the hunter with its affiliation.</summary>
public sealed record PlayerBountyClaim(string PayoutId, string CommandId, long HunterId, long HunterCorporationId, long HunterAllianceId,
    IReadOnlyList<long> Targets, decimal LossValue, long NowMs);

