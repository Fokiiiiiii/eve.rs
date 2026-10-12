using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The scanner probes (EVE.js <c>Handle_LaunchProbes</c> and <c>probeRuntimeState</c>): a probe launcher puts its probes in space on a ring around the ship and the
/// pilot is told each one; the pilot recalls one; a probe whose time is up leaves at the sweep; eight is the most a pilot controls, and a launcher with too few
/// probes or none is refused.
/// </summary>
public class ScanProbeHostTests
{
    private const int ProbeLauncher = 4258, CoreProbe = 30013, HighSlots = 27;
    private const long ProbeBase = 990_000_000_000;
    private const int ProbeSkill = 3412, ProbeSkillLevel = 5;

    /// <summary>A pilot in space with one probe launcher per entry of <paramref name="probesPerLauncher"/> fitted in free high slots (online), each loaded with that many core probes (a launcher holds eight).</summary>
    private static Task<(ClientSession Session, Eve.Characters.Created Character, long[] Launchers)> LaunchersInSpaceAsync(Rig rig, params long[] probesPerLauncher) =>
        LaunchersOfAsync(rig, ProbeLauncher, CoreProbe, probesPerLauncher);

    /// <summary>The same with the launcher and the probe of the types given (the expanded launcher and the combat probe find ships).</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long[] Launchers)> LaunchersOfAsync(Rig rig, int launcherType, int probeType, params long[] probesPerLauncher)
    {
        var character = await rig.NewCharacterAsync();
        // the launcher needs its skill at level five (the SDE's requiredSkill attribute 277 on type 4258, skill 3412)
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(ProbeSkill, ProbeSkillLevel, 0)]);
            await transaction.CommitAsync();
        }
        var launchers = new long[probesPerLauncher.Length];
        for (var index = 0; index < probesPerLauncher.Length; index++)
        {
            var granted = (await rig.Ships.GrantAsync(character.CharacterId, launcherType, 1, singleton: true))!.Single().After;
            // the first high slot the ship has free (the starting ship has some of them fitted)
            var ship = await rig.Ships.LoadAsync(character.CharacterId) ?? throw new InvalidOperationException("no ship");
            var slot = Enumerable.Range(HighSlots, 8).First(flag => ship.Fitted.All(module => module.Flag != flag));
            var fitted = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, slot, 1);
            Assert.True(fitted is Eve.Ships.ShipChanged, fitted.ToString());
            Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, granted.Id, true));
            if (probesPerLauncher[index] > 0)
            {
                var stack = (await rig.Ships.GrantAsync(character.CharacterId, probeType, probesPerLauncher[index], singleton: false))!.Single().After;
                Assert.IsType<Eve.Ships.AmmoChanged>(await rig.Ships.LoadAmmoAsync(character.CharacterId, character.ShipId, [granted.Id], [stack.Id], Rig.Station));
            }
            launchers[index] = granted.Id;
        }
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (session, character, launchers);
    }

    /// <summary>A pilot in space whose one probe launcher holds <paramref name="probes"/> core probes.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long Launcher)> LauncherInSpaceAsync(Rig rig, long probes)
    {
        var (session, character, launchers) = await LaunchersInSpaceAsync(rig, probes);
        return (session, character, launchers[0]);
    }

    private static List<SimPlaceProbe> Placed(Rig rig) => rig.Sim.Heard.OfType<SimPlaceProbe>().ToList();

    /// <summary>The loaded probes of the launcher: the quantity of the charge in it.</summary>
    private static async Task<long> LoadedAsync(Rig rig, Eve.Characters.Created character, long launcher)
    {
        var ship = await rig.Ships.LoadAsync(character.CharacterId) ?? throw new InvalidOperationException("no ship");
        return ship.ChargeOf(ship.Fitted.Single(m => m.Id == launcher))?.Quantity ?? 0;
    }

    /// <summary>Every text inside a wrapped error's payload, joined (the refusal's words).</summary>
    private static string Texts(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyBytes text => System.Text.Encoding.UTF8.GetString(text.Value.Span),
        PyTuple tuple => string.Join("|", tuple.Items.Select(Texts)),
        PyList list => string.Join("|", list.Items.Select(Texts)),
        PyDict dict => string.Join("|", dict.Entries.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) })),
        PyObjectEx ex => string.Join("|", new[] { Texts(ex.Header) }.Concat(ex.List.Select(Texts)).Concat(ex.Dict.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) }))),
        PyObject obj => Texts(obj.Args),
        _ => "",
    };

    /// <summary>A field of a probe's key-value (the <c>util.KeyVal</c> the client is told).</summary>
    private static PyValue Field(PyValue keyVal, string name) =>
        ((PyDict)((PyObject)keyVal).Args).Entries.Single(e => JsSemantics.StringValue(e.Key) == name).Value;

    [PgFact]
    public async Task A_system_whose_client_flags_forbid_scanning_refuses_the_launch_and_places_no_probe()
    {
        // the rig's station (60015249) is in system 30100032; the client's disallowScanning flag is set for that system here
        await using var rig = await Rig.CreateAsync(clientFlags: new Dictionary<int, Eve.Universe.ClientSystemFlags> { [30100032] = new(false, true) });
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 8);

        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.LaunchAsync(session, launcher, 1, CancellationToken.None));

        Assert.Empty(Placed(rig));
    }

    [PgFact]
    public async Task A_launcher_puts_its_probes_in_space_on_a_ring_around_the_ship_and_the_pilot_is_told_each_one()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, launcher) = await LauncherInSpaceAsync(rig, probes: 8);

        await rig.Space.LaunchAsync(session, launcher, 3, CancellationToken.None);

        var placed = Placed(rig);
        Assert.Equal(new[] { ProbeBase + 1, ProbeBase + 2, ProbeBase + 3 }, placed.Select(p => p.ProbeId).ToArray());
        foreach (var probe in placed)
        {
            Assert.Equal(0, probe.Position[1], 6);
            Assert.Equal(10_000, Math.Sqrt((probe.Position[0] * probe.Position[0]) + (probe.Position[2] * probe.Position[2])), 6);
            Assert.Equal(CoreProbe, probe.TypeId);
        }
        var told = Events.Of(session).Where(e => e.Name == "OnNewProbe").ToList();
        Assert.Equal(placed.Select(p => p.ProbeId).ToArray(), told.Select(e => Events.Number(Field(e.Args[0], "probeID"))).ToArray());
        Assert.Equal(5, await LoadedAsync(rig, character, launcher));
    }

    [PgFact]
    public async Task A_probe_the_pilot_recalls_leaves_space_and_the_pilot_is_told()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None);
        var probe = Placed(rig)[0].ProbeId;
        Events.Of(session);
        rig.Sim.Heard.Clear();

        await rig.Space.RecallAsync(session, probe, CancellationToken.None);

        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveProbe removed && removed.ProbeId == probe);
        Assert.Equal(probe, Events.Number(Events.Of(session).Single(e => e.Name == "OnRemoveProbe").Args[0]));
        Assert.Null(await rig.Probes.GetAsync(probe));
    }

    [PgFact]
    public async Task A_probe_whose_time_is_up_leaves_space_at_the_sweep_and_one_still_in_time_stays()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 1, CancellationToken.None);
        var probe = Placed(rig)[0].ProbeId;
        Events.Of(session);
        rig.Sim.Heard.Clear();

        // the core probe lives four thousand seconds: still there just before, gone at the sweep after
        rig.Now += 3_999_000;
        await rig.Space.Probes!.SweepAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimRemoveProbe);
        Assert.NotNull(await rig.Probes.GetAsync(probe));

        rig.Now += 2_000;
        await rig.Space.Probes!.SweepAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveProbe removed && removed.ProbeId == probe);
        Assert.Null(await rig.Probes.GetAsync(probe));
        Assert.Equal(probe, Events.Number(Events.Of(session).Single(e => e.Name == "OnRemoveProbe").Args[0]));
    }

    [PgFact]
    public async Task The_pilot_controls_at_most_eight_probes_and_a_launcher_with_too_few_probes_refuses()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        var system = (int)session.GetInt("solarsystemid2")!.Value;

        // the restore the entry started is done before the record is seeded (the seeding is not part of what the entry restores)
        await rig.Space.Probes!.RestoreAsync(system, CancellationToken.None);
        // one probe of the pilot is already active in this system (a record the cap counts; the sim holds no ball for it)
        await rig.Probes.SaveAsync(new ScanProbeRecord(
            ProbeBase + 999, character.CharacterId, system, CoreProbe, CharacterShips.ScanProbeGroup, launcher, character.ShipId,
            [0, 0, 0], [0, 0, 0], 0, 7, 1, Rig.ClockMs + 3_600_000));

        // eight would make nine: refused as the cap, though the launcher has eight
        var over = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.LaunchAsync(session, launcher, 8, CancellationToken.None));
        Assert.Contains("You cannot control more than eight active probes.", Texts(over.Error.Payload));
        Assert.Equal(8, await LoadedAsync(rig, character, launcher));

        // seven make eight: accepted, and one is left in the launcher
        await rig.Space.LaunchAsync(session, launcher, 7, CancellationToken.None);
        // the record saved above may also be put back in space by the arrival's restore, which runs in the background: only the launched probes are counted
        Assert.Equal(7, Placed(rig).Count(placed => placed.ProbeId != ProbeBase + 999));
        Assert.Equal(1, await LoadedAsync(rig, character, launcher));

        // two are asked for and one is left: refused as not enough
        var shortStack = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None));
        Assert.Contains("You do not have enough loaded scanner probes.", Texts(shortStack.Error.Payload));

        // a recall of one of the pilot's probes makes room under the cap again: the last one goes out
        await rig.Space.RecallAsync(session, ProbeBase + 999, CancellationToken.None);
        await rig.Space.LaunchAsync(session, launcher, 1, CancellationToken.None);
        Assert.Equal(8, Placed(rig).Count);
    }

    [PgFact]
    public async Task A_launcher_with_no_probes_loaded_refuses_the_launch()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 0);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.LaunchAsync(session, launcher, 1, CancellationToken.None));
        Assert.Equal("NoCharges", refused.Error.UserErrorMessage);
        Assert.Empty(Placed(rig));
    }

    [PgFact]
    public async Task A_probe_still_in_its_life_is_put_back_in_space_with_its_id_and_place_when_its_system_is_entered_again()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        var launched = Placed(rig).ToDictionary(p => p.ProbeId, p => p.Position);
        var expiry = (await rig.Probes.GetAsync(launched.Keys.First()))!.ExpiresAtMs;

        // a restart: the balls are gone from the simulation, the records are still in the store
        rig.Sim.Heard.Clear();
        rig.Now += 1_000;
        await rig.Space.Probes!.RestoreAsync(system, CancellationToken.None);

        var again = Placed(rig);
        Assert.Equal(launched.Keys.OrderBy(id => id), again.Select(p => p.ProbeId).OrderBy(id => id));
        foreach (var probe in again)
        {
            Assert.Equal(launched[probe.ProbeId], probe.Position);
            Assert.Equal(CoreProbe, probe.TypeId);
            Assert.Equal(system, probe.SolarSystemId);
        }
        Assert.Equal(expiry, (await rig.Probes.GetAsync(launched.Keys.First()))!.ExpiresAtMs);
    }

    [PgFact]
    public async Task A_probe_whose_time_is_up_is_not_put_back_in_space()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 1, CancellationToken.None);
        var system = (int)session.GetInt("solarsystemid2")!.Value;

        rig.Sim.Heard.Clear();
        rig.Now += 4_001_000;
        await rig.Space.Probes!.RestoreAsync(system, CancellationToken.None);

        Assert.Empty(Placed(rig));
    }

    /// <summary>
    /// A launch in a system waits for the restore that the system's entry started, while that restore has read the records but not yet placed them (the per-system gate
    /// of SpaceProbes). The test holds the restore there; a launch that does not wait places its balls first, so the test sees them placed before the restore is let on.
    /// </summary>
    [PgFact]
    public async Task A_launch_waits_while_the_restore_of_its_system_has_read_the_records_but_not_yet_placed_them()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        await rig.Space.Probes!.RestoreAsync(system, CancellationToken.None);
        // a record of a probe that has no ball (as after a restart)
        await rig.Probes.SaveAsync(new ScanProbeRecord(
            ProbeBase + 500, character.CharacterId, system, CoreProbe, CharacterShips.ScanProbeGroup, launcher, character.ShipId,
            [0, 0, 0], [0, 0, 0], 0, 7, 1, Rig.ClockMs + 3_600_000));
        Events.Of(session);
        rig.Sim.Heard.Clear();

        // the restore of the next entry stops after it has read the record, until the test lets it go on
        var read = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Space.Probes!.AfterRead = async () =>
        {
            read.TrySetResult();
            await release.Task;
        };
        var other = await rig.NewCharacterAsync("Second Tester");
        var otherSession = await rig.SessionAsync(other, 2);
        var otherOid = await rig.BindShipAsync(otherSession);
        await rig.ShipCallAsync(otherSession, "Undock", otherOid, new PyLong(otherSession.GetInt("shipid")!.Value), PyBool.False);
        await read.Task;

        var launch = rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None);
        await Task.WhenAny(launch, Task.Delay(2_000));
        Assert.False(launch.IsCompleted, "the launch waits for the restore of its system");
        Assert.Empty(Placed(rig));

        release.SetResult();
        await launch;
        // (an entry may restore a record more than once, each send is kept once by the simulation: the order of the first placements is what counts)
        Assert.Equal(new[] { ProbeBase + 500, ProbeBase + 1, ProbeBase + 2 }, Placed(rig).Select(p => p.ProbeId).Distinct().ToArray());
    }
    /// <summary>The client's request for a scan: each probe with where it is to go and its range step (its fields as the client's probe key-values carry them).</summary>
    private static PyValue ScanRequest(params (long Probe, double[] Destination, int RangeStep)[] probes) => new PyDict([.. probes.Select(p =>
        new KeyValuePair<PyValue, PyValue>(new PyLong(p.Probe), Py.KeyVal(
            ("destination", new PyList([new PyReal(p.Destination[0]), new PyReal(p.Destination[1]), new PyReal(p.Destination[2])])),
            ("rangeStep", new PyInt(p.RangeStep)))))]);

    private static double Real(PyValue value) => value switch
    {
        PyReal r => r.Value,
        PyInt i => i.Value,
        PyLong l => l.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };

    private static IReadOnlyList<PyValue> Items(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => throw new InvalidOperationException(value.ToString()),
    };

    private static double[] Vector(PyValue value) => Items(value).Select(Real).ToArray();

    /// <summary>The stop of a scan: the results the pilot was told (each a client key-value) and the absent targets.</summary>
    private static (IReadOnlyList<PyValue> Results, IReadOnlyList<long> Absent) ResultsTold(IReadOnlyList<Events.Event> events)
    {
        var stop = Assert.Single(events, e => e.Name == "OnSystemScanStopped");
        return (Items(stop.Args[1]), Items(stop.Args[2]).Select(id => Events.Number(id)).ToArray());
    }

    /// <summary>A ship the scene reports to a probe scan: the Probe frigate (type 586) at the position given, not cloaked.</summary>
    private static SimScanTarget ProbeFrigate(long id, double[] position) => new(id, 1, 586, position);

    private const long ShipInSystem = 980050000077, FarShip = 980050000078;

    [PgFact]
    public async Task A_scan_of_four_spread_probes_finds_a_ship_at_100_percent_and_the_pilot_is_told_once_the_probes_have_moved_and_the_scan_is_over()
    {
        await using var rig = await Rig.CreateAsync();
        // the expanded launcher with combat probes: only they find a ship
        var (session, character, launchers) = await LaunchersOfAsync(rig, 18_639, 30_028, 8);
        var launcher = launchers[0];
        await rig.Space.LaunchAsync(session, launcher, 4, CancellationToken.None);
        var probes = Placed(rig).Select(p => p.ProbeId).ToArray();
        rig.Sim.ScanTargetsAnswer = [ProbeFrigate(ShipInSystem, [0, 0, 0])];
        // the four probes are sent apart, a tenth of an astronomical unit each way: they spread across the scan range
        double[][] spread = [[1e10, 0, 1e10], [-1e10, 0, 1e10], [-1e10, 0, -1e10], [1e10, 0, -1e10]];
        Events.Of(session);
        rig.Sim.Heard.Clear();

        await rig.ProbeScans.RequestScansAsync(session, ScanRequest(probes.Select((id, i) => (id, spread[i], 1)).ToArray()), CancellationToken.None);

        var moving = Events.Of(session);
        Assert.Equal(4, moving.Count(e => e.Name == "OnProbeWarpStart"));
        Assert.DoesNotContain(moving, e => e.Name == "OnSystemScanStarted");
        Assert.All(probes, id => Assert.Equal(4, rig.Probes.GetAsync(id).Result!.State));

        rig.Now += 1_250;
        await rig.Scheduler.RunDueAsync();
        var started = Events.Of(session);
        Assert.Equal(4, started.Count(e => e.Name == "OnProbeWarpEnd"));
        var begun = Assert.Single(started, e => e.Name == "OnSystemScanStarted");
        // the launcher's ten seconds, times each scanning skill's duration bonus (SDE attribute 66: -5 a level on Astrometrics and on Acquisition)
        var levels = await rig.Skills.ForCharacterAsync(character.CharacterId);
        var expected = levels.Where(skill => skill.TypeId is 3412 or 25811).Aggregate(10_000.0, (duration, skill) => duration * (1 - 0.05 * skill.Level));
        Assert.Equal(expected, Events.Number(begun.Args[1]));

        rig.Now += 7_500;
        await rig.Scheduler.RunDueAsync();
        var (results, absent) = ResultsTold(Events.Of(session));
        Assert.Empty(absent);
        var row = Assert.Single(results, r => Events.Number(Field(r, "id")) == ShipInSystem);
        Assert.Equal(1.0, Real(Field(row, "certainty")), 6);
        Assert.Equal(586, Events.Number(Field(row, "typeID")));
        Assert.Equal(new double[] { 0, 0, 0 }, Vector(Field(row, "pos")));
        Assert.Equal(new double[] { 0, 0, 0 }, Vector(Field(row, "data")));
        Assert.All(probes, id => Assert.Equal(1, rig.Probes.GetAsync(id).Result!.State));
    }

    [PgFact]
    public async Task Core_probes_find_no_ship_so_both_ships_are_absent_from_the_results()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None);
        var probes = Placed(rig).Select(p => p.ProbeId).ToArray();
        // the core probes scan cosmic signatures only (wiki: "Combat scanner probes can find structures, ships, and drones"); the ships are out of reach for them
        rig.Sim.ScanTargetsAnswer = [ProbeFrigate(ShipInSystem, [3_000.0, 0, -4_000.0]), ProbeFrigate(FarShip, [1e13, 0, 0])];
        Events.Of(session);

        await rig.ProbeScans.RequestScansAsync(session, ScanRequest(probes.Select(id => (id, Placed(rig).Single(p => p.ProbeId == id).Position.ToArray(), 1)).ToArray()), CancellationToken.None);
        rig.Now += 7_500;
        await rig.Scheduler.RunDueAsync();
        var (results, absent) = ResultsTold(Events.Of(session));
        Assert.Empty(results);
        Assert.Equal(new long[] { ShipInSystem, FarShip }, absent.OrderBy(id => id).ToArray());
    }

    [PgFact]
    public async Task Combat_probes_give_a_group_result_placed_within_the_deviation_and_clear_the_ship_out_of_reach()
    {
        await using var rig = await Rig.CreateAsync();
        // the expanded launcher (18639) with combat probes (30028, SDE attribute 1413: probeCanScanShips)
        var (session, _, launchers) = await LaunchersOfAsync(rig, 18_639, 30_028, 2);
        await rig.Space.LaunchAsync(session, launchers[0], 2, CancellationToken.None);
        var probes = Placed(rig).Select(p => p.ProbeId).ToArray();
        var near = new[] { 3_000.0, 0, -4_000.0 };
        rig.Sim.ScanTargetsAnswer = [ProbeFrigate(ShipInSystem, near), ProbeFrigate(FarShip, [1e13, 0, 0])];
        Events.Of(session);

        await rig.ProbeScans.RequestScansAsync(session, ScanRequest(probes.Select(id => (id, Placed(rig).Single(p => p.ProbeId == id).Position.ToArray(), 1)).ToArray()), CancellationToken.None);
        rig.Now += 7_500;
        await rig.Scheduler.RunDueAsync();
        var (results, absent) = ResultsTold(Events.Of(session));
        Assert.Equal(new long[] { FarShip }, absent);
        var row = Assert.Single(results, r => Events.Number(Field(r, "id")) == ShipInSystem);
        // two probes give a circle, below half: a result below the name's tier names only the group
        var certainty = Real(Field(row, "certainty"));
        Assert.InRange(certainty, 0.25, 0.4991);
        Assert.Equal(PyNone.Instance, Field(row, "typeID"));
        Assert.Equal(25, Events.Number(Field(row, "groupID")));
        // the largest deviation: 0.25 AU at the combat probe's base range of 0.5 AU, times Astrometrics five's 0.75
        var deviation = Real(Field(row, "data"));
        Assert.True(Math.Abs(deviation - 0.25 * 0.75 * 149_597_870_700.0) < 1_000, $"the deviation is {deviation} m");
        var position = Vector(Field(row, "pos"));
        var offset = Math.Sqrt(Math.Pow(position[0] - near[0], 2) + Math.Pow(position[1] - near[1], 2) + Math.Pow(position[2] - near[2], 2));
        Assert.True(offset <= deviation + 1e-3, $"the result is {offset} m off, the deviation is {deviation} m");
        Assert.True(offset > 0);
    }

    [PgFact]
    public async Task A_second_scan_asked_while_one_runs_is_not_started_and_a_probe_that_is_not_idle_is_left_out()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None);
        var probes = Placed(rig).Select(p => p.ProbeId).ToArray();
        rig.Sim.ScanTargetsAnswer = [ProbeFrigate(ShipInSystem, [0, 0, 0])];
        var request = ScanRequest(probes.Select(id => (id, Placed(rig).Single(p => p.ProbeId == id).Position.ToArray(), 1)).ToArray());
        Events.Of(session);

        await rig.ProbeScans.RequestScansAsync(session, request, CancellationToken.None);
        await rig.ProbeScans.RequestScansAsync(session, request, CancellationToken.None);
        Assert.Single(Events.Of(session), e => e.Name == "OnSystemScanStarted");

        rig.Now += 7_500;
        await rig.Scheduler.RunDueAsync();
        Assert.Single(Events.Of(session), e => e.Name == "OnSystemScanStopped");
        // the probes are idle again: a probe asked to stay where it is starts a scan at once
        await rig.ProbeScans.RequestScansAsync(session, ScanRequest((probes[0], Placed(rig).Single(p => p.ProbeId == probes[0]).Position.ToArray(), 1)), CancellationToken.None);
        Assert.Single(Events.Of(session), e => e.Name == "OnSystemScanStarted");
    }

    [PgFact]
    public async Task Recovered_probes_go_back_into_the_launcher_they_came_from_and_the_pilot_is_told_each_one_that_left()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 3, CancellationToken.None);
        var two = Placed(rig).Select(p => p.ProbeId).Take(2).ToArray();
        Assert.Equal(5, await LoadedAsync(rig, character, launcher));
        Events.Of(session);

        var left = await rig.Space.Probes!.RecoverAsync(session, two, CancellationToken.None);

        Assert.Equal(two, left.ToArray());
        Assert.Equal(7, await LoadedAsync(rig, character, launcher));
        var told = Events.Of(session).Where(e => e.Name == "OnRemoveProbe").Select(e => Events.Number(e.Args[0])).ToArray();
        Assert.Equal(two.Select(id => (long)id).ToArray(), told);
        Assert.All(two, id => Assert.Null(rig.Probes.GetAsync(id).Result));
        Assert.Equal(two.Length, rig.Sim.Heard.OfType<SimRemoveProbe>().Count());
    }

    [PgFact]
    public async Task Probes_that_do_not_fit_in_the_launcher_go_to_the_cargo_hold_as_a_stack()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 3, CancellationToken.None);
        Assert.Equal(5, await LoadedAsync(rig, character, launcher));
        var moduleId = launcher;
        var ship = character.ShipId;

        // four come back and the launcher has room for three: the fourth goes to the hold (a stack of one, the stack of its type joins)
        var changes = await rig.Ships.RestoreProbesAsync(character.CharacterId, ship, moduleId, CoreProbe, 4);
        Assert.Equal(8, await LoadedAsync(rig, character, launcher));
        var hold = (await rig.Items.InContainerAsync(ship, CancellationToken.None)).Where(i => i.Flag == Eve.Inventory.ItemFlags.Cargo).ToList();
        var stack = Assert.Single(hold);
        Assert.Equal((CoreProbe, 1L), (stack.TypeId, stack.Quantity));
        Assert.Equal(2, changes.Count);
        Assert.Equal(1, changes[1].After.Quantity);

        // a second one for the hold joins the stack there
        await rig.Ships.RestoreProbesAsync(character.CharacterId, ship, null, CoreProbe, 2);
        var grown = Assert.Single(await rig.Items.InContainerAsync(ship, CancellationToken.None), i => i.Flag == Eve.Inventory.ItemFlags.Cargo);
        Assert.Equal(3, grown.Quantity);
    }

    [PgFact]
    public async Task Reconnecting_tells_the_pilot_each_probe_as_new_and_then_all_idle()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None);
        var probes = Placed(rig).Select(p => p.ProbeId).ToArray();
        Events.Of(session);

        await rig.Space.Probes!.ReconnectAsync(session, CancellationToken.None);

        var told = Events.Of(session);
        Assert.Equal(probes, told.Where(e => e.Name == "OnNewProbe").Select(e => Events.Number(Field(e.Args[0], "probeID"))).ToArray());
        var idle = Assert.Single(told, e => e.Name == "OnProbesIdle");
        Assert.Equal(probes.Length, Items(idle.Args[0]).Count);
        // the order of the notifications: each probe as new, then the idle list
        Assert.Equal("OnProbesIdle", told[^1].Name);
    }

    [PgFact]
    public async Task After_a_restart_the_probes_are_still_recorded_and_can_be_reconnected_and_recovered()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, launcher) = await LauncherInSpaceAsync(rig, probes: 8);
        await rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None);
        var probes = Placed(rig).Select(p => p.ProbeId).ToArray();
        Assert.Equal(6, await LoadedAsync(rig, character, launcher));

        // a restart: the host's services are built again over the same records; the simulation has nothing of the balls
        var restarted = new Eve.Services.Space.SpaceProbes(rig.Ships, rig.Statics, rig.Probes, rig.Space, rig.Sim, () => rig.Now);
        rig.Sim.Heard.Clear();
        await restarted.RestoreAsync((int)session.GetInt("solarsystemid2")!.Value);
        Assert.Equal(probes, Placed(rig).Select(p => p.ProbeId).ToArray());

        Events.Of(session);
        await restarted.ReconnectAsync(session, CancellationToken.None);
        Assert.Equal(probes.Length, Events.Of(session).Count(e => e.Name == "OnNewProbe"));

        await restarted.RecoverAsync(session, probes, CancellationToken.None);
        Assert.Equal(8, await LoadedAsync(rig, character, launcher));
        Assert.Null(await rig.Probes.GetAsync(probes[0]));
    }

    /// <summary>Four combat probes spread apart and scanned to the end: ShipInSystem is found at 100 percent, at the origin.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character)> SpreadScanAsync(Rig rig)
    {
        var (session, character, launchers) = await LaunchersOfAsync(rig, 18_639, 30_028, 8);
        await rig.Space.LaunchAsync(session, launchers[0], 4, CancellationToken.None);
        var probes = Placed(rig).Select(p => p.ProbeId).ToArray();
        rig.Sim.ScanTargetsAnswer = [ProbeFrigate(ShipInSystem, [0, 0, 0])];
        double[][] spread = [[1e10, 0, 1e10], [-1e10, 0, 1e10], [-1e10, 0, -1e10], [1e10, 0, -1e10]];
        await rig.ProbeScans.RequestScansAsync(session, ScanRequest(probes.Select((id, i) => (id, spread[i], 1)).ToArray()), CancellationToken.None);
        rig.Now += 1_250;
        await rig.Scheduler.RunDueAsync();
        rig.Now += 7_500;
        await rig.Scheduler.RunDueAsync();
        return (session, character);
    }

    [PgFact]
    public async Task A_warp_to_a_scan_result_goes_to_the_exact_place_the_last_completed_scan_found_at_100_percent()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = await SpreadScanAsync(rig);
        var oid = await rig.BindBeyonceAsync(session);
        var before = rig.Sim.Heard.Count;

        await rig.BeyonceCallWithAsync(session, "CmdWarpToStuff", oid, [new PyStr("scan"), new PyLong(ShipInSystem)], ("minRange", new PyInt(5_000)));

        var warp = Assert.Single(rig.Sim.Heard.Skip(before).OfType<SimCommand>());
        Assert.Equal(SimCommands.WarpToPoint, warp.Code);
        Assert.Equal(new double[] { 0, 0, 0, 5_000 }, warp.Args.ToArray());
    }

    [PgFact]
    public async Task A_warp_to_a_scan_result_is_refused_for_a_target_no_completed_scan_found_at_100_percent()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = await SpreadScanAsync(rig);
        var oid = await rig.BindBeyonceAsync(session);
        // a target the scan did not find, and the same target after a scan that found nothing: both are refused
        await Assert.ThrowsAnyAsync<Exception>(() => rig.BeyonceCallWithAsync(session, "CmdWarpToStuff", oid, [new PyStr("scan"), new PyLong(FarShip)]));

        var probes = Placed(rig).Select(p => p.ProbeId).Take(4).ToArray();
        var far = ScanRequest(probes.Select(id => (id, new[] { 1e13, 0, 1e13 }, 1)).ToArray());
        await rig.ProbeScans.RequestScansAsync(session, far, CancellationToken.None);
        rig.Now += 1_250;
        await rig.Scheduler.RunDueAsync();
        rig.Now += 7_500;
        await rig.Scheduler.RunDueAsync();
        Assert.Contains(Events.Of(session), e => e.Name == "OnSystemScanStopped");

        await Assert.ThrowsAnyAsync<Exception>(() => rig.BeyonceCallWithAsync(session, "CmdWarpToStuff", oid, [new PyStr("scan"), new PyLong(ShipInSystem)]));
    }

    private static SimDirectionalTarget Thing(long id, byte kind, int type, double x, double y, double z, double radius = 40, bool cloaked = false, bool warping = false) =>
        new(id, kind, type, [x, y, z], radius, cloaked, warping);

    /// <summary>What a cone of the scan finds: the ids of its rows, in the order the client is told them.</summary>
    private static async Task<IReadOnlyList<long>> ConeAsync(Rig rig, ClientSession session, double angle, double range, double x, double y, double z)
    {
        var answer = await rig.Space.DirectionalScans!.ConeScanAsync(session, new PyReal(angle), new PyReal(range), new PyReal(x), new PyReal(y), new PyReal(z), CancellationToken.None);
        return Items(answer).Select(row => Events.Number(Field(row, "id"))).ToArray();
    }

    /// <summary>The scene the directional tests scan: on the axis and off it, in range and out, and the kinds the scan does and does not show.</summary>
    private static SimDirectionalTarget[] Scene() =>
    [
        Thing(1001, 1, 586, 1e9, 0, 0),
        Thing(1002, 1, 586, -1e9, 0, 0),
        Thing(1003, 1, 586, 5e9, 5e9, 0),
        Thing(1004, 1, 586, 2e9, 0, 0, cloaked: true),
        Thing(1005, 1, 586, 3e9, 0, 0, warping: true),
        Thing(1006, 1, 11959, 4e9, 0, 0),
        Thing(1007, 2, 586, 2e9, 0, 0),
        Thing(1008, 1, 586, 1e13, 0, 0),
        Thing(1009, 3, 2454, 0, 4e8, 0, 5),
        Thing(1010, 5, 30013, 5e8, 0, 0, 1),
        Thing(1011, 7, 11, 0, 0, 2e10, 3e6),
        Thing(1012, 7, 12242, 1e10, 0, 0, 5e3),
        Thing(1013, 6, 53025, 7e8, 0, 0, 20),
    ];

    [PgFact]
    public async Task A_narrow_cone_shows_what_it_points_at_in_range_and_nothing_else_and_nearest_first()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, _) = await LauncherInSpaceAsync(rig, probes: 0);
        rig.Sim.DirectionalAnswer = Scene();

        // thirty degrees either side of the ship's heading (+x): the probe, the wreck, the frigate and the station are on it
        var found = await ConeAsync(rig, session, Math.PI / 6, 14.3 * 149_597_870_700.0, 1, 0, 0);

        Assert.Equal(new long[] { 1010, 1013, 1001, 1012 }, found);
    }

    [PgFact]
    public async Task A_sphere_shows_every_visible_thing_in_range_and_not_the_cloaked_warping_npc_combat_recon_or_out_of_range_ones()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, _) = await LauncherInSpaceAsync(rig, probes: 0);
        rig.Sim.DirectionalAnswer = Scene();

        var found = await ConeAsync(rig, session, Math.PI * 2, 14.3 * 149_597_870_700.0, 1, 0, 0);

        Assert.Equal(new long[] { 1001, 1002, 1003, 1009, 1010, 1011, 1012, 1013 }.OrderBy(id => id), found.OrderBy(id => id));
    }

    [PgFact]
    public async Task The_range_of_the_scan_bounds_what_a_cone_finds_and_a_scan_with_no_direction_finds_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, _) = await LauncherInSpaceAsync(rig, probes: 0);
        rig.Sim.DirectionalAnswer = Scene();

        // half a billion metres: the probe at 5e8 and the drone at 4e8 are in it; the frigate at 1e9 and the wreck at 7e8 are not
        var near = await ConeAsync(rig, session, Math.PI * 2, 5e8, 1, 0, 0);
        Assert.Equal(new long[] { 1009, 1010 }, near.OrderBy(id => id).ToArray());

        Assert.Empty(await ConeAsync(rig, session, Math.PI * 2, 5e8, 0, 0, 0));
    }

    [PgFact]
    public async Task The_rows_name_the_object_by_its_item_type_and_group_only()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, _) = await LauncherInSpaceAsync(rig, probes: 0);
        rig.Sim.DirectionalAnswer = [Thing(1010, 5, 30013, 5e8, 0, 0, 1)];

        var answer = await rig.Space.DirectionalScans!.ConeScanAsync(session, new PyReal(Math.PI * 2), new PyReal(5e9), new PyReal(1), new PyReal(0), new PyReal(0), CancellationToken.None);

        var row = Assert.Single(Items(answer));
        Assert.Equal(new long[] { 1010 }, new[] { Events.Number(Field(row, "id")) });
        Assert.Equal(30013, Events.Number(Field(row, "typeID")));
        Assert.Equal(479, Events.Number(Field(row, "groupID")));
    }
}
