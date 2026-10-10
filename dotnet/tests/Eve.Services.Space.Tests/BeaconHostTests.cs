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
    /// <summary>A null-security system (security -1.0), where a standard beacon may be dropped.</summary>
    private const long NullSecSystem = 30000021;
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
}
