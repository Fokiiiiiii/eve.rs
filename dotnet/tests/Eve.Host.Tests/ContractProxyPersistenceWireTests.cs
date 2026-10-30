using System.Net;
using System.Net.Sockets;
using Eve.Data;
using Eve.Host;
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

public partial class HostServerTests
{
    [Fact]
    public async Task Accept_contract_runs_through_host_and_persists_courier_state_and_notification()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_accept_wire_{suffix}";
        var inventorySchema = $"contract_accept_wire_items_{suffix}";
        var walletSchema = $"contract_accept_wire_wallet_{suffix}";
        var schedulerSchema = $"contract_accept_wire_scheduler_{suffix}";
        var eventsSchema = $"contract_accept_wire_events_{suffix}";
        const long nowMs = 1_800_000_000_000;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
        await Eve.Events.EventBus.MigrateAsync(source, eventsSchema);
        try
        {
            var itemStore = new PgItemStore(source, inventorySchema);
            var wallets = new WalletTransactions(source, walletSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            const long contractId = 7001;
            const long issuerId = 100;
            const long stationId = 3001;
            var expiry = Eve.Services.Py.FileTimeFromUnixMs(nowMs + 86_400_000);
            var created = Eve.Services.Py.FileTimeFromUnixMs(nowMs - 1000);
            long wrapItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                wrapItemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(3468, 1, 9_300_000_000 + contractId, ItemFlags.Hangar, Singleton: true), CancellationToken.None);
                await using var seed = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_records
                        (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued,
                         contract_type, status, availability, start_station_id, end_station_id,
                         date_expired_filetime, created_at_filetime, plastic_wrap_item_id, num_days)
                    VALUES ($1, $2, 84, false, 3, 0, 0, $3, 4001, $4, $5, $6, 2)
                    """, connection, transaction);
                seed.Parameters.AddWithValue(contractId);
                seed.Parameters.AddWithValue(issuerId);
                seed.Parameters.AddWithValue(stationId);
                seed.Parameters.AddWithValue(expiry);
                seed.Parameters.AddWithValue(created);
                seed.Parameters.AddWithValue(wrapItemId);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => nowMs,
                contractsSchema, events: events);
            StartContractHost(writer);
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "contractProxy", "AcceptContract", new PyTuple([new PyInt(contractId), PyBool.False]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));

            PyValue? result = null;
            while (result is null)
            {
                var send = Assert.IsType<Send>(await front.ReceiveAsync());
                var response = MarshalCodec.Decode(send.Payload);
                if (response is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                    MachoAddress.Decode(args.Items[2]).CallId != new PyInt(1))
                    continue;
                Assert.Equal(new PyInt(7), args.Items[0]);
                result = Result(response);
            }

            var accepted = Assert.IsType<PyObject>(result);
            var fields = Fields(accepted);
            Assert.Equal(new PyInt(contractId), fields["contractID"]);
            Assert.Equal(new PyInt(3), fields["type"]);
            Assert.Equal(new PyInt(1), fields["status"]);
            Assert.Equal(new PyInt(issuerId), fields["issuerID"]);
            Assert.Equal(new PyInt(42), fields["acceptorID"]);
            Assert.True(((PyLong)fields["dateAccepted"]).Value > 0);

            await using (var state = source.CreateCommand($"SELECT status, acceptor_id, date_accepted_filetime FROM {contractsSchema}.contract_records WHERE contract_id=$1"))
            {
                state.Parameters.AddWithValue(contractId);
                await using var reader = await state.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)1, reader.GetInt16(0));
                Assert.Equal(42L, reader.GetInt64(1));
                Assert.True(reader.GetInt64(2) > 0);
            }
            var movedWrap = await itemStore.GetAsync(wrapItemId, CancellationToken.None);
            Assert.NotNull(movedWrap);
            Assert.Equal((42L, stationId, ItemFlags.Hangar), (movedWrap.OwnerId, movedWrap.LocationId, movedWrap.Flag));
            await using var notification = source.CreateCommand($"SELECT payload->>'name' FROM {eventsSchema}.outbox WHERE topic=$1 ORDER BY id");
            notification.Parameters.AddWithValue(ContractNotifications.Topic);
            Assert.Equal("OnContractAccepted", (string?)await notification.ExecuteScalarAsync());
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
