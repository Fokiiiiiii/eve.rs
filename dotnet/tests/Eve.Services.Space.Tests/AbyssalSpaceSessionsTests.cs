using Eve.Content;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Npc;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class AbyssalSpaceSessionsTests
{
    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync(Eve.Scheduler.LocalTimers? localTimers = null)
    {
        var rig = await Rig.CreateAsync(localTimers: localTimers);
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new Eve.Marshal.PyLong(session.GetInt("shipid")!.Value), Eve.Marshal.PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (rig, character, session);
    }

    [PgFact]
    public async Task Jettison_room_transition_after_atomic_can_creation_compensates_only_the_empty_can_before_cargo_moves()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.NewCharacterAsync();
        var cargo = (await rig.Ships.GrantAsync(created.CharacterId, 34, 100, singleton: false))!.Single().After;
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(created.CharacterId, cargo.Id,
            Rig.Station, created.ShipId, Eve.Ships.CharacterShips.CargoFlag, null));
        var session = await rig.SessionAsync(created);
        var dockedBinding = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", dockedBinding, new Eve.Marshal.PyLong(created.ShipId), Eve.Marshal.PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var contentId = Guid.NewGuid().ToString("D");
        var origin = SimSceneKey.SolarSystem(checked((int)session.GetInt("solarsystemid2")!.Value));
        var first = new SimSceneKey(origin.LocationId, 887, 1);
        var second = first with { RoomIndex = 2 };
        foreach (var scene in new[] { first, second }) Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, created.ShipId, origin, first, [0, 0, 0], [0, 0, 1]));
        long failedCan = 0;
        rig.Sim.PlaceContainerHandler = async request =>
        {
            failedCan = request.ContainerId;
            var can = (await rig.Items.GetAsync(request.ContainerId))!;
            var scope = Assert.IsType<Eve.Inventory.RunSpaceItemProvenance>(can.RunSpaceProvenance).Scope;
            Assert.Equal((Guid.Parse(contentId), first.InstanceId, first.RoomIndex, first.LocationId, created.CharacterId),
                (scope.ContentId, scope.InstanceId, scope.RoomOrdinal, scope.SpaceLocationId, scope.RecipientCharacterId));
            Assert.Null(scope.SettlementKey);
            Assert.Equal(first, request.ExpectedScene);
            Assert.Equal(created.ShipId, (await rig.Items.GetAsync(cargo.Id))!.LocationId);
            Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, created.ShipId, first, second));
            return new CommandReply(false, 0, "SCENE_CHANGED");
        };
        var shipBinding = await rig.BindShipInSpaceAsync(session);
        var result = await rig.ShipCallAsync(session, "Jettison", shipBinding,
            new Eve.Marshal.PyList([new Eve.Marshal.PyLong(cargo.Id)]));
        Assert.Empty(((Eve.Marshal.PyTuple)((Eve.Marshal.PyTuple)result).Items[0]).Items);
        Assert.True(failedCan > 0);
        Assert.Null(await rig.Items.GetAsync(failedCan));
        var retained = (await rig.Items.GetAsync(cargo.Id))!;
        Assert.Equal((created.ShipId, 100L), (retained.LocationId, retained.Quantity));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimChangeWreck>(), change => change.WreckId == failedCan);
        Assert.Equal(second.RoomIndex, rig.Space.RunSpaceScopeOf(session)!.RoomOrdinal);
        rig.Sim.PlaceContainerHandler = null;
        var retry = await rig.ShipCallAsync(session, "Jettison", shipBinding,
            new Eve.Marshal.PyList([new Eve.Marshal.PyLong(cargo.Id)]));
        Assert.Single(((Eve.Marshal.PyTuple)((Eve.Marshal.PyTuple)retry).Items[0]).Items);
        var accepted = rig.Sim.Heard.OfType<SimPlaceContainer>().Last();
        Assert.Equal(second, accepted.ExpectedScene);
        var acceptedCan = (await rig.Items.GetAsync(accepted.ContainerId))!;
        Assert.Equal(second.RoomIndex, acceptedCan.RunSpaceProvenance!.Scope.RoomOrdinal);
        Assert.Equal(acceptedCan.Id, (await rig.Items.GetAsync(cargo.Id))!.LocationId);
        rig.Sim.Lose();
        var beforeRestore = rig.Sim.Heard.Count;
        await rig.Containers.RestoreAsync(second.LocationId);
        Assert.DoesNotContain(rig.Sim.Heard.Skip(beforeRestore).OfType<SimPlaceWreck>(), placement => placement.WreckId == acceptedCan.Id);
        Assert.NotNull(await rig.Items.GetAsync(acceptedCan.Id));

    }

    [PgFact]
    public async Task Private_warp_ship_swap_and_occupied_teardown_are_rejected_before_mutation_then_exact_scene_registries_are_freed()
    {
        var (rig, created, session) = await InSpaceAsync();
        await using var rigScope = rig;
        var contentId = Guid.NewGuid().ToString("D");
        var origin = SimSceneKey.SolarSystem(checked((int)session.GetInt("solarsystemid2")!.Value));
        var scene = new SimSceneKey(origin.LocationId, 889, 1);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, created.ShipId, origin, scene, [0, 0, 0], [0, 0, 1]));
        var before = rig.Sim.Heard.Count;
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.WarpAsync(session, 40_000_001, 0, default));
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.WarpToPointAsync(session, 1_000_000, 0, 0, 0, default));
        await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.Space.EjectAsync(session, default));
        await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.Space.BoardAsync(session, created.ShipId + 1, created.ShipId, default));
        Assert.Equal("ABYSSAL_SCENE_NOT_EMPTY", await rig.Space.RemoveAbyssalSceneAsync(scene, [8_889_010], [8_889_011]));
        Assert.Equal(before, rig.Sim.Heard.Count);
        foreach (var typeId in new[] { 57_319, Eve.Services.Space.Beacons.AnalysisBeaconRules.StandardType, 58_904, 12_235 })
        {
            var deployable = (await rig.Ships.GrantAsync(created.CharacterId, typeId, 1, singleton: true))!.Single().After;
            await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, deployable.Id, default));
            Assert.Equal(deployable.LocationId, (await rig.Items.GetAsync(deployable.Id))!.LocationId);
        }
        Assert.Equal(before, rig.Sim.Heard.Count);
        Assert.Equal(created.ShipId, (await rig.Ships.CharacterAsync(created.CharacterId))!.ShipId);
        Assert.Null(await rig.Space.EnsureAbyssalEnvironmentObjectAsync(contentId, scene, 8_889_001, 47_440,
            created.CharacterId, [0, 0, 0], [0, 0, 1]));
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, created.ShipId, scene, origin, [1_000, 0, 0], [0, 0, 1]));
        Assert.Null(await rig.Space.RemoveAbyssalSceneAsync(scene, [8_889_010], [8_889_011]));
        var removed = rig.Sim.Heard.OfType<SimRemoveAbyssalScene>().Last();
        Assert.Equal(new long[] { 8_889_010 }, removed.SettledNpcIds);
        Assert.Equal(new long[] { 8_889_011 }, removed.SettledLootIds);
        Assert.Null(await rig.Space.RemoveAbyssalSceneAsync(scene, [8_889_010], [8_889_011]));
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        // A changed pose for the same source can register only if the old exact-scene registry was freed.
        Assert.Null(await rig.Space.EnsureAbyssalEnvironmentObjectAsync(contentId, scene, 8_889_001, 47_440,
            created.CharacterId, [10_000, 0, 0], [0, 0, 1]));
    }

    [PgFact]
    public async Task Presentation_only_source_radius_is_validated_and_kept_separate_from_local_mechanics_and_boundary()
    {
        await using var rig = await Rig.CreateAsync();
        var scene = new SimSceneKey(32_000_010, 893, 1);
        var contentId = Guid.NewGuid().ToString("D");
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        foreach (var entry in new[] { (TypeId: 47_465, Radius: 75_000.0), (TypeId: 54_196, Radius: 7_500.0) })
        {
            Assert.Null(await rig.Space.EnsureAbyssalEnvironmentObjectAsync(contentId, scene,
                8_893_000L + entry.TypeId, entry.TypeId, 140_000_342, [0, 0, 0], [0, 0, 1],
                presentationRadiusMeters: entry.Radius));
            var request = rig.Sim.Heard.OfType<SimEnsureAbyssalEnvironmentObject>().Last();
            var decoded = Assert.IsType<SimEnsureAbyssalEnvironmentObject>(SimCodec.Decode(SimCodec.Encode(request).AsMemory(4)));
            Assert.Equal(entry.Radius, decoded.Object.Body.Radius);
            Assert.Equal(0, decoded.Object.EffectRadiusMeters);
            Assert.Null(decoded.Object.PointDefense);
        }
        var requestsBeforeInvalid = rig.Sim.Heard.OfType<SimEnsureAbyssalEnvironmentObject>().Count();
        foreach (var invalid in new[] { -1.0, double.NaN, double.PositiveInfinity })
            Assert.Equal("BAD_ABYSSAL_ENVIRONMENT_OBJECT", await rig.Space.EnsureAbyssalEnvironmentObjectAsync(
                contentId, scene, 8_893_999, 47_465, 140_000_342, [0, 0, 0], [0, 0, 1], presentationRadiusMeters: invalid));
        Assert.Equal(requestsBeforeInvalid, rig.Sim.Heard.OfType<SimEnsureAbyssalEnvironmentObject>().Count());
        Assert.Equal(SceneSpec(scene).Boundary.RadiusMeters, rig.Sim.Heard.OfType<SimEnsureAbyssalScene>().Single().Spec.Boundary.RadiusMeters);
    }

    [PgFact]
    public async Task Local_cloud_sources_apply_once_to_ships_and_NPCs_exit_cleanly_and_ignore_same_type_decor()
    {
        var (rig, created, session) = await InSpaceAsync();
        await using var rigScope = rig;
        var catalog = await AbyssalStaticCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var mechanics = catalog.EnvironmentalTypesById[47_440];
        var signature = Assert.Single(mechanics.Buffs, buff => buff.Modifiers.Any(modifier => modifier.Scope == "itemModifiers" && modifier.AttributeId == 552));
        var factor = 1 + signature.Value / 100;
        var baseline = (await rig.Ships.LoadAsync(created.CharacterId))!.World.Get(created.ShipId, 552);
        var contentId = Guid.NewGuid().ToString("D");
        var origin = SimSceneKey.SolarSystem(checked((int)session.GetInt("solarsystemid2")!.Value));
        var scene = new SimSceneKey(origin.LocationId, 892, 1);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, created.ShipId, origin, scene, [0, 0, 0], [0, 0, 1]));
        foreach (var source in new[] { 8_892_001L, 8_892_002L, 8_892_003L })
            Assert.Null(await rig.Space.EnsureAbyssalEnvironmentObjectAsync(contentId, scene, source, 47_440,
                created.CharacterId, [0, 0, 0], [0, 0, 1], source == 8_892_001 ? null : mechanics));
        var decorations = rig.Sim.Heard.OfType<SimEnsureAbyssalEnvironmentObject>().Single(request => request.Object.Body.ItemId == 8_892_001);
        Assert.Equal(0, decorations.Object.EffectRadiusMeters);
        Assert.Null(decorations.Object.PointDefense);
        var sid = (ulong)session.SessionId;
        double ShipSignature() => rig.Sim.Heard.OfType<SimShipChanged>().Last(message => message.Ship.ShipId == created.ShipId).Ship.SignatureRadius;
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_001, created.ShipId, sid, true));
        Assert.Equal(baseline, ShipSignature(), 8);
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_002, created.ShipId, sid, true));
        Assert.Equal(baseline * factor, ShipSignature(), 8);
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_003, created.ShipId, sid, true));
        Assert.Equal(baseline * factor, ShipSignature(), 8);
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_002, created.ShipId, sid, false));
        Assert.Equal(baseline * factor, ShipSignature(), 8);
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_003, created.ShipId, sid, false));
        Assert.Equal(baseline, ShipSignature(), 8);

        const long npcId = 8_892_010;
        var spawned = await rig.Space.SpawnAbyssalNpcAsync(contentId, scene, npcId, 23_245, [0, 0, 0], [0, 0, 1]);
        Assert.Null(spawned.Error);
        var original = rig.Sim.Heard.OfType<SimSpawn>().Single(spawn => spawn.Ship.ShipId == npcId);
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_002, npcId, spawned.Session!.Value, true));
        var refreshed = rig.Sim.Heard.OfType<SimRefreshAbyssalNpc>().Last();
        Assert.Equal(original.Ship.SignatureRadius * factor, refreshed.Ship.SignatureRadius, 8);
        var tracking = catalog.EnvironmentalTypesById[47_469];
        Assert.Null(await rig.Space.EnsureAbyssalEnvironmentObjectAsync(contentId, scene, 8_892_004, 47_469,
            created.CharacterId, [0, 0, 0], [0, 0, 1], tracking));
        var trackingBuff = Assert.Single(tracking.Buffs, buff => buff.Modifiers.Any(modifier => modifier.AttributeId == 160));
        var originalTracking = Assert.Single(original.Identity.Weapons).TrackingSpeed;
        Assert.True(originalTracking > 0);
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_004, npcId, spawned.Session.Value, true));
        Assert.Equal(originalTracking * (1 + trackingBuff.Value / 100), rig.Sim.Heard.OfType<SimRefreshAbyssalNpc>().Last().Identity.Weapons.Single().TrackingSpeed, 8);
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_004, npcId, spawned.Session.Value, false));
        Assert.Equal(originalTracking, rig.Sim.Heard.OfType<SimRefreshAbyssalNpc>().Last().Identity.Weapons.Single().TrackingSpeed, 8);
        await rig.Space.ApplyAbyssalFieldMembershipAsync(new(scene, 8_892_002, npcId, spawned.Session.Value, false));
        Assert.Equal(original.Ship.SignatureRadius, rig.Sim.Heard.OfType<SimRefreshAbyssalNpc>().Last().Ship.SignatureRadius, 8);
    }

    [PgFact]
    public async Task Hidden_system_entry_and_relogin_set_both_system_ids_and_allow_real_ballpark_bind()
    {
        var (rig, created, session) = await InSpaceAsync();
        await using var rigScope = rig;
        var contentId = Guid.NewGuid().ToString("D");
        var source = SimSceneKey.SolarSystem(checked((int)session.GetInt("solarsystemid2")!.Value));
        var scene = new SimSceneKey(32_000_010, 890, 1);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, created.ShipId, source, scene, [100, 200, 300], [0, 0, 1]));
        Assert.Equal((long?)scene.LocationId, session.GetInt("solarsystemid"));
        Assert.Equal((long?)scene.LocationId, session.GetInt("solarsystemid2"));
        Assert.False(string.IsNullOrWhiteSpace(await rig.BindBeyonceAsync(session)));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Space.AbyssalParticipantDisconnectedAsync = (_, _) =>
        {
            disconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };
        session.Close();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var persisted = (await rig.Characters.GetAsync(created.CharacterId))!;
        var lastStationId = persisted.StationId;
        var sourceSystemId = persisted.SolarSystemId;
        var reconnectLookups = 0;
        var enteredSystems = new List<int>();
        rig.Space.SystemEntered += enteredSystems.Add;
        rig.Space.FindAbyssalReconnectAsync = (_, _, _, _) =>
        {
            reconnectLookups++;
            return Task.FromResult<AbyssalReconnectBinding?>(new(contentId, scene));
        };

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, created.ShipId, lastStationId, Eve.Inventory.ItemFlags.Hangar);
            await transaction.CommitAsync();
        }
        var docked = await rig.SessionAsync(created, 2);
        var dockedAttributes = await rig.Space.GetSessionAttributesAsync(docked, created.CharacterId, CancellationToken.None);
        docked.Change([..dockedAttributes]);
        await rig.Space.OnCharacterEnteredAsync(docked, created.CharacterId, CancellationToken.None);
        Assert.Equal((long?)lastStationId, docked.GetInt("stationid"));
        Assert.Equal((long?)lastStationId, docked.GetInt("locationid"));
        var stillDockedShip = (await rig.Items.GetAsync(created.ShipId))!;
        Assert.Equal(lastStationId, stillDockedShip.LocationId);
        Assert.Equal(Eve.Inventory.ItemFlags.Hangar, stillDockedShip.Flag);
        Assert.Equal(0, reconnectLookups);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, created.ShipId, sourceSystemId, Eve.Ships.CharacterShips.InSpaceFlag);
            await transaction.CommitAsync();
        }
        var reconnected = await rig.SessionAsync(created, 3);
        var attributes = await rig.Space.GetSessionAttributesAsync(reconnected, created.CharacterId, CancellationToken.None);
        reconnected.Change([..attributes]);
        Assert.Null(reconnected.GetInt("stationid"));
        Assert.Null(reconnected.GetInt("stationid2"));
        Assert.Null(reconnected.GetInt("structureid"));
        var restoredShip = (await rig.Items.GetAsync(created.ShipId))!;
        Assert.Equal((long)sourceSystemId, restoredShip.LocationId);
        Assert.Equal(Eve.Ships.CharacterShips.InSpaceFlag, restoredShip.Flag);
        Assert.Equal((long?)scene.LocationId, reconnected.GetInt("solarsystemid"));
        Assert.Equal((long?)scene.LocationId, reconnected.GetInt("solarsystemid2"));
        Assert.Equal((long?)scene.LocationId, reconnected.GetInt("locationid"));
        await rig.Space.OnCharacterEnteredAsync(reconnected, created.CharacterId, CancellationToken.None);
        Assert.Empty(enteredSystems);
        Assert.True(reconnectLookups >= 2);
        Assert.Equal((long?)scene.LocationId, reconnected.GetInt("solarsystemid"));
        Assert.Equal((long?)scene.LocationId, reconnected.GetInt("solarsystemid2"));
        Assert.False(string.IsNullOrWhiteSpace(await rig.BindBeyonceAsync(reconnected)));
    }

    [PgFact]
    public async Task Reward_cache_uses_SDE_health_and_reports_confirmed_destruction_without_NPC_rewards()
    {
        await using var rig = await Rig.CreateAsync();
        var scene = new SimSceneKey(30_100_032, 888, 1);
        var contentId = Guid.NewGuid().ToString("D");
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        foreach (var typeId in new[] { 47_951, 49_689, 49_661, 49_662 })
        {
            var itemId = 8_880_000L + typeId;
            Assert.Null(await rig.Space.EnsureAbyssalRewardCacheAsync(contentId, scene, itemId, typeId,
                140_000_342, [100, 200, 300], [0, 0, 1]));
            var cache = rig.Sim.Heard.OfType<SimEnsureAbyssalRewardCache>().Last();
            Assert.Equal(2_009, cache.Entity.GroupId);
            Assert.Equal(11, cache.Entity.CategoryId);
            Assert.Equal(0, cache.Entity.CharacterId);
            Assert.Equal(0, cache.Entity.MaxVelocity);
            Assert.True(cache.Entity.Vitals!.StructureHp > 0);
            Assert.Empty(cache.Entity.Modules);
            var type = rig.Statics.Type(typeId)!;
            Assert.Equal(type.Attributes[9], cache.Entity.Vitals.StructureHp);
            var completed = new TaskCompletionSource<AbyssalRewardCacheDestroyedNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Space.AbyssalRewardCacheDestroyedAsync = (notice, _) =>
            {
                completed.TrySetResult(notice);
                return ValueTask.CompletedTask;
            };
            rig.Sim.Deliver(new SimDestroyed((1UL << 61) + 1, itemId, 0,
                [100, 200, 300], [0, 0, 1], cache.Entity.Radius, 8_001, ShipTypeId: typeId));
            var notice = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((contentId, scene.InstanceId, scene.RoomIndex, itemId, typeId, 8_001L),
                (notice.ContentId, notice.InstanceId, notice.RoomOrdinal, notice.CacheItemId, notice.TypeId, notice.KillerShipId));
            Assert.Equal([100.0, 200.0, 300.0], notice.Position);
        }
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceWreck>());
    }

    [PgFact]
    public async Task Domain_owned_loot_uses_confirmed_Sim_upsert_and_rejects_an_unensured_room()
    {
        await using var rig = await Rig.CreateAsync();
        var scene = new SimSceneKey(30_100_032, 891, 1);
        var contentId = Guid.NewGuid().ToString("D");
        var container = new SimPlaceWreck(scene, 8_891_001, 12,
            new SimWreckKind(23, "Cargo Container", 1_176), [100, 200, 300], [0, 0, 1], 14,
            8_891_000, new SimCredit(140_000_342, 0, 0, 0), false);
        Assert.Equal("BAD_ABYSSAL_LOOT_CONTAINER", await rig.Space.EnsureAbyssalLootContainerAsync(contentId, container));
        Assert.Empty(rig.Sim.Heard.OfType<SimEnsureAbyssalLootContainer>());
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        Assert.Null(await rig.Space.EnsureAbyssalLootContainerAsync(contentId, container));
        Assert.Null(await rig.Space.EnsureAbyssalLootContainerAsync(contentId, container));
        var requests = rig.Sim.Heard.OfType<SimEnsureAbyssalLootContainer>().ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.Equal(contentId, request.ContentId);
            Assert.Equal(container, request.Container);
        });
        Assert.NotEqual(requests[0].RequestId, requests[1].RequestId);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceWreck>());
    }

    [PgFact]
    public async Task Administrative_detach_after_both_restarts_accepts_confirmed_absence_and_rejects_owner_mismatch()
    {
        var (rig, created, _) = await InSpaceAsync();
        await using var rigScope = rig;
        var character = (await rig.Characters.GetAsync(created.CharacterId))!;
        var ship = (await rig.Items.GetAsync(character.ShipId!.Value))!;
        var scene = new SimSceneKey(character.SolarSystemId, 889, 1);
        var contentId = Guid.NewGuid().ToString("D");
        var sim = new FakeSim { RemoveAbyssalParticipantAnswer = request => new(request.RequestId, true, null) };
        using var freshSpace = new SpaceSessions(sim, rig.Ships, rig.Statics, rig.Stations, rig.Systems, () => rig.Now, rig.Log.Add);
        var bad = await freshSpace.DetachAbyssalParticipantAsync(contentId, scene, character, ship with { OwnerId = character.Id + 1 });
        Assert.False(bad.Succeeded);
        Assert.Empty(sim.Heard.OfType<SimRemoveAbyssalParticipant>());
        var removed = await freshSpace.DetachAbyssalParticipantAsync(contentId, scene, character, ship);
        Assert.True(removed.Succeeded, removed.Error);
        Assert.True(removed.AlreadyAbsent);
        Assert.False(removed.SessionUpdated);
        var request = Assert.Single(sim.Heard.OfType<SimRemoveAbyssalParticipant>());
        Assert.Equal(scene, request.Scene);
        Assert.Equal(ship.Id, request.ShipId);
    }

    [PgFact]
    public async Task Public_trace_accepts_tier_zero_and_six_and_rejects_out_of_range_tiers()
    {
        await using var rig = await Rig.CreateAsync();
        foreach (var tier in new[] { 0, 6, -1, 7 })
        {
            var trace = new SimAbyssalTrace(
                30_100_032, 9_000_000 + tier, 48_964, 1_991, 2, 140_000_342, 1_000,
                [0, 0, 0], [0, 0, 0], [0, 0, 1], null, 0, 1, 47_895, tier, rig.Now + 900_000);
            var priorPublications = rig.Sim.Heard.OfType<SimEnsureAbyssalTracePublished>().Count();

            var result = await rig.Space.EnsureAbyssalTracePublishedAsync(trace);

            if (tier is >= 0 and <= 6)
            {
                Assert.True(result.Succeeded, $"tier {tier}: {result.Error}");
                Assert.True(result.Materialized);
                var publication = rig.Sim.Heard.OfType<SimEnsureAbyssalTracePublished>().Last();
                Assert.Equal(trace.ItemId, publication.Trace.ItemId);
                Assert.Equal(tier, publication.Trace.DifficultyTier);
                Assert.Equal(trace.Position, publication.Trace.Position);
                Assert.Equal(priorPublications + 1, rig.Sim.Heard.OfType<SimEnsureAbyssalTracePublished>().Count());
            }
            else
            {
                Assert.False(result.Succeeded);
                Assert.Equal("BAD_ABYSSAL_TRACE", result.Error);
                Assert.Equal(priorPublications, rig.Sim.Heard.OfType<SimEnsureAbyssalTracePublished>().Count());
            }
        }
    }

    [PgFact]
    public async Task Source_system_pose_is_read_from_the_active_sim_pilot_before_run_creation()
    {
        var releaseSessionChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timers = new Eve.Scheduler.LocalTimers(delay: (duration, token) => duration == TimeSpan.FromSeconds(10)
            ? releaseSessionChange.Task.WaitAsync(token) : Task.CompletedTask);
        var (rig, character, _) = await InSpaceAsync(timers);
        await using var _ = rig;
        var source = SimSceneKey.SolarSystem(checked((int)(await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId));

        var pose = await rig.Space.GetAbyssalDeploymentPoseAsync(character.ShipId, source);

        Assert.True(pose.Found, pose.Error);
        Assert.Equal(character.ShipId, pose.ShipId);
        Assert.Equal(source, pose.Scene);
        Assert.Equal([0.0, 0.0, 0.0], pose.Position);
        Assert.Equal([0.0, 0.0, 1.0], pose.Direction);
        var request = Assert.Single(rig.Sim.Heard.OfType<SimGetAbyssalParticipantPose>());
        Assert.Equal(character.ShipId, request.ShipId);
        Assert.Equal(source, request.Scene);
        Assert.NotNull(pose.Deployment);
        Assert.True(pose.SessionChanging);
        releaseSessionChange.TrySetResult();
        var afterCooldown = await rig.Space.GetAbyssalDeploymentPoseAsync(character.ShipId, source);
        for (var attempt = 0; attempt < 100 && afterCooldown.SessionChanging; attempt++)
        {
            await Task.Delay(10);
            afterCooldown = await rig.Space.GetAbyssalDeploymentPoseAsync(character.ShipId, source);
        }
        Assert.True(afterCooldown.Found, afterCooldown.Error);
        Assert.False(afterCooldown.SessionChanging);
        Assert.False(afterCooldown.Tethered);
        Assert.False(afterCooldown.SelfDestructPending);

    }

    [PgFact]
    public async Task Already_settled_retry_rejects_a_character_snapshot_not_at_its_clone_station()
    {
        var (rig, created, session) = await InSpaceAsync();
        await using var _ = rig;
        var prepared = await PrepareAlreadySettledCloneAsync(rig, created, session, 92, Guid.NewGuid().ToString("D"), disconnect: false);
        var invalidCharacter = prepared.Character with { StationId = prepared.CloneStation.Id + 1 };

        var result = await rig.Space.ResumeAbyssalParticipantSettlementAsync(
            prepared.Request, invalidCharacter, prepared.ActiveShip, prepared.ShipDeath);

        Assert.False(result.Succeeded);
        Assert.Equal("ABYSSAL_ALREADY_SETTLED_CLONE_MISMATCH", result.Error);
        Assert.Empty(rig.Sim.Heard.OfType<SimRemoveAbyssalParticipant>());
        Assert.Equal(prepared.DestroyedShipId, session.GetInt("shipid"));
    }

    [PgFact]
    public async Task Already_settled_retry_cleans_an_active_resident_and_replays_when_sim_already_absent()
    {
        var (rig, created, session) = await InSpaceAsync();
        await using var _ = rig;
        var prepared = await PrepareAlreadySettledCloneAsync(rig, created, session, 93, Guid.NewGuid().ToString("D"), disconnect: false);
        rig.Sim.RemoveAbyssalParticipantAnswer = removal =>
            new SimRemoveAbyssalParticipantReply(removal.RequestId, true, null);

        var result = await rig.Space.ResumeAbyssalParticipantSettlementAsync(
            prepared.Request, prepared.Character, prepared.ActiveShip, prepared.ShipDeath);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.ResidentRemoved);
        Assert.True(result.AlreadyAbsent);
        Assert.True(result.SessionUpdated);
        Assert.Equal(prepared.ActiveShip.Id, session.GetInt("shipid"));
        Assert.Equal(prepared.CloneStation.Id, session.GetInt("stationid2"));
        Assert.Equal(prepared.CloneStation.Id, session.GetInt("locationid"));

        var replay = await rig.Space.ResumeAbyssalParticipantSettlementAsync(
            prepared.Request, prepared.Character, prepared.ActiveShip, prepared.ShipDeath);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.True(replay.AlreadyAbsent);
        Assert.False(replay.SessionUpdated);
        var removals = rig.Sim.Heard.OfType<SimRemoveAbyssalParticipant>().ToArray();
        Assert.Equal(2, removals.Length);
        Assert.All(removals, removal =>
        {
            Assert.Equal(prepared.Scene, removal.Scene);
            Assert.Equal(prepared.DestroyedShipId, removal.ShipId);
        });
    }

    [PgFact]
    public async Task Already_settled_retry_removes_an_offline_resident_without_inventing_a_session()
    {
        var (rig, created, session) = await InSpaceAsync();
        await using var _ = rig;
        var prepared = await PrepareAlreadySettledCloneAsync(rig, created, session, 94, Guid.NewGuid().ToString("D"), disconnect: true);

        var result = await rig.Space.ResumeAbyssalParticipantSettlementAsync(
            prepared.Request, prepared.Character, prepared.ActiveShip, prepared.ShipDeath);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.ResidentRemoved);
        Assert.False(result.AlreadyAbsent);
        Assert.False(result.SessionUpdated);
        var removal = Assert.Single(rig.Sim.Heard.OfType<SimRemoveAbyssalParticipant>());
        Assert.Equal(prepared.Scene, removal.Scene);
        Assert.Equal(prepared.DestroyedShipId, removal.ShipId);
    }

    [PgFact]
    public async Task Already_settled_retry_after_space_restart_checks_real_clone_and_accepts_sim_already_absent()
    {
        var (rig, created, session) = await InSpaceAsync();
        await using var _ = rig;
        var prepared = await PrepareAlreadySettledCloneAsync(
            rig, created, session, 95, Guid.NewGuid().ToString("D"), disconnect: false);

        var freshSim = new FakeSim
        {
            RemoveAbyssalParticipantAnswer = removal =>
                new SimRemoveAbyssalParticipantReply(removal.RequestId, true, null),
        };
        using var freshSpace = new SpaceSessions(
            freshSim, rig.Ships, rig.Statics, rig.Stations, rig.Systems, () => rig.Now, rig.Log.Add)
        {
            RookieShipOfRace = rig.Space.RookieShipOfRace,
        };

        var result = await freshSpace.ResumeAbyssalParticipantSettlementAsync(
            prepared.Request, prepared.Character, prepared.ActiveShip, prepared.ShipDeath);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.ResidentRemoved);
        Assert.True(result.AlreadyAbsent);
        Assert.False(result.SessionUpdated);
        var removal = Assert.Single(freshSim.Heard.OfType<SimRemoveAbyssalParticipant>());
        Assert.Equal(prepared.Scene, removal.Scene);
        Assert.Equal(prepared.DestroyedShipId, removal.ShipId);

        var persistedCharacter = await rig.Characters.GetAsync(created.CharacterId);
        var persistedActiveShip = await rig.Items.GetAsync(prepared.ActiveShip.Id);
        Assert.Equal(prepared.CloneStation.Id, persistedCharacter!.StationId);
        Assert.Equal(prepared.ActiveShip.Id, persistedCharacter.ShipId);
        Assert.Equal(prepared.CloneStation.Id, persistedActiveShip!.LocationId);
        Assert.Equal(Eve.Inventory.ItemFlags.Hangar, persistedActiveShip.Flag);
    }

    [PgFact]
    public async Task Abyssal_transfer_and_origin_conduits_are_static_scene_objects_not_npcs()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        var contentId = Guid.NewGuid().ToString("D");
        var source = SimSceneKey.SolarSystem(checked((int)session.GetInt("solarsystemid2")!.Value));
        var room = new SimSceneKey(source.LocationId, 97, 1);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(room)));
        await rig.Statics.EnsureTypesAsync([47_685, 47_686]);
        var transferType = rig.Statics.Type(47_685)
            ?? throw new InvalidOperationException("The transfer conduit type is absent from the pinned SDE.");
        var originType = rig.Statics.Type(47_686)
            ?? throw new InvalidOperationException("The origin conduit type is absent from the pinned SDE.");

        Assert.Equal((366, 2, 22_130, 5_000.0),
            (transferType.GroupId, transferType.CategoryId, transferType.GraphicId, transferType.Attributes[162]));
        Assert.Equal((366, 2, 22_131, 5_000.0),
            (originType.GroupId, originType.CategoryId, originType.GraphicId, originType.Attributes[162]));

        var roomOrigin = new[] { 100.0, 200.0, 300.0 };
        var direction = new[] { 0.0, 0.0, 1.0 };
        double[] transferPosition = [roomOrigin[0], roomOrigin[1], roomOrigin[2] + 35_000.0];
        Assert.Null(await rig.Space.EnsureAbyssalSceneObjectAsync(
            contentId, room, 9_840_000_000_001, 47_685, character.CharacterId,
            transferPosition, direction, false));
        Assert.Null(await rig.Space.EnsureAbyssalSceneObjectAsync(
            contentId, room, 9_840_000_000_002, 47_686, character.CharacterId,
            [roomOrigin[0], roomOrigin[1], roomOrigin[2] + 35_000.0], direction, true));

        var objects = rig.Sim.Heard.OfType<SimEnsureAbyssalSceneObject>().ToArray();
        Assert.Equal(2, objects.Length);
        Assert.Equal(9_840_000_000_001L, objects[0].Object.ItemId);
        Assert.Equal(47_685, objects[0].Object.TypeId);
        Assert.Equal(room, objects[0].Object.Scene);
        Assert.Equal(transferPosition, objects[0].Object.Position);
        Assert.False(objects[0].Object.IsOpen);
        Assert.Equal(9_840_000_000_002L, objects[1].Object.ItemId);
        Assert.Equal(47_686, objects[1].Object.TypeId);
        Assert.Equal(room, objects[1].Object.Scene);
        Assert.True(objects[1].Object.IsOpen);
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
    }

    private static SimAbyssalSceneSpec SceneSpec(SimSceneKey scene, IReadOnlyList<SimAbyssalAttributeModifier>? modifiers = null) => new(
        scene,
        new SimAbyssalEnvironmentProfile(modifiers ?? [], []),
        new SimAbyssalBoundaryProfile([0.0, 0.0, 0.0], 100_000.0, 1_000,
            [new SimAbyssalBoundaryDamagePoint(0.0, 0.0)]),
        134_351_527_136_350_000);

    private static async Task<(AbyssalParticipantDeathSettlementRequestedEvent Request,
        Eve.Characters.Character Character, Eve.Inventory.Item ActiveShip, Eve.Ships.ShipDeath ShipDeath, SimSceneKey Scene,
        long DestroyedShipId, Eve.Universe.Station CloneStation)> PrepareAlreadySettledCloneAsync(
        Rig rig, Eve.Characters.Created created, ClientSession session, long instanceId, string contentId, bool disconnect)
    {
        var initialCharacter = await rig.Characters.GetAsync(created.CharacterId)
            ?? throw new InvalidOperationException("Test character disappeared before death settlement.");
        var destroyedShip = await rig.Items.GetAsync(created.ShipId)
            ?? throw new InvalidOperationException("Test ship disappeared before death settlement.");
        var source = SimSceneKey.SolarSystem(initialCharacter.SolarSystemId);
        var originPose = await rig.Space.GetAbyssalDeploymentPoseAsync(destroyedShip.Id, source);
        Assert.True(originPose.Found, originPose.Error);
        var room = new SimSceneKey(source.LocationId, instanceId, 1);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(room)));
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(
            contentId, destroyedShip.Id, source, room, originPose.Position, originPose.Direction));

        if (disconnect)
        {
            var disconnected = new TaskCompletionSource<AbyssalParticipantDisconnectedNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Space.AbyssalParticipantDisconnectedAsync = (notice, _) =>
            {
                disconnected.TrySetResult(notice);
                return ValueTask.CompletedTask;
            };
            session.Close();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var shipDeath = await rig.Ships.DestroyShipAsync(created.CharacterId, destroyedShip.Id, null, contentId);
        Assert.NotNull(shipDeath);
        var capsuleCharacter = await rig.Characters.GetAsync(created.CharacterId)
            ?? throw new InvalidOperationException("Character disappeared after ship destruction.");
        var capsuleId = capsuleCharacter.ShipId
            ?? throw new InvalidOperationException("Capsule was not assigned after ship destruction.");
        var cloneStation = rig.Stations.Get(capsuleCharacter.HomeStationId) ?? rig.Stations.Get(capsuleCharacter.StationId)
            ?? throw new InvalidOperationException("Clone station is absent from the pinned station catalog.");
        await rig.Statics.EnsureTypesAsync([destroyedShip.TypeId]);
        var shipType = rig.Statics.Type(destroyedShip.TypeId)
            ?? throw new InvalidOperationException("Destroyed ship type is absent from the pinned SDE.");
        var radius = shipType.Attributes.GetValueOrDefault(162);
        Assert.True(double.IsFinite(radius) && radius > 0);
        var podDeath = await rig.Ships.DestroyCapsuleAsync(
            created.CharacterId,
            capsuleId,
            null,
            string.Empty,
            destroyedShip.TypeId,
            new Eve.Ships.HomePlace(cloneStation.Id, cloneStation.SolarSystemId, cloneStation.ConstellationId, cloneStation.RegionId));
        Assert.NotNull(podDeath);

        var character = await rig.Characters.GetAsync(created.CharacterId)
            ?? throw new InvalidOperationException("Character disappeared after clone settlement.");
        var activeShipId = character.ShipId
            ?? throw new InvalidOperationException("Clone settlement left the character without an active ship.");
        var activeShip = await rig.Items.GetAsync(activeShipId)
            ?? throw new InvalidOperationException("Clone settlement did not persist the active rookie ship.");
        Assert.Null(await rig.Items.GetAsync(destroyedShip.Id));
        Assert.Equal(cloneStation.Id, character.StationId);
        Assert.Equal(cloneStation.Id, activeShip.LocationId);
        Assert.Equal(Eve.Inventory.ItemFlags.Hangar, activeShip.Flag);

        var origin = new AbyssalParticipantOrigin(
            source.LocationId,
            session.GetInt("locationid") ?? source.LocationId,
            new AbyssalPosition(originPose.Position[0], originPose.Position[1], originPose.Position[2]),
            new AbyssalPosition(originPose.Direction[0], originPose.Direction[1], originPose.Direction[2]),
            radius);
        var request = new AbyssalParticipantDeathSettlementRequestedEvent(
            instanceId,
            contentId,
            $"abyssal:{contentId}:death:{created.CharacterId}",
            created.CharacterId,
            destroyedShip.Id,
            AbyssalDeathSettlementCause.ShipDestroyed,
            origin,
            source.LocationId,
            room.RoomIndex,
            rig.Now,
            null);
        return (request, character, activeShip, shipDeath!, room, destroyedShip.Id, cloneStation);
    }

    [PgFact]
    public async Task Abyssal_weather_recomputes_NPC_ship_and_weapon_stats_from_static_Dogma_once()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;
        const int npcTypeId = 23245;
        const long npcShipId = 8_400_000_001;
        const long instanceId = 84;
        var locationId = checked((int)session.GetInt("solarsystemid2")!.Value);
        var scene = new SimSceneKey(locationId, instanceId, 1);
        var contentId = Guid.NewGuid().ToString("D");
        var effects = new EffectDef(91_000_001, "test-only resolved abyssal weather", EffectDef.Passive,
        [
            new Modifier(ModifierFunc.Item, "itemID", Operation.PostPercent, 37, 0, Literal: 50.0),
            new Modifier(ModifierFunc.Item, "itemID", Operation.PostPercent, NpcArmament.DamageMultiplier, 0, Literal: 50.0),
            new Modifier(ModifierFunc.Item, "itemID", Operation.PostPercent, 212, 0, Literal: 50.0),
        ]);
        var profile = SceneSpec(scene,
        [
            new SimAbyssalAttributeModifier(37, SimAbyssalModifierOperation.Multiply, 1.5),
            new SimAbyssalAttributeModifier(NpcArmament.DamageMultiplier, SimAbyssalModifierOperation.Multiply, 1.5),
            new SimAbyssalAttributeModifier(212, SimAbyssalModifierOperation.Multiply, 1.5),
        ]);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(profile, [effects]));

        await rig.Statics.EnsureTypesAsync([npcTypeId]);
        var type = rig.Statics.Type(npcTypeId)!;
        var baselineWeapon = NpcArmament.HullWeapon(type, npcShipId, rig.Statics);
        Assert.NotNull(baselineWeapon);
        var first = await rig.Space.SpawnAbyssalNpcAsync(
            contentId, scene, npcShipId, npcTypeId, [500.0, 0.0, 0.0], [1.0, 0.0, 0.0]);
        Assert.NotNull(first.Session);

        var firstSpawn = Assert.Single(rig.Sim.Heard.OfType<SimSpawn>());
        Assert.Equal(contentId, firstSpawn.AbyssalContentId);
        var baseVelocity = type.Attributes.GetValueOrDefault(37);
        Assert.Equal(npcTypeId, firstSpawn.Ship.TypeId);
        Assert.Equal(type.GroupId, firstSpawn.Ship.GroupId);
        Assert.Equal(type.CategoryId, firstSpawn.Ship.CategoryId);
        Assert.Equal(baseVelocity * 1.5, firstSpawn.Ship.MaxVelocity, 4);
        Assert.Equal([500.0, 0.0, 0.0], firstSpawn.Position);
        Assert.Equal([1.0, 0.0, 0.0], firstSpawn.Direction);
        for (var index = 0; index < baselineWeapon.RawDamage.Count; index++)
            Assert.Equal(baselineWeapon.RawDamage[index] * 1.5, firstSpawn.Identity.Weapons.Single().RawDamage[index], 6);
        Assert.Null(firstSpawn.Identity.Wreck);
        Assert.False(firstSpawn.Identity.HostWreck);

        var abyssalKill = new TaskCompletionSource<AbyssalNpcDestroyedNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryNpcKillRaised = false;
        rig.Space.AbyssalNpcDestroyedAsync = (notice, _) =>
        {
            abyssalKill.TrySetResult(notice);
            return ValueTask.CompletedTask;
        };
        rig.Space.NpcDestroyed += _ => ordinaryNpcKillRaised = true;
        rig.Sim.Say(new SimDestroyed(first.Session!.Value, npcShipId, 0, [500.0, 0.0, 0.0], [1.0, 0.0, 0.0], 47.0, 0,
            new SimWreckKind(26_506, "NPC wreck", 1), npcTypeId, 1_000_125));
        var kill = await abyssalKill.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((instanceId, scene.LocationId, scene.RoomIndex, npcShipId, npcTypeId),
            (kill.InstanceId, kill.LocationId, kill.RoomIndex, kill.EntityId, kill.TypeId));
        Assert.False(ordinaryNpcKillRaised);
        Assert.Empty(await rig.Bounties.PendingAsync());
    }

    [PgFact]
    public async Task Abyssal_weather_is_recomputed_from_player_baseline_when_the_ship_changes_rooms()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        var snapshot = (await rig.Ships.LoadAsync(character.CharacterId))!;
        var baselineVelocity = snapshot.World.Get(snapshot.Ship.Id, 37);
        var contentId = Guid.NewGuid().ToString("D");
        var origin = SimSceneKey.SolarSystem(checked((int)session.GetInt("solarsystemid2")!.Value));
        var first = new SimSceneKey(origin.LocationId, 90, 1);
        var second = new SimSceneKey(origin.LocationId, 90, 2);
        var modifier = new SimAbyssalAttributeModifier(37, SimAbyssalModifierOperation.Multiply, 1.5);
        var effect = new EffectDef(91_000_002, "test-only resolved abyssal weather", EffectDef.Passive,
            [new Modifier(ModifierFunc.Item, "itemID", Operation.PostPercent, 37, 0, Literal: 50.0)]);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(first, [modifier]), [effect]));
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(second, [modifier]), [effect]));

        var entryPose = await rig.Space.GetAbyssalDeploymentPoseAsync(character.ShipId, origin);
        Assert.True(entryPose.Found, entryPose.Error);
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(
            contentId, character.ShipId, origin, first, entryPose.Position, entryPose.Direction));
        var entryMove = Assert.Single(rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>());
        Assert.True(entryMove.RequestId > 0);
        Assert.True(entryMove.SessionHandoff);
        Assert.Equal((long?)first.LocationId, session.GetInt("solarsystemid"));
        var movesAfterEntry = rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count();
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(
            contentId, character.ShipId, origin, first, [900.0, 800.0, 700.0], [1.0, 0.0, 0.0]));
        Assert.Equal(movesAfterEntry, rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count());
        Assert.Equal((long?)first.LocationId, session.GetInt("solarsystemid"));
        var firstWeathered = rig.Sim.Heard.OfType<SimShipChanged>().Last(message => message.Ship.ShipId == character.ShipId).Ship.MaxVelocity;

        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, character.ShipId, first, second));
        var roomMove = rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Last();
        Assert.True(roomMove.PreserveSourcePose);
        Assert.False(roomMove.SessionHandoff);
        var movesAfterRoom = rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count();
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, character.ShipId, first, second));
        Assert.Equal(movesAfterRoom, rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count());
        var secondWeathered = rig.Sim.Heard.OfType<SimShipChanged>().Last(message => message.Ship.ShipId == character.ShipId).Ship.MaxVelocity;

        Assert.Equal(baselineVelocity * 1.5, firstWeathered, 4);
        Assert.Equal(firstWeathered, secondWeathered, 8);

        var returnPose = await rig.Space.GetAbyssalParticipantPoseAsync(contentId, character.ShipId, second);
        Assert.True(returnPose.Found, returnPose.Error);
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(
            contentId, character.ShipId, second, origin, returnPose.Position, returnPose.Direction));
        var returnMove = rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Last();
        Assert.True(returnMove.RequestId > 0);
        Assert.True(returnMove.SessionHandoff);
        Assert.Equal((long?)origin.LocationId, session.GetInt("solarsystemid"));
        Assert.Equal((long?)origin.LocationId, session.GetInt("solarsystemid2"));
        Assert.Equal((long?)origin.LocationId, session.GetInt("locationid"));
        var movesAfterReturn = rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count();
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(
            contentId, character.ShipId, second, origin, [700.0, 800.0, 900.0], [1.0, 0.0, 0.0]));
        Assert.Equal(movesAfterReturn, rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count());
        Assert.Equal((long?)origin.LocationId, session.GetInt("locationid"));
    }

    [PgFact]
    public async Task Disconnect_persists_the_domain_transition_but_keeps_the_sim_resident_ship()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        var contentId = Guid.NewGuid().ToString();
        var origin = SimSceneKey.SolarSystem(checked((int)session.GetInt("solarsystemid2")!.Value));
        var room = new SimSceneKey(origin.LocationId, 88, 1);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(room)));
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, new SimMoveAbyssalParticipant(
            902, character.ShipId, origin, room, [100.0, 200.0, 300.0], [0.0, 0.0, 1.0])));

        var disconnected = new TaskCompletionSource<AbyssalParticipantDisconnectedNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Space.AbyssalParticipantDisconnectedAsync = (notice, _) =>
        {
            disconnected.TrySetResult(notice);
            return ValueTask.CompletedTask;
        };
        session.Close();
        var notice = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((88L, contentId, character.CharacterId, character.ShipId, room.LocationId, room.RoomIndex),
            (notice.InstanceId, notice.ContentId, notice.CharacterId, notice.ShipId, notice.LocationId, notice.RoomIndex));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimLeave>(), leave => leave.SessionId == (ulong)session.SessionId);

        var movesBeforeReturn = rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count();
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(
            contentId, character.ShipId, room, origin, [400.0, 500.0, 600.0], [1.0, 0.0, 0.0]));
        var movesAfterReturn = rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count();
        Assert.Equal(movesBeforeReturn + 1, movesAfterReturn);
        Assert.False(rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Last().SessionHandoff);
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(
            contentId, character.ShipId, room, origin, [700.0, 800.0, 900.0], [0.0, 1.0, 0.0]));
        Assert.Equal(movesAfterReturn, rig.Sim.Heard.OfType<SimMoveAbyssalParticipant>().Count());
    }

    [PgFact]
    public async Task Abyssal_participant_death_uses_domain_settlement_without_respawning_a_capsule_in_the_run()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        var contentId = Guid.NewGuid().ToString();
        var origin = SimSceneKey.SolarSystem(checked((int)session.GetInt("solarsystemid2")!.Value));
        var room = new SimSceneKey(origin.LocationId, 89, 1);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(room)));
        Assert.Null(await rig.Space.MoveAbyssalParticipantAsync(contentId, new SimMoveAbyssalParticipant(
            903, character.ShipId, origin, room, [100.0, 200.0, 300.0], [0.0, 0.0, 1.0])));

        var destroyed = new TaskCompletionSource<AbyssalParticipantDestroyedNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Space.AbyssalParticipantDestroyedAsync = (notice, _) =>
        {
            destroyed.TrySetResult(notice);
            return ValueTask.CompletedTask;
        };
        rig.Sim.Say(new SimDestroyed((ulong)session.SessionId, character.ShipId, 0, [100.0, 200.0, 300.0], [0.0, 0.0, 1.0], 47.0, 0));
        var notice = await destroyed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((contentId, character.CharacterId, character.ShipId, room.InstanceId, room.RoomIndex),
            (notice.ContentId, notice.CharacterId, notice.ShipId, notice.InstanceId, notice.RoomIndex));
        Assert.Same(session, notice.Session);
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimRespawn>(), respawn => respawn.SessionId == (ulong)session.SessionId);
    }

    [PgFact]
    public async Task Domain_owned_loot_is_removed_from_its_private_scene_before_the_scene_is_removed()
    {
        var (rig, _, _) = await InSpaceAsync();
        await using var _ = rig;
        var scene = new SimSceneKey(30_000_001, 91, 2);
        Assert.Null(await rig.Space.EnsureAbyssalSceneAsync(SceneSpec(scene)));
        var container = new SimPlaceWreck(scene, 8_400_000_091, 0,
            new SimWreckKind(23, "Cargo Container", 1_176),
            [10.0, 20.0, 30.0], [0.0, 0.0, 1.0], 10.0, 0, new SimCredit(0, 0, 0, 0), false);

        rig.Space.PlaceAbyssalLootContainer(container);
        rig.Space.RemoveAbyssalLootContainer(scene, container.WreckId);
        Assert.Null(await rig.Space.RemoveAbyssalSceneAsync(scene));

        Assert.Equal([container], rig.Sim.Heard.OfType<SimPlaceWreck>().ToArray());
        Assert.Equal(new SimRemoveWreck(container.WreckId, 0), rig.Sim.Heard.OfType<SimRemoveWreck>().Single());
        var removed = Assert.Single(rig.Sim.Heard.OfType<SimRemoveAbyssalScene>());
        Assert.Equal(scene, removed.Scene);
        Assert.NotEqual(0UL, removed.RequestId);
    }
}
