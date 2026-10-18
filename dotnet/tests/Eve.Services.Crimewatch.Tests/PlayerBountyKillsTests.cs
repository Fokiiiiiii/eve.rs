using Eve.Crimewatch;
using Eve.Data;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Services.Crimewatch.Tests;

/// <summary>
/// A pilot's kill claims the bounties on the victim (CH-18): the pools pay a fifth of the loss, pilot first; the claim is recorded once; the credit is paid once,
/// also across a restart (the store and the service are built again).
/// </summary>
public sealed class PlayerBountyKillsTests
{
    private const long Victim = 96_000_010, Hunter = 96_000_011, Placer = 96_000_012, VictimCorporation = 98_000_001, HunterCorporation = 98_000_002;
    private const long ShipId = 96_500_001;
    private const int ShipType = 648;

    [PgFact]
    public async Task A_kill_claims_the_pilot_pool_first_once_and_the_hunter_is_credited_once_across_a_restart()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var crimewatch = "pbk_c_" + suffix;
        var walletSchema = "pbk_w_" + suffix;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        try
        {
            await PgCrimewatchStore.MigrateAsync(source, crimewatch);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await new PgWalletStore(source, walletSchema).OpenAsync(connection, transaction, Hunter, 0m, 0);
                var placements = new PlayerBountyStore(source, crimewatch);
                await placements.RecordPlacementAsync(connection, transaction, new BountyPool(Victim, BountyOwnerKind.Character, VictimCorporation, 0, 0), Placer, 2_000_000m, "placement-1", 1);
                await placements.RecordPlacementAsync(connection, transaction, new BountyPool(VictimCorporation, BountyOwnerKind.Corporation, VictimCorporation, 0, 0), Placer, 1_000_000m, "placement-2", 1);
                await transaction.CommitAsync();
            }

            var scheduled = 0;
            Task Schedule(NpgsqlConnection _, NpgsqlTransaction __, string ___) { scheduled++; return Task.CompletedTask; }
            var kills = Kills(source, crimewatch, walletSchema);

            // the ship is worth 10,000,000: a fifth is 2,000,000, which the pilot's pool (2,000,000) pays in full; the corporation keeps its 1,000,000
            var payout = await kills.ClaimShipLossAsync(Victim, ShipId, ShipType, [], Hunter, 100, Schedule);
            Assert.NotNull(payout);
            Assert.Equal("kill:" + ShipId, payout);
            Assert.Equal(1, scheduled);
            Assert.Equal(0m, await PoolAsync(source, crimewatch, Victim));
            Assert.Equal(1_000_000m, await PoolAsync(source, crimewatch, VictimCorporation));
            Assert.Equal(2_000_000m, await ScalarAsync<decimal>(source,
                $"SELECT bounties_claimed FROM {crimewatch}.player_bounty_hunters WHERE owner_kind = 1 AND hunter_id = {Hunter}"));
            Assert.Equal(1L, await ScalarAsync<long>(source,
                $"SELECT number_of_kills FROM {crimewatch}.player_bounty_hunters WHERE owner_kind = 1 AND hunter_id = {Hunter}"));

            // the same kill again is answered as it was: nothing moves and nothing is scheduled again
            Assert.Equal(payout, await kills.ClaimShipLossAsync(Victim, ShipId, ShipType, [], Hunter, 200, Schedule));
            Assert.Equal(1, scheduled);
            Assert.Equal(1_000_000m, await PoolAsync(source, crimewatch, VictimCorporation));

            // the job credits the hunter once; a second run, and a run after a restart, credit nothing more
            await SettleAsync(source, kills, payout!, 300, walletSchema);
            Assert.Equal(2_000_000m, await IskAsync(source, walletSchema, Hunter));
            await SettleAsync(source, Kills(source, crimewatch, walletSchema), payout!, 400, walletSchema);
            Assert.Equal(2_000_000m, await IskAsync(source, walletSchema, Hunter));
            Assert.Equal("paid", await ScalarAsync<string>(source, $"SELECT status FROM {crimewatch}.player_bounty_payouts WHERE payout_id = 'kill:{ShipId}'"));

            // a hunter who is not a pilot claims nothing
            Assert.Null(await kills.ClaimShipLossAsync(Victim, ShipId + 1, ShipType, [], Placer + 999, 500, Schedule));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {crimewatch} CASCADE; DROP SCHEMA IF EXISTS {walletSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task A_kill_by_a_fleet_member_is_shared_equally_across_the_fleet_and_each_share_is_credited_once()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var crimewatch = "pbk_f_" + suffix;
        var walletSchema = "pbk_fw_" + suffix;
        const long memberOne = 96_000_013, memberTwo = 96_000_014;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        try
        {
            await PgCrimewatchStore.MigrateAsync(source, crimewatch);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var owner in new[] { Hunter, memberOne, memberTwo })
                    await new PgWalletStore(source, walletSchema).OpenAsync(connection, transaction, owner, 0m, 0);
                await new PlayerBountyStore(source, crimewatch).RecordPlacementAsync(connection, transaction,
                    new BountyPool(Victim, BountyOwnerKind.Character, VictimCorporation, 0, 0), Placer, 2_000_000m, "placement-fleet", 1);
                await transaction.CommitAsync();
            }

            var scheduled = new List<string>();
            Task Schedule(NpgsqlConnection _, NpgsqlTransaction __, string payoutId) { scheduled.Add(payoutId); return Task.CompletedTask; }
            var kills = Kills(source, crimewatch, walletSchema);

            // the fleet is the hunter and two members: a fifth of the loss (2,000,000) is split three ways; the hunter keeps the cent that does not divide
            var payout = await kills.ClaimShipLossAsync(Victim, ShipId, ShipType, [], Hunter, 100, Schedule, [Hunter, memberOne, memberTwo]);
            Assert.Equal("kill:" + ShipId, payout);
            Assert.Equal(3, scheduled.Count);
            Assert.Equal(new[] { "kill:" + ShipId, $"kill:{ShipId}:{memberOne}", $"kill:{ShipId}:{memberTwo}" }, scheduled.ToArray());
            Assert.Equal(666_666.68m, await ScalarAsync<decimal>(source, $"SELECT amount FROM {crimewatch}.player_bounty_payouts WHERE payout_id = 'kill:{ShipId}'"));
            Assert.Equal(666_666.66m, await ScalarAsync<decimal>(source, $"SELECT amount FROM {crimewatch}.player_bounty_payouts WHERE payout_id = 'kill:{ShipId}:{memberOne}'"));
            Assert.Equal(2_000_000m, await ScalarAsync<decimal>(source, $"SELECT SUM(amount) FROM {crimewatch}.player_bounty_payouts"));

            // each sharer's own job credits its share, once
            foreach (var id in scheduled)
                await SettleAsync(source, kills, id, 300, walletSchema);
            Assert.Equal(666_666.68m, await IskAsync(source, walletSchema, Hunter));
            Assert.Equal(666_666.66m, await IskAsync(source, walletSchema, memberOne));
            Assert.Equal(666_666.66m, await IskAsync(source, walletSchema, memberTwo));
            Assert.Equal(new[] { "paid", "paid", "paid" }, (await ScalarAsync<string>(source, $"SELECT string_agg(status, ',') FROM {crimewatch}.player_bounty_payouts")).Split(','));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {crimewatch} CASCADE; DROP SCHEMA IF EXISTS {walletSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static PlayerBountyKills Kills(NpgsqlDataSource source, string crimewatch, string walletSchema) => new(
        new PlayerBountyStore(source, crimewatch), crimewatch, new WalletTransactions(source, walletSchema),
        (typeIds, _) => Task.FromResult<IReadOnlyDictionary<long, decimal>>(typeIds.ToDictionary(id => id, id => id == ShipType ? 10_000_000m : 0m)),
        (characterId, _) => Task.FromResult<(long, long)?>(characterId switch
        {
            Victim => (VictimCorporation, 0L),
            Hunter => (HunterCorporation, 0L),
            _ => null,
        }));

    private static async Task SettleAsync(NpgsqlDataSource source, PlayerBountyKills kills, string payoutId, long nowMs, string walletSchema)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await kills.SettlePayoutAsync(connection, transaction, payoutId, nowMs);
        await transaction.CommitAsync();
    }

    private static async Task<decimal> PoolAsync(NpgsqlDataSource source, string crimewatch, long targetId) =>
        await ScalarAsync<decimal>(source, $"SELECT bounty FROM {crimewatch}.player_bounty_pools WHERE target_id = {targetId}");

    private static async Task<decimal> IskAsync(NpgsqlDataSource source, string walletSchema, long owner) =>
        (await new PgWalletStore(source, walletSchema).GetAsync(owner))?.Isk ?? 0m;

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("no row: " + sql);
        return (T)Convert.ChangeType(value, typeof(T));
    }
}
