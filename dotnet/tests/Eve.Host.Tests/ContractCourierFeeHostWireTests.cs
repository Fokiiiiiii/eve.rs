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
    public async Task Registered_public_courier_creation_does_not_charge_sales_tax()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_courier_tax_{suffix}";
        var inventorySchema = $"contract_courier_tax_items_{suffix}";
        var walletSchema = $"contract_courier_tax_wallet_{suffix}";
        var schedulerSchema = $"contract_courier_tax_scheduler_{suffix}";
        var eventsSchema = $"contract_courier_tax_events_{suffix}";
        const long issuerId = 42;
        var nowMs = FixedNowMs;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
        await Eve.Events.EventBus.MigrateAsync(source, eventsSchema);
        try
        {
            var items = new PgItemStore(source, inventorySchema);
            var wallets = new WalletTransactions(source, walletSchema);
            long offeredItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, issuerId);
                await using var balance = new NpgsqlCommand(
                    $"UPDATE {walletSchema}.balances SET isk = 50000000 WHERE owner_id = $1 AND account_key = 1000",
                    connection, transaction);
                balance.Parameters.AddWithValue(issuerId);
                await balance.ExecuteNonQueryAsync();
                offeredItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, issuerId, 3001, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }

            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            var expiryJobs = new ContractExpiryJobs(source, items, wallets, scheduler, contractsSchema, events);
            var writer = new PersistenceContractWriteModel(source, items, wallets, () => nowMs,
                contractsSchema, expiryJobs, events);
            StartContractHost(writer, characterIdForAccount: accountId => accountId,
                contractSkillLevel: (_, _, _) => ValueTask.FromResult(0));
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            var kwargs = Eve.Services.Py.Dict(("machoVersion", new PyInt(1)), ("confirm", PyNone.Instance));
            var contractInfo = new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
                new PyInt(3), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(7),
                new PyInt(3001), new PyInt(3002), new PyInt(0), new PyInt(20_000_000), new PyInt(5_000_000),
                new PyStr("Courier fee timing"), new PyStr(""),
                new PyList([new PyTuple([new PyLong(offeredItemId), new PyInt(1)])]),
                new PyInt(ItemFlags.Hangar), new PyList([]), PyBool.False, PyBool.False]));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 1, "contractProxy", "CreateContract",
                    new PyTuple([contractInfo]), kwargs))));
            PyValue? response = null;
            while (response is null)
            {
                var send = Assert.IsType<Send>(await front.ReceiveAsync());
                var packet = MarshalCodec.Decode(send.Payload);
                if (packet is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                    MachoAddress.Decode(args.Items[2]).CallId != new PyInt(1))
                    continue;
                Assert.Equal(new PyInt(7), args.Items[0]);
                response = Result(packet);
            }

            var contractId = Assert.IsType<PyInt>(Assert.Single(Assert.IsType<PyList>(response).Items)).Value;
            await using (var terms = source.CreateCommand($"""
                SELECT contract_type, status, reward_escrow, creation_fee_isk, deposit_isk, sales_tax_isk
                FROM {contractsSchema}.contract_records WHERE contract_id = $1
                """))
            {
                terms.Parameters.AddWithValue(contractId);
                await using var reader = await terms.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((3, 0, 20_000_000m, 10_000m, 20_000m, 0m),
                    (reader.GetInt16(0), reader.GetInt16(1), reader.GetDecimal(2), reader.GetDecimal(3),
                        reader.GetDecimal(4), reader.GetDecimal(5)));
            }
            Assert.Equal(29_970_000m, await wallets.AccountBalanceAsync(
                new WalletAccount(issuerId), CancellationToken.None));
            await using (var refs = source.CreateCommand($"""
                SELECT ref_type_id FROM {walletSchema}.journal
                WHERE owner_id = $1 AND reference_id = $2 ORDER BY id
                """))
            {
                refs.Parameters.AddWithValue(issuerId);
                refs.Parameters.AddWithValue(contractId);
                var journalRefs = new List<int>();
                await using var reader = await refs.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    journalRefs.Add(reader.GetInt32(0));
                Assert.Equal(new[] { 83, 72, 74 }, journalRefs);
            }
            var contractItems = await source.CreateCommand($"""
                SELECT count(*) FROM {contractsSchema}.contract_items
                WHERE contract_id = {contractId} AND in_crate AND is_package
                """).ExecuteScalarAsync();
            Assert.Equal(1L, (long)contractItems!);
            Assert.NotNull(await scheduler.FindAsync(ContractExpiryJobs.JobKind, contractId.ToString()));
            await using var createdNotice = source.CreateCommand($"""
                SELECT count(*) FROM {eventsSchema}.outbox
                WHERE topic = $1 AND payload->>'name' = 'OnContractCreated'
                  AND (payload->>'contractId')::bigint = $2
                """);
            createdNotice.Parameters.AddWithValue(Eve.Host.Persistence.ContractNotifications.Topic);
            createdNotice.Parameters.AddWithValue(contractId);
            Assert.Equal(1L, (long)(await createdNotice.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var schema in new[] { contractsSchema, inventorySchema, walletSchema, schedulerSchema, eventsSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
