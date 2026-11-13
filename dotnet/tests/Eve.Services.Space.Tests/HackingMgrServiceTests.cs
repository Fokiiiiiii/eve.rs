using Eve.Hacking;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Skills;
using Eve.Ships;
using Eve.Space;
using Eve.Scheduler;
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
        public bool FailNextEnd { get; set; }
        public Action<int>? EndedCountChanged { get; set; }
        public List<(HackableTarget Target, bool Won)> Ended { get; } = [];
        public HashSet<Guid> ActiveReservations { get; } = [];
        public List<Guid> ReleasedReservations { get; } = [];

        public ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Target is { } target && target.ItemId == targetId ? target : null);

        public ValueTask<string?> AnalyzerStartRefusalAsync(
            ClientSession session, HackableTarget target, double maxRange, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken) =>
            ValueTask.FromResult(InReach);

        public ValueTask<bool> TryReserveAsync(ClientSession session, HackableTarget target, Guid reservationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ActiveReservations.Add(reservationId));

        public ValueTask ReleaseReservationAsync(Guid reservationId, CancellationToken cancellationToken)
        {
            ActiveReservations.Remove(reservationId);
            ReleasedReservations.Add(reservationId);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<HackingLootItem>> AttemptEndedAsync(
            ClientSession session, HackableTarget target, Guid reservationId, bool won, CancellationToken cancellationToken)
        {
            Ended.Add((target, won));
            EndedCountChanged?.Invoke(Ended.Count);
            if (FailNextEnd)
            {
                FailNextEnd = false;
                throw new InvalidOperationException("site settlement failed");
            }
            ActiveReservations.Remove(reservationId);
            return ValueTask.FromResult<IReadOnlyList<HackingLootItem>>([]);
        }
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    private static PyValue Tile(int x, int y) => Py.Tuple(new PyInt(x), new PyInt(y));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(TimeSpan.FromMilliseconds(1), timeout.Token);
    }

    private static IReadOnlyList<int> EventIds(Events.Event note) =>
        ((PyTuple)note.Args[0]).Items.Select(item => (int)Number(Field(item, "eventID"))).ToArray();

    private static PyValue FirstHiddenNeighbour(Events.Event startNotification)
    {
        var tiles = ((PyTuple)startNotification.Args[0]).Items
            .Where(item => Number(Field(item, "eventID")) == HackingWire.EventTileCreated)
            .Select(item => Field(item, "eventData"))
            .Select(data =>
            {
                var coord = Assert.IsType<PyTuple>(Field(data, "coord")).Items;
                return (Coord: new Coord((int)Number(coord[0]), (int)Number(coord[1])),
                    Hidden: Assert.IsType<PyBool>(Field(data, "hidden")).Value);
            }).ToList();
        var start = Assert.Single(tiles, tile => !tile.Hidden);
        var available = HackingBoard.Neighbours(start.Coord)
            .Where(coord => tiles.Any(tile => tile.Coord == coord && tile.Hidden)).ToList();
        Assert.NotEmpty(available);
        var selected = available[0];
        return Tile(selected.X, selected.Y);
    }

    private static PyValue Field(PyValue keyVal, string key)
    {
        var entries = keyVal switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict dict } => dict.Entries,
            _ => throw new Xunit.Sdk.XunitException($"Expected a hacking event mapping, got {keyVal.GetType().Name}"),
        };
        return entries.Single(entry => JsSemantics.StringValue(entry.Key) == key).Value;
    }

    private static long Number(PyValue value) => value switch
    {
        PyInt i => i.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };

    [PgFact]
    public async Task Analyzer_range_uses_both_simulated_radii_and_keeps_the_clients_one_meter_tolerance()
    {
        await using var rig = await Eve.Services.Space.Tests.Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var characterState = await rig.Characters.GetAsync(character.CharacterId);
        var solarSystemId = characterState!.SolarSystemId;
        var snapshot = await rig.Ships.LoadAsync(character.CharacterId);
        var shipRadius = rig.Statics.Type(snapshot!.Ship.TypeId)!.Attributes[162];
        Assert.True(shipRadius > 0);

        var session = await rig.SessionAsync(character, 1);
        var shipBinding = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipBinding,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);

        const long targetId = 7_000_000_000_000_909L;
        Target(rig, session, "add", targetId);
        rig.Sim.PositionRadiusAnswers[character.ShipId] = shipRadius;
        rig.Sim.PositionRadiusAnswers[targetId] = 50;
        rig.Sim.PositionAnswers[character.ShipId] = [0, 0, 0];
        rig.Sim.PositionAnswers[targetId] = [shipRadius + 50 + 101, 0, 0];
        var target = new HackableTarget(targetId, solarSystemId, 4, Relic: false);

        Assert.Null(await rig.Space.AnalyzerStartRefusalAsync(session, target, 100, CancellationToken.None));

        rig.Sim.PositionAnswers[targetId] = [shipRadius + 50 + 101.1, 0, 0];
        Assert.Equal("TARGET_OUT_OF_RANGE",
            await rig.Space.AnalyzerStartRefusalAsync(session, target, 100, CancellationToken.None));
    }

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
    public async Task Clicking_a_reachable_tile_from_the_generated_start_sends_its_flip_and_keeps_the_attempt()
    {
        var (service, store, _, session) = Rig();
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        var start = Assert.Single(Events.Of(session));

        await service.CallAsync(Call("ClickedOnTile", session, FirstHiddenNeighbour(start)));

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
        store.Set(CharacterId, new HackingWindow(new HackingGame(attempt, board), Data, 0, session));

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));
        Assert.Equal(new[] { HackingWire.EventTileFlipped }, EventIds(Assert.Single(Events.Of(session))));
        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));

        Assert.Contains(HackingWire.EventGameWon, EventIds(Assert.Single(Events.Of(session))));
        Assert.Null(store.Get(CharacterId));
        Assert.Equal([(Data, true)], site.Ended);
    }

    [Fact]
    public async Task A_replacement_session_cannot_operate_the_previous_sessions_hacking_window()
    {
        var (service, store, site, originalSession) = Rig();
        var board = HackingBoard.Blank();
        var core = board.Tiles[new Coord(0, 3)];
        core.Type = HackingWire.TypeCore;
        core.Subtype = HackingWire.SubtypeCoreLow;
        core.Coherence = core.Strength = 10;
        var attempt = new HackingAttempt(ContainerId, 22329, HackingWire.GameTypeHacking, 1, 40, 20, 3, 1);
        store.Set(CharacterId, new HackingWindow(new HackingGame(attempt, board), Data, 0, originalSession));
        var replacement = new ClientSession(new Sink()) { CharacterId = CharacterId };

        await service.CallAsync(Call("ClickedOnTile", replacement, Tile(0, 3)));
        await service.CallAsync(Call("QuitHackingAttempt", replacement));

        Assert.NotNull(store.Get(CharacterId));
        Assert.Equal(new[] { HackingWire.EventAck }, EventIds(Assert.Single(Events.Of(replacement))));
        Assert.Empty(site.Ended);

        await service.CallAsync(Call("ClickedOnTile", originalSession, Tile(0, 3)));
        Assert.Equal(new[] { HackingWire.EventTileFlipped }, EventIds(Assert.Single(Events.Of(originalSession))));
        await service.CallAsync(Call("ClickedOnTile", originalSession, Tile(0, 3)));

        Assert.Null(store.Get(CharacterId));
        Assert.Equal([(Data, true)], site.Ended);
    }

    [Fact]
    public async Task Closing_the_attempts_session_settles_the_lost_attempt_and_releases_its_target()
    {
        var (service, store, site, session) = Rig();
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        Events.Of(session);

        session.Close();

        Assert.Null(store.Get(CharacterId));
        Assert.Equal([(Data, false)], site.Ended);
    }

    [Fact]
    public async Task Closing_the_session_retries_a_failed_settlement_until_the_target_is_released()
    {
        var store = new HackingAttempts();
        var site = new FakeSite();
        using var timers = new LocalTimers(delay: (delay, _) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(1), delay);
            return Task.CompletedTask;
        });
        var service = new HackingMgrService(store, site, localTimers: timers);
        var session = new ClientSession(new Sink()) { CharacterId = CharacterId };
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        site.FailNextEnd = true;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        site.EndedCountChanged = count =>
        {
            if (count == 2)
                settled.TrySetResult();
        };

        session.Close();
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => store.Get(CharacterId) is null);

        Assert.Null(store.Get(CharacterId));
        Assert.Equal([(Data, false), (Data, false)], site.Ended);
        Assert.Empty(site.ActiveReservations);
    }

    [Fact]
    public async Task A_failed_win_settlement_keeps_the_attempt_and_sends_no_terminal_event_until_retry_commits()
    {
        var (service, store, site, session) = Rig();
        var board = HackingBoard.Blank();
        var core = board.Tiles[new Coord(0, 3)];
        core.Type = HackingWire.TypeCore;
        core.Subtype = HackingWire.SubtypeCoreLow;
        core.Coherence = core.Strength = 10;
        var attempt = new HackingAttempt(ContainerId, 22329, HackingWire.GameTypeHacking, 1, 40, 20, 3, 1);
        store.Set(CharacterId, new HackingWindow(new HackingGame(attempt, board), Data, 0, session));

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));
        Assert.Equal(new[] { HackingWire.EventTileFlipped }, EventIds(Assert.Single(Events.Of(session))));
        site.FailNextEnd = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3))).AsTask());

        Assert.NotNull(store.Get(CharacterId));
        Assert.Empty(Events.Of(session));

        await service.CallAsync(Call("ClickedOnTile", session, Tile(0, 3)));

        Assert.Null(store.Get(CharacterId));
        Assert.Contains(HackingWire.EventGameWon, EventIds(Assert.Single(Events.Of(session))));
        Assert.Equal([(Data, true), (Data, true)], site.Ended);
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
        Assert.Equal("ANALYZER_TARGET_INVALID", await service.AnalyzerRefusalAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None));

        site.Target = Data;
        Assert.Equal("ANALYZER_TYPE_MISMATCH", await service.AnalyzerRefusalAsync(session, CharacterId, ContainerId, Analyzer(data: false, relic: true), CancellationToken.None));

        Assert.Null(await service.AnalyzerRefusalAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None));
        await service.OpenAnalyzerAttemptAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None);
        var otherSession = new ClientSession(new Sink()) { CharacterId = 43 };
        Assert.Equal("ANALYZER_TARGET_BUSY", await service.AnalyzerRefusalAsync(otherSession, 43, ContainerId, Analyzer(), CancellationToken.None));
    }

    [Fact]
    public async Task A_character_cannot_hold_two_analyzer_start_reservations_at_once()
    {
        var (service, _, site, session) = Rig();
        Assert.Null(await service.AnalyzerRefusalAsync(session, CharacterId, ContainerId, Analyzer(), CancellationToken.None));

        var otherTarget = new HackableTarget(ContainerId + 1, Data.SolarSystemId, Data.Difficulty, Data.Relic);
        site.Target = otherTarget;
        var refusal = await service.AnalyzerRefusalAsync(session, CharacterId, otherTarget.ItemId, Analyzer(), CancellationToken.None);

        Assert.Equal("ANALYZER_TARGET_BUSY", refusal);
        Assert.Single(site.ActiveReservations);
    }

    [PgFact]
    public async Task A_simulation_refusal_releases_the_real_module_path_analyzer_reservation_for_retry()
    {
        await using var rig = await Eve.Services.Space.Tests.Rig.CreateAsync();
        var character = await rig.NewCharacterAsync(shipTypeId: 28_352);
        var analyzer = (await rig.Ships.GrantAsync(character.CharacterId, 22_329, 1, singleton: true))!.Single().After;
        var fitted = await rig.Ships.MoveAsync(
            character.CharacterId, analyzer.Id, Eve.Services.Space.Tests.Rig.Station, character.ShipId, 19, 1);
        Assert.True(fitted is Eve.Ships.ShipChanged, fitted is Eve.Ships.ShipRefused refused
            ? $"{refused.Reason} {refused.Fit} {refused.Detail}"
            : fitted.ToString());
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new CharacterSkill(21_718, 1, 0)]);
            await transaction.CommitAsync();
        }
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, analyzer.Id, true));
        var session = await rig.SessionAsync(character, 1);
        var shipBinding = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipBinding,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);

        var site = new FakeSite();
        var service = new HackingMgrService(new HackingAttempts(), site);
        rig.Space.HackingService = service;
        rig.Sim.ActivateAnswer = new CommandReply(false, 0, "NO_CAPACITOR");

        var refusal = await rig.Space.ActivateAsync(session, analyzer.Id, SpaceSessions.AnalyzerEffectName, ContainerId, 1, CancellationToken.None);

        Assert.Equal("NO_CAPACITOR", refusal?.Reason);
        Assert.Empty(site.ActiveReservations);
        Assert.Single(site.ReleasedReservations);

        Assert.Null(await service.AnalyzerRefusalAsync(session, character.CharacterId, ContainerId, Analyzer(), CancellationToken.None));
        Assert.Single(site.ActiveReservations);
        await service.CancelAnalyzerReservationAsync(session, ContainerId, CancellationToken.None);
        Assert.Empty(site.ActiveReservations);
    }

    [PgFact]
    public async Task The_registered_space_module_path_refuses_an_unlocked_site_container_before_reserving_it()
    {
        await using var rig = await Eve.Services.Space.Tests.Rig.CreateAsync();
        await using var sites = await SiteContainerRig.CreateAsync();
        const long containerId = 7_000_000_000_000_907L;
        const long otherSystemContainerId = 7_000_000_000_000_908L;

        var character = await rig.NewCharacterAsync(shipTypeId: 28_352);
        var solarSystemId = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;
        await sites.InsertAsync(new SiteContainerRecord(containerId, 5_300_000_000_907,
            solarSystemId, "containment_facility", false, 17, 306,
            [1_000, 2_000, 3_000], 50, 4, 0, "secure", sites.Now + 86_400_000),
            new SiteContainerRecord(otherSystemContainerId, 5_300_000_000_908,
                solarSystemId == int.MaxValue ? solarSystemId - 1 : solarSystemId + 1,
                "containment_facility", false, 17, 306, [1_000, 2_000, 3_000], 50, 4, 0, "secure", sites.Now + 86_400_000));
        var analyzer = (await rig.Ships.GrantAsync(character.CharacterId, 22_329, 1, singleton: true))!.Single().After;
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, analyzer.Id, Eve.Services.Space.Tests.Rig.Station, character.ShipId, 19, 1));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new CharacterSkill(21_718, 1, 0)]);
            await transaction.CommitAsync();
        }
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, analyzer.Id, true));
        var session = await rig.SessionAsync(character, 1);
        var shipBinding = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipBinding,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        Assert.Equal(solarSystemId, rig.Space.SolarSystemOf(session));
        Assert.NotNull(await rig.Space.SurfaceDistanceToItemAsync(session, containerId, CancellationToken.None));

        var hacks = sites.Hacks();
        var site = new SpaceHackingSite(rig.Space)
        {
            Find = (_, targetId, token) => hacks.FindAsync(targetId, token),
            Reserve = (owner, target, reservation, token) =>
                hacks.TryReserveAsync(target, reservation, owner.CharacterId ?? 0, owner.SessionId, token),
            Released = (reservation, token) => hacks.ReleaseReservationAsync(reservation, token),
            Settle = (owner, target, reservation, won, token) =>
                hacks.EndedAsync(target, reservation, won, owner.CharacterId ?? 0, token),
        };
        var attempts = new HackingAttempts();
        rig.Space.HackingService = new HackingMgrService(attempts, site);

        var refusal = await rig.Space.ActivateAsync(session, analyzer.Id, SpaceSessions.AnalyzerEffectName,
            containerId, 1, CancellationToken.None);

        Assert.Equal("TARGET_NOT_LOCKED", refusal?.Reason);
        Assert.Null(attempts.Get(character.CharacterId));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimActivate>(), message => message.Effect.TargetId == containerId);
        Assert.Equal(0, await ActiveReservationsAsync(sites, containerId));
        Assert.Equal(0, (await sites.Store.GetAsync(containerId))!.Failures);
        Assert.DoesNotContain(Events.Of(session), note => note.Name == "OnHackingTurnComplete");

        Target(rig, session, "add", otherSystemContainerId);
        var otherSystemRefusal = await rig.Space.ActivateAsync(session, analyzer.Id, SpaceSessions.AnalyzerEffectName,
            otherSystemContainerId, 1, CancellationToken.None);
        Assert.Equal("TARGET_NOT_FOUND", otherSystemRefusal?.Reason);
        Assert.Equal(0, await ActiveReservationsAsync(sites, otherSystemContainerId));
        Assert.Equal(0, (await sites.Store.GetAsync(otherSystemContainerId))!.Failures);

        Target(rig, session, "add", containerId);
        rig.Sim.PositionAnswers[containerId] = [1_000_000_000, 0, 0];
        var rangeRefusal = await rig.Space.ActivateAsync(session, analyzer.Id, SpaceSessions.AnalyzerEffectName,
            containerId, 1, CancellationToken.None);
        Assert.Equal("TARGET_OUT_OF_RANGE", rangeRefusal?.Reason);
        Assert.Equal(0, await ActiveReservationsAsync(sites, containerId));
        Assert.Equal(0, (await sites.Store.GetAsync(containerId))!.Failures);

        var flown = await rig.Ships.LoadAsync(character.CharacterId);
        var analyzerRange = flown!.World.Get(analyzer.Id, 54);
        var shipRadius = flown.World.Get(character.ShipId, 162);
        rig.Sim.PositionRadiusAnswers[character.ShipId] = shipRadius;
        rig.Sim.PositionRadiusAnswers[containerId] = 50;
        rig.Sim.PositionAnswers[containerId] = [shipRadius + 50 + analyzerRange + 1, 0, 0];
        var accepted = await rig.Space.ActivateAsync(session, analyzer.Id, SpaceSessions.AnalyzerEffectName,
            containerId, 1, CancellationToken.None);
        Assert.Null(accepted?.Reason);
        Assert.NotNull(attempts.Get(character.CharacterId));
        Assert.Equal(1, await ActiveReservationsAsync(sites, containerId));
        Assert.Contains(Events.Of(session), note => note.Name == "OnHackingTurnComplete");

        await rig.Space.HackingService!.CloseOnWarpAsync(session, character.CharacterId, CancellationToken.None);
        Assert.Null(attempts.Get(character.CharacterId));
        Assert.Equal(0, await ActiveReservationsAsync(sites, containerId));
        Assert.Equal(1, (await sites.Store.GetAsync(containerId))!.Failures);
    }

    [PgFact]
    public async Task Moving_out_of_analyzerrange_ends_an_idle_attempt_and_releases_its_database_reservation()
    {
        await using var rig = await Eve.Services.Space.Tests.Rig.CreateAsync();
        await using var sites = await SiteContainerRig.CreateAsync();
        const long containerId = 7_000_000_000_000_910L;
        var character = await rig.NewCharacterAsync(shipTypeId: 28_352);
        var solarSystemId = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;
        await sites.InsertAsync(new SiteContainerRecord(containerId, 5_300_000_000_910,
            solarSystemId, "containment_facility", false, 17, 306, [0, 0, 0], 50, 4, 0,
            "secure", sites.Now + 86_400_000));
        var analyzer = (await rig.Ships.GrantAsync(character.CharacterId, 22_329, 1, singleton: true))!.Single().After;
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, analyzer.Id, Eve.Services.Space.Tests.Rig.Station, character.ShipId, 19, 1));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new CharacterSkill(21_718, 1, 0)]);
            await transaction.CommitAsync();
        }
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, analyzer.Id, true));
        var session = await rig.SessionAsync(character, 1);
        var shipBinding = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipBinding,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);

        var hacks = sites.Hacks();
        var site = new SpaceHackingSite(rig.Space)
        {
            Find = (_, targetId, token) => hacks.FindAsync(targetId, token),
            Reserve = (owner, target, reservation, token) =>
                hacks.TryReserveAsync(target, reservation, owner.CharacterId ?? 0, owner.SessionId, token),
            Released = (reservation, token) => hacks.ReleaseReservationAsync(reservation, token),
            Settle = (owner, target, reservation, won, token) =>
                hacks.EndedAsync(target, reservation, won, owner.CharacterId ?? 0, token),
        };
        var attempts = new HackingAttempts();
        rig.Space.HackingService = new HackingMgrService(attempts, site);
        Target(rig, session, "add", containerId);

        Assert.Null((await rig.Space.ActivateAsync(session, analyzer.Id, SpaceSessions.AnalyzerEffectName,
            containerId, 1, CancellationToken.None))?.Reason);
        Assert.NotNull(attempts.Get(character.CharacterId));
        Assert.Equal(1, await ActiveReservationsAsync(sites, containerId));

        rig.Sim.PositionAnswers[containerId] = [1_000_000_000, 0, 0];
        var stop = Py.Tuple(new PyInt(1), Py.Tuple(new PyStr("Stop"), Py.Tuple(new PyLong(character.ShipId))));
        rig.Sim.Say(new SimNotify((ulong)session.SessionId, "DoDestinyUpdate", "clientID",
            MarshalCodec.Encode(Py.Tuple(new PyList([stop]), PyBool.False))));
        await rig.Space.QuiesceAsync();

        Assert.Null(attempts.Get(character.CharacterId));
        Assert.Equal(0, await ActiveReservationsAsync(sites, containerId));
        Assert.Equal(1, (await sites.Store.GetAsync(containerId))!.Failures);
        var notifications = Events.Of(session);
        var movementIndex = notifications.ToList().FindIndex(note => note.Name == "DoDestinyUpdate");
        var lostIndex = notifications.ToList().FindLastIndex(note => note.Name == "OnHackingTurnComplete");
        Assert.True(movementIndex >= 0 && lostIndex > movementIndex,
            "the movement update must reach the client before the terminal hacking turn");
        var lostTurn = notifications[lostIndex];
        var lostEvents = Assert.IsType<PyTuple>(lostTurn.Args[0]).Items;
        Assert.Equal(HackingWire.EventGameLost, Number(Field(Assert.Single(lostEvents), "eventID")));
    }

    [PgFact]
    public async Task Losing_the_target_lock_while_simulation_accepts_activation_releases_the_pending_reservation()
    {
        await using var rig = await Eve.Services.Space.Tests.Rig.CreateAsync();
        await using var sites = await SiteContainerRig.CreateAsync();
        const long containerId = 7_000_000_000_000_911L;
        var character = await rig.NewCharacterAsync(shipTypeId: 28_352);
        var solarSystemId = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;
        await sites.InsertAsync(new SiteContainerRecord(containerId, 5_300_000_000_911,
            solarSystemId, "containment_facility", false, 17, 306, [0, 0, 0], 50, 4, 0,
            "secure", sites.Now + 86_400_000));
        var analyzer = (await rig.Ships.GrantAsync(character.CharacterId, 22_329, 1, singleton: true))!.Single().After;
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(
            character.CharacterId, analyzer.Id, Eve.Services.Space.Tests.Rig.Station, character.ShipId, 19, 1));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new CharacterSkill(21_718, 1, 0)]);
            await transaction.CommitAsync();
        }
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, analyzer.Id, true));
        var session = await rig.SessionAsync(character, 1);
        var shipBinding = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipBinding,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);

        var hacks = sites.Hacks();
        var site = new SpaceHackingSite(rig.Space)
        {
            Find = (_, targetId, token) => hacks.FindAsync(targetId, token),
            Reserve = (owner, target, reservation, token) =>
                hacks.TryReserveAsync(target, reservation, owner.CharacterId ?? 0, owner.SessionId, token),
            Released = (reservation, token) => hacks.ReleaseReservationAsync(reservation, token),
            Settle = (owner, target, reservation, won, token) =>
                hacks.EndedAsync(target, reservation, won, owner.CharacterId ?? 0, token),
        };
        var attempts = new HackingAttempts();
        rig.Space.HackingService = new HackingMgrService(attempts, site);
        Target(rig, session, "add", containerId);
        var activationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Sim.ActivateGate = activationGate;

        var activation = rig.Space.ActivateAsync(session, analyzer.Id, SpaceSessions.AnalyzerEffectName,
            containerId, 1, CancellationToken.None);
        await WaitUntilAsync(() => rig.Sim.Heard.OfType<SimActivate>().Any(message => message.Effect.TargetId == containerId));
        Assert.Equal(1, await ActiveReservationsAsync(sites, containerId));

        Target(rig, session, "lost", containerId);
        activationGate.SetResult();
        Assert.Null((await activation)?.Reason);

        Assert.Null(attempts.Get(character.CharacterId));
        Assert.Equal(0, await ActiveReservationsAsync(sites, containerId));
        Assert.Equal(0, (await sites.Store.GetAsync(containerId))!.Failures);
        Assert.DoesNotContain(Events.Of(session), note => note.Name == "OnHackingTurnComplete");
    }

    private static void Target(Rig rig, ClientSession session, string action, long? targetId = null)
    {
        var values = targetId is { } id
            ? Py.Tuple(new PyStr(action), new PyLong(id))
            : Py.Tuple(new PyStr(action));
        var args = Py.Tuple(new PyInt(0), Py.Tuple(new PyInt(1), values));
        rig.Sim.Say(new SimNotify((ulong)session.SessionId, "OnTarget", "shipid", MarshalCodec.Encode(args)));
    }

    private static async Task<long> ActiveReservationsAsync(SiteContainerRig rig, long containerId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {rig.SitesSchema}.site_container_hack_attempts WHERE container_id = $1 AND status = 'active'",
            connection);
        command.Parameters.AddWithValue(containerId);
        return (long)(await command.ExecuteScalarAsync())!;
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
