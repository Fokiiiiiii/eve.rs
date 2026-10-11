using Eve.Hacking;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

/// <summary>An open attempt: its rules, the container it works on and the reach the window keeps (0 when none).</summary>
public sealed record HackingWindow(HackingGame Game, HackableTarget Target, double MaxRange);

/// <summary>The analyzer's values after dogma and what its type can hack (skills 21718 data, 13278 archaeology).</summary>
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
/// The attempts of the hacking minigame, one per character, in memory: a restart ends them (EveJS keeps its attempts in memory too).
/// </summary>
public sealed class HackingAttempts
{
    private readonly Dictionary<long, HackingWindow> _windows = new();
    private readonly object _gate = new();

    public HackingWindow? Get(long characterId)
    {
        lock (_gate) return _windows.GetValueOrDefault(characterId);
    }

    public void Set(long characterId, HackingWindow window)
    {
        lock (_gate) _windows[characterId] = window;
    }

    public void Remove(long characterId, HackingWindow window)
    {
        lock (_gate)
        {
            if (_windows.TryGetValue(characterId, out var current) && ReferenceEquals(current, window))
                _windows.Remove(characterId);
        }
    }

    public bool IsBeingHacked(long targetId)
    {
        lock (_gate) return _windows.Values.Any(window => window.Target.ItemId == targetId);
    }
}

/// <summary>
/// The client's <c>hackingMgr</c> service. Each call is one step of the attempt the character has open; the events of the step go to the
/// client as <c>OnHackingTurnComplete</c> (EveJS <c>hackingMgrService.js</c>). The analyzer opens an attempt on its activation, and the
/// window closes on a warp, out of the analyzer's reach, or when the attempt is lost or won; each end reports to <see cref="IHackingSite"/>.
/// </summary>
public sealed class HackingMgrService(HackingAttempts attempts, IHackingSite site) : IService
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "ClickedOnTile", "UsedUtilityElement", "UsedUtilityElementOnVirus", "QuitHackingAttempt",
    };

    public string Name => "hackingMgr";

    public bool HasMethod(string method) => Methods.Contains(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method) || call.Session.CharacterId is not { } characterId || characterId <= 0)
            return PyNone.Instance;

        var session = call.Session;
        if (attempts.Get(characterId) is not { } window)
        {
            Send(session, [Ack()]);
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
        Send(session, events);
        await EndIfOverAsync(session, characterId, window, call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>Whether a fitted analyzer may start on the container now: the reason it may not, or null.</summary>
    public async ValueTask<string?> AnalyzerRefusalAsync(long characterId, long targetId, AnalyzerValues values, CancellationToken cancellationToken)
    {
        if (await site.FindAsync(characterId, targetId, cancellationToken) is not { } target)
            return "ANALYZER_TARGET_INVALID";
        if (target.Relic ? !values.CanHackRelic : !values.CanHackData)
            return "ANALYZER_TYPE_MISMATCH";
        return attempts.IsBeingHacked(target.ItemId) ? "ANALYZER_TARGET_BUSY" : null;
    }

    /// <summary>Opens the analyzer's attempt on the container and sends its start events. A character's earlier attempt ends as lost first.</summary>
    public async ValueTask OpenAnalyzerAttemptAsync(ClientSession session, long characterId, long targetId, AnalyzerValues values, CancellationToken cancellationToken)
    {
        if (await site.FindAsync(characterId, targetId, cancellationToken) is not { } target)
            return;
        if (attempts.Get(characterId) is { } previous)
            await LoseAsync(session, characterId, previous, cancellationToken);

        var attempt = new HackingAttempt(
            target.ItemId,
            values.ModuleTypeId,
            target.Relic ? HackingWire.GameTypeArchaeology : HackingWire.GameTypeHacking,
            Math.Max(1, (int)Math.Floor(target.Difficulty - values.AccessDifficultyBonus / 5)),
            (int)Math.Round(values.VirusCoherence),
            (int)Math.Round(values.VirusStrength),
            (int)Math.Round(values.VirusSlots),
            Random.Shared.Next());
        var window = new HackingWindow(HackingGame.Create(attempt), target, values.MaxRange);
        attempts.Set(characterId, window);
        Send(session, window.Game.Start());
    }

    /// <summary>The window closes at a warp: the attempt is lost.</summary>
    public ValueTask CloseOnWarpAsync(ClientSession session, long characterId, CancellationToken cancellationToken) =>
        attempts.Get(characterId) is { } window ? LoseAsync(session, characterId, window, cancellationToken) : ValueTask.CompletedTask;

    private async ValueTask LoseAsync(ClientSession session, long characterId, HackingWindow window, CancellationToken cancellationToken)
    {
        Send(session, window.Game.Quit());
        await EndIfOverAsync(session, characterId, window, cancellationToken);
    }

    private async ValueTask EndIfOverAsync(ClientSession session, long characterId, HackingWindow window, CancellationToken cancellationToken)
    {
        if (window.Game.Outcome == HackingOutcome.Active)
            return;
        attempts.Remove(characterId, window);
        await site.AttemptEndedAsync(session, window.Target, window.Game.Outcome == HackingOutcome.Won, cancellationToken);
    }

    private static HackingEvent Ack() => new(HackingWire.EventAck, new Dictionary<string, object?>());

    private static void Send(ClientSession session, IReadOnlyList<HackingEvent> events)
    {
        if (events.Count > 0)
            session.SendNotification("OnHackingTurnComplete", "clientID", new PyList(events.Select(e => ToPy(e)).ToArray()));
    }

    /// <summary>A util.KeyVal per event and per record, as the client reads them (<c>event['eventID']</c>, <c>TileData(**eventData)</c>).</summary>
    public static PyValue ToPy(HackingEvent e) => Py.KeyVal(
        ("eventID", new PyInt(e.EventId)),
        ("eventData", ToPy(e.Data)));

    private static PyValue ToPy(IReadOnlyDictionary<string, object?> data) =>
        Py.KeyVal(data.Select(kv => (kv.Key, ToPy(kv.Value))).ToArray());

    private static PyValue ToPy(object? value) => value switch
    {
        null => PyNone.Instance,
        bool b => b ? PyBool.True : PyBool.False,
        int i => new PyInt(i),
        long l => new PyInt(l),
        string s => new PyStr(s),
        Coord c => Py.Tuple(new PyInt(c.X), new PyInt(c.Y)),
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
