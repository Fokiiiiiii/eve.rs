using Eve.Data;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Wallet;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Registered_lp_store_uses_only_the_largest_non_singleton_hangar_stack_for_requirements()
    {
        const long characterId = 90_101;
        const long storeCorporationId = 980_101;
        const long stationId = 60_000_001;
        const long offerId = 70_010;
        const int requiredTypeId = 34;
        const int rewardTypeId = 35;
        const long requiredQuantity = 2;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var walletSchema = "lprs_w_" + suffix;
        var inventorySchema = "lprs_i_" + suffix;
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);

        try
        {
            var wallets = new WalletTransactions(source, walletSchema);
            var loyalty = new LoyaltyPointStore(source, walletSchema);
            var items = new PgItemStore(source, inventorySchema);
            var offerStore = new LpStoreOfferStore(source, walletSchema);
            await offerStore.ReplaceAsync(storeCorporationId,
                [new LpStoreOffer(storeCorporationId, offerId, rewardTypeId, 1, 0, 0, 0,
                    [new LpStoreRequirement(requiredTypeId, requiredQuantity)])], DateTimeOffset.UtcNow);
            var offers = new LpStoreOfferCache(offerStore, null, () => DateTimeOffset.UtcNow);
            var purchases = new LpStorePurchases(source, walletSchema, wallets, loyalty, items,
                (typeId, _) => Task.FromResult<ItemType?>(typeId switch
                {
                    requiredTypeId => new ItemType(requiredTypeId, 18, 4),
                    rewardTypeId => new ItemType(rewardTypeId, 18, 4),
                    _ => null,
                }), offers, () => FixedNowMs);
            var service = new LpStoreMgrService(offers, purchases,
                (_, _, _) => ValueTask.FromResult(true),
                (_, _, _) => ValueTask.FromResult(true));

            var initialItems = new List<(long Id, bool Singleton, long Quantity)>();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                initialItems.Add((await items.CreateAsync(connection, transaction,
                    new NewItem(requiredTypeId, characterId, stationId, ItemFlags.Hangar, Singleton: true)), true, 1));
                initialItems.Add((await items.CreateAsync(connection, transaction,
                    new NewItem(requiredTypeId, characterId, stationId, ItemFlags.Hangar, Quantity: 1)), false, 1));
                initialItems.Add((await items.CreateAsync(connection, transaction,
                    new NewItem(requiredTypeId, characterId, stationId, ItemFlags.Hangar, Quantity: 1)), false, 1));
                await transaction.CommitAsync();
            }

            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    session.Set("stationid", new PyLong(stationId));
                });

            using var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            uint callId = 1;

            async Task<(MachoPacket Packet, PyValue Wire)> SendRpc(string method, PyValue args)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId++, "LPStoreMgr", method, args, kwargs))));
                var response = Assert.IsType<Send>(await front.ReceiveAsync());
                var wire = MarshalCodec.Decode(response.Payload);
                return (Assert.IsType<MachoPacket>(MachoPacket.Decode(wire)), wire);
            }

            var (offerReply, offerWire) = await SendRpc("GetAvailableOffersFromCorp", Py.Tuple(new PyLong(storeCorporationId)));
            Assert.Equal(MachoMessage.CallRsp, offerReply.Type);
            var offerRows = Assert.IsType<PyList>(Result(offerWire));
            var offer = Assert.IsType<PyObject>(Assert.Single(offerRows.Items));
            var offerFields = Assert.IsType<PyDict>(offer.Args);
            var requirement = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(
                Assert.Single(offerFields.Entries, entry => JsSemantics.StringValue(entry.Key) == "reqItems").Value).Items)).Items;
            Assert.Equal(new PyInt(requiredTypeId), requirement[0]);
            Assert.Equal(new PyInt(requiredQuantity), requirement[1]);

            var (refusedReply, _) = await SendRpc("TakeOfferForCharacter",
                Py.Tuple(new PyLong(storeCorporationId), new PyLong(offerId), new PyInt(1)));
            Assert.Equal(MachoMessage.ErrorResponse, refusedReply.Type);
            foreach (var seeded in initialItems)
            {
                var item = Assert.IsType<Item>(await items.GetAsync(seeded.Id));
                Assert.Equal((seeded.Singleton, seeded.Quantity), (item.Singleton, item.Quantity));
            }
            Assert.Equal(0L, await LpPurchaseRowsAsync(source, walletSchema, characterId));

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var seeded in initialItems)
                    await items.DeleteAsync(connection, transaction, seeded.Id);
                await transaction.CommitAsync();
            }

            long validStackId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                validStackId = await items.CreateAsync(connection, transaction,
                    new NewItem(requiredTypeId, characterId, stationId, ItemFlags.Hangar, Quantity: 3));
                await transaction.CommitAsync();
            }

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, callId++, "LPStoreMgr", "TakeOfferForCharacter",
                Py.Tuple(new PyLong(storeCorporationId), new PyLong(offerId), new PyInt(1)), kwargs))));
            var requiredItemNotice = Assert.IsType<Send>(await front.ReceiveAsync());
            var requiredItemPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(requiredItemNotice.Payload)));
            Assert.Equal(MachoMessage.Notification, requiredItemPacket.Type);
            Assert.Equal("OnItemChange", requiredItemPacket.Dest.BroadcastId);
            Assert.Equal("clientID", requiredItemPacket.Dest.IdType);
            var consumedItemArgs = NotificationArgs(requiredItemPacket);
            var consumedItemRow = Assert.IsType<PyPackedRow>(consumedItemArgs.Items[0]);
            var consumedItemFields = consumedItemRow.Fields();
            Assert.Equal(InventoryWire.Columns, consumedItemFields.Select(field => field.Key));
            Assert.Equal(
                [InventoryWire.Id(validStackId), InventoryWire.Id(requiredTypeId), InventoryWire.Id(characterId),
                    InventoryWire.Id(stationId), new PyInt(ItemFlags.Hangar), InventoryWire.Id(1), new PyInt(18), new PyInt(4)],
                consumedItemFields.Take(8).Select(field => field.Value));
            Assert.Equal(
                [new KeyValuePair<PyValue, PyValue>(new PyInt(9), InventoryWire.Id(3))],
                Assert.IsType<PyDict>(consumedItemArgs.Items[1]).Entries);
            var rewardItemNotice = Assert.IsType<Send>(await front.ReceiveAsync());
            var rewardItemPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(rewardItemNotice.Payload)));
            Assert.Equal(MachoMessage.Notification, rewardItemPacket.Type);
            Assert.Equal("OnItemChange", rewardItemPacket.Dest.BroadcastId);
            Assert.Equal("clientID", rewardItemPacket.Dest.IdType);
            var grantedItemArgs = NotificationArgs(rewardItemPacket);
            var grantedItemFields = Assert.IsType<PyPackedRow>(grantedItemArgs.Items[0]).Fields();
            Assert.Equal(InventoryWire.Id(rewardTypeId), grantedItemFields[1].Value);
            Assert.Equal(InventoryWire.Id(1), grantedItemFields[5].Value);
            Assert.Empty(Assert.IsType<PyDict>(grantedItemArgs.Items[1]).Entries);
            var purchaseReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var purchaseWire = MarshalCodec.Decode(purchaseReply.Payload);
            var purchasePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(purchaseWire));
            Assert.Equal(MachoMessage.CallRsp, purchasePacket.Type);
            Assert.Equal(PyBool.True, Result(purchaseWire));

            var remainingStack = Assert.IsType<Item>(await items.GetAsync(validStackId));
            Assert.Equal(1L, remainingStack.Quantity);
            var owned = await items.OwnedByAsync(characterId);
            var granted = Assert.Single(owned, item => item.TypeId == rewardTypeId);
            Assert.Equal((stationId, ItemFlags.Hangar, 1L), (granted.LocationId, granted.Flag, granted.Quantity));
            Assert.Equal(1L, await LpPurchaseRowsAsync(source, walletSchema, characterId));

            long concurrentStackId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                concurrentStackId = await items.CreateAsync(connection, transaction,
                    new NewItem(requiredTypeId, characterId, stationId, ItemFlags.Hangar, Quantity: 3));
                await transaction.CommitAsync();
            }

            using var contender = await Handshaken(await ConnectAsync());
            var contenderOpen = Open();
            await contender.SendAsync(contenderOpen);
            _ = await contender.ReceiveAsync();
            var firstTake = new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, callId++, "LPStoreMgr", "TakeOfferForCharacter",
                Py.Tuple(new PyLong(storeCorporationId), new PyLong(offerId), new PyInt(1)), kwargs)));
            var secondTake = new Packet(contenderOpen.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                contenderOpen, 1, "LPStoreMgr", "TakeOfferForCharacter",
                Py.Tuple(new PyLong(storeCorporationId), new PyLong(offerId), new PyInt(1)), kwargs)));
            await Task.WhenAll(front.SendAsync(firstTake), contender.SendAsync(secondTake));

            async Task<IReadOnlyList<MachoPacket>> ReceiveThroughReply(TestFront client)
            {
                var packets = new List<MachoPacket>();
                while (true)
                {
                    var sent = Assert.IsType<Send>(await client.ReceiveAsync());
                    var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(sent.Payload)));
                    packets.Add(packet);
                    if (packet.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse) return packets;
                }
            }

            var concurrentReplies = await Task.WhenAll(ReceiveThroughReply(front), ReceiveThroughReply(contender));
            var successfulPurchase = Assert.Single(concurrentReplies, packets => packets[^1].Type == MachoMessage.CallRsp);
            var refusedPurchase = Assert.Single(concurrentReplies, packets => packets[^1].Type == MachoMessage.ErrorResponse);
            Assert.Equal(new[] { "OnItemChange", "OnItemChange", null },
                successfulPurchase.Select(packet => packet.Dest.BroadcastId).ToArray());
            Assert.Single(refusedPurchase);
            Assert.Equal(1L, Assert.IsType<Item>(await items.GetAsync(validStackId)).Quantity);
            Assert.Equal(1L, Assert.IsType<Item>(await items.GetAsync(concurrentStackId)).Quantity);
            Assert.Equal(2L, await LpPurchaseRowsAsync(source, walletSchema, characterId));
            Assert.Equal(2, (await items.OwnedByAsync(characterId)).Count(item => item.TypeId == rewardTypeId));
        }
        finally
        {
            foreach (var schema in new[] { walletSchema, inventorySchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<long> LpPurchaseRowsAsync(NpgsqlDataSource source, string walletSchema, long characterId)
    {
        await using var command = source.CreateCommand($"SELECT count(*) FROM {walletSchema}.lp_store_purchases WHERE character_id = $1");
        command.Parameters.AddWithValue(characterId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
