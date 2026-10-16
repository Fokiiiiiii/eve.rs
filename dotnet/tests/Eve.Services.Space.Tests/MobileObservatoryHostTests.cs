using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The Mobile Observatory (E-04's remote decloak ping; the wiki's rules and EveJS <c>mobileObservatoryRuntime</c>): a pilot with Anchoring III drops one from the hold
/// 2,500 m ahead of the ship, in a system below 0.5 security; it is anchored ten minutes later, pings its system at that moment and every ten minutes after, and leaves
/// with its item when its hundred minutes and thirty seconds are up. A second one within 10 km of the first is refused.
/// </summary>
public class MobileObservatoryHostTests
{
    private const int Observatory = 58904, Anchoring = 11584, AnchoringLevel = 3;
    /// <summary>Asabona (Derelik, security 0.32): below the cutoff and outside Pochven, so the observatory may be dropped there.</summary>
    private const long LowSecSystem = 30000012;
    /// <summary>Kuharah (Pochven, security -1.0): an observatory is refused there.</summary>
    private const long PochvenSystem = 30000021;
    /// <summary>Jita: high security, where an observatory is refused.</summary>
    private const long HighSecSystem = 30000142;
    /// <summary>The first Abyssal host location (EVE.js ABYSSAL_LOCATION_ID_MIN): an observatory is refused in Abyssal space.</summary>
    private const long AbyssalSystem = 32_000_001;
    private const long AnchoringMs = 600_000;
    private const long PingIntervalMs = 600_000;
    private const long LifetimeMs = (60 + 40) * 60_000 + 30_000;

    /// <summary>A pilot with <paramref name="count"/> observatories in its hold, in space in <paramref name="system"/>; it has Anchoring III unless told not to.</summary>
    private static async Task<(ClientSession Session, long[] Items)> PilotWithObservatoriesAsync(Rig rig, int count, long system, bool skilled = true)
    {
        var character = await rig.NewCharacterAsync();
        if (skilled)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(Anchoring, AnchoringLevel, 0)]);
            await transaction.CommitAsync();
        }
        var items = new long[count];
        for (var index = 0; index < count; index++)
        {
            var granted = (await rig.Ships.GrantAsync(character.CharacterId, Observatory, 1, singleton: true))!.Single().After;
            var moved = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, CharacterShips.CargoFlag, 1);
            Assert.True(moved is Eve.Ships.ShipChanged, moved.ToString());
            items[index] = granted.Id;
        }

        var session = await rig.SessionAsync(character, 1);
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
    public async Task A_dropped_observatory_is_anchored_and_pings_its_system_then_every_ten_minutes_until_it_leaves_with_its_item()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 1, LowSecSystem);
        var observatory = items[0];
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        var launchedAt = rig.Now;

        var placed = await rig.Containers.LaunchFromShipAsync(session, observatory, CancellationToken.None);

        Assert.Equal(observatory, placed);
        var ball = Assert.Single(rig.Sim.Heard.OfType<SimPlaceObservatory>());
        Assert.False(ball.Active, "an observatory is not anchored when it is dropped");
        Assert.Equal(new[] { 2_500.0, 0.0, 0.0 }, ball.Position.ToArray());
        Assert.Equal(100.0, ball.Radius);
        var record = (await rig.Deployed.GetAsync(observatory))!;
        Assert.Equal("mobile_observatory", record.Kind);
        Assert.Equal(launchedAt + AnchoringMs, record.ActivateAtMs);
        Assert.Equal(launchedAt + LifetimeMs, record.ExpiresAtMs);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs - 1_000;
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive or SimRemoteDecloakPing);
        rig.Now += 2_000;
        await rig.Scheduler.RunDueAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimSetDisruptorActive anchored && anchored.DeployableId == observatory && anchored.Active);
        // the first ping comes when it is anchored (the wiki), then one every ten minutes
        await rig.Scheduler.RunDueAsync();
        var first = Assert.Single(rig.Sim.Heard.OfType<SimRemoteDecloakPing>());
        Assert.Equal(system, first.SolarSystemId);
        Assert.Equal(observatory, first.ObservatoryId);

        rig.Sim.Heard.Clear();
        rig.Now += PingIntervalMs - 2_000;
        await rig.Scheduler.RunDueAsync();
        Assert.Empty(rig.Sim.Heard.OfType<SimRemoteDecloakPing>());
        rig.Now += 2_000;
        await rig.Scheduler.RunDueAsync();
        Assert.Single(rig.Sim.Heard.OfType<SimRemoteDecloakPing>());

        // its time is up at a hundred minutes and thirty seconds: it is still there just before, gone at the sweep after, and pings no more
        rig.Sim.Heard.Clear();
        rig.Now = launchedAt + LifetimeMs - 1_000;
        await rig.Containers.SweepAsync();
        Assert.NotNull(await rig.Items.GetAsync(observatory));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimRemoveDeployed);
        rig.Now += 2_000;
        await rig.Containers.SweepAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == observatory);
        Assert.Null(await rig.Items.GetAsync(observatory));
        Assert.Null(await rig.Deployed.GetAsync(observatory));
        rig.Sim.Heard.Clear();
        rig.Now += PingIntervalMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Empty(rig.Sim.Heard.OfType<SimRemoteDecloakPing>());
    }

    [PgFact]
    public async Task An_observatory_in_space_comes_back_after_a_restart_unanchored_or_anchored_as_its_record_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 1, LowSecSystem);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None);
        rig.Sim.Heard.Clear();

        rig.Now += 1_000;
        await rig.Containers.RestoreDeployedAsync(system);
        var waiting = Assert.Single(rig.Sim.Heard.OfType<SimPlaceObservatory>());
        Assert.False(waiting.Active);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs;
        await rig.Containers.RestoreDeployedAsync(system);
        var anchored = Assert.Single(rig.Sim.Heard.OfType<SimPlaceObservatory>());
        Assert.True(anchored.Active);
    }

    [PgFact]
    public async Task An_observatory_is_not_dropped_in_pochven_and_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 1, PochvenSystem);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("Pochven", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceObservatory>());
    }

    [PgFact]
    public async Task A_station_within_a_thousand_km_of_the_drop_point_refuses_the_observatory_and_one_beyond_it_is_dropped()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 2, LowSecSystem);
        // the drop point is 2 500 m ahead of the ship (the placement test above): a station 500 km from it is too close, one 1 500 km away is not
        rig.Containers.ReadObservatoryLandmarks = (_, _) => Task.FromResult<IReadOnlyList<double[]>>([[502_500.0, 0.0, 0.0]]);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("too close", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceObservatory>());

        rig.Containers.ReadObservatoryLandmarks = (_, _) => Task.FromResult<IReadOnlyList<double[]>>([[1_502_500.0, 0.0, 0.0]]);
        Assert.Equal((long?)items[0], await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));
        Assert.Single(rig.Sim.Heard.OfType<SimPlaceObservatory>());
    }

    [PgFact]
    public async Task A_control_tower_within_100_km_of_the_drop_point_refuses_the_observatory_and_one_beyond_it_is_dropped()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 2, LowSecSystem);
        // the drop point is 2 500 m ahead of the ship: a tower 50 km from it is too close, one 150 km away is not (the wiki's 100 km rule)
        rig.Containers.ReadControlTowers = (_, _) => Task.FromResult<IReadOnlyList<double[]>>([[52_500.0, 0.0, 0.0]]);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("control tower", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceObservatory>());

        rig.Containers.ReadControlTowers = (_, _) => Task.FromResult<IReadOnlyList<double[]>>([[152_500.0, 0.0, 0.0]]);
        Assert.Equal((long?)items[0], await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));
        Assert.Single(rig.Sim.Heard.OfType<SimPlaceObservatory>());
    }

    [PgFact]
    public async Task An_observatory_is_not_anchored_in_a_deadspace_pocket_and_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 2, LowSecSystem);
        // a site's anchor 500 km from the drop point is inside its pocket (EveJS 1e9 m); one 2 000 000 km away is not
        rig.Containers.ReadDeadspaceAnchors = (_, _) => Task.FromResult<IReadOnlyList<double[]>>([[502_500.0, 0.0, 0.0]]);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("deadspace", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceObservatory>());

        rig.Containers.ReadDeadspaceAnchors = (_, _) => Task.FromResult<IReadOnlyList<double[]>>([[2_000_000_000.0, 0.0, 0.0]]);
        Assert.Equal((long?)items[0], await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));
        Assert.Single(rig.Sim.Heard.OfType<SimPlaceObservatory>());
    }

    [PgFact]
    public async Task An_observatory_is_not_dropped_in_abyssal_space_and_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 1, AbyssalSystem);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("Abyssal space", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceObservatory>());
    }

    [PgFact]
    public async Task A_pilot_without_anchoring_three_cannot_drop_an_observatory_and_it_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 1, LowSecSystem, skilled: false);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("requires", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceObservatory>());
    }

    [PgFact]
    public async Task An_observatory_is_not_dropped_in_high_security_space_and_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 1, HighSecSystem);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("below 0.5 security", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceObservatory>());
    }

    [PgFact]
    public async Task A_second_observatory_within_10_km_of_the_first_is_refused_and_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithObservatoriesAsync(rig, 2, LowSecSystem);
        await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None);

        // the simulation gives the same point ahead of the ship: the second would sit on the first
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[1], CancellationToken.None));

        Assert.Contains("too close", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[1]))!.Flag);
        Assert.Single(rig.Sim.Heard.OfType<SimPlaceObservatory>());
    }
}
