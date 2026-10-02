using Eve.Crimewatch;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Crimewatch.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)"; }
}

internal sealed class Capture : IPacketSink
{
    public List<PyValue> Sent { get; } = [];

    public void Send(PyValue packet) => Sent.Add(packet);
}

/// <summary>The events the server sent a session, read back: the name, how they are addressed, and what they carry.</summary>
internal static class Notes
{
    public sealed record Note(string Name, string IdType, PyValue[] Args);

    public static IReadOnlyList<Note> Of(Capture sink)
    {
        var notes = new List<Note>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
            {
                continue;
            }
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var args = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items;
            notes.Add(new Note(JsSemantics.StringValue(address[1])!, JsSemantics.StringValue(address[3])!, [.. args]));
        }
        sink.Sent.Clear();
        return notes;
    }
}

/// <summary>A wait the test finishes by hand.</summary>
internal sealed class Waits
{
    public List<(TimeSpan After, TaskCompletionSource Done)> Made { get; } = [];

    public Task Delay(TimeSpan after, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => done.TrySetCanceled(token));
        Made.Add((after, done));
        return done.Task;
    }
}

public class RuntimeTests : IAsyncLifetime
{
    private const long Start = 1_700_000_000_000;
    private const long System = 30100032;
    private const long OtherSystem = 30000142;

    private readonly string _schema = "cwr_" + Guid.NewGuid().ToString("N")[..12];
    private NpgsqlDataSource? _source;
    private long _now = Start;
    private readonly Waits _waits = new();
    private readonly CharacterSessions _sessions = new();
    private readonly Dictionary<long, Capture> _sinks = [];

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is not { } url)
        {
            return;
        }
        _source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        await PgCrimewatchStore.MigrateAsync(_source, _schema);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_source is null)
        {
            return;
        }
        await using (var connection = await _source.OpenConnectionAsync())
        {
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {_schema} CASCADE", CancellationToken.None);
        }
        await _source.DisposeAsync();
    }

    private PgCrimewatchStore Store => new(_source!, _schema);

    private CrimewatchRuntime Runtime() => new(Store, _sessions, () => _now, delay: _waits.Delay);

    /// <summary>A pilot in space in a system (a docked one has a station instead).</summary>
    private void Pilot(long character, long system, bool docked = false)
    {
        var sink = new Capture();
        var session = new ClientSession(sink) { SessionId = 26_859_804_000_000 + character, ClientId = 34_065_450 + character, UserId = 34 + character };
        session.CharacterId = character;
        if (docked)
        {
            session.Set("stationid", new PyInt(60015249));
            session.Set("solarsystemid2", new PyInt(system));
        }
        session.Set("solarsystemid", new PyInt(system));   // a docked pilot is in the system too: it is not in space
        _sessions.Enter(character, session);
        _sinks[character] = sink;
    }

    private IReadOnlyList<Notes.Note> Told(long character) => Notes.Of(_sinks[character]);

    private static AggressionRequest Attack(long attacker, long target, SecurityClass where, long system, bool capsule) =>
        new(attacker, target, 9_000_000_000_000 + target, system, where switch { SecurityClass.ZeroSec => 0.0, SecurityClass.LowSec => 0.3, SecurityClass.HighSec => 0.7, _ => 1.0 }, where, capsule);

    private static long FileTime(long unixMs) => Py.FileTimeFromUnixMs(unixMs);

    private static long Number(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, _ => throw new InvalidOperationException(value.ToString()) };

    private static long[] Ids(PyValue value) => [.. ((PyTuple)value).Items.Select(Number)];

    private static async Task Settle(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }
    }

    [PgFact]
    public async Task A_crime_in_high_security_tells_the_attacker_its_timers_and_the_system_who_is_flagged_as_the_reference_does()
    {
        Pilot(1, System);
        Pilot(2, System);
        Pilot(3, OtherSystem);
        Pilot(4, System, docked: true);
        using var runtime = Runtime();

        var result = await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.SafeSec, System, false));

        Assert.Equal(AggressionReason.Criminal, result.Outcome.Reason);
        // the order and the states of what the reference told the attacker: weapons 102 (a minute), pvp 202 and criminal 303 (a quarter of an hour), then the system
        var attacker = Told(1);
        Assert.Equal(["OnWeaponsTimerUpdate", "OnPvpTimerUpdate", "OnCriminalTimerUpdate", "OnSystemCriminalFlagUpdates"], attacker.Select(n => n.Name));
        Assert.All(attacker.Take(3), n => Assert.Equal("charid", n.IdType));
        Assert.Equal([new PyInt(102), new PyLong(FileTime(Start + 60_000))], attacker[0].Args);
        Assert.Equal([new PyInt(202), new PyLong(FileTime(Start + 900_000))], attacker[1].Args);
        Assert.Equal([new PyInt(303), new PyLong(FileTime(Start + 900_000))], attacker[2].Args);
        Assert.Equal("solarsystemid2", attacker[3].IdType);
        Assert.Equal([[], [], [1L]], attacker[3].Args.Select(Ids));
        // the pilot attacked is told who is flagged, not the timers of the attacker; nobody elsewhere or docked is told anything
        var victim = Told(2);
        Assert.Equal(["OnSystemCriminalFlagUpdates"], victim.Select(n => n.Name));
        Assert.Equal([[], [], [1L]], victim[0].Args.Select(Ids));
        Assert.Empty(Told(3));
        Assert.Empty(Told(4));
    }

    [PgFact]
    public async Task A_second_attack_moves_the_timers_on_and_tells_no_flag_again()
    {
        Pilot(1, System);
        Pilot(2, System);
        using var runtime = Runtime();
        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));
        Told(1);
        Told(2);

        _now += 10_000;
        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));

        var attacker = Told(1);
        Assert.Equal(["OnWeaponsTimerUpdate", "OnPvpTimerUpdate", "OnCriminalTimerUpdate"], attacker.Select(n => n.Name));
        Assert.Equal([new PyInt(102), new PyLong(FileTime(Start + 70_000))], attacker[0].Args);
        Assert.Empty(Told(2));   // the system is flagged as before
    }

    [PgFact]
    public async Task A_timer_that_runs_out_is_told_when_it_does_and_the_flag_comes_off_with_the_last_of_them()
    {
        Pilot(1, System);
        Pilot(2, System);
        using var runtime = Runtime();
        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));
        Told(1);
        Told(2);
        Assert.Equal(TimeSpan.FromMilliseconds(60_005), _waits.Made[^1].After);   // the first end, a moment after

        _now = Start + 60_005;
        _waits.Made[^1].Done.SetResult();
        await Settle(() => _sinks[1].Sent.Count > 0);
        var weapons = Told(1);
        Assert.Equal(["OnWeaponsTimerUpdate"], weapons.Select(n => n.Name));
        Assert.Equal([new PyInt(100), PyNone.Instance], weapons[0].Args);
        Assert.Empty(Told(2));

        Assert.Equal(TimeSpan.FromMilliseconds(900_000 - 60_005 + 5), _waits.Made[^1].After);   // then the end of the pvp timer and the flag
        _now = Start + 900_005;
        var waiting = _waits.Made.Count;
        _waits.Made[^1].Done.SetResult();
        await Settle(() => _sinks[1].Sent.Count >= 3);
        var rest = Told(1);
        Assert.Equal(["OnPvpTimerUpdate", "OnCriminalTimerUpdate", "OnSystemCriminalFlagUpdates"], rest.Select(n => n.Name));
        Assert.Equal([new PyInt(200), PyNone.Instance], rest[0].Args);
        Assert.Equal([new PyInt(300), PyNone.Instance], rest[1].Args);
        Assert.Equal([[1L], [], []], rest[2].Args.Select(Ids));   // the pilots that went idle
        Assert.Equal(["OnSystemCriminalFlagUpdates"], Told(2).Select(n => n.Name));
        Assert.Equal(waiting, _waits.Made.Count);   // nothing is left to wait for
    }

    [PgFact]
    public async Task A_ship_shot_in_low_security_makes_a_suspect_and_in_null_security_no_flag()
    {
        Pilot(1, System);
        Pilot(2, System);
        using var runtime = Runtime();
        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.LowSec, System, false));
        var suspect = Told(1);
        Assert.Equal([new PyInt(304), new PyLong(FileTime(Start + 900_000))], suspect.Single(n => n.Name == "OnCriminalTimerUpdate").Args);
        Assert.Equal([[], [1L], []], suspect.Single(n => n.Name == "OnSystemCriminalFlagUpdates").Args.Select(Ids));

        // pilot 3 shoots in null security: the weapons and pvp timers run, nothing else is told
        Pilot(3, OtherSystem);
        Pilot(5, OtherSystem);
        await runtime.RecordAggressionAsync(Attack(3, 5, SecurityClass.ZeroSec, OtherSystem, true));
        Assert.Equal(["OnWeaponsTimerUpdate", "OnPvpTimerUpdate"], Told(3).Select(n => n.Name));
        Assert.Empty(Told(5));
    }

    [PgFact]
    public async Task What_the_law_allows_runs_the_timers_and_is_no_crime_and_a_listener_hears_every_attack_once()
    {
        Pilot(1, System);
        Pilot(2, System);
        using var runtime = Runtime();
        runtime.Legal = (attacker, target, _) => attacker == 1 && target == 2;
        List<AggressionResult> heard = [];
        runtime.Aggressed += result =>
        {
            heard.Add(result);
            return Task.CompletedTask;
        };

        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));

        Assert.Equal(["OnWeaponsTimerUpdate", "OnPvpTimerUpdate"], Told(1).Select(n => n.Name));
        Assert.Empty(Told(2));
        var one = Assert.Single(heard);
        Assert.Equal((1L, 2L, AggressionReason.Legal), (one.Request.Attacker, one.Request.Target, one.Outcome.Reason));
    }

    [PgFact]
    public async Task A_process_that_starts_takes_up_the_wait_for_what_still_runs()
    {
        Pilot(1, System);
        Pilot(2, System);
        using (var first = Runtime())
        {
            await first.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));
        }
        _waits.Made.Clear();
        Told(1);

        _now += 20_000;
        using var second = Runtime();
        await second.ResumeAsync();

        // the client of the pilot is told what it was not told by this process; the wait is for the end of the weapons timer, forty seconds on
        Assert.Contains(_waits.Made, wait => wait.After == TimeSpan.FromMilliseconds(40_005));
        Assert.Contains("OnWeaponsTimerUpdate", Told(1).Select(n => n.Name));
    }

    [PgFact]
    public async Task GetClientStates_answers_the_timers_and_who_is_flagged_in_the_system_of_the_pilot()
    {
        Pilot(1, System);
        Pilot(2, System);
        using var runtime = Runtime();
        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));
        var service = new CrimewatchService(Store, () => _now);

        async Task<PyTuple> StatesOf(long character)
        {
            var session = _sessions.Of(character)!;
            return (PyTuple)await ((IService)service).CallAsync(new ServiceCall { Method = "GetClientStates", Args = [], Session = session });
        }

        var attacker = await StatesOf(1);
        var timers = ((PyTuple)attacker.Items[0]).Items.Cast<PyTuple>().ToList();
        Assert.Equal([new PyInt(102), new PyLong(FileTime(Start + 60_000))], timers[0].Items);
        Assert.Equal([new PyInt(202), new PyLong(FileTime(Start + 900_000))], timers[1].Items);
        Assert.Equal([new PyInt(400), PyNone.Instance], timers[2].Items);
        Assert.Equal([new PyInt(303), new PyLong(FileTime(Start + 900_000))], timers[3].Items);
        Assert.Equal([new PyInt(500), PyNone.Instance], timers[4].Items);
        // the pilot attacked has no timer but sees who is flagged in its system
        var victim = await StatesOf(2);
        Assert.All(((PyTuple)victim.Items[0]).Items.Cast<PyTuple>(), timer => Assert.Equal(PyNone.Instance, timer.Items[1]));
        var flagged = (PyTuple)victim.Items[2];
        Assert.Equal(InventoryWire.Set([new PyInt(1)]), flagged.Items[0]);
        Assert.Equal(InventoryWire.Set([]), flagged.Items[1]);
    }
}

public class PenaltyTests : IAsyncLifetime
{
    private const long Start = 1_700_000_000_000;
    private const long System = 30100032;

    private readonly string _schema = "cwp_" + Guid.NewGuid().ToString("N")[..12];
    private NpgsqlDataSource? _source;
    private long _now = Start;
    private readonly CharacterSessions _sessions = new();
    private readonly Dictionary<long, double> _status = [];
    private readonly List<(long Character, double Value, string Reason)> _written = [];
    private readonly List<long> _shipsRefreshed = [];
    private Capture _attacker = new();

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is not { } url)
        {
            return;
        }
        _source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        await PgCrimewatchStore.MigrateAsync(_source, _schema);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_source is null)
        {
            return;
        }
        await using (var connection = await _source.OpenConnectionAsync())
        {
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {_schema} CASCADE", CancellationToken.None);
        }
        await _source.DisposeAsync();
    }

    private (CrimewatchRuntime Runtime, SecurityStatusPenalties Penalties) Rig()
    {
        var runtime = new CrimewatchRuntime(new PgCrimewatchStore(_source!, _schema), _sessions, () => _now, delay: new Waits().Delay);
        var penalties = new SecurityStatusPenalties(
            runtime, _sessions,
            (id, _) => Task.FromResult(_status.GetValueOrDefault(id)),
            (id, value, reason, _) =>
            {
                _status[id] = value;
                _written.Add((id, value, reason));
                return Task.CompletedTask;
            },
            () => _now,
            (id, _) =>
            {
                _shipsRefreshed.Add(id);
                return Task.CompletedTask;
            });
        penalties.Attach();
        var session = new ClientSession(_attacker) { SessionId = 26_859_804_000_001, ClientId = 34_065_451, UserId = 35 };
        session.CharacterId = 1;
        session.Set("solarsystemid", new PyInt(System));
        session.Set("shipid", new PyLong(9_988_400_003_315));
        _sessions.Enter(1, session);
        return (runtime, penalties);
    }

    private static AggressionRequest Attack(long ship = 9_988_400_003_311, bool capsule = false, double security = 1.0, SecurityClass where = SecurityClass.SafeSec) =>
        new(1, 2, ship, System, security, where, capsule);

    [PgFact]
    public async Task A_crime_costs_a_quarter_point_and_the_pilot_is_told_as_the_reference_told_it_before_its_timers()
    {
        var (runtime, _) = Rig();
        using var _ = runtime;

        await runtime.RecordAggressionAsync(Attack());

        Assert.Equal([(1L, -0.25, "aggression")], _written);   // recorded: −0.25 from 0.0 for a ship in a 1.0 system
        Assert.Equal([1L], _shipsRefreshed);
        var told = Notes.Of(_attacker);
        Assert.Equal(["OnModuleAttributeChanges", "OnSecurityStatusUpdate", "OnWeaponsTimerUpdate", "OnPvpTimerUpdate", "OnCriminalTimerUpdate", "OnSystemCriminalFlagUpdates"], told.Select(n => n.Name));
        var change = (PyTuple)((PyList)told[0].Args[0]).Items[0];
        Assert.Equal("OnModuleAttributeChange", ((PyStr)change.Items[0]).Value);
        Assert.Equal(1L, ((PyInt)change.Items[1]).Value);
        Assert.Equal(9_988_400_003_315, ((PyLong)change.Items[2]).Value);   // the pilot's ship
        Assert.Equal(PyInt.Of(2610), change.Items[3]);
        Assert.Equal([Py.Number(-0.25), Py.Number(0)], new[] { change.Items[5], change.Items[6] });
        Assert.Equal("charid", told[1].IdType);
        Assert.Equal([Py.Number(-0.25)], told[1].Args);
    }

    [PgFact]
    public async Task A_second_shot_at_the_same_ship_costs_nothing_for_a_minute_and_one_at_another_ship_costs_again()
    {
        var (runtime, _) = Rig();
        using var _ = runtime;
        await runtime.RecordAggressionAsync(Attack());
        _now += 10_000;
        await runtime.RecordAggressionAsync(Attack());
        Assert.Single(_written);

        await runtime.RecordAggressionAsync(Attack(ship: 9_988_400_003_999));   // another ship
        Assert.Equal(2, _written.Count);
        Assert.True(_written[1].Value < _written[0].Value);

        _now += 61_000;   // the window is over
        await runtime.RecordAggressionAsync(Attack());
        Assert.Equal(3, _written.Count);
    }

    [PgFact]
    public async Task A_capsule_costs_ten_times_a_ship_and_what_is_no_crime_costs_nothing()
    {
        var (runtime, _) = Rig();
        using var _ = runtime;
        await runtime.RecordAggressionAsync(Attack(capsule: true));
        Assert.Equal(-2.5, _written.Single().Value);

        // the law allows it: the timers run and nothing else
        runtime.Legal = (_, _, _) => true;
        _status.Clear();
        _written.Clear();
        await runtime.RecordAggressionAsync(Attack(ship: 42));
        Assert.Empty(_written);

        // null security is no crime
        runtime.Legal = null;
        await runtime.RecordAggressionAsync(Attack(ship: 43, security: 0, where: SecurityClass.ZeroSec));
        Assert.Empty(_written);
    }

    [PgFact]
    public async Task A_pilot_at_the_floor_loses_no_more()
    {
        var (runtime, _) = Rig();
        using var _ = runtime;
        _status[1] = -10;
        await runtime.RecordAggressionAsync(Attack(capsule: true));
        Assert.Empty(_written);
        Assert.Empty(_shipsRefreshed);
        Assert.DoesNotContain(Notes.Of(_attacker), n => n.Name == "OnSecurityStatusUpdate");
    }
}
