using Eve.Hacking;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space.Tests;

public sealed class HackingMgrServiceTests
{
    private const long CharacterId = 42;
    private const long ContainerId = 500;

    private static readonly HackableTarget Data = new(ContainerId, 30_000_142, 1, Relic: false);

    private static AnalyzerValues Analyzer(double maxRange = 54_000, bool data = true, bool relic = false) =>
        new(22329, data, relic, maxRange, 40, 20, 3, 0);

    private sealed class FakeSite : IHackingSite
    {
        public HackableTarget? Target { get; set; } = Data;
        public bool InReach { get; set; } = true;
        public List<(HackableTarget Target, bool Won)> Ended { get; } = [];

        public ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Target is { } target && target.ItemId == targetId ? target : null);

        public ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken) =>
            ValueTask.FromResult(InReach);

        public ValueTask AttemptEndedAsync(ClientSession session, HackableTarget target, bool won, CancellationToken cancellationToken)
        {
            Ended.Add((target, won));
            return ValueTask.CompletedTask;
        }
    }

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

    private static (HackingMgrService Service, HackingAttempts Store, FakeSite Site, ClientSession Session) Rig()
    {
        var store = new HackingAttempts();
        var site = new FakeSite();
        return (new HackingMgrService(store, site), store, site, new ClientSession(new Sink()) { CharacterId = CharacterId });
    }

    [Fact]
    public async Task The_analyzer_opens_an_attempt_and_the_client_gets_the_start_events()
    {
        var (service, store, _, session) = Rig();

        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);

        var note = Assert.Single(Events.Of(session));
        Assert.Equal("OnHackingTurnComplete", note.Name);
        Assert.Equal("clientID", note.IdType);
        var ids = EventIds(note);
        Assert.Equal(HackingWire.EventGameStyle, ids[0]);
        Assert.Equal(HackingWire.EventVirusCreated, ids[^2]);
        Assert.Equal(HackingWire.EventGameStart, ids[^1]);
        Assert.Equal(ContainerId, store.Get(CharacterId)!.Target.ItemId);
    }

    [Fact]
    public async Task Clicking_an_explored_tile_sends_its_flip_and_keeps_the_attempt()
    {
        var (service, store, _, session) = Rig();
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        Events.Of(session);

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));

        var note = Assert.Single(Events.Of(session));
        Assert.Equal(HackingWire.EventTileFlipped, EventIds(note)[0]);
        Assert.NotNull(store.Get(CharacterId));
    }

    [Fact]
    public async Task Clicking_a_tile_no_explored_node_reaches_sends_only_an_ack()
    {
        var (service, _, _, session) = Rig();
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        Events.Of(session);

        await service.CallAsync(Call("ClickedOnTile", session, Tile(9, 0)));

        Assert.Equal(new[] { HackingWire.EventAck }, EventIds(Assert.Single(Events.Of(session))));
    }

    [Fact]
    public async Task Quitting_sends_the_loss_counts_one_failure_and_ends_the_attempt()
    {
        var (service, store, site, session) = Rig();
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        Events.Of(session);

        await service.CallAsync(Call("QuitHackingAttempt", session));

        Assert.Equal(new[] { HackingWire.EventGameLost }, EventIds(Assert.Single(Events.Of(session))));
        Assert.Null(store.Get(CharacterId));
        Assert.Equal([(Data, false)], site.Ended);
    }

    [Fact]
    public async Task Leaving_the_analyzer_reach_loses_the_window_on_the_next_call()
    {
        var (service, store, site, session) = Rig();
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        Events.Of(session);
        site.InReach = false;

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));

        Assert.Equal(new[] { HackingWire.EventGameLost }, EventIds(Assert.Single(Events.Of(session))));
        Assert.Null(store.Get(CharacterId));
        Assert.Equal([(Data, false)], site.Ended);
    }

    [Fact]
    public async Task A_warp_loses_the_window()
    {
        var (service, store, site, session) = Rig();
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        Events.Of(session);

        await service.CloseOnWarpAsync(session, CharacterId, CancellationToken.None);

        Assert.Equal(new[] { HackingWire.EventGameLost }, EventIds(Assert.Single(Events.Of(session))));
        Assert.Null(store.Get(CharacterId));
        Assert.Equal([(Data, false)], site.Ended);
    }

    [Fact]
    public async Task Destroying_the_core_wins_and_reports_the_clear()
    {
        var (service, store, site, session) = Rig();
        var board = HackingBoard.Blank();
        var core = board.Tiles[new Coord(0, 3)];
        core.Type = HackingWire.TypeCore;
        core.Subtype = HackingWire.SubtypeCoreLow;
        core.Coherence = core.Strength = 10;
        var attempt = new HackingAttempt(ContainerId, 22329, HackingWire.GameTypeHacking, 1, 40, 20, 3, 1);
        store.Set(CharacterId, new HackingWindow(new HackingGame(attempt, board), Data, 0));

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));

        Assert.Contains(HackingWire.EventGameWon, EventIds(Assert.Single(Events.Of(session))));
        Assert.Null(store.Get(CharacterId));
        Assert.Equal([(Data, true)], site.Ended);
    }

    [Fact]
    public async Task A_call_with_no_open_attempt_is_acknowledged_and_a_call_without_a_character_is_ignored()
    {
        var (service, _, _, session) = Rig();

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));
        Assert.Equal(new[] { HackingWire.EventAck }, EventIds(Assert.Single(Events.Of(session))));

        var anonymous = new ClientSession(new Sink());
        var result = await service.CallAsync(Call("ClickedOnTile", anonymous, Tile(0, 3)));
        Assert.IsType<PyNone>(result);
        Assert.Empty(Events.Of(anonymous));
    }

    [Fact]
    public async Task The_analyzer_is_refused_for_no_container_for_the_wrong_kind_and_for_a_container_being_hacked()
    {
        var (service, _, site, session) = Rig();

        site.Target = null;
        Assert.Equal("ANALYZER_TARGET_INVALID", await service.AnalyzerRefusalAsync(CharacterId, ContainerId, Analyzer(), CancellationToken.None));

        site.Target = Data;
        Assert.Equal("ANALYZER_TYPE_MISMATCH", await service.AnalyzerRefusalAsync(CharacterId, ContainerId, Analyzer(data: false, relic: true), CancellationToken.None));

        Assert.Null(await service.AnalyzerRefusalAsync(CharacterId, ContainerId, Analyzer(), CancellationToken.None));
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        Assert.Equal("ANALYZER_TARGET_BUSY", await service.AnalyzerRefusalAsync(43, ContainerId, Analyzer(), CancellationToken.None));
    }
}
