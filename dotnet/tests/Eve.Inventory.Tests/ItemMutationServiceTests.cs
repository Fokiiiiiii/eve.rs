using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Eve.Scheduler;
using Eve.Sde;
using Npgsql;

namespace Eve.Inventory.Tests;

/// <summary>
/// The abyssal mutation authority over a scratch item schema, a scratch event bus and the real SDE (skipped without EVE_PG_URL;
/// skipped is not passed). The numbers are the spec's worked example: mutaplasmid 47297 on 50MN Microwarpdrive I (12052) gives 47408.
/// </summary>
public class ItemMutationServiceTests
{
    private const long Character = 140_000_001;
    private const long Stranger = 140_000_002;
    private const long Station = 60015249;
    private const long OtherStation = 60000610;
    private const long ClockMs = 1_700_000_000_123;
    private const int Mutaplasmid = 47297;
    private const int Microwarpdrive = 12052;
    private const int Abyssal = 47408;
    private const int SmallArmorRepairerII = 1183;

    /// <summary>Roll fractions that give the spec's multipliers 1.0, 0.95, 1.325, 0.8, 1.3 for attributes 6, 20, 30, 50, 554.</summary>
    private static readonly Dictionary<int, double> WorkedRolls = new() { [6] = 0.5, [20] = 0.25, [30] = 0.75, [50] = 0.0, [554] = 1.0 };

    private static readonly Lazy<Task<(DogmaStatic Statics, MutatorCatalog Catalog)>> StaticData = new(LoadStaticDataAsync);

    private static async Task<(DogmaStatic Statics, MutatorCatalog Catalog)> LoadStaticDataAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        return (await DogmaStatic.LoadAsync(sde), await MutatorCatalog.LoadAsync(sde));
    }

    private static MutationRoll Rolls(IReadOnlyDictionary<int, double> fractions) => attribute => fractions[attribute];

    private static void AssertSameBits(IReadOnlyDictionary<int, double> expected, IReadOnlyDictionary<int, double> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (attribute, value) in expected)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(actual[attribute]));
        }
    }

    private static void AssertValues(IReadOnlyDictionary<int, double> expected, IReadOnlyDictionary<int, double> actual, int precision = 9)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (attribute, value) in expected)
        {
            Assert.Equal(value, actual[attribute], precision);
        }
    }

    /// <summary>A scratch item schema, event bus and scheduler, with the real static data, and what the bus delivered to a subscriber.</summary>
    private sealed class Mutations : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _source;
        private readonly string _schema;
        private readonly string _eventsSchema;
        private readonly string _schedulerSchema;

        private Mutations(NpgsqlDataSource source, string id, DogmaStatic statics, MutatorCatalog catalog)
        {
            _source = source;
            _schema = "mut_" + id;
            _eventsSchema = "mute_" + id;
            _schedulerSchema = "muts_" + id;
            Statics = statics;
            Catalog = catalog;
            Items = new PgItemStore(source, _schema);
            Store = new ItemMutationStore(source, _schema);
            Scheduler = new DurableScheduler(source, () => ClockMs, new SchedulerOptions { Concurrency = 1 }, _schedulerSchema);
            Bus = new EventBus(source, Scheduler, null, _eventsSchema);
        }

        public NpgsqlDataSource Source => _source;
        public string Schema => _schema;
        public string ProvenanceTable => $"{_schema}.item_mutations";
        public string OutboxTable => $"{_eventsSchema}.outbox";
        public DogmaStatic Statics { get; }
        public MutatorCatalog Catalog { get; }
        public PgItemStore Items { get; }
        public ItemMutationStore Store { get; }
        public DurableScheduler Scheduler { get; }
        public EventBus Bus { get; }
        public List<DomainEvent> Delivered { get; } = [];

        public static async Task<Mutations> CreateAsync()
        {
            var (statics, catalog) = await StaticData.Value;
            var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
            var id = Guid.NewGuid().ToString("N")[..12];
            var mutations = new Mutations(source, id, statics, catalog);
            await PgItemStore.MigrateAsync(source, mutations._schema);
            await DurableScheduler.MigrateAsync(source, mutations._schedulerSchema);
            await EventBus.MigrateAsync(source, mutations._eventsSchema);
            mutations.Bus.Subscribe("test.item_mutated", [ItemMutationService.MutatedTopic], (context, _) =>
            {
                mutations.Delivered.Add(context.Event);
                return Task.CompletedTask;
            });
            await mutations.Bus.PumpAsync();
            return mutations;
        }

        /// <summary>The service as a restarted process would build it: fresh handles over the same schema.</summary>
        public ItemMutationService Service(MutationRoll? roll = null, DogmaStatic? statics = null) =>
            new(_source, new PgItemStore(_source, _schema), new ItemMutationStore(_source, _schema), Catalog, statics ?? Statics, new BusEvents(Bus), () => ClockMs,
                roll ?? Rolls(WorkedRolls));

        /// <summary>The Event Bus as the authority's port (the Host supplies the same in production).</summary>
        private sealed class BusEvents(EventBus bus) : IItemMutationEvents
        {
            public async Task PublishMutatedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ItemMutationService.ItemMutated payload, string key, CancellationToken cancellationToken)
            {
                await bus.PublishAsync(connection, transaction, ItemMutationService.MutatedTopic, payload, key, cancellationToken);
            }

            public void Wake() => bus.Wake();
        }

        public async Task<long> AddAsync(int type, long owner, long location, int flag, long quantity = 1, bool singleton = false)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await Items.CreateAsync(connection, transaction, new NewItem(type, owner, location, flag, quantity, singleton));
            await transaction.CommitAsync();
            return id;
        }

        public async Task SetBlueprintLockedAsync(long id)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"UPDATE {_schema}.items SET blueprint_locked = true WHERE id = $1", CancellationToken.None, id);
        }

        /// <summary>Drops a table, so that the next write to it fails inside the mutation's transaction.</summary>
        public async Task DropAsync(string qualifiedTable)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"DROP TABLE {qualifiedTable}", CancellationToken.None);
        }

        /// <summary>What the character and the stranger own: what a refusal or a failure must leave as it was.</summary>
        public async Task<List<(long Id, int Type, long Quantity, bool Singleton, long Location, int Flag, long Owner)>> ItemsAsync()
        {
            var owned = new List<(long Id, int Type, long Quantity, bool Singleton, long Location, int Flag, long Owner)>();
            foreach (var owner in new[] { Character, Stranger })
            {
                owned.AddRange((await Items.OwnedByAsync(owner)).Select(i => (i.Id, i.TypeId, i.Quantity, i.Singleton, i.LocationId, i.Flag, i.OwnerId)));
            }
            return owned.OrderBy(i => i.Id).ToList();
        }

        public async Task<long> ProvenanceCountAsync()
        {
            await using var connection = await _source.OpenConnectionAsync();
            return (long)(await Sql.ScalarAsync(connection, null, $"SELECT count(*) FROM {ProvenanceTable}", CancellationToken.None))!;
        }

        /// <summary>A published mutaplasmid and an input type that is listed for it but not published (the client filters those out).</summary>
        public async Task<(int Mutator, int Input)> UnpublishedPairAsync()
        {
            var sde = new SdeReader(_source);
            var published = (await sde.ReadTypeSummariesAsync()).Where(t => t.Published).Select(t => (long)t.TypeId).ToHashSet();
            var pair = (await sde.ReadAsync("dynamicItemAttributes"))
                .Where(row => published.Contains(row.Key))
                .SelectMany(row => row.Doc.GetProperty("inputOutputMapping").EnumerateArray()
                    .SelectMany(mapping => mapping.GetProperty("applicableTypes").EnumerateArray())
                    .Select(input => (Mutator: row.Key, Input: input.GetInt64())))
                .First(candidate => !published.Contains(candidate.Input));
            return ((int)pair.Mutator, (int)pair.Input);
        }

        public async ValueTask DisposeAsync() => await _source.DisposeAsync();
    }

    private static async Task<(long Mutator, long Source)> StandardAsync(Mutations m, long mutatorQuantity = 1)
    {
        var mutator = await m.AddAsync(Mutaplasmid, Character, Station, ItemFlags.Hangar, mutatorQuantity);
        var source = await m.AddAsync(Microwarpdrive, Character, Station, ItemFlags.Hangar, 1, singleton: true);
        return (mutator, source);
    }

    /// <summary>A refusal: the typed reason, the world as it was, and no announcement.</summary>
    private static async Task AssertRefusedAsync(Mutations m, ItemMutationRefusal reason, ItemMutationRequest request, MutationRoll? roll = null)
    {
        var items = await m.ItemsAsync();
        var provenance = await m.ProvenanceCountAsync();
        await m.Bus.PumpAsync();
        var announced = m.Delivered.Count;

        var outcome = await m.Service(roll).MutateAsync(request);

        Assert.Equal(reason, Assert.IsType<ItemMutationRefused>(outcome).Reason);
        Assert.Equal(items, await m.ItemsAsync());
        Assert.Equal(provenance, await m.ProvenanceCountAsync());
        await m.Bus.PumpAsync();
        Assert.Equal(announced, m.Delivered.Count);
    }

    // ---- success --------------------------------------------------------------------------------------------------------------------------------

    [PgFact]
    public async Task A_mutaplasmid_and_a_module_make_the_abyssal_module_in_the_source_place_with_the_rolled_values()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);

        var receipt = Assert.IsType<ItemMutationAccepted>(await m.Service().MutateAsync(new(Character, Station, mutator, source))).Receipt;

        Assert.Equal(Abyssal, receipt.TypeId);
        Assert.Equal(Station, receipt.LocationId);
        Assert.Equal(ItemFlags.Hangar, receipt.Flag);
        var made = await m.Items.GetAsync(receipt.ItemId);
        Assert.NotNull(made);
        Assert.Equal((Abyssal, Character, Station, ItemFlags.Hangar, 1L, true), (made.TypeId, made.OwnerId, made.LocationId, made.Flag, made.Quantity, made.Singleton));
        Assert.Null(await m.Items.GetAsync(mutator));
        Assert.Null(await m.Items.GetAsync(source));

        Assert.Equal(180.0, receipt.MutatedValues[6], 9);
        Assert.Equal(475.0, receipt.MutatedValues[20], 9);
        Assert.Equal(198.75, receipt.MutatedValues[30], 9);
        Assert.Equal(40.0, receipt.MutatedValues[50], 9);
        Assert.Equal(650.0, receipt.MutatedValues[554], 9);
        Assert.Equal(5, receipt.MutatedValues.Count);
    }

    [PgFact]
    public async Task The_changes_are_the_mutaplasmid_then_the_source_then_the_item_made_each_with_the_place_and_stack_it_had()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);

        var receipt = Assert.IsType<ItemMutationAccepted>(await m.Service().MutateAsync(new(Character, Station, mutator, source))).Receipt;

        // the mutaplasmid is a single unit here: consumed (after quantity 0); the source is consumed; the item made is new, from no place
        Assert.Equal(3, receipt.Changes.Count);
        Assert.Equal((mutator, 0L, Station, ItemFlags.Hangar, 1L), (receipt.Changes[0].After.Id, receipt.Changes[0].After.Quantity, receipt.Changes[0].PreviousLocationId, receipt.Changes[0].PreviousFlag, receipt.Changes[0].PreviousQuantity));
        Assert.Equal((source, 0L, Station, ItemFlags.Hangar, 1L), (receipt.Changes[1].After.Id, receipt.Changes[1].After.Quantity, receipt.Changes[1].PreviousLocationId, receipt.Changes[1].PreviousFlag, receipt.Changes[1].PreviousQuantity));
        Assert.Equal((receipt.ItemId, Abyssal, 1L, Station, ItemFlags.Hangar), (receipt.Changes[2].After.Id, receipt.Changes[2].After.TypeId, receipt.Changes[2].After.Quantity, receipt.Changes[2].After.LocationId, receipt.Changes[2].After.Flag));
        Assert.Equal((0L, 0, 0L), (receipt.Changes[2].PreviousLocationId, receipt.Changes[2].PreviousFlag, receipt.Changes[2].PreviousQuantity));
    }

    [PgFact]
    public async Task The_provenance_records_the_mutaplasmid_the_input_the_rolls_and_the_base_values()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        var receipt = Assert.IsType<ItemMutationAccepted>(await m.Service().MutateAsync(new(Character, Station, mutator, source))).Receipt;

        var record = await m.Store.GetAsync(receipt.ItemId);

        Assert.NotNull(record);
        Assert.Equal((Mutaplasmid, mutator, Microwarpdrive, source, Character, ClockMs),
            (record.MutatorTypeId, record.MutatorItemId, record.SourceTypeId, record.SourceItemId, record.CharacterId, record.CreatedAtMs));
        AssertValues(new Dictionary<int, double> { [6] = 1.0, [20] = 0.95, [30] = 1.325, [50] = 0.8, [554] = 1.3 }, record.Rolls, 9);
        AssertValues(new Dictionary<int, double> { [6] = 180, [20] = 500, [30] = 150, [50] = 50, [554] = 500 }, record.BaseValues, 9);
        AssertSameBits(receipt.Rolls, record.Rolls);
        AssertSameBits(receipt.MutatedValues, record.MutatedValues);
    }

    [PgFact]
    public async Task The_announcement_is_delivered_once_the_transaction_has_committed()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);

        var receipt = Assert.IsType<ItemMutationAccepted>(await m.Service().MutateAsync(new(Character, Station, mutator, source))).Receipt;
        await m.Bus.PumpAsync();

        var delivered = Assert.Single(m.Delivered);
        Assert.Equal(ItemMutationService.MutatedTopic, delivered.Topic);
        Assert.Equal($"char:{Character}", delivered.Key);
        Assert.Equal(new ItemMutationService.ItemMutated(receipt.ItemId, Abyssal, Mutaplasmid, Microwarpdrive, Character),
            delivered.Payload<ItemMutationService.ItemMutated>());
    }

    [PgFact]
    public async Task One_unit_of_a_stack_of_mutaplasmids_is_consumed_and_the_rest_stays()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m, mutatorQuantity: 3);

        Assert.IsType<ItemMutationAccepted>(await m.Service().MutateAsync(new(Character, Station, mutator, source)));

        var left = await m.Items.GetAsync(mutator);
        Assert.NotNull(left);
        Assert.Equal(2, left.Quantity);
        Assert.Equal(Mutaplasmid, left.TypeId);
    }

    [PgFact]
    public async Task The_same_rolls_give_the_same_values_each_attribute_is_drawn_once_and_the_interval_ends_are_the_printed_multipliers()
    {
        await using var m = await Mutations.CreateAsync();
        var low = new Dictionary<int, double> { [6] = 0, [20] = 0, [30] = 0, [50] = 0, [554] = 0 };
        var high = new Dictionary<int, double> { [6] = 1, [20] = 1, [30] = 1, [50] = 1, [554] = 1 };
        var draws = new Dictionary<int, int>();
        MutationRoll Counting(IReadOnlyDictionary<int, double> fractions) => attribute =>
        {
            draws[attribute] = draws.GetValueOrDefault(attribute) + 1;
            return fractions[attribute];
        };

        var (mutatorLow, sourceLow) = await StandardAsync(m);
        var atLow = Assert.IsType<ItemMutationAccepted>(await m.Service(Counting(low)).MutateAsync(new(Character, Station, mutatorLow, sourceLow))).Receipt;
        var (mutatorHigh, sourceHigh) = await StandardAsync(m);
        var atHigh = Assert.IsType<ItemMutationAccepted>(await m.Service(Counting(high)).MutateAsync(new(Character, Station, mutatorHigh, sourceHigh))).Receipt;
        var (mutatorAgain, sourceAgain) = await StandardAsync(m);
        var again = Assert.IsType<ItemMutationAccepted>(await m.Service(Counting(low)).MutateAsync(new(Character, Station, mutatorAgain, sourceAgain))).Receipt;

        // the printed minimum of each attribute times the source's base: 0.6 x 180, 0.9 x 500, 0.8 x 150, 0.8 x 50, 0.7 x 500
        AssertValues(new Dictionary<int, double> { [6] = 108, [20] = 450, [30] = 120, [50] = 40, [554] = 350 }, atLow.MutatedValues);
        // the printed maximum: 1.4 x 180, 1.1 x 500, 1.5 x 150, 1.5 x 50, 1.3 x 500
        AssertValues(new Dictionary<int, double> { [6] = 252, [20] = 550, [30] = 225, [50] = 75, [554] = 650 }, atHigh.MutatedValues);
        AssertSameBits(atLow.MutatedValues, again.MutatedValues);
        Assert.Equal(new[] { 6, 20, 30, 50, 554 }, draws.Keys.Order());
        Assert.All(draws.Values, count => Assert.Equal(3, count));
    }

    // ---- refusals (each leaves the world as it was and announces nothing) ----------------------------------------------------------------

    [PgFact]
    public async Task A_module_cannot_be_its_own_mutaplasmid()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, _) = await StandardAsync(m);
        await AssertRefusedAsync(m, ItemMutationRefusal.SameItem, new(Character, Station, mutator, mutator));
    }

    [PgFact]
    public async Task A_missing_mutaplasmid_or_source_is_refused()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        await AssertRefusedAsync(m, ItemMutationRefusal.MutatorMissing, new(Character, Station, 9_999_999_991, source));
        await AssertRefusedAsync(m, ItemMutationRefusal.SourceMissing, new(Character, Station, mutator, 9_999_999_992));
    }

    [PgFact]
    public async Task Both_items_must_be_the_callers()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        var theirModule = await m.AddAsync(Microwarpdrive, Stranger, Station, ItemFlags.Hangar, 1, singleton: true);
        await AssertRefusedAsync(m, ItemMutationRefusal.NotOwner, new(Character, Station, mutator, theirModule));
        var theirMutaplasmid = await m.AddAsync(Mutaplasmid, Stranger, Station, ItemFlags.Hangar);
        await AssertRefusedAsync(m, ItemMutationRefusal.NotOwner, new(Character, Station, theirMutaplasmid, source));
    }

    [PgFact]
    public async Task Both_items_must_be_in_the_location_the_caller_is_in()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        var moduleElsewhere = await m.AddAsync(Microwarpdrive, Character, OtherStation, ItemFlags.Hangar, 1, singleton: true);
        await AssertRefusedAsync(m, ItemMutationRefusal.NotInReach, new(Character, Station, mutator, moduleElsewhere));
        var mutaplasmidElsewhere = await m.AddAsync(Mutaplasmid, Character, OtherStation, ItemFlags.Hangar);
        await AssertRefusedAsync(m, ItemMutationRefusal.NotInReach, new(Character, Station, mutaplasmidElsewhere, source));
    }

    [PgFact]
    public async Task A_fitted_module_is_not_an_input()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, _) = await StandardAsync(m);
        var fitted = await m.AddAsync(Microwarpdrive, Character, Station, ItemFlags.LowSlot0, 1, singleton: true);
        await AssertRefusedAsync(m, ItemMutationRefusal.NotStowed, new(Character, Station, mutator, fitted));
    }

    [PgFact]
    public async Task A_stack_is_not_an_input()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, _) = await StandardAsync(m);
        var stack = await m.AddAsync(Microwarpdrive, Character, Station, ItemFlags.Hangar, 2);
        await AssertRefusedAsync(m, ItemMutationRefusal.NotSingleton, new(Character, Station, mutator, stack));
    }

    [PgFact]
    public async Task A_blueprint_locked_item_is_refused()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        await m.SetBlueprintLockedAsync(source);
        await AssertRefusedAsync(m, ItemMutationRefusal.ItemLocked, new(Character, Station, mutator, source));
    }

    [PgFact]
    public async Task Only_a_mutaplasmid_can_mutate()
    {
        await using var m = await Mutations.CreateAsync();
        var notAMutaplasmid = await m.AddAsync(Microwarpdrive, Character, Station, ItemFlags.Hangar, 1, singleton: true);
        var (_, source) = await StandardAsync(m);
        await AssertRefusedAsync(m, ItemMutationRefusal.NotAMutaplasmid, new(Character, Station, notAMutaplasmid, source));
    }

    [PgFact]
    public async Task An_input_the_mutaplasmid_does_not_take_is_refused_and_so_is_an_output()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, _) = await StandardAsync(m);
        var notApplicable = await m.AddAsync(SmallArmorRepairerII, Character, Station, ItemFlags.Hangar, 1, singleton: true);
        await AssertRefusedAsync(m, ItemMutationRefusal.InputNotMappable, new(Character, Station, mutator, notApplicable));

        // a mutated item is never mutated again: an output of the data is no input of any mutaplasmid
        var output = await m.AddAsync(Abyssal, Character, Station, ItemFlags.Hangar, 1, singleton: true);
        await AssertRefusedAsync(m, ItemMutationRefusal.InputNotMappable, new(Character, Station, mutator, output));
    }

    [PgFact]
    public async Task An_unpublished_input_is_refused()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutatorType, inputType) = await m.UnpublishedPairAsync();
        var mutator = await m.AddAsync(mutatorType, Character, Station, ItemFlags.Hangar);
        var input = await m.AddAsync(inputType, Character, Station, ItemFlags.Hangar, 1, singleton: true);
        await AssertRefusedAsync(m, ItemMutationRefusal.InputNotMappable, new(Character, Station, mutator, input));
    }

    [PgFact]
    public async Task A_source_without_a_base_value_for_a_mutated_attribute_is_refused_not_completed()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        // the static data of the source type without attribute 554 (signatureRadiusBonus), which the mutaplasmid moves
        var withoutSignature = new DogmaStatic(Array.Empty<AttributeDef>(), Array.Empty<EffectDef>(),
            [new TypeDef(Microwarpdrive, 1, 1, new Dictionary<int, double> { [6] = 180, [20] = 500, [30] = 150, [50] = 50 }, Array.Empty<int>())]);
        var items = await m.ItemsAsync();

        var outcome = await m.Service(statics: withoutSignature).MutateAsync(new(Character, Station, mutator, source));

        Assert.Equal(ItemMutationRefusal.AttributeMissingInSource, Assert.IsType<ItemMutationRefused>(outcome).Reason);
        Assert.Equal(items, await m.ItemsAsync());
        Assert.Equal(0, await m.ProvenanceCountAsync());
    }

    // ---- atomicity -------------------------------------------------------------------------------------------------------------------------

    [PgFact]
    public async Task A_failed_provenance_write_rolls_back_the_consumption_and_the_creation()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        var items = await m.ItemsAsync();
        await m.DropAsync(m.ProvenanceTable);

        // the item is consumed and created inside the transaction before the record is written; the write fails
        await Assert.ThrowsAsync<PostgresException>(() => m.Service().MutateAsync(new(Character, Station, mutator, source)));

        Assert.Equal(items, await m.ItemsAsync());
        await m.Bus.PumpAsync();
        Assert.Empty(m.Delivered);
    }

    [PgFact]
    public async Task A_failed_announcement_rolls_back_the_provenance_and_both_inputs_remain()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        var items = await m.ItemsAsync();
        await m.DropAsync(m.OutboxTable);

        // the record is written, then the outbox row fails: the whole transaction is rolled back
        await Assert.ThrowsAsync<PostgresException>(() => m.Service().MutateAsync(new(Character, Station, mutator, source)));

        Assert.Equal(items, await m.ItemsAsync());
        Assert.Equal(0, await m.ProvenanceCountAsync());
    }

    [PgFact]
    public async Task A_roll_that_cannot_be_drawn_leaves_both_inputs_and_announces_nothing()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        var items = await m.ItemsAsync();
        MutationRoll failing = attribute => attribute == 30 ? throw new InvalidOperationException("no roll") : 0.5;

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Service(failing).MutateAsync(new(Character, Station, mutator, source)));

        Assert.Equal(items, await m.ItemsAsync());
        Assert.Equal(0, await m.ProvenanceCountAsync());
        await m.Bus.PumpAsync();
        Assert.Empty(m.Delivered);
    }

    // ---- concurrency, restart -----------------------------------------------------------------------------------------------------------

    [PgFact]
    public async Task Two_requests_for_one_singleton_source_make_exactly_one_item()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m, mutatorQuantity: 2);
        var service = m.Service();
        var request = new ItemMutationRequest(Character, Station, mutator, source);

        var outcomes = await Task.WhenAll(service.MutateAsync(request), service.MutateAsync(request));

        Assert.Single(outcomes.OfType<ItemMutationAccepted>());
        Assert.Equal(ItemMutationRefusal.SourceMissing, Assert.Single(outcomes.OfType<ItemMutationRefused>()).Reason);
        Assert.Equal(1, (await m.Items.GetAsync(mutator))!.Quantity);
        Assert.Single(await m.Items.OwnedByAsync(Character), i => i.TypeId == Abyssal);
        Assert.Equal(1, await m.ProvenanceCountAsync());
    }

    [PgFact]
    public async Task After_a_restart_the_stored_values_are_read_back_and_a_made_item_is_still_no_input()
    {
        await using var m = await Mutations.CreateAsync();
        var (mutator, source) = await StandardAsync(m);
        var made = Assert.IsType<ItemMutationAccepted>(await m.Service().MutateAsync(new(Character, Station, mutator, source))).Receipt;

        // a new store on the same schema (a restarted process) reads the stored values back exactly
        var reloaded = await new ItemMutationStore(m.Source, m.Schema).GetAsync(made.ItemId);
        Assert.NotNull(reloaded);
        AssertSameBits(made.MutatedValues, reloaded.MutatedValues);
        AssertSameBits(made.Rolls, reloaded.Rolls);
        Assert.Equal(Abyssal, (await new PgItemStore(m.Source, m.Schema).GetAsync(made.ItemId))!.TypeId);

        // the restarted authority never draws again for a stored item; the made item is an output, so it is refused as an input
        var second = await m.AddAsync(Mutaplasmid, Character, Station, ItemFlags.Hangar);
        await AssertRefusedAsync(m, ItemMutationRefusal.InputNotMappable, new(Character, Station, second, made.ItemId),
            roll: _ => throw new InvalidOperationException("a stored item is never re-rolled"));
    }
}
