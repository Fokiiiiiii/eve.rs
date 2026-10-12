using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Market;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Station.Tests;

public sealed class RepairPgFactAttribute : FactAttribute
{
    public RepairPgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

internal sealed class RepairRig : IAsyncDisposable
{
    public const long Station = 60000011;
    public const long StationOwner = 1000002;
    public const int Rifter = 587;
    public const int Afterburner = 1447;
    public const long Character = 9411;

    private readonly string _wallets;
    private RepairRig(NpgsqlDataSource source, string items, string wallets, string modules, string market, PgItemStore itemStore, PgWalletStore wallet,
        WalletTransactions transactions, PgModuleStateStore moduleStore, AdjustedPriceStore priceStore, DogmaStatic statics, ItemTypeCatalog types, StationCatalog stations)
    {
        Source = source; _items = items; _wallets = wallets; _modules = modules; _market = market;
        Items = itemStore; Wallets = wallet; Transactions = transactions; Modules = moduleStore; Prices = priceStore; Statics = statics; Types = types; Stations = stations;
    }

    private readonly string _items;
    private readonly string _modules;
    private readonly string _market;

    public NpgsqlDataSource Source { get; }
    public PgItemStore Items { get; }
    public PgWalletStore Wallets { get; }
    public WalletTransactions Transactions { get; }
    public PgModuleStateStore Modules { get; }
    public AdjustedPriceStore Prices { get; }
    public DogmaStatic Statics { get; }
    public ItemTypeCatalog Types { get; }
    public StationCatalog Stations { get; }

    public static async Task<RepairRig> CreateAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var id = Guid.NewGuid().ToString("N")[..10];
        var items = "rpi_" + id; var wallets = "rpw_" + id; var modules = "rpm_" + id; var market = "rpk_" + id;
        await PgItemStore.MigrateAsync(source, items);
        await PgWalletStore.MigrateAsync(source, wallets);
        await PgModuleStateStore.MigrateAsync(source, modules);
        await MarketStore.MigrateAsync(source, market);
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var stations = new StationCatalog([new Eve.Universe.Station(Station, 1531, "Repair Station", StationOwner, "Owner", null, "", 0, 0, 30000142, "Jita", 20000020, "Kimotoro", 10000002, "The Forge", 0.9, 0.05, 4)]);
        return new RepairRig(source, items, wallets, modules, market, new PgItemStore(source, items), new PgWalletStore(source, wallets),
            new WalletTransactions(source, wallets), new PgModuleStateStore(source, modules), new AdjustedPriceStore(source, market), statics, new ItemTypeCatalog(sde), stations);
    }

    public RepairService Service(long now = 1_790_000_000_000L) => new(Source, Items, Types, Stations, Statics, Modules,
        new AdjustedPriceCache(Prices, esi: null, () => now), Transactions, Wallets, () => now);

    public async Task SetPriceAsync(int typeId, double price) => await Prices.ReplaceAsync([new AdjustedPriceRow(typeId, price, null)], 1_790_000_000_000L);

    public async Task OpenWalletAsync(long owner, decimal isk)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Wallets.OpenAsync(connection, transaction, owner, isk, 0);
        await transaction.CommitAsync();
    }

    public async Task<decimal> IskAsync(long owner) => (await Wallets.GetAsync(owner))!.Isk;

    public async Task<long> CreateAsync(long owner, int typeId)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Items.CreateAsync(connection, transaction, new NewItem(typeId, owner, Station, ItemFlags.Hangar, 1, Singleton: true));
        await transaction.CommitAsync();
        return id;
    }

    public async Task SetShipAsync(long ship, ShipCondition condition)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Modules.SetShipConditionAsync(connection, transaction, ship, condition);
        await transaction.CommitAsync();
    }

    public async Task<long> JournalCountAsync(int refType, long owner)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_wallets}.journal WHERE owner_id = $1 AND ref_type_id = $2", connection)
        {
            Parameters = { new() { Value = owner }, new() { Value = refType } },
        };
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync() => await Source.DisposeAsync();
}

public sealed class RepairServiceTests
{
    private static ClientSession Session(long character, long station)
    {
        var session = new ClientSession { CharacterId = character };
        session.Set("stationid", new PyInt(station));
        return session;
    }

    private static async Task<PyValue> CallShop(IService service, ClientSession session, long station, string method, params PyValue[] args)
    {
        var answer = await service.CallAsync(new ServiceCall
        {
            Method = "MachoBindObject",
            Args = [new PyInt(station), new PyTuple([new PyStr(method), new PyTuple(args), PyNone.Instance])],
            Session = session,
        });
        return ((PyTuple)answer).Items[1];
    }

    private static PyList Ids(params long[] ids) => new(ids.Select(id => (PyValue)new PyLong(id)).ToList());

    private static double Field(PyValue row, string name)
    {
        var entries = row switch
        {
            PyObjectEx ex => ex.Dict,
            PyObject { Args: PyDict dict } => dict.Entries,
            _ => throw new InvalidOperationException("the quote is not a named row"),
        };
        foreach (var (key, value) in entries)
        {
            if (key is PyStr text && text.Value == name)
                return value switch { PyReal real => real.Value, PyLong number => number.Value, PyInt number => number.Value, _ => throw new InvalidOperationException(name) };
        }
        throw new InvalidOperationException($"no field {name}");
    }

    // Rifter: shield 450, armor 450, structure 350 (1250 HP). Damage 0.5 structure, 0.2 armor, shield at half: 175 + 90 + 225 = 490 HP lost.
    // Price 1000 ISK: unit cost 1000 x 0.1 / 1250 = 0.08; full cost ceil(490) x 0.08 = 39.2 ISK.
    private static readonly ShipCondition Damaged = new(0.5, 1.0, 0.5, 0.2, false);

    [RepairPgFact]
    public async Task A_quote_gives_the_damage_in_hp_and_the_unit_cost_at_the_station_multiplier()
    {
        await using var rig = await RepairRig.CreateAsync();
        await rig.SetPriceAsync(RepairRig.Rifter, 1000);
        var ship = await rig.CreateAsync(RepairRig.Character, RepairRig.Rifter);
        await rig.SetShipAsync(ship, Damaged);

        var quotes = (PyDict)await CallShop(rig.Service(), Session(RepairRig.Character, RepairRig.Station), RepairRig.Station, "GetRepairQuotes", Ids(ship));

        var row = Assert.Single((PyList)quotes.Entries[0].Value is { } list ? list.Items : []);
        Assert.Equal(490.0, Field(row, "damage"), 6);
        Assert.Equal(1250.0, Field(row, "maxHealth"), 6);
        Assert.Equal(0.08, Field(row, "costToRepairOneUnitOfDamage"), 6);
    }

    [RepairPgFact]
    public async Task A_full_payment_charges_the_quote_and_repairs_the_ship_in_one_transaction()
    {
        await using var rig = await RepairRig.CreateAsync();
        await rig.SetPriceAsync(RepairRig.Rifter, 1000);
        await rig.OpenWalletAsync(RepairRig.Character, 100m);
        var ship = await rig.CreateAsync(RepairRig.Character, RepairRig.Rifter);
        await rig.SetShipAsync(ship, Damaged);

        await CallShop(rig.Service(), Session(RepairRig.Character, RepairRig.Station), RepairRig.Station, "RepairItemsInStation", Ids(ship), PyNone.Instance);

        Assert.Equal(ShipCondition.Full, await rig.Modules.ShipConditionAsync(ship));
        Assert.Equal(100m - 39.2m, await rig.IskAsync(RepairRig.Character));
        Assert.Equal(1, await rig.JournalCountAsync(RepairService.RepairBillRefType, RepairRig.Character));
    }

    [RepairPgFact]
    public async Task A_partial_payment_repairs_structure_then_armor_and_charges_only_what_it_bought()
    {
        await using var rig = await RepairRig.CreateAsync();
        await rig.SetPriceAsync(RepairRig.Rifter, 1000);
        await rig.OpenWalletAsync(RepairRig.Character, 100m);
        var ship = await rig.CreateAsync(RepairRig.Character, RepairRig.Rifter);
        await rig.SetShipAsync(ship, Damaged);

        // 19.6 ISK buys 245 HP: the 175 HP of structure, then 70 of the 90 HP of armor
        await CallShop(rig.Service(), Session(RepairRig.Character, RepairRig.Station), RepairRig.Station, "RepairItemsInStation", Ids(ship), new PyReal(19.6));

        var after = await rig.Modules.ShipConditionAsync(ship);
        Assert.Equal(0.0, after.Damage, 9);
        Assert.Equal(20.0 / 450.0, after.ArmorDamage, 9);
        Assert.Equal(0.5, after.ShieldCharge, 9);
        Assert.Equal(100m - 19.6m, await rig.IskAsync(RepairRig.Character));
    }

    [RepairPgFact]
    public async Task A_payment_short_of_the_isk_for_the_repair_changes_nothing()
    {
        await using var rig = await RepairRig.CreateAsync();
        await rig.SetPriceAsync(RepairRig.Rifter, 1000);
        await rig.OpenWalletAsync(RepairRig.Character, 1m);
        var ship = await rig.CreateAsync(RepairRig.Character, RepairRig.Rifter);
        await rig.SetShipAsync(ship, Damaged);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            CallShop(rig.Service(), Session(RepairRig.Character, RepairRig.Station), RepairRig.Station, "RepairItemsInStation", Ids(ship), PyNone.Instance));

        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        Assert.Equal(Damaged, await rig.Modules.ShipConditionAsync(ship));
        Assert.Equal(1m, await rig.IskAsync(RepairRig.Character));
        Assert.Equal(0, await rig.JournalCountAsync(RepairService.RepairBillRefType, RepairRig.Character));
    }

    [RepairPgFact]
    public async Task A_fitted_module_is_repaired_in_full_only()
    {
        await using var rig = await RepairRig.CreateAsync();
        await rig.SetPriceAsync(RepairRig.Afterburner, 2000);
        await rig.OpenWalletAsync(RepairRig.Character, 1000m);
        var module = await rig.CreateAsync(RepairRig.Character, RepairRig.Afterburner);
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [module] = new ModuleCondition(0.5, 0.0, 1.0, false) }, CancellationToken.None);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            CallShop(rig.Service(), Session(RepairRig.Character, RepairRig.Station), RepairRig.Station, "RepairItemsInStation", Ids(module), new PyReal(0.01)));

        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        Assert.Equal(1000m, await rig.IskAsync(RepairRig.Character));
        Assert.Equal(0.5, (await rig.Modules.ModuleConditionsAsync([module]))[module].Damage, 9);
    }

    [RepairPgFact]
    public async Task An_item_with_no_price_is_refused_rather_than_repaired_for_nothing()
    {
        await using var rig = await RepairRig.CreateAsync();
        await rig.OpenWalletAsync(RepairRig.Character, 1000m);
        var ship = await rig.CreateAsync(RepairRig.Character, RepairRig.Rifter);
        await rig.SetShipAsync(ship, Damaged);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            CallShop(rig.Service(), Session(RepairRig.Character, RepairRig.Station), RepairRig.Station, "RepairItemsInStation", Ids(ship), PyNone.Instance));

        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        Assert.Equal(Damaged, await rig.Modules.ShipConditionAsync(ship));
    }
}
