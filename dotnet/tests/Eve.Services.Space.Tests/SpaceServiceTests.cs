using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;



public sealed class FleetLoginHookTests
{
    [Fact]
    public async Task Fleet_membership_is_folded_into_login_session_attributes_and_cleared_without_a_fleet()
    {
        var member = new Eve.Services.Space.FleetMemberSnapshot(2001, 3001, 4001, 2, 0)
        {
            FleetId = 1001,
        };
        var hook = new Eve.Services.Space.FleetLoginHook(
            (_, _) => Task.FromResult<Eve.Services.Space.FleetMemberSnapshot?>(member));

        var attributes = await hook.GetSessionAttributesAsync(
            new Eve.Services.ClientSession(), 2001, CancellationToken.None);
        var values = attributes.ToDictionary(attribute => attribute.Name, attribute => attribute.Value, StringComparer.Ordinal);
        Assert.Equal(new Eve.Marshal.PyLong(1001), values["fleetid"]);
        Assert.Equal(new Eve.Marshal.PyInt(2), values["fleetrole"]);
        Assert.Equal(new Eve.Marshal.PyLong(3001), values["wingid"]);
        Assert.Equal(new Eve.Marshal.PyLong(4001), values["squadid"]);

        var noFleet = new Eve.Services.Space.FleetLoginHook(
            (_, _) => Task.FromResult<Eve.Services.Space.FleetMemberSnapshot?>(null));
        var cleared = await noFleet.GetSessionAttributesAsync(
            new Eve.Services.ClientSession(), 2001, CancellationToken.None);
        Assert.All(cleared, attribute => Assert.Equal(Eve.Marshal.PyNone.Instance, attribute.Value));
    }
}

public sealed class CharacterEntryInventoryBootstrapTests
{
    [Fact]
    public async Task First_active_space_ship_list_primes_visuals_once()
    {
        var sim = new FakeSim();
        using var space = new SpaceSessions(sim, null!, null!, null!, null!, () => 1_700_000_000_000);
        var session = new Eve.Services.ClientSession();
        session.Set("loginInventoryBootstrapPending", new Eve.Marshal.PyInt(1));

        await space.OnInventoryRpcAsync(session, "List", stationHangar: false, activeSpaceShipInventoryList: true, CancellationToken.None);
        Assert.Equal(SimCommands.PrimeLoginBallparkVisuals, Assert.Single(sim.Heard.OfType<SimCommand>()).Code);
        Assert.Equal(Eve.Marshal.PyNone.Instance, session.Get("loginInventoryBootstrapPending"));

        await space.OnInventoryRpcAsync(session, "List", stationHangar: false, activeSpaceShipInventoryList: true, CancellationToken.None);
        Assert.Single(sim.Heard.OfType<SimCommand>());
    }

    [Fact]
    public async Task A_docked_hangar_list_leaves_the_shipid_the_selection_gave()
    {
        // the active ship is in the selection's session change (no deferral): the hangar list does not change it again
        using var space = new SpaceSessions(new FakeSim(), null!, null!, null!, null!, () => 1_700_000_000_000);
        var session = new Eve.Services.ClientSession();
        session.Set("shipid", new Eve.Marshal.PyLong(9_998_400_000_001));

        await space.OnInventoryRpcAsync(session, "List", stationHangar: true, activeSpaceShipInventoryList: false, CancellationToken.None);

        session.RunAfterResponse();
        Assert.Equal(9_998_400_000_001, session.GetInt("shipid"));
    }
}

public class SpaceServiceTests
{
    private static long Id(PyValue value) => Events.Number(value);

    [Fact]
    public async Task Admin_get_bubble_id_requires_view_role_and_uses_available_bubble_sources()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);

        var denied = await rig.BeyonceCallAsync(session, "AdminGetBubbleID", null, new PyLong(character.ShipId));
        Assert.Same(PyNone.Instance, denied);

        session.Set("role", new PyLong(1));
        Assert.Same(PyNone.Instance, await rig.BeyonceCallAsync(
            session, "AdminGetBubbleID", null, new PyLong(character.ShipId)));

        session.Set("role", new PyLong(18014398509481984L));
        session.Set("shipid", new PyLong(character.ShipId));
        session.Set("ballparkid", new PyLong(987653));
        var ownShipBubble = await rig.BeyonceCallAsync(
            session, "AdminGetBubbleID", null, new PyLong(character.ShipId));
        Assert.Equal(new PyLong(987653), ownShipBubble);

        const long otherItemId = 987654321;
        long requestedItem = 0;
        rig.Beyonce.BubbleIdForItem = (_, itemId, _) =>
        {
            requestedItem = itemId;
            return ValueTask.FromResult<long?>(987654);
        };

        var bubble = await rig.BeyonceCallAsync(session, "AdminGetBubbleID", null, new PyLong(otherItemId));
        Assert.Equal(new PyLong(987654), bubble);
        Assert.Equal(otherItemId, requestedItem);

        rig.Beyonce.BubbleIdForItem = (_, _, _) => ValueTask.FromResult<long?>(null);
        Assert.Same(PyNone.Instance, await rig.BeyonceCallAsync(
            session, "AdminGetBubbleID", null, new PyLong(otherItemId)));
        Assert.True(((Eve.Services.IService)rig.Beyonce).HasMethod("AdminGetBubbleID"));
    }

    [Fact]
    public async Task Get_cyno_jammer_state_uses_the_session_system_and_returns_filetime_or_none()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        session.Set("solarsystemid2", new PyInt(0));
        session.Set("solarsystemid", new PyInt(30000142));

        Assert.Same(PyNone.Instance, await rig.BeyonceCallAsync(session, "GetCynoJammerState"));

        const long activationFileTime = 133900000000000000L;
        long requestedSystem = 0;
        rig.Beyonce.CynoJammerOnlineSimTimeForSystem = (systemId, _) =>
        {
            requestedSystem = systemId;
            return ValueTask.FromResult<long?>(activationFileTime);
        };
        var state = await rig.BeyonceCallAsync(session, "GetCynoJammerState");

        Assert.Equal(new PyLong(activationFileTime), state);
        Assert.Equal(30000142, requestedSystem);

        rig.Beyonce.CynoJammerOnlineSimTimeForSystem = (_, _) => ValueTask.FromResult<long?>(0);
        Assert.Equal(new PyLong(0), await rig.BeyonceCallAsync(session, "GetCynoJammerState"));
        Assert.True(((Eve.Services.IService)rig.Beyonce).HasMethod("GetCynoJammerState"));
    }

    [Fact]
    public void Ship_dirt_timestamp_is_a_current_EVE_filetime()
    {
        const long nowUnixMs = 1_000;
        var service = new ShipService(null!, () => nowUnixMs);

        Assert.Equal(new PyLong(116444736000000000L + nowUnixMs * 10_000L), service.GetDirtTimestamp(null!));
    }

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> DockedAsync(
        TimeSpan? protection = null, Eve.Scheduler.LocalTimers? localTimers = null)
    {
        var rig = await Rig.CreateAsync(protection, localTimers: localTimers);
        var character = await rig.NewCharacterAsync();
        return (rig, character, await rig.SessionAsync(character));
    }

    [PgFact]
    public async Task Safe_logoff_closes_the_space_session_after_its_timer()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timers = new Eve.Scheduler.LocalTimers(delay: (duration, token) =>
        {
            if (duration != TimeSpan.FromSeconds(30)) return Task.CompletedTask;
            started.TrySetResult();
            return release.Task.WaitAsync(token);
        });
        var (rig, character, session) = await DockedAsync(localTimers: timers);
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var oid = await rig.BindShipInSpaceAsync(session);

        var result = await rig.ShipCallAsync(session, "SafeLogoff", oid);

        Assert.Empty(Assert.IsType<PyList>(result).Items);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Rig.UntilAsync(() => !rig.Space.IsInSpace(session));

        var recovered = false;
        for (var attempt = 0; attempt < 200 && !recovered; attempt++)
        {
            var ship = await rig.Items.GetAsync(character.ShipId);
            recovered = ship is not null
                && ship.LocationId == Rig.Station
                && ship.Flag == Eve.Inventory.ItemFlags.Hangar;
            if (!recovered)
                await Task.Delay(25);
        }
        Assert.True(recovered, "the logged-off ship should be in the station hangar");
    }

    [PgFact]
    public async Task Aborting_safe_logoff_keeps_the_session_and_ship_in_space()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timers = new Eve.Scheduler.LocalTimers(delay: (duration, token) =>
        {
            if (duration != TimeSpan.FromSeconds(30)) return Task.CompletedTask;
            started.TrySetResult();
            return release.Task.WaitAsync(token);
        });
        var (rig, character, session) = await DockedAsync(localTimers: timers);
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var oid = await rig.BindShipInSpaceAsync(session);
        var closed = false;
        session.Closed += () => closed = true;

        Assert.Empty(Assert.IsType<PyList>(await rig.ShipCallAsync(session, "SafeLogoff", oid)).Items);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.ShipCallAsync(session, "AbortSafeLogoff", oid);
        release.TrySetResult();
        await Task.Delay(25);

        Assert.False(closed);
        Assert.True(rig.Space.IsInSpace(session));
        Assert.Equal(character.ShipId, session.GetInt("shipid"));
    }

    [PgFact]
    public async Task A_movement_command_aborts_a_running_safe_logoff()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timers = new Eve.Scheduler.LocalTimers(delay: (duration, token) =>
        {
            if (duration != TimeSpan.FromSeconds(30)) return Task.CompletedTask;
            started.TrySetResult();
            return release.Task.WaitAsync(token);
        });
        var (rig, _, session) = await DockedAsync(localTimers: timers);
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var oid = await rig.BindShipInSpaceAsync(session);
        var closed = false;
        session.Closed += () => closed = true;

        Assert.Empty(Assert.IsType<PyList>(await rig.ShipCallAsync(session, "SafeLogoff", oid)).Items);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Space.Command(session, SimCommands.Stop);
        release.TrySetResult();
        await Task.Delay(25);

        Assert.False(closed);
        Assert.True(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task Ejecting_in_space_leaves_the_hull_and_boards_it_back()
    {
        var sessionChangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSessionChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionChangeTimerCount = 0;
        using var timers = new Eve.Scheduler.LocalTimers(delay: async (after, token) =>
        {
            if (after == TimeSpan.FromSeconds(10) && Interlocked.Increment(ref sessionChangeTimerCount) > 1)
            {
                sessionChangeStarted.TrySetResult();
                await releaseSessionChange.Task.WaitAsync(token);
            }
        });
        var (rig, character, session) = await DockedAsync(localTimers: timers);
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var shipId = character.ShipId;
        var systemId = (int)session.GetInt("solarsystemid")!.Value;
        var oid = await rig.BindShipInSpaceAsync(session);

        var ejected = await rig.ShipCallAsync(session, "Eject", oid);

        Assert.IsType<PyTuple>(ejected);
        var capsuleId = (await rig.Characters.GetAsync(character.CharacterId))!.ShipId!.Value;
        Assert.NotEqual(shipId, capsuleId);
        Assert.Equal(capsuleId, session.GetInt("shipid"));
        Assert.Equal((systemId, 0),
            ((await rig.Items.GetAsync(shipId))!.LocationId, (await rig.Items.GetAsync(shipId))!.Flag));
        Assert.Equal((systemId, 0, Eve.Ships.CharacterShips.CapsuleTypeId),
            ((await rig.Items.GetAsync(capsuleId))!.LocationId, (await rig.Items.GetAsync(capsuleId))!.Flag, (await rig.Items.GetAsync(capsuleId))!.TypeId));

        await sessionChangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var earlyBoard = await rig.ShipCallAsync(session, "Board", oid, new PyLong(shipId), new PyLong(capsuleId));
        Assert.IsType<PyNone>(earlyBoard);
        releaseSessionChange.TrySetResult();
        await Task.Delay(25);

        var board = await rig.ShipCallAsync(session, "Board", oid, new PyLong(shipId), new PyLong(capsuleId));

        Assert.IsType<PyTuple>(board);
        Assert.Equal(shipId, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Equal(shipId, session.GetInt("shipid"));
        Assert.Equal((systemId, 0),
            ((await rig.Items.GetAsync(shipId))!.LocationId, (await rig.Items.GetAsync(shipId))!.Flag));
        Assert.Equal((systemId, 0),
            ((await rig.Items.GetAsync(capsuleId))!.LocationId, (await rig.Items.GetAsync(capsuleId))!.Flag));
    }


    [PgFact]
    public async Task Ejecting_from_a_battleship_with_a_usable_frigate_escape_bay_respawns_in_the_frigate()
    {
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        var escapeCapacityId = rig.Statics.AttributeId("frigateEscapeBayCapacity");
        Assert.NotNull(escapeCapacityId);
        var battleship = (await rig.Statics.TypesInGroupsAsync(["Battleship"]))
            .First(type => type.Attributes.GetValueOrDefault(escapeCapacityId.Value) > 0);
        var frigate = (await rig.Statics.TypesInGroupsAsync(["Frigate", "Assault Frigate", "Electronic Attack Ship", "Logistics Frigate"]))
            .First(type => type.CategoryId == 6);
        var character = await rig.NewCharacterAsync(shipTypeId: battleship.TypeId);
        foreach (var skillId in frigate.RequiredSkills)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(skillId, 5, 0)]);
            await transaction.CommitAsync();
        }
        var session = await rig.SessionAsync(character);
        await InSpaceAsync(rig, session);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var systemId = session.GetInt("solarsystemid")!.Value;
        long frigateId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            frigateId = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(frigate.TypeId, character.CharacterId, character.ShipId,
                    Eve.Ships.CharacterShips.FrigateEscapeBayFlag, 1, true));
            await transaction.CommitAsync();
        }

        var ejected = await rig.Space.EjectAsync(session, CancellationToken.None);

        Assert.True(ejected is PyTuple, string.Join(Environment.NewLine, rig.Log));
        Assert.Equal(frigateId, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Equal(frigateId, session.GetInt("shipid"));
        var escaped = (await rig.Items.GetAsync(frigateId))!;
        Assert.Equal((systemId, Eve.Ships.CharacterShips.InSpaceFlag, frigate.TypeId), (escaped.LocationId, escaped.Flag, escaped.TypeId));
        Assert.Contains(rig.Sim.Heard.OfType<SimShipChanged>(), changed => changed.Ship.ShipId == character.ShipId && changed.Ship.Vitals is not null);
        Assert.Contains(rig.Sim.Heard.OfType<SimRespawn>(), respawn => respawn.Ship.ShipId == escaped.Id);
    }

    [PgFact]
    public async Task Destroying_an_abandoned_ejected_ship_persists_loss_without_replacing_the_pilots_capsule()
    {
        using var timers = new Eve.Scheduler.LocalTimers(delay: (_, _) => Task.CompletedTask);
        var (rig, character, session) = await DockedAsync(localTimers: timers);
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var shipId = character.ShipId;
        var lost = new TaskCompletionSource<ShipLoss>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Space.ShipLost += loss => lost.TrySetResult(loss);

        Assert.IsType<PyTuple>(await rig.Space.EjectAsync(session, CancellationToken.None));
        var capsuleId = (await rig.Characters.GetAsync(character.CharacterId))!.ShipId!.Value;

        rig.Sim.Report(new SimDestroyed(
            (ulong)session.SessionId,
            shipId,
            0,
            [0, 0, 0],
            [1, 0, 0],
            1,
            0));

        var loss = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(character.CharacterId, loss.CharacterId);
        Assert.Equal(shipId, loss.ShipId);
        Assert.Equal((await rig.Characters.GetAsync(character.CharacterId))!.ShipId, capsuleId);
        Assert.Null(await rig.Items.GetAsync(shipId));
    }


    [PgFact]
    public async Task Ejecting_that_loses_the_mechanics_hull_bonus_destroys_the_abandoned_ship_through_the_loss_path()
    {
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        var character = await rig.NewCharacterAsync(shipTypeId: 582); // Bantam has ordinary SDE hitpoints, without the Cockroach QA hull modifier.
        var session = await rig.SessionAsync(character);
        await InSpaceAsync(rig, session);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        await rig.Statics.EnsureTypesAsync([3392]);
        var mechanics = rig.Statics.Type(3392);
        Assert.Equal("Mechanics", mechanics?.Name);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId,
                [new Eve.Skills.CharacterSkill(mechanics!.TypeId, 5, 0)]);
            await transaction.CommitAsync();
        }
        var shipId = character.ShipId;
        var hullAttribute = rig.Statics.AttributeId("hp");
        Assert.NotNull(hullAttribute);
        var beforeFit = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!;
        var oldHull = beforeFit.World.Get(shipId, hullAttribute.Value);
        var baseHull = rig.Statics.Type(beforeFit.Ship.TypeId)!.Attributes[hullAttribute.Value];
        Assert.Equal(baseHull * 1.25, oldHull, 2);
        await rig.Modules.SetShipConditionAsync(shipId, new Eve.Fitting.ShipCondition(0.9, 1, 1, 0, false));

        var lost = new TaskCompletionSource<ShipLoss>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Space.ShipLost += loss => lost.TrySetResult(loss);
        Assert.IsType<PyTuple>(await rig.Space.EjectAsync(session, CancellationToken.None));
        var capsuleId = (await rig.Characters.GetAsync(character.CharacterId))!.ShipId!.Value;

        var heard = rig.Sim.Heard.ToArray();
        var changedIndex = Array.FindIndex(heard, message => message is SimShipChanged changed && changed.Ship.ShipId == shipId);
        var leaveIndex = Array.FindIndex(heard, message => message is SimLeave leave && leave.Why == LeaveWhy.Ejected);
        var destroyIndex = Array.FindIndex(heard, message => message is SimDestroy destroy && destroy.ShipId == shipId);
        Assert.True(changedIndex >= 0 && changedIndex < leaveIndex && leaveIndex < destroyIndex);
        var changed = rig.Sim.Heard.OfType<SimShipChanged>().Single(message => message.Ship.ShipId == shipId);
        Assert.Equal(baseHull, changed.Ship.Vitals!.StructureHp);
        Assert.True(changed.Ship.Condition!.Damage * changed.Ship.Vitals.StructureHp >= changed.Ship.Vitals.StructureHp);

        rig.Sim.Report(new SimDestroyed((ulong)session.SessionId, shipId, 0, [0, 0, 0], [1, 0, 0], 1, 0));
        var loss = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(shipId, loss.ShipId);
        Assert.Equal(capsuleId, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Null(await rig.Items.GetAsync(shipId));
    }

    [PgFact]
    public async Task An_outsider_cannot_board_a_targeted_ship_but_boarding_after_unlock_invalidates_insurance()
    {
        using var timers = new Eve.Scheduler.LocalTimers(delay: (_, _) => Task.CompletedTask);
        var (rig, owner, ownerSession) = await DockedAsync(localTimers: timers);
        await using var _ = rig;
        var outsider = await rig.NewCharacterAsync("Boarding Outsider", account: 2);
        var outsiderSession = await rig.SessionAsync(outsider, id: 2);
        var observer = await rig.NewCharacterAsync("Targeting Observer", account: 3);
        var observerSession = await rig.SessionAsync(observer, id: 3);

        await InSpaceAsync(rig, ownerSession);
        await InSpaceAsync(rig, outsiderSession);
        await InSpaceAsync(rig, observerSession);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);

        var ownerShipId = owner.ShipId;
        var outsiderShipId = outsider.ShipId;
        rig.Sim.PositionAnswers[ownerShipId] = [0, 0, 0];
        rig.Sim.PositionAnswers[outsiderShipId] = [6_500, 0, 0];

        Assert.IsType<PyTuple>(await rig.Space.EjectAsync(ownerSession, CancellationToken.None));
        await Task.Delay(25);
        var ownerCapsuleId = (await rig.Characters.GetAsync(owner.CharacterId))!.ShipId!.Value;
        rig.Sim.PositionAnswers[ownerCapsuleId] = [0, 0, 0];
        Assert.IsType<PyTuple>(await rig.Space.EjectAsync(outsiderSession, CancellationToken.None));
        await Task.Delay(25);
        var outsiderCapsuleId = (await rig.Characters.GetAsync(outsider.CharacterId))!.ShipId!.Value;
        rig.Sim.PositionAnswers[outsiderCapsuleId] = [6_500, 0, 0];

        rig.Sim.TargetAnswer = new CommandReply(true, 0, null) { Pending = false, Targets = [ownerShipId] };
        Assert.True((await rig.Space.OrderAsync(
            observerSession, TargetingOrder.Lock, [ownerShipId], CancellationToken.None))!.Ok);

        ShipBoarded? insuranceBoarding = null;
        rig.Space.InvalidateInsuranceForBoarding = (boarding, _) =>
        {
            insuranceBoarding = boarding;
            return ValueTask.CompletedTask;
        };

        var ownerBoard = await rig.Space.BoardAsync(
            ownerSession, ownerShipId, ownerCapsuleId, CancellationToken.None);
        Assert.IsType<PyTuple>(ownerBoard);
        Assert.Null(insuranceBoarding);
        await Task.Delay(25);

        Assert.IsType<PyTuple>(await rig.Space.EjectAsync(ownerSession, CancellationToken.None));
        await Task.Delay(25);
        ownerCapsuleId = (await rig.Characters.GetAsync(owner.CharacterId))!.ShipId!.Value;
        rig.Sim.PositionAnswers[ownerCapsuleId] = [0, 0, 0];

        var lockedBoard = await rig.Space.BoardAsync(
            outsiderSession, ownerShipId, outsiderCapsuleId, CancellationToken.None);
        Assert.IsType<PyNone>(lockedBoard);

        rig.Sim.TargetAnswer = new CommandReply(true, 0, null) { Pending = false, Targets = [] };
        await rig.Space.OrderAsync(observerSession, TargetingOrder.Remove, [ownerShipId], CancellationToken.None);

        var outsiderBoard = await rig.Space.BoardAsync(
            outsiderSession, ownerShipId, outsiderCapsuleId, CancellationToken.None);

        Assert.IsType<PyTuple>(outsiderBoard);
        Assert.Equal(owner.CharacterId, insuranceBoarding!.PreviousOwnerCharacterId);
        Assert.Equal(outsider.CharacterId, insuranceBoarding.ActorCharacterId);
        Assert.Equal((await rig.Characters.GetAsync(outsider.CharacterId))!.CorporationId, insuranceBoarding.ActorCorporationId);
        Assert.True(insuranceBoarding.BoardedAtFileTime > 0);
        Assert.Equal(ownerShipId, (await rig.Characters.GetAsync(outsider.CharacterId))!.ShipId);
    }

    [PgFact]
    public async Task Boarding_switches_the_active_ship_once_and_keeps_both_hulls_in_the_hangar()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var ships = (await rig.Ships.GrantAsync(character.CharacterId, Rig.Cockroach, 2, singleton: true))!;
        var first = ships[0].After;
        var second = ships[1].After;
        var oid = await rig.BindShipAsync(session);
        var beforeSimulation = rig.Sim.Heard.Count;

        var results = await Task.WhenAll(
            rig.ShipCallAsync(session, "Board", oid, new PyLong(first.Id), new PyLong(character.ShipId)),
            rig.ShipCallAsync(session, "Board", oid, new PyLong(second.Id), new PyLong(character.ShipId)));

        var reply = Assert.IsType<PyTuple>(Assert.Single(results, result => result is PyTuple));
        Assert.Single(results, result => result is PyNone);
        Assert.Equal(4, reply.Items.Count);
        Assert.All(reply.Items, item => Assert.IsType<PyDict>(item));

        var activeShipId = (await rig.Characters.GetAsync(character.CharacterId))!.ShipId;
        Assert.Contains(activeShipId, new long?[] { first.Id, second.Id });
        Assert.Equal(activeShipId, session.GetInt("shipid"));
        var hulls = new[]
        {
            (await rig.Items.GetAsync(character.ShipId))!,
            (await rig.Items.GetAsync(first.Id))!,
            (await rig.Items.GetAsync(second.Id))!,
        };
        Assert.All(hulls, hull => Assert.Equal((Rig.Station, Eve.Inventory.ItemFlags.Hangar), (hull.LocationId, hull.Flag)));
        Assert.Equal(beforeSimulation, rig.Sim.Heard.Count);
    }

    [PgFact]
    public async Task Boarding_with_a_stale_current_ship_id_is_refused_without_changing_session_or_custody()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var offers = await rig.Ships.GrantAsync(character.CharacterId, Rig.Cockroach, 1, singleton: true) ?? [];
        var offered = Assert.Single(offers);
        var oid = await rig.BindShipAsync(session);

        var result = await rig.ShipCallAsync(
            session,
            "Board",
            oid,
            new PyLong(offered.After.Id),
            new PyLong(character.ShipId + 1));

        Assert.Equal(PyNone.Instance, result);
        Assert.Equal(character.ShipId, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Equal(character.ShipId, session.GetInt("shipid"));
        var hulls = new[]
        {
            (await rig.Items.GetAsync(character.ShipId))!,
            (await rig.Items.GetAsync(offered.After.Id))!,
        };
        Assert.All(hulls, hull => Assert.Equal((Rig.Station, Eve.Inventory.ItemFlags.Hangar), (hull.LocationId, hull.Flag)));
    }

    [PgFact]
    public async Task Leaving_a_ship_prepares_a_capsule_then_board_switches_while_storing_the_hull()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        var oid = await rig.BindShipAsync(session);

        var leave = await rig.ShipCallAsync(session, "LeaveShip", oid, new PyLong(character.ShipId));
        var capsuleId = leave switch
        {
            PyInt value => value.Value,
            PyLong value => value.Value,
            _ => 0,
        };

        Assert.True(capsuleId > 0);
        Assert.Equal(capsuleId, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Equal(capsuleId, session.GetInt("shipid"));
        var capsule = (await rig.Items.GetAsync(capsuleId))!;
        Assert.Equal((Rig.Station, Eve.Inventory.ItemFlags.Hangar), (capsule.LocationId, capsule.Flag));

        var board = await rig.ShipCallAsync(session, "Board", oid, new PyLong(character.ShipId), new PyLong(capsuleId));

        Assert.IsType<PyTuple>(board);
        Assert.Equal(character.ShipId, (await rig.Characters.GetAsync(character.CharacterId))!.ShipId);
        Assert.Equal(character.ShipId, session.GetInt("shipid"));
        var hull = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, Eve.Inventory.ItemFlags.Hangar), (hull.LocationId, hull.Flag));
    }

    /// <summary>Binds the ship service and undocks.</summary>
    private static async Task<(string Oid, PyValue Answer)> UndockAsync(Rig rig, ClientSession session)
    {
        var oid = await rig.BindShipAsync(session);
        var answer = await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        return (oid, answer);
    }


    private static SimEnter AssertUndockSimulationMessages(Rig rig)
    {
        var enter = Assert.Single(rig.Sim.Heard.OfType<SimEnter>());
        var heat = Assert.Single(rig.Sim.Heard.OfType<SimSetHeatProfile>());
        Assert.Equal(2, rig.Sim.Heard.Count);
        Assert.Equal(enter.SessionId, heat.SessionId);

        var profile = heat.Profile;
        Assert.Equal(3, profile.Capacity.Count);
        Assert.Equal(3, profile.InitialRackHeat.Count);
        Assert.Equal(3, profile.DissipationRate.Count);
        Assert.Equal(3, profile.Attenuation.Count);
        Assert.InRange(profile.ThermodynamicsLevel, 0, 5);
        Assert.True(double.IsFinite(profile.GenerationMultiplier) && profile.GenerationMultiplier > 0);
        Assert.Equal(Math.Max(0.75, 1 - profile.ThermodynamicsLevel * 0.05), profile.DamageMultiplier);
        Assert.All(profile.Capacity, value => Assert.True(double.IsFinite(value) && value > 0));
        Assert.All(profile.InitialRackHeat, value => Assert.True(double.IsFinite(value) && value >= 0));
        Assert.All(profile.DissipationRate, value => Assert.True(double.IsFinite(value) && value > 0));
        Assert.All(profile.Attenuation, value => Assert.True(double.IsFinite(value) && value > 0));
        Assert.Equal(enter.Ship.Modules.Count, profile.Modules.Count);
        Assert.Equal(
            enter.Ship.Modules.Select(module => (module.ItemId, module.Flag)).OrderBy(module => module.ItemId).ToArray(),
            profile.Modules.Select(module => (module.ModuleId, module.Flag)).OrderBy(module => module.ModuleId).ToArray());
        Assert.All(profile.Modules, module =>
        {
            Assert.True(double.IsFinite(module.StructureHp) && module.StructureHp > 0);
            Assert.True(double.IsFinite(module.DamageHp) && module.DamageHp >= 0);
            Assert.True(double.IsFinite(module.HeatDamageHp) && module.HeatDamageHp >= 0);
            Assert.True(double.IsFinite(module.HeatAbsorptionRate) && module.HeatAbsorptionRate >= 0);
        });
        return enter;
    }

    private static async Task<(string Oid, PyValue Answer)> InSpaceAsync(Rig rig, ClientSession session)
    {
        var undocked = await UndockAsync(rig, session);
        Sequence.Of(session);
        return undocked;
    }

    // ── binding ──────────────────────────────────────────────────────────────

    [PgFact]
    public async Task The_ship_service_binds_to_the_station_the_character_is_at_and_to_no_other_place()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;

        var oid = await rig.BindShipAsync(session);
        Assert.StartsWith("N=65450:", oid);
        Assert.Equal(PyNone.Instance, await rig.ShipCallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(60000001), new PyInt(15)), PyNone.Instance));
        Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), await rig.ShipCallAsync(session, "MachoResolveObject"));
    }

    [PgFact]
    public async Task An_undock_that_is_not_addressed_to_an_object_the_session_was_given_does_nothing()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        Assert.Equal(PyNone.Instance, await rig.ShipCallAsync(session, "Undock", null, new PyLong(session.GetInt("shipid")!.Value), PyBool.False));
        Assert.Equal(PyNone.Instance, await rig.ShipCallAsync(session, "Undock", "N=65450:999", new PyLong(1), PyBool.False));
        Assert.Empty(rig.Sim.Heard);
    }

    // ── undocking ────────────────────────────────────────────────────────────

    [PgFact]
    public async Task Undocking_tells_the_client_in_the_reference_order_and_answers_with_the_object_and_the_time()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        // the client holds another object bound to the station too (dogma, an inventory)
        session.RememberBoundObject("N=65450:900");

        var (oid, answer) = await UndockAsync(rig, session);

        Assert.Equal(["OnItemsChanged", "OnMachoObjectDisconnect", "OnMachoObjectDisconnect", "$sessionchange", "OnInvulunOnUndockingUpdated"], Sequence.Of(session));
        var tuple = Assert.IsType<PyTuple>(answer);
        Assert.Equal(oid, JsSemantics.StringValue(tuple.Items[0]));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs + 10_000)), tuple.Items[1]);
        Assert.Empty(session.BoundOids); // every object of the station was let go
    }

    [PgFact]
    public async Task Undocking_moves_the_ship_item_and_the_session_and_tells_the_simulation()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var system = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;

        await UndockAsync(rig, session);

        // the item
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((system, 0), (ship.LocationId, ship.Flag));
        // the session: three attributes, in this order, and nothing else
        Assert.Equal(
            [("stationid", new PyInt(Rig.Station), PyNone.Instance), ("locationid", new PyInt(Rig.Station), new PyInt(system)), ("solarsystemid", PyNone.Instance, new PyInt(system))],
            Sequence.LastChange(session));
        Assert.Null(session.GetInt("stationid"));
        Assert.Null(session.GetInt("stationid2")); // the reference clears the server value on undock (characterState: session.stationid2 = null)
        Assert.Equal(system, session.GetInt("solarsystemid"));
        // the simulation
        var enter = AssertUndockSimulationMessages(rig);
        Assert.Equal(((ulong)session.SessionId, system, EnterHow.Undock, Rig.Station), (enter.SessionId, enter.Scene.LocationId, enter.How, enter.StationId));
        Assert.Equal((character.ShipId, character.CharacterId, "Cockroach"), (enter.Ship.ShipId, enter.Ship.CharacterId, enter.Ship.Name));
        Assert.True(enter.Ship.Mass > 0 && enter.Ship.MaxVelocity > 0 && enter.Ship.Agility > 0 && enter.Ship.Radius > 0 && enter.Ship.ShieldRechargeMs > 0);
        Assert.Equal(3, enter.Ship.Modules.Count);
        Assert.True(enter.Ship.WarpSpeedAu > 0);
        Assert.Equal(3.0, SimShips.WarpSpeedAu(1.0, 3.0));
        Assert.Equal(1.5, SimShips.WarpSpeedAu(0.5, 3.0)); // a base speed that something changed counts
        Assert.Equal(3.0, SimShips.WarpSpeedAu(0.0, 3.0)); // no base speed is the base of one
        Assert.Equal(1.0, SimShips.WarpSpeedAu(1.0, 0.0)); // no multiplier is a multiplier of one
        // what the ship may lock: the lower of the ship's and the pilot's limit when both allow some, else the higher
        Assert.Equal([3, 3, 4, 4, 0], new[] { SimShips.LockLimit(3, 4), SimShips.LockLimit(4, 3), SimShips.LockLimit(0, 4), SimShips.LockLimit(4, 0), SimShips.LockLimit(-1, 0) });
        // the Cockroach snapshot carries its SDE-derived lock range, scan resolution, signature and lock count.
        // (`TargetNotWithinRangeGeneric`, 29700), and a lock of 52 m at 522.5 mm takes 3.55 s (the recorded one was had 3.8 s after it was asked for: the reference's tick and the call)
        Assert.True(enter.Ship.MaxTargetRange > 0 && enter.Ship.ScanResolution > 0 && enter.Ship.SignatureRadius > 0 && enter.Ship.MaxLockedTargets > 0);
        // the Cockroach capacitor values are populated from its SDE attributes.
        Assert.True(enter.Ship.CapacitorCapacity > 0 && enter.Ship.CapacitorRechargeSeconds > 0);
        Assert.True(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task A_ship_leaves_its_station_with_full_shield_and_capacitor_and_the_armor_and_hull_it_had_and_what_it_can_take()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var hurt = new ShipCondition(0.35, 0.18, 0.22, 0.6, false);
        await rig.Modules.SetShipConditionAsync(character.ShipId, hurt);

        await UndockAsync(rig, session);

        var enter = AssertUndockSimulationMessages(rig);
        Assert.Equal(new SimCondition(0.35, 0.6, 1.0, 1.0), enter.Ship.Condition);
        // Cockroach durability from the SDE: 5.5M shield, 1.1M armor, 33,810 structure, and 99.9% resistances.
        var vitals = enter.Ship.Vitals!;
        Assert.Equal((5_500_000.0, 1_100_000.0, 33_810.0), (vitals.ShieldCapacity, vitals.ArmorHp, vitals.StructureHp));
        Assert.All(new[] { vitals.Resonances.Shield, vitals.Resonances.Armor, vitals.Resonances.Structure }, layer => Assert.Equal(4, layer.Count));
        Assert.All(vitals.Resonances.Shield.Concat(vitals.Resonances.Armor).Concat(vitals.Resonances.Structure), r => Assert.InRange(r, 0.0, 1.0));
        // the Cockroach's shield, armor, and structure resistances are nearly complete.
        AssertResonances([0.001, 0.001, 0.001, 0.001], vitals.Resonances.Shield);
        AssertResonances([0.001, 0.001, 0.001, 0.001], vitals.Resonances.Armor);
        AssertResonances([0.001, 0.001, 0.001, 0.001], vitals.Resonances.Structure);
    }

    private static void AssertResonances(double[] expected, IReadOnlyList<double> actual) =>
        Assert.Equal(expected, actual.Select(r => Math.Round(r, 6)));

    [PgFact]
    public async Task A_ship_that_docks_keeps_what_the_simulation_says_it_has_taken()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);

        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station, new SimCondition(0.1, 0.4, 0.75, 0.5)));
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);

        Assert.Equal(new ShipCondition(0.1, 0.5, 0.75, 0.4, false), await rig.Modules.ShipConditionAsync(character.ShipId));
        // a ship that docks whole leaves no trace
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station, SimCondition.Full));
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station && rig.Modules.ShipConditionAsync(character.ShipId).GetAwaiter().GetResult() == ShipCondition.Full);
    }

    [PgFact]
    public async Task A_pilot_that_docks_is_reported_to_the_mission_runtime_with_the_station_it_docked_at()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var docked = new List<(long Character, long Station)>();
        rig.Space.PilotDocked = (characterId, stationId, _) =>
        {
            docked.Add((characterId, stationId));
            return Task.CompletedTask;
        };

        // the simulation accepts the dock command; the ship is docked when it reports the dock, and then the hook is told
        await rig.Space.DockAsync(session, Rig.Station, CancellationToken.None);
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station, SimCondition.Full));
        await Rig.UntilAsync(() => docked.Count > 0);

        Assert.Equal((character.CharacterId, Rig.Station), Assert.Single(docked));
    }

    [PgFact]
    public async Task A_dock_that_the_simulation_starts_on_its_own_reports_the_pilot_too()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var docked = new List<(long Character, long Station)>();
        rig.Space.PilotDocked = (characterId, stationId, _) =>
        {
            docked.Add((characterId, stationId));
            return Task.CompletedTask;
        };

        // no client dock command: the simulation docks the ship after an approach and reports it
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station, SimCondition.Full));
        await Rig.UntilAsync(() => docked.Count > 0);

        Assert.Equal((character.CharacterId, Rig.Station), Assert.Single(docked));
    }

    [PgFact]
    public async Task The_condition_of_a_ship_in_space_is_asked_of_the_simulation_and_a_heal_is_ordered_of_it()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        Assert.Null(await rig.Space.ConditionAsync(session, CancellationToken.None));
        Assert.False(await rig.Space.HealAsync(session, CancellationToken.None));
        Assert.Empty(rig.Sim.Heard);

        await InSpaceAsync(rig, session);
        rig.Sim.Heard.Clear();
        rig.Sim.ConditionAnswer = new SimCondition(0.35, 0.6, 0.9, 0.7);

        Assert.Equal(new SpaceCondition(0.35, 0.7, 0.9, 0.6), await rig.Space.ConditionAsync(session, CancellationToken.None));
        Assert.True(await rig.Space.HealAsync(session, CancellationToken.None));
        rig.Sim.HealAnswer = new CommandReply(false, 0, "NO_SHIP_IN_SPACE");
        Assert.False(await rig.Space.HealAsync(session, CancellationToken.None));
        Assert.Equal([SimCommands.GetCondition, SimCommands.Heal, SimCommands.Heal], rig.Sim.Heard.OfType<SimCommand>().Select(c => c.Code));
        // a heal does not end the undocking protection: the pilot has not flown
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnInvulnCancelled");
    }

    [PgFact]
    public async Task What_the_client_is_told_of_the_ship_and_the_protection_is_what_the_reference_sent()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var system = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;
        var oid = await rig.BindShipAsync(session);
        Events.Of(session);

        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False);
        var events = Events.Of(session);

        var items = events[0];
        Assert.Equal(("OnItemsChanged", "charid"), (items.Name, items.IdType));
        var row = (PyPackedRow)((PyList)items.Args[0]).Items.Single();
        Assert.Equal($"Undocking:{Rig.Station}", JsSemantics.StringValue(row.Values[8]));
        Assert.Equal((character.ShipId, (long)system, 0L), (Id(row.Values[0]), Id(row.Values[3]), Id(row.Values[4])));
        var change = (PyDict)items.Args[1];
        Assert.Equal([(3L, (long)Rig.Station), (4L, (long)ItemFlags.Hangar)], change.Entries.Select(e => (Id(e.Key), Id(e.Value))));
        Assert.Equal(PyNone.Instance, items.Args[2]);

        var disconnect = events[1];
        Assert.Equal(("OnMachoObjectDisconnect", "+clientID"), (disconnect.Name, disconnect.IdType));
        Assert.Equal((oid, session.ClientId), (JsSemantics.StringValue(disconnect.Args[0]), Id(disconnect.Args[1])));
        Assert.Equal(PyNone.Instance, disconnect.Args[2]);

        var invulnerable = events[2];
        Assert.Equal(("OnInvulunOnUndockingUpdated", "shipid"), (invulnerable.Name, invulnerable.IdType));
        Assert.Equal((character.ShipId, Py.FileTimeFromUnixMs(Rig.ClockMs) + 300_000_000, 300_000_000L), (Id(invulnerable.Args[0]), Id(invulnerable.Args[1]), Id(invulnerable.Args[2])));
    }

    [PgFact]
    public async Task Without_the_simulation_an_undock_is_refused_with_a_message_and_nothing_moves()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        rig.Sim.IsConnected = false;
        var oid = await rig.BindShipAsync(session);
        Events.Of(session);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False));
        Assert.True(error.Error.IsUserError);
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        Assert.Empty(Events.Of(session));
        Assert.Equal(Rig.Station, session.GetInt("stationid"));
        Assert.False(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task When_the_simulation_will_not_take_the_ship_it_is_put_back_and_the_client_is_told_nothing()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        rig.Sim.EnterAnswer = "UNKNOWN_STATION 60015249";
        var oid = await rig.BindShipAsync(session);
        Events.Of(session);

        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False));

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        Assert.Empty(Events.Of(session));
        Assert.False(rig.Space.IsInSpace(session));
        Assert.Contains(rig.Log, line => line.Contains("UNKNOWN_STATION"));
    }

    [PgFact]
    public async Task A_ship_in_space_does_not_undock_again()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        var (oid, _) = await InSpaceAsync(rig, session);
        var again = await rig.ShipCallAsync(session, "Undock", oid, new PyLong(1), PyBool.False);
        Assert.Equal(PyNone.Instance, again);
        AssertUndockSimulationMessages(rig);
    }

    [PgFact]
    public async Task Station_undock_carries_the_contraband_confirmation_before_any_ship_moves()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var stationId = session.GetInt("stationid");
        var binding = await rig.BindShipAsync(session);
        var inspections = 0;
        rig.Space.InspectUndockContrabandAsync = (_, _, _, _) =>
        {
            inspections++;
            return Task.FromResult<IReadOnlyList<int>>([3721]);
        };
        var enters = rig.Sim.Heard.OfType<SimEnter>().Count();
        var warning = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.ShipCallAsync(session, "Undock", binding, new PyLong(character.ShipId), PyBool.False));
        Assert.Equal("ShipContrabandWarningUndock", warning.Error.UserErrorMessage);
        Assert.Equal(stationId, session.GetInt("stationid"));
        Assert.Equal(enters, rig.Sim.Heard.OfType<SimEnter>().Count());

        await rig.ShipCallAsync(session, "Undock", binding, new PyLong(character.ShipId), PyBool.True);
        Assert.True(rig.Space.IsInSpace(session));
        Assert.Null(session.GetInt("stationid"));
        Assert.Equal(1, inspections);
    }

    // ── in space ─────────────────────────────────────────────────────────────

    [PgFact]
    public async Task The_ballpark_binds_only_for_a_ship_in_space_and_once_the_answer_has_gone_the_simulation_sends_the_scene()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid2")!.Value;

        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(system), new PyInt(5)), PyNone.Instance));

        await InSpaceAsync(rig, session);
        var answer = (PyTuple)await rig.BeyonceCallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(system), new PyInt(5)), PyNone.Instance);
        Assert.StartsWith("N=65450:", Rig.OidOf(answer.Items[0]));
        Assert.Equal(PyNone.Instance, answer.Items[1]);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimBind);

        session.RunAfterResponse();
        Assert.Equal(new SimBind((ulong)session.SessionId), rig.Sim.Heard.Last());
        // not the ballpark of another system
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(30000142), new PyInt(5)), PyNone.Instance));
    }

    [PgFact]
    public async Task Commands_reach_the_simulation_from_a_bound_object_the_session_was_given_and_from_no_other()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        var before = rig.Sim.Heard.Count;

        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyReal(0.5));
        await rig.BeyonceCallAsync(session, "CmdGotoDirection", oid, new PyReal(1), new PyInt(0), new PyReal(0));
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        await rig.BeyonceCallAsync(session, "CmdFollowBall", oid, new PyInt((int)Rig.Station), new PyInt(2500));
        await rig.BeyonceCallAsync(session, "CmdOrbit", oid, new PyInt((int)Rig.Station), new PyReal(5000.5));
        await rig.BeyonceCallAsync(session, "CmdAlignTo", oid, new PyInt((int)Rig.Station));
        await rig.BeyonceCallWithAsync(session, "CmdAlignTo", oid, [], ("dstID", new PyInt((int)Rig.Station))); // named, as the client may call it
        await rig.BeyonceCallAsync(session, "UpdateStateRequest", oid);

        Assert.Equal(
            new SimMessage[]
            {
                new SimCommand((ulong)session.SessionId, SimCommands.SetSpeedFraction, [0.5]),
                new SimCommand((ulong)session.SessionId, SimCommands.GotoDirection, [1, 0, 0]),
                new SimCommand((ulong)session.SessionId, SimCommands.Stop, []),
                new SimCommand((ulong)session.SessionId, SimCommands.FollowBall, [Rig.Station, 2500]),
                new SimCommand((ulong)session.SessionId, SimCommands.Orbit, [Rig.Station, 5000.5]),
                new SimCommand((ulong)session.SessionId, SimCommands.AlignTo, [Rig.Station]),
                new SimCommand((ulong)session.SessionId, SimCommands.AlignTo, [Rig.Station]),
                new SimUpdateState((ulong)session.SessionId),
            }.Select(Describe),
            rig.Sim.Heard.Skip(before).Select(Describe));

        // calls that were not made on an object of this session, or that carry nonsense, are not passed on
        await rig.BeyonceCallAsync(session, "CmdStop", null);
        await rig.BeyonceCallAsync(session, "CmdStop", "N=65450:999");
        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyStr("fast"));
        await rig.BeyonceCallAsync(session, "CmdGotoDirection", oid, new PyReal(1));
        await rig.BeyonceCallAsync(session, "CmdFollowBall", oid, new PyStr("station"), new PyInt(500));
        await rig.BeyonceCallAsync(session, "CmdOrbit", "N=65450:999", new PyInt((int)Rig.Station), new PyInt(500));
        await rig.BeyonceCallAsync(session, "CmdAlignTo", oid, new PyInt(0));
        Assert.Equal(before + 8, rig.Sim.Heard.Count);
    }

    private static string Describe(SimMessage message) => message switch
    {
        SimCommand c => $"command {c.Code} {string.Join(",", c.Args)}",
        var other => other.ToString()!,
    };

    private static SimNotify Update(ClientSession session) => new(
        (ulong)session.SessionId, "DoDestinyUpdate", "clientID",
        MarshalCodec.Encode(Py.Tuple(new PyList([Py.Tuple(new PyInt(1790679102), Py.Tuple(new PyStr("Stop"), Py.Tuple(new PyLong(9988400001939))))]), PyBool.False)));

    [PgFact]
    public async Task The_first_movement_the_simulation_accepts_while_the_protection_lasts_ends_it_and_the_client_is_told_once_after_the_update()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var id = (ulong)session.SessionId;

        // a command the simulation refuses costs the pilot nothing, and neither does a docking that is accepted
        await rig.BeyonceCallAsync(session, "CmdAlignTo", oid, new PyInt(42));
        rig.Sim.Say(new SimRefused(id, SimCommands.AlignTo, "UNKNOWN_TARGET"));
        rig.Sim.Say(new SimAccepted(id, SimCommands.Dock, 134351527136350000));
        rig.Sim.Say(Update(session));
        Assert.Equal(["DoDestinyUpdate"], Events.Of(session).Select(e => e.Name));

        // a movement it accepts does, whatever it is (stopping included), and the client hears of it after the update the command made
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(Update(session));
        Assert.Equal(["DoDestinyUpdate"], Events.Of(session).Select(e => e.Name));
        rig.Sim.Say(new SimAccepted(id, SimCommands.Stop, 134351527136350000));
        var events = Events.Of(session);
        Assert.Equal(["OnInvulnCancelled"], events.Select(e => e.Name));
        Assert.Equal(("OnInvulnCancelled", "shipid"), (events[0].Name, events[0].IdType));
        Assert.Equal(session.GetInt("shipid"), Id(events[0].Args[0]));

        // once
        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyReal(1));
        rig.Sim.Say(Update(session));
        rig.Sim.Say(new SimAccepted(id, SimCommands.SetSpeedFraction, 134351527136350000));
        Assert.Equal(["DoDestinyUpdate"], Events.Of(session).Select(e => e.Name));
    }

    // ── targets ──────────────────────────────────────────────────────────────

    private static string Describe(TargetingResult? result) =>
        result is null ? "none" : $"{result.Ok} {result.Pending} [{string.Join(",", result.Targets)}] {result.Reason} {result.TargetType} {result.DesiredRange} {result.ActualDistance}";

    [PgFact]
    public async Task An_order_about_targets_is_given_to_the_simulation_and_is_answered_with_what_it_says_only_for_a_ship_in_space()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        Assert.Null(await rig.Space.OrderAsync(session, TargetingOrder.Lock, [42], CancellationToken.None));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimCommand);
        await InSpaceAsync(rig, session);
        rig.Sim.TargetAnswer = new CommandReply(true, 0, null) { Pending = false, Targets = [60015249, 9988400002151] };
        var answer = await rig.Space.OrderAsync(session, TargetingOrder.Remove, [42, 43], CancellationToken.None);
        Assert.Equal("True False [60015249,9988400002151]  0 0 0", Describe(answer));
        Assert.Equal(Describe(new SimCommand((ulong)session.SessionId, SimCommands.RemoveTargets, [42, 43])), Describe(rig.Sim.Heard.Last()));
        foreach (var (order, code) in new[] { (TargetingOrder.Lock, SimCommands.AddTarget), (TargetingOrder.Cancel, SimCommands.CancelAddTarget), (TargetingOrder.Clear, SimCommands.ClearTargets), (TargetingOrder.Targets, SimCommands.GetTargets), (TargetingOrder.Targeters, SimCommands.GetTargeters) })
        {
            await rig.Space.OrderAsync(session, order, [], CancellationToken.None);
            Assert.Equal(code, ((SimCommand)rig.Sim.Heard.Last()).Code);
        }
    }

    [PgFact]
    public async Task A_lock_the_simulation_accepts_ends_the_protection_and_the_client_is_told_twice_as_the_reference_tells_it()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        Events.Of(session);
        var answer = await rig.Space.OrderAsync(session, TargetingOrder.Lock, [9988400002151], CancellationToken.None);
        Assert.Equal("True True []  0 0 0", Describe(answer));
        var told = Events.Of(session);
        Assert.Equal(["OnInvulnCancelled", "OnInvulnCancelled"], told.Select(e => e.Name));
        Assert.All(told, e => Assert.Equal(("shipid", session.GetInt("shipid")), (e.IdType, Id(e.Args[0]))));
        // the protection is over: the next lock is told once, and so is a movement that follows
        await rig.Space.OrderAsync(session, TargetingOrder.Lock, [9988400002152], CancellationToken.None);
        Assert.Equal(["OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task A_lock_that_is_refused_costs_the_pilot_its_protection_nothing_and_says_why()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        Events.Of(session);
        rig.Sim.TargetAnswer = new CommandReply(false, 0, "TARGET_OUT_OF_RANGE") { TargetType = 92885, DesiredRange = 29700, ActualDistance = 306288156879.55023 };
        var answer = await rig.Space.OrderAsync(session, TargetingOrder.Lock, [60015249], CancellationToken.None);
        Assert.Equal("False False [] TARGET_OUT_OF_RANGE 92885 29700 306288156879.55023", Describe(answer));
        Assert.Empty(Events.Of(session));
        // the protection is still there to be ended by a movement
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        Assert.Equal(["OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
    }

    [PgFact]
    public async Task Questions_about_targets_do_not_end_the_protection()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        Events.Of(session);
        rig.Sim.TargetAnswer = new CommandReply(true, 0, null) { Targets = [1] };
        foreach (var order in new[] { TargetingOrder.Targets, TargetingOrder.Targeters, TargetingOrder.Clear, TargetingOrder.Cancel, TargetingOrder.Remove })
        {
            await rig.Space.OrderAsync(session, order, [], CancellationToken.None);
        }
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task Without_the_simulation_a_targeting_order_is_a_message_the_client_shows()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Sim.TargetsUnavailable = true;
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.Space.OrderAsync(session, TargetingOrder.Lock, [1], CancellationToken.None));
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", ((PyStr)arguments[0]).Value);
        Assert.Equal("Space is not available right now. Try again in a moment.", ((PyStr)((PyDict)arguments[1]).Entries.Single().Value).Value);
    }

    [PgFact]
    public async Task The_orders_of_a_pilot_are_asked_one_at_a_time_and_each_is_answered_to_the_one_that_asked()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Sim.TargetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rig.Space.OrderAsync(session, TargetingOrder.Lock, [1], CancellationToken.None);
        var second = rig.Space.OrderAsync(session, TargetingOrder.Lock, [2], CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal([1.0], rig.Sim.Heard.OfType<SimCommand>().Where(c => c.Code == SimCommands.AddTarget).SelectMany(c => c.Args).ToArray());
        rig.Sim.TargetGate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal([1.0, 2.0], rig.Sim.Heard.OfType<SimCommand>().Where(c => c.Code == SimCommands.AddTarget).SelectMany(c => c.Args).ToArray());
    }

    // ── modules ──────────────────────────────────────────────────────────────

    /// <summary>The ship a new character has, as dogma sees it, and the id of one of its modules by type.</summary>
    private static async Task<(Eve.Ships.ShipSnapshot Snapshot, long Afterburner, long Gun, long Miner)> ShipAsync(Rig rig, Eve.Characters.Created character)
    {
        var snapshot = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!;
        long Of(int type) => snapshot.Fitted.Single(m => m.TypeId == type).Id;
        return (snapshot, Of(21857), Of(3638), Of(3651));
    }

    [Fact]
    public void An_afterburner_makes_the_ship_heavier_by_its_mass_and_faster_by_its_speed_factor_over_the_new_mass()
    {
        // a sample hull with a 1MN Civilian Afterburner exercises mass and speed scaling.
        var (on, off) = ModulePackages.Propulsion(1_163_000, 339.25, 52, 500_000, 60, 1_500_000, 0);
        Assert.Equal(new SimNumbers(1_663_000, 522.848918, 52), on);
        Assert.Equal(new SimNumbers(1_163_000, 339.25, 52), off);
        // a microwarpdrive also makes the ship look bigger, by its signature bonus (a percentage)
        var (bigger, _) = ModulePackages.Propulsion(1_163_000, 339.25, 52, 500_000, 60, 1_500_000, 25);
        Assert.Equal(65, bigger.SignatureRadius);
        // a ship with no mass has nothing to be boosted
        Assert.Equal(339.25, ModulePackages.Propulsion(0, 339.25, 52, 0, 60, 1_500_000, 0).On.MaxVelocity);
    }

    [PgFact]
    public async Task What_the_simulation_is_asked_to_run_for_an_afterburner_is_what_the_reference_told_the_client()
    {
        var (rig, character, _) = await DockedAsync();
        await using var _ = rig;
        var (snapshot, afterburner, _, _) = await ShipAsync(rig, character);
        var made = ModulePackages.Of(snapshot, rig.Statics, afterburner, "moduleBonusAfterburner", 0, 1000);
        var package = made.Effect!;
        Assert.Equal((afterburner, 21857, 6731, "effects.Afterburner", 10_000.0, 5.0, 1000, 0L), (package.ModuleId, package.ModuleType, package.EffectId, package.Fx, package.DurationMs, package.CapacitorNeed, package.Repeat, package.TargetId));
        Assert.Equal(new SimNumbers(1_670_000, 1_769.760479, 30), package.On);
        Assert.Equal(new SimNumbers(1_170_000, 1_150, 30), package.Off);
        // the module's speed factor, the capacitor it needs and the length of its cycle, then the ship's mass, top speed and signature, as recorded
        var ship = snapshot.Ship.Id;
        Assert.Equal(
            [(afterburner, 20, 60.0), (afterburner, 6, 5.0), (afterburner, 73, 10_000.0), (ship, 4, 1_670_000.0), (ship, 37, 1_769.760479), (ship, 552, 30.0)],
            package.OnChanges.Select(c => (c.ItemId, c.AttributeId, c.Value)));
        Assert.Equal(
            [(afterburner, 20, 60.0), (afterburner, 6, 5.0), (afterburner, 73, 10_000.0), (ship, 4, 1_170_000.0), (ship, 37, 1_150.0), (ship, 552, 30.0)],
            package.OffChanges.Select(c => (c.ItemId, c.AttributeId, c.Value)));
        // the name of the effect does not have to be in the case the client wrote it
        Assert.NotNull(ModulePackages.Of(snapshot, rig.Statics, afterburner, "MODULEBONUSAFTERBURNER", 0, 1).Effect);
        // and at least one cycle is asked for
        Assert.Equal(1, ModulePackages.Of(snapshot, rig.Statics, afterburner, "moduleBonusAfterburner", 0, 0).Effect!.Repeat);
    }

    [PgFact]
    public async Task A_mining_laser_is_built_as_an_ore_cycle_from_the_module_own_numbers_by_attribute_name()
    {
        var (rig, character, _) = await DockedAsync();
        await using var _ = rig;
        var (snapshot, _, _, miner) = await ShipAsync(rig, character);

        var package = ModulePackages.Of(snapshot, rig.Statics, miner, "miningLaser", 9988400002215, 1);

        Assert.Null(package.Reason);
        // Miner I (SDE): no waste, a 1 % crit chance of twice the yield; its amount is the module's own after the pilot's skills (the world's value)
        var mining = Assert.IsType<SimAction.Mining>(package.Effect!.Action);
        var amountId = rig.Statics.AttributeId("miningAmount")!.Value;
        Assert.Equal((SimMiningFamilies.Ore, snapshot.World.Get(miner, amountId), 0d, 0d, 2d), (mining.Family, mining.MiningAmountM3, mining.WasteVolumeMultiplier, mining.WasteProbability, mining.CritQuantityMultiplier));
        Assert.Equal(0.01, mining.CritProbability, 6);
    }

    [PgFact]
    public async Task A_passive_targeter_asks_the_simulation_for_a_passive_lock_on_its_target_and_needs_a_target_for_it()
    {
        var (rig, character, _) = await DockedAsync();
        await using var _ = rig;
        const int passiveTargeterI = 581;
        await rig.Statics.EnsureTypesAsync([passiveTargeterI]);
        long targeter;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            targeter = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(passiveTargeterI, character.CharacterId, character.ShipId, Eve.Inventory.ItemFlags.LowSlot0, 1, true));
            await rig.Modules.SetAsync(connection, transaction, targeter, Eve.Dogma.ItemState.Online);
            await transaction.CommitAsync();
        }
        var (snapshot, _, _, _) = await ShipAsync(rig, character);

        var refused = ModulePackages.Of(snapshot, rig.Statics, targeter, "targetPassively", 0, 1);
        var package = ModulePackages.Of(snapshot, rig.Statics, targeter, "targetPassively", 9988400002215, 1);

        Assert.Equal("TARGET_REQUIRED", refused.Reason);
        Assert.Null(package.Reason);
        Assert.IsType<SimAction.PassiveLock>(package.Effect!.Action);
        Assert.Equal(9988400002215, package.Effect.TargetId);
    }

    [PgFact]
    public async Task A_frequency_mining_laser_without_a_crystal_has_nothing_to_mine_with()
    {
        var (rig, character, _) = await DockedAsync();
        await using var _ = rig;
        const int modulatedStripMinerII = 17912;
        await rig.Statics.EnsureTypesAsync([modulatedStripMinerII]);
        long laser;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            laser = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(modulatedStripMinerII, character.CharacterId, character.ShipId, Eve.Inventory.ItemFlags.HiSlot0, 1, true));
            await rig.Modules.SetAsync(connection, transaction, laser, Eve.Dogma.ItemState.Online);
            await transaction.CommitAsync();
        }
        var (snapshot, _, _, _) = await ShipAsync(rig, character);

        var refused = ModulePackages.Of(snapshot, rig.Statics, laser, "miningLaser", 9988400002215, 1);

        Assert.Equal("NO_AMMO", refused.Reason);
    }

    [PgFact]
    public async Task An_auto_targeting_system_sweeps_for_the_ships_that_target_its_pilot_within_its_sweep_range_and_needs_no_target()
    {
        var (rig, character, _) = await DockedAsync();
        await using var _ = rig;
        const int autoTargetingSystemI = 1182;
        await rig.Statics.EnsureTypesAsync([autoTargetingSystemI]);
        long system;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            system = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(autoTargetingSystemI, character.CharacterId, character.ShipId, Eve.Inventory.ItemFlags.LowSlot0 + 1, 1, true));
            await rig.Modules.SetAsync(connection, transaction, system, Eve.Dogma.ItemState.Online);
            await transaction.CommitAsync();
        }
        var (snapshot, _, _, _) = await ShipAsync(rig, character);

        var package = ModulePackages.Of(snapshot, rig.Statics, system, "targetHostiles", 0, 1);

        Assert.Null(package.Reason);
        // the sweep range is the module's own targetHostileRange (50 km for the six modules of the group)
        Assert.Equal(50_000d, Assert.IsType<SimAction.AutoTarget>(package.Effect!.Action).RangeMeters);
    }

    [PgFact]
    public async Task What_cannot_be_run_is_refused_with_the_reason_and_the_module_that_the_message_names()
    {
        var (rig, character, _) = await DockedAsync();
        await using var _ = rig;
        var (snapshot, afterburner, gun, miner) = await ShipAsync(rig, character);
        string Why(long module, string effect, long target = 0) => ModulePackages.Of(snapshot, rig.Statics, module, effect, target, 1).Reason!;
        Assert.Equal("MODULE_NOT_FOUND", Why(42, "moduleBonusAfterburner"));
        Assert.Equal("UNSUPPORTED_EFFECT", Why(afterburner, "targetAttack"));
        Assert.Equal("UNSUPPORTED_EFFECT", Why(afterburner, "online")); // an effect that is not the module's to start
        // a weapon and a miner need a target; with one, a mining laser is accepted here (its lock and range are the simulation's to check)
        Assert.Equal("TARGET_REQUIRED", Why(gun, "targetAttack"));
        Assert.Equal("TARGET_REQUIRED", Why(miner, "miningLaser"));
        Assert.Null(Why(miner, "miningLaser", 9988400002215));
        // a gun that takes a charge, with none loaded (nothing loads one yet), has nothing to shoot, and says so before it asks for a target
        snapshot.World.SetValue(gun, 604, 86);
        Assert.Equal("NO_AMMO", Why(gun, "targetAttack"));
        Assert.Equal("NO_AMMO", Why(gun, "targetAttack", 9988400002215));
        snapshot.World.SetValue(gun, 604, 0);
        var named = ModulePackages.Of(snapshot, rig.Statics, gun, "targetAttack", 0, 1);
        Assert.Equal((3638, "Civilian Gatling Railgun"), (named.ModuleType, named.ModuleName));
        // a module that is switched off cannot run anything
        snapshot.World.SetState(afterburner, Eve.Dogma.ItemState.Offline);
        Assert.Equal("MODULE_NOT_ONLINE", Why(afterburner, "moduleBonusAfterburner"));
    }



    [Fact]
    public void A_cloaking_device_packages_a_non_targeted_cloak_action()
    {
        var loaded = ModulePackageFixture(11370, "Prototype Cloaking Device I", "cloakingPrototype",
            new Dictionary<int, double> { [6] = 0, [56] = 1, [73] = 1000 }, groupId: 330);
        var result = ModulePackages.Of(loaded.Snapshot, loaded.Statics, loaded.ModuleId, "cloakingPrototype", 0, 100);
        var package = Assert.IsType<SimEffect>(result.Effect);
        Assert.IsType<SimAction.Cloak>(package.Action);
        Assert.Equal(0, package.TargetId);
    }

    [Fact]
    public void Ancillary_repairers_map_the_fueled_effects_and_apply_the_armor_charge_multiplier()
    {
        var armor = ModulePackageFixture(
            33076,
            "Small Ancillary Armor Repairer",
            "fueledArmorRepair",
            new Dictionary<int, double> { [6] = 5, [56] = 1, [73] = 6000, [84] = 52, [1886] = 3 },
            chargeTypeId: 91001,
            chargeQuantity: 1);
        var armorPackage = ModulePackages.Of(armor.Snapshot, armor.Statics, armor.ModuleId, "fueledArmorRepair", 0, 5).Effect;
        var armorCharge = Assert.IsType<SimAction.Charged>(Assert.IsType<SimEffect>(armorPackage).Action);
        Assert.Equal(new SimAction.Repair(SimLayer.Armor, 156, true), armorCharge.Action);
        Assert.Equal(new SimAction.Repair(SimLayer.Armor, 52, true), armorCharge.Fallback);
        Assert.Equal((91001, 1L), (armorCharge.ChargeTypeId, (long)armorCharge.Charges));

        var shield = ModulePackageFixture(
            32774,
            "Small Ancillary Shield Booster",
            "fueledShieldBoosting",
            new Dictionary<int, double> { [6] = 35, [56] = 1, [73] = 3000, [68] = 200 });
        var shieldPackage = ModulePackages.Of(shield.Snapshot, shield.Statics, shield.ModuleId, "fueledShieldBoosting", 0, 5).Effect;
        var shieldRepair = Assert.IsType<SimAction.Repair>(Assert.IsType<SimEffect>(shieldPackage).Action);
        Assert.Equal(new SimAction.Repair(SimLayer.Shield, 200, false), shieldRepair);
    }

    [Fact]
    public void Power_booster_maps_the_loaded_charge_to_a_self_capacitor_transfer()
    {
        var loaded = ModulePackageFixture(
            3566,
            "Small Capacitor Booster I",
            "powerBooster",
            new Dictionary<int, double> { [6] = 0, [56] = 1, [73] = 15000 },
            chargeTypeId: 91002,
            chargeAttributes: new Dictionary<int, double> { [67] = 25 },
            chargeQuantity: 3);
        var made = ModulePackages.Of(loaded.Snapshot, loaded.Statics, loaded.ModuleId, "powerBooster", 0, 100);
        var package = Assert.IsType<SimEffect>(made.Effect);
        var charged = Assert.IsType<SimAction.Charged>(package.Action);
        var transfer = Assert.IsType<SimAction.Remote>(charged.Action);
        Assert.Null(charged.Fallback);
        Assert.Equal((91002, 3L), (charged.ChargeTypeId, (long)charged.Charges));

        Assert.Equal(loaded.ShipId, package.TargetId);
        Assert.Equal(3, package.Repeat);
        Assert.Equal(new SimRemote.Transfer(25), transfer.What);
        Assert.Equal((0d, 0d, false, "energyTransfer"), (transfer.Optimal, transfer.Falloff, transfer.AtCycleEnd, transfer.Jam));

        var unloaded = ModulePackageFixture(
            3566,
            "Small Capacitor Booster I",
            "powerBooster",
            new Dictionary<int, double> { [6] = 0, [56] = 1, [73] = 15000 });
        var refused = ModulePackages.Of(unloaded.Snapshot, unloaded.Statics, unloaded.ModuleId, "powerBooster", 0, 1);
        Assert.Equal("NO_AMMO", refused.Reason);
        Assert.Null(refused.Effect);
    }

    [Fact]
    public void Ancillary_armor_repairer_without_paste_mends_its_plain_points_as_an_unloaded_repair()
    {
        var unloaded = ModulePackageFixture(
            33076,
            "Small Ancillary Armor Repairer",
            "fueledArmorRepair",
            new Dictionary<int, double> { [6] = 5, [56] = 1, [73] = 6000, [84] = 52, [1886] = 3 });
        var made = ModulePackages.Of(unloaded.Snapshot, unloaded.Statics, unloaded.ModuleId, "fueledArmorRepair", 0, 5);
        var package = Assert.IsType<SimEffect>(made.Effect);
        Assert.Equal(new SimAction.Repair(SimLayer.Armor, 52, true), package.Action);
        Assert.Equal(5, package.Repeat);
        Assert.Null(made.Reason);
    }

    [Fact]
    public void A_micro_jump_drive_package_uses_dogma_range_and_cooldown_and_spools_once()
    {
        var standard = ModulePackageFixture(4383, "Large Micro Jump Drive", "microJumpDrive",
            new Dictionary<int, double> { [6] = 0, [73] = 12_000, [669] = 180_000 },
            effectId: 4921, effectGuid: "effects.MicroJumpDriveEngage");
        var standardResult = ModulePackages.Of(standard.Snapshot, standard.Statics, standard.ModuleId, "microJumpDrive", 0, 20);
        var standardPackage = Assert.IsType<SimEffect>(standardResult.Effect);
        Assert.Equal(4921, standardPackage.EffectId);
        Assert.Equal("effects.MicroJumpDriveEngage", standardPackage.Fx);
        Assert.Equal(12_000, standardPackage.DurationMs);
        Assert.Equal(1, standardPackage.Repeat);
        Assert.Equal(new SimAction.MicroJumpDrive(100_000, 180_000), standardPackage.Action);

        var capital = ModulePackageFixture(88712, "Xarasier Capital Micro Jump Drive", "microJumpDrive",
            new Dictionary<int, double> { [6] = 0, [73] = 12_000, [669] = 150_000, [2066] = 250_000 },
            effectId: 4921, effectGuid: "effects.MicroJumpDriveEngage");
        var capitalResult = ModulePackages.Of(capital.Snapshot, capital.Statics, capital.ModuleId, "microJumpDrive", 0, 100);
        var capitalPackage = Assert.IsType<SimEffect>(capitalResult.Effect);
        Assert.Equal(1, capitalPackage.Repeat);
        Assert.Equal(new SimAction.MicroJumpDrive(250_000, 150_000), capitalPackage.Action);
    }

    [Fact]
    public void A_local_sensor_booster_applies_and_removes_its_ship_bonuses()
    {
        var loaded = ModulePackageFixture(
            90005,
            "Sensor Booster I",
            "sensorBoosterActivePercentage",
            new Dictionary<int, double> { [6] = 12, [73] = 5_000, [309] = 30, [566] = 40 },
            effectId: 70006,
            effectGuid: "effects.ElectronicAttributeModifyActivate");

        var result = ModulePackages.Of(loaded.Snapshot, loaded.Statics, loaded.ModuleId, "sensorBoosterActivePercentage", 0, 20);
        var package = Assert.IsType<SimEffect>(result.Effect);

        Assert.Equal(70006, package.EffectId);
        Assert.Equal(5_000, package.DurationMs);
        Assert.Equal(12, package.CapacitorNeed);
        Assert.Equal(20, package.Repeat);
        Assert.Equal(loaded.ShipId, package.TargetId);

        var remote = Assert.IsType<SimAction.Remote>(package.Action);
        Assert.Equal(new SimRemote.Damp(30, 40), remote.What);
        Assert.Equal((0d, 0d, false, "remoteSensorBoost"), (remote.Optimal, remote.Falloff, remote.AtCycleEnd, remote.Jam));
        Assert.Equal(package.OnChanges, package.OffChanges);
        Assert.All(package.OnChanges, change => Assert.Equal(loaded.ModuleId, change.ItemId));
    }

    private static (Eve.Ships.ShipSnapshot Snapshot, Eve.Dogma.DogmaStatic Statics, long ModuleId, long ShipId) ModulePackageFixture(
        int moduleTypeId,
        string moduleName,
        string effectName,
        IReadOnlyDictionary<int, double> moduleAttributes,
        int? chargeTypeId = null,
        IReadOnlyDictionary<int, double>? chargeAttributes = null,
        long chargeQuantity = 0,
        int groupId = 1,
        int effectId = 70005,
        string? effectGuid = null)
    {
        const long characterId = 70001;
        const long shipId = 70002;
        const long moduleId = 70003;
        const long chargeId = 70004;
        const int flag = 11;

        var effect = new Eve.Dogma.EffectDef(effectId, effectName, Eve.Dogma.EffectDef.Activation, [])
        {
            Guid = effectGuid ?? $"effects.{char.ToUpperInvariant(effectName[0])}{effectName[1..]}",
            DurationAttributeId = 73,
            DischargeAttributeId = 6,
        };
        var moduleType = new Eve.Dogma.TypeDef(moduleTypeId, groupId, 7, moduleAttributes, [effectId], moduleName);
        var types = new List<Eve.Dogma.TypeDef> { moduleType };
        var charges = new List<Eve.Inventory.Item>();
        if (chargeTypeId is { } typeId && chargeQuantity > 0)
        {
            types.Add(new Eve.Dogma.TypeDef(typeId, 2, 8, chargeAttributes ?? new Dictionary<int, double>(), [], "Test charge"));
            charges.Add(new Eve.Inventory.Item(chargeId, typeId, characterId, shipId, flag, chargeQuantity, false, null));
        }

        var statics = new Eve.Dogma.DogmaStatic([], [effect], types);
        var character = new Eve.Characters.Character(
            characterId, 1, "Module test", 1, 1, 1, 1, 1, 1, 1000001,
            null, null, 60003760, 60003760, 30000142, 20000020, 10000001, shipId,
            DateTimeOffset.UnixEpoch, null, null);
        var ship = new Eve.Inventory.Item(shipId, 587, characterId, 60003760, 0, 1, true, null);
        var module = new Eve.Inventory.Item(moduleId, moduleTypeId, characterId, shipId, flag, 1, true, null);
        var world = new Eve.Dogma.DogmaWorld(statics, characterId, shipId);
        world.Add(new Eve.Dogma.DogmaItem(characterId, character.TypeId, characterId, shipId, Eve.Ships.ShipSnapshot.PilotFlag, Eve.Dogma.ItemState.Online));
        world.Add(new Eve.Dogma.DogmaItem(shipId, ship.TypeId, characterId, ship.LocationId, ship.Flag, Eve.Dogma.ItemState.Online));
        world.Add(new Eve.Dogma.DogmaItem(moduleId, moduleTypeId, characterId, shipId, flag, Eve.Dogma.ItemState.Online));
        if (charges.Count > 0)
        {
            world.Add(new Eve.Dogma.DogmaItem(chargeId, chargeTypeId!.Value, characterId, shipId, flag, Eve.Dogma.ItemState.Online));
        }

        return (
            new Eve.Ships.ShipSnapshot { Character = character, Ship = ship, Fitted = [module], Charges = charges, Skills = [], World = world },
            statics,
            moduleId,
            shipId);
    }

    [PgFact]
    public async Task A_gun_that_needs_no_charge_shoots_with_its_own_damage_after_the_skills_of_the_pilot()
    {
        var (rig, character, _) = await DockedAsync();
        await using var _ = rig;
        var (snapshot, _, gun, _) = await ShipAsync(rig, character);
        var made = ModulePackages.Of(snapshot, rig.Statics, gun, "targetAttack", 980050000105, 1000);
        var package = Assert.IsType<SimEffect>(made.Effect);
        var world = snapshot.World;
        // the cycle, the cost and the range are the module's after dogma, kept to six places
        double Six(int attribute) => Math.Round(world.Get(gun, attribute), 6, MidpointRounding.AwayFromZero);
        Assert.Equal((gun, 3638, 10, "effects.Laser", 980050000105, 1000), (package.ModuleId, package.ModuleType, package.EffectId, package.Fx, package.TargetId, package.Repeat));
        Assert.Equal((Six(51), Six(6)), (package.DurationMs, package.CapacitorNeed));
        var turret = Assert.IsType<SimTurret>(package.Turret);
        Assert.Equal((3638, "effects.Laser", Six(54), Six(158), Six(160)), (turret.WeaponTypeId, turret.Fx, turret.OptimalRange, turret.Falloff, turret.TrackingSpeed));
        // a shot is the damage of the gun times its multiplier: the civilian railgun hurts with heat and motion only
        var multiplier = world.Get(gun, 64);
        Assert.Equal(
            [0.0, Math.Round(world.Get(gun, 118) * multiplier, 6), Math.Round(world.Get(gun, 117) * multiplier, 6), 0.0],
            turret.RawDamage);
        Assert.True(turret.RawDamage[1] > 0 && turret.RawDamage[2] > 0);
        // the client is told the cost, the cycle and the range as it starts and as it stops
        var told = new[] { (gun, 6, package.CapacitorNeed), (gun, 51, Math.Round(package.DurationMs, 3)), (gun, 54, turret.OptimalRange) };
        Assert.Equal(told, package.OnChanges.Select(c => (c.ItemId, c.AttributeId, c.Value)));
        Assert.Equal(told, package.OffChanges.Select(c => (c.ItemId, c.AttributeId, c.Value)));
        Assert.Null(package.On);
        Assert.Null(package.Off);
    }

    [Theory]
    // (class, safety level, a capsule is the target) -> whether the safety stops the weapon: high security needs none; low security needs partial, and none for a capsule; null security no crime
    [InlineData(Eve.Universe.SecurityClass.HighSec, 2, false, true)]
    [InlineData(Eve.Universe.SecurityClass.HighSec, 1, false, true)]
    [InlineData(Eve.Universe.SecurityClass.HighSec, 0, false, false)]
    [InlineData(Eve.Universe.SecurityClass.SafeSec, 1, false, true)]
    [InlineData(Eve.Universe.SecurityClass.SafeSec, 0, false, false)]
    [InlineData(Eve.Universe.SecurityClass.LowSec, 2, false, true)]
    [InlineData(Eve.Universe.SecurityClass.LowSec, 1, false, false)]
    [InlineData(Eve.Universe.SecurityClass.LowSec, 1, true, true)]
    [InlineData(Eve.Universe.SecurityClass.LowSec, 0, true, false)]
    [InlineData(Eve.Universe.SecurityClass.ZeroSec, 2, false, false)]
    [InlineData(Eve.Universe.SecurityClass.ZeroSec, 2, true, false)]
    public void The_safety_a_pilot_has_set_decides_what_it_may_attack(Eve.Universe.SecurityClass where, int level, bool capsule, bool stops) =>
        Assert.Equal(stops, WeaponSafety.StopsFire(where, level, capsule));

    [Theory]
    [InlineData(Eve.Universe.SecurityClass.SafeSec, true)]
    [InlineData(Eve.Universe.SecurityClass.HighSec, true)]
    [InlineData(Eve.Universe.SecurityClass.LowSec, true)]
    [InlineData(Eve.Universe.SecurityClass.ZeroSec, false)]
    public void The_safety_stops_a_weapon_aimed_at_another_pilot_only_in_empire_space(Eve.Universe.SecurityClass where, bool stops) =>
        Assert.Equal(stops, WeaponSafety.StopsFire(where));

    [PgFact]
    public async Task An_effect_the_simulation_accepts_ends_the_protection_and_the_client_is_told_once_and_only_for_a_ship_in_space()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var (_, afterburner, _, _) = await ShipAsync(rig, character);
        Assert.Null(await rig.Space.ActivateAsync(session, afterburner, "moduleBonusAfterburner", 0, 1000, CancellationToken.None));
        await InSpaceAsync(rig, session);
        Events.Of(session);
        var answer = await rig.Space.ActivateAsync(session, afterburner, "moduleBonusAfterburner", 0, 1000, CancellationToken.None);
        Assert.Equal(ModuleResult.Done, answer);
        var told = Events.Of(session);
        Assert.Equal(["OnInvulnCancelled"], told.Select(e => e.Name));
        var heard = Assert.IsType<SimActivate>(rig.Sim.Heard.Last());
        Assert.Equal(((ulong)session.SessionId, afterburner, "effects.Afterburner"), (heard.SessionId, heard.Effect.ModuleId, heard.Effect.Fx));
        // the protection has ended: a second effect tells nothing more
        rig.Sim.ActivateAnswer = new CommandReply(true, 0, null);
        await rig.Space.ActivateAsync(session, afterburner, "moduleBonusAfterburner", 0, 1000, CancellationToken.None);
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task An_effect_the_simulation_refuses_costs_the_protection_nothing_and_says_why()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var (_, afterburner, gun, _) = await ShipAsync(rig, character);
        await InSpaceAsync(rig, session);
        Events.Of(session);
        rig.Sim.ActivateAnswer = new CommandReply(false, 0, "ALREADY_ACTIVE");
        var running = await rig.Space.ActivateAsync(session, afterburner, "moduleBonusAfterburner", 0, 1000, CancellationToken.None);
        Assert.Equal(new ModuleResult(false, "MODULE_ALREADY_ACTIVE", 21857, "1MN Civilian Afterburner"), running);
        rig.Sim.ActivateAnswer = new CommandReply(false, 0, "NOT_ENOUGH_CAPACITOR");
        Assert.Equal("NOT_ENOUGH_CAPACITOR", (await rig.Space.ActivateAsync(session, afterburner, "moduleBonusAfterburner", 0, 1, CancellationToken.None))!.Reason);
        // a package that cannot be made is never asked of the simulation
        var before = rig.Sim.Heard.Count;
        Assert.Equal("TARGET_REQUIRED", (await rig.Space.ActivateAsync(session, gun, "targetAttack", 0, 1, CancellationToken.None))!.Reason);
        Assert.Equal(before, rig.Sim.Heard.Count);
        Assert.Empty(Events.Of(session));
        // the protection is still there to be ended by a movement
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        Assert.Equal(["OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
    }

    [PgFact]
    public async Task Asking_for_an_effect_to_stop_is_given_to_the_simulation_and_a_refusal_names_the_module()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var (_, afterburner, _, _) = await ShipAsync(rig, character);
        Assert.Null(await rig.Space.DeactivateAsync(session, afterburner, "moduleBonusAfterburner", CancellationToken.None));
        await InSpaceAsync(rig, session);
        Assert.Equal(ModuleResult.Done, await rig.Space.DeactivateAsync(session, afterburner, "moduleBonusAfterburner", CancellationToken.None));
        Assert.Equal(new SimCommand((ulong)session.SessionId, SimCommands.Deactivate, [afterburner]).Code, ((SimCommand)rig.Sim.Heard.Last()).Code);
        Assert.Equal([(double)afterburner], ((SimCommand)rig.Sim.Heard.Last()).Args);
        rig.Sim.DockAnswer = new CommandReply(false, 0, "NOT_ACTIVE");
        Assert.Equal(new ModuleResult(false, "MODULE_NOT_ACTIVE", 21857, "1MN Civilian Afterburner"), await rig.Space.DeactivateAsync(session, afterburner, "moduleBonusAfterburner", CancellationToken.None));
    }

    [PgFact]
    public async Task A_dock_that_a_running_module_prevents_is_said_in_the_reference_words()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await ShipAsync(rig, character);
        await InSpaceAsync(rig, session);
        rig.Sim.DockAnswer = new CommandReply(false, 0, "DOCKING_DISALLOWED_BY_MODULE");
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.DockAsync(session, Rig.Station, CancellationToken.None));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
        Assert.Equal("You cannot dock while a module that prevents docking is active.", JsSemantics.StringValue(values.Entries.Single().Value));
    }

    [PgFact]
    public async Task The_module_orders_of_a_pilot_are_asked_one_at_a_time()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var (_, afterburner, _, _) = await ShipAsync(rig, character);
        await InSpaceAsync(rig, session);
        rig.Sim.ActivateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rig.Space.ActivateAsync(session, afterburner, "moduleBonusAfterburner", 0, 1000, CancellationToken.None);
        var second = rig.Space.ActivateAsync(session, afterburner, "moduleBonusAfterburner", 0, 1, CancellationToken.None);
        // the first order reaches the simulation after awaits of its own, which a loaded machine can stretch past any fixed delay: wait for it
        for (var attempt = 0; attempt < 400 && !rig.Sim.Heard.OfType<SimActivate>().Any(); attempt++)
        {
            await Task.Delay(25);
        }
        // a second send would show within this window (the negative check has no completion signal to wait for)
        await Task.Delay(100);
        Assert.Single(rig.Sim.Heard.OfType<SimActivate>());
        rig.Sim.ActivateGate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, rig.Sim.Heard.OfType<SimActivate>().Count());
    }

    [PgFact]
    public async Task Without_the_simulation_a_module_order_is_a_message_the_client_shows()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var (_, afterburner, _, _) = await ShipAsync(rig, character);
        await InSpaceAsync(rig, session);
        rig.Sim.IsConnected = false;
        rig.Sim.ActivateUnavailable = true;
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.Space.ActivateAsync(session, afterburner, "moduleBonusAfterburner", 0, 1000, CancellationToken.None));
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", ((PyStr)arguments[0]).Value);
    }

    // ── warp ─────────────────────────────────────────────────────────────────

    [PgFact]
    public async Task Autopilot_warp_uses_the_reference_arrival_range_and_keeps_the_warp_refusal_contract()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);

        var answer = await rig.BeyonceCallAsync(session, "CmdWarpToStuffAutopilot", oid, new PyLong(40491233));

        Assert.Equal(PyNone.Instance, answer);
        Assert.Equal(
            Describe(new SimCommand((ulong)session.SessionId, SimCommands.WarpTo, [40491233, 10_000])),
            Describe(rig.Sim.Heard.Last()));

        rig.Sim.WarpAnswer = new CommandReply(false, 0, "WARP_SCRAMBLED");
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.BeyonceCallAsync(session, "CmdWarpToStuffAutopilot", oid, new PyLong(40491233)));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);

        var otherCharacter = await rig.NewCharacterAsync("Other Pilot", account: 2);
        var otherSession = await rig.SessionAsync(otherCharacter, id: 2);
        var heard = rig.Sim.Heard.Count;
        Assert.Equal(
            PyNone.Instance,
            await rig.BeyonceCallAsync(otherSession, "CmdWarpToStuffAutopilot", oid, new PyLong(40491233)));
        Assert.Equal(heard, rig.Sim.Heard.Count);
        Assert.Equal(
            PyNone.Instance,
            await rig.BeyonceCallAsync(session, "CmdWarpToStuffAutopilot", null, new PyLong(40491233)));
        Assert.Equal(heard, rig.Sim.Heard.Count);

        Assert.Single(Events.Of(session), e => e.Name == "OnInvulnCancelled");
    }

    [PgFact]
    public async Task A_warp_is_given_to_the_simulation_with_the_range_the_client_asked_for_and_ends_the_protection()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var before = rig.Sim.Heard.Count;

        var answer = await rig.BeyonceCallWithAsync(session, "CmdWarpToStuff", oid, [new PyStr("item"), new PyInt(40491233)], ("minRange", new PyInt(30000)));
        Assert.Equal(PyNone.Instance, answer);
        Assert.Equal(
            [Describe(new SimCommand((ulong)session.SessionId, SimCommands.WarpTo, [40491233, 30000]))],
            rig.Sim.Heard.Skip(before).Select(Describe));
        // no range asked for is none
        await rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyInt(40491233));
        Assert.Equal(Describe(new SimCommand((ulong)session.SessionId, SimCommands.WarpTo, [40491233, 0])), Describe(rig.Sim.Heard.Last()));

        // the ship has left its protection: the client is told once, whatever the simulation said before
        var told = Events.Of(session).Where(e => e.Name == "OnInvulnCancelled").ToArray();
        Assert.Single(told);
        Assert.Equal(("OnInvulnCancelled", "shipid"), (told[0].Name, told[0].IdType));
    }

    [PgFact]
    public async Task A_warp_that_cannot_be_done_is_a_message_the_client_shows_and_one_of_another_kind_of_place_is_not_passed_on()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var before = rig.Sim.Heard.Count;

        foreach (var reason in new[] { "TARGET_NOT_FOUND", "WARP_DISTANCE_TOO_CLOSE" })
        {
            rig.Sim.WarpAnswer = new CommandReply(false, 0, reason);
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyInt(99999999)));
            Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
            var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
            Assert.Equal("You cannot warp there right now.", JsSemantics.StringValue(values.Entries.Single().Value));
        }
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnInvulnCancelled"); // a warp that was refused costs the pilot nothing

        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("unsupported"), new PyInt(5)));
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyStr("planet")));
        Assert.Equal(before + 2, rig.Sim.Heard.Count);

        // not from an object of this session
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "CmdWarpToStuff", null, new PyStr("item"), new PyInt(40491233)));
        Assert.Equal(before + 2, rig.Sim.Heard.Count);
    }

[PgFact]
    public async Task Fleet_warp_scopes_to_commander_role_skips_opt_outs_and_keeps_solo_warp_single()
    {
        await using var rig = await Rig.CreateAsync();
        var leader = await rig.NewCharacterAsync("Warp Fleet Leader", 101);
        var wingCommander = await rig.NewCharacterAsync("Warp Fleet Wing", 102);
        var squadCommander = await rig.NewCharacterAsync("Warp Fleet Squad", 103);
        var squadMember = await rig.NewCharacterAsync("Warp Fleet Member", 104);
        var optOutMember = await rig.NewCharacterAsync("Warp Opt Out", 105);
        var otherWingMember = await rig.NewCharacterAsync("Warp OtherWing", 106);
        var dockedMember = await rig.NewCharacterAsync("Warp Fleet Docked", 107);

        var leaderSession = await rig.SessionAsync(leader, 101);
        var wingSession = await rig.SessionAsync(wingCommander, 102);
        var squadSession = await rig.SessionAsync(squadCommander, 103);
        var memberSession = await rig.SessionAsync(squadMember, 104);
        var optOutSession = await rig.SessionAsync(optOutMember, 105);
        var otherWingSession = await rig.SessionAsync(otherWingMember, 106);
        var dockedSession = await rig.SessionAsync(dockedMember, 107);
        await InSpaceAsync(rig, leaderSession);
        await InSpaceAsync(rig, wingSession);
        await InSpaceAsync(rig, squadSession);
        await InSpaceAsync(rig, memberSession);
        await InSpaceAsync(rig, optOutSession);
        await InSpaceAsync(rig, otherWingSession);

        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        try
        {
            var wingOne = await store.CreateWingAsync(fleetId, leader.CharacterId);
            var wingTwo = await store.CreateWingAsync(fleetId, leader.CharacterId);
            var squadOne = await store.CreateSquadAsync(fleetId, leader.CharacterId, wingOne);
            var squadTwo = await store.CreateSquadAsync(fleetId, leader.CharacterId, wingOne);
            var squadThree = await store.CreateSquadAsync(fleetId, leader.CharacterId, wingTwo);

            async Task Join(Eve.Characters.Created character, long wingId, long squadId, int role)
            {
                Assert.True(await store.InviteAsync(fleetId, leader.CharacterId, character.CharacterId,
                    wingId, squadId, role));
                Assert.NotNull(await store.JoinAsync(fleetId, character.CharacterId));
            }

            await Join(wingCommander, wingOne, Eve.Services.Space.FleetStore.NoUnit,
                Eve.Services.Space.FleetStore.WingCommander);
            await Join(squadCommander, wingOne, squadOne, Eve.Services.Space.FleetStore.SquadCommander);
            await Join(squadMember, wingOne, squadOne, Eve.Services.Space.FleetStore.Member);
            await Join(optOutMember, wingOne, squadTwo, Eve.Services.Space.FleetStore.Member);
            await Join(otherWingMember, wingTwo, squadThree, Eve.Services.Space.FleetStore.Member);
            await Join(dockedMember, wingOne, squadOne, Eve.Services.Space.FleetStore.Member);
            Assert.NotNull(await store.SetMemberOptionsAsync(fleetId, optOutMember.CharacterId, acceptsFleetWarp: false));
            Assert.False((await store.SnapshotAsync(fleetId))!.Members
                .Single(member => member.CharacterId == optOutMember.CharacterId).AcceptsFleetWarp);

            var bounds = new Dictionary<long, string>
            {
                [leader.CharacterId] = await rig.BindBeyonceAsync(leaderSession),
                [wingCommander.CharacterId] = await rig.BindBeyonceAsync(wingSession),
                [squadCommander.CharacterId] = await rig.BindBeyonceAsync(squadSession),
                [squadMember.CharacterId] = await rig.BindBeyonceAsync(memberSession),
                [optOutMember.CharacterId] = await rig.BindBeyonceAsync(optOutSession),
                [otherWingMember.CharacterId] = await rig.BindBeyonceAsync(otherWingSession)
            };

            async Task<PyValue> Warp(ClientSession caller, PyValue target, bool? fleet, string targetType = "item") =>
                fleet is { } enabled
                    ? await rig.BeyonceCallWithAsync(caller, "CmdWarpToStuff", bounds[caller.CharacterId!.Value],
                        [new PyStr(targetType), target], ("fleet", enabled ? PyBool.True : PyBool.False))
                    : await rig.BeyonceCallAsync(caller, "CmdWarpToStuff", bounds[caller.CharacterId!.Value],
                        new PyStr(targetType), target);

            SimCommand[] WarpCommandsSince(int index) =>
                rig.Sim.Heard.Skip(index).OfType<SimCommand>()
                    .Where(command => command.Code == SimCommands.WarpTo).ToArray();
            void AssertWarpSessions(int index, params ClientSession[] expected)
            {
                Assert.Equal(expected.Select(session => (ulong)session.SessionId).OrderBy(id => id),
                    WarpCommandsSince(index).Select(command => command.SessionId).OrderBy(id => id));
            }

            var beforeSolo = rig.Sim.Heard.Count;
            Assert.Equal(PyNone.Instance, await Warp(leaderSession, new PyLong(90_001), null));
            AssertWarpSessions(beforeSolo, leaderSession);

            var beforeFleet = rig.Sim.Heard.Count;
            Assert.Equal(PyNone.Instance, await Warp(leaderSession, new PyLong(90_002), true));
            AssertWarpSessions(beforeFleet, leaderSession, wingSession, squadSession, memberSession, otherWingSession);
            var fleetWarpMessage = Assert.Single(Events.Of(memberSession), item => item.Name == "OnRemoteMessage");
            Assert.Equal("FleetWarp", JsSemantics.StringValue(fleetWarpMessage.Args[0]));
            Assert.Contains(Assert.IsType<PyDict>(fleetWarpMessage.Args[1]).Entries,
                entry => JsSemantics.StringValue(entry.Key) == "celestialID" && entry.Value == new PyLong(90_002));

            var beforeWing = rig.Sim.Heard.Count;
            Assert.Equal(PyNone.Instance, await Warp(wingSession, new PyLong(90_003), true));
            AssertWarpSessions(beforeWing, wingSession, squadSession, memberSession);

            var beforeSquad = rig.Sim.Heard.Count;
            Assert.Equal(PyNone.Instance, await Warp(squadSession, new PyLong(90_004), true));
            AssertWarpSessions(beforeSquad, squadSession, memberSession);

            var otherWingFrom = (int)otherWingSession.GetInt("solarsystemid2")!.Value;
            rig.Sim.Say(new SimJumped((ulong)otherWingSession.SessionId, otherWingFrom, Gate, NextSystem, NextGate));
            await Rig.UntilAsync(() => otherWingSession.GetInt("solarsystemid") == NextSystem);
            var beforeOtherSystem = rig.Sim.Heard.Count;
            Assert.Equal(PyNone.Instance, await Warp(leaderSession, new PyLong(90_005), true));
            AssertWarpSessions(beforeOtherSystem, leaderSession, wingSession, squadSession, memberSession);

            var beforeMemberRefusal = rig.Sim.Heard.Count;
            await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
                () => Warp(memberSession, new PyLong(90_005), true));
            Assert.Empty(WarpCommandsSince(beforeMemberRefusal));

            var beforeDockedTarget = rig.Sim.Heard.Count;
            await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
                Warp(leaderSession, new PyLong(dockedMember.CharacterId), true, "char"));
            Assert.Empty(WarpCommandsSince(beforeDockedTarget));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

    [PgFact]
    public async Task Fleet_warp_to_character_resolves_ship_and_preserves_issuer_refusal()
    {
        await using var rig = await Rig.CreateAsync();
        var leader = await rig.NewCharacterAsync("Warp Character Leader", 201);
        var member = await rig.NewCharacterAsync("Warp Character Member", 202);
        var leaderSession = await rig.SessionAsync(leader, 201);
        var memberSession = await rig.SessionAsync(member, 202);
        await InSpaceAsync(rig, leaderSession);
        await InSpaceAsync(rig, memberSession);

        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        try
        {
            Assert.True(await store.InviteAsync(fleetId, leader.CharacterId, member.CharacterId,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit,
                Eve.Services.Space.FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, member.CharacterId));
            var leaderBound = await rig.BindBeyonceAsync(leaderSession);

            async Task<PyValue> WarpCharacter(bool fleet) =>
                await rig.BeyonceCallWithAsync(leaderSession, "CmdWarpToStuff", leaderBound,
                    [new PyStr("char"), new PyLong(member.CharacterId)],
                    ("fleet", fleet ? PyBool.True : PyBool.False));
            SimCommand[] WarpCommandsSince(int index) =>
                rig.Sim.Heard.Skip(index).OfType<SimCommand>()
                    .Where(command => command.Code == SimCommands.WarpTo).ToArray();

            var soloStart = rig.Sim.Heard.Count;
            Assert.Equal(PyNone.Instance, await WarpCharacter(false));
            var solo = Assert.Single(WarpCommandsSince(soloStart));
            Assert.Equal((ulong)leaderSession.SessionId, solo.SessionId);
            Assert.Equal((double)member.ShipId, solo.Args[0]);

            var fleetStart = rig.Sim.Heard.Count;
            Assert.Equal(PyNone.Instance, await WarpCharacter(true));
            var fleetCommands = WarpCommandsSince(fleetStart);
            Assert.Equal(2, fleetCommands.Length);
            Assert.All(fleetCommands, command => Assert.Equal((double)member.ShipId, command.Args[0]));

            rig.Sim.WarpAnswer = new CommandReply(false, 0, "WARP_SCRAMBLED");
            var refusalStart = rig.Sim.Heard.Count;
            var refusal = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() => WarpCharacter(true));
            Assert.Equal("CustomNotify", refusal.Error.UserErrorMessage);
            var notify = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)refusal.Error.Payload).Header).Items[1]).Items[1];
            Assert.Equal("You cannot warp because you are warp scrambled.",
                JsSemantics.StringValue(notify.Entries.Single().Value));
            Assert.Equal(2, WarpCommandsSince(refusalStart).Length);

            var memberFrom = (int)memberSession.GetInt("solarsystemid2")!.Value;
            rig.Sim.Say(new SimJumped((ulong)memberSession.SessionId, memberFrom, Gate, NextSystem, NextGate));
            await Rig.UntilAsync(() => memberSession.GetInt("solarsystemid") == NextSystem);
            var beforeCrossSystemTarget = rig.Sim.Heard.Count;
            await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() => WarpCharacter(false));
            Assert.Empty(WarpCommandsSince(beforeCrossSystemTarget));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

    [PgFact]
    public async Task A_bookmark_warp_uses_the_bookmark_id_and_requested_range()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        var before = rig.Sim.Heard.Count;

        const long bookmarkId = 50_123_456;
        const long range = 20_000;
        const double x = 1_250_000;
        const double y = -4_500_000;
        const double z = 7_750_000;
        var systemId = (int)session.GetInt("solarsystemid")!.Value;
        rig.Beyonce.BookmarkTargetForCharacter = (characterId, requestedId, _) =>
            Task.FromResult<WarpCoordinates?>(requestedId == bookmarkId
                ? new WarpCoordinates(systemId, x, y, z)
                : null);

        var answer = await rig.BeyonceCallWithAsync(
            session,
            "CmdWarpToStuff",
            oid,
            [new PyStr("bookmark"), new PyLong(bookmarkId)],
            ("minRange", new PyLong(range)));

        Assert.Equal(PyNone.Instance, answer);
        Assert.Equal(
            [Describe(new SimCommand((ulong)session.SessionId, SimCommands.WarpToPoint, [x, y, z, range]))],
            rig.Sim.Heard.Skip(before).Select(Describe));
    }


    [PgFact]
    public async Task A_coordinate_warp_uses_the_supplied_position_and_range()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        var before = rig.Sim.Heard.Count;

        const double x = 21_500_000;
        const double y = -3_250_000;
        const double z = 800_000;
        const double range = 5_000;
        var target = new PyDict([
            new(new PyStr("x"), new PyReal(x)),
            new(new PyStr("y"), new PyReal(y)),
            new(new PyStr("z"), new PyReal(z)),
        ]);

        var answer = await rig.BeyonceCallWithAsync(
            session,
            "CmdWarpToStuff",
            oid,
            [new PyStr("position"), target],
            ("minRange", new PyReal(range)));

        Assert.Equal(PyNone.Instance, answer);
        Assert.Equal(
            [Describe(new SimCommand((ulong)session.SessionId, SimCommands.WarpToPoint, [x, y, z, range]))],
            rig.Sim.Heard.Skip(before).Select(Describe));
    }


    [PgFact]
    public async Task Active_criminal_timer_blocks_warp_and_gate_jump_in_high_security_space()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var systemId = (int)session.GetInt("solarsystemid")!.Value;
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(new Eve.Crimewatch.CombatState(
            0, 0, 0, Rig.ClockMs + 60_000, Eve.Crimewatch.Flag.Criminal, 0, systemId));
        var oid = await rig.BindBeyonceAsync(session);
        var before = rig.Sim.Heard.Count;

        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyLong(40491233)));
        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.Space.JumpAsync(session, Gate, NextGate, character.ShipId, oid, CancellationToken.None));

        Assert.Equal(before, rig.Sim.Heard.Count);
    }


    [PgFact]
    public async Task Concord_response_spawns_the_authored_fleet_and_enforces_ship_loss_after_timer()
    {
        var waits = new List<(TimeSpan After, TaskCompletionSource Done)>();
        using var timers = new Eve.Scheduler.LocalTimers(delay: (after, token) =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waits) waits.Add((after, done));
            return done.Task.WaitAsync(token);
        });
        var responder = new Eve.Npc.NpcProfile(
            "test-concord-response", "Test CONCORD response", [], "npc", Rig.Cockroach,
            1000125, 0, 500003, Eve.Npc.NpcAuthority.RetailEmptyLoadoutId, "", "CONCORD response", 0, 0, 0);
        var authority = new Eve.Npc.NpcAuthority(
            [responder], [],
            [new Eve.Npc.NpcPool(
                "concord_response_fleet", "CONCORD response fleet", [], "npc",
                [new Eve.Npc.NpcPoolEntry(responder.Id, 1)])]);
        var rig = await Rig.CreateAsync(localTimers: timers, npcAuthority: authority);
        await using var _ = rig;
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        await InSpaceAsync(rig, session);
        var systemId = session.GetInt("solarsystemid")!.Value;
        rig.Sim.PositionAnswers[character.ShipId] = [0, 0, 0];

        Assert.True(
            await rig.Space.RespondToCriminalAsync(character.CharacterId, systemId, character.ShipId),
            string.Join(Environment.NewLine, rig.Log));

        var responders = rig.Sim.Heard.OfType<SimSpawn>().ToArray();
        Assert.Equal(3, responders.Length);
        Assert.All(responders, spawn =>
        {
            Assert.Equal(0, spawn.Identity.HostileResponseThreshold);
            Assert.True(spawn.Identity.AggressionRange >= 50_000);
            Assert.Equal("belt rat", spawn.Behavior);
        });

        (TimeSpan After, TaskCompletionSource Done) punishment;
        lock (waits)
        {
            punishment = Assert.Single(waits, wait => wait.After == TimeSpan.FromSeconds(15));
        }
        punishment.Done.TrySetResult();
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimDestroy>().Any(destroy => destroy.ShipId == character.ShipId));
    }


    [PgFact]
    public async Task A_ship_that_is_in_warp_is_told_so_when_it_is_asked_to_warp_or_to_stop_and_is_not_otherwise_answered()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);

        rig.Sim.WarpAnswer = new CommandReply(false, 0, "IN_WARP");
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyInt(40491233)));
        var told = Assert.Single(Events.Of(session));
        Assert.Equal(("OnRemoteMessage", "clientID"), (told.Name, told.IdType));
        Assert.Equal("ShipInWarp", JsSemantics.StringValue(told.Args[0]));
        Assert.Equal(PyNone.Instance, told.Args[1]);

        // a stop in warp is refused by the simulation and the client is told the same
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(new SimRefused((ulong)session.SessionId, SimCommands.Stop, "IN_WARP"));
        Assert.Equal(["OnRemoteMessage"], Events.Of(session).Select(e => e.Name));
        // other orders that are refused in warp say nothing
        rig.Sim.Say(new SimRefused((ulong)session.SessionId, SimCommands.GotoDirection, "IN_WARP"));
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task When_the_time_is_up_and_the_pilot_has_not_flown_the_client_is_told_the_protection_ended_once()
    {
        var (rig, _, session) = await DockedAsync(TimeSpan.FromMilliseconds(150));
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        Events.Of(session);

        await Rig.UntilAsync(() => Events.Of(session, clear: false).Any(e => e.Name == "OnInvulnCancelled"));
        var events = Events.Of(session);
        var told = events.Single(e => e.Name == "OnInvulnCancelled");
        Assert.Equal(("OnInvulnCancelled", "shipid"), (told.Name, told.IdType));
        Assert.Equal(session.GetInt("shipid"), Id(told.Args[0]));

        // a movement after that has nothing left to end
        var oid = await rig.BindBeyonceAsync(session);
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        await Task.Delay(300);
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnInvulnCancelled");
    }

    [PgFact]
    public async Task Space_sessions_leave_a_shared_local_timer_owned_by_the_foundations()
    {
        using var shared = new Eve.Scheduler.LocalTimers(delay: (_, token) =>
            Task.Delay(Timeout.InfiniteTimeSpan, token));
        var (rig, _, session) = await DockedAsync(Timeout.InfiniteTimeSpan, shared);
        try
        {
            await InSpaceAsync(rig, session);
            Assert.True(shared.Count > 0);
        }
        finally
        {
            await rig.DisposeAsync();
        }

        Assert.True(shared.Count > 0);
    }

    [PgFact]
    public async Task A_pilot_who_flew_before_the_time_was_up_is_not_told_again_when_it_is()
    {
        var (rig, _, session) = await DockedAsync(TimeSpan.FromMilliseconds(400));
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);

        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        Assert.Equal(["OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
        await Task.Delay(800);
        Assert.Empty(Events.Of(session));
    }

    // ── jumps ────────────────────────────────────────────────────────────────

    private const long Gate = 50016630;
    private const long NextGate = 50016839;
    private const int NextSystem = 30100034;

    private static Task<PyValue> JumpAsync(Rig rig, ClientSession session, string oid, long ship) =>
        rig.BeyonceCallAsync(session, "CmdStargateJump", oid, new PyInt((int)Gate), new PyInt((int)NextGate), new PyLong(ship));

    [PgFact]
    public async Task A_jump_is_given_to_the_simulation_the_client_is_told_the_queue_and_the_answer_is_the_object_and_the_time()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var before = rig.Sim.Heard.Count;

        var answer = (PyTuple)await JumpAsync(rig, session, oid, character.ShipId);
        Assert.Equal(oid, JsSemantics.StringValue(answer.Items[0]));
        Assert.Equal(Py.FileTimeFromUnixMs(Rig.ClockMs + 10_000), Id(answer.Items[1]));
        Assert.Equal(
            [Describe(new SimCommand((ulong)session.SessionId, SimCommands.Jump, [Gate, NextGate]))],
            rig.Sim.Heard.Skip(before).Select(Describe));

        var told = Events.Of(session);
        var queue = Assert.Single(told, e => e.Name == "OnJumpQueueUpdate");
        Assert.Equal("clientID", queue.IdType);
        Assert.Equal(NextSystem, Id(queue.Args[0]));
        Assert.Equal(1000, Id(queue.Args[1]));
        Assert.Equal([character.CharacterId], ((PyTuple)queue.Args[2]).Items.Select(Id));
        var stamps = ((PyDict)queue.Args[3]).Entries.Select(e => (Id(e.Key), Id(e.Value))).ToArray();
        Assert.Equal([(character.CharacterId, Py.FileTimeFromUnixMs(Rig.ClockMs))], stamps);
        Assert.Equal(180, Id(queue.Args[4]));
        Assert.True(rig.Space.IsInSpace(session), "the ship has not left yet");

        // asking again does not queue the pilot twice
        await JumpAsync(rig, session, oid, character.ShipId);
        var again = Events.Of(session).Single(e => e.Name == "OnJumpQueueUpdate");
        Assert.Equal([character.CharacterId], ((PyTuple)again.Args[2]).Items.Select(Id));
    }

    [PgFact]
    public async Task A_gate_whose_client_list_leaves_out_the_pilot_ship_group_refuses_the_jump_before_the_simulation_hears_it()
    {
        // the pilot flies a frigate (group 25, the Cockroach); the gate's list names only group 1022 (the Zarzakh rule, subcapitals only)
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        rig.Space.GateRules = new Eve.Services.Space.Gates.GateRules(
            new Dictionary<long, int> { [Gate] = 579 }, new Dictionary<int, IReadOnlySet<int>> { [579] = new HashSet<int> { 1022 } },
            new Dictionary<long, long>(), new Dictionary<long, int>(), new Dictionary<int, Eve.Universe.AlignmentToll>());

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => JumpAsync(rig, session, oid, character.ShipId));

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimCommand { Code: SimCommands.Jump });

        // a list that names the pilot's group admits the jump
        rig.Space.GateRules = new Eve.Services.Space.Gates.GateRules(
            new Dictionary<long, int> { [Gate] = 579 }, new Dictionary<int, IReadOnlySet<int>> { [579] = new HashSet<int> { 25 } },
            new Dictionary<long, long>(), new Dictionary<long, int>(), new Dictionary<int, Eve.Universe.AlignmentToll>());
        Assert.IsType<PyTuple>(await JumpAsync(rig, session, oid, character.ShipId));
        Assert.Contains(rig.Sim.Heard, m => m is SimCommand { Code: SimCommands.Jump });
    }

    [PgFact]
    public async Task A_gate_toll_is_charged_once_when_the_jump_is_taken_and_refused_when_the_wallet_cannot_pay_or_the_simulation_refuses()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        // the gate of the rig's jump has a toll: the minimum for a small ship, 10000 ISK (client alignmentBasedToll, the wiki's "starts at 10k")
        var toll = new Eve.Universe.AlignmentToll(0.015, 1.1, -100_000, 10_000, 0.0, [500011, 500010]);
        rig.Space.GateRules = new Eve.Services.Space.Gates.GateRules(
            new Dictionary<long, int>(), new Dictionary<int, IReadOnlySet<int>>(), new Dictionary<long, long>(),
            new Dictionary<long, int> { [Gate] = 77918 }, new Dictionary<int, Eve.Universe.AlignmentToll> { [77918] = toll });
        var charges = new List<(long Character, decimal Amount, string Key)>();
        var refunds = new List<(long Character, decimal Amount, string Key)>();
        var payable = true;
        rig.Space.ChargeGateToll = (characterId, amount, key, _) => { charges.Add((characterId, amount, key)); return Task.FromResult(payable); };
        rig.Space.RefundGateToll = (characterId, amount, key, _) => { refunds.Add((characterId, amount, key)); return Task.CompletedTask; };

        // a jump the wallet cannot pay is refused, and the simulation hears nothing
        payable = false;
        var unpaid = await Assert.ThrowsAsync<MachoWrappedException>(() => JumpAsync(rig, session, oid, character.ShipId));
        Assert.Equal("CustomNotify", unpaid.Error.UserErrorMessage);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimCommand { Code: SimCommands.Jump });
        Assert.Empty(refunds);
        charges.Clear();

        // a paid jump is charged once and taken
        payable = true;
        Assert.IsType<PyTuple>(await JumpAsync(rig, session, oid, character.ShipId));
        Assert.Single(charges);
        Assert.Equal((character.CharacterId, 10_000m), (charges[0].Character, charges[0].Amount));
        Assert.StartsWith("gate-toll:", charges[0].Key);
        Assert.Contains(rig.Sim.Heard, m => m is SimCommand { Code: SimCommands.Jump });

        // a jump the simulation refuses gets the toll back under its refund key
        charges.Clear();
        rig.Sim.JumpAnswer = new CommandReply(false, 0, "TOO_FAR_FROM_STARGATE");
        await Assert.ThrowsAsync<MachoWrappedException>(() => JumpAsync(rig, session, oid, character.ShipId));
        Assert.Single(charges);
        Assert.Single(refunds);
        Assert.Equal((character.CharacterId, 10_000m, charges[0].Key + ":refund"), refunds[0]);
    }

    [PgFact]
    public async Task A_jump_that_cannot_be_done_is_the_message_the_reference_tells()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);

        async Task<MachoWrappedException> Asked(string reason, long? ship = null)
        {
            rig.Sim.JumpAnswer = new CommandReply(false, 0, reason);
            return await Assert.ThrowsAsync<MachoWrappedException>(() => JumpAsync(rig, session, oid, ship ?? character.ShipId));
        }

        static string Info(MachoWrappedException error)
        {
            var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
            var info = Assert.Single(values.Entries, e => JsSemantics.StringValue(e.Key) == "info").Value;
            return info is PyTuple label ? $"{Id(label.Items[0])}:{JsSemantics.StringValue(label.Items[1])}" : JsSemantics.StringValue(info)!;
        }

        Assert.Equal("TargetingAttemptCancelled", (await Asked("STARGATE_NOT_FOUND")).Error.UserErrorMessage);
        Assert.Equal("TargetingAttemptCancelled", (await Asked("STARGATE_DESTINATION_MISMATCH")).Error.UserErrorMessage);
        Assert.Equal("DeniedShipChanged", (await Asked("NO_SHIP_IN_SPACE")).Error.UserErrorMessage);
        Assert.Equal("DeniedShipChanged", (await Asked("WRONG_SOLAR_SYSTEM")).Error.UserErrorMessage);
        var far = await Asked("TOO_FAR_FROM_STARGATE");
        Assert.Equal(("CustomInfo", "101:UI/Menusvc/MenuHints/NotWithingMaxJumpDist"), (far.Error.UserErrorMessage, Info(far)));
        Assert.Equal("101:UI/GateIcons/GateClosed", Info(await Asked("STARGATE_NOT_ACTIVE")));
        Assert.Equal("Stargate jump already in progress.", Info(await Asked("STARGATE_JUMP_IN_PROGRESS")));
        Assert.Equal("The stargate jump could not be completed.", Info(await Asked("IN_WARP")));
        // a ship that is not the one flown is refused before the simulation hears of it
        var before = rig.Sim.Heard.Count;
        Assert.Equal("DeniedShipChanged", (await Asked("STARGATE_NOT_FOUND", character.ShipId + 1)).Error.UserErrorMessage);
        Assert.Equal(before, rig.Sim.Heard.Count);
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnJumpQueueUpdate");
        Assert.True(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task A_jump_asked_of_a_ship_that_is_not_in_space_or_on_an_object_the_session_was_not_given_does_nothing_or_is_denied()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        // docked: the call is to nothing the session was given
        Assert.Equal(PyNone.Instance, await JumpAsync(rig, session, "N=1:1", character.ShipId));
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Assert.Equal(PyNone.Instance, await JumpAsync(rig, session, "N=1:999", character.ShipId));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimCommand { Code: SimCommands.Jump });
        Assert.NotEmpty(oid);
    }

    [PgFact]
    public async Task When_the_simulation_says_the_ship_has_jumped_the_client_is_told_in_the_reference_order_and_the_ship_and_the_character_are_in_the_other_system()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        await JumpAsync(rig, session, oid, character.ShipId);
        Events.Of(session, clear: false);
        ((Sink)session.Sink!).Sent.Clear();

        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);

        Assert.Equal(["OnMachoObjectDisconnect", "OnItemChange", "OnJumpCloakUpdated", "$sessionchange"], Sequence.Of(session, clear: false));
        Assert.Equal(
            [
                ("solarsystemid", new PyInt(from), new PyInt(NextSystem)),
                ("solarsystemid2", new PyInt(from), new PyInt(NextSystem)),
                ("constellationid", new PyInt(20010001), new PyInt(20010002)),
                ("locationid", new PyInt(from), new PyInt(NextSystem)),
            ],
            Sequence.LastChange(session));

        var events = Events.Of(session);
        Assert.Equal(("OnMachoObjectDisconnect", "+clientID"), (events[0].Name, events[0].IdType));
        Assert.Equal(oid, JsSemantics.StringValue(events[0].Args[0]));
        var item = events[1];
        Assert.Equal(("OnItemChange", "clientID"), (item.Name, item.IdType));
        var row = (PyPackedRow)item.Args[0];
        Assert.Equal((character.ShipId, (long)NextSystem, 0L), (Id(row.Values[0]), Id(row.Values[3]), Id(row.Values[4])));
        Assert.Equal($"Undocking:{Rig.Station}", JsSemantics.StringValue(row.Values[8]));
        Assert.Equal([(3L, (long)from)], ((PyDict)item.Args[1]).Entries.Select(e => (Id(e.Key), Id(e.Value))));
        Assert.Equal(PyNone.Instance, item.Args[2]);
        var cloak = events[2];
        Assert.Equal(("OnJumpCloakUpdated", "shipid"), (cloak.Name, cloak.IdType));
        Assert.Equal((character.ShipId, Py.FileTimeFromUnixMs(Rig.ClockMs) + Eve.Services.Space.SpaceSessions.JumpCloakTicks, Eve.Services.Space.SpaceSessions.JumpCloakTicks), (Id(cloak.Args[0]), Id(cloak.Args[1]), Id(cloak.Args[2])));

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((NextSystem, 0), (ship.LocationId, ship.Flag));
        var record = (await rig.Characters.GetAsync(character.CharacterId))!;
        Assert.Equal((NextSystem, 20010002), (record.SolarSystemId, record.ConstellationId));
        Assert.Equal(NextSystem, rig.Space.SolarSystemOf(session));

        // the client binds the ballpark of the new system, and the simulation is asked to send the scene
        var before = rig.Sim.Heard.Count;
        await rig.BindBeyonceAsync(session);
        session.RunAfterResponse();
        Assert.Contains(rig.Sim.Heard.Skip(before), m => m is SimBind);
    }

    [PgFact]
    public async Task A_jump_that_is_reported_twice_is_finished_once()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        ((Sink)session.Sink!).Sent.Clear();

        var jumped = new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate);
        rig.Sim.Say(jumped);
        rig.Sim.Say(jumped);
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);
        await Task.Delay(200);

        Assert.Single(Sequence.Of(session), "$sessionchange");
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal(NextSystem, ship.LocationId);
    }

    [PgFact]
    public async Task The_cloak_of_a_ship_that_has_jumped_ends_with_the_first_movement_and_the_client_is_told_once_in_the_reference_order()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);
        Events.Of(session);

        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.SetSpeedFraction, 0));
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 0));
        var told = Events.Of(session);
        Assert.Equal(["OnJumpCloakCancelled", "OnInvulnCancelled"], told.Select(e => e.Name));
        Assert.All(told, e => Assert.Equal(("shipid", character.ShipId), (e.IdType, Id(e.Args[0]))));
    }

    [PgFact]
    public async Task The_cloak_of_a_ship_that_has_jumped_ends_when_its_time_is_up_if_the_pilot_has_not_flown()
    {
        var rig = await Rig.CreateAsync(jumpCloak: TimeSpan.FromMilliseconds(150));
        await using var _ = rig;
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);
        Events.Of(session);

        await Rig.UntilAsync(() => ((Sink)session.Sink!).Sent.Count >= 2);
        Assert.Equal(["OnJumpCloakCancelled", "OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
        // and it is not told again by a movement
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 0));
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task A_ship_that_docks_in_the_other_system_is_in_the_hangar_of_a_station_there()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);
        Events.Of(session);

        var station = rig.Stations.All.First(s => s.SolarSystemId == NextSystem);
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, station.Id));
        await Rig.UntilAsync(() => session.GetInt("stationid") == station.Id);

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((station.Id, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        var record = (await rig.Characters.GetAsync(character.CharacterId))!;
        Assert.Equal((station.Id, NextSystem), (record.StationId, record.SolarSystemId));
    }

    [PgFact]
    public async Task When_a_session_ends_after_a_jump_the_ship_is_put_back_in_the_hangar_of_the_station_it_left_and_the_character_is_in_its_system()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);

        session.Close();
        await Rig.UntilAsync(() => rig.Items.GetAsync(character.ShipId).GetAwaiter().GetResult()!.Flag == ItemFlags.Hangar);

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal(Rig.Station, ship.LocationId);
        var record = (await rig.Characters.GetAsync(character.CharacterId))!;
        Assert.Equal((from, Rig.Station), (record.SolarSystemId, record.StationId));
        Assert.Contains(rig.Sim.Heard, m => m is SimLeave { Why: LeaveWhy.LoggedOff });
    }

    [PgFact]
    public async Task The_formations_are_kept_by_the_client_for_the_length_of_the_session()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        var answer = Assert.IsType<PyObject>(await rig.BeyonceCallAsync(session, "GetFormations"));
        Assert.Equal(ObjectCaching.ResultClass, JsSemantics.StringValue(answer.Name));
        var formations = Assert.IsType<PyTuple>(((PySubStream)((PyTuple)answer.Args).Items[1]).Decoded);
        Assert.All(formations.Items, formation =>
        {
            var points = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(formation).Items[1]);
            Assert.Equal(4, points.Items.Count);
            Assert.All(points.Items, point => Assert.All(Assert.IsType<PyTuple>(point).Items,
                coordinate => Assert.IsType<PyInt>(coordinate)));
        });
        Assert.Equal(["Diamond", "Arrow"], formations.Items.Select(f => JsSemantics.StringValue(((PyTuple)f).Items[0])));
    }

    // ── what the simulation says ─────────────────────────────────────────────

    [PgFact]
    public async Task What_the_simulation_says_for_a_session_reaches_that_client_as_the_notification_it_named()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var args = MarshalCodec.Encode(Py.Tuple(new PyList([Py.Tuple(new PyInt(1790679102), Py.Tuple(new PyStr("Stop"), Py.Tuple(new PyLong(9988400001939))))]), PyBool.False));

        rig.Sim.Say(new SimNotify((ulong)session.SessionId, "DoDestinyUpdate", "clientID", args));
        rig.Sim.Say(new SimNotify(999, "DoDestinyUpdate", "clientID", args)); // a session that is not in space: nobody's

        var events = Events.Of(session);
        var update = Assert.Single(events);
        Assert.Equal(("DoDestinyUpdate", "clientID"), (update.Name, update.IdType));
        var actions = ((PyList)update.Args[0]).Items;
        Assert.Equal(1790679102, Id(((PyTuple)actions.Single()).Items[0]));
        Assert.Equal(PyBool.False, update.Args[1]);
    }

    [PgFact]
    public async Task The_target_lock_notices_and_the_damage_state_action_reach_the_pilot_as_the_simulation_sent_them()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        const long other = 9988400002187;
        PyValue Targeted(params PyValue[] words) => Py.Tuple(new PyInt(0), Py.Tuple(new PyInt(1), new PyTuple(words)));
        var targets = new[]
        {
            Targeted(new PyStr("lost"), new PyLong(other)),
            Targeted(new PyStr("otherlost"), new PyLong(other)),
            Targeted(new PyStr("clear")),
        };
        foreach (var args in targets)
        {
            var sent = ((PyTuple)args).Items;
            rig.Sim.Say(new SimNotify((ulong)session.SessionId, "OnTarget", "shipid",
                MarshalCodec.Encode(Py.Tuple(sent[0], sent[1]))));
        }
        var damage = Py.Tuple(
            Py.Tuple(new PyReal(0.5), new PyReal(110000.0), new PyLong(134351527136350000)), new PyReal(1.0), new PyReal(0.25));
        var action = Py.Tuple(new PyInt(1790679103), Py.Tuple(new PyStr("OnDamageStateChange"), Py.Tuple(new PyLong(other), damage)));
        rig.Sim.Say(new SimNotify((ulong)session.SessionId, "DoDestinyUpdate", "clientID",
            MarshalCodec.Encode(Py.Tuple(new PyList([action]), PyBool.False))));

        var events = Events.Of(session);
        Assert.Equal(["OnTarget", "OnTarget", "OnTarget", "DoDestinyUpdate"], events.Select(e => e.Name));
        Assert.All(events.Take(3), e => Assert.Equal("shipid", e.IdType));
        Assert.Equal(
            ["lost", "otherlost", "clear"],
            events.Take(3).Select(e => JsSemantics.StringValue(((PyTuple)((PyTuple)e.Args[1]).Items[1]).Items[0])));
        Assert.Equal(other, Id(((PyTuple)((PyTuple)events[0].Args[1]).Items[1]).Items[1]));
        var delivered = (PyTuple)((PyTuple)((PyList)events[3].Args[0]).Items.Single()).Items[1];
        Assert.Equal("OnDamageStateChange", JsSemantics.StringValue(delivered.Items[0]));
        Assert.Equal(damage, ((PyTuple)delivered.Items[1]).Items[1]);
    }

    [PgFact]
    public async Task A_forced_decloak_notice_reaches_the_pilot_with_the_message_and_its_arguments()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var proximity = Py.Tuple(new PyStr("CloakDeactivatedByProximityToType"), Py.Dict(("proximityTypeID", new PyInt(5))));
        var custom = Py.Tuple(new PyStr("CustomNotify"), Py.Dict(
            ("notify", new PyStr("Your cloak has been deactivated.")),
            ("decloakReason", new PyStr("remoteDecloakPing")),
            ("itemID", new PyLong(1_000_000_000_042)),
            ("distanceMeters", new PyReal(1234.568))));
        foreach (var notice in new[] { proximity, custom })
            rig.Sim.Say(new SimNotify((ulong)session.SessionId, "OnCloakModuleForceDeactivated", "shipid", MarshalCodec.Encode(notice)));

        var events = Events.Of(session);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(("OnCloakModuleForceDeactivated", "shipid"), (e.Name, e.IdType)));
        string Text(PyValue value) => JsSemantics.StringValue(value)!;
        Assert.Equal(["CloakDeactivatedByProximityToType", "CustomNotify"], events.Select(e => Text(e.Args[0])));
        var proximityArgs = Assert.IsType<PyDict>(events[0].Args[1]).Entries;
        Assert.Equal(("proximityTypeID", 5L), (Text(proximityArgs.Single().Key), Id(proximityArgs.Single().Value)));
        var customArgs = Assert.IsType<PyDict>(events[1].Args[1]).Entries.ToDictionary(e => Text(e.Key), e => e.Value);
        Assert.Equal(
            ["notify", "decloakReason", "itemID", "distanceMeters"], customArgs.Keys);
        Assert.Equal("remoteDecloakPing", Text(customArgs["decloakReason"]));
        Assert.Equal(1_000_000_000_042, Id(customArgs["itemID"]));
        Assert.Equal(new PyReal(1234.568), customArgs["distanceMeters"]);
    }

    // ── docking ──────────────────────────────────────────────────────────────

    [PgFact]
    public async Task A_dock_is_answered_with_the_time_it_was_accepted_and_with_nothing_when_it_was_not()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);

        var answer = await rig.BeyonceCallAsync(session, "CmdDock", oid, new PyInt((int)Rig.Station), new PyLong(session.GetInt("shipid")!.Value));
        Assert.Equal(new PyLong(134351527136350000), answer);
        Assert.Equal(Describe(new SimCommand((ulong)session.SessionId, SimCommands.Dock, [Rig.Station])), Describe(rig.Sim.Heard.Last()));

        rig.Sim.DockAnswer = new CommandReply(false, 0, "STATION_NOT_FOUND");
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "CmdDock", oid, new PyInt((int)Rig.Station), new PyLong(1)));
        Assert.True(rig.Space.IsInSpace(session), "a refused docking leaves the ship where it is");
    }

    [PgFact]
    public async Task A_dock_from_beyond_the_range_is_answered_with_the_reference_user_error_and_the_distance_rounded_half_up()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);

        async Task<MachoWrappedException> Asked(double distance)
        {
            rig.Sim.DockAnswer = new CommandReply(false, 0, null, distance);
            return await Assert.ThrowsAsync<MachoWrappedException>(() => rig.BeyonceCallAsync(session, "CmdDock", oid, new PyInt((int)Rig.Station), new PyLong(1)));
        }

        static long DistanceOf(MachoWrappedException error)
        {
            var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
            var (name, distance) = Assert.Single(values.Entries.Select(e => (JsSemantics.StringValue(e.Key), Id(e.Value))));
            Assert.Equal("distance", name);
            return distance;
        }

        var error = await Asked(15974.5);
        Assert.True(error.Error.IsUserError);
        Assert.Equal("DockingApproach", error.Error.UserErrorMessage);
        Assert.Equal(15975, DistanceOf(error));
        Assert.Equal(15974, DistanceOf(await Asked(15974.4)));
        Assert.Equal(0, DistanceOf(await Asked(0.4)));
        Assert.True(rig.Space.IsInSpace(session), "the ship is on its way, not gone");
    }

    [PgFact]
    public async Task When_the_simulation_says_the_ship_has_docked_the_client_is_told_in_the_reference_order_and_the_ship_is_in_the_hangar()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session, clear: false);
        var sink = (Sink)session.Sink!;
        sink.Sent.Clear();

        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        // the docking's notifications are finished by background work: they are all done before the sequence is asserted
        await rig.Space.QuiesceAsync();
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);

        Assert.Equal(["OnInvulnCancelled", "OnMachoObjectDisconnect", "OnItemsChanged", "$sessionchange", "OnDockingFinished"], Sequence.Of(session, clear: false));
        Assert.Equal(
            [("stationid", PyNone.Instance, new PyInt(Rig.Station)), ("locationid", new PyInt(system), new PyInt(Rig.Station)), ("solarsystemid", new PyInt(system), PyNone.Instance)],
            Sequence.LastChange(session));
        Assert.False(rig.Space.IsInSpace(session));

        var events = Events.Of(session);
        Assert.Equal((oid, "+clientID"), (JsSemantics.StringValue(events[1].Args[0]), events[1].IdType));
        var items = events[2];
        Assert.Equal(("OnItemsChanged", "charid"), (items.Name, items.IdType));
        var row = (PyPackedRow)((PyList)items.Args[0]).Items.Single();
        Assert.Equal((character.ShipId, (long)Rig.Station, (long)ItemFlags.Hangar), (Id(row.Values[0]), Id(row.Values[3]), Id(row.Values[4])));
        Assert.Equal("", JsSemantics.StringValue(row.Values[8]));
        Assert.Equal([(3L, (long)system), (4L, 0L)], ((PyDict)items.Args[1]).Entries.Select(e => (Id(e.Key), Id(e.Value))));
        var place = (PyTuple)items.Args[2];
        Assert.Equal(("Station", Rig.Station, "StationHangar"), (JsSemantics.StringValue(place.Items[0]), Id(place.Items[1]), JsSemantics.StringValue(place.Items[2])));
        Assert.Equal(("OnDockingFinished", "charid", (long)Rig.Station), (events[3].Name, events[3].IdType, Id(events[3].Args[0])));

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
    }

    [PgFact]
    public async Task A_docking_that_is_reported_twice_is_finished_once()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        ((Sink)session.Sink!).Sent.Clear();

        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        // the docking's notifications are finished by background work: they are all done before the sequence is asserted
        await rig.Space.QuiesceAsync();
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);
        await Task.Delay(100);

        var told = Sequence.Of(session);
        Assert.Equal(1, told.Count(name => name == "OnDockingFinished"));
        Assert.Equal(1, told.Count(name => name == "OnInvulnCancelled"));
        Assert.Equal(1, told.Count(name => name == "OnItemsChanged"));
    }

    [PgFact]
    public async Task A_ship_can_undock_again_after_it_has_docked()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        // the docking's notifications are finished by background work: they are all done before the sequence is asserted
        await rig.Space.QuiesceAsync();
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);

        var (_, answer) = await UndockAsync(rig, session);
        Assert.IsType<PyTuple>(answer);
        Assert.True(rig.Space.IsInSpace(session));
    }

    // ── losing the ship ──────────────────────────────────────────────────────

    [PgFact]
    public async Task When_the_simulation_is_lost_the_ship_docks_at_the_station_the_character_left()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        ((Sink)session.Sink!).Sent.Clear();

        rig.Sim.Lose();
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);

        Assert.Contains("OnDockingFinished", Sequence.Of(session));
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        Assert.False(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task When_a_session_ends_in_space_the_simulation_is_told_and_the_ship_is_put_back_in_the_hangar()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);

        session.Close();

        var leave = new SimLeave((ulong)session.SessionId, LeaveWhy.LoggedOff);
        await Rig.UntilAsync(() => rig.Sim.Heard.Contains(leave));
        Assert.Contains(leave, rig.Sim.Heard);
        await Rig.UntilAsync(() => rig.Items.GetAsync(character.ShipId).GetAwaiter().GetResult() is { LocationId: Rig.Station });
        Assert.False(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task Space_entry_reloads_authoritative_state_after_preflight_and_previous_pilot_leave()
    {
        var (rig, previous, previousSession) = await DockedAsync();
        await using var _ = rig;
        var entering = await rig.NewCharacterAsync("Second Space Tester");
        var enteringSession = await rig.SessionAsync(entering, id: 2);
        var sim = new FakeSim();
        using var space = new SpaceSessions(
            sim, rig.Ships, rig.Statics, rig.Stations, rig.Systems, () => rig.Now, spaceItems: rig.SpaceItems);

        async Task SaveSpaceItemAsync(long characterId, IReadOnlyList<double> position)
        {
            var snapshot = await rig.Ships.LoadAsync(characterId, CancellationToken.None);
            Assert.NotNull(snapshot);
            var ship = SimShips.Of(snapshot!, rig.Statics);
            await rig.SpaceItems.SaveAsync(new Eve.Ships.SpaceItem(
                ship.ShipId,
                snapshot!.Character.SolarSystemId,
                position,
                [0d, 0d, 1d],
                ship.Radius,
                ship.GroupId,
                0,
                ship.CharacterId,
                ship.CorporationId,
                ship.AllianceId,
                ship.WarFactionId,
                long.MaxValue), CancellationToken.None);
        }

        async Task PutShipInSpaceAsync(long characterId, IReadOnlyList<double> position)
        {
            Assert.NotNull((await rig.Ships.UndockAsync(characterId, CancellationToken.None)).Moved);
            await SaveSpaceItemAsync(characterId, position);
        }

        double[] previousInitialPose = [100d, 200d, 300d];
        double[] previousSavedPose = [400d, 500d, 600d];
        double[] enteringPreparedPose = [10d, 20d, 30d];
        double[] enteringCurrentPose = [40d, 50d, 60d];
        await PutShipInSpaceAsync(previous.CharacterId, previousInitialPose);
        await PutShipInSpaceAsync(entering.CharacterId, enteringPreparedPose);
        sim.PositionAnswers[previous.ShipId] = previousSavedPose;

        var previousAttributes = await space.GetSessionAttributesAsync(previousSession, previous.CharacterId, CancellationToken.None);
        previousSession.Change([.. previousAttributes]);
        await space.OnCharacterEnteredAsync(previousSession, previous.CharacterId, CancellationToken.None);
        Assert.True(space.IsInSpace(previousSession));
        var repeatedAttributes = await space.GetSessionAttributesAsync(previousSession, previous.CharacterId, CancellationToken.None);
        Assert.Equal(PyNone.Instance, Assert.Single(repeatedAttributes, attribute => attribute.Name == "stationid").Value);
        Assert.Equal(Eve.Ships.CharacterShips.InSpaceFlag, (await rig.Ships.LoadAsync(previous.CharacterId, CancellationToken.None))!.Ship.Flag);

        var enteringAttributes = await space.GetSessionAttributesAsync(previousSession, entering.CharacterId, CancellationToken.None);
        Assert.True(space.IsInSpace(previousSession));
        Assert.DoesNotContain(sim.Heard.OfType<SimLeave>(), leave => leave.SessionId == (ulong)previousSession.SessionId);
        await SaveSpaceItemAsync(entering.CharacterId, enteringCurrentPose);

        await space.OnCharacterLeavingAsync(previousSession, previous.CharacterId, CancellationToken.None);
        Assert.False(space.IsInSpace(previousSession));
        Assert.Contains(new SimLeave((ulong)previousSession.SessionId, LeaveWhy.LoggedOff), sim.Heard);

        await space.OnCharacterEnteredAsync(previousSession, previous.CharacterId, CancellationToken.None);
        Assert.True(space.IsInSpace(previousSession));
        var previousEnters = sim.Heard.OfType<SimEnter>()
            .Where(enter => enter.SessionId == (ulong)previousSession.SessionId)
            .ToArray();
        Assert.Equal(2, previousEnters.Length);
        Assert.Equal(previousInitialPose, previousEnters[0].Position);
        Assert.Equal(previousSavedPose, previousEnters[1].Position);

        enteringSession.Change([.. enteringAttributes]);
        await space.OnCharacterEnteredAsync(enteringSession, entering.CharacterId, CancellationToken.None);
        Assert.True(space.IsInSpace(enteringSession));
        var enteringEnter = Assert.Single(sim.Heard.OfType<SimEnter>(),
            enter => enter.SessionId == (ulong)enteringSession.SessionId);
        Assert.Equal(enteringCurrentPose, enteringEnter.Position);

        await space.OnCharacterLeavingAsync(previousSession, previous.CharacterId, CancellationToken.None);
        Assert.True(await rig.SpaceItems.RemoveAsync(previous.ShipId, CancellationToken.None));
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await space.OnCharacterEnteredAsync(previousSession, previous.CharacterId, CancellationToken.None));
        Assert.False(space.IsInSpace(previousSession));
        await space.OnCharacterLeavingAsync(enteringSession, entering.CharacterId, CancellationToken.None);
    }

    [Fact]
    public void Fleet_roles_are_limited_to_valid_hierarchy_positions()
    {
        Assert.Equal(25, Eve.Services.Space.FleetStore.MaxWings);
        Assert.Equal(25, Eve.Services.Space.FleetStore.MaxSquadsPerWing);
        Assert.Equal(256, Eve.Services.Space.FleetStore.MaxMembers);
        Assert.True(Eve.Services.Space.FleetStore.HasBossJob(Eve.Services.Space.FleetStore.CreatorJob));
        Assert.True(Eve.Services.Space.FleetStore.HasBossJob(Eve.Services.Space.FleetStore.CreatorJob | 4));
        Assert.False(Eve.Services.Space.FleetStore.HasBossJob(4));
        Assert.True(Eve.Services.Space.FleetStore.IsValidAssignment(Eve.Services.Space.FleetStore.Leader, -1, -1));
        Assert.True(Eve.Services.Space.FleetStore.IsValidAssignment(Eve.Services.Space.FleetStore.WingCommander, 10, -1));
        Assert.True(Eve.Services.Space.FleetStore.IsValidAssignment(Eve.Services.Space.FleetStore.SquadCommander, 10, 20));
        Assert.True(Eve.Services.Space.FleetStore.IsValidAssignment(Eve.Services.Space.FleetStore.Member, -1, -1));
        Assert.False(Eve.Services.Space.FleetStore.IsValidAssignment(Eve.Services.Space.FleetStore.WingCommander, -1, -1));
        Assert.False(Eve.Services.Space.FleetStore.IsValidAssignment(Eve.Services.Space.FleetStore.SquadCommander, 10, -1));
        Assert.False(Eve.Services.Space.FleetStore.IsValidAssignment(Eve.Services.Space.FleetStore.Leader, 10, 20));
    }


    [Fact]
    public void Fleet_warp_formations_decode_settings_and_keep_relative_fleet_positions()
    {
        var parsed = FleetWarpFormation.Parse(new PyDict([
            new(new PyStr("formationType"), new PyInt(4)),
            new(new PyStr("formationSpacing"), new PyInt(103)),
            new(new PyStr("formationSize"), new PyInt(106)),
        ]));

        Assert.Equal(new FleetFormationSettings(4, 4_000, 50_000, SpacingSkillLevel: 3, SizeSkillLevel: 4), parsed);
        Assert.Null(FleetWarpFormation.Parse(PyNone.Instance));

        var start = new WarpCoordinates(30_000_142, 0, 0, 0);
        var landing = new WarpCoordinates(30_000_142, 1_000_000, 100_000, 0);
        foreach (var type in Enumerable.Range(0, 5))
        {
            var settings = new FleetFormationSettings(type, 2_000, 20_000);
            var destinations = Enumerable.Range(0, 4)
                .Select(index => FleetWarpFormation.Destination(settings, index, 4, start, landing, null))
                .ToArray();

            Assert.All(destinations, point => Assert.NotNull(point));
            Assert.Equal(4, destinations.Distinct().Count());
            Assert.All(destinations, point =>
            {
                var distance = Math.Sqrt(
                    Math.Pow(point!.Value.X - landing.X, 2)
                    + Math.Pow(point.Value.Y - landing.Y, 2)
                    + Math.Pow(point.Value.Z - landing.Z, 2));
                Assert.InRange(distance, 1, 20_000);
            });
        }

        var relative = new FleetFormationSettings(5, 1_000, 10_000);
        var nearStart = new WarpCoordinates(30_000_142, 10_000, 5_000, -2_000);
        var nearLanding = FleetWarpFormation.Destination(relative, 0, 1, start, landing, nearStart);
        Assert.Equal(
            new WarpCoordinates(30_000_142, landing.X + 10_000, landing.Y + 5_000, landing.Z - 2_000),
            nearLanding);
        Assert.Null(FleetWarpFormation.Destination(
            relative, 0, 1, start, landing, new WarpCoordinates(30_000_142, 200_001, 0, 0)));
    }

    [Fact]
    public void Fleet_warp_formations_apply_their_client_shapes_within_configured_size()
    {
        const int system = 30_000_142;
        var origin = new WarpCoordinates(system, 0, 0, 0);
        var landing = new WarpCoordinates(system, 1_000_000, 0, 0);
        WarpCoordinates?[] Destinations(int type) => Enumerable.Range(0, 4)
            .Select(index => FleetWarpFormation.Destination(
                new FleetFormationSettings(type, 2_000, 20_000),
                index, 4, origin, landing, null))
            .ToArray();
        static double Distance(WarpCoordinates point, WarpCoordinates center) =>
            Math.Sqrt(Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2) + Math.Pow(point.Z - center.Z, 2));

        var point = Destinations(0);
        Assert.All(point, destination => Assert.InRange(Distance(destination!.Value, landing), 0, 2_000));

        var sphere = Destinations(1);
        Assert.All(sphere, destination => Assert.InRange(Distance(destination!.Value, landing), 0, 10_000));

        var plane = Destinations(2);
        Assert.All(plane, destination => Assert.Equal(landing.Y, destination!.Value.Y));

        var wall = Destinations(3);
        Assert.All(wall, destination => Assert.Equal(landing.X, destination!.Value.X));

        var arrow = Destinations(4);
        Assert.All(arrow, destination => Assert.True(destination!.Value.X < landing.X));
    }

    [Fact]
    public void Fleet_warp_formations_enforce_pinned_skill_and_distance_levels()
    {
        Assert.Equal(57317, FleetWarpFormation.FormationSkillTypeId);
        Assert.Equal(57318, FleetWarpFormation.FormationDistanceSkillTypeId);

        var point = new FleetFormationSettings(0, 1_000, 10_000);
        Assert.True(FleetWarpFormation.Available(point, 0, 0));

        var sphere = new FleetFormationSettings(1, 1_000, 10_000);
        Assert.False(FleetWarpFormation.Available(sphere, 0, 0));
        Assert.True(FleetWarpFormation.Available(sphere, 1, 0));

        var largeSphere = new FleetFormationSettings(1, 1_000, 50_000, SizeSkillLevel: 4);
        Assert.False(FleetWarpFormation.Available(largeSphere, 1, 3));
        Assert.True(FleetWarpFormation.Available(largeSphere, 1, 4));

        var widePlane = new FleetFormationSettings(2, 4_000, 30_000, SpacingSkillLevel: 4, SizeSkillLevel: 2);
        Assert.False(FleetWarpFormation.Available(widePlane, 2, 3));
        Assert.True(FleetWarpFormation.Available(widePlane, 2, 4));

        var relative = new FleetFormationSettings(5, 1_000, 10_000);
        Assert.False(FleetWarpFormation.Available(relative, 4, 5));
        Assert.True(FleetWarpFormation.Available(relative, 5, 0));
    }

    [PgFact]
    public async Task Fleet_invite_join_and_leave_are_persisted_with_the_member_hierarchy()
    {
        await using var rig = await Rig.CreateAsync();
        var store = rig.FleetStore;
        var reloaded = new Eve.Services.Space.FleetStore(rig.Source, rig.FleetSchema);
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var memberId = leaderId + 1;
        var fleetId = await store.CreateAsync(leaderId);
        try
        {
            Assert.True(fleetId > 0);
            var wingId = await store.CreateWingAsync(fleetId, leaderId);
            var squadId = await store.CreateSquadAsync(fleetId, leaderId, wingId);
            Assert.True(wingId > 0);
            Assert.True(squadId > 0);
            Assert.True(await store.InviteAsync(fleetId, leaderId, memberId, wingId, squadId, Eve.Services.Space.FleetStore.SquadCommander));

            var joined = await store.JoinAsync(fleetId, memberId);
            Assert.NotNull(joined);
            Assert.Equal(Eve.Services.Space.FleetStore.SquadCommander, joined.Role);
            Assert.Equal(wingId, joined.WingId);
            Assert.Equal(squadId, joined.SquadId);
            Assert.Equal(fleetId, await store.FleetIdForCharacterAsync(leaderId));
            Assert.Equal(fleetId, await store.FleetIdForCharacterAsync(memberId));

            var persisted = await reloaded.SnapshotAsync(fleetId);
            Assert.NotNull(persisted);
            Assert.Equal(2, persisted.Members.Count);
            Assert.Single(persisted.Wings);
            Assert.Single(persisted.Wings[0].Squads);
            Assert.Equal(joined, persisted.Members.Single(member => member.CharacterId == memberId));

            Assert.NotNull(await store.LeaveAsync(fleetId, memberId));
            Assert.Null(await reloaded.MemberForCharacterAsync(memberId));
            Assert.Null(await reloaded.FleetIdForCharacterAsync(memberId));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
        }
    }


    [PgFact]
    public async Task Fleet_invites_and_membership_publish_client_notifications_and_session_changes()
    {
        await using var rig = await Rig.CreateAsync();
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var inviteeId = leaderId + 1;
        var leaderSession = new ClientSession(new Sink());
        var inviteeSession = new ClientSession(new Sink());
        leaderSession.Set("charid", new PyLong(leaderId));
        inviteeSession.Set("charid", new PyLong(inviteeId));
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leaderId);
        var fleet = new Eve.Services.Space.FleetObjectService(
            store, () => Rig.ClockMs,
            sessionForCharacter: id => id == leaderId ? leaderSession : id == inviteeId ? inviteeSession : null);
        PyValue Field(PyValue value, string name)
        {
            var dict = value is PyObject keyVal ? Assert.IsType<PyDict>(keyVal.Args) : Assert.IsType<PyDict>(value);
            return Assert.Single(dict.Entries, entry => JsSemantics.StringValue(entry.Key) == name).Value;
        }
        try
        {
            var invite = await FleetObjectCallAsync(fleet, fleetId, leaderSession, "Invite",
                [new PyLong(inviteeId)]);
            Assert.Equal(PyBool.True, invite);

            var inviteEvent = Assert.Single(Events.Of(inviteeSession));
            Assert.Equal("OnFleetInvite", inviteEvent.Name);
            Assert.Equal("clientID", inviteEvent.IdType);
            Assert.Equal(new PyLong(fleetId), inviteEvent.Args[0]);
            Assert.Equal(new PyLong(leaderId), inviteEvent.Args[1]);
            Assert.Equal("AskJoinFleet", JsSemantics.StringValue(inviteEvent.Args[2]));
            Assert.Equal(PyBool.False, Field(inviteEvent.Args[3], "autoAccept"));
            var inviterName = Assert.IsType<PyTuple>(Field(inviteEvent.Args[3], "name"));
            Assert.Equal(new PyInt(2), inviterName.Items[0]);
            Assert.Equal(new PyLong(leaderId), inviterName.Items[1]);

            Assert.Equal(PyBool.True, await FleetObjectCallAsync(fleet, fleetId, inviteeSession, "AcceptInvite", [new PyInt(603)]));
            Assert.Equal(fleetId, inviteeSession.GetInt("fleetid"));
            Assert.Equal(Eve.Services.Space.FleetStore.Member, inviteeSession.GetInt("fleetrole"));

            var joinNotifications = Events.Of(inviteeSession);
            var joinFlowEvents = joinNotifications
                .Where(item => item.Name is "OnFleetRespawnPointsUpdate" or "OnFleetStateChange" or "OnFleetJoin")
                .Select(item => item.Name).ToArray();
            Assert.Equal(["OnFleetRespawnPointsUpdate", "OnFleetStateChange", "OnFleetJoin"], joinFlowEvents);
            var joinedEvent = Assert.Single(joinNotifications, item => item.Name == "OnFleetJoin");
            Assert.Equal(new PyLong(603), Field(joinedEvent.Args[0], "shipTypeID"));
            Assert.Contains(Events.Of(leaderSession), item => item.Name == "OnFleetJoin");
            Assert.Equal(603, (await store.MemberForCharacterAsync(inviteeId))?.ShipTypeId);

            Assert.Equal(PyNone.Instance, await FleetObjectCallAsync(fleet, fleetId, inviteeSession, "LeaveFleet", []));
            Assert.Null(inviteeSession.GetInt("fleetid"));
            Assert.Equal(new PyLong(inviteeId),
                Assert.Single(Events.Of(inviteeSession), item => item.Name == "OnFleetLeave").Args[0]);
            Assert.Equal(new PyLong(inviteeId),
                Assert.Single(Events.Of(leaderSession), item => item.Name == "OnFleetLeave").Args[0]);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
        }
    }

[PgFact]
    public async Task Fleet_warp_is_scoped_to_the_commander_group_and_honors_opt_outs()
    {
        var (rig, leader, leaderSession) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, leaderSession);
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        try
        {
            var wing = await store.CreateWingAsync(fleetId, leader.CharacterId);
            var squad = await store.CreateSquadAsync(fleetId, leader.CharacterId, wing);

            var wingCommander = await rig.NewCharacterAsync("Warp Commander");
            var wingCommanderSession = await rig.SessionAsync(wingCommander, id: 2);
            await InSpaceAsync(rig, wingCommanderSession);
            var inScope = await rig.NewCharacterAsync("Warp Member");
            var inScopeSession = await rig.SessionAsync(inScope, id: 3);
            await InSpaceAsync(rig, inScopeSession);

            Assert.True(await store.InviteAsync(fleetId, leader.CharacterId, wingCommander.CharacterId,
                wing, FleetStore.NoUnit, FleetStore.WingCommander));
            Assert.NotNull(await store.JoinAsync(fleetId, wingCommander.CharacterId));
            Assert.True(await store.InviteAsync(fleetId, leader.CharacterId, inScope.CharacterId,
                wing, squad, FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, inScope.CharacterId));

            Assert.NotNull(await store.SetMemberOptionsAsync(fleetId, inScope.CharacterId, acceptsFleetWarp: false));
            var oid = await rig.BindBeyonceAsync(wingCommanderSession);

            var beforeOptOutWarp = rig.Sim.Heard.Count;
            var optedOutAnswer = await rig.BeyonceCallWithAsync(
                wingCommanderSession, "CmdWarpToStuff", oid,
                [new PyStr("item"), new PyLong(40491233)],
                ("fleet", new PyInt(1)));

            Assert.Equal(PyNone.Instance, optedOutAnswer);
            var optedOutWarps = rig.Sim.Heard.Skip(beforeOptOutWarp).OfType<SimCommand>()
                .Where(command => command.Code == SimCommands.WarpTo)
                .OrderBy(command => command.SessionId)
                .ToArray();
            Assert.Equal(new ulong[] { 2 }, optedOutWarps.Select(command => command.SessionId));

            Assert.NotNull(await store.SetMemberOptionsAsync(fleetId, inScope.CharacterId, acceptsFleetWarp: true));
            rig.Sim.PositionAnswers[wingCommander.ShipId] = [0, 0, 0];
            rig.Sim.WarpDestinationAnswer = [1_000_000, 0, 0];
            var beforeAcceptedWarp = rig.Sim.Heard.Count;
            var acceptedAnswer = await rig.BeyonceCallWithAsync(
                wingCommanderSession, "CmdWarpToStuff", oid,
                [new PyStr("item"), new PyLong(40491233)],
                ("fleet", new PyInt(1)),
                ("fleetFormationSettings", new PyDict([
                    new(new PyStr("formationType"), new PyInt(4)),
                    new(new PyStr("formationSpacing"), new PyInt(100)),
                    new(new PyStr("formationSize"), new PyInt(106)),
                ])));

            Assert.Equal(PyNone.Instance, acceptedAnswer);
            var acceptedCommands = rig.Sim.Heard.Skip(beforeAcceptedWarp).OfType<SimCommand>().ToArray();
            var acceptedWarps = acceptedCommands
                .Where(command => command.Code == SimCommands.WarpTo)
                .OrderBy(command => command.SessionId)
                .ToArray();
            var formationWarps = acceptedCommands
                .Where(command => command.Code == SimCommands.WarpToPoint)
                .ToArray();
            Assert.Equal(new ulong[] { 2 }, acceptedWarps.Select(command => command.SessionId));
            var memberWarp = Assert.Single(formationWarps);
            Assert.Equal((ulong)3, memberWarp.SessionId);
            Assert.Equal(4, memberWarp.Args.Count);
            Assert.NotEqual(new double[] { 1_000_000, 0, 0 }, memberWarp.Args.Take(3));
            Assert.Contains(acceptedCommands, command => command.Code == SimCommands.ResolveWarpTarget);
            Assert.Contains(acceptedCommands, command => command.Code == SimCommands.PositionOf);
        }
        finally
        {
            if (fleetId > 0)
                await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

[PgFact]
    public async Task Fleet_warp_to_member_uses_the_members_live_ship()
    {
        var (rig, leader, leaderSession) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, leaderSession);
        var member = await rig.NewCharacterAsync("Warp Member");
        var memberSession = await rig.SessionAsync(member, id: 2);
        await InSpaceAsync(rig, memberSession);
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        try
        {
            var wing = await store.CreateWingAsync(fleetId, leader.CharacterId);
            var squad = await store.CreateSquadAsync(fleetId, leader.CharacterId, wing);
            Assert.True(await store.InviteAsync(fleetId, leader.CharacterId, member.CharacterId,
                wing, squad, FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, member.CharacterId));

            var oid = await rig.BindBeyonceAsync(leaderSession);
            var before = rig.Sim.Heard.Count;
            var answer = await rig.BeyonceCallWithAsync(
                leaderSession, "CmdWarpToStuff", oid,
                [new PyStr("char"), new PyLong(member.CharacterId)],
                ("fleet", new PyInt(1)));

            Assert.Equal(PyNone.Instance, answer);
            var warps = rig.Sim.Heard.Skip(before).OfType<SimCommand>()
                .Where(command => command.Code == SimCommands.WarpTo)
                .OrderBy(command => command.SessionId)
                .ToArray();
            Assert.Equal(new ulong[] { 1, 2 }, warps.Select(command => command.SessionId));
            Assert.All(warps, command => Assert.Equal((double)member.ShipId, command.Args[0]));
        }
        finally
        {
            if (fleetId > 0)
                await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

    [PgFact]
    public async Task Fleet_boss_leaving_transfers_the_boss_job_to_the_fleet_commander()
    {
        await using var rig = await Rig.CreateAsync();
        var store = rig.FleetStore;
        var bossId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var commanderId = bossId + 1;
        var fleetId = await store.CreateAsync(bossId);
        try
        {
            Assert.True(await store.InviteAsync(fleetId, bossId, commanderId,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit,
                Eve.Services.Space.FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, commanderId));
            Assert.True(await store.MakeLeaderAsync(fleetId, bossId, commanderId));

            Assert.NotNull(await store.LeaveAsync(fleetId, bossId));

            var current = await store.SnapshotAsync(fleetId);
            Assert.NotNull(current);
            Assert.DoesNotContain(current.Members, member => member.CharacterId == bossId);
            var commander = current.Members.Single(member => member.CharacterId == commanderId);
            Assert.Equal(Eve.Services.Space.FleetStore.Leader, commander.Role);
            Assert.True(Eve.Services.Space.FleetStore.HasBossJob(commander.Job));
        }
        finally
        {
            if (fleetId > 0 &&
                !await store.DisbandAsync(fleetId, commanderId))
                await store.DisbandAsync(fleetId, bossId);
        }
    }

    [PgFact]
    public async Task Fleet_boss_who_is_commander_leaves_and_promotes_a_member_in_the_top_squad()
    {
        await using var rig = await Rig.CreateAsync();
        var store = rig.FleetStore;
        var bossId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var memberId = bossId + 1;
        var fleetId = await store.CreateAsync(bossId);
        try
        {
            var wingId = await store.CreateWingAsync(fleetId, bossId);
            var squadId = await store.CreateSquadAsync(fleetId, bossId, wingId);
            Assert.True(await store.InviteAsync(fleetId, bossId, memberId, wingId, squadId,
                Eve.Services.Space.FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, memberId));

            Assert.NotNull(await store.LeaveAsync(fleetId, bossId));

            var current = await store.SnapshotAsync(fleetId);
            Assert.NotNull(current);
            Assert.DoesNotContain(current.Members, member => member.CharacterId == bossId);
            var successor = current.Members.Single(member => member.CharacterId == memberId);
            Assert.Equal(Eve.Services.Space.FleetStore.Member, successor.Role);
            Assert.True(Eve.Services.Space.FleetStore.HasBossJob(successor.Job));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, memberId);
        }
    }


    [Fact]
    public void Fleet_added_bound_manager_and_proxy_rpcs_are_registered_for_dispatch()
    {
        using var source = Npgsql.NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest("Host=127.0.0.1;Database=unused;Username=unused;Password=unused"));
        var store = new Eve.Services.Space.FleetStore(source);
        Eve.Services.IService bound = new Eve.Services.Space.FleetObjectService(store, () => Rig.ClockMs);
        Eve.Services.IService manager = new Eve.Services.Space.FleetManagerService(store);
        Eve.Services.IService proxy = new Eve.Services.Space.FleetProxyService(store);

        string[] boundMethods =
        [
            "SetTakesFleetWarp", "SetAcceptsConduitJumpsValue", "SetAcceptsRegroupValue", "SetOptions",
            "GetFleetComposition", "SendBroadcast", "GetWings", "UpdateMemberInfo", "SetMotdEx", "GetMotd",
            "GetFleetMaxSize", "LoadFleetSetup", "SetFleetMaxSize", "MassInvite", "Reconnect", "FinishMove"
        ];
        string[] managerMethods =
        [
            "AddToWatchlist", "RemoveFromWatchlist", "RegisterForDamageUpdates",
            "BroadcastToBubble", "BroadcastToSystem"
        ];
        string[] proxyMethods =
        [
            "GetAvailableFleetAds", "AddFleetFinderAdvert", "UpdateFleetAdvertWithNewLeader",
            "RemoveFleetFinderAdvert", "GetMyFleetFinderAdvert", "UpdateAdvertInfo",
            "UpdateAdvertAllowedEntities"
        ];

        Assert.Equal(28, boundMethods.Length + managerMethods.Length + proxyMethods.Length);
        Assert.All(boundMethods, method => Assert.True(bound.HasMethod(method), method));
        Assert.All(managerMethods, method => Assert.True(manager.HasMethod(method), method));
        Assert.All(proxyMethods, method => Assert.True(proxy.HasMethod(method), method));
    }

    [PgFact]
    public async Task Fleet_max_member_count_wire_limits_joins_and_init_state_tracks_members()
    {
        await using var rig = await Rig.CreateAsync();
        var leader = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(leader);
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        var memberOne = Random.Shared.NextInt64(8_000_000_000_000, 9_000_000_000_000);
        var memberTwo = memberOne + 1;
        var fleet = new Eve.Services.Space.FleetObjectService(store, () => Rig.ClockMs);

        try
        {
            Assert.Equal(PyNone.Instance, await FleetObjectCallAsync(
                fleet, fleetId, session, "SetFleetMaxSize", [new PyInt(2)]));
            Assert.Equal(new PyInt(2), await FleetObjectCallAsync(
                fleet, fleetId, session, "GetFleetMaxSize", []));

            var wingId = await store.CreateWingAsync(fleetId, leader.CharacterId);
            var squadId = await store.CreateSquadAsync(fleetId, leader.CharacterId, wingId);
            Assert.True(wingId > 0);
            Assert.True(squadId > 0);
            Assert.True(await store.InviteAsync(fleetId, leader.CharacterId, memberOne,
                wingId, squadId, Eve.Services.Space.FleetStore.Member));
            Assert.True(await store.InviteAsync(fleetId, leader.CharacterId, memberTwo,
                wingId, squadId, Eve.Services.Space.FleetStore.Member));

            Assert.NotNull(await store.JoinAsync(fleetId, memberOne));
            Assert.Null(await store.JoinAsync(fleetId, memberTwo));

            var snapshot = await store.SnapshotAsync(fleetId);
            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot!.Members.Count);
            Assert.Equal(2, await store.MaxMemberCountAsync(fleetId));
            Assert.Contains(snapshot.Members, member => member.CharacterId == leader.CharacterId);
            Assert.Contains(snapshot.Members, member => member.CharacterId == memberOne);
            Assert.DoesNotContain(snapshot.Members, member => member.CharacterId == memberTwo);

            var initState = Assert.IsType<PyObject>(await FleetObjectCallAsync(
                fleet, fleetId, session, "GetInitState", []));
            Assert.Equal(new PyStr("util.KeyVal"), initState.Name);
            var fields = Assert.IsType<PyDict>(initState.Args).Entries
                .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
            var members = Assert.IsType<PyDict>(fields["members"]);
            Assert.Equal(2, members.Entries.Count);
            Assert.Contains(members.Entries, entry => entry.Key == new PyLong(leader.CharacterId));
            Assert.Contains(members.Entries, entry => entry.Key == new PyLong(memberOne));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

    [PgFact]
    public async Task Fleet_join_requests_and_invites_remain_consistent_through_acceptance_and_replay()
    {
        await using var rig = await Rig.CreateAsync();
        var store = rig.FleetStore;
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var applicantId = leaderId + 1;
        var fleetId = await store.CreateAsync(leaderId);
        try
        {
            Assert.True(await store.RequestJoinAsync(fleetId, applicantId));
            Assert.Contains((await store.SnapshotAsync(fleetId))!.JoinRequests, request => request.CharacterId == applicantId);

            Assert.True(await store.InviteAsync(fleetId, leaderId, applicantId,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.Member));
            Assert.DoesNotContain((await store.SnapshotAsync(fleetId))!.JoinRequests, request => request.CharacterId == applicantId);
            Assert.False(await store.RequestJoinAsync(fleetId, applicantId), "an outstanding invite excludes a join request");

            Assert.NotNull(await store.JoinAsync(fleetId, applicantId));
            Assert.Null(await store.JoinAsync(fleetId, applicantId));
            Assert.Equal(fleetId, await store.FleetIdForCharacterAsync(applicantId));
            Assert.DoesNotContain((await store.SnapshotAsync(fleetId))!.JoinRequests, request => request.CharacterId == applicantId);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
        }
    }

    [PgFact]
    public async Task Fleet_join_persists_the_ship_type_supplied_by_acceptance()
    {
        await using var rig = await Rig.CreateAsync();
        var store = rig.FleetStore;
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var memberId = leaderId + 1;
        var fleetId = await store.CreateAsync(leaderId);
        try
        {
            Assert.True(await store.InviteAsync(fleetId, leaderId, memberId,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit,
                Eve.Services.Space.FleetStore.Member));
            var joined = await store.JoinAsync(fleetId, memberId, 603);
            Assert.Equal(603, joined?.ShipTypeId);
            Assert.Equal(603, (await store.MemberForCharacterAsync(memberId))?.ShipTypeId);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
        }
    }

    [PgFact]
    public async Task Fleet_invite_acceptance_enforces_the_advert_join_limit()
    {
        await using var rig = await Rig.CreateAsync();
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var firstId = leaderId + 1;
        var secondId = leaderId + 2;
        var leaderSession = new ClientSession(new Sink());
        var firstSession = new ClientSession(new Sink());
        var secondSession = new ClientSession(new Sink());
        leaderSession.Set("charid", new PyLong(leaderId));
        firstSession.Set("charid", new PyLong(firstId));
        secondSession.Set("charid", new PyLong(secondId));
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leaderId);
        var wingId = await store.CreateWingAsync(fleetId, leaderId);
        var squadId = await store.CreateSquadAsync(fleetId, leaderId, wingId);
        var advert = Py.KeyVal([
            ("fleetName", new PyWString("Capped fleet")),
            ("description", new PyWString("two pilots")),
            ("inviteScope", new PyInt(16)),
            ("joinNeedsApproval", PyBool.False),
            ("advertJoinLimit", new PyInt(2))
        ]);
        var fleet = new Eve.Services.Space.FleetObjectService(
            store, () => Rig.ClockMs,
            sessionForCharacter: id => id switch
            {
                var value when value == leaderId => leaderSession,
                var value when value == firstId => firstSession,
                var value when value == secondId => secondSession,
                _ => null
            });
        try
        {
            Assert.IsType<PyDict>(await store.FleetFinderAdvertOperationAsync(
                "AddFleetFinderAdvert", leaderId, [advert], leaderSession));
            Assert.True(await store.InviteAsync(fleetId, leaderId, firstId, wingId, squadId,
                Eve.Services.Space.FleetStore.Member));
            Assert.True(await store.InviteAsync(fleetId, leaderId, secondId, wingId, squadId,
                Eve.Services.Space.FleetStore.Member));

            Assert.Equal(PyBool.True, await FleetObjectCallAsync(
                fleet, fleetId, firstSession, "AcceptInvite", [new PyInt(603)]));
            await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
                FleetObjectCallAsync(fleet, fleetId, secondSession, "AcceptInvite", [new PyInt(603)]));

            var snapshot = await store.SnapshotAsync(fleetId);
            Assert.Equal(2, snapshot!.Members.Count);
            Assert.DoesNotContain(snapshot.Members, member => member.CharacterId == secondId);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
        }
    }

    [PgFact]
    public async Task Fleet_mass_invite_move_reconnect_and_finishmove_match_client_events()
    {
        await using var rig = await Rig.CreateAsync();
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var memberId = leaderId + 1;
        var leaderSession = new ClientSession(new Sink());
        var memberSession = new ClientSession(new Sink());
        leaderSession.Set("charid", new PyLong(leaderId));
        memberSession.Set("charid", new PyLong(memberId));
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leaderId);
        var firstWing = await store.CreateWingAsync(fleetId, leaderId);
        var firstSquad = await store.CreateSquadAsync(fleetId, leaderId, firstWing);
        var nextWing = await store.CreateWingAsync(fleetId, leaderId);
        var nextSquad = await store.CreateSquadAsync(fleetId, leaderId, nextWing);
        var fleet = new Eve.Services.Space.FleetObjectService(
            store, () => Rig.ClockMs,
            sessionForCharacter: id => id == leaderId ? leaderSession : id == memberId ? memberSession : null);
        try
        {
            var inviteResult = await FleetObjectCallAsync(fleet, fleetId, leaderSession, "MassInvite",
                [new PyList([new PyLong(memberId), new PyLong(leaderId)]),
                 new PyLong(firstWing), new PyLong(firstSquad), new PyInt(Eve.Services.Space.FleetStore.Member)]);
            Assert.Equal(new PyList([new PyLong(memberId)]), inviteResult);
            var inviteEvent = Assert.Single(Events.Of(memberSession));
            Assert.Equal("OnFleetInvite", inviteEvent.Name);
            Assert.Equal(new PyLong(leaderId), inviteEvent.Args[1]);

            Assert.Equal(PyBool.True, await FleetObjectCallAsync(
                fleet, fleetId, memberSession, "AcceptInvite", [new PyInt(603)]));
            Assert.Equal(firstWing, memberSession.GetInt("wingid"));
            Assert.Equal(firstSquad, memberSession.GetInt("squadid"));
            Events.Of(memberSession);
            Events.Of(leaderSession);

            await FleetObjectCallAsync(fleet, fleetId, leaderSession, "MoveMember",
                [new PyLong(memberId), new PyLong(nextWing), new PyLong(nextSquad),
                 new PyInt(Eve.Services.Space.FleetStore.Member)]);
            var movementEvents = Events.Of(memberSession)
                .Where(item => item.Name is "OnFleetMemberChanged" or "OnFleetMove")
                .Select(item => item.Name).ToArray();
            Assert.Equal(["OnFleetMemberChanged", "OnFleetMove"], movementEvents);
            Assert.Equal(firstWing, memberSession.GetInt("wingid"));
            Assert.Equal(firstSquad, memberSession.GetInt("squadid"));

            Assert.Equal(PyBool.True, await FleetObjectCallAsync(fleet, fleetId, memberSession, "FinishMove", []));
            Assert.Equal(nextWing, memberSession.GetInt("wingid"));
            Assert.Equal(nextSquad, memberSession.GetInt("squadid"));

            memberSession.Set("fleetid", PyNone.Instance);
            memberSession.Set("fleetrole", PyNone.Instance);
            memberSession.Set("wingid", PyNone.Instance);
            memberSession.Set("squadid", PyNone.Instance);
            Assert.Equal(PyBool.True, await FleetObjectCallAsync(fleet, fleetId, memberSession, "Reconnect", []));
            Assert.Equal(fleetId, memberSession.GetInt("fleetid"));
            Assert.Equal(nextWing, memberSession.GetInt("wingid"));
            Assert.Equal(nextSquad, memberSession.GetInt("squadid"));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
        }
    }

    [PgFact]
    public async Task Fleet_finder_joins_create_invites_or_approval_requests_with_client_notifications()
    {
        await using var rig = await Rig.CreateAsync();
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var leaderSession = new ClientSession(new Sink());
        leaderSession.Set("charid", new PyLong(leaderId));
        leaderSession.Set("corpid", new PyLong(980000123));
        leaderSession.Set("allianceid", new PyLong(990000123));
        leaderSession.Set("warfactionid", new PyLong(990000124));
        leaderSession.Set("securityStatus", new PyReal(0.75));
        leaderSession.Set("solarsystemid2", new PyLong(30000142));
        var approvedApplicantId = leaderId + 1;
        var openApplicantId = leaderId + 2;
        var approvedApplicant = new ClientSession(new Sink());
        var openApplicant = new ClientSession(new Sink());
        approvedApplicant.Set("charid", new PyLong(approvedApplicantId));
        approvedApplicant.Set("corpid", new PyLong(980000123));
        approvedApplicant.Set("allianceid", new PyLong(990000123));
        approvedApplicant.Set("warfactionid", new PyLong(990000124));
        approvedApplicant.Set("securityStatus", new PyReal(0.75));
        openApplicant.Set("charid", new PyLong(openApplicantId));
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leaderId);
        var wingId = await store.CreateWingAsync(fleetId, leaderId);
        var squadId = await store.CreateSquadAsync(fleetId, leaderId, wingId);
        ClientSession? SessionForCharacter(long id) => id switch
        {
            var value when value == leaderId => leaderSession,
            var value when value == approvedApplicantId => approvedApplicant,
            var value when value == openApplicantId => openApplicant,
            _ => null
        };
        Eve.Services.IService proxy = new Eve.Services.Space.FleetProxyService(
            store, store.FleetFinderAdvertOperationAsync, SessionForCharacter);
        var fleet = new Eve.Services.Space.FleetObjectService(
            store, () => Rig.ClockMs, sessionForCharacter: SessionForCharacter);
        var callId = 0;
        async Task<PyValue> ProxyCall(string method, ClientSession caller, params PyValue[] args) =>
            await proxy.CallAsync(new Eve.Services.ServiceCall
            {
                Method = method,
                Args = args,
                Session = caller,
                CallId = new PyInt(++callId)
            });
        async Task<PyValue> Apply(ClientSession applicant, bool autoAccept) =>
            await ProxyCall("ApplyToJoinFleet", applicant, new PyLong(fleetId),
                autoAccept ? PyBool.True : PyBool.False);
        PyValue Field(PyValue value, string name)
        {
            var dict = value is PyObject keyVal ? Assert.IsType<PyDict>(keyVal.Args) : Assert.IsType<PyDict>(value);
            return Assert.Single(dict.Entries, entry => JsSemantics.StringValue(entry.Key) == name).Value;
        }
        PyValue Advert(bool approval) => Py.KeyVal([
            ("fleetName", new PyWString("Fleet Finder test")),
            ("description", new PyWString("join flow")),
            ("inviteScope", new PyInt(16)),
            ("joinNeedsApproval", approval ? PyBool.True : PyBool.False),
            ("advertJoinLimit", new PyInt(10))
        ]);

        try
        {
            Assert.IsType<PyDict>(await ProxyCall("AddFleetFinderAdvert", leaderSession, Advert(approval: false)));
            Assert.Equal(PyBool.False, await Apply(openApplicant, autoAccept: true));
            var inviteEvent = Assert.Single(Events.Of(openApplicant));
            Assert.Equal("OnFleetInvite", inviteEvent.Name);
            Assert.Equal("clientID", inviteEvent.IdType);
            Assert.Equal(new PyLong(leaderId), inviteEvent.Args[1]);
            Assert.Equal(PyBool.True, Field(inviteEvent.Args[3], "autoAccept"));
            Assert.Null(await store.FleetIdForCharacterAsync(openApplicantId));

            Assert.Equal(PyBool.True, await FleetObjectCallAsync(fleet, fleetId, openApplicant,
                "AcceptInvite", [new PyInt(603)]));
            Assert.Equal(fleetId, await store.FleetIdForCharacterAsync(openApplicantId));
            Events.Of(leaderSession);

            await ProxyCall("RemoveFleetFinderAdvert", leaderSession);
            Assert.IsType<PyDict>(await ProxyCall("AddFleetFinderAdvert", leaderSession, Advert(approval: true)));
            Events.Of(leaderSession);

            Assert.Equal(PyBool.True, await Apply(approvedApplicant, autoAccept: false));
            var requestEvent = Assert.Single(Events.Of(leaderSession));
            Assert.Equal("OnFleetJoinRequest", requestEvent.Name);
            Assert.Equal("fleetid", requestEvent.IdType);
            Assert.Equal(new PyLong(approvedApplicantId), Field(requestEvent.Args[0], "charID"));
            Assert.Equal(new PyLong(980000123), Field(requestEvent.Args[0], "corpID"));
            Assert.Equal(new PyLong(990000123), Field(requestEvent.Args[0], "allianceID"));
            Assert.Equal(new PyLong(990000124), Field(requestEvent.Args[0], "warFactionID"));
            Assert.Equal(new PyReal(0.75), Field(requestEvent.Args[0], "securityStatus"));

            var requests = Assert.IsType<PyDict>(await FleetObjectCallAsync(
                fleet, fleetId, leaderSession, "GetJoinRequests", []));
            Assert.Contains(requests.Entries, entry => entry.Key == new PyLong(approvedApplicantId));

            var reloadedStore = new Eve.Services.Space.FleetStore(rig.Source, rig.FleetSchema);
            var persistedRequest = Assert.Single((await reloadedStore.SnapshotAsync(fleetId))!.JoinRequests);
            Assert.Equal(980000123, persistedRequest.CorporationId);
            Assert.Equal(990000123, persistedRequest.AllianceId);
            Assert.Equal(990000124, persistedRequest.WarFactionId);
            Assert.Equal(0.75, persistedRequest.SecurityStatus);
            var offlineFleet = new Eve.Services.Space.FleetObjectService(
                reloadedStore, () => Rig.ClockMs,
                sessionForCharacter: id => id == leaderId ? leaderSession : null);
            var offlineRequests = Assert.IsType<PyDict>(await FleetObjectCallAsync(
                offlineFleet, fleetId, leaderSession, "GetJoinRequests", []));
            var offlineRequest = Assert.Single(offlineRequests.Entries).Value;
            Assert.Equal(new PyLong(980000123), Field(offlineRequest, "corpID"));
            Assert.Equal(new PyReal(0.75), Field(offlineRequest, "securityStatus"));

            Assert.Equal(PyBool.True, await FleetObjectCallAsync(fleet, fleetId, leaderSession, "Invite",
                [new PyLong(approvedApplicantId), new PyLong(wingId), new PyLong(squadId),
                 new PyInt(Eve.Services.Space.FleetStore.Member)]));
            Assert.Empty((await store.SnapshotAsync(fleetId))!.JoinRequests);
            var requestUpdates = Events.Of(leaderSession).Where(item => item.Name == "OnJoinRequestUpdate").ToArray();
            Assert.Single(requestUpdates);
            Assert.Empty(Assert.IsType<PyDict>(requestUpdates[0].Args[0]).Entries);
            Assert.Equal("OnFleetInvite", Assert.Single(Events.Of(approvedApplicant)).Name);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
        }
    }

    [PgFact]
    public async Task Fleet_parallel_accepts_across_fleets_preserve_one_membership_without_throwing()
    {
        await using var rig = await Rig.CreateAsync();
        var store = rig.FleetStore;
        var leaderOne = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var leaderTwo = leaderOne + 10;
        var memberId = leaderOne + 20;
        var fleetOne = await store.CreateAsync(leaderOne);
        var fleetTwo = await store.CreateAsync(leaderTwo);
        try
        {
            Assert.True(await store.InviteAsync(fleetOne, leaderOne, memberId,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.Member));
            Assert.True(await store.InviteAsync(fleetTwo, leaderTwo, memberId,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.Member));

            var joins = await Task.WhenAll(store.JoinAsync(fleetOne, memberId), store.JoinAsync(fleetTwo, memberId));
            Assert.Single(joins, member => member is not null);
            var joinedFleet = await store.FleetIdForCharacterAsync(memberId);
            Assert.Contains(joinedFleet, new long?[] { fleetOne, fleetTwo });
            var snapshots = await Task.WhenAll(store.SnapshotAsync(fleetOne), store.SnapshotAsync(fleetTwo));
            Assert.Equal(1, snapshots.Sum(snapshot => snapshot!.Members.Count(member => member.CharacterId == memberId)));
            Assert.Null(await store.JoinAsync(joinedFleet == fleetOne ? fleetTwo : fleetOne, memberId));
            Assert.Equal(joinedFleet, await store.FleetIdForCharacterAsync(memberId));
        }
        finally
        {
            if (fleetOne > 0) await store.DisbandAsync(fleetOne, leaderOne);
            if (fleetTwo > 0) await store.DisbandAsync(fleetTwo, leaderTwo);
        }
    }

    [PgFact]
    public async Task Fleet_parallel_joins_do_not_exceed_the_member_limit()
    {
        await using var rig = await Rig.CreateAsync();
        var store = rig.FleetStore;
        var leaderId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var memberOne = leaderId + 1;
        var memberTwo = leaderId + 2;
        var fleetId = await store.CreateAsync(leaderId);
        try
        {
            Assert.True(await store.SetMaxMemberCountAsync(fleetId, leaderId, 2));
            Assert.True(await store.InviteAsync(fleetId, leaderId, memberOne,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.Member));
            Assert.True(await store.InviteAsync(fleetId, leaderId, memberTwo,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.Member));

            var joins = await Task.WhenAll(store.JoinAsync(fleetId, memberOne), store.JoinAsync(fleetId, memberTwo));
            Assert.Single(joins, member => member is not null);
            var snapshot = await store.SnapshotAsync(fleetId);
            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot.Members.Count);
            Assert.Equal(2, await store.MaxMemberCountAsync(fleetId));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leaderId);
        }
    }

    [PgFact]
    public async Task Fleet_invite_authorization_waits_for_a_manager_revocation()
    {
        await using var rig = await Rig.CreateAsync();
        var store = rig.FleetStore;
        var bossId = Random.Shared.NextInt64(8_000_000_000_000, 8_999_999_999_999);
        var commanderId = bossId + 1;
        var inviteeId = bossId + 2;
        var fleetId = await store.CreateAsync(bossId);
        try
        {
            Assert.True(await store.InviteAsync(fleetId, bossId, commanderId,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, commanderId));
            Assert.True(await store.MakeLeaderAsync(fleetId, bossId, commanderId));

            var invite = false;
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using (var revoke = connection.CreateCommand())
                {
                    revoke.Transaction = transaction;
                    revoke.CommandText = $"UPDATE {rig.FleetSchema}.fleet_members SET job = 0 WHERE fleet_id = $1 AND character_id = $2";
                    var fleetParameter = revoke.CreateParameter();
                    fleetParameter.Value = fleetId;
                    revoke.Parameters.Add(fleetParameter);
                    var bossParameter = revoke.CreateParameter();
                    bossParameter.Value = bossId;
                    revoke.Parameters.Add(bossParameter);
                    Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
                }

                var inviteTask = store.InviteAsync(fleetId, bossId, inviteeId,
                    Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.Member);
                await Task.Delay(250);
                var waitedForRevocation = !inviteTask.IsCompleted;
                await transaction.CommitAsync();
                invite = await inviteTask;
                Assert.True(waitedForRevocation, "invite authorization must wait for the manager row lock");
            }

            Assert.False(invite);
        }
        finally
        {
            if (fleetId > 0)
            {
                await using var connection = await rig.Source.OpenConnectionAsync();
                await using var restore = connection.CreateCommand();
                restore.CommandText = $"UPDATE {rig.FleetSchema}.fleet_members SET job = $1 WHERE fleet_id = $2 AND character_id = $3";
                var job = restore.CreateParameter();
                job.Value = Eve.Services.Space.FleetStore.CreatorJob;
                restore.Parameters.Add(job);
                var fleet = restore.CreateParameter();
                fleet.Value = fleetId;
                restore.Parameters.Add(fleet);
                var boss = restore.CreateParameter();
                boss.Value = bossId;
                restore.Parameters.Add(boss);
                await restore.ExecuteNonQueryAsync();
                await store.DisbandAsync(fleetId, bossId);
            }
        }
    }

[PgFact]
    public async Task Fleet_finder_advert_operations_match_evejs_arguments_and_persist_the_full_payload()
    {
        await using var rig = await Rig.CreateAsync();
        var leader = await rig.NewCharacterAsync("Fleet Finder Leader");
        var member = await rig.NewCharacterAsync("Fleet Finder Member");
        var leaderSession = await rig.SessionAsync(leader);
        var memberSession = await rig.SessionAsync(member);
        const long leaderCorporationId = 980000123;
        const long leaderAllianceId = 990000123;
        const long leaderWarFactionId = 990000124;
        leaderSession.Set("corpid", new PyLong(leaderCorporationId));
        leaderSession.Set("allianceid", new PyLong(leaderAllianceId));
        leaderSession.Set("warfactionid", new PyLong(leaderWarFactionId));
        leaderSession.Set("securityStatus", new PyReal(0.75));
        leaderSession.Set("solarsystemid2", new PyLong(30000142));
        memberSession.Set("corpid", new PyLong(leaderCorporationId));

        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        try
        {
            Assert.True(await store.InviteAsync(fleetId, leader.CharacterId, member.CharacterId,
                Eve.Services.Space.FleetStore.NoUnit, Eve.Services.Space.FleetStore.NoUnit,
                Eve.Services.Space.FleetStore.Member));
            Assert.NotNull(await store.JoinAsync(fleetId, member.CharacterId));
            Eve.Services.IService proxy = new Eve.Services.Space.FleetProxyService(
                store, store.FleetFinderAdvertOperationAsync, _ => leaderSession);

            async Task<PyValue> Call(string method, ClientSession caller, params PyValue[] args) =>
                await proxy.CallAsync(new Eve.Services.ServiceCall
                {
                    Method = method,
                    Args = args,
                    Session = caller,
                    CallId = new PyInt(1)
                });

            PyValue Field(PyValue advert, string name)
            {
                var fields = Assert.IsType<PyDict>(advert).Entries;
                return Assert.Single(fields, entry => entry.Key == new PyStr(name)).Value;
            }

            PyValue LeaderField(PyValue advert, string name)
            {
                var keyVal = Assert.IsType<PyObject>(Field(advert, "leader"));
                Assert.Equal(new PyStr("util.KeyVal"), keyVal.Name);
                var fields = Assert.IsType<PyDict>(keyVal.Args).Entries;
                return Assert.Single(fields, entry => entry.Key == new PyStr(name)).Value;
            }

            string[] expectedFields =
            [
                "fleetID", "leader", "solarSystemID", "numMembers", "advertTime", "dateCreated",
                "fleetName", "description", "inviteScope", "activityValue", "useAdvanceOptions",
                "newPlayerFriendly", "public_minStanding", "public_minSecurity", "public_allowedEntities",
                "public_disallowedEntities", "membergroups_minStanding", "membergroups_minSecurity",
                "membergroups_allowedEntities", "membergroups_disallowedEntities", "joinNeedsApproval",
                "hideInfo", "updateOnBossChange", "advertJoinLimit"
            ];

            Assert.Equal(PyNone.Instance, await Call("AddFleetFinderAdvert", memberSession, new PyDict([])));
            Assert.Equal(PyNone.Instance, await Call("UpdateFleetAdvertWithNewLeader", leaderSession, new PyDict([])));
            Assert.Equal(PyNone.Instance, await Call("RemoveFleetFinderAdvert", leaderSession));
            Assert.Equal(PyNone.Instance, await Call("UpdateAdvertInfo", leaderSession, new PyInt(1), new PyDict([])));
            Assert.Equal(PyNone.Instance, await Call("UpdateAdvertAllowedEntities", leaderSession, new PyDict([])));

            var input = Py.KeyVal([
                ("fleetName", new PyWString("Durable fleet")),
                ("description", new PyWString("Persisted description")),
                ("inviteScope", new PyInt(1)),
                ("activityValue", new PyInt(7)),
                ("useAdvanceOptions", PyBool.True),
                ("newPlayerFriendly", PyBool.True),
                ("public_minStanding", new PyReal(0.5)),
                ("public_minSecurity", new PyReal(-0.2)),
                ("public_allowedEntities", new PyList([new PyLong(leaderCorporationId)])),
                ("public_disallowedEntities", new PyList([new PyLong(980000124)])),
                ("membergroups_minStanding", new PyReal(1.25)),
                ("membergroups_minSecurity", new PyReal(0.1)),
                ("membergroups_allowedEntities", new PyList([new PyLong(980000125)])),
                ("membergroups_disallowedEntities", new PyList([new PyLong(980000126)])),
                ("joinNeedsApproval", PyBool.True),
                ("hideInfo", PyBool.False),
                ("updateOnBossChange", PyBool.False),
                ("advertJoinLimit", new PyInt(12))
            ]);

            var added = await Call("AddFleetFinderAdvert", leaderSession, input);
            Assert.IsType<PyDict>(added);
            Assert.Equal(expectedFields, Assert.IsType<PyDict>(added).Entries
                .Select(entry => ((PyStr)entry.Key).Value).ToArray());
            Assert.Equal(new PyLong(fleetId), Field(added, "fleetID"));
            Assert.Equal(new PyLong(leader.CharacterId), LeaderField(added, "charID"));
            Assert.Equal(new PyLong(leaderCorporationId), LeaderField(added, "corpID"));
            Assert.Equal(new PyLong(leaderAllianceId), LeaderField(added, "allianceID"));
            Assert.Equal(new PyLong(leaderWarFactionId), LeaderField(added, "warFactionID"));
            Assert.Equal(new PyReal(0.75), LeaderField(added, "securityStatus"));
            Assert.Equal(new PyLong(30000142), Field(added, "solarSystemID"));
            Assert.Equal(new PyInt(2), Field(added, "numMembers"));
            Assert.True(((PyLong)Field(added, "advertTime")).Value > 0);
            Assert.True(((PyLong)Field(added, "dateCreated")).Value > 0);
            Assert.Equal(new PyWString("Durable fleet"), Field(added, "fleetName"));
            Assert.Equal(new PyWString("Persisted description"), Field(added, "description"));
            Assert.Equal(new PyInt(1), Field(added, "inviteScope"));
            Assert.Equal(new PyInt(7), Field(added, "activityValue"));
            Assert.Equal(PyBool.True, Field(added, "useAdvanceOptions"));
            Assert.Equal(PyBool.True, Field(added, "newPlayerFriendly"));
            Assert.Equal(new PyReal(0.5), Field(added, "public_minStanding"));
            Assert.Equal(new PyReal(-0.2), Field(added, "public_minSecurity"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(leaderCorporationId)]), Field(added, "public_allowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000124)]), Field(added, "public_disallowedEntities"));
            Assert.Equal(new PyReal(1.25), Field(added, "membergroups_minStanding"));
            Assert.Equal(new PyReal(0.1), Field(added, "membergroups_minSecurity"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000125)]), Field(added, "membergroups_allowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000126)]), Field(added, "membergroups_disallowedEntities"));
            Assert.Equal(PyBool.True, Field(added, "joinNeedsApproval"));
            Assert.Equal(PyBool.False, Field(added, "hideInfo"));
            Assert.Equal(PyBool.False, Field(added, "updateOnBossChange"));
            Assert.Equal(new PyInt(12), Field(added, "advertJoinLimit"));

            var reloadedStore = rig.FleetStore;
            Eve.Services.IService reloadedProxy = new Eve.Services.Space.FleetProxyService(
                reloadedStore, reloadedStore.FleetFinderAdvertOperationAsync);
            var advert = await reloadedProxy.CallAsync(new Eve.Services.ServiceCall
            {
                Method = "GetMyFleetFinderAdvert",
                Args = [],
                Session = leaderSession,
                CallId = new PyInt(2)
            });
            Assert.Equal(added, advert);

            var available = Assert.IsType<PyDict>(await Call("GetAvailableFleetAds", memberSession));
            var advertised = Assert.Single(available.Entries);
            Assert.Equal(new PyLong(fleetId), advertised.Key);
            Assert.Equal(added, advertised.Value);
            memberSession.Set("corpid", new PyLong(leaderCorporationId + 1));
            Assert.Empty(Assert.IsType<PyDict>(await Call("GetAvailableFleetAds", memberSession)).Entries);
            memberSession.Set("corpid", new PyLong(leaderCorporationId));

            Assert.Equal(PyNone.Instance, await store.FleetFinderAdvertOperationAsync(
                "UpdateAdvertInfo", member.CharacterId,
                [new PyInt(2), Py.KeyVal([("publicToAddToAllowed", new PyList([new PyLong(leaderCorporationId + 1)]))])],
                memberSession));

            var infoUpdate = await Call("UpdateAdvertInfo", leaderSession, new PyInt(9), Py.KeyVal([
                ("publicToAddToAllowed", new PyList([new PyLong(leaderCorporationId + 1)])),
                ("publicToRemoveFromAllowed", new PyList([new PyLong(leaderCorporationId)])),
                ("publicToAddToDisallowed", new PyList([new PyLong(980000127)])),
                ("membergroupsToAddToAllowed", new PyList([new PyLong(980000128)])),
                ("membergroupsToRemoveFromDisallowed", new PyList([new PyLong(980000126)]))
            ]));
            Assert.Equal(new PyInt(9), Field(infoUpdate, "numMembers"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(leaderCorporationId + 1)]), Field(infoUpdate, "public_allowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000124), new PyLong(980000127)]), Field(infoUpdate, "public_disallowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000125), new PyLong(980000128)]), Field(infoUpdate, "membergroups_allowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([]), Field(infoUpdate, "membergroups_disallowedEntities"));

            var allowedUpdate = await Call("UpdateAdvertAllowedEntities", leaderSession, Py.KeyVal([
                ("public_allowedEntities", new PyList([new PyLong(980000129)])),
                ("public_disallowedEntities", new PyList([new PyLong(980000130)])),
                ("membergroups_allowedEntities", new PyList([new PyLong(980000131)])),
                ("membergroups_disallowedEntities", new PyList([new PyLong(980000132)]))
            ]));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000129)]), Field(allowedUpdate, "public_allowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000130)]), Field(allowedUpdate, "public_disallowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000131)]), Field(allowedUpdate, "membergroups_allowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000132)]), Field(allowedUpdate, "membergroups_disallowedEntities"));

            Assert.True(await store.MakeLeaderAsync(fleetId, leader.CharacterId, member.CharacterId));
            memberSession.Set("corpid", new PyLong(980000200));
            memberSession.Set("allianceid", new PyLong(990000200));
            memberSession.Set("warfactionid", new PyLong(990000201));
            memberSession.Set("securityStatus", new PyReal(1.5));
            memberSession.Set("solarsystemid2", new PyLong(30000143));
            var leaderUpdate = await Call("UpdateFleetAdvertWithNewLeader", memberSession, Py.KeyVal([
                ("public_allowedEntities", new PyList([new PyLong(980000133)])),
                ("membergroups_allowedEntities", new PyList([new PyLong(980000134)]))
            ]));
            Assert.Equal(new PyLong(member.CharacterId), LeaderField(leaderUpdate, "charID"));
            Assert.Equal(new PyLong(980000200), LeaderField(leaderUpdate, "corpID"));
            Assert.Equal(new PyLong(990000200), LeaderField(leaderUpdate, "allianceID"));
            Assert.Equal(new PyLong(990000201), LeaderField(leaderUpdate, "warFactionID"));
            Assert.Equal(new PyReal(1.5), LeaderField(leaderUpdate, "securityStatus"));
            Assert.Equal(new PyLong(30000143), Field(leaderUpdate, "solarSystemID"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000133)]), Field(leaderUpdate, "public_allowedEntities"));
            Assert.Equal(Eve.Services.InventoryWire.Set([new PyLong(980000134)]), Field(leaderUpdate, "membergroups_allowedEntities"));
            Assert.Equal(Eve.Services.Space.FleetStore.Leader,
                (await store.MemberForCharacterAsync(member.CharacterId))?.Role);

            var removed = await Call("RemoveFleetFinderAdvert", memberSession);
            Assert.Equal(leaderUpdate, removed);
            Assert.Equal(PyNone.Instance, await Call("UpdateFleetAdvertWithNewLeader", memberSession, new PyDict([])));
            Assert.Equal(PyNone.Instance, await Call("RemoveFleetFinderAdvert", memberSession));
            Assert.Equal(PyNone.Instance, await Call("UpdateAdvertInfo", memberSession, new PyInt(1), new PyDict([])));
            Assert.Equal(PyNone.Instance, await Call("UpdateAdvertAllowedEntities", memberSession, new PyDict([])));
            Assert.Empty(Assert.IsType<PyDict>(await Call("GetAvailableFleetAds", leaderSession)).Entries);
            Assert.Equal(PyNone.Instance, await Call("GetMyFleetFinderAdvert", memberSession));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

    [PgFact]
    public async Task Fleet_object_init_advert_callback_persists_through_fleet_store()
    {
        await using var rig = await Rig.CreateAsync();
        var leader = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(leader);
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        PyValue Field(PyValue value, string name)
        {
            var entries = Assert.IsType<PyDict>(value).Entries;
            return Assert.Single(entries, entry => entry.Key == new PyStr(name)).Value;
        }
        try
        {
            var advertData = Py.KeyVal([
                ("fleetName", new PyWString("Init advert")),
                ("description", new PyWString("Saved through FleetStore"))
            ]);
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("adInfoData"), advertData)
            ]);
            var fleet = new Eve.Services.Space.FleetObjectService(
                store, () => Rig.ClockMs,
                createFleetAdvert: (id, characterId, data, callbackSession, cancellationToken) =>
                    store.FleetFinderAdvertOperationAsync(
                        "AddFleetFinderAdvert", characterId, [data], callbackSession, cancellationToken));

            var initReply = await FleetObjectCallAsync(
                fleet, fleetId, session, "Init", [new PyInt(0), PyNone.Instance], kwargs);
            Assert.IsType<PyDict>(initReply);

            Eve.Services.IService proxy = new Eve.Services.Space.FleetProxyService(
                store, store.FleetFinderAdvertOperationAsync);
            var advert = await proxy.CallAsync(new Eve.Services.ServiceCall
            {
                Method = "GetMyFleetFinderAdvert",
                Args = [],
                Session = session,
                CallId = new PyInt(2)
            });
            Assert.IsType<PyDict>(advert);
            Assert.Equal(Field(initReply, "fleetName"), Field(advert, "fleetName"));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

    [PgFact]
    public async Task Fleet_store_applies_saved_character_setup_atomically()
    {
        await using var rig = await Rig.CreateAsync();
        var leader = await rig.NewCharacterAsync("Fleet Setup Leader");
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        try
        {
            var setup = System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "wingsInfo": {
                    "first": { "wingName": "Alpha", "wingIdx": 0, "squadNames": ["Scout", "Heavy"] }
                  },
                  "defaultSquad": ["Alpha", 1],
                  "motd": "Fleet setup loaded",
                  "isFreeMove": true,
                  "maxFleetSize": 50
                }
                """)!.AsObject();

            Assert.True(await store.ApplyFleetSetupAsync(fleetId, leader.CharacterId, setup));
            var snapshot = await store.SnapshotAsync(fleetId);
            var wing = Assert.Single(snapshot!.Wings);
            Assert.Equal("Alpha", wing.Name);
            Assert.Equal(["Scout", "Heavy"], wing.Squads.Select(squad => squad.Name));
            Assert.Equal(wing.Squads[1].SquadId, snapshot.AutoJoinSquadId);
            Assert.Equal("Fleet setup loaded", snapshot.Motd);
            Assert.True(snapshot.IsFreeMove);
            Assert.Equal(50, snapshot.MaxMemberCount);

            var invalidSetup = System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "wingsInfo": {
                    "first": { "wingName": "Must rollback", "wingIdx": 0, "squadNames": ["Changed"] }
                  },
                  "motd": "Must roll back",
                  "maxFleetSize": 999
                }
                """)!.AsObject();
            Assert.False(await store.ApplyFleetSetupAsync(fleetId, leader.CharacterId, invalidSetup));
            var afterRollback = await store.SnapshotAsync(fleetId);
            Assert.Equal("Alpha", Assert.Single(afterRollback!.Wings).Name);
            Assert.Equal("Fleet setup loaded", afterRollback.Motd);
            Assert.Equal(50, afterRollback.MaxMemberCount);
            Assert.Equal(["Scout", "Heavy"], afterRollback.Wings[0].Squads.Select(squad => squad.Name));
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

    [PgFact]
    public async Task Fleet_setup_and_advert_rpcs_forward_client_data_to_host_callbacks()
    {
        await using var rig = await Rig.CreateAsync();
        var leader = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(leader);
        var store = rig.FleetStore;
        var fleetId = await store.CreateAsync(leader.CharacterId);
        var setupCalls = new List<(long FleetId, long CharacterId, string Name, ClientSession Session)>();
        var createAdvertCalls = new List<(long FleetId, long CharacterId, PyValue Data, ClientSession Session)>();

        Task<PyValue?> LoadSetup(long id, long characterId, string name, ClientSession callbackSession, CancellationToken cancellationToken)
        {
            setupCalls.Add((id, characterId, name, callbackSession));
            return Task.FromResult<PyValue?>(null);
        }

        Task<PyValue?> CreateAdvert(long id, long characterId, PyValue data, ClientSession callbackSession, CancellationToken cancellationToken)
        {
            createAdvertCalls.Add((id, characterId, data, callbackSession));
            return Task.FromResult<PyValue?>(new PyStr("advert-persisted"));
        }

        var fleet = new Eve.Services.Space.FleetObjectService(
            store, () => Rig.ClockMs, loadFleetSetup: LoadSetup, createFleetAdvert: CreateAdvert);
        var advertData = new PyDict([]);
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("adInfoData"), advertData)
        ]);

        try
        {
            Assert.Equal(new PyStr("advert-persisted"), await FleetObjectCallAsync(
                fleet, fleetId, session, "Init", [new PyInt(1234), new PyStr("Saved setup")], kwargs));
            var initSetup = Assert.Single(setupCalls);
            Assert.Equal((fleetId, leader.CharacterId, "Saved setup"), (initSetup.FleetId, initSetup.CharacterId, initSetup.Name));
            Assert.Same(session, initSetup.Session);
            var advert = Assert.Single(createAdvertCalls);
            Assert.Equal((fleetId, leader.CharacterId), (advert.FleetId, advert.CharacterId));
            Assert.Same(advertData, advert.Data);
            Assert.Same(session, advert.Session);

            Assert.Equal(PyNone.Instance, await FleetObjectCallAsync(
                fleet, fleetId, session, "LoadFleetSetup", [new PyStr("Second setup")]));
            Assert.Equal(["Saved setup", "Second setup"], setupCalls.Select(call => call.Name));

            var finderCalls = new List<(string Operation, long CharacterId, PyValue[] Args, ClientSession Session)>();
            Task<PyValue?> FleetFinder(string operation, long characterId, IReadOnlyList<PyValue> args,
                ClientSession callbackSession, CancellationToken cancellationToken)
            {
                finderCalls.Add((operation, characterId, [.. args], callbackSession));
                return Task.FromResult<PyValue?>(operation == "GetAvailableFleetAds"
                    ? new PyDict([])
                    : new PyStr(operation));
            }

            Eve.Services.IService proxy = new Eve.Services.Space.FleetProxyService(
                store, FleetFinder, _ => session);
            (string Method, PyValue[] Args)[] requests =
            [
                ("GetAvailableFleetAds", []),
                ("AddFleetFinderAdvert", [advertData]),
                ("UpdateFleetAdvertWithNewLeader", [new PyLong(leader.CharacterId + 1)]),
                ("RemoveFleetFinderAdvert", []),
                ("GetMyFleetFinderAdvert", []),
                ("UpdateAdvertInfo", [new PyStr("Fleet name"), new PyStr("Fleet description")]),
                ("UpdateAdvertAllowedEntities", [new PyList([new PyLong(leader.CharacterId + 1)])])
            ];

            for (var i = 0; i < requests.Length; i++)
            {
                var (method, args) = requests[i];
                var reply = await proxy.CallAsync(new Eve.Services.ServiceCall
                {
                    Method = method,
                    Args = args,
                    Session = session,
                    CallId = new PyInt(1)
                });
                if (method == "GetAvailableFleetAds")
                    Assert.IsType<PyDict>(reply);
                else
                    Assert.Equal(new PyStr(method), reply);

                Assert.Equal(method, finderCalls[i].Operation);
                Assert.Equal(leader.CharacterId, finderCalls[i].CharacterId);
                Assert.Equal(args, finderCalls[i].Args);
                Assert.Same(session, finderCalls[i].Session);
            }
            Assert.Equal(requests.Length, finderCalls.Count);
        }
        finally
        {
            if (fleetId > 0) await store.DisbandAsync(fleetId, leader.CharacterId);
        }
    }

    private static async Task<PyValue> FleetObjectCallAsync(
        Eve.Services.Space.FleetObjectService service,
        long fleetId,
        ClientSession session,
        string method,
        IReadOnlyList<PyValue> args,
        PyValue? kwargs = null)
    {
        var nestedCall = Eve.Services.Py.Tuple(
            new PyStr(method),
            new PyTuple(args),
            kwargs ?? PyNone.Instance);
        var reply = Assert.IsType<PyTuple>(await ((Eve.Services.IService)service).CallAsync(
            new Eve.Services.ServiceCall
            {
                Method = "MachoBindObject",
                Args = [new PyLong(fleetId), nestedCall],
                Session = session,
                CallId = new PyInt(1)
            }));
        return reply.Items[1];
    }


}
