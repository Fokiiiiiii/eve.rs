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

public sealed class ConcordResponseDelayTests
{
    [Theory]
    [InlineData(0.5, 19_000)]
    [InlineData(0.6, 14_000)]
    [InlineData(0.7, 10_000)]
    [InlineData(0.8, 7_000)]
    [InlineData(0.9, 6_000)]
    public void Response_delay_uses_the_community_observed_base_table(double security, long expectedMs)
    {
        Assert.Equal(expectedMs, CrimewatchRuntime.ConcordResponseDelayMs(security));
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
    private readonly CrimewatchPersistenceFake _persistence = new();
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

    private CrimewatchRuntime Runtime(Eve.Scheduler.LocalTimers? localTimers = null) => new(Store, _sessions, () => _now, _persistence, delay: _waits.Delay, localTimers: localTimers);

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
    public async Task Criminal_aggression_grants_a_persisted_kill_right_and_schedules_one_concord_response()
    {
        Pilot(1, System);
        Pilot(2, System);
        using var runtime = Runtime();
        var created = new List<KillRightCreated>();
        var responses = new List<ConcordResponseRequest>();
        runtime.CreateKillRight = (request, ct) =>
            Store.CreateKillRightAsync(request.OwnerCharacterId, request.TargetCharacterId, request.SolarSystemId, request.CreatedAtUnixMs, ct);
        runtime.KillRightGranted += right =>
        {
            created.Add(right);
            return Task.CompletedTask;
        };
        runtime.ConcordResponseDue += response =>
        {
            responses.Add(response);
            return Task.CompletedTask;
        };

        var offense = Attack(1, 2, SecurityClass.HighSec, System, capsule: false);
        var result = await runtime.RecordAggressionAsync(offense);
        await runtime.RecordAggressionAsync(offense);

        Assert.Equal(AggressionReason.Criminal, result.Outcome.Reason);
        var rights = await Store.KillRightsOfAsync(2);
        var right = Assert.Single(rights);
        Assert.Equal((2L, 1L, System, Start),
            (right.OwnerCharacterId, right.TargetCharacterId, right.SolarSystemId, right.CreatedAtUnixMs));
        Assert.Equal([new KillRightCreated(right.KillRightId, 2, 1, System, Start)], created);
        var responseWait = Assert.Single(_waits.Made, wait => wait.After == TimeSpan.FromMilliseconds(10_005));
        Assert.Empty(responses);

        _now = Start + 10_005;
        responseWait.Done.TrySetResult();
        await Settle(() => responses.Count == 1);

        var response = Assert.Single(responses);
        Assert.Equal((1L, System, 0.7), (response.AttackerCharacterId, response.SolarSystemId, response.Security));
        Assert.Equal(Start, response.CrimeAtUnixMs);
    }


    [PgFact]
    public async Task Low_security_grants_a_kill_right_only_for_a_criminal_capsule_attack_without_concord()
    {
        Pilot(1, System);
        Pilot(2, System);
        Pilot(3, System);
        using var runtime = Runtime();
        var grants = new List<KillRightGrantRequest>();
        var responses = new List<ConcordResponseRequest>();
        runtime.CreateKillRight = (request, _) =>
        {
            grants.Add(request);
            return Task.FromResult<long?>(8_001);
        };
        runtime.KillRightGranted += _ => Task.CompletedTask;
        runtime.ConcordResponseDue += response =>
        {
            responses.Add(response);
            return Task.CompletedTask;
        };

        var shipAttack = await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.LowSec, System, capsule: false));
        var capsuleAttack = await runtime.RecordAggressionAsync(Attack(1, 3, SecurityClass.LowSec, System, capsule: true));

        Assert.Equal(AggressionReason.Suspect, shipAttack.Outcome.Reason);
        Assert.Equal(AggressionReason.Criminal, capsuleAttack.Outcome.Reason);
        Assert.Equal([(3L, 1L)], grants.Select(request => (request.OwnerCharacterId, request.TargetCharacterId)));
        Assert.Empty(responses);
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
    public async Task Crimewatch_uses_an_injected_shared_timer_without_disposing_it()
    {
        Pilot(1, System);
        Pilot(2, System);
        using var shared = new Eve.Scheduler.LocalTimers(delay: _waits.Delay);
        var runtime = Runtime(shared);

        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));

        Assert.True(shared.Count > 0);
        runtime.Dispose();
        Assert.True(shared.Count > 0);
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
        runtime.Legal = (attacker, target, _, _) => Task.FromResult(attacker == 1 && target == 2);
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
    public async Task Host_wired_wars_use_owner_chains_allies_and_active_expiry()
    {
        Pilot(1, System);
        Pilot(2, System);
        Pilot(3, System);

        await using var relations = await RelationsRig.Create(_source!, () => _now);
        var attackerCorporation = 98_000_101L;
        var attackerAlliance = 99_000_101L;
        var defenderCorporation = 98_000_102L;
        var allyCorporation = 98_000_103L;
        await relations.Set(Eve.Relations.RelationKinds.CorporationMember.Name, 1, attackerCorporation);
        await relations.Set(Eve.Relations.RelationKinds.AllianceMember.Name, attackerCorporation, attackerAlliance);
        await relations.Set(Eve.Relations.RelationKinds.CorporationMember.Name, 2, defenderCorporation);
        await relations.Set(Eve.Relations.RelationKinds.CorporationMember.Name, 3, allyCorporation);

        var wars = new RelationWars(relations.Relations);
        Assert.False(await wars.AreAtWarAsync(1, 2, _now));

        await relations.Set(Eve.Relations.RelationKinds.War.Name, attackerAlliance, defenderCorporation);
        var activeWar = await relations.Relations.GetAsync(Eve.Relations.RelationKinds.War.Name, attackerAlliance, defenderCorporation);
        Assert.NotNull(activeWar);
        Assert.True(await wars.AreAtWarAsync(1, 2, _now));
        await relations.Set(Eve.Relations.RelationKinds.WarAlly.Name, allyCorporation, activeWar.Id, attrs: new { side = "defender" }, until: _now + 1_000);
        Assert.True(await wars.AreAtWarAsync(1, 3, _now));

        var opponentSupport = await wars.AssistanceFactsAsync(1, 2, _now);
        Assert.Equal(new RelationWars.AssistanceFacts(true, true, false), opponentSupport);
        var allySupport = await wars.AssistanceFactsAsync(3, 2, _now);
        Assert.Equal(new RelationWars.AssistanceFacts(true, true, true), allySupport);

        var stranger = 8L;
        var strangerCorporation = 98_000_110L;
        await relations.Set(Eve.Relations.RelationKinds.CorporationMember.Name, stranger, strangerCorporation);
        var thirdPartySupport = await wars.AssistanceFactsAsync(stranger, 2, _now);
        Assert.Equal(new RelationWars.AssistanceFacts(true, false, false), thirdPartySupport);

        using var runtime = Runtime();
        runtime.Legal = wars.AreAtWarAsync;
        var result = await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));
        Assert.Equal(AggressionReason.Legal, result.Outcome.Reason);
        var allyResult = await runtime.RecordAggressionAsync(Attack(1, 3, SecurityClass.HighSec, System, false));
        Assert.Equal(AggressionReason.Legal, allyResult.Outcome.Reason);
        Assert.Empty(_persistence.Writes); // a legal war attack does not create an LE
        _now += 1_000;
        Assert.False(await wars.AreAtWarAsync(1, 3, _now));

        var allyAttacker = 6L;
        var allyDefender = 7L;
        var allyAttackerCorporation = 98_000_106L;
        var allyDefenderCorporation = 98_000_107L;
        var warCorporationA = 98_000_108L;
        var warCorporationB = 98_000_109L;
        await relations.Set(Eve.Relations.RelationKinds.CorporationMember.Name, allyAttacker, allyAttackerCorporation);
        await relations.Set(Eve.Relations.RelationKinds.CorporationMember.Name, allyDefender, allyDefenderCorporation);
        await relations.Set(Eve.Relations.RelationKinds.War.Name, warCorporationA, warCorporationB);
        var hiddenWar = await relations.Relations.GetAsync(Eve.Relations.RelationKinds.War.Name, warCorporationA, warCorporationB);
        Assert.NotNull(hiddenWar);
        await relations.Set(Eve.Relations.RelationKinds.WarAlly.Name, allyAttackerCorporation, hiddenWar.Id, attrs: new { side = "aggressor" }, until: _now + 1_000);
        await relations.Set(Eve.Relations.RelationKinds.WarAlly.Name, allyDefenderCorporation, hiddenWar.Id, attrs: new { side = "defender" }, until: _now + 1_000);
        Assert.True(await wars.AreAtWarAsync(allyAttacker, allyDefender, _now));

        var expiredAttacker = 4L;
        var expiredTarget = 5L;
        var expiredAttackerCorporation = 98_000_104L;
        var expiredTargetCorporation = 98_000_105L;
        await relations.Set(Eve.Relations.RelationKinds.CorporationMember.Name, expiredAttacker, expiredAttackerCorporation);
        await relations.Set(Eve.Relations.RelationKinds.CorporationMember.Name, expiredTarget, expiredTargetCorporation);
        await relations.Set(Eve.Relations.RelationKinds.War.Name, expiredAttackerCorporation, expiredTargetCorporation, until: _now);
        Assert.False(await wars.AreAtWarAsync(expiredAttacker, expiredTarget, _now));
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
    public async Task An_active_criminal_timer_replays_the_concord_response_after_restart()
    {
        Pilot(1, System);
        Pilot(2, System);
        using (var first = Runtime())
        {
            first.SecurityOfSystem = (_, _) => Task.FromResult<double?>(0.7);
            await first.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, capsule: false));
        }
        _waits.Made.Clear();

        _now = Start + 20_000;
        using var resumed = Runtime();
        resumed.SecurityOfSystem = (_, _) => Task.FromResult<double?>(0.7);
        var responses = new List<ConcordResponseRequest>();
        resumed.ConcordResponseDue += response =>
        {
            responses.Add(response);
            return Task.CompletedTask;
        };

        await resumed.ResumeAsync();

        var replay = Assert.Single(_waits.Made, wait => wait.After == TimeSpan.FromMilliseconds(5));
        replay.Done.TrySetResult();
        await Settle(() => responses.Count == 1);
        Assert.Equal((1L, System), (Assert.Single(responses).AttackerCharacterId, responses[0].SolarSystemId));
    }

    [PgFact]
    public async Task GetClientStates_answers_the_timers_and_who_is_flagged_in_the_system_of_the_pilot()
    {
        Pilot(1, System);
        Pilot(2, System);
        using var runtime = Runtime();
        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));
        var service = new CrimewatchService(Store, runtime, () => _now);

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

    [PgFact]
    public async Task GetSecurityStatusTransactions_returns_attribute_accessible_packed_rows_with_filetimes()
    {
        Pilot(1, System);
        _persistence.History[1] =
        [
            new SecurityStatusTransaction(SecurityStatusEventTypeIds.KillPirateNpc, Start + 2_000, null, null, null, null, 5.0),
            new SecurityStatusTransaction(SecurityStatusEventTypeIds.IllegalAggression, Start + 1_000, 2, 587, System, -0.025, -0.25),
        ];
        using var runtime = Runtime();
        var service = new CrimewatchService(Store, runtime, () => _now);

        var result = await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetSecurityStatusTransactions",
            Args = [],
            Session = _sessions.Of(1)!,
        });

        var rowset = Assert.IsType<PyObjectEx>(result);
        Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
        Assert.Equal(new PyToken("carbon.common.script.sys.crowset.CRowset"), ((PyTuple)((PyTuple)rowset.Header).Items[0]).Items[0]);
        Assert.Equal(2, rowset.List.Count);
        var newest = Assert.IsType<PyPackedRow>(rowset.List[0]);
        Assert.Equal(
            ["eventTypeID", "eventDate", "otherOwnerID", "otherTypeID", "locationID", "modification", "newValue"],
            newest.Columns.Select(column => column.Name).ToArray());
        var newestFields = newest.Fields().ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(new PyInt(SecurityStatusEventTypeIds.KillPirateNpc), newestFields["eventTypeID"]);
        Assert.Equal(new PyLong(FileTime(Start + 2_000)), newestFields["eventDate"]);
        Assert.Equal(PyNone.Instance, newestFields["otherOwnerID"]);
        Assert.Equal(PyNone.Instance, newestFields["otherTypeID"]);
        Assert.Equal(PyNone.Instance, newestFields["locationID"]);
        Assert.Equal(PyNone.Instance, newestFields["modification"]);
        Assert.Equal(Py.Number(5.0), newestFields["newValue"]);

        var crimeFields = Assert.IsType<PyPackedRow>(rowset.List[1]).Fields().ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(new PyInt(SecurityStatusEventTypeIds.IllegalAggression), crimeFields["eventTypeID"]);
        Assert.Equal(InventoryWire.Id(2), crimeFields["otherOwnerID"]);
        Assert.Equal(new PyInt(587), crimeFields["otherTypeID"]);
        Assert.Equal(InventoryWire.Id(System), crimeFields["locationID"]);
        Assert.Equal(Py.Number(-0.025), crimeFields["modification"]);
        Assert.Equal(Py.Number(-0.25), crimeFields["newValue"]);
    }

    [Fact]
    public async Task GetKillRights_without_requested_targets_returns_an_empty_list()
    {
        Pilot(1, System);
        var service = new KillRightMgrService(Store);

        var result = await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetKillRights",
            Args = [new PyList([])],
            Session = _sessions.Of(1)!,
        });

        Assert.Empty(Assert.IsType<PyList>(result).Items);
    }

    [PgFact]
    public async Task GetKillRights_returns_only_the_callers_active_rights_for_requested_targets()
    {
        Pilot(1, System);
        Pilot(2, System);
        var owned = await Store.CreateKillRightAsync(1, 2, System, Start);
        var consumed = await Store.CreateKillRightAsync(1, 3, System, Start + 1);
        Assert.NotNull(owned);
        Assert.NotNull(consumed);
        Assert.True(await Store.MarkKillRightUsedAsync(1, consumed.Value, Start + 2));
        Assert.NotNull(await Store.CreateKillRightAsync(3, 2, System, Start + 3));

        var service = new KillRightMgrService(Store);
        var result = await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetKillRights",
            Args = [new PyList([InventoryWire.Id(2), InventoryWire.Id(3)])],
            Session = _sessions.Of(1)!,
        });

        var rights = Assert.IsType<PyList>(result);
        var right = Assert.IsType<PyObject>(Assert.Single(rights.Items));
        Assert.Equal(new PyStr("util.KeyVal"), right.Name);
        var fields = Assert.IsType<PyDict>(right.Args).Entries
            .ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        Assert.Equal(new PyLong(owned.Value), fields["killRightID"]);
        Assert.Equal(InventoryWire.Id(1), fields["fromID"]);
        Assert.Equal(InventoryWire.Id(2), fields["toID"]);
        Assert.Equal(InventoryWire.Id(1), fields["restrictedTo"]);
        Assert.Equal(PyNone.Instance, fields["price"]);
        Assert.Equal(InventoryWire.Id(System), fields["solarSystemID"]);
    }

    [PgFact]
    public async Task GetClientStates_projects_the_shared_active_engagement_pair_to_the_peer_FILETIME()
    {
        Pilot(1, System);
        _persistence.Engagements[(1, 2)] = Start + 300_000;
        using var runtime = Runtime();
        var service = new CrimewatchService(Store, runtime, () => _now);

        var result = (PyTuple)await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetClientStates",
            Args = [],
            Session = _sessions.Of(1)!,
        });

        var expected = new PyDict([
            new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(2), new PyLong(FileTime(Start + 300_000))),
        ]);
        Assert.Equal(expected, result.Items[1]);
    }

    [PgFact]
    public async Task Attacking_a_suspect_creates_and_combat_renews_the_five_minute_pair_but_a_war_only_attack_does_not()
    {
        Pilot(1, System);
        Pilot(2, System);
        Pilot(3, System);
        var flagged = Aggression.Record(CombatState.Idle, CombatState.Idle, SecurityClass.LowSec, System, false, false, _now).Attacker;
        await Store.UpdateAsync(2, _ => flagged, _now);

        using var runtime = Runtime();
        runtime.Legal = (_, _, _, _) => Task.FromResult(true);
        var first = await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));
        // EveJS resolves an already-suspect target before the ordinary war check.
        Assert.Equal(AggressionReason.TargetFlagged, first.Outcome.Reason);
        Assert.Equal(Start + CrimewatchRuntime.LimitedEngagementDurationMs, _persistence.Engagements[(1, 2)]);
        Assert.Contains(Told(1), note => note.Name == "OnCrimewatchEngagementCreated");
        Assert.Contains(Told(2), note => note.Name == "OnCrimewatchEngagementCreated");

        _now += 120_000;
        await runtime.RecordAggressionAsync(Attack(1, 2, SecurityClass.HighSec, System, false));
        Assert.Equal(_now + CrimewatchRuntime.LimitedEngagementDurationMs, _persistence.Engagements[(1, 2)]);
        Assert.Contains(Told(1), note => note.Name == "OnCrimewatchEngagementStartTimeout");
        Assert.Contains(Told(2), note => note.Name == "OnCrimewatchEngagementStartTimeout");

        var warOnly = await runtime.RecordAggressionAsync(Attack(1, 3, SecurityClass.HighSec, System, false));
        Assert.Equal(AggressionReason.Legal, warOnly.Outcome.Reason);
        Assert.False(_persistence.Engagements.ContainsKey((1, 3)));
    }

    [PgFact]
    public async Task A_persisted_engagement_expires_once_and_notifies_both_online_pilots()
    {
        Pilot(1, System);
        Pilot(2, System);
        _persistence.Engagements[(1, 2)] = Start + 300_000;
        using var runtime = Runtime();
        await runtime.ResumeAsync();

        var expiry = Assert.Single(_waits.Made);
        Assert.Equal(TimeSpan.FromMilliseconds(300_005), expiry.After);
        _now = Start + 300_005;
        expiry.Done.SetResult();
        // the end is removed first and told after, so the test waits for both notices (Told drains a sink, so only the count is read here)
        await Settle(() => _persistence.Engagements.Count == 0 && _sinks[1].Sent.Count > 0 && _sinks[2].Sent.Count > 0);

        Assert.Contains(Told(1), note => note.Name == "OnCrimewatchEngagementEnded");
        Assert.Contains(Told(2), note => note.Name == "OnCrimewatchEngagementEnded");
    }

    [PgFact]
    public async Task Duel_challenges_use_host_expiry_and_acceptance_or_decline_consumes_the_pending_request()
    {
        Pilot(1, System);
        Pilot(2, System);
        Pilot(3, System);
        var challenger = _sessions.Of(1)!;
        challenger.Set("corpid", new PyLong(98_000_001));
        challenger.Set("allianceid", new PyLong(99_000_001));
        using var runtime = Runtime();
        var service = new CrimewatchService(Store, runtime, () => _now);

        Assert.Equal(PyNone.Instance, await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "StartDuelChallenge",
            Args = [new PyLong(2)],
            Session = challenger,
        }));
        Assert.Equal([(1L, 2L, 98_000_001L, 99_000_001L)], _persistence.ChallengesStarted);
        var expiry = _persistence.ChallengeExpiryFileTime;
        await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "RespondToDuelChallenge",
            Args = [new PyLong(1), new PyLong(expiry), PyBool.True],
            Session = _sessions.Of(2)!,
        });

        Assert.Equal(Start + CrimewatchRuntime.LimitedEngagementDurationMs, _persistence.Engagements[(1, 2)]);
        Assert.Contains(Told(1), note => note.Name == "OnCrimewatchEngagementCreated");
        Assert.Contains(Told(2), note => note.Name == "OnCrimewatchEngagementCreated");

        await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "StartDuelChallenge",
            Args = [new PyLong(3)],
            Session = challenger,
        });
        await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "RespondToDuelChallenge",
            Args = [new PyLong(1), new PyLong(_persistence.ChallengeExpiryFileTime), PyBool.False],
            Session = _sessions.Of(3)!,
        });

        Assert.DoesNotContain((1L, 3L), _persistence.Engagements.Keys);
        Assert.Equal([true, false], _persistence.ChallengesAnswered.Select(response => response.Accepted));
    }

    [PgFact]
    public async Task Npc_and_disapproval_timers_persist_renew_and_expire()
    {
        Pilot(1, System);
        using var runtime = Runtime();

        await runtime.RecordNpcTimerAsync(1);
        var started = Told(1);
        Assert.Equal(["OnNpcTimerUpdate"], started.Select(note => note.Name));
        Assert.Equal([new PyInt(402), new PyLong(FileTime(Start + CombatState.NpcDurationMs))], started[0].Args);

        _now += 10_000;
        await runtime.RecordNpcTimerAsync(1);
        await runtime.RecordDisapprovalTimerAsync(1);

        var active = Told(1);
        var state = await Store.StateOfAsync(1);
        Assert.Equal(Start + 10_000 + CombatState.NpcDurationMs, state.NpcUntilMs);
        Assert.Equal(Start + 10_000 + CombatState.DisapprovalDurationMs, state.DisapprovalUntilMs);
        Assert.Equal(
            ["OnNpcTimerUpdate", "OnDisapprovalTimerUpdate"],
            active.Select(note => note.Name));
        Assert.Equal(TimeSpan.FromMilliseconds(300_005), _waits.Made[^1].After);

        _now = Start + 310_005;
        _waits.Made[^1].Done.SetResult();
        await Settle(() => _sinks[1].Sent.OfType<PyObject>().Count() >= 2);

        var ended = Told(1);
        Assert.Equal(["OnNpcTimerUpdate", "OnDisapprovalTimerUpdate"], ended.Select(note => note.Name));
        Assert.Equal([new PyInt(400), PyNone.Instance], ended[0].Args);
        Assert.Equal([new PyInt(500), PyNone.Instance], ended[1].Args);
    }


    [Fact]
    public void Kill_right_manager_exposes_activation_and_purchase_rpcs()
    {
        var service = (IService)new KillRightMgrService(Store);

        Assert.True(service.HasMethod("ActivateKillRight"));
        Assert.True(service.HasMethod("BuyKillRight"));
    }


    [PgFact]
    public async Task GetKillRights_returns_sale_price_and_owner_restriction()
    {
        Pilot(1, System);
        var store = Store;
        var killRightId = await store.CreateKillRightAsync(1, 2, System, _now);
        Assert.NotNull(killRightId);
        Assert.True(await store.SetKillRightSaleAsync(1, killRightId.Value, 250m, 9000001, _now));
        var service = (IService)new KillRightMgrService(store, nowUnixMs: () => _now);

        var result = await service.CallAsync(new ServiceCall
        {
            Method = "GetKillRights",
            Args = [new PyList([InventoryWire.Id(2)])],
            Session = _sessions.Of(1)!,
        });

        var right = Assert.IsType<PyObject>(Assert.Single(Assert.IsType<PyList>(result).Items));
        var fields = Assert.IsType<PyDict>(right.Args).Entries
            .ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        Assert.Equal(new PyReal(250d), fields["price"]);
        Assert.Equal(InventoryWire.Id(9000001), fields["restrictedTo"]);
    }



    [PgFact]
    public async Task Bounty_proxy_read_rpcs_match_the_reference_routes_and_sale_access()
    {
        Pilot(1, System);
        var session = _sessions.Of(1)!;
        session.Set("corpid", new PyInt(500));
        session.Set("allianceid", new PyInt(700));

        var ownerRight = await Store.CreateKillRightAsync(1, 2, System, _now);
        var targetRight = await Store.CreateKillRightAsync(3, 1, System, _now);
        var publicSale = await Store.CreateKillRightAsync(4, 2, System, _now);
        var corpSale = await Store.CreateKillRightAsync(5, 2, System, _now);
        var allianceSale = await Store.CreateKillRightAsync(6, 2, System, _now);
        var restrictedSale = await Store.CreateKillRightAsync(7, 2, System, _now);
        var privateRight = await Store.CreateKillRightAsync(8, 2, System, _now);
        Assert.All([ownerRight, targetRight, publicSale, corpSale, allianceSale, restrictedSale, privateRight],
            id => Assert.NotNull(id));
        Assert.True(await Store.SetKillRightSaleAsync(4, publicSale!.Value, 100m, null, _now));
        Assert.True(await Store.SetKillRightSaleAsync(5, corpSale!.Value, 200m, 500, _now));
        Assert.True(await Store.SetKillRightSaleAsync(6, allianceSale!.Value, 300m, 700, _now));
        Assert.True(await Store.SetKillRightSaleAsync(7, restrictedSale!.Value, 400m, 900, _now));

        var service = (IService)new BountyProxyKillRightService(store: Store, nowUnixMs: () => _now);
        Assert.True(service.HasMethod("GetMyKillRights"));
        Assert.True(service.HasMethod("GetKillRightsOnCharacters"));
        Assert.True(service.HasMethod("GetBountiesAndKillRights"));
        Assert.False(service.HasMethod("GetKillRights"));

        var myRights = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetMyKillRights",
            Args = [],
            Session = session,
        }));
        Assert.Equal([ownerRight!.Value, targetRight!.Value], myRights.Items
            .Select(row => (long)((PyLong)Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args)
                .Entries.Single(entry => entry.Key == new PyStr("killRightID")).Value).Value));

        var available = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetKillRightsOnCharacters",
            Args = [new PyList([new PyLong(2)])],
            Session = session,
        }));
        Assert.Equal([publicSale!.Value, corpSale!.Value, allianceSale!.Value], available.Items
            .Select(row => (long)((PyLong)Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args)
                .Entries.Single(entry => entry.Key == new PyStr("killRightID")).Value).Value));
        var myPayload = Assert.IsType<PyDict>(Assert.IsType<PyObject>(myRights.Items[0]).Args);
        Assert.Equal(
            ["killRightID", "fromID", "toID", "expiryTime", "price", "restrictedTo"],
            myPayload.Entries.Select(entry => Assert.IsType<PyStr>(entry.Key).Value));
        Assert.Same(PyNone.Instance, myPayload.Entries.Single(entry => entry.Key == new PyStr("price")).Value);
        Assert.Same(PyNone.Instance, myPayload.Entries.Single(entry => entry.Key == new PyStr("restrictedTo")).Value);

        var combined = Assert.IsType<PyTuple>(await service.CallAsync(new ServiceCall
        {
            Method = "GetBountiesAndKillRights",
            Args = [new PyList([]), new PyList([new PyLong(2)])],
            Session = session,
        }));
        var defaultBountyRows = Assert.IsType<PyList>(combined.Items[0]);
        Assert.Equal(
            [new PyInt(0), new PyInt(1), new PyInt(500), new PyInt(700)],
            defaultBountyRows.Items.Select(item => Assert.IsType<PyTuple>(item).Items[0]));
        Assert.Equal(available.Items, Assert.IsType<PyList>(combined.Items[1]).Items);
    }

    [PgFact]
    public async Task Bounty_proxy_sells_and_cancels_restricted_and_open_kill_rights()
    {
        Pilot(1, System);
        Pilot(2, System);
        var store = Store;
        var killRightId = await store.CreateKillRightAsync(1, 2, System, _now);
        Assert.NotNull(killRightId);
        var notifications = new List<(long Recipient, long Sender, int Type, System.Text.Json.Nodes.JsonObject Data)>();
        var service = (IService)new BountyProxyKillRightService(
            store: store,
            notificationWriter: (recipient, sender, type, data, _, _) =>
            {
                notifications.Add((recipient, sender, type, (System.Text.Json.Nodes.JsonObject)data.DeepClone()));
                return Task.CompletedTask;
            },
            nowUnixMs: () => _now);
        Assert.True(service.HasMethod("SellKillRight"));
        Assert.True(service.HasMethod("CancelSellKillRight"));

        var restrictedSale = await service.CallAsync(new ServiceCall
        {
            Method = "SellKillRight",
            Args = [new PyLong(killRightId.Value), new PyReal(250d), new PyLong(9000001)],
            Session = _sessions.Of(1)!,
        });
        var listed = Assert.IsType<PyObject>(Assert.Single(Assert.IsType<PyList>(restrictedSale).Items));
        var fields = Assert.IsType<PyDict>(listed.Args).Entries
            .ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        Assert.Equal(new PyReal(250d), fields["price"]);
        Assert.Equal(InventoryWire.Id(9000001), fields["restrictedTo"]);
        Assert.Equal((2L, 1L, KillRightNotificationTypes.Available),
            (notifications[0].Recipient, notifications[0].Sender, notifications[0].Type));
        Assert.Equal(1L, notifications[0].Data["charID"]!.GetValue<long>());
        Assert.Equal(9000001L, notifications[0].Data["toEntityID"]!.GetValue<long>());

        var cancelled = await service.CallAsync(new ServiceCall
        {
            Method = "CancelSellKillRight",
            Args = [new PyLong(killRightId.Value), new PyLong(2)],
            Session = _sessions.Of(1)!,
        });
        Assert.Same(PyNone.Instance, cancelled);
        Assert.Null(Assert.Single(await store.KillRightsOfAsync(1)).SalePrice);
        Assert.Equal(KillRightNotificationTypes.Unavailable, notifications[1].Type);
        Assert.Equal(9000001L, notifications[1].Data["toEntityID"]!.GetValue<long>());

        await service.CallAsync(new ServiceCall
        {
            Method = "SellKillRight",
            Args = [new PyLong(killRightId.Value), new PyReal(50d), PyNone.Instance],
            Session = _sessions.Of(1)!,
        });
        Assert.Equal(KillRightNotificationTypes.AvailableOpen, notifications[2].Type);
        Assert.Null(notifications[2].Data["toEntityID"]);
        await service.CallAsync(new ServiceCall
        {
            Method = "CancelSellKillRight",
            Args = [new PyLong(killRightId.Value), new PyLong(2)],
            Session = _sessions.Of(1)!,
        });
        Assert.Equal(KillRightNotificationTypes.UnavailableOpen, notifications[3].Type);
    }


    [PgFact]
    public async Task Killmail_hook_selects_the_final_attackers_activation_and_consumes_the_right_once()
    {
        var store = Store;
        var killRightId = await store.CreateKillRightAsync(1, 2, System, _now);
        Assert.NotNull(killRightId);
        Assert.True(await store.SetKillRightSaleAsync(1, killRightId.Value, 0m, null, _now));
        var ownerActivation = await store.ActivateKillRightAsync(killRightId.Value, 1, _now);
        var buyerPurchase = await store.BuyKillRightAsync(
            killRightId.Value, 3, [3L], null, _now + 1, payment: null);
        Assert.True(ownerActivation.Success);
        Assert.True(buyerPurchase.Success);

        var consumed = await store.ConsumeKillRightAfterKillAsync(2, 3, _now + 2);

        Assert.Equal(buyerPurchase.Activation!.ActivationId, consumed?.ActivationId);
        Assert.Empty(await store.ActiveKillRightActivationsForTargetAsync(2, _now + 2));
        Assert.Empty(await store.KillRightsOfAsync(1));
        Assert.Null(await store.ConsumeKillRightAfterKillAsync(2, 3, _now + 3));
    }


    [PgFact]
    public async Task Activating_an_owned_kill_right_persists_a_fifteen_minute_suspect_flag()
    {
        Pilot(1, System);
        Pilot(2, System);
        var store = Store;
        var killRightId = await store.CreateKillRightAsync(1, 2, System, _now);
        Assert.NotNull(killRightId);
        using var runtime = Runtime();
        var service = (IService)new KillRightMgrService(store, runtime, nowUnixMs: () => _now);

        var result = await service.CallAsync(new ServiceCall
        {
            Method = "ActivateKillRight",
            Args = [new PyLong(killRightId.Value)],
            Session = _sessions.Of(1)!,
        });

        Assert.Same(PyNone.Instance, result);
        var state = await store.StateOfAsync(2);
        Assert.Equal(Flag.Suspect, state.FlagAt(_now));
        Assert.Equal(_now + CombatState.FlagDurationMs, state.FlagUntilMs);
        var activation = Assert.Single(await store.ActiveKillRightActivationsForTargetAsync(2, _now));
        Assert.Equal(killRightId.Value, activation.KillRightId);
        Assert.Equal(1, activation.ActivatorCharacterId);
        Assert.Equal("activate", activation.ActivationKind);
        Assert.Contains(Told(1), note => note.Name == "OnKillRightActivated");
    }

    [PgFact]
    public async Task Buying_a_restricted_kill_right_transfers_isk_atomically_and_applies_suspect()
    {
        var walletSchema = "krw_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Wallet.PgWalletStore.MigrateAsync(_source!, walletSchema);
            var walletStore = new Eve.Wallet.PgWalletStore(_source!, walletSchema);
            await using (var connection = await _source!.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await walletStore.OpenAsync(connection, transaction, 1, 0m, 0);
                await walletStore.OpenAsync(connection, transaction, 3, 1_000m, 0);
                await transaction.CommitAsync();
            }
            Pilot(1, System);
            Pilot(2, System);
            Pilot(3, System);
            Pilot(4, System);
            _sessions.Of(3)!.Set("corpid", new PyInt(9000001));
            var store = Store;
            var killRightId = await store.CreateKillRightAsync(1, 2, System, _now);
            Assert.NotNull(killRightId);
            Assert.True(await store.SetKillRightSaleAsync(1, killRightId.Value, 250m, 9000001, _now));
            using var runtime = Runtime();
            var usedNotifications = new List<KillRightActivation>();
            var killRightManager = new KillRightMgrService(
                store, runtime, new Eve.Wallet.WalletTransactions(_source!, walletSchema), walletStore, () => _now)
            {
                KillRightUsedNotification = (activation, _) =>
                {
                    usedNotifications.Add(activation);
                    return Task.CompletedTask;
                },
            };
            var service = (IService)killRightManager;

            var result = await service.CallAsync(new ServiceCall
            {
                Method = "BuyKillRight",
                Args = [new PyLong(killRightId.Value), new PyReal(250d)],
                Session = _sessions.Of(3)!,
            });

            Assert.Same(PyNone.Instance, result);
            Assert.Equal(750m, (await walletStore.GetAsync(3))!.Isk);
            Assert.Equal(250m, (await walletStore.GetAsync(1))!.Isk);
            Assert.Equal(Flag.Suspect, (await store.StateOfAsync(2)).FlagAt(_now));
            var activation = Assert.Single(await store.ActiveKillRightActivationsForTargetAsync(2, _now));
            Assert.Equal(3, activation.ActivatorCharacterId);
            Assert.Equal(250m, activation.PricePaid);
            Assert.Equal("buy", activation.ActivationKind);
            Assert.Contains(Told(3), note => note.Name == "OnKillRightActivated");
            Assert.Equal(killRightId.Value, Assert.Single(usedNotifications).KillRightId);

            var unaffordableId = await store.CreateKillRightAsync(1, 4, System, _now + 1);
            Assert.NotNull(unaffordableId);
            Assert.True(await store.SetKillRightSaleAsync(1, unaffordableId.Value, 2_000m, 9000001, _now + 1));
            var error = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(async () =>
                await service.CallAsync(new ServiceCall
                {
                    Method = "BuyKillRight",
                    Args = [new PyLong(unaffordableId.Value), new PyReal(2_000d)],
                    Session = _sessions.Of(3)!,
                }));
            Assert.True(error.Error.IsUserError);
            Assert.Equal("NotEnoughMoney", error.Error.UserErrorMessage);
            Assert.Equal(750m, (await walletStore.GetAsync(3))!.Isk);
            Assert.Empty(await store.ActiveKillRightActivationsForTargetAsync(4, _now));

            await using var journal = _source.CreateCommand(
                $"SELECT count(*) FROM {walletSchema}.journal WHERE ref_type_id = 116 AND reason = 'Kill right fee'");
            Assert.Equal(2L, (long)(await journal.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var drop = _source!.CreateCommand($"DROP SCHEMA IF EXISTS {walletSchema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}

internal sealed class CrimewatchPersistenceFake : ICrimewatchPersistenceCallbacks
{
    public Dictionary<long, List<SecurityStatusTransaction>> History { get; } = [];
    public Dictionary<(long First, long Second), long> Engagements { get; } = [];
    public List<(long First, long Second, long ExpiresAtUnixMs)> Writes { get; } = [];
    public List<(long Challenger, long Target, long Corporation, long Alliance)> ChallengesStarted { get; } = [];
    public List<(long Responder, long Challenger, long ExpiryFileTime, bool Accepted)> ChallengesAnswered { get; } = [];
    public long ChallengeExpiryFileTime { get; set; } = Py.FileTimeFromUnixMs(1_700_000_120_000);
    public bool ChallengeValid { get; set; } = true;
    private (long Challenger, long Target, long ExpiryFileTime)? _pending;

    public Task<IReadOnlyList<SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(long characterId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SecurityStatusTransaction>>(History.GetValueOrDefault(characterId) ?? []);

    public Task<long?> StartDuelChallengeAsync(
        long challengerCharacterId,
        long targetCharacterId,
        long challengerCorporationId,
        long challengerAllianceId,
        CancellationToken cancellationToken)
    {
        ChallengesStarted.Add((challengerCharacterId, targetCharacterId, challengerCorporationId, challengerAllianceId));
        if (!ChallengeValid)
        {
            return Task.FromResult<long?>(null);
        }
        _pending = (challengerCharacterId, targetCharacterId, ChallengeExpiryFileTime);
        return Task.FromResult<long?>(ChallengeExpiryFileTime);
    }

    public Task<IReadOnlyList<LimitedEngagement>> LimitedEngagementsAsync(long nowUnixMs, long? characterId, CancellationToken cancellationToken)
    {
        IReadOnlyList<LimitedEngagement> rows = [.. Engagements
            .Where(entry => entry.Value > nowUnixMs &&
                (characterId is null || entry.Key.First == characterId || entry.Key.Second == characterId))
            .OrderBy(entry => entry.Key.First)
            .ThenBy(entry => entry.Key.Second)
            .Select(entry => new LimitedEngagement(entry.Key.First, entry.Key.Second, entry.Value))];
        return Task.FromResult(rows);
    }

    public Task UpsertLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expiresAtUnixMs, CancellationToken cancellationToken)
    {
        var first = Math.Min(firstCharacterId, secondCharacterId);
        var second = Math.Max(firstCharacterId, secondCharacterId);
        Engagements[(first, second)] = expiresAtUnixMs;
        Writes.Add((first, second, expiresAtUnixMs));
        return Task.CompletedTask;
    }

    public Task<bool> ExpireLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expectedExpiresAtUnixMs, long nowUnixMs, CancellationToken cancellationToken)
    {
        var key = (Math.Min(firstCharacterId, secondCharacterId), Math.Max(firstCharacterId, secondCharacterId));
        if (Engagements.TryGetValue(key, out var expires) && expires == expectedExpiresAtUnixMs && expires <= nowUnixMs)
        {
            Engagements.Remove(key);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> RespondToDuelChallengeAsync(
        long responderCharacterId,
        long challengerCharacterId,
        long challengeExpiresAtFileTime,
        bool accept,
        long? engagementExpiresAtUnixMs,
        CancellationToken cancellationToken)
    {
        ChallengesAnswered.Add((responderCharacterId, challengerCharacterId, challengeExpiresAtFileTime, accept));
        if (!ChallengeValid || _pending is not { } pending ||
            pending.Challenger != challengerCharacterId ||
            pending.Target != responderCharacterId ||
            pending.ExpiryFileTime != challengeExpiresAtFileTime)
        {
            return Task.FromResult(false);
        }
        _pending = null; // declines also consume the pending challenge
        if (!accept || engagementExpiresAtUnixMs is not { } expiresAt)
        {
            return Task.FromResult(false);
        }
        var first = Math.Min(responderCharacterId, challengerCharacterId);
        var second = Math.Max(responderCharacterId, challengerCharacterId);
        Engagements[(first, second)] = expiresAt;
        Writes.Add((first, second, expiresAt));
        return Task.FromResult(true);
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
    private readonly CrimewatchPersistenceFake _persistence = new();
    private readonly Dictionary<long, double> _status = [];
    private readonly List<(long Character, double Value, string Reason)> _written = [];
    private readonly List<SecurityStatusPenaltyChange> _changes = [];
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
        var runtime = new CrimewatchRuntime(new PgCrimewatchStore(_source!, _schema), _sessions, () => _now, _persistence, delay: new Waits().Delay);
        var penalties = new SecurityStatusPenalties(
            runtime, _sessions,
            (id, _) => Task.FromResult(_status.GetValueOrDefault(id)),
            (request, _) => Task.FromResult<int?>(request.TargetIsCapsule ? 670 : 587),
            (change, _) =>
            {
                _status[change.CharacterId] = change.NextStatus;
                _written.Add((change.CharacterId, change.NextStatus, change.Reason));
                _changes.Add(change);
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
        var transaction = Assert.Single(_changes);
        Assert.Equal(SecurityStatusEventTypeIds.IllegalAggression, transaction.EventTypeID);
        Assert.Equal(2, transaction.OtherOwnerID);
        Assert.Equal(587, transaction.OtherTypeID);
        Assert.Equal(System, transaction.LocationID);
        Assert.Equal(-0.025, transaction.Modification, 12);
        Assert.Equal(9_988_400_003_311, transaction.TargetShipItemId);
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
        runtime.Legal = (_, _, _, _) => Task.FromResult(true);
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

    [PgFact]
    public async Task Concurrent_penalties_for_one_pilot_keep_each_distinct_ships_penalty()
    {
        var (runtime, _) = Rig();
        using var _ = runtime;
        var current = 0d;
        var sync = new object();
        var changes = new List<SecurityStatusPenaltyChange>();
        var penalties = new SecurityStatusPenalties(
            runtime, _sessions,
            async (id, _) =>
            {
                await Task.Delay(50);
                lock (sync)
                {
                    return id == 1 ? current : 0;
                }
            },
            (_, _) => Task.FromResult<int?>(587),
            (change, _) =>
            {
                lock (sync)
                {
                    current = change.NextStatus;
                    changes.Add(change);
                }
                return Task.CompletedTask;
            },
            () => _now);
        var offense = new AggressionOutcome(CombatState.Idle, AggressionReason.Criminal);

        await Task.WhenAll(
            penalties.OnAggressionAsync(new AggressionResult(Attack(ship: 9_988_400_004_001), offense)),
            penalties.OnAggressionAsync(new AggressionResult(Attack(ship: 9_988_400_004_002), offense)));

        Assert.Equal(2, changes.Count);
        Assert.Equal(0, changes[0].PreviousStatus);
        Assert.Equal(changes[0].NextStatus, changes[1].PreviousStatus);
        Assert.True(changes[1].NextStatus < changes[0].NextStatus);
    }

    [PgFact]
    public async Task Concurrent_penalties_for_the_same_ship_are_applied_once()
    {
        var (runtime, _) = Rig();
        using var _ = runtime;
        var current = 0d;
        var sync = new object();
        var written = 0;
        var writesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedWrites = 0;
        var penalties = new SecurityStatusPenalties(
            runtime, _sessions,
            (id, _) =>
            {
                lock (sync)
                {
                    return Task.FromResult(id == 1 ? current : 0);
                }
            },
            (_, _) => Task.FromResult<int?>(587),
            async (change, _) =>
            {
                if (Interlocked.Increment(ref reachedWrites) == 2)
                {
                    writesReady.TrySetResult();
                }
                await Task.WhenAny(writesReady.Task, Task.Delay(50));
                lock (sync)
                {
                    current = change.NextStatus;
                    written++;
                }
            },
            () => _now);
        var offense = new AggressionOutcome(CombatState.Idle, AggressionReason.Criminal);
        var result = new AggressionResult(Attack(), offense);

        await Task.WhenAll(
            penalties.OnAggressionAsync(result),
            penalties.OnAggressionAsync(result));

        Assert.Equal(1, written);
    }
}

internal sealed class RelationsRig : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;
    private readonly string[] _schemas;
    private readonly Eve.Scheduler.DurableScheduler _scheduler;

    private RelationsRig(NpgsqlDataSource source, string[] schemas, Eve.Scheduler.DurableScheduler scheduler, Eve.Relations.RelationStore relations)
    {
        _source = source;
        _schemas = schemas;
        _scheduler = scheduler;
        Relations = relations;
    }

    public Eve.Relations.RelationStore Relations { get; }

    public static async Task<RelationsRig> Create(NpgsqlDataSource source, Func<long> now)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        string[] schemas = ["warsch_" + id, "warevt_" + id, "warrel_" + id];
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schemas[0]);
        await Eve.Events.EventBus.MigrateAsync(source, schemas[1]);
        await Eve.Relations.RelationStore.MigrateAsync(source, schemas[2]);
        var scheduler = new Eve.Scheduler.DurableScheduler(source, now, new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, schemas[0]);
        var events = new Eve.Events.EventBus(source, scheduler, null, schemas[1]);
        var relations = new Eve.Relations.RelationStore(source, scheduler, events, schemas[2]);
        return new RelationsRig(source, schemas, scheduler, relations);
    }

    public async Task Set(string kind, long subject, long obj, object? attrs = null, long? until = null)
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Relations.SetAsync(connection, transaction, kind, subject, obj, 0, "test", attrs, until);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        await using var connection = await _source.OpenConnectionAsync();
        foreach (var schema in _schemas)
        {
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
