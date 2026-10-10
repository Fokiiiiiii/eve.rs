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
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long[] Launchers)> LaunchersInSpaceAsync(Rig rig, params long[] probesPerLauncher)
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
            var granted = (await rig.Ships.GrantAsync(character.CharacterId, ProbeLauncher, 1, singleton: true))!.Single().After;
            // the first high slot the ship has free (the starting ship has some of them fitted)
            var ship = await rig.Ships.LoadAsync(character.CharacterId) ?? throw new InvalidOperationException("no ship");
            var slot = Enumerable.Range(HighSlots, 8).First(flag => ship.Fitted.All(module => module.Flag != flag));
            var fitted = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, slot, 1);
            Assert.True(fitted is Eve.Ships.ShipChanged, fitted.ToString());
            Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, granted.Id, true));
            if (probesPerLauncher[index] > 0)
            {
                var stack = (await rig.Ships.GrantAsync(character.CharacterId, CoreProbe, probesPerLauncher[index], singleton: false))!.Single().After;
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
        Assert.Equal(7, Placed(rig).Count);
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
    /// A launch that follows a system's entry waits for the restore the entry starts (the per-system gate of SpaceProbes): each probe is placed once and in the order
    /// it was made. Without the gate the restore of the record can land after the launch's balls, or beside them.
    /// </summary>
    [PgFact]
    public async Task A_launch_right_after_a_system_is_entered_waits_for_the_restore_so_each_probe_is_placed_once_and_in_order()
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

        // a second pilot enters the system (its entry restores the record), and the first pilot launches at once
        var other = await rig.NewCharacterAsync("Second Tester");
        var otherSession = await rig.SessionAsync(other, 2);
        var otherOid = await rig.BindShipAsync(otherSession);
        await rig.ShipCallAsync(otherSession, "Undock", otherOid, new PyLong(otherSession.GetInt("shipid")!.Value), PyBool.False);
        await rig.Space.LaunchAsync(session, launcher, 2, CancellationToken.None);

        // the restored probe comes first and each launched probe is placed once; the entry's restore may send the restored probe again (the simulation keeps one)
        Assert.Equal(new[] { ProbeBase + 500, ProbeBase + 1, ProbeBase + 2 }, Placed(rig).Select(p => p.ProbeId).Distinct().ToArray());
    }
}
