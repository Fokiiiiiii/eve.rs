using Eve.Inventory;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A launcher that fires missiles: what the host tells the simulation to fly (the damage of the missile, its speed and flight time, what its explosion reaches), the effect the pilot
/// asks for and the one that runs, and the refusals of a launcher with nothing in it. The numbers are those of the recording of the reference (<c>oracle/fixtures/scenarios/missile.json</c>).
/// </summary>
public class MissileHostTests
{
    private const int RocketLauncherI = 10629, ScourgeRocket = 266, Slot = 28, UseMissiles = 101;

    /// <summary>A pilot in space whose slot 28 holds a Rocket Launcher I (online), loaded with Scourge Rockets when <paramref name="loaded"/>.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long Launcher)> ArmedInSpaceAsync(Rig rig, bool loaded)
    {
        var character = await rig.NewCharacterAsync();
        await TrainAsync(rig, character.CharacterId);
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, character.ShipId + 3, character.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var granted = (await rig.Ships.GrantAsync(character.CharacterId, RocketLauncherI, 1, singleton: true))!.Single().After;
        var fitted = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, Slot, 1);
        Assert.True(fitted is Eve.Ships.ShipChanged, fitted.ToString());
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, granted.Id, true));
        if (loaded)
        {
            var rockets = (await rig.Ships.GrantAsync(character.CharacterId, ScourgeRocket, 40, singleton: false))!.Single().After;
            Assert.IsType<Eve.Ships.AmmoChanged>(await rig.Ships.LoadAmmoAsync(character.CharacterId, character.ShipId, [granted.Id], [rockets.Id], Rig.Station));
        }
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (session, character, granted.Id);
    }

    /// <summary>Missile Launcher Operation and Rockets, to level 5: what is tested is not the fitting rules.</summary>
    private static async Task TrainAsync(Rig rig, long character)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Skills.GrantAsync(connection, transaction, character, new[] { 3319, 3320 }.Select(type => new Eve.Skills.CharacterSkill(type, 5, 0)));
        await transaction.CommitAsync();
    }

    [PgFact]
    public async Task A_launcher_is_started_with_the_missile_its_charge_describes_after_the_skills_of_the_pilot()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, launcher) = await ArmedInSpaceAsync(rig, loaded: true);

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
        Assert.Equal(new SimTurretCharge(ScourgeRocket, Slot, 37, 1), turret.Charge);
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
}
