using Eve.Inventory;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>A gun that takes charges: what the host tells the simulation to fire (the damage of the charge, its rounds), the refusal of an empty gun, and the rounds the simulation says were spent.</summary>
public class AmmoHostTests
{
    private const int RailgunI = 10678, Antimatter = 222, Slot = 28;

    /// <summary>A pilot in space whose slot 28 holds a 125mm Railgun I (online), loaded with antimatter when <paramref name="loaded"/>.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long Gun)> ArmedInSpaceAsync(Rig rig, bool loaded)
    {
        var character = await rig.NewCharacterAsync();
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, character.ShipId + 3, character.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var granted = (await rig.Ships.GrantAsync(character.CharacterId, RailgunI, 1, singleton: true))!.Single().After;
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, Slot, 1));
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, granted.Id, true));
        if (loaded)
        {
            var ammunition = (await rig.Ships.GrantAsync(character.CharacterId, Antimatter, 500, singleton: false))!.Single().After;
            Assert.IsType<Eve.Ships.AmmoChanged>(await rig.Ships.LoadAmmoAsync(character.CharacterId, character.ShipId, [granted.Id], [ammunition.Id], Rig.Station));
        }
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (session, character, granted.Id);
    }

    [PgFact]
    public async Task A_loaded_gun_is_started_with_its_charge_its_rounds_and_the_damage_of_the_charge()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, gun) = await ArmedInSpaceAsync(rig, loaded: true);

        var answer = await rig.Space.ActivateAsync(session, gun, "projectileFired", 980050000105, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, answer);
        var turret = Assert.IsType<SimTurret>(Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect.Turret);
        Assert.Equal(new SimTurretCharge(Antimatter, Slot, 80, 1), turret.Charge);
        // the shot is the damage of the charge (antimatter: 7 kinetic, 5 thermal) times the damage multiplier of the gun, none of it the gun's own
        var snapshot = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!;
        var multiplier = snapshot.World.Get(gun, 64);
        Assert.Equal(Math.Round(5 * multiplier, 6), turret.RawDamage[1], 6);
        Assert.Equal(Math.Round(7 * multiplier, 6), turret.RawDamage[2], 6);
        Assert.True(turret.RawDamage[1] > 0 && turret.RawDamage[2] > 0 && turret.RawDamage[0] == 0 && turret.RawDamage[3] == 0, string.Join(",", turret.RawDamage));
        // antimatter halves the optimal range: the one the simulation is given and the client is told is the loaded gun's
        var told = rig.Sim.Heard.OfType<SimActivate>().Last().Effect.OnChanges.Single(c => c.AttributeId == 54);
        Assert.Equal(turret.OptimalRange, told.Value);
        Assert.Equal(snapshot.World.Get(gun, 54), turret.OptimalRange, 3);
    }

    [PgFact]
    public async Task A_gun_with_nothing_in_it_is_refused_before_it_is_asked_for_a_target()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, gun) = await ArmedInSpaceAsync(rig, loaded: false);
        var heard = rig.Sim.Heard.Count;

        var answer = await rig.Space.ActivateAsync(session, gun, "projectileFired", 0, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Refusal("NO_AMMO", RailgunI, "125mm Railgun I"), answer);
        Assert.Equal(heard, rig.Sim.Heard.Count);
    }

    [PgFact]
    public async Task The_rounds_the_simulation_says_are_spent_are_kept_and_the_last_one_takes_the_charge_away()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, gun) = await ArmedInSpaceAsync(rig, loaded: true);

        async Task<long?> RoundsAsync() => (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.Charges.SingleOrDefault()?.Quantity;
        async Task WaitForAsync(long? expected)
        {
            for (var i = 0; i < 100 && await RoundsAsync() != expected; i++)
            {
                await Task.Delay(50);
            }
            Assert.Equal(expected, await RoundsAsync());
        }

        rig.Sim.Say(new SimChargeSpent((ulong)session.SessionId, gun, 79));
        await WaitForAsync(79);
        rig.Sim.Say(new SimChargeSpent((ulong)session.SessionId, gun, 0));
        await WaitForAsync(null);
    }
}
