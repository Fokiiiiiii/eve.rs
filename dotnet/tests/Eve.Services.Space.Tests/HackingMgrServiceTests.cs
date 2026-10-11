using Eve.Hacking;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space.Tests;

public sealed class HackingMgrServiceTests
{
    private const long CharacterId = 42;

    private static HackingAttempt Attempt(int seed = 5) => new(9001, 22329, HackingWire.GameTypeHacking, 1, 40, 20, 3, seed);

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    private static PyValue Tile(int x, int y) => Py.Tuple(new PyInt(x), new PyInt(y));

    private static IReadOnlyList<int> EventIds(Events.Event note) =>
        ((PyList)note.Args[0]).Items.Select(item => (int)Number(Field(item, "eventID"))).ToArray();

    private static PyValue Field(PyValue keyVal, string key) =>
        ((PyDict)((PyObject)keyVal).Args).Entries.Single(entry => JsSemantics.StringValue(entry.Key) == key).Value;

    private static long Number(PyValue value) => value switch
    {
        PyInt i => i.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };

    [Fact]
    public void Opening_an_attempt_sends_its_start_events_to_the_client()
    {
        var store = new HackingAttempts();
        var service = new HackingMgrService(store);
        var session = new ClientSession(new Sink()) { CharacterId = CharacterId };

        service.Start(session, CharacterId, Attempt());

        var note = Assert.Single(Events.Of(session));
        Assert.Equal("OnHackingTurnComplete", note.Name);
        Assert.Equal("clientID", note.IdType);
        var ids = EventIds(note);
        Assert.Equal(HackingWire.EventGameStyle, ids[0]);
        Assert.Equal(HackingWire.EventVirusCreated, ids[^2]);
        Assert.Equal(HackingWire.EventGameStart, ids[^1]);
        Assert.NotNull(store.Get(CharacterId));
    }

    [Fact]
    public async Task Clicking_an_explored_tile_sends_its_flip_and_keeps_the_attempt()
    {
        var store = new HackingAttempts();
        var service = new HackingMgrService(store);
        var session = new ClientSession(new Sink()) { CharacterId = CharacterId };
        service.Start(session, CharacterId, Attempt());
        Events.Of(session);

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));

        var note = Assert.Single(Events.Of(session));
        Assert.Equal(HackingWire.EventTileFlipped, EventIds(note)[0]);
        Assert.NotNull(store.Get(CharacterId));
    }

    [Fact]
    public async Task Clicking_a_tile_no_explored_node_reaches_sends_only_an_ack()
    {
        var store = new HackingAttempts();
        var service = new HackingMgrService(store);
        var session = new ClientSession(new Sink()) { CharacterId = CharacterId };
        service.Start(session, CharacterId, Attempt());
        Events.Of(session);

        await service.CallAsync(Call("ClickedOnTile", session, Tile(9, 0)));

        Assert.Equal(new[] { HackingWire.EventAck }, EventIds(Assert.Single(Events.Of(session))));
    }

    [Fact]
    public async Task Quitting_sends_the_loss_and_ends_the_attempt()
    {
        var store = new HackingAttempts();
        var service = new HackingMgrService(store);
        var session = new ClientSession(new Sink()) { CharacterId = CharacterId };
        service.Start(session, CharacterId, Attempt());
        Events.Of(session);

        await service.CallAsync(Call("QuitHackingAttempt", session));

        Assert.Equal(new[] { HackingWire.EventGameLost }, EventIds(Assert.Single(Events.Of(session))));
        Assert.Null(store.Get(CharacterId));
    }

    [Fact]
    public async Task A_call_with_no_open_attempt_is_acknowledged_and_a_call_without_a_character_is_ignored()
    {
        var service = new HackingMgrService(new HackingAttempts());
        var session = new ClientSession(new Sink()) { CharacterId = CharacterId };

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));
        Assert.Equal(new[] { HackingWire.EventAck }, EventIds(Assert.Single(Events.Of(session))));

        var anonymous = new ClientSession(new Sink());
        var result = await service.CallAsync(Call("ClickedOnTile", anonymous, Tile(0, 3)));
        Assert.IsType<PyNone>(result);
        Assert.Empty(Events.Of(anonymous));
    }
}
