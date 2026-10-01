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

    /// <summary>The writing of the rounds spent is held until the test lets it go, and what was written is kept.</summary>
    private sealed class HeldWriter(Rig rig)
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(long Module, long Rounds)> Written { get; } = [];

        public async Task WriteAsync(long character, long module, long rounds)
        {
            await Gate.Task;
            lock (Written)
            {
                Written.Add((module, rounds));
            }
            await rig.Ships.SpendChargeAsync(character, module, rounds);
        }
    }

    [PgFact]
    public async Task A_ship_that_docks_is_not_moved_before_the_rounds_its_guns_spent_are_written()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, gun) = await ArmedInSpaceAsync(rig, loaded: true);
        var writer = new HeldWriter(rig);
        rig.Space.ChargeWriter = writer.WriteAsync;

        rig.Sim.Say(new SimChargeSpent((ulong)session.SessionId, gun, 79));
        rig.Sim.Say(new SimChargeSpent((ulong)session.SessionId, gun, 78));
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        await Task.Delay(700);
        Assert.Null(session.GetInt("stationid")); // the dock waits for the rounds
        writer.Gate.SetResult();
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);

        // what the hangar shows is the last count, written once and in order
        Assert.Equal([(gun, 78L)], writer.Written.ToArray());
        var snapshot = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!;
        Assert.Equal(78, snapshot.Charges.Single().Quantity);
    }

    [PgFact]
    public async Task A_gun_is_started_with_the_rounds_it_has_left_and_not_the_rounds_it_had()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, gun) = await ArmedInSpaceAsync(rig, loaded: true);
        var writer = new HeldWriter(rig);
        rig.Space.ChargeWriter = writer.WriteAsync;
        rig.Sim.Say(new SimChargeSpent((ulong)session.SessionId, gun, 3));

        var started = rig.Space.ActivateAsync(session, gun, "projectileFired", 980050000105, 1000, CancellationToken.None);
        await Task.Delay(700);
        Assert.False(started.IsCompleted); // the order waits for the count
        writer.Gate.SetResult();
        Assert.Equal(ModuleResult.Done, await started);

        var heard = Assert.IsType<SimActivate>(rig.Sim.Heard.Last());
        Assert.Equal(3, heard.Effect.Turret!.Charge!.Rounds);
    }

    [PgFact]
    public async Task A_session_that_ends_has_its_rounds_written_before_its_ship_is_put_back_and_the_host_stopping_waits_for_them()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, gun) = await ArmedInSpaceAsync(rig, loaded: true);
        var writer = new HeldWriter(rig);
        rig.Space.ChargeWriter = writer.WriteAsync;
        rig.Sim.Say(new SimChargeSpent((ulong)session.SessionId, gun, 70));

        session.Close();
        await Task.Delay(700);
        Assert.NotEqual(ItemFlags.Hangar, (await rig.Items.GetAsync(character.ShipId))!.Flag); // not put back yet
        writer.Gate.SetResult();
        await Rig.UntilAsync(() => rig.Items.GetAsync(character.ShipId).GetAwaiter().GetResult()!.Flag == ItemFlags.Hangar);
        Assert.Equal(70, (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.Charges.Single().Quantity);

        // and when the host stops
        var other = new HeldWriter(rig);
        rig.Space.ChargeWriter = other.WriteAsync;
        var second = await rig.NewCharacterAsync("Space Second", 2);
        Assert.NotNull(second);
        rig.Space.EnqueueForTest(777, character.CharacterId, gun, 60);
        var stopping = Task.Run(rig.Space.Dispose);
        await Task.Delay(700);
        Assert.False(stopping.IsCompleted);
        other.Gate.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains((gun, 60L), other.Written);
    }

    [PgFact]
    public async Task A_change_of_the_rounds_of_a_gun_that_runs_is_made_after_the_counts_told_before_the_answer_and_before_those_told_after_it()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, gun) = await ArmedInSpaceAsync(rig, loaded: true);
        var order = new List<string>();
        // writing a count takes a while, so what the simulation tells meanwhile is waiting on the lane together
        rig.Space.ChargeWriter = async (_, _, rounds) =>
        {
            await Task.Delay(150);
            lock (order)
            {
                order.Add($"count {rounds}");
            }
        };
        // the simulation told that the gun was down to 2 rounds, answered that it has 80 now, and went on shooting
        rig.Sim.AdjustAnswer = request =>
        {
            rig.Sim.Say(new SimChargeSpent((ulong)session.SessionId, gun, 2));
            return new SimChargeAdjusted(request.RequestId, true, 2, 80);
        };
        rig.Sim.AfterAnswer = _ => rig.Sim.Say(new SimChargeSpent((ulong)session.SessionId, gun, 79));

        var done = await rig.Space.AdjustAsync(
            session, gun, Antimatter, new Eve.Services.Ships.RoundsChange.Add(80, 100),
            rounds =>
            {
                lock (order)
                {
                    order.Add($"adjust {rounds.Before} to {rounds.After}");
                }
                return Task.FromResult(7);
            },
            CancellationToken.None);

        Assert.Equal(7, done);
        var request = Assert.IsType<SimAdjustCharge>(rig.Sim.Heard.Last());
        Assert.Equal((gun, Antimatter, new SimChargeOp.Add(80, 100)), (request.ModuleId, request.ChargeType, request.Op));
        await Rig.UntilAsync(() => { lock (order) { return order.Count == 3; } });
        lock (order)
        {
            Assert.Equal(["count 2", "adjust 2 to 80", "count 79"], order);
        }
    }

    [PgFact]
    public async Task A_gun_that_does_not_run_is_answered_so_and_a_pilot_who_is_not_in_space_has_no_gun_that_runs()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, gun) = await ArmedInSpaceAsync(rig, loaded: true);

        var idle = await rig.Space.AdjustAsync(session, gun, 0, new Eve.Services.Ships.RoundsChange.Remove(0), rounds => Task.FromResult(rounds), CancellationToken.None);
        Assert.Equal(Eve.Ships.GunRounds.Idle, idle);

        var heard = rig.Sim.Heard.Count;
        var stranger = await rig.SessionAsync(await rig.NewCharacterAsync("Space Stranger", 2), 99);
        var none = await rig.Space.AdjustAsync(stranger, gun, 0, new Eve.Services.Ships.RoundsChange.Remove(5), rounds => Task.FromResult(rounds), CancellationToken.None);
        Assert.Equal(Eve.Ships.GunRounds.Idle, none);
        Assert.Equal(heard, rig.Sim.Heard.Count); // the simulation is not asked about a ship it does not have
    }

    [PgFact]
    public async Task A_simulation_that_cannot_be_reached_fails_the_change_and_the_rounds_a_hold_was_short_of_are_taken_back()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, gun) = await ArmedInSpaceAsync(rig, loaded: true);

        rig.Space.GiveBack(session, gun, 60);
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimAdjustCharge>().Any(a => a.Op is SimChargeOp.Remove { Quantity: 60 } && a.ModuleId == gun));

        rig.Sim.IsConnected = false;
        await Assert.ThrowsAsync<SimUnavailableException>(() => rig.Space.AdjustAsync(
            session, gun, Antimatter, new Eve.Services.Ships.RoundsChange.Add(80, 1), rounds => Task.FromResult(rounds), CancellationToken.None));
    }
}
