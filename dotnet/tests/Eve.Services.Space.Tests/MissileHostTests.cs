using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services.Space.Effects;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A launcher that fires missiles: what the host tells the simulation to fly (the damage of the missile, its speed and flight time, what its explosion reaches), the effect the pilot
/// asks for and the one that runs, and the refusals of a launcher with nothing in it. The numbers are those of the recording of the reference (<c>oracle/fixtures/scenarios/missile.json</c>).
/// </summary>
public class MissileHostTests
{
    private const int RocketLauncherI = 10629, ScourgeRocket = 266, Slot = 28, UseMissiles = 101;
    private const int RapidTorpedoLauncherI = 37288, ScourgeTorpedo = 267, Phoenix = 19726;
    /// <summary>The SCARAB pods fit the deathless Tholos and Cenotaph (wiki), so the breacher test flies a Tholos (SDE 85087).</summary>
    private const int SmallBreacherLauncher = 85084, SmallBreacherPod = 85088, Tholos = 85087;

    /// <summary>A pilot in space whose slot 28 holds a Rocket Launcher I (online), loaded with Scourge Rockets when <paramref name="loaded"/>.</summary>
    private static Task<(ClientSession Session, Eve.Characters.Created Character, long Launcher)> ArmedInSpaceAsync(Rig rig, bool loaded) =>
        ArmedInSpaceAsync(rig, loaded, RocketLauncherI, ScourgeRocket, Rig.Cockroach, (3319, 5), (3320, 5));

    /// <summary>A pilot in <paramref name="shipType"/> in space whose slot 28 holds <paramref name="launcherType"/> (online), loaded with <paramref name="chargeType"/> when <paramref name="loaded"/>.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long Launcher)> ArmedInSpaceAsync(
        Rig rig, bool loaded, int launcherType, int chargeType, int shipType, params (int Skill, int Level)[] skills)
    {
        var character = await rig.NewCharacterAsync(shipTypeId: shipType);
        await TrainAsync(rig, character.CharacterId, skills);
        if (shipType == Rig.Cockroach)
        {
            Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, character.ShipId + 3, character.ShipId, Rig.Station, ItemFlags.Hangar, null));
        }
        var granted = (await rig.Ships.GrantAsync(character.CharacterId, launcherType, 1, singleton: true))!.Single().After;
        var fitted = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, Slot, 1);
        Assert.True(fitted is Eve.Ships.ShipChanged, fitted.ToString());
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, granted.Id, true));
        if (loaded)
        {
            var rockets = (await rig.Ships.GrantAsync(character.CharacterId, chargeType, 40, singleton: false))!.Single().After;
            Assert.IsType<Eve.Ships.AmmoChanged>(await rig.Ships.LoadAmmoAsync(character.CharacterId, character.ShipId, [granted.Id], [rockets.Id], Rig.Station));
        }
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (session, character, granted.Id);
    }

    /// <summary>The skills the launcher and its charge need, at the levels given: what is tested is not the fitting rules.</summary>
    private static async Task TrainAsync(Rig rig, long character, params (int Skill, int Level)[] skills)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Skills.GrantAsync(connection, transaction, character, skills.Select(s => new Eve.Skills.CharacterSkill(s.Skill, s.Level, 0)));
        await transaction.CommitAsync();
    }

    [PgFact]
    public async Task A_launcher_is_started_with_the_missile_its_charge_describes_after_the_skills_of_the_pilot()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, launcher) = await ArmedInSpaceAsync(rig, loaded: true);
        rig.Space.AutoReloadUsers = new HashSet<long> { 34 }; // a bot: the launcher is reloaded from its hold when its rockets run out

        // the pilot asks for the effect of the charge, as the client does; the launcher runs its own
        var answer = await rig.Space.ActivateAsync(session, launcher, "missileLaunching", 980050000105, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, answer);
        var package = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.Equal((launcher, RocketLauncherI, UseMissiles, "effects.MissileDeployment", 980050000105, 1000), (package.ModuleId, package.ModuleType, package.EffectId, package.Fx, package.TargetId, package.Repeat));
        var snapshot = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!;
        var charge = snapshot.Charges.Single();
        var world = snapshot.World;
        double Six(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
        var turret = Assert.IsType<SimTurret>(package.Turret);
        Assert.Equal(new SimTurretCharge(ScourgeRocket, Slot, 37, 1, Reloads: true), turret.Charge);
        var missile = Assert.IsType<SimMissile>(turret.Missile);
        // how far it goes and for how long, from the charge
        Assert.Equal((ScourgeRocket, Six(world.Get(charge.Id, 37)), Six(world.Get(charge.Id, 281))), (missile.ChargeTypeId, missile.MaxVelocity, missile.FlightTimeMs));
        Assert.Equal(Six(missile.MaxVelocity * missile.FlightTimeMs / 1000), turret.OptimalRange);
        Assert.Equal((Six(world.Get(charge.Id, 654)), Six(world.Get(charge.Id, 653))), (missile.ExplosionRadius, missile.ExplosionVelocity));
        Assert.True(missile.MaxVelocity > 0 && missile.FlightTimeMs > 1 && missile.ExplosionRadius >= 1, string.Join(",", missile));
        // the damage is the charge's times the pilot's missile damage multiplier, and a launcher's own multiplier is not in it
        var multiplier = world.Get(character.CharacterId, 212);
        Assert.Equal(
            [Six(world.Get(charge.Id, 114) * multiplier), Six(world.Get(charge.Id, 118) * multiplier), Six(world.Get(charge.Id, 117) * multiplier), Six(world.Get(charge.Id, 116) * multiplier)],
            turret.RawDamage);
        Assert.True(turret.RawDamage.Sum() > 0, "the rocket does damage");
        // a launcher is told its cost and its cycle as it starts and stops, and no range
        var told = new[] { (launcher, 6, package.CapacitorNeed), (launcher, 51, Math.Round(package.DurationMs, 3)) };
        Assert.Equal(told, package.OnChanges.Select(c => (c.ItemId, c.AttributeId, c.Value)));
        Assert.Equal(told, package.OffChanges.Select(c => (c.ItemId, c.AttributeId, c.Value)));
    }

    [PgFact]
    public async Task A_breacher_pod_launcher_is_started_with_the_damage_over_time_its_charge_describes()
    {
        await using var rig = await Rig.CreateAsync();
        // the launcher needs its skill (SDE 86260 at level 1: requiredSkill1 182 and its level 277 in the launcher's dogma)
        var (session, character, launcher) = await ArmedInSpaceAsync(rig, loaded: true, SmallBreacherLauncher, SmallBreacherPod, Tholos, (86260, 1));

        // the pilot asks for the effect of the pod, as the client does; the launcher runs its own, and the pod's hit starts a damage over time
        var answer = await rig.Space.ActivateAsync(session, launcher, "dotMissileLaunching", 980050000105, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, answer);
        var package = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.Equal((launcher, SmallBreacherLauncher, UseMissiles), (package.ModuleId, package.ModuleType, package.EffectId));
        var snapshot = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!;
        var charge = snapshot.Charges.Single();
        var world = snapshot.World;
        var turret = Assert.IsType<SimTurret>(package.Turret);
        var missile = Assert.IsType<SimMissile>(turret.Missile);
        // the SDE's numbers: dotDuration 40 s (5735), 200 a tick at most for the small pod (5736), 0.6 % of the target's hit points a tick (5737), which the
        // pilot's skills change in dogma: Operation (86260) at level 1 takes 5 % more of the hit points (0.6 becomes 0.63, the wiki's +5 % a level)
        var percent = Math.Round(world.Get(charge.Id, 5737), 6, MidpointRounding.AwayFromZero);
        Assert.Equal((40_000.0, 200.0, 0.63), (world.Get(charge.Id, 5735), world.Get(charge.Id, 5736), percent));
        Assert.Equal(new SimDot(40_000.0, 200.0, percent), missile.Dot);
        // the pod does no damage of its own: its effect is the damage over time
        Assert.Equal(new double[] { 0, 0, 0, 0 }, turret.RawDamage);
    }

    [PgFact]
    public async Task The_launcher_itself_can_be_asked_for_by_its_own_effect_and_other_names_are_not_launchers_effects()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await ArmedInSpaceAsync(rig, loaded: true);

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, launcher, "useMissiles", 980050000105, 1000, CancellationToken.None));
        Assert.Equal(UseMissiles, Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect.EffectId);
        Assert.Equal(
            ModuleResult.Refusal("UNSUPPORTED_EFFECT", RocketLauncherI, "Rocket Launcher I"),
            await rig.Space.ActivateAsync(session, launcher, "targetAttack", 980050000105, 1000, CancellationToken.None));
    }

    [PgFact]
    public async Task A_launcher_with_nothing_in_it_is_refused_before_it_is_asked_for_a_target()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await ArmedInSpaceAsync(rig, loaded: false);
        var heard = rig.Sim.Heard.Count;

        Assert.Equal(ModuleResult.Refusal("NO_AMMO", RocketLauncherI, "Rocket Launcher I"), await rig.Space.ActivateAsync(session, launcher, "missileLaunching", 0, 1000, CancellationToken.None));
        Assert.Equal(heard, rig.Sim.Heard.Count);
    }

    [PgFact]
    public async Task A_launcher_with_rockets_needs_a_target()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, launcher) = await ArmedInSpaceAsync(rig, loaded: true);
        var heard = rig.Sim.Heard.Count;

        Assert.Equal(ModuleResult.Refusal("TARGET_REQUIRED", RocketLauncherI, "Rocket Launcher I"), await rig.Space.ActivateAsync(session, launcher, "missileLaunching", 0, 1000, CancellationToken.None));
        Assert.Equal(heard, rig.Sim.Heard.Count);
    }

    /// <summary>
    /// A Rapid Torpedo Launcher I (group 1673, not a launcher group before) is served: it runs <c>useMissiles</c>, its charge is a torpedo, and what the client plays is the torpedo deployment.
    /// </summary>
    [PgFact]
    public async Task A_rapid_torpedo_launcher_fires_a_torpedo_with_the_torpedo_deployment()
    {
        await using var rig = await Rig.CreateAsync();
        // a Rapid Torpedo Launcher fits only a dreadnought (canFitShipGroup 485, 4594) with launcher slots: a Phoenix, with its hull skills
        var (session, _, launcher) = await ArmedInSpaceAsync(rig, loaded: true, RapidTorpedoLauncherI, ScourgeTorpedo, Phoenix, (3325, 1), (3319, 5), (20314, 1), (20533, 1), (20530, 1), (3456, 1));

        var answer = await rig.Space.ActivateAsync(session, launcher, "missileLaunching", 980050000105, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, answer);
        var package = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.Equal((launcher, RapidTorpedoLauncherI, UseMissiles, "effects.TorpedoDeployment", 980050000105, 1000), (package.ModuleId, package.ModuleType, package.EffectId, package.Fx, package.TargetId, package.Repeat));
        var turret = Assert.IsType<SimTurret>(package.Turret);
        Assert.Equal(ScourgeTorpedo, Assert.IsType<SimTurretCharge>(turret.Charge).TypeId);
        var missile = Assert.IsType<SimMissile>(turret.Missile);
        Assert.Equal(ScourgeTorpedo, missile.ChargeTypeId);
        Assert.True(missile.MaxVelocity > 0 && missile.FlightTimeMs > 1, string.Join(",", missile));
    }

    /// <summary>
    /// Every group the static data gives a module <c>useMissiles</c> is either served (the launchers that fire) or refused with a reason of its own: a group in neither is
    /// the silent <c>UNSUPPORTED_EFFECT</c> this guards against. The groups are read from the catalog of the test's build, not listed here.
    /// </summary>
    [PgFact]
    public async Task Every_launcher_group_the_static_data_gives_useMissiles_is_served_or_refused_with_a_reason_of_its_own()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync() ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var useMissiles = statics.Effects.Single(effect => effect.Name.Equals(MissileEffect.LauncherEffectName, StringComparison.OrdinalIgnoreCase));
        // the modules (category 7) and the structures' modules (category 66) whose types carry useMissiles, by their group
        var moduleGroupIds = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() is 7 or 66)
            .Select(row => row.Key)
            .ToArray();
        var moduleTypes = await sde.ReadByFieldAsync("types", "groupID", moduleGroupIds);
        var groupOf = moduleTypes.ToDictionary(row => row.Key, row => row.Doc.GetProperty("groupID").GetInt64());
        var carrying = new SortedSet<long>();
        foreach (var row in await sde.ReadAsync("typeDogma", moduleTypes.Select(type => type.Key).ToArray()))
        {
            if (row.Doc.TryGetProperty("dogmaEffects", out var effects) && effects.ValueKind == System.Text.Json.JsonValueKind.Array
                && effects.EnumerateArray().Any(effect => effect.GetProperty("effectID").GetInt64() == useMissiles.Id))
            {
                carrying.Add(groupOf[row.Key]);
            }
        }
        Assert.NotEmpty(carrying);

        var served = MissileEffect.ServedLauncherGroups(statics).Concat(MissileEffect.DeployableLauncherGroups).Concat(MissileEffect.OtherServedLauncherGroups)
            .Select(group => (long)group).ToHashSet();
        var refused = MissileEffect.RefusedLauncherGroups.Keys.Select(group => (long)group).ToHashSet();
        foreach (var group in carrying)
        {
            Assert.True(served.Contains(group) || refused.Contains(group),
                $"SDE build {catalogBuild}: launcher group {group} carries useMissiles but is neither served nor refused with a reason.");
            Assert.False(served.Contains(group) && refused.Contains(group), $"SDE build {catalogBuild}: launcher group {group} is both served and refused.");
        }
        foreach (var (group, reason) in MissileEffect.RefusedLauncherGroups)
        {
            Assert.EndsWith("_LAUNCHER_NOT_IMPLEMENTED", reason, StringComparison.Ordinal);
            Assert.True(carrying.Contains(group), $"SDE build {catalogBuild}: refused group {group} carries no useMissiles in this build.");
        }
    }

    /// <summary>
    /// A launcher of a group that is refused is refused with that group's reason, before its charge or a target is asked for, and it packages nothing (the defender and the bomb
    /// launchers are named: they are the ones the reference fires, and the simulation does not run yet).
    /// </summary>
    [PgFact]
    public async Task A_launcher_of_a_refused_group_is_refused_with_its_own_reason_before_a_target_is_asked_for()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var useMissiles = statics.Effects.Single(effect => effect.Name.Equals(MissileEffect.LauncherEffectName, StringComparison.OrdinalIgnoreCase));
        var shipGroupIds = (await sde.ReadAsync("groups")).Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 6).Select(row => row.Key).ToArray();
        var shipTypeId = checked((int)(await sde.ReadByFieldAsync("types", "groupID", shipGroupIds)).First(row => row.Doc.TryGetProperty("published", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True).Key);
        await statics.EnsureTypesAsync([shipTypeId]);

        foreach (var (group, expected) in MissileEffect.RefusedLauncherGroups)
        {
            var moduleTypeId = checked((int)(await sde.ReadByFieldAsync("types", "groupID", [group])).Select(row => row.Key).OrderBy(id => id).First());
            await statics.EnsureTypesAsync([moduleTypeId]);
            Assert.True(statics.Type(moduleTypeId)?.EffectIds.Contains(useMissiles.Id) == true, $"group {group}'s type {moduleTypeId} does not carry useMissiles.");
            const long characterId = 74001, shipId = 74002, moduleId = 74003;
            var ship = new Eve.Inventory.Item(shipId, shipTypeId, characterId, 60003760, 0, 1, true, null);
            var module = new Eve.Inventory.Item(moduleId, moduleTypeId, characterId, shipId, 11, 1, true, null);
            var world = new Eve.Dogma.DogmaWorld(statics, characterId, shipId);
            world.Add(new Eve.Dogma.DogmaItem(shipId, shipTypeId, characterId, ship.LocationId, ship.Flag, Eve.Dogma.ItemState.Online));
            world.Add(new Eve.Dogma.DogmaItem(moduleId, moduleTypeId, characterId, shipId, 11, Eve.Dogma.ItemState.Online));
            var snapshot = new Eve.Ships.ShipSnapshot { Character = null!, Ship = ship, Fitted = [module], Charges = [], Skills = [], World = world };

            foreach (var target in new long[] { 0, 980050000105 })
            {
                var result = Eve.Services.Space.ModulePackages.Of(snapshot, statics, moduleId, MissileEffect.LauncherEffectName, target, 1);
                Assert.Null(result.Effect);
                Assert.Equal(expected, result.Reason);
            }
            // the client asks for the firing effect of the charge, and a refused launcher is refused the same way
            Assert.Equal(expected, Eve.Services.Space.ModulePackages.Of(snapshot, statics, moduleId, "missileLaunching", 980050000105, 1).Reason);
        }
    }
}
