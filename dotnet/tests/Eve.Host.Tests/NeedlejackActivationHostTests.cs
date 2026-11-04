using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eve.Crimewatch;
using Eve.Content;
using Eve.Dogma;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services;
using Eve.Services.Crimewatch;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class NeedlejackActivationHostTests
{
    private const int NeedlejackGroupId = 4041;
    private const int CapacityAttributeId = 2832;
    private const int ActivityAttributeId = 3025;
    private const int CapitalSizeAttributeId = 1785;

    [PgFact]
    public async Task Host_registration_uses_noise_and_signal_activity_and_moves_the_consumed_stack_once()
    {
        await using var rig = await AbyssalHostFlowTests.Rig.Create();
        using var fixture = await SetupAsync(rig);
        var candidates = fixture.Systems.Systems
            .Where(system => system.Security <= 0 && system.RegionName != "Pochven" && system.Gates.Count > 0)
            .OrderBy(system => system.Id).ToArray();
        Assert.True(candidates.Length > 2);
        foreach (var mode in new[] { 0, 1 })
        {
            var run = await rig.CreateRun(enter: false);
            var session = await rig.ConnectPublic(run);
            var characterId = run.Data.CreatorCharacterId;
            var linkedRun = await rig.CreateRun(enter: false);
            var linkedSession = await rig.ConnectPublic(linkedRun);
            var linkedCharacterId = linkedRun.Data.CreatorCharacterId;
            var sourceSystemId = run.Data.SpaceLocationId;
            var activityCandidates = candidates.Where(system => system.Id != sourceSystemId).ToArray();
            Assert.True(activityCandidates.Length > 2);
            var activeSystem = activityCandidates[^1];
            var noiseDestination = activityCandidates.First(system => system.Id != activeSystem.Id).Id;
            var expectedDestination = mode == 0 ? noiseDestination : activeSystem.Id;
            var fleetId = mode + 1;
            await fixture.CrimewatchStore.SetSafetyAsync(characterId, SafetyLevel.Partial, rig.Now);
            await fixture.CrimewatchStore.SetSafetyAsync(linkedCharacterId, SafetyLevel.Partial, rig.Now);
            await AddFleetMemberAsync(rig, characterId, run.Data.CreatorShipId, fleetId, FleetStore.Leader);
            await AddFleetMemberAsync(rig, linkedCharacterId, linkedRun.Data.CreatorShipId, fleetId, FleetStore.Member);
            var filamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, mode);
            var filamentId = await CreateFilamentAsync(rig, characterId, run.Data.CreatorShipId, filamentTypeId, 2);
            if (mode == 0)
                await fixture.Bounties.RecordAsync(new BountyKill(characterId, activeSystem.Id, 0, 0, 1m), rig.Now);

            rig.Order.Clear();
            rig.ClientNotifications.Clear();
            rig.MessageOrder.Clear();
            var call = ActivateCall(session, filamentId, 10 + mode);
            Assert.Equal(PyNone.Instance, await fixture.Service.CallAsync(call));

            var ship = (await rig.Items.GetAsync(run.Data.CreatorShipId))!;
            var linkedShip = (await rig.Items.GetAsync(linkedRun.Data.CreatorShipId))!;
            var item = (await rig.Items.GetAsync(filamentId))!;
            var timer = await fixture.CrimewatchStore.StateOfAsync(characterId);
            var linkedTimer = await fixture.CrimewatchStore.StateOfAsync(linkedCharacterId);
            Assert.Equal(expectedDestination, ship.LocationId);
            Assert.Equal(expectedDestination, session.GetInt("solarsystemid"));
            Assert.Equal(expectedDestination, linkedShip.LocationId);
            Assert.Equal(expectedDestination, linkedSession.GetInt("solarsystemid"));
            Assert.Equal(1, item.Quantity);
            Assert.Equal(rig.Now + CombatState.PvpDurationMs, timer.PvpUntilMs);
            Assert.Equal(expectedDestination, timer.SolarSystemId);
            Assert.Equal(rig.Now + CombatState.PvpDurationMs, linkedTimer.PvpUntilMs);
            Assert.Equal(expectedDestination, linkedTimer.SolarSystemId);
            Assert.Equal(new[] { "OnItemChange", "OnItemChange", "SessionChange", "OnItemChange", "SessionChange" }, rig.Order.Take(5));
            Assert.Equal("OnItemChange", rig.ClientNotifications[0]);
            Assert.Equal(new[] { "OnItemChange", "OnItemChange", "SessionChange", "OnPvpTimerUpdate",
                "OnItemChange", "SessionChange", "OnPvpTimerUpdate" }, rig.MessageOrder);
            Assert.Equal((ulong)linkedSession.SessionId, fixture.Simulation!.PositionSessions[linkedShip.Id]);
        }
    }

    [PgFact]
    public async Task Host_uses_the_lowest_sde_system_id_when_no_pending_activity_is_recorded()
    {
        await using var rig = await AbyssalHostFlowTests.Rig.Create();
        using var fixture = await SetupAsync(rig);
        var run = await rig.CreateRun(enter: false);
        var session = await rig.ConnectPublic(run);
        var characterId = run.Data.CreatorCharacterId;
        var candidates = fixture.Systems.Systems
            .Where(system => system.Id != run.Data.SpaceLocationId && system.Security <= 0 &&
                system.RegionName != "Pochven" && system.Gates.Count > 0)
            .OrderBy(system => system.Id).ToArray();
        Assert.NotEmpty(candidates);
        await fixture.CrimewatchStore.SetSafetyAsync(characterId, SafetyLevel.Partial, rig.Now);
        await AddFleetMemberAsync(rig, characterId, run.Data.CreatorShipId);
        var filamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, 1);
        var filamentId = await CreateFilamentAsync(rig, characterId, run.Data.CreatorShipId, filamentTypeId, 1);

        Assert.Equal(PyNone.Instance, await fixture.Service.CallAsync(ActivateCall(session, filamentId, 20)));

        Assert.Equal(candidates[0].Id, session.GetInt("solarsystemid"));
        Assert.Null(await rig.Items.GetAsync(filamentId));
    }

    [PgFact]
    public async Task Host_rejects_unfleeted_green_safety_and_active_capsuleer_timer_without_consuming()
    {
        await using var rig = await AbyssalHostFlowTests.Rig.Create();
        using var fixture = await SetupAsync(rig);
        var run = await rig.CreateRun(enter: false);
        var session = await rig.ConnectPublic(run);
        var characterId = run.Data.CreatorCharacterId;
        var filamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, 0);
        var filamentId = await CreateFilamentAsync(rig, characterId, run.Data.CreatorShipId, filamentTypeId, 2);
        var callId = 30;

        var noFleet = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Service.CallAsync(ActivateCall(session, filamentId, callId++)).AsTask());
        AssertErrorCode(noFleet, 23);

        await AddFleetMemberAsync(rig, characterId, run.Data.CreatorShipId);
        var greenSafety = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Service.CallAsync(ActivateCall(session, filamentId, callId++)).AsTask());
        AssertErrorCode(greenSafety, 9);

        await fixture.CrimewatchStore.SetSafetyAsync(characterId, SafetyLevel.Partial, rig.Now);
        await fixture.CrimewatchStore.UpdateAsync(characterId,
            state => state with { PvpUntilMs = rig.Now + 60_000 }, rig.Now);
        var activeSystem = fixture.Systems.Systems.First(system => system.Id != run.Data.SpaceLocationId &&
            system.Security <= 0 && system.RegionName != "Pochven" && system.Gates.Count > 0);
        await fixture.Bounties.RecordAsync(new BountyKill(characterId, activeSystem.Id, 0, 0, 1m), rig.Now);
        var activeTimer = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Service.CallAsync(ActivateCall(session, filamentId, callId)).AsTask());
        AssertErrorCode(activeTimer, 8);
        Assert.Equal(2, (await rig.Items.GetAsync(filamentId))!.Quantity);
        Assert.Equal(0, await ActivationCountAsync(rig));
    }

    [PgFact]
    public async Task Host_rejects_capital_hulls_and_fleets_above_the_sde_candidate_limit()
    {
        await using var rig = await AbyssalHostFlowTests.Rig.Create();
        using var fixture = await SetupAsync(rig);

        var capitalTypeId = await CapitalShipTypeAsync(rig, fixture.Dogma);
        var capitalRun = await rig.CreateRun(enter: false, shipTypeId: capitalTypeId);
        var capitalSession = await rig.ConnectPublic(capitalRun);
        var capitalCharacterId = capitalRun.Data.CreatorCharacterId;
        var capitalFilamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, 0);
        var capitalFilamentId = await CreateFilamentAsync(rig, capitalCharacterId, capitalRun.Data.CreatorShipId, capitalFilamentTypeId, 1);
        await fixture.CrimewatchStore.SetSafetyAsync(capitalCharacterId, SafetyLevel.Partial, rig.Now);
        await AddFleetMemberAsync(rig, capitalCharacterId, capitalRun.Data.CreatorShipId);

        var capital = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Service.CallAsync(ActivateCall(capitalSession, capitalFilamentId, 42)).AsTask());

        AssertErrorCode(capital, 4);
        Assert.Equal(1, (await rig.Items.GetAsync(capitalFilamentId))!.Quantity);

        var filamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, 0);
        var maximumShips = checked((int)fixture.Dogma.Type(filamentTypeId)!.Attributes[CapacityAttributeId]);
        var fleet = new List<(AbyssalRunState Run, ClientSession Session)>();
        for (var i = 0; i <= maximumShips; i++)
        {
            var run = await rig.CreateRun(enter: false);
            fleet.Add((run, await rig.ConnectPublic(run)));
        }
        for (var i = 0; i < fleet.Count; i++)
        {
            var run = fleet[i].Run;
            await fixture.CrimewatchStore.SetSafetyAsync(run.Data.CreatorCharacterId, SafetyLevel.Partial, rig.Now);
            await AddFleetMemberAsync(rig, run.Data.CreatorCharacterId, run.Data.CreatorShipId, 2,
                i == 0 ? FleetStore.Leader : FleetStore.Member);
        }

        var filamentId = await CreateFilamentAsync(rig, fleet[0].Run.Data.CreatorCharacterId,
            fleet[0].Run.Data.CreatorShipId, filamentTypeId, 1);
        var overCapacity = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Service.CallAsync(ActivateCall(fleet[0].Session, filamentId, 43)).AsTask());

        Assert.Equal(PyList.Of(PyList.Of(new PyInt(24), PyList.Of(new PyInt(maximumShips)))), ErrorValue(overCapacity, "errors"));
        Assert.Equal(1, (await rig.Items.GetAsync(filamentId))!.Quantity);
        Assert.Equal(0, await ActivationCountAsync(rig));
        Assert.Empty(fixture.Simulation!.Moves);
        foreach (var (run, _) in fleet)
            Assert.Equal(0, (await fixture.CrimewatchStore.StateOfAsync(run.Data.CreatorCharacterId)).PvpUntilMs);
    }

    [PgFact]
    public async Task Host_rejects_activation_within_one_thousand_kilometers_of_a_station()
    {
        await using var rig = await AbyssalHostFlowTests.Rig.Create();
        using var fixture = await SetupAsync(rig);
        var run = await rig.CreateRun(enter: false);
        var session = await rig.ConnectPublic(run);
        var characterId = run.Data.CreatorCharacterId;
        var shipId = run.Data.CreatorShipId;
        var sourceSystemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var station = rig.Stations.InSystem(sourceSystemId).First();
        fixture.Simulation!.Positions[shipId] = [station.X, station.Y, station.Z];
        var filamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, 0);
        var filamentId = await CreateFilamentAsync(rig, characterId, shipId, filamentTypeId, 1);
        await fixture.CrimewatchStore.SetSafetyAsync(characterId, SafetyLevel.Partial, rig.Now);
        await AddFleetMemberAsync(rig, characterId, shipId);

        var proximity = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Service.CallAsync(ActivateCall(session, filamentId, 40)).AsTask());

        AssertErrorCode(proximity, 7);
        Assert.Equal(1, (await rig.Items.GetAsync(filamentId))!.Quantity);
        Assert.Equal(0, await ActivationCountAsync(rig));
    }

    [PgFact]
    public async Task Host_applies_the_proximity_limit_to_the_activating_pilot_only()
    {
        await using var rig = await AbyssalHostFlowTests.Rig.Create();
        using var fixture = await SetupAsync(rig);
        var run = await rig.CreateRun(enter: false);
        var session = await rig.ConnectPublic(run);
        var linkedRun = await rig.CreateRun(enter: false);
        var linkedSession = await rig.ConnectPublic(linkedRun);
        var characterId = run.Data.CreatorCharacterId;
        var linkedCharacterId = linkedRun.Data.CreatorCharacterId;
        var shipId = run.Data.CreatorShipId;
        var linkedShipId = linkedRun.Data.CreatorShipId;
        var sourceSystemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var station = rig.Stations.InSystem(sourceSystemId).First();
        fixture.Simulation!.Positions[shipId] = [station.X + 1_020_000, station.Y, station.Z];
        fixture.Simulation.Positions[linkedShipId] = [station.X + 990_000, station.Y, station.Z];
        await fixture.CrimewatchStore.SetSafetyAsync(characterId, SafetyLevel.Partial, rig.Now);
        await fixture.CrimewatchStore.SetSafetyAsync(linkedCharacterId, SafetyLevel.Partial, rig.Now);
        await AddFleetMemberAsync(rig, characterId, shipId, 3, FleetStore.Leader);
        await AddFleetMemberAsync(rig, linkedCharacterId, linkedShipId, 3, FleetStore.Member);
        var filamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, 0);
        var filamentId = await CreateFilamentAsync(rig, characterId, shipId, filamentTypeId, 1);

        Assert.Equal(PyNone.Instance, await fixture.Service.CallAsync(ActivateCall(session, filamentId, 41)));

        Assert.Equal(session.GetInt("solarsystemid"), linkedSession.GetInt("solarsystemid"));
        Assert.NotEqual(sourceSystemId, session.GetInt("solarsystemid"));
        Assert.Equal((ulong)linkedSession.SessionId, fixture.Simulation.PositionSessions[linkedShipId]);
    }

    [PgFact]
    public async Task Host_serializes_concurrent_retries_and_consumes_the_filament_once()
    {
        await using var rig = await AbyssalHostFlowTests.Rig.Create();
        using var fixture = await SetupAsync(rig);
        var run = await rig.CreateRun(enter: false);
        var session = await rig.ConnectPublic(run);
        var characterId = run.Data.CreatorCharacterId;
        var filamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, 1);
        var filamentId = await CreateFilamentAsync(rig, characterId, run.Data.CreatorShipId, filamentTypeId, 2);
        await fixture.CrimewatchStore.SetSafetyAsync(characterId, SafetyLevel.Partial, rig.Now);
        await AddFleetMemberAsync(rig, characterId, run.Data.CreatorShipId);
        var destination = fixture.Systems.Systems.First(system => system.Id != run.Data.SpaceLocationId &&
            system.Security <= 0 && system.RegionName != "Pochven" && system.Gates.Count > 0);
        await fixture.Bounties.RecordAsync(new BountyKill(characterId, destination.Id, 0, 0, 1m), rig.Now);

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Simulation!.MoveEntered = entered;
        fixture.Simulation.MoveRelease = release;
        var call = ActivateCall(session, filamentId, 46);
        var first = fixture.Service.CallAsync(call).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = fixture.Service.CallAsync(call).AsTask();
        try
        {
            await Task.Delay(100);
            Assert.Single(fixture.Simulation.Moves);
        }
        finally
        {
            release.TrySetResult(true);
        }
        Assert.Equal(new PyValue[] { PyNone.Instance, PyNone.Instance }, await Task.WhenAll(first, second));
        Assert.Single(fixture.Simulation.Moves);
        Assert.Equal(1, (await rig.Items.GetAsync(filamentId))!.Quantity);
        Assert.Equal(1, await ActivationCountAsync(rig));
        Assert.Equal(destination.Id, session.GetInt("solarsystemid"));
    }

    [PgFact]
    public async Task Host_retries_lost_simulation_replies_and_keeps_the_timer_across_restart_and_expiry()
    {
        await using var rig = await AbyssalHostFlowTests.Rig.Create();
        using var fixture = await SetupAsync(rig);
        var run = await rig.CreateRun(enter: false);
        var session = await rig.ConnectPublic(run);
        var sessionClosed = false;
        session.Closed += () => sessionClosed = true;
        var sourceSystemId = session.GetInt("solarsystemid");
        var characterId = run.Data.CreatorCharacterId;
        var filamentTypeId = await FilamentTypeAsync(rig, fixture.Dogma, 0);
        var filamentId = await CreateFilamentAsync(rig, characterId, run.Data.CreatorShipId, filamentTypeId, 2);
        await fixture.CrimewatchStore.SetSafetyAsync(characterId, SafetyLevel.Partial, rig.Now);
        await AddFleetMemberAsync(rig, characterId, run.Data.CreatorShipId);
        var activeSystem = fixture.Systems.Systems
            .First(system => system.Id != run.Data.SpaceLocationId && system.Security <= 0 &&
                system.RegionName != "Pochven" && system.Gates.Count > 0);
        await fixture.Bounties.RecordAsync(new BountyKill(characterId, activeSystem.Id, 0, 0, 1m), rig.Now);

        var call = ActivateCall(session, filamentId, 45);
        rig.Simulator.DropNextPublicMoveReply = true;
        var lostReply = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Service.CallAsync(call).AsTask());
        AssertErrorCode(lostReply, 22);
        Assert.Equal(1, (await rig.Items.GetAsync(filamentId))!.Quantity);
        Assert.Equal(sourceSystemId, session.GetInt("solarsystemid"));
        Assert.True(!sessionClosed, "The public session closed after a lost simulation reply.");
        Assert.Null(rig.Space.SolarSystemOf(session));
        var committedDestination = await ActivationDestinationAsync(rig);
        var committedShip = (await rig.Items.GetAsync(run.Data.CreatorShipId))!;
        var committedCharacter = (await rig.Characters.GetAsync(characterId))!;
        Assert.Equal((committedDestination, committedDestination), (committedShip.LocationId, committedCharacter.SolarSystemId));
        Assert.False(rig.Simulator.DropNextPublicMoveReply);
        Assert.Single(fixture.Simulation!.Moves);
        var snapshot = await rig.Ships.LoadAsync(characterId);
        Assert.NotNull(snapshot);
        Assert.Equal((committedDestination, CharacterShips.InSpaceFlag), (snapshot.Ship.LocationId, snapshot.Ship.Flag));
        var persisted = await fixture.CrimewatchStore.StateOfAsync(characterId);
        Assert.Equal(rig.Now + CombatState.PvpDurationMs, persisted.PvpUntilMs);

        PyValue? retryResult = null;
        var retryFailure = await Record.ExceptionAsync(async () => retryResult = await fixture.Service.CallAsync(call));
        Assert.True(retryFailure is null,
            $"retry failed: {retryFailure}; moves={fixture.Simulation.Moves.Count}; source={rig.Space.SolarSystemOf(session)}; " +
            $"session={session.GetInt("solarsystemid")}; problems={string.Join(" | ", rig.Problems)}");
        Assert.Equal(PyNone.Instance, retryResult);
        Assert.Equal(1, (await rig.Items.GetAsync(filamentId))!.Quantity);
        Assert.Equal(1, await ActivationCountAsync(rig));

        fixture.RestartCrimewatch(rig);
        rig.ClientNotifications.Clear();
        await fixture.CrimewatchRuntime.SyncAsync(characterId);
        Assert.Contains("OnPvpTimerUpdate", rig.ClientNotifications);
        rig.Now = persisted.PvpUntilMs + 1;
        rig.ClientNotifications.Clear();
        await fixture.CrimewatchRuntime.SyncAsync(characterId);
        Assert.Contains("OnPvpTimerUpdate", rig.ClientNotifications);
        Assert.Equal(1, (await rig.Items.GetAsync(filamentId))!.Quantity);
    }

    private static async Task<ActivationFixture> SetupAsync(AbyssalHostFlowTests.Rig rig)
    {
        var spaceSchema = rig.Schemas[4];
        var structureSchema = rig.Schemas[11];
        var bountySchema = rig.Schemas[12];
        await NeedlejackActivation.MigrateAsync(rig.Source, spaceSchema);
        await StructureStore.MigrateAsync(rig.Source, structureSchema);
        await PgBountyStore.MigrateAsync(rig.Source, bountySchema);

        var crimewatchStore = new PgCrimewatchStore(rig.Source, rig.Schemas[10]);
        var runtime = new CrimewatchRuntime(crimewatchStore, rig.Sessions, () => rig.Now, new CrimewatchCallbackStub());
        var systems = await SolarSystemCatalog.LoadAsync(rig.Sde);
        var dogma = await DogmaStatic.LoadAsync(rig.Sde);
        var fixture = new ActivationFixture(crimewatchStore, runtime, systems, dogma,
            new PgBountyStore(rig.Source, null!, bountySchema));
        SetHostField(rig.Host, "spaceSchema", spaceSchema);
        SetHostField(rig.Host, "solarSystems", systems);
        SetHostField(rig.Host, "structureStore", new StructureStore(rig.Source, structureSchema));
        SetHostField(rig.Host, "deployedStore", new PgDeployedStore(rig.Source, spaceSchema));
        SetHostField(rig.Host, "bountyStore", fixture.Bounties);
        SetHostField(rig.Host, "crimewatchStore", crimewatchStore);
        SetHostField(rig.Host, "crimewatchRuntime", runtime);
        SetHostField(rig.Host, "now", (Func<long>)(() => rig.Now));
        var guestField = typeof(CharacterServicesComposition).GetField("stationGuestLedger", BindingFlags.Instance | BindingFlags.NonPublic)!;
        guestField.SetValue(rig.Host, RuntimeHelpers.GetUninitializedObject(guestField.FieldType));
        var simulation = DispatchProxy.Create<ISimulation, NeedlejackSimulation>();
        fixture.Simulation = (NeedlejackSimulation)(object)simulation;
        rig.Simulator.Inner = simulation;
        var spaceRpc = typeof(CharacterServicesComposition).GetMethod("SpaceRpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var services = Assert.IsAssignableFrom<IReadOnlyList<IService>>(spaceRpc.Invoke(rig.Host, null));
        fixture.Service = Assert.Single(services, service => service.Name == "randomJumpMgr");
        return fixture;
    }

    private static void SetHostField(CharacterServicesComposition host, string name, object value) =>
        typeof(CharacterServicesComposition).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);

    private static async Task<int> FilamentTypeAsync(AbyssalHostFlowTests.Rig rig, DogmaStatic dogma, int mode)
    {
        var rows = await rig.Sde.ReadAsync("types", cancellationToken: CancellationToken.None);
        await dogma.EnsureTypesAsync(rows.Where(row => row.Doc.GetProperty("groupID").GetInt32() == NeedlejackGroupId)
            .Select(row => (int)row.Key));
        foreach (var row in rows)
        {
            if (row.Doc.GetProperty("groupID").GetInt32() != NeedlejackGroupId) continue;
            var type = dogma.Type((int)row.Key);
            if (type is not null && type.Attributes.TryGetValue(CapacityAttributeId, out var capacity) && capacity > 0 &&
                type.Attributes.TryGetValue(ActivityAttributeId, out var activity) && activity == mode)
                return (int)row.Key;
        }
        throw new InvalidOperationException($"The pinned SDE has no Needlejack type for activity mode {mode}.");
    }

    private static async Task<int> CapitalShipTypeAsync(AbyssalHostFlowTests.Rig rig, DogmaStatic dogma)
    {
        var groups = await rig.Sde.ReadAsync("groups", cancellationToken: CancellationToken.None);
        var shipGroupIds = groups.Where(row => row.Doc.TryGetProperty("categoryID", out var category) && category.GetInt32() == 6)
            .Select(row => (int)row.Key).ToHashSet();
        var rows = await rig.Sde.ReadAsync("types", cancellationToken: CancellationToken.None);
        var shipTypeIds = rows.Where(row => row.Doc.TryGetProperty("groupID", out var group) && shipGroupIds.Contains(group.GetInt32()))
            .Select(row => (int)row.Key).ToArray();
        await dogma.EnsureTypesAsync(shipTypeIds);
        var capitalTypeId = shipTypeIds.FirstOrDefault(typeId => dogma.Type(typeId) is { CategoryId: 6 } hull &&
            hull.Attributes.TryGetValue(CapitalSizeAttributeId, out var capitalSize) && capitalSize > 0);
        if (capitalTypeId <= 0)
            throw new InvalidOperationException("The pinned SDE has no ship with a positive capital-size attribute.");
        return capitalTypeId;
    }

    private static async Task<long> CreateFilamentAsync(AbyssalHostFlowTests.Rig rig, long characterId, long shipId, int typeId, long quantity)
    {
        long itemId = 0;
        await rig.Transaction(async (connection, transaction) =>
            itemId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(typeId, characterId, shipId, CharacterShips.CargoFlag, quantity)));
        return itemId;
    }

    private static Task AddFleetMemberAsync(
        AbyssalHostFlowTests.Rig rig, long characterId, long shipId, int fleetId = 1, int role = FleetStore.Leader) =>
        rig.Transaction(async (connection, transaction) =>
        {
            await using (var fleet = new NpgsqlCommand($"INSERT INTO {rig.Schemas[9]}.fleets (fleet_id) VALUES ($1) ON CONFLICT DO NOTHING", connection, transaction))
            {
                fleet.Parameters.AddWithValue(fleetId);
                await fleet.ExecuteNonQueryAsync();
            }
            var ship = (await rig.Items.GetAsync(shipId))!;
            await using var member = new NpgsqlCommand($"""
                INSERT INTO {rig.Schemas[9]}.fleet_members (
                    fleet_id, character_id, wing_id, squad_id, role, job, accepts_fleet_warp,
                    accepts_conduit_jumps, accepts_fleet_regroups, ship_type_id, joined_at)
                VALUES ($1, $2, -1, -1, $3, 0, true, true, true, $4, 0)
                """, connection, transaction);
            member.Parameters.AddWithValue(fleetId);
            member.Parameters.AddWithValue(characterId);
            member.Parameters.AddWithValue((short)role);
            member.Parameters.AddWithValue(ship.TypeId);
            await member.ExecuteNonQueryAsync();
        });

    private static ServiceCall ActivateCall(ClientSession session, long itemId, long callId) => new()
    {
        Method = "ActivateRandomJumpFilament",
        Args = [new PyLong(itemId)],
        CallId = new PyLong(callId),
        Session = session,
    };

    private static async Task<long> ActivationCountAsync(AbyssalHostFlowTests.Rig rig)
    {
        await using var command = rig.Source.CreateCommand($"SELECT count(*) FROM {rig.Schemas[4]}.needlejack_activations");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ActivationDestinationAsync(AbyssalHostFlowTests.Rig rig)
    {
        await using var command = rig.Source.CreateCommand($"SELECT destination_system_id FROM {rig.Schemas[4]}.needlejack_activations");
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static void AssertErrorCode(MachoWrappedException error, int expected)
    {
        Assert.Equal("RandomJumpError", error.Error.UserErrorMessage);
        var errors = Assert.IsType<PyList>(ErrorValue(error, "errors"));
        var first = Assert.IsType<PyList>(errors.Items[0]);
        Assert.Equal(new PyInt(expected), first.Items[0]);
        Assert.IsType<PyList>(first.Items[1]);
    }

    private static PyValue ErrorValue(MachoWrappedException error, string name)
    {
        var payload = Assert.IsType<PyObjectEx>(error.Error.Payload);
        var header = Assert.IsType<PyTuple>(payload.Header);
        var args = Assert.IsType<PyTuple>(header.Items[1]);
        var values = Assert.IsType<PyDict>(args.Items[1]);
        return Assert.Single(values.Entries, entry => entry.Key == new PyStr(name)).Value;
    }

    private sealed class ActivationFixture(
        PgCrimewatchStore crimewatchStore,
        CrimewatchRuntime crimewatchRuntime,
        SolarSystemCatalog systems,
        DogmaStatic dogma,
        PgBountyStore bounties) : IDisposable
    {
        public PgCrimewatchStore CrimewatchStore { get; } = crimewatchStore;
        public CrimewatchRuntime CrimewatchRuntime { get; private set; } = crimewatchRuntime;
        public SolarSystemCatalog Systems { get; } = systems;
        public DogmaStatic Dogma { get; } = dogma;
        public PgBountyStore Bounties { get; } = bounties;
        public IService Service { get; set; } = null!;
        public NeedlejackSimulation? Simulation { get; set; }

        public void RestartCrimewatch(AbyssalHostFlowTests.Rig target)
        {
            CrimewatchRuntime.Dispose();
            CrimewatchRuntime = new CrimewatchRuntime(CrimewatchStore, target.Sessions, () => target.Now, new CrimewatchCallbackStub());
            SetHostField(target.Host, "crimewatchRuntime", CrimewatchRuntime);
        }

        public void Dispose() => CrimewatchRuntime.Dispose();
    }

    private sealed class CrimewatchCallbackStub : ICrimewatchPersistenceCallbacks
    {
        public Task<IReadOnlyList<SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(long characterId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SecurityStatusTransaction>>([]);

        public Task<long?> StartDuelChallengeAsync(long challengerCharacterId, long targetCharacterId, long challengerCorporationId,
            long challengerAllianceId, CancellationToken cancellationToken) => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<LimitedEngagement>> LimitedEngagementsAsync(long nowUnixMs, long? characterId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LimitedEngagement>>([]);

        public Task UpsertLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expiresAtUnixMs, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> ExpireLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expectedExpiresAtUnixMs,
            long nowUnixMs, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> RespondToDuelChallengeAsync(long responderCharacterId, long challengerCharacterId,
            long challengeExpiresAtFileTime, bool accept, long? engagementExpiresAtUnixMs, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    public class NeedlejackSimulation : DispatchProxy
    {
        public ConcurrentQueue<SimMovePublicParticipant> Moves { get; } = new();
        public ConcurrentDictionary<long, double[]> Positions { get; } = new();
        public ConcurrentDictionary<long, ulong> PositionSessions { get; } = new();
        public TaskCompletionSource<bool>? MoveEntered { get; set; }
        public TaskCompletionSource<bool>? MoveRelease { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (method.Name == "get_IsConnected") return true;
            if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
            if (method.Name == nameof(ISimulation.EnterAsync)) return Task.FromResult<string?>(null);
            if (method.Name == nameof(ISimulation.MovePublicParticipantInOrderAsync))
                return MovePublicAsync((SimMovePublicParticipant)args![0]!,
                    (Action<SimMovePublicParticipantReply>)args[1]!);
            if (method.Name == "CommandAsync")
            {
                var sessionId = (ulong)args![0]!;
                var command = (byte)args![1]!;
                var commandArgs = (double[])args[2]!;
                var shipId = commandArgs.Length > 0 ? (long)commandArgs[0] : 0;
                if (command == SimCommands.PositionOf)
                    PositionSessions[shipId] = sessionId;
                var position = command == SimCommands.PositionOf && Positions.TryGetValue(shipId, out var found)
                    ? found
                    : new double[] { 1e15, 1e15, 1e15 };
                return Task.FromResult(new CommandReply(true, 0, null)
                {
                    Position = position,
                    Direction = [0, 0, 1],
                    Condition = SimCondition.Full,
                });
            }
            if (method.ReturnType == typeof(Task<string>)) return Task.FromResult<string?>(null);
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType == typeof(void)) return null;
            throw new NotSupportedException(method.Name);
        }

        private async Task<SimMovePublicParticipantReply> MovePublicAsync(
            SimMovePublicParticipant move, Action<SimMovePublicParticipantReply> notify)
        {
            Moves.Enqueue(move);
            MoveEntered?.TrySetResult(true);
            if (MoveRelease is { } release) await release.Task.ConfigureAwait(false);
            var reply = new SimMovePublicParticipantReply(move.RequestId, null);
            notify(reply);
            return reply;
        }
    }
}
