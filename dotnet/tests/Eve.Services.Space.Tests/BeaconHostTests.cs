using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The Mobile Cynosural Beacon (SDE group 4093): a pilot drops one from the hold 2,500 m ahead of the ship; it is anchored after its activation time (two minutes, a
/// covert one a minute: EveJS); it lives an hour (thirty minutes covert) and leaves with its item; a standard or covert one cannot be dropped at 0.45 security or
/// above, the highsec authorized one can (its own SDE type); a pilot without the SDE's skills cannot drop one; a restart puts it back with the state its record gives.
/// </summary>
public class BeaconHostTests
{
    private const int Beacon = 57319, HighsecBeacon = 58906, CovertBeacon = 59630;
    private const int Anchoring = 11584, CynosuralFieldTheory = 21603, Polaris = 9955;
    /// <summary>A null-security system (security -1.0, RU-97T) outside Pochven, where a standard beacon may be dropped.</summary>
    private const long NullSecSystem = 30000377;
    /// <summary>Kuharah (SDE region 10000070, Pochven): a beacon cannot be lit there (the wiki's region-wide jammer).</summary>
    private const long PochvenSystem = 30000021;
    /// <summary>A wormhole system (SDE region 11000033): a beacon may be lit there (the wiki), though no jump drive may enter it.</summary>
    private const long WormholeSystem = 31000001;
    /// <summary>Jita (security 0.9): a standard or covert beacon may not be dropped there.</summary>
    private const long HighSecSystem = 30000142;

    /// <summary>A pilot with <paramref name="count"/> beacons of <paramref name="type"/> in its hold, in space in <paramref name="system"/>; it has the skills the SDE asks
    /// for the type unless <paramref name="skilled"/> is false.</summary>
    private static async Task<(ClientSession Session, long[] Items)> PilotWithBeaconsAsync(Rig rig, int type, int count, long system, bool skilled = true, string name = "Beacon Pilot", long sessionId = 1)
    {
        var character = await rig.NewCharacterAsync(name);
        if (skilled)
        {
            var skills = type switch
            {
                CovertBeacon => new[] { new Eve.Skills.CharacterSkill(Anchoring, 5, 0), new Eve.Skills.CharacterSkill(CynosuralFieldTheory, 5, 0) },
                HighsecBeacon => new[] { new Eve.Skills.CharacterSkill(Anchoring, 3, 0), new Eve.Skills.CharacterSkill(Polaris, 5, 0) },
                _ => new[] { new Eve.Skills.CharacterSkill(Anchoring, 3, 0) },
            };
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, skills);
            await transaction.CommitAsync();
        }
        var items = new long[count];
        for (var index = 0; index < count; index++)
        {
            var granted = (await rig.Ships.GrantAsync(character.CharacterId, type, 1, singleton: true))!.Single().After;
            var moved = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, CharacterShips.CargoFlag, 1);
            Assert.True(moved is Eve.Ships.ShipChanged, moved.ToString());
            items[index] = granted.Id;
        }

        var session = await rig.SessionAsync(character, sessionId);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        session.Set("solarsystemid2", new PyInt(system));
        return (session, items);
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

    [PgFact]
    public async Task A_dropped_standard_beacon_is_anchored_two_minutes_later_and_leaves_with_its_item_an_hour_after_the_drop()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithBeaconsAsync(rig, Beacon, 1, NullSecSystem);
        var beacon = items[0];
        var launchedAt = rig.Now;

        var placed = await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);

        Assert.Equal(beacon, placed);
        var ball = Assert.Single(rig.Sim.Heard.OfType<SimPlaceCynoBeacon>());
        Assert.False(ball.Active, "a beacon is not anchored when it is dropped");
        Assert.Equal(100.0, ball.Radius);
        Assert.Equal(new[] { 2_500.0, 0.0, 0.0 }, ball.Position.ToArray());
        var record = (await rig.Deployed.GetAsync(beacon))!;
        Assert.Equal("mobile_cyno_beacon", record.Kind);
        Assert.Equal(launchedAt + 120_000, record.ActivateAtMs);

        rig.Sim.Heard.Clear();
        rig.Now += 119_000;
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive);
        rig.Now += 2_000;
        await rig.Scheduler.RunDueAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimSetDisruptorActive anchored && anchored.DeployableId == beacon && anchored.Active);

        rig.Sim.Heard.Clear();
        rig.Now += 60L * 60 * 1000 - 121_000 - 2_000;
        await rig.Containers.SweepAsync();
        Assert.NotNull(await rig.Items.GetAsync(beacon));
        rig.Now += 2_000;
        await rig.Containers.SweepAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == beacon);
        Assert.Null(await rig.Items.GetAsync(beacon));
        Assert.Null(await rig.Deployed.GetAsync(beacon));
    }

    [PgFact]
    public async Task A_covert_beacon_is_anchored_a_minute_later_and_lives_half_an_hour()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithBeaconsAsync(rig, CovertBeacon, 1, NullSecSystem);
        var launchedAt = rig.Now;

        await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None);

        var record = (await rig.Deployed.GetAsync(items[0]))!;
        Assert.Equal(launchedAt + 60_000, record.ActivateAtMs);
        Assert.Equal(launchedAt + 30L * 60 * 1000, record.ExpiresAtMs);
    }

    [PgFact]
    public async Task A_standard_or_covert_beacon_is_refused_at_high_security_but_the_highsec_authorized_one_is_not()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithBeaconsAsync(rig, Beacon, 1, HighSecSystem);
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));
        Assert.Contains("0.5 security", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceCynoBeacon>());

        var (highsecSession, highsecItems) = await PilotWithBeaconsAsync(rig, HighsecBeacon, 1, HighSecSystem, name: "Highsec Beacon Pilot", sessionId: 2);
        var placed = await rig.Containers.LaunchFromShipAsync(highsecSession, highsecItems[0], CancellationToken.None);
        Assert.Equal(highsecItems[0], placed);
        Assert.Equal("mobile_cyno_beacon", (await rig.Deployed.GetAsync(highsecItems[0]))!.Kind);
    }

    [PgFact]
    public async Task A_pilot_without_the_skills_of_the_beacon_cannot_drop_it_and_it_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithBeaconsAsync(rig, Beacon, 1, NullSecSystem, skilled: false);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("requires", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceCynoBeacon>());
    }

    [PgFact]
    public async Task A_beacon_in_space_comes_back_after_a_restart_unanchored_or_anchored_as_its_record_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithBeaconsAsync(rig, Beacon, 1, NullSecSystem);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None);
        rig.Sim.Heard.Clear();

        rig.Now += 1_000;
        await rig.Containers.RestoreDeployedAsync(system);
        var waiting = Assert.Single(rig.Sim.Heard.OfType<SimPlaceCynoBeacon>());
        Assert.False(waiting.Active);

        rig.Sim.Heard.Clear();
        rig.Now += 120_000;
        await rig.Containers.RestoreDeployedAsync(system);
        var anchored = Assert.Single(rig.Sim.Heard.OfType<SimPlaceCynoBeacon>());
        Assert.True(anchored.Active);
    }

    /// <summary>The record of a Mobile Cynosural Inhibitor in <paramref name="system"/> at <paramref name="at"/>: anchored (its activation is past), 200 km.</summary>
    private static async Task InhibitorOnRecordAsync(Rig rig, int system, double[] at)
    {
        await rig.Deployed.SaveAsync(new DeployedRecord(
            4_250_000_000_000_501, "mobile_cyno_inhibitor", system, "Mobile Cynosural Inhibitor", 33476, 1249, at, 100, 1, 0, 0, 0,
            rig.Now + 60L * 60 * 1000, rig.Now - 1, 200_000));
    }

    [PgFact]
    public async Task A_standard_beacon_that_anchors_inside_an_inhibitors_range_is_never_lit()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithBeaconsAsync(rig, Beacon, 1, NullSecSystem);
        var beacon = items[0];
        await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);
        var at = (await rig.Deployed.GetAsync(beacon))!.Position;
        rig.Now += 2 * 60_000;
        await InhibitorOnRecordAsync(rig, (int)NullSecSystem, at);
        rig.Sim.Heard.Clear();

        await rig.Containers.AnchorAsync(beacon);

        // refused at its activation: the record keeps it unlit, the simulation is not told it is anchored, and it is kept to its end
        Assert.Equal(long.MaxValue, (await rig.Deployed.GetAsync(beacon))!.ActivateAtMs);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive lit && lit.DeployableId == beacon && lit.Active);
        Assert.NotNull(await rig.Deployed.GetAsync(beacon));
    }

    [PgFact]
    public async Task A_covert_beacon_is_not_stopped_by_an_inhibitor_and_a_standard_one_outside_its_range_anchors()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithBeaconsAsync(rig, CovertBeacon, 1, NullSecSystem);
        var covert = items[0];
        await rig.Containers.LaunchFromShipAsync(session, covert, CancellationToken.None);
        var at = (await rig.Deployed.GetAsync(covert))!.Position;
        rig.Now += 60_000;
        await InhibitorOnRecordAsync(rig, (int)NullSecSystem, at);
        rig.Sim.Heard.Clear();

        await rig.Containers.AnchorAsync(covert);
        Assert.Contains(rig.Sim.Heard, m => m is SimSetDisruptorActive lit && lit.DeployableId == covert && lit.Active);

        // a standard beacon 250 km from the inhibitor (its range is 200 km) anchors
        var (standardSession, standardItems) = await PilotWithBeaconsAsync(rig, Beacon, 1, NullSecSystem, name: "Standard Beacon Pilot", sessionId: 2);
        var standard = standardItems[0];
        await rig.Containers.LaunchFromShipAsync(standardSession, standard, CancellationToken.None);
        rig.Now += 2 * 60_000;
        // the standard beacon is dropped where the covert one is, so the inhibitor 250 km along x is 250 km from it
        await InhibitorOnRecordAsync(rig, (int)NullSecSystem, [at[0] + 250_000, at[1], at[2]]);
        rig.Sim.Heard.Clear();
        await rig.Containers.AnchorAsync(standard);
        Assert.Contains(rig.Sim.Heard, m => m is SimSetDisruptorActive lit && lit.DeployableId == standard && lit.Active);
    }

    [PgFact]
    public async Task An_inhibitor_placed_later_does_not_unlight_a_beacon_already_lit()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithBeaconsAsync(rig, Beacon, 1, NullSecSystem);
        var beacon = items[0];
        await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);
        var at = (await rig.Deployed.GetAsync(beacon))!.Position;
        rig.Now += 2 * 60_000;
        await rig.Containers.AnchorAsync(beacon);
        var lit = (await rig.Deployed.GetAsync(beacon))!.ActivateAtMs;
        rig.Now += 60_000;
        await InhibitorOnRecordAsync(rig, (int)NullSecSystem, at);
        rig.Sim.Heard.Clear();

        // the wiki: an inhibitor "do not remove existing cynos": the lit beacon keeps its activation and is not sent away
        await rig.Containers.SweepAsync();
        Assert.Equal(lit, (await rig.Deployed.GetAsync(beacon))!.ActivateAtMs);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive off && off.DeployableId == beacon && !off.Active);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == beacon);
    }

    [PgFact]
    public async Task A_beacon_cannot_be_dropped_in_pochven_but_it_may_be_lit_in_wormhole_space_as_the_wiki_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (pochven, pochvenItems) = await PilotWithBeaconsAsync(rig, Beacon, 1, PochvenSystem);
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(pochven, pochvenItems[0], CancellationToken.None));
        Assert.Contains("cannot be deployed in Pochven", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(pochvenItems[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceCynoBeacon>());

        var (wormhole, wormholeItems) = await PilotWithBeaconsAsync(rig, Beacon, 1, WormholeSystem, name: "Wormhole Beacon Pilot", sessionId: 2);
        var placed = await rig.Containers.LaunchFromShipAsync(wormhole, wormholeItems[0], CancellationToken.None);
        Assert.Equal(wormholeItems[0], placed);
        Assert.Equal("mobile_cyno_beacon", (await rig.Deployed.GetAsync(wormholeItems[0]))!.Kind);
    }
}
