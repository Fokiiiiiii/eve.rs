using System.Text;
using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Wallet;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class WalletAuditHostRpcTests
{
    private const long CharacterId = 140_000_031;
    private const long StoreCorporationId = 1_000_125;
    private const long StationId = 60_000_001;
    private const long OfferId = 20_001;
    private const int RewardTypeId = 1_234;

    [PgFact]
    public async Task GetCorpCashBalance_defaults_omitted_account_key_to_the_selected_division_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var characterSchema = $"wac_{suffix}";
        var walletSchema = $"waw_{suffix}";
        var skillSchema = $"was_{suffix}";
        var corporationSchema = $"wacorp_{suffix}";
        var schemas = new[] { characterSchema, walletSchema, skillSchema, corporationSchema };

        try
        {
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await CorporationStore.MigrateAsync(source, corporationSchema);

            var characters = new PgCharacterStore(source, characterSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var transactions = new WalletTransactions(source, walletSchema);
            long founder;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founder = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(1, $"Wallet Audit {suffix}", 0, 1, 1, 1, 11, 1376, 1000044, null,
                        StationId, 30_000_001, 20_000_001, 10_000_001, null, null));
                await wallets.OpenAsync(connection, transaction, founder, CorporationStore.CorporationFoundingCost + 4_875m, 0);
                await skills.GrantAsync(connection, transaction, founder, [new CharacterSkill(3363, 1, 0)]);
                await transaction.CommitAsync();
            }

            var corporations = new CorporationStore(source, characters, wallets, transactions, skills,
                () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), corporationSchema);
            var corporation = await corporations.CreateAsync(founder,
                new NewCorporation($"Wallet Audit Corp {suffix}", $"W{suffix[..4]}", "", "", 0m),
                $"wallet-audit:{suffix}");
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await transactions.OpenCorporationDivisionsAsync(connection, transaction, corporation.Id);
                for (var accountKey = 1001; accountKey <= 1006; accountKey++)
                    await transactions.AdjustAsync(connection, transaction, new WalletAccount(corporation.Id, accountKey),
                        accountKey, founder, 1, "wallet audit division seed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        $"wallet-audit:{suffix}:division:{accountKey}", $"wallet-audit:{suffix}:division:{accountKey}");
                await transaction.CommitAsync();
            }

            var sink = new PacketCapture();
            var session = Session(sink, founder);
            session.Set("corpid", new PyLong(corporation.Id));
            session.Set("corporationID", new PyLong(corporation.Id));
            session.Set("rolesAtAll", new PyLong(CorporationRoles.Director));
            session.Set("corpAccountKey", new PyInt(1001));
            var registry = new ServiceRegistry();
            registry.Register(new AccountService(wallets, corporations, transactions));
            var dispatcher = new PacketDispatcher(registry);
            var open = SessionOpenFor(session);

            Assert.Equal(new PyReal(0), await CallAsync(dispatcher, open, session, sink, 1, "account", "GetCashBalance",
                [new PyInt(1), new PyInt(1000)]));
            var callId = 2L;
            for (var accountKey = 1001; accountKey <= 1006; accountKey++)
                Assert.Equal(new PyReal(accountKey), await CallAsync(dispatcher, open, session, sink, callId++, "account",
                    "GetCashBalance", [new PyInt(1), new PyInt(accountKey)]));
            Assert.Equal(new PyReal(1001), await CallAsync(dispatcher, open, session, sink, callId++, "account", "GetCashBalance",
                [new PyInt(1)], Py.Dict(("accountKey", PyNone.Instance))));
            Assert.Equal(new PyReal(1006), await CallAsync(dispatcher, open, session, sink, callId++, "account", "GetCashBalance",
                [new PyInt(1), new PyInt(1006)], Py.Dict(("accountKey", PyNone.Instance))));
            Assert.Equal(new PyReal(1002), await CallAsync(dispatcher, open, session, sink, callId++, "account", "GetCashBalance",
                [new PyInt(1), new PyInt(1006)], Py.Dict(("accountKey", new PyInt(1002)))));
            Assert.Equal(new PyReal(1001), await CallAsync(dispatcher, open, session, sink, callId++, "account", "GetCashBalance",
                [new PyInt(1)]));

            session.Set("corpAccountKey", PyNone.Instance);
            session.Set("corpaccountkey", new PyInt(1001));
            Assert.Equal(new PyReal(1001), await CallAsync(dispatcher, open, session, sink, callId++, "account", "GetCashBalance",
                [new PyInt(1)], Py.Dict(("accountKey", PyNone.Instance))));

            session.Set("corpaccountkey", PyNone.Instance);
            Assert.Equal(new PyReal(0), await CallAsync(dispatcher, open, session, sink, callId++, "account", "GetCashBalance",
                [new PyInt(1)]));

            var unauthorizedSink = new PacketCapture();
            var unauthorized = Session(unauthorizedSink, founder + 1);
            unauthorized.Set("corpid", new PyLong(corporation.Id));
            unauthorized.Set("corporationID", new PyLong(corporation.Id));
            unauthorized.Set("corpAccountKey", new PyInt(1001));
            Assert.Contains("CrpAccessDenied", await CallErrorAsync(dispatcher, SessionOpenFor(unauthorized), unauthorized,
                unauthorizedSink, callId, "account", "GetCashBalance", [new PyInt(1)], Py.Dict(("accountKey", PyNone.Instance))));
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Lp_store_journal_entry_type_resolves_through_the_registered_account_rpc()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var walletSchema = $"wlp_{suffix}";
        var inventorySchema = $"wli_{suffix}";

        try
        {
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgItemStore.MigrateAsync(source, inventorySchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var transactions = new WalletTransactions(source, walletSchema);
            var loyalty = new LoyaltyPointStore(source, walletSchema);
            var items = new PgItemStore(source, inventorySchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.OpenAsync(connection, transaction, CharacterId, 1_000m, 0);
                await transaction.CommitAsync();
            }

            var offerStore = new LpStoreOfferStore(source, walletSchema);
            await offerStore.ReplaceAsync(StoreCorporationId,
                [new LpStoreOffer(StoreCorporationId, OfferId, RewardTypeId, 1, 0, 125, 0, [])], DateTimeOffset.UtcNow);
            var offerCache = new LpStoreOfferCache(offerStore, null, () => DateTimeOffset.UtcNow);
            var purchases = new LpStorePurchases(source, walletSchema, transactions, loyalty, items,
                (typeId, _) => Task.FromResult<ItemType?>(typeId == RewardTypeId ? new ItemType(RewardTypeId, 1, 4) : null),
                offerCache);
            var lpStore = new LpStoreMgrService(offerCache, purchases,
                (_, _, _) => ValueTask.FromResult(false),
                (_, _, _) => ValueTask.FromResult(true));

            var sink = new PacketCapture();
            var session = Session(sink, CharacterId);
            session.Set("stationid", new PyInt(StationId));
            var sessions = new CharacterSessions();
            sessions.Enter(CharacterId, session);
            using var announcer = new WalletAnnouncer(transactions, sessions);
            var registry = new ServiceRegistry();
            registry.Register(new AccountService(wallets, transactions: transactions));
            registry.Register(lpStore);
            var dispatcher = new PacketDispatcher(registry);
            var open = SessionOpenFor(session);

            var offers = Assert.IsType<PyList>(await CallAsync(dispatcher, open, session, sink, 1, "LPStoreMgr",
                "GetAvailableOffersFromCorp", [new PyInt(StoreCorporationId)]));
            Assert.Single(offers.Items);
            Assert.Equal(PyBool.True, await CallAsync(dispatcher, open, session, sink, 2, "LPStoreMgr",
                "TakeOfferForCharacter", [new PyInt(StoreCorporationId), new PyInt(OfferId), new PyInt(1)]));

            var sent = sink.Sent.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
            Assert.Equal([MachoMessage.Notification, MachoMessage.Notification, MachoMessage.CallRsp],
                sent.Select(packet => packet.Type));
            Assert.Equal("OnAccountChange", sent[0].Dest.BroadcastId);
            Assert.Equal("OnItemChange", sent[1].Dest.BroadcastId);

            var journal = Assert.IsType<PyList>(await CallAsync(dispatcher, open, session, sink, 3, "account", "GetTransactions",
                [new PyInt(1000), PyNone.Instance, PyNone.Instance, PyBool.False]));
            var purchase = Assert.Single(journal.Items);
            Assert.Equal(new PyInt(LpStorePurchases.PaymentToLpStoreRefType), Field(purchase, "entryTypeID"));
            Assert.Equal(new PyReal(-125), Field(purchase, "amount"));
            Assert.Equal(new PyReal(875), Field(purchase, "balance"));

            var cached = Assert.IsType<PyObject>(await CallAsync(dispatcher, open, session, sink, 4, "account", "GetEntryTypes", []));
            var cachedParts = Assert.IsType<PyTuple>(cached.Args).Items;
            var entryTypes = Assert.IsType<PyList>(Assert.IsType<PySubStream>(cachedParts[1]).Decoded);
            var entryType = Assert.Single(entryTypes.Items,
                row => Field(row, "entryTypeID") is PyInt { Value: LpStorePurchases.PaymentToLpStoreRefType });
            Assert.Equal(new PyStr("PaymentToLPStore"), Field(entryType, "entryTypeName"));
            Assert.Equal(new PyInt(59_818), Field(entryType, "entryTypeNameID"));
        }
        finally
        {
            await using (var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {walletSchema} CASCADE"))
                await cleanup.ExecuteNonQueryAsync();
            await using (var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {inventorySchema} CASCADE"))
                await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Personal_cash_balance_without_a_wallet_is_a_wire_real_zero()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var walletSchema = $"wpl_{Guid.NewGuid():N}"[..14];
        try
        {
            await PgWalletStore.MigrateAsync(source, walletSchema);
            var sink = new PacketCapture();
            var session = Session(sink, CharacterId);
            var registry = new ServiceRegistry();
            registry.Register(new AccountService(new PgWalletStore(source, walletSchema)));
            var dispatcher = new PacketDispatcher(registry);

            Assert.Equal(new PyReal(0), await CallAsync(dispatcher, SessionOpenFor(session), session, sink,
                1, "account", "GetCashBalance", [new PyInt(0)]));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {walletSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static ClientSession Session(PacketCapture sink, long characterId) => new(sink)
    {
        SessionId = 908,
        ClientId = 909,
        UserId = 910,
        CharacterId = characterId,
    };

    private static SessionOpen SessionOpenFor(ClientSession session) => new(
        (ulong)session.SessionId, (ulong)session.ClientId, (uint)session.UserId.GetValueOrDefault(), 0,
        "127.0.0.1", "EN", "GB");

    private static async Task<PyValue?> CallAsync(PacketDispatcher dispatcher, SessionOpen open, ClientSession session,
        PacketCapture sink, long callId, string service, string method, PyValue[] args, PyValue? kwargs = null)
    {
        sink.Sent.Clear();
        var request = kwargs is null
            ? Packets.Call(open, callId, service, method, args)
            : Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), kwargs);
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(request));
        Assert.True(await dispatcher.DispatchAsync(decoded, session));
        var replies = sink.Sent.Where(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp).ToArray();
        return Result(Assert.Single(replies));
    }

    private static async Task<string> CallErrorAsync(PacketDispatcher dispatcher, SessionOpen open, ClientSession session,
        PacketCapture sink, long callId, string service, string method, PyValue[] args, PyValue? kwargs = null)
    {
        sink.Sent.Clear();
        var request = kwargs is null
            ? Packets.Call(open, callId, service, method, args)
            : Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), kwargs);
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(request));
        Assert.True(await dispatcher.DispatchAsync(decoded, session));
        var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Sent)));
        Assert.Equal(MachoMessage.ErrorResponse, response.Type);
        return Encoding.ASCII.GetString(MarshalCodec.Encode(sink.Sent[0]));
    }

    private static PyValue? Result(PyValue response)
    {
        var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)state[0]).Value);
        var payload = Assert.IsType<PyTuple>(state[4]);
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    private static PyValue Field(PyValue row, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries
            .Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
