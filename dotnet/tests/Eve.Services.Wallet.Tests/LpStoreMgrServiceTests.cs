using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Wallet.Tests;

/// <summary>
/// LPStoreMgr: the offers of a corporation's store as the client reads them, and the take of an offer (the character's, and a
/// corporation's with the Director rule) settled in one PostgreSQL transaction (LpStorePurchases). A refusal names its reason and
/// changes nothing.
/// </summary>
public class LpStoreMgrServiceTests
{
    private const long Corporation = 1000125;
    private const long PayerCorporation = 98000001;
    private const long Character = 140000031;
    private const long Station = 60000001;
    private const int Reward = 1234;
    private const int Required = 34;
    private const long Offer = 20001;

    private static readonly ItemType RewardType = new(Reward, 1, 4);
    private static readonly ItemType RequiredType = new(Required, 2, 16);

    [LpPgFact]
    public async Task The_offers_are_answered_as_the_reference_rows()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Store.ReplaceAsync(Corporation, [new LpStoreOffer(Corporation, Offer, Reward, 1, 5000, 100000, 0,
            [new LpStoreRequirement(Required, 10)])], DateTimeOffset.UtcNow);
        var service = rig.Service((_, _, _) => ValueTask.FromResult(false));

        var rows = await service.CallAsync(Call("GetAvailableOffersFromCorp", rig.Pilot(new Sink()), new PyInt(Corporation)));

        var offer = Assert.IsType<PyObject>(Assert.Single(Assert.IsType<PyList>(rows).Items));
        var fields = Assert.IsType<PyDict>(offer.Args).Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        Assert.Equal((long)Reward, ((PyInt)fields["typeID"]).Value);
        Assert.Equal(Offer, ((PyInt)fields["offerID"]).Value);
        Assert.Equal(5000L, ((PyInt)fields["lpCost"]).Value);
        Assert.Equal(100000L, ((PyInt)fields["iskCost"]).Value);
        Assert.Equal(Corporation, ((PyInt)fields["corpID"]).Value);
        Assert.IsType<PyNone>(fields["requiredStandings"]);
        var required = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(fields["reqItems"]).Items));
        Assert.Equal((Required, 10L), (((PyInt)required.Items[0]).Value, ((PyInt)required.Items[1]).Value));
    }

    [LpPgFact]
    public async Task A_character_take_debits_lp_isk_and_items_grants_the_item_and_announces_it()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedOffer(lpCost: 5000, iskCost: 100000, required: 10);
        await rig.Loyalty.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, Character), Corporation, 12000, "seed", "seed:lp");
        await rig.SeedIsk(Character, 1000000m);
        await rig.SeedItem(Character, Required, 25);
        var sink = new Sink();
        var service = rig.Service((_, _, _) => ValueTask.FromResult(false));

        var result = await service.CallAsync(Call("TakeOfferForCharacter", rig.Pilot(sink), new PyInt(Corporation), new PyInt(Offer), new PyInt(2)));

        Assert.True(Assert.IsType<PyBool>(result).Value);
        Assert.Equal(12000L - 2 * 5000L, await rig.LpBalance(new LoyaltyHolder(LoyaltyHolderKind.Character, Character), Corporation));
        Assert.Equal(1000000m - 2 * 100000m, await rig.Isk(Character, 1000));
        Assert.Equal((26, -200000m), await rig.LastIskJournal(Character));
        var required = Assert.Single(await rig.ItemsAt(Character, Required));
        Assert.Equal(5L, required.Quantity);
        var granted = Assert.Single(await rig.ItemsAt(Character, Reward));
        Assert.Equal((Station, ItemFlags.Hangar, 2L), (granted.LocationId, granted.Flag, granted.Quantity));
        Assert.Equal(2, sink.Packets.Count);
        Assert.Equal(1, await rig.PurchaseRows(Character));
    }

    [LpPgFact]
    public async Task A_take_without_enough_loyalty_points_is_refused_and_changes_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedOffer(lpCost: 5000, iskCost: 100000, required: 10);
        await rig.Loyalty.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, Character), Corporation, 4000, "seed", "seed:lp");
        await rig.SeedIsk(Character, 1000000m);
        await rig.SeedItem(Character, Required, 25);
        var service = rig.Service((_, _, _) => ValueTask.FromResult(false));

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(Call("TakeOfferForCharacter", rig.Pilot(new Sink()),
            new PyInt(Corporation), new PyInt(Offer), new PyInt(1))).AsTask());

        Assert.IsType<MachoWrappedException>(refused);
        Assert.Equal(4000L, await rig.LpBalance(new LoyaltyHolder(LoyaltyHolderKind.Character, Character), Corporation));
        Assert.Equal(1000000m, await rig.Isk(Character, 1000));
        Assert.Equal(25L, Assert.Single(await rig.ItemsAt(Character, Required)).Quantity);
        Assert.Empty(await rig.ItemsAt(Character, Reward));
        Assert.Equal(0, await rig.PurchaseRows(Character));
    }

    [LpPgFact]
    public async Task A_corporation_take_needs_a_director_and_then_pays_from_the_corporation_wallets()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedOffer(lpCost: 5000, iskCost: 100000, required: 10);
        await rig.Loyalty.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, PayerCorporation), Corporation, 6000, "seed", "seed:corp-lp");
        await rig.SeedIsk(PayerCorporation, 500000m, accountKey: 1001);
        await rig.SeedItem(Character, Required, 10);
        var session = rig.Pilot(new Sink(), PayerCorporation);
        session.Set("corpAccountKey", new PyInt(1001));
        var corpHolder = new LoyaltyHolder(LoyaltyHolderKind.Corporation, PayerCorporation);

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Service((_, _, _) => ValueTask.FromResult(false))
            .CallAsync(Call("TakeOfferForCorporation", session, new PyInt(Corporation), new PyInt(Offer), new PyInt(1))).AsTask());
        Assert.Equal(6000L, await rig.LpBalance(corpHolder, Corporation));

        var result = await rig.Service((_, _, _) => ValueTask.FromResult(true))
            .CallAsync(Call("TakeOfferForCorporation", session, new PyInt(Corporation), new PyInt(Offer), new PyInt(1)));

        Assert.True(Assert.IsType<PyBool>(result).Value);
        Assert.Equal(1000L, await rig.LpBalance(corpHolder, Corporation));
        Assert.Equal(400000m, await rig.Isk(PayerCorporation, 1001));
        Assert.Equal((26, -100000m), await rig.LastIskJournal(PayerCorporation, 1001));
        Assert.Single(await rig.ItemsAt(Character, Reward));
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session, CallId = new PyInt(1), Kwargs = PyNone.Instance };

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _wallet;
        private readonly string _inventory;
        private readonly NpgsqlDataSource _source;
        private readonly LpStorePurchases _purchases;

        public LpStoreOfferStore Store { get; }
        public LpStoreOfferCache Offers { get; }
        public LoyaltyPointStore Loyalty { get; }
        public PgItemStore Items { get; }
        public WalletTransactions Wallets { get; }

        private Rig(NpgsqlDataSource source, string wallet, string inventory)
        {
            _source = source;
            _wallet = wallet;
            _inventory = inventory;
            Store = new LpStoreOfferStore(source, wallet);
            Offers = new LpStoreOfferCache(Store, null, () => DateTimeOffset.UtcNow);
            Loyalty = new LoyaltyPointStore(source, wallet);
            Items = new PgItemStore(source, inventory);
            Wallets = new WalletTransactions(source, wallet);
            _purchases = new LpStorePurchases(source, wallet, Wallets, Loyalty, Items,
                (typeId, _) => Task.FromResult<ItemType?>(typeId == Reward ? RewardType : typeId == Required ? RequiredType : null),
                Offers);
        }

        public static async Task<Rig> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
            var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
            var wallet = "lpp_" + Guid.NewGuid().ToString("N")[..12];
            var inventory = "lpi_" + Guid.NewGuid().ToString("N")[..12];
            await PgWalletStore.MigrateAsync(source, wallet);
            await PgItemStore.MigrateAsync(source, inventory);
            return new Rig(source, wallet, inventory);
        }

        public LpStoreMgrService Service(Func<long, long, CancellationToken, ValueTask<bool>> isDirector) =>
            new(Offers, _purchases, isDirector);

        public ClientSession Pilot(Sink sink, long corporation = Corporation)
        {
            var session = new ClientSession(sink) { UserId = 7, CharacterId = Character };
            session.Set("stationid", new PyInt(Station));
            session.Set("corpid", new PyInt(corporation));
            return session;
        }

        public Task SeedOffer(long lpCost, long iskCost, long required) =>
            Store.ReplaceAsync(Corporation, [new LpStoreOffer(Corporation, Offer, Reward, 1, lpCost, iskCost, 0,
                [new LpStoreRequirement(Required, required)])], DateTimeOffset.UtcNow);

        public async Task SeedIsk(long owner, decimal amount, int accountKey = 1000)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {_wallet}.balances (owner_id, account_key, isk, plex) VALUES ($1, $2, $3, 0)", connection)
            {
                Parameters = { new() { Value = owner }, new() { Value = accountKey }, new() { Value = amount } },
            };
            await command.ExecuteNonQueryAsync();
        }

        public async Task SeedItem(long owner, int typeId, long quantity)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Items.CreateAsync(connection, transaction, new NewItem(typeId, owner, Station, ItemFlags.Hangar, quantity), default);
            await transaction.CommitAsync();
        }

        public async Task<IReadOnlyList<Item>> ItemsAt(long owner, int typeId) =>
            (await Items.InLocationAsync(Station, owner)).Where(item => item.TypeId == typeId).ToArray();

        public async Task<long> LpBalance(LoyaltyHolder holder, long issuer)
        {
            var rows = await Loyalty.ReadAsync(holder);
            return rows.SingleOrDefault(row => row.IssuerCorpId == issuer)?.Amount ?? 0;
        }

        public async Task<decimal> Isk(long owner, int accountKey) =>
            (await Wallets.AccountBalanceAsync(new WalletAccount(owner, accountKey))) ?? throw new InvalidOperationException("no balance");

        public async Task<(int RefType, decimal Amount)> LastIskJournal(long owner, int accountKey = 1000)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT ref_type_id, amount FROM {_wallet}.journal WHERE owner_id = $1 AND account_key = $2 ORDER BY id DESC LIMIT 1", connection)
            {
                Parameters = { new() { Value = owner }, new() { Value = accountKey } },
            };
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0), reader.GetDecimal(1));
        }

        public async Task<int> PurchaseRows(long character)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_wallet}.lp_store_purchases WHERE character_id = $1", connection)
            {
                Parameters = { new() { Value = character } },
            };
            return (int)(long)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await using (var drop = _source.CreateCommand($"DROP SCHEMA IF EXISTS {_wallet} CASCADE; DROP SCHEMA IF EXISTS {_inventory} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await _source.DisposeAsync();
        }
    }
}
