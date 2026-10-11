using Eve.Hacking;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

/// <summary>
/// The attempts of the hacking minigame, one per character. Held in memory: an attempt does not survive a restart,
/// as the window closing does not (EveJS keeps its attempts in memory too).
/// </summary>
public sealed class HackingAttempts
{
    private readonly Dictionary<long, HackingGame> _games = new();
    private readonly object _gate = new();

    public HackingGame? Get(long characterId)
    {
        lock (_gate) return _games.GetValueOrDefault(characterId);
    }

    public void Set(long characterId, HackingGame game)
    {
        lock (_gate) _games[characterId] = game;
    }

    public void Remove(long characterId)
    {
        lock (_gate) _games.Remove(characterId);
    }
}

/// <summary>
/// The client's <c>hackingMgr</c> service. Each call is one step of the attempt the character has open; the
/// events of the step go to the client as <c>OnHackingTurnComplete</c> (EveJS <c>hackingMgrService.js</c>).
/// An attempt is opened by the analyzer activation (<see cref="Start"/>).
/// </summary>
public sealed class HackingMgrService(HackingAttempts attempts) : IService
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "ClickedOnTile", "UsedUtilityElement", "UsedUtilityElementOnVirus", "QuitHackingAttempt",
    };

    public string Name => "hackingMgr";

    public bool HasMethod(string method) => Methods.Contains(method);

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method) || call.Session.CharacterId is not { } characterId || characterId <= 0)
            return ValueTask.FromResult<PyValue>(PyNone.Instance);

        var session = call.Session;
        var game = attempts.Get(characterId);
        IReadOnlyList<HackingEvent> events;
        if (game is null)
        {
            events = [new HackingEvent(HackingWire.EventAck, new Dictionary<string, object?>())];
        }
        else
        {
            events = call.Method switch
            {
                "ClickedOnTile" when ReadCoord(call.Arg(0)) is { } tile => game.ClickTile(tile),
                "UsedUtilityElement" when ReadInt(call.Arg(0)) is { } index && ReadCoord(call.Arg(1)) is { } target => game.UseOnTarget(index, target),
                "UsedUtilityElementOnVirus" when ReadInt(call.Arg(0)) is { } index => game.UseOnVirus(index),
                "QuitHackingAttempt" => game.Quit(),
                _ => [new HackingEvent(HackingWire.EventAck, new Dictionary<string, object?>())],
            };
            if (game.Outcome != HackingOutcome.Active)
                attempts.Remove(characterId);
        }
        Send(session, events);
        return ValueTask.FromResult<PyValue>(PyNone.Instance);
    }

    /// <summary>Opens an attempt for the character and sends its start events. A character's earlier attempt is replaced.</summary>
    public void Start(ClientSession session, long characterId, HackingAttempt attempt)
    {
        var game = HackingGame.Create(attempt);
        attempts.Set(characterId, game);
        Send(session, game.Start());
    }

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
