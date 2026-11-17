using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class SmartbombHitHostTests
{
    [PgFact]
    public async Task A_smartbomb_hit_extends_the_timer_of_an_offline_combat_logoff_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var attacker = await rig.NewCharacterAsync("Smartbomb Attacker", 1);
        var attackerSession = await WeaponHostTests.InSpaceAsync(rig, attacker, 1);
        var victim = await rig.NewCharacterAsync("Offline Smartbomb Victim", 2);
        var victimSession = await WeaponHostTests.InSpaceAsync(rig, victim, 2);
        var activeVictim = await rig.NewCharacterAsync("Active Smartbomb Victim", 3);
        var activeVictimSession = await WeaponHostTests.InSpaceAsync(rig, activeVictim, 3);
        var targetShipId = victimSession.GetInt("shipid")!.Value;
        var activeTargetShipId = activeVictimSession.GetInt("shipid")!.Value;
        var systemId = attackerSession.GetInt("solarsystemid")!.Value;
        var schema = "cw_logoff_bomb_" + Guid.NewGuid().ToString("N")[..12];
        await Eve.Crimewatch.PgCrimewatchStore.MigrateAsync(rig.Source, schema);
        try
        {
            using var timers = new Eve.Scheduler.LocalTimers(delay: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
            using var crimewatch = new Eve.Services.Crimewatch.CrimewatchRuntime(
                new Eve.Crimewatch.PgCrimewatchStore(rig.Source, schema), rig.CharacterSessions, () => rig.Now,
                new WeaponHostTests.EmptyCrimewatchPersistence(), localTimers: timers);
            var store = new Eve.Crimewatch.PgCrimewatchStore(rig.Source, schema);
            var originalExpiresAt = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
            await store.UpdateAsync(victim.CharacterId, state => state with
            {
                PvpUntilMs = originalExpiresAt,
                SolarSystemId = systemId,
            }, rig.Now);
            rig.Space.CrimewatchStateOf = crimewatch.StateOfAsync;
            rig.Space.ModuleAggression = (request, moduleId, token) =>
                crimewatch.RecordModuleAggressionAsync(request, moduleId, token);

            victimSession.Close();
            await rig.Space.QuiesceAsync();
            Assert.Equal(SpaceItemKind.CombatLogoffShip, (await rig.SpaceItems.GetAsync(targetShipId))?.Kind);
            rig.Now += 10_000;
            var victimSnapshot = Assert.IsType<ShipSnapshot>(await rig.Ships.LoadAsync(victim.CharacterId));
            var activeVictimSnapshot = Assert.IsType<ShipSnapshot>(await rig.Ships.LoadAsync(activeVictim.CharacterId));

            rig.Sim.Deliver(new SimSmartbombHit(
                (ulong)attackerSession.SessionId,
                attackerSession.GetInt("shipid")!.Value,
                ModuleId: 733,
                Targets:
                [
                    new SimSmartbombTarget(targetShipId, victim.CharacterId, victimSnapshot.Ship.TypeId),
                    new SimSmartbombTarget(activeTargetShipId, activeVictim.CharacterId, activeVictimSnapshot.Ship.TypeId),
                    new SimSmartbombTarget(987654321, 0, 0),
                ]));
            await rig.Space.QuiesceAsync();

            var extendedUntil = rig.Now + Eve.Crimewatch.CombatState.PvpDurationMs;
            Assert.Equal(extendedUntil, (await crimewatch.StateOfAsync(victim.CharacterId)).PvpUntilMs);
            Assert.Equal(extendedUntil, (await crimewatch.StateOfAsync(activeVictim.CharacterId)).PvpUntilMs);

            rig.Now = originalExpiresAt;
            await rig.Containers.SweepAsync();
            var retained = Assert.IsType<SpaceItem>(await rig.SpaceItems.GetAsync(targetShipId));
            Assert.Equal((SpaceItemKind.CombatLogoffShip, extendedUntil), (retained.Kind, retained.ExpiresAtMs));
        }
        finally
        {
            await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
