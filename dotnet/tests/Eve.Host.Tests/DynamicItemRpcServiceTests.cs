using Eve.Dogma;
using Eve.Events;
using Eve.Host;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The <c>dynamicItemService</c> route on the real authority, stores and static data (skipped without EVE_PG_URL; skipped is not passed). The
/// worked example is mutaplasmid 47297 on 50MN Microwarpdrive I (12052), giving 50MN Abyssal Microwarpdrive (47408), with the rolls of ADR 0016.
/// </summary>
public sealed class DynamicItemRpcServiceTests
{
    private const long ClockMs = 1_700_000_000_123;
    private const long Character = 20, Stranger = 21, Station = 60015249;
    private const int Mutaplasmid = 47297, Input = 12052, Result = 47408, SmallArmorRepairerII = 1183;

    private static readonly Dictionary<int, double> Rolls = new() { [6] = 0.5, [20] = 0.25, [30] = 0.75, [50] = 0.0, [554] = 1.0 };

    /// <summary>The route as the Host builds it, over a scratch item schema, with a scratch scheduler and event bus.</summary>
    private sealed class Route : IAsyncDisposable
    {
        private readonly string[] _schemas;
        public required NpgsqlDataSource Source { get; init; }
        public required PgItemStore Items { get; init; }
        public required DogmaStatic Dogma { get; init; }
        public required DynamicItemRpcService Service { get; init; }
        public required Sink Packets { get; init; }
        public required ClientSession Session { get; init; }
        public required ClientSession NoCharacter { get; init; }

        private Route(string[] schemas) => _schemas = schemas;

        public static async Task<Route> CreateAsync(string pgUrl)
        {
            var source = NpgsqlDataSource.Create(pgUrl);
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var dogma = await DogmaStatic.LoadAsync(sde);
            var catalog = await MutatorCatalog.LoadAsync(sde);
            var id = Guid.NewGuid().ToString("N")[..12];
            var schemas = new[] { $"rpc_dyn_i_{id}", $"rpc_dyn_e_{id}", $"rpc_dyn_s_{id}" };
            await PgItemStore.MigrateAsync(source, schemas[0]);
            await DurableScheduler.MigrateAsync(source, schemas[2]);
            await EventBus.MigrateAsync(source, schemas[1]);
            var items = new PgItemStore(source, schemas[0]);
            var bus = new EventBus(source, new DurableScheduler(source, () => ClockMs, new SchedulerOptions { Concurrency = 1 }, schemas[2]), null, schemas[1]);
            var mutations = new ItemMutationService(source, items, items.Mutations, catalog, dogma, new BusItemMutationEvents(bus), () => ClockMs,
                attribute => Rolls[attribute]);
            var service = new DynamicItemRpcService(items, mutations, dogma, new ItemTypeCatalog(sde));
            var packets = new Sink();
            var session = new ClientSession(packets);
            session.Set("charid", new PyInt(Character));
            return new Route(schemas)
            {
                Source = source, Items = items, Dogma = dogma, Service = service, Packets = packets, Session = session,
                NoCharacter = new ClientSession(new Sink()),
            };
        }

        public async Task<long> GiveAsync(int type, long owner, long location = Station, int flag = ItemFlags.Hangar, bool singleton = true)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await Items.CreateAsync(connection, transaction, new NewItem(type, owner, location, flag, 1, singleton));
            await transaction.CommitAsync();
            return id;
        }

        public Task<PyValue> CallAsync(string method, ClientSession session, params long[] args) =>
            Service.CallAsync(new ServiceCall { Method = method, Args = [.. args.Select(a => (PyValue)new PyLong(a))], Session = session }).AsTask();

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var connection = await Source.OpenConnectionAsync();
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    /// <summary>The packets a session was sent, in order.</summary>
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    /// <summary>The notifications among the packets: name, how they are addressed, and their arguments (as the client receives them).</summary>
    private static List<(string Name, PyValue[] Args)> Notifications(Sink sink)
    {
        var events = new List<(string Name, PyValue[] Args)>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification }) continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            events.Add((JsSemantics.StringValue(address[1])!, [.. ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items]));
        }
        return events;
    }

    /// <summary>The item an <c>OnItemChange</c> is about, its quantity, and the differences it carries (the place, flag and stack it had).</summary>
    private static (long ItemId, long Quantity, Dictionary<int, long> Before) Change((string Name, PyValue[] Args) notification)
    {
        Assert.Equal("OnItemChange", notification.Name);
        var row = ((PyPackedRow)notification.Args[0]).Values;
        var before = ((PyDict)notification.Args[1]).Entries.ToDictionary(e => (int)Number(e.Key), e => Number(e.Value));
        return (Number(row[0]), Number(row[5]), before);
    }

    private static long Number(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, _ => throw new InvalidOperationException(value.ToString()) };

    /// <summary>The message of a refusal: the client's <c>CustomNotify</c> error and the text it shows.</summary>
    private static string Notice(Exception exception)
    {
        var error = Assert.IsType<MachoWrappedException>(exception).Error;
        Assert.Equal("CustomNotify", error.UserErrorMessage);
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Payload).Header).Items[1]).Items;
        return ((PyStr)((PyDict)arguments[1]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "notify").Value).Value;
    }

    private static string PgUrl() => Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");

    [PgFact]
    public async Task CreateDynamicItem_makes_the_mutated_item_and_announces_its_changes_in_order_before_the_reply()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        var mutator = await route.GiveAsync(Mutaplasmid, Character, singleton: false);
        var source = await route.GiveAsync(Input, Character);

        var reply = await route.CallAsync("CreateDynamicItem", route.Session, mutator, source);

        var made = Assert.IsType<PyInt>(reply).Value;
        Assert.Equal(Result, (await route.Items.GetAsync(made))!.TypeId);

        // the mutaplasmid and the source are gone, announced first, then the item made; then the reply
        var changes = Notifications(route.Packets).Select(Change).ToList();
        Assert.Equal(new[] { mutator, source, made }, changes.Select(c => c.ItemId));
        Assert.Equal(new[] { 0L, 0L, 1L }, changes.Select(c => c.Quantity));
        Assert.Equal(0, changes[2].Before[3]); // the item made came from nothing: its previous place is 0
        Assert.Equal(1L, changes[1].Before[9]); // the source was a single module of stack size 1 (key 9)
        Assert.Null(await route.Items.GetAsync(mutator));
        Assert.Null(await route.Items.GetAsync(source));
    }

    [PgFact]
    public async Task GetDynamicItemInfo_answers_from_the_stored_record_and_is_none_for_an_item_that_was_not_mutated()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        var mutator = await route.GiveAsync(Mutaplasmid, Character, singleton: false);
        var source = await route.GiveAsync(Input, Character);
        var made = Assert.IsType<PyInt>(await route.CallAsync("CreateDynamicItem", route.Session, mutator, source)).Value;
        var plain = await route.GiveAsync(Input, Character);

        var info = Assert.IsType<PyObject>(await route.CallAsync("GetDynamicItemInfo", route.Session, made)).Args;
        var fields = Assert.IsType<PyDict>(info);
        Assert.Equal(new PyInt(made), Field(fields, "itemID"));
        Assert.Equal(new PyInt(Result), Field(fields, "typeID"));
        Assert.Equal(new PyInt(Input), Field(fields, "sourceTypeID"));
        Assert.Equal(new PyInt(source), Field(fields, "sourceItemID"));
        Assert.Equal(new PyInt(Mutaplasmid), Field(fields, "mutatorTypeID"));
        Assert.Equal(new PyInt(mutator), Field(fields, "mutatorItemID"));
        Assert.Equal(new PyInt(Character), Field(fields, "characterID"));

        // the effective attributes: the rolled values, the input's base for the rest, the skill of the result
        var attributes = Assert.IsType<PyDict>(Field(fields, "attributes"));
        Assert.Equal(180.0, Num(attributes, 6), 9);
        Assert.Equal(198.75, Num(attributes, 30), 9);
        Assert.Equal(40.0, Num(attributes, 9), 9);
        Assert.Equal(10000.0, Num(attributes, 73), 9);
        Assert.Equal(3454.0, Num(attributes, 182), 9);
        Assert.Equal(4.0, Num(attributes, 277), 9);
        // the client's GetMutatedAttributes reads every mutator attribute: all of them are present
        foreach (var attribute in new[] { 6, 20, 30, 50, 554 })
            Assert.Contains(attributes.Entries, e => e.Key == new PyInt(attribute));
        // the modifiers carry the rolled multiplier of each mutator attribute: 20 is 0.9 + 0.25 x 0.2
        Assert.Equal(0.95, Num(Assert.IsType<PyDict>(Field(fields, "modifiers")), 20), 9);
        Assert.Equal(500.0, Num(Assert.IsType<PyDict>(Field(fields, "baseAttributes")), 20), 9);

        Assert.IsType<PyNone>(await route.CallAsync("GetDynamicItemInfo", route.Session, plain));
    }

    [PgFact]
    public async Task Refusals_are_client_notices_and_change_nothing_and_announce_nothing()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        var mutator = await route.GiveAsync(Mutaplasmid, Character, singleton: false);
        var source = await route.GiveAsync(Input, Character);
        var stranger = await route.GiveAsync(Input, Stranger);
        var repairer = await route.GiveAsync(SmallArmorRepairerII, Character);
        var notMutaplasmid = await route.GiveAsync(Input, Character, singleton: false);

        async Task AssertRefused(string expected, ClientSession session, long a, long b)
        {
            var before = Notifications(route.Packets).Count;
            var notice = Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => route.CallAsync("CreateDynamicItem", session, a, b)));
            Assert.Equal(expected, notice);
            Assert.Equal(before, Notifications(route.Packets).Count);
        }

        await AssertRefused("You need an active character to mutate an item.", route.NoCharacter, mutator, source);
        await AssertRefused("The mutaplasmid and source item must be different inventory items.", route.Session, source, source);
        await AssertRefused("You can only mutate items owned by the active character.", route.Session, mutator, stranger);
        await AssertRefused("That item is not a supported mutaplasmid.", route.Session, notMutaplasmid, source);
        await AssertRefused("That mutaplasmid cannot mutate the selected source item.", route.Session, mutator, repairer);
        await AssertRefused("The selected mutaplasmid or source item could not be found.", route.Session, mutator, 999_999_999);

        // nothing was consumed by any refusal
        Assert.NotNull(await route.Items.GetAsync(mutator));
        Assert.NotNull(await route.Items.GetAsync(source));
        Assert.NotNull(await route.Items.GetAsync(stranger));
        Assert.Empty(Notifications(route.Packets));
    }

    private static PyValue Field(PyDict fields, string name) =>
        fields.Entries.Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;

    private static double Num(PyDict values, int key) => values.Entries.Single(pair => pair.Key == new PyInt(key)).Value switch
    {
        PyInt i => i.Value,
        PyLong l => l.Value,
        PyReal r => r.Value,
        var other => throw new InvalidOperationException(other.ToString()),
    };
}
