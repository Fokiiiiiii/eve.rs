using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Space.Beacons;
using Eve.Events;
using Eve.Scheduler;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The CONCORD Rogue Analysis Beacon in the space of the host (D-L1 and D-S1): a pilot drops it from the hold, links it for four minutes and scans for 10 minutes (16 for the Carrier). Its four mutaplasmid rewards enter the beacon hold, are restricted to the linking pilot for 120 seconds, then move intact to a public wreck. The simulation is the fake one; the rest is the host's own composition.
/// </summary>
public sealed class AnalysisBeaconHostTests
{
    private const int StandardCrab = 60244, CarrierCrab = 92183, Cockroach = 11019, Anchoring3 = 11584;
    private const long NullSecSystem = 30000021, JitaSystem = 30000142;
    private const long TwentySeconds = 20_000, FourMinutes = 240_000, TenMinutes = 600_000, OneHour = 3_600_000, Step = 10_000;
    private const int RewardAnalysisData = 60459, RewardHeavyFirepower = 60464, RewardHeavyProjection = 60466, RewardHeavyNavigation = 60463, RewardHeavyDurability = 60465;

    /// <summary>A pilot in null-security space, flying a Cockroach, with a beacon of <paramref name="typeId"/> in its hold.</summary>
    private static async Task<(ClientSession Session, long ShipId, long Beacon)> PilotWithBeaconAsync(Rig rig, int typeId = StandardCrab, bool skilled = true, string name = "Space Tester", int account = 1, long sessionId = 1)
    {
        var character = await rig.NewCharacterAsync(name, account);
        if (skilled)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(Anchoring3, 3, 0)]);
            await transaction.CommitAsync();
        }
        var granted = (await rig.Ships.GrantAsync(character.CharacterId, typeId, 1, singleton: true))!.Single().After;
        var moved = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, CharacterShips.CargoFlag, 1);
        Assert.True(moved is ShipChanged, moved.ToString());
        // the pilot lives in the null-security system from the start, so the session and the pilot's space record agree on it
        var place = rig.Systems.Get((int)NullSecSystem) ?? throw new InvalidOperationException($"the static data has no solar system {NullSecSystem}");
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Characters.SetLocationAsync(connection, transaction, character.CharacterId, (int)NullSecSystem, place.ConstellationId, place.RegionId);
            await transaction.CommitAsync();
        }
        var session = await rig.SessionAsync(character, sessionId);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        session.Set("solarsystemid", new PyInt(NullSecSystem));
        await rig.BindBeyonceAsync(session);
        return (session, character.ShipId, granted.Id);
    }

    /// <summary>The director over the rig's own scheduler, bus, simulation and space; wired as the composition wires it (launch, record kind, link).</summary>
    private static async Task<AnalysisBeaconDirector> DirectorAsync(Rig rig, DurableScheduler? scheduler = null, EventBus? bus = null)
    {
        await AnalysisBeaconDirector.MigrateAsync(rig.Source, rig.SpaceSchema);
        var director = new AnalysisBeaconDirector(
            rig.Source, rig.SpaceSchema, AnalysisBeaconRules.LoadEmbedded(), scheduler ?? rig.Scheduler, bus ?? rig.Bus!, rig.Sim, rig.Ships, rig.Items, rig.Statics, rig.Space,
            rig.Deployed, new SpaceDungeonNpcs(rig.Space), new NpcSelector(NpcAuthority.Load()), () => rig.Now);
        await director.AttachAsync();
        rig.Containers.LaunchAnalysisBeacon = director.LaunchAsync;
        rig.Containers.RegisterDeployedKind(AnalysisBeaconDirector.Kind, director.ExpireAsync, director.RestoreAsync);
        rig.Beyonce.InitiateBeaconLink = (session, beacon) => director.InitiateLinkAsync(session, beacon);
        await (bus ?? rig.Bus!).PumpAsync();
        return director;
    }

    /// <summary>The due jobs and the announcements they make, run until nothing is left (the space's own background work first).</summary>
    private static async Task RunAsync(Rig rig, DurableScheduler? scheduler = null, EventBus? bus = null)
    {
        await rig.Space.QuiesceAsync();
        for (var round = 0; round < 6; round++)
        {
            await (scheduler ?? rig.Scheduler).RunDueAsync();
            var delivered = 0;
            for (var i = 0; i < 20; i++)
            {
                var pumped = (await (bus ?? rig.Bus!).PumpAsync()).Delivered;
                if (pumped == 0)
                {
                    break;
                }
                delivered += pumped;
            }
            if (delivered == 0)
            {
                break;
            }
        }
    }

    /// <summary>Moves the clock on in steps of the scan tick, running what is due at each step.</summary>
    private static async Task AdvanceAsync(Rig rig, long milliseconds)
    {
        for (var elapsed = 0L; elapsed < milliseconds; elapsed += Step)
        {
            rig.Now += Step;
            await RunAsync(rig);
        }
    }

    /// <summary>Every text inside a wrapped error's payload, joined (the refusal's words).</summary>
    private static string Texts(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyTuple tuple => string.Join("|", tuple.Items.Select(Texts)),
        PyList list => string.Join("|", list.Items.Select(Texts)),
        PyDict dict => string.Join("|", dict.Entries.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) })),
        PyObjectEx ex => string.Join("|", new[] { Texts(ex.Header) }.Concat(ex.List.Select(Texts)).Concat(ex.Dict.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) }))),
        PyObject obj => Texts(obj.Args),
        _ => "",
    };

    /// <summary>The quantity of a type in a ship's hold (the pilot's items in the ship).</summary>
    private static async Task<long> AddedToCargoAsync(Rig rig, ClientSession session, long shipId, int typeId) =>
        (await rig.Items.InLocationAsync(shipId, session.CharacterId!.Value)).Where(item => item.TypeId == typeId && item.Flag == CharacterShips.CargoFlag).Sum(item => item.Quantity);

    private static async Task<long> InContainerAsync(Rig rig, long containerId, int typeId) =>
        (await rig.Items.InContainerAsync(containerId)).Where(item => item.TypeId == typeId).Sum(item => item.Quantity);

    [PgFact]
    public async Task A_standard_crab_dropped_from_the_hold_activates_after_twenty_seconds_and_waits_for_linking_before_its_first_wave()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        var director = await DirectorAsync(rig);
        var (session, _, beacon) = await PilotWithBeaconAsync(rig);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        var launchedAt = rig.Now;

        var placed = await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);

        Assert.Equal(beacon, placed);
        var item = (await rig.Items.GetAsync(beacon))!;
        Assert.Equal(system, (int)item.LocationId);
        Assert.Equal(CharacterShips.InSpaceFlag, item.Flag);
        var ball = Assert.Single(rig.Sim.Heard.OfType<SimPlaceAnalysisBeacon>());
        Assert.Equal(StandardCrab, ball.TypeId);
        Assert.Equal(4137, ball.GroupId);
        Assert.False(ball.State.Active, "a beacon is not active when it is dropped");
        Assert.Equal(20, ball.State.ActivationSeconds);
        Assert.Equal(AnalysisBeaconDirector.FiletimeOf(launchedAt + TwentySeconds), ball.State.ActivateAtFiletime);
        Assert.Equal(AnalysisBeaconDirector.FiletimeOf(launchedAt), ball.State.DeployedAtFiletime);
        var record = (await rig.Deployed.GetAsync(beacon))!;
        Assert.Equal(AnalysisBeaconDirector.Kind, record.Kind);
        Assert.Equal(launchedAt + TwentySeconds, record.ActivateAtMs);
        Assert.Equal(launchedAt + OneHour, record.ExpiresAtMs);

        rig.Sim.Heard.Clear();
        rig.Now += TwentySeconds - 1_000;
        await RunAsync(rig);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetAnalysisBeaconState);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSpawn);

        rig.Now += 2_000;
        await RunAsync(rig);
        var active = Assert.Single(rig.Sim.Heard.OfType<SimSetAnalysisBeaconState>());
        Assert.Equal(beacon, active.DeployableId);
        Assert.True(active.State.Active);
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
        Assert.NotNull(director);
    }


    [PgFact]
    public async Task A_crab_is_refused_within_ten_thousand_kilometres_of_an_asteroid_and_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (session, _, beacon) = await PilotWithBeaconAsync(rig);
        var point = await rig.Space.DropPointAsync(session, AnalysisBeaconRules.DeployDistanceMeters, CancellationToken.None);
        Assert.NotNull(point);
        var candidate = point.Value;
        var asteroid = (await rig.Statics.TypesInCategoryAsync("Asteroid")).First();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.SpaceItems.SaveAsync(connection, transaction, new Eve.Ships.SpaceItem(
                9_000_000_001, (int)NullSecSystem, [candidate.X, candidate.Y, candidate.Z], [1, 0, 0], 100, asteroid.GroupId,
                0, 0, 0, 0, 0, 0, 0), CancellationToken.None);
            await transaction.CommitAsync();
        }

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None));

        Assert.Contains("10,000 km of an asteroid", Texts(refusal.Error.Payload));
        Assert.DoesNotContain(rig.Sim.Heard, message => message is SimPlaceAnalysisBeacon);
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(beacon))!.Flag);
    }


    [PgFact]
    public async Task A_crab_can_be_deployed_beyond_ten_thousand_kilometres_from_an_asteroid()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (session, _, beacon) = await PilotWithBeaconAsync(rig);
        var point = await rig.Space.DropPointAsync(session, AnalysisBeaconRules.DeployDistanceMeters, CancellationToken.None);
        Assert.NotNull(point);
        var candidate = point.Value;
        var asteroid = (await rig.Statics.TypesInCategoryAsync("Asteroid")).First();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.SpaceItems.SaveAsync(connection, transaction, new Eve.Ships.SpaceItem(
                9_000_000_002, (int)NullSecSystem, [candidate.X + 10_000_001, candidate.Y, candidate.Z], [1, 0, 0], 100, asteroid.GroupId,
                0, 0, 0, 0, 0, 0), CancellationToken.None);
            await transaction.CommitAsync();
        }

        var deployed = await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);

        Assert.NotNull(deployed);
        Assert.Contains(rig.Sim.Heard, message => message is SimPlaceAnalysisBeacon);
        Assert.NotEqual(CharacterShips.CargoFlag, (await rig.Items.GetAsync(beacon))!.Flag);
    }

    [PgFact]
    public async Task A_crab_is_refused_in_high_security_space_and_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (session, _, beacon) = await PilotWithBeaconAsync(rig);
        session.Set("solarsystemid2", new PyInt(JitaSystem));

        var highSec = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None));

        Assert.Contains("low or null security", Texts(highSec.Error.Payload));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimPlaceAnalysisBeacon);
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(beacon))!.Flag);
    }

    [PgFact]
    public async Task A_pilot_without_the_anchoring_skill_cannot_deploy_a_crab_and_it_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (unskilled, _, other) = await PilotWithBeaconAsync(rig, skilled: false);

        var missing = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(unskilled, other, CancellationToken.None));

        Assert.Contains("requires", Texts(missing.Error.Payload));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimPlaceAnalysisBeacon);
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(other))!.Flag);
    }

    [PgFact]
    public async Task A_linked_cockroach_starts_the_link_and_the_link_is_complete_after_four_minutes_and_a_second_link_is_refused()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (session, shipId, beacon) = await PilotWithBeaconAsync(rig);
        await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);
        await AdvanceAsync(rig, TwentySeconds);
        var bound = await rig.BindBeyonceAsync(session);
        rig.Sim.Heard.Clear();

        // the client's component call: linkWithShip.InitiateLink on the beacon, through the route of the ballpark service
        var linkedAt = rig.Now;
        await rig.BeyonceCallAsync(session, "CallComponentFromClient", bound, new PyLong(beacon), PyStrOfClient("linkWithShip"), PyStrOfClient("InitiateLink"));
        await RunAsync(rig);

        var running = rig.Sim.Heard.OfType<SimSetAnalysisBeaconState>().Single(m => m.State.LinkState == 2);
        Assert.Equal(beacon, running.DeployableId);
        Assert.Equal(shipId, running.State.LinkedShipId);
        Assert.Equal(AnalysisBeaconDirector.FiletimeOf(linkedAt + FourMinutes), running.State.LinkCompleteAtFiletime);

        // a beacon linked already is not linked again
        rig.Sim.Heard.Clear();
        await rig.BeyonceCallAsync(session, "CallComponentFromClient", bound, new PyLong(beacon), new PyStr("linkWithShip"), new PyStr("InitiateLinkQuick"));
        await RunAsync(rig);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetAnalysisBeaconState);

        await AdvanceAsync(rig, FourMinutes);
        var complete = Assert.Single(rig.Sim.Heard.OfType<SimSetAnalysisBeaconState>());
        Assert.Equal(3, complete.State.LinkState);
        Assert.Equal(shipId, complete.State.LinkedShipId);
        Assert.Equal(4, rig.Sim.Heard.OfType<SimSpawn>().Count());
    }

    [PgFact]
    public async Task A_ship_not_on_the_link_list_cannot_link_the_carrier_crab_and_nothing_is_linked()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (session, _, beacon) = await PilotWithBeaconAsync(rig, typeId: CarrierCrab);
        await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);
        await AdvanceAsync(rig, TwentySeconds);
        var bound = await rig.BindBeyonceAsync(session);
        rig.Sim.Heard.Clear();

        await rig.BeyonceCallAsync(session, "CallComponentFromClient", bound, new PyLong(beacon), PyStrOfClient("linkWithShip"), PyStrOfClient("InitiateLink"));
        await RunAsync(rig);

        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetAnalysisBeaconState);
    }

    [PgFact]
    public async Task A_standard_scan_does_not_move_while_a_drone_lives_and_completes_after_ten_minutes_of_scanning_and_pays_once()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        var director = await DirectorAsync(rig);
        var (session, shipId, beacon) = await PilotWithBeaconAsync(rig);
        await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);
        rig.Sim.Heard.Clear();
        await AdvanceAsync(rig, TwentySeconds);
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
        await CompleteLinkAsync(rig, session, beacon);
        var drones = rig.Sim.Heard.OfType<SimSpawn>().Select(spawn => spawn.Ship.ShipId).ToList();
        Assert.Equal(4, drones.Count);

        // five minutes with the drones alive: the scan does not move, so nothing completes
        await AdvanceAsync(rig, 5 * 60_000);
        Assert.Equal(0, await AddedToCargoAsync(rig, session, shipId, RewardAnalysisData));

        // the drones die; the scan goes on from now
        foreach (var ship in drones)
        {
            rig.Sim.Report(new SimDestroyed((ulong)session.SessionId, ship, 0, [0, 0, 0], [1, 0, 0], 1, 0));
        }
        await RunAsync(rig);
        // the scan reaches half from here and makes its second wave, which holds the scan until it is dead
        await AdvanceAsync(rig, TenMinutes - 10_000);
        Assert.Equal(0, await AddedToCargoAsync(rig, session, shipId, RewardAnalysisData));
        var second = rig.Sim.Heard.OfType<SimSpawn>().Select(spawn => spawn.Ship.ShipId).Except(drones).ToList();
        Assert.Equal(4, second.Count);
        Kill(rig, session, second);
        await RunAsync(rig);

        // the scan was at half when the second wave died, so five more minutes complete it
        await AdvanceAsync(rig, 5 * 60_000 - 10_000);
        Assert.Equal(0, await AddedToCargoAsync(rig, session, shipId, RewardAnalysisData));
        await AdvanceAsync(rig, 10_000);
        Assert.Equal(1441, await InContainerAsync(rig, beacon, RewardAnalysisData));
        Assert.Equal(1, await InContainerAsync(rig, beacon, RewardHeavyFirepower));
        Assert.Equal(1, await InContainerAsync(rig, beacon, RewardHeavyProjection));
        Assert.Equal(1, await InContainerAsync(rig, beacon, RewardHeavyNavigation));
        Assert.Equal(1, await InContainerAsync(rig, beacon, RewardHeavyDurability));
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceWreck>());

        // The linking pilot can open the cargo during its exclusive window; at 120 seconds it becomes a wreck.
        var linkingPilot = session.CharacterId!.Value;
        session.CharacterId = linkingPilot + 1;
        Assert.Null(await rig.Containers.OpenAsync(session, beacon, CancellationToken.None));
        session.CharacterId = linkingPilot;
        var deployment = await rig.Deployed.GetAsync(beacon);
        Assert.NotNull(deployment);
        var earlyExpiry = deployment with { ExpiresAtMs = rig.Now };
        await rig.Deployed.SaveAsync(earlyExpiry);
        var protectedBeacon = await rig.Items.GetAsync(beacon);
        Assert.StartsWith($"analysis_beacon_loot:{linkingPilot}:", protectedBeacon?.CustomInfo);
        var accessUntil = long.Parse(protectedBeacon!.CustomInfo!.Split(':')[2]);
        Assert.True(rig.Now < accessUntil);
        await director.ExpireAsync(earlyExpiry);
        Assert.Equal(accessUntil, (await rig.Deployed.GetAsync(beacon))?.ExpiresAtMs);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceWreck>());
        Assert.NotNull(await rig.Containers.OpenAsync(session, beacon, CancellationToken.None));

        var rewardBeacon = await rig.Items.GetAsync(beacon);
        Assert.StartsWith($"analysis_beacon_loot:{linkingPilot}:", rewardBeacon?.CustomInfo);
        Assert.Equal(4137, rig.Statics.Type(rewardBeacon!.TypeId)?.GroupId);
        Assert.Equal(rig.Space.SolarSystemOf(session), checked((int)rewardBeacon.LocationId));
        Assert.NotNull(await rig.Containers.OpenAsync(session, beacon, CancellationToken.None));
        rig.Now += 119_999;
        Assert.NotNull(await rig.Containers.OpenAsync(session, beacon, CancellationToken.None));
        rig.Now += 1;
        Assert.Null(await rig.Containers.OpenAsync(session, beacon, CancellationToken.None));
        var restartedScheduler = new DurableScheduler(rig.Source, () => rig.Now, null, rig.SchedulerSchema);
        var restartedBus = new EventBus(rig.Source, restartedScheduler, null, rig.EventsSchema);
        var restarted = await DirectorAsync(rig, restartedScheduler, restartedBus);
        await RunAsync(rig, restartedScheduler, restartedBus);
        var wreck = Assert.Single(rig.Sim.Heard.OfType<SimPlaceWreck>());
        Assert.NotEqual(beacon, wreck.WreckId);
        var wreckRights = await rig.SpaceItems.GetAsync(wreck.WreckId);
        Assert.NotNull(wreckRights);
        Assert.Equal((0, 0, 0, 0), (wreckRights.CharacterId, wreckRights.CorporationId, wreckRights.AllianceId, wreckRights.WarFactionId));
        Assert.Equal(1441, await InContainerAsync(rig, wreck.WreckId, RewardAnalysisData));
        Assert.Equal(1, await InContainerAsync(rig, wreck.WreckId, RewardHeavyNavigation));
        Assert.Equal(1, await InContainerAsync(rig, wreck.WreckId, RewardHeavyFirepower));
        Assert.Equal(1, await InContainerAsync(rig, wreck.WreckId, RewardHeavyProjection));
        Assert.Equal(1, await InContainerAsync(rig, wreck.WreckId, RewardHeavyDurability));
        var (otherPilot, otherShip, _) = await PilotWithBeaconAsync(rig, name: "Wreck Loot Tester", account: 2, sessionId: 2);
        Assert.NotEqual(session.SessionId, otherPilot.SessionId);
        Assert.NotNull(await rig.Containers.OpenAsync(otherPilot, wreck.WreckId, CancellationToken.None));
        var dataStack = Assert.Single(await rig.Items.InContainerAsync(wreck.WreckId), item => item.TypeId == RewardAnalysisData);
        Assert.Equal((int)otherShip, otherPilot.GetInt("shipid"));
        Assert.True(rig.Space.IsInSpace(otherPilot));
        var cargoShip = await rig.Ships.LoadAsync(otherPilot.CharacterId!.Value, CancellationToken.None);
        Assert.NotNull(cargoShip);
        Assert.Equal(CharacterShips.InSpaceFlag, cargoShip.Ship.Flag);
        Assert.Equal((await rig.Items.GetAsync(wreck.WreckId))?.LocationId, cargoShip.Ship.LocationId);
        Assert.Equal(wreck.WreckId, dataStack.LocationId);
        Assert.NotNull(await rig.Containers.MoveAsync(otherPilot, dataStack.Id, wreck.WreckId, otherShip, null, CancellationToken.None));
        // Moving from the public wreck assigns the stack to the taking pilot and places it in the ship's cargo hold.
        Assert.Equal(1441, (await rig.Items.InContainerAsync(otherShip)).Where(item => item.TypeId == RewardAnalysisData && item.Flag == CharacterShips.CargoFlag).Sum(item => item.Quantity));
        var remainingData = (await rig.Items.InContainerAsync(wreck.WreckId)).Where(item => item.TypeId == RewardAnalysisData).ToList();
        Assert.True(remainingData.Count == 0, string.Join(", ", remainingData.Select(item => $"id={item.Id} quantity={item.Quantity} owner={item.OwnerId} location={item.LocationId} flag={item.Flag}")));
        await restarted.CreateRewardWreckAsync(beacon);
        Assert.Equal(0, await InContainerAsync(rig, wreck.WreckId, RewardAnalysisData));
    }

    [PgFact]
    public async Task A_scan_makes_its_second_wave_when_it_reaches_half_and_completes_only_after_both_waves_are_dead()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (session, shipId, beacon) = await PilotWithBeaconAsync(rig);
        await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);
        rig.Sim.Heard.Clear();
        await AdvanceAsync(rig, TwentySeconds);
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
        await CompleteLinkAsync(rig, session, beacon);
        var first = rig.Sim.Heard.OfType<SimSpawn>().Select(spawn => spawn.Ship.ShipId).ToList();
        Assert.Equal(4, first.Count);
        Kill(rig, session, first);
        await RunAsync(rig);

        // the scan reaches half (five minutes) after link completion: the second wave comes then, and not before
        await AdvanceAsync(rig, 290_000);
        Assert.Equal(4, rig.Sim.Heard.OfType<SimSpawn>().Count());
        await AdvanceAsync(rig, 10_000);
        var second = rig.Sim.Heard.OfType<SimSpawn>().Select(spawn => spawn.Ship.ShipId).Except(first).ToList();
        Assert.Equal(4, second.Count);

        // the second wave blocks the scan until its drones are dead: half the scan is held, nothing pays
        await AdvanceAsync(rig, 5 * 60_000);
        Assert.Equal(0, await AddedToCargoAsync(rig, session, shipId, RewardAnalysisData));
        Kill(rig, session, second);
        await RunAsync(rig);

        // the scan goes on from the death of the second wave: 300 more seconds, then the reward
        await AdvanceAsync(rig, 290_000);
        Assert.Equal(0, await AddedToCargoAsync(rig, session, shipId, RewardAnalysisData));
        await AdvanceAsync(rig, 10_000);
        Assert.Equal(1441, await InContainerAsync(rig, beacon, RewardAnalysisData));
    }

    private static async Task CompleteLinkAsync(Rig rig, ClientSession session, long beacon)
    {
        var bound = await rig.BindBeyonceAsync(session);
        await rig.BeyonceCallAsync(session, "CallComponentFromClient", bound, new PyLong(beacon), PyStrOfClient("linkWithShip"), PyStrOfClient("InitiateLink"));
        await RunAsync(rig);
        await AdvanceAsync(rig, FourMinutes);
    }

    /// <summary>A Python <c>str</c> as the client sends it: the bytes of the text (a PyBytes on the wire).</summary>
    private static PyBytes PyStrOfClient(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    private static void Kill(Rig rig, ClientSession session, IEnumerable<long> ships)
    {
        foreach (var ship in ships)
        {
            rig.Sim.Report(new SimDestroyed((ulong)session.SessionId, ship, 0, [0, 0, 0], [1, 0, 0], 1, 0));
        }
    }

    [PgFact]
    public async Task A_beacon_whose_hour_is_up_is_taken_out_with_its_drones_and_its_item_and_pays_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (session, shipId, beacon) = await PilotWithBeaconAsync(rig);
        await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);
        await AdvanceAsync(rig, TwentySeconds);
        var record = (await rig.Deployed.GetAsync(beacon))!;
        rig.Sim.Heard.Clear();

        rig.Now = record.ExpiresAtMs;
        await rig.Containers.SweepAsync();
        await RunAsync(rig);

        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == beacon);
        Assert.Null(await rig.Items.GetAsync(beacon));
        Assert.Null(await rig.Deployed.GetAsync(beacon));
        Assert.Equal(0, await AddedToCargoAsync(rig, session, shipId, RewardAnalysisData));
    }

    [PgFact]
    public async Task A_crab_is_activated_by_a_restarted_host_from_its_durable_job_and_is_put_back_with_its_state()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        await DirectorAsync(rig);
        var (session, _, beacon) = await PilotWithBeaconAsync(rig);
        await rig.Containers.LaunchFromShipAsync(session, beacon, CancellationToken.None);

        // the host restarts: a new scheduler, a new bus with the same cursors, and a new director over the same tables, which register their jobs again
        var restartedScheduler = new DurableScheduler(rig.Source, () => rig.Now, null, rig.SchedulerSchema);
        var restartedBus = new EventBus(rig.Source, restartedScheduler, null, rig.EventsSchema);
        var restarted = await DirectorAsync(rig, restartedScheduler, restartedBus);
        rig.Sim.Heard.Clear();
        for (var elapsed = 0L; elapsed < TwentySeconds; elapsed += Step)
        {
            rig.Now += Step;
            await RunAsync(rig, restartedScheduler, restartedBus);
        }
        Assert.Contains(rig.Sim.Heard, m => m is SimSetAnalysisBeaconState active && active.DeployableId == beacon && active.State.Active);
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());

        rig.Sim.Heard.Clear();
        var record = (await rig.Deployed.GetAsync(beacon))!;
        await restarted.RestoreAsync(record, rig.Now);
        var put = Assert.Single(rig.Sim.Heard.OfType<SimPlaceAnalysisBeacon>());
        Assert.True(put.State.Active, "the restored beacon is active, as its row says");
        Assert.Equal(beacon, put.DeployableId);
    }

    [PgFact]
    public async Task Character_energy_and_system_interference_are_spent_once_and_regenerate_after_link_attempts()
    {
        await using var rig = await Rig.CreateAsync();
        await AnalysisBeaconDirector.MigrateAsync(rig.Source, rig.SpaceSchema);
        var store = new AnalysisBeaconStore(rig.Source, rig.SpaceSchema);
        var characterId = 90001L;
        var systemId = (int)NullSecSystem;

        async Task<bool> Spend(long character, int system, int typeId)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var allowed = await store.TryConsumeLinkResourcesAsync(connection, transaction, character, system, typeId, rig.Now, CancellationToken.None);
            await transaction.CommitAsync();
            return allowed;
        }

        Assert.True(await Spend(characterId, systemId, StandardCrab));
        Assert.True(await Spend(characterId, systemId, StandardCrab));
        Assert.True(await Spend(characterId, systemId, StandardCrab));
        Assert.False(await Spend(characterId, systemId, StandardCrab));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Spend(characterId + 1, systemId + 1, StandardCrab)));
        Assert.Equal(3, attempts.Count(allowed => allowed));

        var systemAttempts = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Spend(100_000 + index, systemId + 2, StandardCrab)));
        Assert.Equal(34, systemAttempts.Count(allowed => allowed));
        Assert.False(await Spend(100_100, systemId + 2, StandardCrab));

        rig.Now += 22 * 60 * 60 * 1000L;
        Assert.True(await Spend(characterId, systemId, CarrierCrab));
    }

}
