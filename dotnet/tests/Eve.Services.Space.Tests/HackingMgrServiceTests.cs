using Eve.Hacking;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Npgsql;

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
        ((PyTuple)note.Args[0]).Items.Select(item => (int)Number(Field(item, "eventID"))).ToArray();

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
    public async Task With_no_attempt_open_a_quit_sends_nothing_and_a_tile_click_sends_the_ack()
    {
        var (service, _, _, session) = Rig();

        Assert.IsType<PyNone>(await service.CallAsync(Call("QuitHackingAttempt", session)));
        Assert.Empty(Events.Of(session));

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 0)));
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

/// <summary>
/// The analyzer's virus stats after dogma, as the hacking service reads them (SpaceSessions.AnalyzerValuesOf). The base numbers are the module's own
/// SDE attributes (1909 coherence, 1910 strength): Alpha Data Analyzer I has 50 coherence and needs Hacking, Relic Analyzer I has 40 and needs Archaeology.
/// </summary>
public sealed class AnalyzerSkillTests
{
    private const int DataAnalyzer = 22329, RelicAnalyzer = 22177, Hacking = 21718, Archaeology = 13278, Rorqual = 28352;
    private const int VirusCoherence = 1909, VirusStrength = 1910;
    private const long CharacterId = 80_201, ShipId = 80_202, AnalyzerId = 80_203;
    private const int HighSlotFlag = 27;

    private static async Task<DogmaStatic> LoadStaticsAsync()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([DataAnalyzer, RelicAnalyzer, Rorqual, Hacking, Archaeology]);
        return statics;
    }

    /// <summary>A Rorqual with the analyzer in a high slot, and the pilot trained to the given levels (skill type id to level).</summary>
    private static ShipSnapshot Fitted(DogmaStatic statics, int analyzerType, IReadOnlyDictionary<int, int> levels)
    {
        var character = new Eve.Characters.Character(
            CharacterId, 1, "Analyzer test", 1, 1, 1, 1, 1, 1, 1000001,
            null, null, 60003760, 60003760, 30000142, 20000020, 10000001, ShipId,
            DateTimeOffset.UnixEpoch, null, null);
        var ship = new Eve.Inventory.Item(ShipId, Rorqual, CharacterId, 60003760, 0, 1, true, null);
        var analyzer = new Eve.Inventory.Item(AnalyzerId, analyzerType, CharacterId, ShipId, HighSlotFlag, 1, true, null);
        var world = new DogmaWorld(statics, CharacterId, ShipId);
        world.Add(new DogmaItem(CharacterId, character.TypeId, CharacterId, ShipId, ShipSnapshot.PilotFlag, ItemState.Online));
        world.Add(new DogmaItem(ShipId, Rorqual, CharacterId, 60003760, 0, ItemState.Online));
        world.Add(new DogmaItem(AnalyzerId, analyzerType, CharacterId, ShipId, HighSlotFlag, ItemState.Online));
        foreach (var (skill, level) in levels)
        {
            var skillId = CharacterId * 100_000 + skill;
            world.Add(new DogmaItem(skillId, skill, CharacterId, CharacterId, 7));
            world.SetValue(skillId, DogmaWorld.SkillLevel, level);
        }
        return new ShipSnapshot { Character = character, Ship = ship, Fitted = [analyzer], Charges = [], Skills = [], World = world };
    }

    [PgFact]
    public async Task A_data_analyzer_untrained_has_its_sde_base()
    {
        var statics = await LoadStaticsAsync();
        var snapshot = Fitted(statics, DataAnalyzer, new Dictionary<int, int>());

        Assert.Equal(50d, snapshot.World.Get(AnalyzerId, VirusCoherence));
        Assert.Equal(20d, snapshot.World.Get(AnalyzerId, VirusStrength));
    }

    [PgFact]
    public async Task Hacking_adds_ten_coherence_a_level_to_a_data_analyzer_and_not_the_strength()
    {
        var statics = await LoadStaticsAsync();
        var snapshot = Fitted(statics, DataAnalyzer, new Dictionary<int, int> { [Hacking] = 4 });

        Assert.Equal(90d, snapshot.World.Get(AnalyzerId, VirusCoherence));
        Assert.Equal(20d, snapshot.World.Get(AnalyzerId, VirusStrength));
    }

    [PgFact]
    public async Task Archaeology_does_not_change_a_data_analyzer()
    {
        var statics = await LoadStaticsAsync();
        var snapshot = Fitted(statics, DataAnalyzer, new Dictionary<int, int> { [Archaeology] = 5 });

        Assert.Equal(50d, snapshot.World.Get(AnalyzerId, VirusCoherence));
    }

    [PgFact]
    public async Task Archaeology_adds_ten_coherence_a_level_to_a_relic_analyzer()
    {
        var statics = await LoadStaticsAsync();
        var untrained = Fitted(statics, RelicAnalyzer, new Dictionary<int, int>());
        var trained = Fitted(statics, RelicAnalyzer, new Dictionary<int, int> { [Archaeology] = 3 });
        var hackingOnly = Fitted(statics, RelicAnalyzer, new Dictionary<int, int> { [Hacking] = 5 });

        Assert.Equal(40d, untrained.World.Get(AnalyzerId, VirusCoherence));
        Assert.Equal(70d, trained.World.Get(AnalyzerId, VirusCoherence));
        Assert.Equal(20d, trained.World.Get(AnalyzerId, VirusStrength));
        Assert.Equal(40d, hackingOnly.World.Get(AnalyzerId, VirusCoherence));
    }
}
