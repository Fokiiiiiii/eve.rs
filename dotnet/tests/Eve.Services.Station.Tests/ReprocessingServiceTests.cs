using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Station.Tests;

public sealed class ReprocessingPgFactAttribute : FactAttribute
{
    public ReprocessingPgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

/// <summary>The reprocessing data of the tests: one ore (Veldspar, portion 100) that yields 415 Tritanium per portion, and prices of 5 ISK.</summary>
internal sealed class FixedReprocessingData : IReprocessingData
{
    public const int Veldspar = 1230;
    public const int Tritanium = 34;

    public Task<IReadOnlyDictionary<int, ReprocessingTypeInfo>> TypesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, ReprocessingTypeInfo>>(typeIds.Where(id => id == Veldspar)
            .ToDictionary(id => id, id => new ReprocessingTypeInfo(id, 100, 0, [new ReprocessingRules.Material(Tritanium, 415)])));

    public Task<IReadOnlyDictionary<int, double>> PercentagePerLevelAsync(IReadOnlyCollection<int> skillTypeIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, double>>(new Dictionary<int, double>());

    public Task<IReadOnlyDictionary<int, double>> AdjustedPricesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, double>>(typeIds.Where(id => id == Tritanium).ToDictionary(id => id, _ => 5.0));

    public Task<double> StationEfficiencyAsync(long stationId, CancellationToken cancellationToken) => Task.FromResult(0.5);
}

internal sealed class ReprocessingRig : IAsyncDisposable
{
    public const long Station = 60000001;
    public const long OtherStation = 60000002;
    public const long StationOwner = 1000002;

    private readonly string _walletSchema;

    public string ItemSchema { get; private init; } = "";
    public PgCharacterStore Characters { get; private init; } = null!;
    public DogmaStatic Statics { get; private init; } = null!;

    private ReprocessingRig(NpgsqlDataSource source, string walletSchema, PgItemStore items, PgWalletStore wallets, WalletTransactions transactions,
        PgSkillStore skills, RelationStore relations, ItemTypeCatalog types, StationCatalog stations, FixedReprocessingData data, DateTimeOffset start)
    {
        Source = source; _walletSchema = walletSchema; Items = items; Wallets = wallets; Transactions = transactions; Skills = skills;
        Relations = relations; Types = types; Stations = stations; Data = data; Start = start;
    }

    public NpgsqlDataSource Source { get; }
    public PgItemStore Items { get; }
    public PgWalletStore Wallets { get; }
    public WalletTransactions Transactions { get; }
    public PgSkillStore Skills { get; }
    public RelationStore Relations { get; }
    public ItemTypeCatalog Types { get; }
    public StationCatalog Stations { get; }
    public FixedReprocessingData Data { get; }
    public DateTimeOffset Start { get; }

    public static async Task<ReprocessingRig> CreateAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var id = Guid.NewGuid().ToString("N")[..10];
        var itemSchema = "ri_" + id;
        var walletSchema = "rw_" + id;
        var skillSchema = "rs_" + id;
        var scheduler = "rsch_" + id;
        var events = "rev_" + id;
        var relations = "rrel_" + id;
        await PgItemStore.MigrateAsync(source, itemSchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await PgSkillStore.MigrateAsync(source, skillSchema);
        await DurableScheduler.MigrateAsync(source, scheduler);
        await EventBus.MigrateAsync(source, events);
        await RelationStore.MigrateAsync(source, relations);
        var start = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var durable = new DurableScheduler(source, () => start.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, scheduler);
        var bus = new EventBus(source, durable, null, events);
        var stations = new StationCatalog([
            new Eve.Universe.Station(Station, 1531, "Test Station", StationOwner, "Owner", null, "", 0, 0, 30000142, "Jita", 20000020, "Kimotoro", 10000002, "The Forge", 0.9, 0.05, 4),
            new Eve.Universe.Station(OtherStation, 1531, "Other Station", StationOwner, "Owner", null, "", 0, 0, 30000142, "Jita", 20000020, "Kimotoro", 10000002, "The Forge", 0.9, 0.05, 4),
        ]);
        var wallets = new PgWalletStore(source, walletSchema);
        var characterSchema = "rc_" + id;
        await PgCharacterStore.MigrateAsync(source, characterSchema);
        var sde = new SdeReader(source);
        return new ReprocessingRig(source, walletSchema, new PgItemStore(source, itemSchema), wallets,
            new WalletTransactions(source, walletSchema), new PgSkillStore(source, skillSchema),
            new RelationStore(source, durable, bus, relations), new ItemTypeCatalog(sde), stations, new FixedReprocessingData(), start)
        {
            ItemSchema = itemSchema,
            Characters = new PgCharacterStore(source, characterSchema),
            Statics = await DogmaStatic.LoadAsync(sde),
        };
    }

    public ReprocessingService Service() => new(Source, Items, Types, Stations, Data, Relations, Skills, Transactions, Wallets, Characters, Statics, () => Start.ToUnixTimeMilliseconds());

    public async Task OpenWalletAsync(long owner, decimal isk)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Wallets.OpenAsync(connection, transaction, owner, isk, 0);
        await transaction.CommitAsync();
    }

    public async Task<decimal> IskAsync(long owner) => (await Wallets.GetAsync(owner))!.Isk;

    public async Task<long> CreateAsync(long owner, long location, int flag, int typeId, long quantity)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Items.CreateAsync(connection, transaction, new NewItem(typeId, owner, location, flag, quantity));
        await transaction.CommitAsync();
        return id;
    }

    public async Task<long> TritaniumAsync(long owner, long location, int flag)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT COALESCE(SUM(quantity), 0) FROM " + ItemSchema + ".items WHERE owner_id = $1 AND location_id = $2 AND flag = $3 AND type_id = 34", connection)
        {
            Parameters = { new() { Value = owner }, new() { Value = location }, new() { Value = flag } },
        };
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task<long> JournalCountAsync(int refType, long owner)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_walletSchema}.journal WHERE owner_id = $1 AND ref_type_id = $2", connection)
        {
            Parameters = { new() { Value = owner }, new() { Value = refType } },
        };
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync() => await Source.DisposeAsync();
}

public sealed class ReprocessingServiceTests
{
    private const long Character = 9301;
    private static ClientSession Session(long character, long station)
    {
        var session = new ClientSession { CharacterId = character };
        session.Set("stationid", new PyInt(station));
        return session;
    }

    /// <summary>A call of the bound plant: <c>MachoBindObject(stationID, (method, args, kwargs))</c>, as the client's moniker sends it. The nested answer is returned.</summary>
    private static async Task<PyValue> CallPlant(IService service, ClientSession session, long station, string method, params PyValue[] args)
    {
        var answer = await service.CallAsync(new ServiceCall
        {
            Method = "MachoBindObject",
            Args = [new PyInt(station), new PyTuple([new PyStr(method), new PyTuple(args), PyNone.Instance])],
            Session = session,
        });
        return ((PyTuple)answer).Items[1];
    }

    private static PyList Ids1(params long[] ids) => new(ids.Select(id => (PyValue)new PyLong(id)).ToList());

    [ReprocessingPgFact]
    public async Task GetQuotes_quotes_the_hangar_stack_at_the_station_efficiency_and_the_tax()
    {
        await using var rig = await ReprocessingRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 1000m);
        var stack = await rig.CreateAsync(Character, ReprocessingRig.Station, ItemFlags.Hangar, FixedReprocessingData.Veldspar, 250);

        var quotes = (PyList)await CallPlant(rig.Service(), Session(Character, ReprocessingRig.Station), ReprocessingRig.Station, "GetQuotes", Ids1(stack), new PyInt(0));

        Assert.Equal(0.05, ((PyReal)quotes.Items[0]).Value, 6);
        var quoteRows = (PyDict)quotes.Items[2];
        Assert.Single(quoteRows.Entries);
        var quote = quoteRows.Entries[0].Value;
        Assert.Equal(200L, FieldLong(quote, "quantityToProcess"));
        Assert.Equal(50L, FieldLong(quote, "leftOvers"));
        Assert.Equal(2L, FieldLong(quote, "portions"));
    }

    [ReprocessingPgFact]
    public async Task Reprocess_consumes_whole_portions_keeps_the_leftovers_creates_the_yield_and_charges_the_tax_once()
    {
        await using var rig = await ReprocessingRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 1000m);
        var stack = await rig.CreateAsync(Character, ReprocessingRig.Station, ItemFlags.Hangar, FixedReprocessingData.Veldspar, 250);
        var session = Session(Character, ReprocessingRig.Station);

        var answer = (PyList)await CallPlant(rig.Service(), session, ReprocessingRig.Station, "Reprocess", Ids1(stack), new PyInt(ReprocessingRig.Station), new PyInt(0), PyNone.Instance, PyNone.Instance);

        Assert.Equal(new long[] { stack }, ((PyList)answer.Items[0]).Items.Select(v => ((PyLong)v).Value).ToArray());
        Assert.Equal(50, (await rig.Items.GetAsync(stack))!.Quantity);
        // 2 portions x 415 Tritanium x 50% efficiency = 415 yielded, in the hangar of the station
        var yielded = await rig.TritaniumAsync(Character, ReprocessingRig.Station, ItemFlags.Hangar);
        Assert.Equal(415, yielded);
        // the tax: 5 ISK x 415 x 5% (the station's take, the standing is zero)
        var tax = 5.0m * 415 * 0.05m;
        Assert.Equal(1000m - tax, await rig.IskAsync(Character));
        Assert.Equal(1, await rig.JournalCountAsync(ReprocessingService.ReprocessingTaxRefType, Character));
    }

    [ReprocessingPgFact]
    public async Task Reprocess_without_the_isk_for_the_tax_changes_nothing()
    {
        await using var rig = await ReprocessingRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 10m);
        var stack = await rig.CreateAsync(Character, ReprocessingRig.Station, ItemFlags.Hangar, FixedReprocessingData.Veldspar, 250);
        var session = Session(Character, ReprocessingRig.Station);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await CallPlant(rig.Service(), session, ReprocessingRig.Station, "Reprocess", Ids1(stack), new PyInt(ReprocessingRig.Station), new PyInt(0), PyNone.Instance, PyNone.Instance));

        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        Assert.Equal(250, (await rig.Items.GetAsync(stack))!.Quantity);
        Assert.Equal(10m, await rig.IskAsync(Character));
        Assert.Equal(0, await rig.JournalCountAsync(ReprocessingService.ReprocessingTaxRefType, Character));
    }

    [ReprocessingPgFact]
    public async Task A_stack_outside_the_hangar_of_this_station_is_not_quoted()
    {
        await using var rig = await ReprocessingRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 1000m);
        var elsewhere = await rig.CreateAsync(Character, ReprocessingRig.OtherStation, ItemFlags.Hangar, FixedReprocessingData.Veldspar, 250);

        var quotes = (PyList)await CallPlant(rig.Service(), Session(Character, ReprocessingRig.Station), ReprocessingRig.Station, "GetQuotes", Ids1(elsewhere), new PyInt(0));

        Assert.Empty(((PyDict)quotes.Items[2]).Entries);
    }

    [ReprocessingPgFact]
    public async Task The_plant_is_bound_only_for_the_station_the_caller_is_docked_in()
    {
        await using var rig = await ReprocessingRig.CreateAsync();
        var session = Session(Character, ReprocessingRig.Station);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await CallPlant(rig.Service(), session, ReprocessingRig.OtherStation, "GetQuotes", Ids1(), new PyInt(0)));

        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
    }

    [ReprocessingPgFact]
    public async Task A_stack_in_a_container_at_the_station_is_quoted_and_reprocessed_from_there()
    {
        await using var rig = await ReprocessingRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 1000m);
        var container = await rig.CreateAsync(Character, ReprocessingRig.Station, ItemFlags.Hangar, FixedReprocessingData.Tritanium, 1);
        var inside = await rig.CreateAsync(Character, container, ItemFlags.Cargo, FixedReprocessingData.Veldspar, 100);
        var session = Session(Character, ReprocessingRig.Station);

        var quotes = (PyList)await CallPlant(rig.Service(), session, ReprocessingRig.Station, "GetQuotes", Ids1(inside), new PyInt(0));
        Assert.Single(((PyDict)quotes.Items[2]).Entries);

        var answer = (PyList)await CallPlant(rig.Service(), session, ReprocessingRig.Station, "Reprocess", Ids1(inside), new PyInt(ReprocessingRig.Station), new PyInt(0), PyNone.Instance, PyNone.Instance);
        Assert.Equal(new long[] { inside }, ((PyList)answer.Items[0]).Items.Select(v => ((PyLong)v).Value).ToArray());
        Assert.Null(await rig.Items.GetAsync(inside));
        // the yield goes where the sample stood: inside the container
        Assert.Equal(207, await rig.TritaniumAsync(Character, container, ItemFlags.Cargo));
    }

    [ReprocessingPgFact]
    public async Task A_stack_too_large_for_the_client_is_split_into_chunks_and_all_of_it_is_reprocessed()
    {
        await using var rig = await ReprocessingRig.CreateAsync();
        // 3 billion Veldspar: 30 million portions, 415 Tritanium each at 50% = about 6.2 billion, which is above 2^31 - 1
        await rig.OpenWalletAsync(Character, 5_000_000_000m);
        var stack = await rig.CreateAsync(Character, ReprocessingRig.Station, ItemFlags.Hangar, FixedReprocessingData.Veldspar, 3_000_000_000);
        var session = Session(Character, ReprocessingRig.Station);

        var answer = (PyList)await CallPlant(rig.Service(), session, ReprocessingRig.Station, "Reprocess", Ids1(stack), new PyInt(ReprocessingRig.Station), new PyInt(0), PyNone.Instance, PyNone.Instance);

        var processed = ((PyList)answer.Items[0]).Items.Select(v => ((PyLong)v).Value).ToList();
        Assert.True(processed.Count >= 2, "the stack was split and every chunk was reprocessed");
        Assert.Null(await rig.Items.GetAsync(stack));
        var yielded = (await rig.TritaniumAsync(Character, ReprocessingRig.Station, ItemFlags.Hangar));
        // half of 415 per 100 units, over 30 million portions: 6,225,000,000 Tritanium, each chunk rounded down (at most one unit per chunk)
        Assert.InRange(yielded, 6_225_000_000L - processed.Count, 6_225_000_000L);
    }

    private static long FieldLong(PyValue row, string name)
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
                return value switch { PyLong number => number.Value, PyInt number => number.Value, _ => throw new InvalidOperationException($"{name} is not a number") };
        }
        throw new InvalidOperationException($"no field {name}");
    }
}
