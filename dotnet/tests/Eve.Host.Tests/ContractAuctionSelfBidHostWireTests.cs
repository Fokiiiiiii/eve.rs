using System.Net.Sockets;
using Eve.Data;
using Eve.Host.Persistence;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class HostServerTests
{
    [PgFact]
    public async Task Issuer_cannot_accept_or_bid_on_its_corporation_contracts_after_leaving_the_corporation()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_self_bid_{suffix}";
        var inventorySchema = $"contract_self_bid_items_{suffix}";
        var walletSchema = $"contract_self_bid_wallet_{suffix}";
        const long nowMs = 1_790_646_377_000;
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        const long contractId = 9051;
        const long itemExchangeId = 9052;
        const long courierId = 9053;
        const long issuerId = 42;
        const long issuerCorporationId = 999;

        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        try
        {
            var items = new PgItemStore(source, inventorySchema);
            var wallets = new WalletTransactions(source, walletSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var ownerId in new[] { issuerId, issuerCorporationId })
                    await wallets.EnsureAsync(connection, transaction, ownerId);
                await using var balances = new NpgsqlCommand(
                    $"UPDATE {walletSchema}.balances SET isk = 100000 WHERE owner_id IN ($1, $2) AND account_key = 1000",
                    connection, transaction);
                balances.Parameters.AddWithValue(issuerId);
                balances.Parameters.AddWithValue(issuerCorporationId);
                await balances.ExecuteNonQueryAsync();
                await using var contract = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_records
                        (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued,
                         contract_type, status, availability, assignee_id, start_station_id,
                         date_expired_filetime, price_isk, collateral_isk, acceptor_id,
                         issuer_wallet_account_key, created_at_filetime)
                    VALUES ($1, $2, $3, true, 2, 0, 0, 0, 3001, $4, 1000, 0, 0, 1000, $5),
                           ($6, $2, $3, true, 1, 0, 0, 0, 3001, $4, 1000, 0, 0, 1000, $5),
                           ($7, $2, $3, true, 3, 0, 0, 0, 3001, $4, 1000, 0, 0, 1000, $5)
                    """, connection, transaction);
                contract.Parameters.AddWithValue(contractId);
                contract.Parameters.AddWithValue(issuerId);
                contract.Parameters.AddWithValue(issuerCorporationId);
                contract.Parameters.AddWithValue(nowFileTime + 60 * 10_000_000);
                contract.Parameters.AddWithValue(nowFileTime);
                contract.Parameters.AddWithValue(itemExchangeId);
                contract.Parameters.AddWithValue(courierId);
                await contract.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            StartContractHost(new PersistenceContractWriteModel(source, items, wallets, () => nowMs, contractsSchema));
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 1, "contractProxy", "PlaceBid",
                    new PyTuple([new PyInt(contractId), new PyInt(1000), PyBool.False]),
                    Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));

            var failure = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal("ConCannotAcceptOwnContract", UserErrorKey(failure));

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 2, "contractProxy", "AcceptContract",
                    new PyTuple([new PyInt(itemExchangeId), PyBool.False]),
                    Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
            failure = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal("ConCannotAcceptOwnContract", UserErrorKey(failure));

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 3, "contractProxy", "AcceptContract",
                    new PyTuple([new PyInt(courierId), PyBool.False]),
                    Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
            failure = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal("ConCannotAcceptOwnContract", UserErrorKey(failure));
            Assert.Equal(100000m, await wallets.AccountBalanceAsync(new WalletAccount(issuerId), CancellationToken.None));
            await using var bids = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_bids WHERE contract_id = $1");
            bids.Parameters.AddWithValue(contractId);
            Assert.Equal(0L, (long)(await bids.ExecuteScalarAsync())!);
            await using var status = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_records WHERE status <> 0");
            Assert.Equal(0L, (long)(await status.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var schema in new[] { contractsSchema, inventorySchema, walletSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
