using Eve.Hacking;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;

namespace Eve.Services.Space;

/// <summary>An open attempt: its rules, the container it works on and the reach the window keeps (0 when none).</summary>
public sealed record HackingWindow(HackingGame Game, HackableTarget Target, double MaxRange, ClientSession? Session = null, Guid ReservationId = default)
{
    public IReadOnlyList<HackingEvent>? PendingTerminalEvents { get; set; }
    public Action? ClosedHandler { get; set; }
    public bool QaPractice { get; init; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed record HackingReservation(ClientSession Session, long CharacterId, HackableTarget Target, Guid ReservationId, Action ClosedHandler);

public sealed record AnalyzerValues(
    int ModuleTypeId,
    bool CanHackData,
    bool CanHackRelic,
    double MaxRange,
    double VirusCoherence,
    double VirusStrength,
    double VirusSlots,
    double AccessDifficultyBonus);

/// <summary>
/// </summary>
public sealed class HackingAttempts
{
    private readonly Dictionary<long, HackingWindow> _windows = new();
    private readonly Dictionary<(ClientSession Session, long TargetId), HackingReservation> _reservations = new();
    private readonly object _gate = new();

    public HackingWindow? Get(long characterId)
    {
        lock (_gate) return _windows.GetValueOrDefault(characterId);
    }

    public void Set(long characterId, HackingWindow window)
    {
        lock (_gate)
        {
            if (_windows.TryGetValue(characterId, out var previous) && previous.ClosedHandler is { } previousHandler && previous.Session is { } previousSession)
                previousSession.Closed -= previousHandler;
            _windows[characterId] = window;
        }
    }

    public void Remove(long characterId, HackingWindow window)
    {
        lock (_gate)
        {
            if (_windows.TryGetValue(characterId, out var current) && ReferenceEquals(current, window))
            {
                _windows.Remove(characterId);
                if (window.ClosedHandler is { } handler && window.Session is { } session)
                    session.Closed -= handler;
            }
        }
    }

    public bool IsBeingHacked(long targetId)
    {
        lock (_gate) return _windows.Values.Any(window => window.Target.ItemId == targetId)
            || _reservations.Values.Any(reservation => reservation.Target.ItemId == targetId);
    }

    public bool TryAddReservation(HackingReservation reservation)
    {
        if (reservation.Session.IsClosed)
            return false;
        var key = (reservation.Session, reservation.Target.ItemId);
        lock (_gate)
        {
            if (_windows.Values.Any(window => window.Target.ItemId == reservation.Target.ItemId)
                || _reservations.Values.Any(current =>
                    current.Target.ItemId == reservation.Target.ItemId || current.CharacterId == reservation.CharacterId))
                return false;
            _reservations.Add(key, reservation);
        }
        reservation.Session.Closed += reservation.ClosedHandler;
        if (reservation.Session.IsClosed)
        {
            RemoveReservation(reservation.Session, reservation.Target.ItemId, reservation.ReservationId);
            return false;
        }
        return true;
    }

    public bool TryAddPracticeWindow(long characterId, HackingWindow window, Action closedHandler)
    {
        if (window.Session is not { IsClosed: false } session)
            return false;
        lock (_gate)
        {
            if (_windows.ContainsKey(characterId) || _reservations.Values.Any(reservation => reservation.CharacterId == characterId))
                return false;
            window.ClosedHandler = closedHandler;
            _windows.Add(characterId, window);
        }
        session.Closed += closedHandler;
        if (session.IsClosed)
        {
            Remove(characterId, window);
            return false;
        }
        return true;
    }

    public HackingReservation? Reservation(ClientSession session, long targetId)
    {
        lock (_gate) return _reservations.GetValueOrDefault((session, targetId));
    }

    public bool RemoveReservation(ClientSession session, long targetId, Guid reservationId)
    {
        HackingReservation? removed = null;
        lock (_gate)
        {
            var key = (session, targetId);
            if (_reservations.TryGetValue(key, out var current) && current.ReservationId == reservationId)
            {
                _reservations.Remove(key);
                removed = current;
            }
        }
        if (removed is not null)
        {
            removed.Session.Closed -= removed.ClosedHandler;
            return true;
        }
        return false;
    }

    public bool TransferReservationToWindow(long characterId, HackingReservation reservation, HackingWindow window, Action closedHandler)
    {
        var key = (reservation.Session, reservation.Target.ItemId);
        lock (_gate)
        {
            if (!_reservations.TryGetValue(key, out var current) || current.ReservationId != reservation.ReservationId)
                return false;
            _reservations.Remove(key);
            _windows[characterId] = window;
            window.ClosedHandler = closedHandler;
        }
        reservation.Session.Closed -= reservation.ClosedHandler;
        reservation.Session.Closed += closedHandler;
        return !reservation.Session.IsClosed;
    }
}

/// <summary>
/// window closes on a warp, out of the analyzer's reach, or when the attempt is lost or won; each end reports to <see cref="IHackingSite"/>.
/// </summary>
public sealed class HackingMgrService : IService
{
    private readonly HackingAttempts attempts;
    private readonly IHackingSite site;
    private readonly Func<HackingAttempt, HackingGame> newGame;
    private readonly Action<string>? log;
    private readonly LocalTimers? localTimers;

    private static readonly TimeSpan ClosedSessionSettlementRetryDelay = TimeSpan.FromSeconds(1);

    /// <param name="newGame">The game of an attempt; the board is generated unless a test supplies one.</param>
    public HackingMgrService(
        HackingAttempts attempts,
        IHackingSite site,
        Func<HackingAttempt, HackingGame>? newGame = null,
        Action<string>? log = null,
        LocalTimers? localTimers = null)
    {
        this.attempts = attempts;
        this.site = site;
        this.newGame = newGame ?? HackingGame.Create;
        this.log = log;
        this.localTimers = localTimers;
    }

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "ClickedOnTile", "UsedUtilityElement", "UsedUtilityElementOnVirus", "QuitHackingAttempt", "StartNewGameInstance",
    };

    public string Name => "hackingMgr";

    public bool HasMethod(string method) => Methods.Contains(method);

    public bool HasOpenAttempt(ClientSession session, long characterId) =>
        attempts.Get(characterId) is { } window && ReferenceEquals(window.Session, session);

    private async ValueTask<PyValue> StartNewGameInstanceAsync(ServiceCall call, long characterId)
    {
        const string qaOnly = "Activate an analyzer to hack a container. Standalone practice boards require the QA role.";
        if (!HasQaRole(call.Session) || call.Args.Count != 7 || call.Arg(0) is not PyNone)
        {
            throw MachoErrors.ThrowUserError(qaOnly);
        }
        if (!TryInt32(call.Arg(1), out var gameType) || !TryInt32(call.Arg(2), out var moduleTypeId)
            || !TryInt32(call.Arg(3), out var difficulty) || !TryInt32(call.Arg(4), out var coherence)
            || !TryInt32(call.Arg(5), out var strength) || !TryInt32(call.Arg(6), out var slots)
            || moduleTypeId <= 0 || slots < 0)
        {
            throw MachoErrors.ThrowUserError("HACKING_PRACTICE_ARGUMENTS_INVALID");
        }

        var session = call.Session;
        if (attempts.Get(characterId) is { } previous)
        {
            await LoseAsync(previous.Session ?? session, characterId, previous, call.Cancellation, send: previous.Session?.IsClosed != true);
            if (ReferenceEquals(attempts.Get(characterId), previous))
                throw MachoErrors.ThrowUserError("ANALYZER_TARGET_BUSY");
        }

        var tier = Math.Clamp(difficulty, 0, 5);
        var attempt = new HackingAttempt(null, moduleTypeId, gameType, tier, coherence, strength, slots, Random.Shared.Next());
        var target = new HackableTarget(0, 0, tier, Relic: false);
        var window = new HackingWindow(newGame(attempt), target, 0, session) { QaPractice = true };
        Action closedHandler = () => _ = EndAfterSessionCloseAsync(session, characterId, window);
        if (!attempts.TryAddPracticeWindow(characterId, window, closedHandler))
            throw MachoErrors.ThrowUserError("ANALYZER_TARGET_BUSY");
        Send(session, window.Game.Start());
        return PyNone.Instance;
    }

    private static bool TryInt32(PyValue value, out int result)
    {
        var number = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => long.MinValue,
        };
        result = number is >= int.MinValue and <= int.MaxValue ? (int)number : 0;
        return number is >= int.MinValue and <= int.MaxValue;
    }

    private static bool HasQaRole(ClientSession session)
    {
        var role = session.GetInt("role") ?? session.GetInt("accountRole") ?? 0;
        return (role & GameMasterRoles.Qa) != 0;
    }

    /// <summary>Ends an open attempt after a movement or target-lock update makes its authoritative start conditions false.</summary>
    public async ValueTask RecheckReachAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        if (attempts.Get(characterId) is not { } window || !ReferenceEquals(window.Session, session) || window.MaxRange <= 0)
            return;

        await window.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(attempts.Get(characterId), window))
                return;
            try
            {
                if (window.PendingTerminalEvents is not null)
                {
                    await EndIfOverAsync(session, characterId, window, [Ack()], send: true, cancellationToken);
                    return;
                }
                if (await site.AnalyzerStartRefusalAsync(session, window.Target, window.MaxRange, cancellationToken) is null)
                    return;

                var terminal = window.Game.Quit();
                await EndIfOverAsync(session, characterId, window, terminal, send: true, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log?.Invoke($"the hacking attempt for container {window.Target.ItemId} could not settle after losing reach: {error.Message}");
                localTimers?.Set(SettlementRetryKey(window.ReservationId), ClosedSessionSettlementRetryDelay,
                    () => RecheckReachAsync(session, characterId, CancellationToken.None).AsTask());
            }
        }
        finally
        {
            window.Gate.Release();
        }
    }

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method) || call.Session.CharacterId is not { } characterId || characterId <= 0)
            return PyNone.Instance;

        var session = call.Session;
        if (call.Method == "StartNewGameInstance")
            return await StartNewGameInstanceAsync(call, characterId);

        if (attempts.Get(characterId) is not { } window)
        {
            // EVE.js Handle_QuitHackingAttempt returns null with no open attempt and sends nothing; a tile click answers with the ACK turn
            if (call.Method != "QuitHackingAttempt")
                Send(session, [Ack()]);
            return PyNone.Instance;
        }

        if (window.Session is not null && !ReferenceEquals(window.Session, session))
        {
            if (call.Method != "QuitHackingAttempt")
                Send(session, [Ack()]);
            return PyNone.Instance;
        }

        await window.Gate.WaitAsync(call.Cancellation);
        try
        {
            if (!ReferenceEquals(attempts.Get(characterId), window)
                || window.Session is not null && !ReferenceEquals(window.Session, session))
            {
                if (call.Method != "QuitHackingAttempt")
                    Send(session, [Ack()]);
                return PyNone.Instance;
            }
            if (window.PendingTerminalEvents is not null)
            {
                await EndIfOverAsync(session, characterId, window, [Ack()], send: true, call.Cancellation);
                return PyNone.Instance;
            }

            IReadOnlyList<HackingEvent> events;
            if (window.MaxRange > 0 && !await site.InReachAsync(session, window.Target.ItemId, window.MaxRange, call.Cancellation))
            {
                events = window.Game.Quit();
            }
            else
            {
                events = call.Method switch
                {
                    "ClickedOnTile" when ReadCoord(call.Arg(0)) is { } tile => window.Game.ClickTile(tile),
                    "UsedUtilityElement" when ReadInt(call.Arg(0)) is { } index && ReadCoord(call.Arg(1)) is { } target => window.Game.UseOnTarget(index, target),
                    "UsedUtilityElementOnVirus" when ReadInt(call.Arg(0)) is { } index => window.Game.UseOnVirus(index),
                    "QuitHackingAttempt" => window.Game.Quit(),
                    _ => [Ack()],
                };
            }
            if (window.Game.Outcome == HackingOutcome.Active)
                Send(session, events);
            else
                await EndIfOverAsync(session, characterId, window, events, send: true, call.Cancellation);
            return PyNone.Instance;
        }
        finally
        {
            window.Gate.Release();
        }
    }

    /// <summary>Whether a fitted analyzer may start on the container now: the reason it may not, or null.</summary>
    public async ValueTask<string?> AnalyzerRefusalAsync(ClientSession session, long characterId, long targetId, AnalyzerValues values, CancellationToken cancellationToken)
    {
        if (await site.FindAsync(characterId, targetId, cancellationToken) is not { } target)
            return "ANALYZER_TARGET_INVALID";
        if (target.Relic ? !values.CanHackRelic : !values.CanHackData)
            return "ANALYZER_TYPE_MISMATCH";
        if (await site.AnalyzerStartRefusalAsync(session, target, values.MaxRange, cancellationToken) is { } startRefusal)
            return startRefusal;

        if (attempts.Get(characterId) is { } previous && !ReferenceEquals(previous.Session, session))
        {
            await LoseAsync(previous.Session ?? session, characterId, previous, cancellationToken, send: previous.Session?.IsClosed != true);
            if (ReferenceEquals(attempts.Get(characterId), previous))
                return "ANALYZER_TARGET_BUSY";
            if (target.ItemId == previous.Target.ItemId)
            {
                if (await site.FindAsync(characterId, targetId, cancellationToken) is not { } current)
                    return "ANALYZER_TARGET_INVALID";
                target = current;
                if (target.Relic ? !values.CanHackRelic : !values.CanHackData)
                    return "ANALYZER_TYPE_MISMATCH";
            }
        }

        if (attempts.IsBeingHacked(target.ItemId))
            return "ANALYZER_TARGET_BUSY";
        var reservationId = Guid.NewGuid();
        if (!await site.TryReserveAsync(session, target, reservationId, cancellationToken))
            return "ANALYZER_TARGET_BUSY";
        Action closedHandler = () => _ = CancelPendingAfterCloseAsync(session, target.ItemId, reservationId);
        if (!attempts.TryAddReservation(new HackingReservation(session, characterId, target, reservationId, closedHandler)))
        {
            await site.ReleaseReservationAsync(reservationId, CancellationToken.None);
            return "ANALYZER_TARGET_BUSY";
        }
        return null;
    }

    public async ValueTask CancelAnalyzerReservationAsync(ClientSession session, long targetId, CancellationToken cancellationToken)
    {
        var reservation = attempts.Reservation(session, targetId);
        if (reservation is null)
            return;
        await site.ReleaseReservationAsync(reservation.ReservationId, cancellationToken);
        attempts.RemoveReservation(session, targetId, reservation.ReservationId);
    }

    /// <summary>Opens the analyzer's attempt on the container and sends its start events. A character's earlier attempt ends as lost first.</summary>
    public async ValueTask OpenAnalyzerAttemptAsync(ClientSession session, long characterId, long targetId, AnalyzerValues values, CancellationToken cancellationToken)
    {
        if (attempts.Reservation(session, targetId) is null)
        {
            if (await AnalyzerRefusalAsync(session, characterId, targetId, values, cancellationToken) is not null)
                return;
        }
        if (session.IsClosed)
        {
            await CancelAnalyzerReservationAsync(session, targetId, CancellationToken.None);
            return;
        }
        var reservation = attempts.Reservation(session, targetId);
        if (reservation is null)
            return;
        if (await site.FindAsync(characterId, targetId, cancellationToken) is not { } target)
        {
            await CancelAnalyzerReservationAsync(session, targetId, CancellationToken.None);
            return;
        }
        if (reservation.CharacterId != characterId || reservation.Target.ItemId != target.ItemId
            || await site.AnalyzerStartRefusalAsync(session, target, values.MaxRange, cancellationToken) is not null)
        {
            await CancelAnalyzerReservationAsync(session, targetId, CancellationToken.None);
            return;
        }
        if (attempts.Get(characterId) is { } previous)
        {
            await LoseAsync(previous.Session ?? session, characterId, previous, cancellationToken, send: previous.Session?.IsClosed != true);
            if (ReferenceEquals(attempts.Get(characterId), previous))
            {
                await CancelAnalyzerReservationAsync(session, targetId, CancellationToken.None);
                return;
            }
        }

        var attempt = new HackingAttempt(
            target.ItemId,
            values.ModuleTypeId,
            target.Relic ? HackingWire.GameTypeArchaeology : HackingWire.GameTypeHacking,
            EffectiveDifficulty(target.Difficulty, values.AccessDifficultyBonus),
            (int)Math.Round(values.VirusCoherence),
            (int)Math.Round(values.VirusStrength),
            (int)Math.Round(values.VirusSlots),
            Random.Shared.Next());
        var window = new HackingWindow(newGame(attempt), target, values.MaxRange, session, reservation.ReservationId);
        Action closedHandler = () => _ = EndAfterSessionCloseAsync(session, characterId, window);
        if (!attempts.TransferReservationToWindow(characterId, reservation, window, closedHandler))
        {
            await EndAfterSessionCloseAsync(session, characterId, window);
            return;
        }
        if (session.IsClosed)
        {
            await EndAfterSessionCloseAsync(session, characterId, window);
            return;
        }
        Send(session, window.Game.Start());
    }

    private static int EffectiveDifficulty(int difficulty, double accessDifficultyBonus)
    {
        var adjusted = difficulty - accessDifficultyBonus / 5;
        return double.IsFinite(adjusted) ? (int)Math.Clamp(Math.Floor(adjusted), 0d, 5d) : 0;
    }

    /// <summary>The window closes at a warp: the attempt is lost.</summary>
    public ValueTask CloseOnWarpAsync(ClientSession session, long characterId, CancellationToken cancellationToken) =>
        attempts.Get(characterId) is { QaPractice: false } window && ReferenceEquals(window.Session, session)
            ? LoseAsync(session, characterId, window, cancellationToken, send: true)
            : ValueTask.CompletedTask;

    private async ValueTask LoseAsync(ClientSession session, long characterId, HackingWindow window, CancellationToken cancellationToken, bool send)
    {
        await window.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(attempts.Get(characterId), window))
                return;
            var events = window.PendingTerminalEvents ?? window.Game.Quit();
            await EndIfOverAsync(session, characterId, window, events, send, cancellationToken);
        }
        finally
        {
            window.Gate.Release();
        }
    }

    private async ValueTask EndIfOverAsync(
        ClientSession session, long characterId, HackingWindow window, IReadOnlyList<HackingEvent> events, bool send, CancellationToken cancellationToken)
    {
        if (window.Game.Outcome == HackingOutcome.Active && window.PendingTerminalEvents is null)
        {
            if (send && !session.IsClosed)
                Send(session, events);
            return;
        }
        window.PendingTerminalEvents ??= events;
        if (window.QaPractice)
        {
            attempts.Remove(characterId, window);
            localTimers?.Cancel(SettlementRetryKey(window.ReservationId));
            if (send && !session.IsClosed)
                Send(session, window.PendingTerminalEvents);
            return;
        }
        var contents = await site.AttemptEndedAsync(
            session, window.Target, window.ReservationId, window.Game.Outcome == HackingOutcome.Won, cancellationToken);
        var terminalEvents = window.Game.Outcome == HackingOutcome.Won
            ? WithCoreContents(window.PendingTerminalEvents, contents)
            : window.PendingTerminalEvents;
        attempts.Remove(characterId, window);
        localTimers?.Cancel(SettlementRetryKey(window.ReservationId));
        if (send && !session.IsClosed)
            Send(session, terminalEvents);
    }

    private async Task CancelPendingAfterCloseAsync(ClientSession session, long targetId, Guid reservationId)
    {
        try
        {
            if (attempts.Reservation(session, targetId) is { } reservation && reservation.ReservationId == reservationId)
                await CancelAnalyzerReservationAsync(session, targetId, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.Invoke($"the pending hacking reservation for container {targetId} could not be released: {error.Message}");
        }
    }

    private async Task EndAfterSessionCloseAsync(ClientSession session, long characterId, HackingWindow window)
    {
        try
        {
            await LoseAsync(session, characterId, window, CancellationToken.None, send: false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.Invoke($"the hacking attempt for container {window.Target.ItemId} could not settle after session close: {error.Message}");
            localTimers?.Set(SettlementRetryKey(window.ReservationId), ClosedSessionSettlementRetryDelay,
                () => EndAfterSessionCloseAsync(session, characterId, window));
        }
    }

    private static string SettlementRetryKey(Guid reservationId) => $"hacking.closed-settlement:{reservationId:N}";

    private static IReadOnlyList<HackingEvent> WithCoreContents(IReadOnlyList<HackingEvent> events, IReadOnlyList<HackingLootItem> contents)
    {
        var terminalIndex = -1;
        Coord? coreCoord = null;
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            if (item.EventId == HackingWire.EventGameWon)
                terminalIndex = index;
            if (item.EventId == HackingWire.EventObjectKilled
                && item.Data.TryGetValue("type", out var type) && type is int typeId && typeId == HackingWire.TypeCore
                && item.Data.TryGetValue("coord", out var coord) && coord is Coord coordinate)
                coreCoord = coordinate;
        }
        if (terminalIndex < 0 || coreCoord is not { } core)
            throw new InvalidOperationException("a winning hacking turn has no destroyed core coordinate");

        var coreContents = new HackingEvent(HackingWire.EventCoreContents, new Dictionary<string, object?>
        {
            ["coord"] = core,
            ["contentsList"] = contents.Select(item => (object?)(item.TypeId, item.Quantity)).ToList(),
        });
        var result = events.ToList();
        result.Insert(terminalIndex, coreContents);
        return result;
    }

    private static HackingEvent Ack() => new(HackingWire.EventAck, new Dictionary<string, object?>());

    private static void Send(ClientSession session, IReadOnlyList<HackingEvent> events)
    {
        if (events.Count > 0)
            // EVE.js sends the turn as the array of events, which marshals as a tuple (the client reads it by iteration)
            session.SendNotification("OnHackingTurnComplete", "clientID", new PyTuple(events.Select(e => ToPy(e)).ToArray()));
    }

    /// <summary>The outer event is a util.KeyVal; the client expands event data and its records with Python <c>**kwargs</c>.</summary>
    public static PyValue ToPy(HackingEvent e) => Py.KeyVal(
        ("eventID", new PyInt(e.EventId)),
        ("eventData", ToPy(e.Data)));

    private static PyValue ToPy(IReadOnlyDictionary<string, object?> data) =>
        new PyDict(data.Select(kv => new KeyValuePair<PyValue, PyValue>(new PyStr(kv.Key), ToPy(kv.Value))).ToArray());

    private static PyValue ToPy(object? value) => value switch
    {
        null => PyNone.Instance,
        bool b => b ? PyBool.True : PyBool.False,
        int i => new PyInt(i),
        long l => new PyInt(l),
        string s => new PyStr(s),
        Coord c => Py.Tuple(new PyInt(c.X), new PyInt(c.Y)),
        ValueTuple<int, long> tuple => Py.Tuple(new PyInt(tuple.Item1), new PyInt(tuple.Item2)),
        IReadOnlyDictionary<string, object?> d => ToPy(d),
        IReadOnlyList<object?> l => new PyList(l.Select(v => ToPy(v)).ToArray()),
        _ => throw new InvalidOperationException($"hacking event value {value.GetType().Name} has no wire form"),
    };

    private static Coord? ReadCoord(PyValue value) =>
        value is PyTuple { Items: [var x, var y] } && ReadInt(x) is { } cx && ReadInt(y) is { } cy
            ? new Coord(cx, cy)
            : null;

    private static int? ReadInt(PyValue value) => value switch
    {
        PyInt i => (int)i.Value,
        _ => null,
    };
}
