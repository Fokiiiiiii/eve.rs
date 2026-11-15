using Eve.Marshal;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class CombatLogoffRestoreTests
{
    [PgFact]
    public async Task A_resumed_CONCORD_response_resolves_an_offline_ship_after_the_host_restarts()
    {
        var waits = new List<(TimeSpan After, TaskCompletionSource Done)>();
        using var timers = new Eve.Scheduler.LocalTimers(delay: (after, token) =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waits) waits.Add((after, done));
            return done.Task.WaitAsync(token);
        });
        var authority = Eve.Npc.NpcAuthority.Load();
        await using var rig = await Rig.CreateAsync(localTimers: timers, npcAuthority: authority);
        var character = await rig.NewCharacterAsync("Concord Restart Pilot");
        var session = await rig.SessionAsync(character, 1);
        var shipObject = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipObject,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var expiresAt = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(
            new Eve.Crimewatch.CombatState(0, expiresAt, 0, long.MaxValue, Eve.Crimewatch.Flag.Criminal, 0, systemId));
        session.Close();
        await rig.Space.QuiesceAsync();
        var persisted = Assert.IsType<SpaceItem>(await rig.SpaceItems.GetAsync(character.ShipId));
        var snapshot = Assert.IsType<ShipSnapshot>(await rig.Ships.LoadAsync(character.CharacterId));
        Assert.Equal(SpaceItemKind.CombatLogoffShip, persisted.Kind);
        Assert.Equal((character.CharacterId, (long)systemId, 3),
            (persisted.CharacterId, (long)persisted.SolarSystemId, persisted.Position.Count));
        Assert.True(persisted.ExpiresAtMs > rig.Now);
        Assert.Equal((persisted.ItemId, persisted.CharacterId, (long)systemId),
            (snapshot.Ship.Id, snapshot.Character.Id, snapshot.Character.SolarSystemId));
        Assert.Equal(persisted.ItemId, snapshot.Ship.Id);
        Assert.Equal((long?)persisted.ItemId, snapshot.Character.ShipId);
        Assert.Equal((long)systemId, snapshot.Ship.LocationId);
        Assert.Equal((long)systemId, snapshot.Character.SolarSystemId);
        Assert.Equal(CharacterShips.InSpaceFlag, snapshot.Ship.Flag);

        var restartLog = new List<string>();
        using var restarted = new SpaceSessions(
            rig.Sim, rig.Ships, rig.Statics, rig.Stations, rig.Systems, () => rig.Now, restartLog.Add,
            localTimers: timers, spaceItems: rig.SpaceItems)
        {
            Npcs = rig.Space.Npcs,
            CrimewatchStateOf = rig.Space.CrimewatchStateOf,
        };
        Assert.NotNull(restarted.Npcs);
        var hydrate = typeof(SpaceSessions).GetMethod(
            "FindOfflineCombatLogoffShipAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var hydrated = await (Task<SpaceItem?>)hydrate.Invoke(
            restarted, [character.CharacterId, (long)systemId, 0L, CancellationToken.None])!;
        Assert.NotNull(hydrated);

        var handled = await restarted.RespondToCriminalAsync(character.CharacterId, systemId, attackerShipId: 0);
        Assert.True(handled,
            $"expires={persisted.ExpiresAtMs} now={rig.Now} characterShip={snapshot.Character.ShipId}; " +
            $"messages={string.Join(",", rig.Sim.Heard.Select(message => message.GetType().Name))}; " +
            $"logs={string.Join(" | ", restartLog)}");
        Assert.Equal(2, rig.Sim.Heard.OfType<SimSpawn>().Count());

        (TimeSpan After, TaskCompletionSource Done) punishment;
        lock (waits) punishment = Assert.Single(waits, wait => wait.After == TimeSpan.FromSeconds(15));
        punishment.Done.TrySetResult();
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimDestroy>().Any(destroy => destroy.ShipId == character.ShipId));
    }

    [PgFact]
    public async Task A_combat_logoff_ship_destroyed_after_a_host_restart_is_settled_as_a_ship_loss()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync("Logoff Victim");
        var session = await rig.SessionAsync(character, 1);
        var shipObject = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipObject,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var expiresAt = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(
            new Eve.Crimewatch.CombatState(0, expiresAt, 0, 0, Eve.Crimewatch.Flag.None, 0, systemId));

        session.Close();
        await rig.Space.QuiesceAsync();
        Assert.Equal(SpaceItemKind.CombatLogoffShip,
            (await rig.SpaceItems.GetAsync(character.ShipId))?.Kind);
        rig.Space.Dispose();

        using var restarted = new SpaceSessions(
            rig.Sim, rig.Ships, rig.Statics, rig.Stations, rig.Systems, () => rig.Now, spaceItems: rig.SpaceItems);
        rig.Sim.Deliver(new SimDestroyed(
            (1UL << 61) + 1, character.ShipId, 0, [120.0, 240.0, 360.0], [0.0, 0.0, 1.0], 47, 0));
        await restarted.QuiesceAsync();

        Assert.Null(await rig.SpaceItems.GetAsync(character.ShipId));
        Assert.Null(await rig.Items.GetAsync(character.ShipId));
        var replacement = Assert.IsType<ShipSnapshot>(await rig.Ships.LoadAsync(character.CharacterId));
        Assert.NotEqual(character.ShipId, replacement.Ship.Id);
        Assert.Equal(CharacterShips.CapsuleTypeId, replacement.Ship.TypeId);
        Assert.Equal(CharacterShips.InSpaceFlag, replacement.Ship.Flag);
        Assert.Equal(systemId, replacement.Character.SolarSystemId);
    }

    [PgFact]
    public async Task A_disconnected_combat_ship_remains_in_space_when_a_later_attack_extends_its_capsuleer_timer()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync("Timer Extension");
        var session = await rig.SessionAsync(character, 1);
        var shipObject = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipObject,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var originalUntil = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
        var combat = new Eve.Crimewatch.CombatState(0, originalUntil, 0, 0, Eve.Crimewatch.Flag.None, 0, systemId);
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(combat);

        session.Close();
        await rig.Space.QuiesceAsync();
        Assert.Equal(originalUntil, (await rig.SpaceItems.GetAsync(character.ShipId))?.ExpiresAtMs);

        rig.Now = originalUntil;
        var extendedUntil = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
        combat = combat with { PvpUntilMs = extendedUntil };
        await rig.Containers.SweepAsync();

        var retained = Assert.IsType<SpaceItem>(await rig.SpaceItems.GetAsync(character.ShipId));
        Assert.Equal(SpaceItemKind.CombatLogoffShip, retained.Kind);
        Assert.Equal(extendedUntil, retained.ExpiresAtMs);
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimRemoveUnpilotedShip>(), removal =>
            removal.ShipId == character.ShipId);
    }

    [PgFact]
    public async Task A_fresh_system_restore_recreates_an_offline_combat_logoff_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync("Offline Restore Pilot");
        var session = await rig.SessionAsync(character, 1);
        var shipObject = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipObject,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var expiresAt = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(
            new Eve.Crimewatch.CombatState(0, expiresAt, 0, 0, Eve.Crimewatch.Flag.None, 0, systemId));

        session.Close();
        await rig.Space.QuiesceAsync();
        var persisted = Assert.IsType<SpaceItem>(await rig.SpaceItems.GetAsync(character.ShipId));
        Assert.Equal(SpaceItemKind.CombatLogoffShip, persisted.Kind);

        rig.Containers.Dispose();
        rig.Space.Dispose();
        using var restartedSpace = new SpaceSessions(
            rig.Sim, rig.Ships, rig.Statics, rig.Stations, rig.Systems, () => rig.Now, spaceItems: rig.SpaceItems);
        using var restartedContainers = new SpaceContainers(
            rig.Sim, rig.Ships, rig.Items, rig.SpaceItems, new Eve.Ships.PgWorkCycleLedger(rig.Source, rig.SpaceSchema),
            new EventBusFacts(rig.Events), rig.Statics, restartedSpace, () => rig.Now);
        var beforeRestore = rig.Sim.Heard.Count;
        await restartedContainers.RestoreAsync(systemId);

        var restored = Assert.Single(rig.Sim.Heard.Skip(beforeRestore).OfType<SimEnter>(), enter => enter.Ship.ShipId == character.ShipId);
        Assert.Equal(EnterHow.InSpace, restored.How);
        Assert.Equal(persisted.Position.Take(3), restored.Position);
        Assert.Equal(persisted.Direction.Take(3), restored.Direction);
        Assert.Equal(character.CharacterId, restored.Ship.CharacterId);
        Assert.Contains(rig.Sim.Heard.Skip(beforeRestore).OfType<SimLeave>(), leave =>
            leave.SessionId == restored.SessionId && leave.Why == LeaveWhy.Ejected);
    }

    [PgFact]
    public async Task Reentering_a_combat_logoff_ship_after_a_simulation_restart_ignores_the_unloaded_ball()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync("Restart Pilot");
        var session = await rig.SessionAsync(character, 1);
        var shipObject = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipObject,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var expiresAt = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(
            new Eve.Crimewatch.CombatState(0, expiresAt, 0, 0, Eve.Crimewatch.Flag.None, 0, systemId));

        session.Close();
        await rig.Space.QuiesceAsync();

        var reconnecting = await rig.SessionAsync(character, 2);
        reconnecting.Set("stationid", PyNone.Instance);
        reconnecting.Set("stationid2", PyNone.Instance);
        reconnecting.Set("structureid", PyNone.Instance);
        reconnecting.Set("worldspaceid", PyNone.Instance);
        reconnecting.Set("solarsystemid", new PyInt(systemId));
        reconnecting.Set("solarsystemid2", new PyInt(systemId));
        reconnecting.Set("locationid", new PyInt(systemId));
        var persistedCharacter = (await rig.Characters.GetAsync(character.CharacterId))!;
        reconnecting.Set("constellationid", new PyInt(persistedCharacter.ConstellationId));
        reconnecting.Set("regionid", new PyInt(persistedCharacter.RegionId));
        reconnecting.Set("shipid", new PyLong(character.ShipId));
        rig.Sim.RemoveUnpilotedShipAnswer = removal =>
            new SimRemoveUnpilotedShipReply(removal.RequestId, removal.Scene, removal.ShipId, false, "SCENE_NOT_LOADED");

        await rig.Space.OnCharacterEnteredAsync(reconnecting, character.CharacterId, CancellationToken.None);

        Assert.Contains(rig.Sim.Heard.OfType<SimEnter>(), enter => enter.SessionId == (ulong)reconnecting.SessionId);
        Assert.Null(await rig.SpaceItems.GetAsync(character.ShipId));
    }

    [PgFact]
    public async Task Restoring_a_system_after_a_combat_logoff_expiry_keeps_the_ship_and_removes_its_unpiloted_ball()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync("Combat Logoff Pilot");
        var session = await rig.SessionAsync(character, 1);
        var shipObject = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipObject,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var expiresAt = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
        rig.Space.CrimewatchStateOf = (_, _) => Task.FromResult(
            new Eve.Crimewatch.CombatState(0, expiresAt, 0, 0, Eve.Crimewatch.Flag.None, 0, systemId));

        session.Close();
        await rig.Space.QuiesceAsync();
        var persisted = Assert.IsType<SpaceItem>(await rig.SpaceItems.GetAsync(character.ShipId));
        Assert.Equal(SpaceItemKind.CombatLogoffShip, persisted.Kind);

        rig.Now = expiresAt;
        rig.Sim.RemoveUnpilotedShipAnswer = removal =>
            new SimRemoveUnpilotedShipReply(removal.RequestId, removal.Scene, removal.ShipId, false, "SCENE_NOT_LOADED");
        await rig.Containers.RestoreAsync(systemId);

        var ship = Assert.IsType<Eve.Inventory.Item>(await rig.Items.GetAsync(character.ShipId));
        Assert.Equal((systemId, CharacterShips.InSpaceFlag), (ship.LocationId, ship.Flag));
        var active = Assert.IsType<SpaceItem>(await rig.SpaceItems.GetAsync(character.ShipId));
        Assert.Equal((SpaceItemKind.ActiveShip, long.MaxValue), (active.Kind, active.ExpiresAtMs));
        Assert.Contains(rig.Sim.Heard.OfType<Eve.Space.SimRemoveUnpilotedShip>(), removal =>
            removal.ShipId == character.ShipId && removal.Scene.LocationId == systemId);
    }
}
