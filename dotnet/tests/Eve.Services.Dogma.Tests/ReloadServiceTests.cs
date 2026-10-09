using Eve.Characters;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Services.Ships;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// Ammunition in space as the client is told it (the recording <c>reload.json</c>): a load is told at once as a wait until a moment, the rounds move when the reload time has run
/// and the client is told what changed, last that the module is ready; an unload takes no time.
/// </summary>
public class ReloadServiceTests
{
    private const long System = 30100032;
    private const int RailgunI = 10678, Antimatter = 222, Slot = 28, Cargo = 5, NextActivationTime = 2729, QuantityCharge = 805;

    private sealed record Flight(Rig Rig, Created Character, ClientSession Session, string Oid, long Gun, long Stack) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Rig.DisposeAsync();
    }

    private static async Task<long> PutAsync(Rig rig, long owner, long place, int flag, int type, long quantity)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction, new NewItem(type, owner, place, flag, quantity));
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>A pilot in space whose slot 28 holds a 125mm Railgun I (online), with 200 antimatter in the hold, bound to dogma.</summary>
    private static async Task<Flight> InSpaceAsync(long inGun = 0)
    {
        var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, c.ShipId + 3, c.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var gun = (await rig.Ships.GrantAsync(c.CharacterId, RailgunI, 1, singleton: true))!.Single().After.Id;
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, gun, Rig.Station, c.ShipId, Slot, 1));
        var stack = await PutAsync(rig, c.CharacterId, c.ShipId, Cargo, Antimatter, 200);
        if (inGun > 0)
        {
            await PutAsync(rig, c.CharacterId, c.ShipId, Slot, Antimatter, inGun);
        }
        Assert.NotNull((await rig.Ships.UndockAsync(c.CharacterId)).Moved);
        var session = Rig.Session(c.CharacterId, station: null);
        session.Set("solarsystemid", new PyInt(System));
        return new Flight(rig, c, session, await rig.BindInSpaceAsync(session, System), gun, stack);
    }

    private static Task<PyValue> LoadAsync(Flight f) =>
        f.Rig.CallAsync(f.Session, "LoadAmmo", f.Oid, new PyLong(f.Character.ShipId), new PyList([new PyLong(f.Gun)]), new PyList([new PyLong(f.Stack)]), new PyLong(f.Character.ShipId));

    private static async Task<long> HoldAsync(Flight f) =>
        (await f.Rig.Items.InLocationAsync(f.Character.ShipId, f.Character.CharacterId)).Where(i => i.TypeId == Antimatter && i.Flag == Cargo).Sum(i => i.Quantity);

    private static async Task<long?> InGunAsync(Flight f) =>
        (await f.Rig.Items.InLocationAsync(f.Character.ShipId, f.Character.CharacterId)).SingleOrDefault(i => i.Flag == Slot && i.TypeId == Antimatter)?.Quantity;

    /// <summary>The attributes an <c>OnModuleAttributeChanges</c> carries: <c>(item, attribute, new, old)</c> of each line.</summary>
    private static (PyValue Item, long Attribute, PyValue New, PyValue Old)[] LinesOf(Events.Event e) =>
        [.. ((PyList)e.Args[0]).Items.Select(line => ((PyTuple)line).Items).Select(t => (t[2], Events.Number(t[3]), t[5], t[6]))];

    private static async Task<IReadOnlyList<Events.Event>> UntilAsync(ClientSession session, int count)
    {
        var heard = new List<Events.Event>();
        for (var i = 0; i < 200 && heard.Count < count; i++)
        {
            heard.AddRange(Events.Of(session));
            if (heard.Count < count)
            {
                await Task.Delay(25);
            }
        }
        return heard;
    }

    [PgFact]
    public async Task A_load_in_space_is_told_as_a_wait_until_a_moment_and_nothing_moves_until_the_time_has_run()
    {
        await using var f = await InSpaceAsync();

        await LoadAsync(f);
        Assert.True(f.Rig.Timers.IsSet($"dogma.reload:{f.Gun}"));

        // the module is ready at a moment (its next activation time), then a charge is being loaded for the module's reload time
        var told = Events.Of(f.Session);
        Assert.Equal(["OnModuleAttributeChanges", "OnChargeBeingLoadedToModule"], told.Select(e => e.Name));
        var wait = Assert.Single(LinesOf(told[0]));
        var ready = Py.FileTimeFromUnixMs(Rig.ClockMs + 5000);
        Assert.Equal((f.Gun, (long)NextActivationTime, ready, 0L), (Events.Number(wait.Item), wait.Attribute, Events.Number(wait.New), Events.Number(wait.Old)));
        Assert.Equal([f.Gun, Antimatter, 5000], [.. ((PyList)told[1].Args[0]).Items.Select(Events.Number), Events.Number(told[1].Args[1]), Events.Number(told[1].Args[2])]);
        Assert.Equal([TimeSpan.FromSeconds(5)], f.Rig.Delay.Asked);
        Assert.Equal((200L, (long?)null), (await HoldAsync(f), await InGunAsync(f)));
        // a gun that is waiting for a reload goes on waiting for it
        await LoadAsync(f);
        Assert.Empty(Events.Of(f.Session));
        Assert.Single(f.Rig.Delay.Asked);
    }

    [PgFact]
    public async Task When_the_time_has_run_the_rounds_move_and_the_client_is_told_in_the_order_of_the_recording_with_the_ready_moment_last()
    {
        await using var f = await InSpaceAsync();
        await LoadAsync(f);
        Events.Of(f.Session);

        f.Rig.Delay.Release();
        var told = await UntilAsync(f.Session, 5);

        Assert.Equal(["OnItemsChanged", "OnGodmaPrimeItem", "OnModuleAttributeChanges", "OnModuleAttributeChanges", "OnModuleAttributeChanges"], told.Select(e => e.Name));
        Assert.Equal((80L, 120L), (await InGunAsync(f) ?? 0, await HoldAsync(f)));
        // the hold's stack got smaller, and the batch is for the character with the hold named
        Assert.Equal("charid", told[0].IdType);
        Assert.Equal(["Ship", "ShipCargo"], [JsSemantics.StringValue(((PyTuple)told[0].Args[2]).Items[0])!, JsSemantics.StringValue(((PyTuple)told[0].Args[2]).Items[2])!]);
        // the rounds in the slot: the charge is told by (ship, slot, kind), 0 to 80
        var rounds = Assert.Single(LinesOf(told[2]));
        Assert.Equal(((long)QuantityCharge, 80L, 0L), (rounds.Attribute, Events.Number(rounds.New), Events.Number(rounds.Old)));
        // the range of the gun with the antimatter in it, and last that the module is ready (no reload is waiting for it)
        Assert.Contains(LinesOf(told[3]), l => l.Attribute == 54);
        var ready = Assert.Single(LinesOf(told[4]));
        Assert.Equal(((long)NextActivationTime, Py.FileTimeFromUnixMs(Rig.ClockMs + 5000), 0L), (ready.Attribute, Events.Number(ready.Old), Events.Number(ready.New)));
        // a gun that does not run is not asked of the simulation but through the lane: it is answered that it does not
        Assert.Equal([$"{f.Gun} {Antimatter} Add {{ Capacity = 80, Available = 200 }}"], f.Rig.Rounds.Asked);
    }

    [PgFact]
    public async Task A_gun_that_runs_is_given_what_the_simulation_added_and_the_rounds_the_hold_was_short_of_are_given_back()
    {
        await using var f = await InSpaceAsync(inGun: 3);
        await using var poor = await InSpaceAsync(inGun: 3);
        // the hold of this one has 200: the simulation added 77 and it was given them
        f.Rig.Rounds.Answer = new GunRounds(true, 3, 80);
        await LoadAsync(f);
        Events.Of(f.Session);
        f.Rig.Delay.Release();
        await UntilAsync(f.Session, 4);
        Assert.Equal((80L, 123L), (await InGunAsync(f) ?? 0, await HoldAsync(f)));
        Assert.DoesNotContain(f.Rig.Rounds.Asked, a => a.StartsWith("give back"));

        // this one has 200 too, but the simulation added 397: it was given the 200 there were and 197 are given back
        poor.Rig.Rounds.Answer = new GunRounds(true, 3, 400);
        await LoadAsync(poor);
        Events.Of(poor.Session);
        poor.Rig.Delay.Release();
        await UntilAsync(poor.Session, 4);
        Assert.Equal((203L, 0L), (await InGunAsync(poor) ?? 0, await HoldAsync(poor)));
        Assert.Contains($"give back {poor.Gun} 197", poor.Rig.Rounds.Asked);
    }

    [PgFact]
    public async Task An_unload_in_space_takes_no_time_and_tells_the_stack_of_the_hold_first_and_then_the_rounds_in_the_slot()
    {
        await using var f = await InSpaceAsync(inGun: 80);

        await f.Rig.CallAsync(f.Session, "UnloadAmmo", f.Oid, new PyLong(f.Character.ShipId), new PyList([new PyLong(f.Gun)]), new PyLong(f.Character.ShipId), new PyInt(75));

        var told = Events.Of(f.Session);
        Assert.Equal(["OnItemsChanged", "OnModuleAttributeChanges"], told.Select(e => e.Name));
        var rounds = Assert.Single(LinesOf(told[1]));
        Assert.Equal(((long)QuantityCharge, 5L, 80L), (rounds.Attribute, Events.Number(rounds.New), Events.Number(rounds.Old)));
        Assert.Equal((5L, 275L), (await InGunAsync(f) ?? 0, await HoldAsync(f)));
        Assert.Empty(f.Rig.Delay.Asked);
        Assert.Equal([$"{f.Gun} 0 Remove {{ Quantity = 75 }}"], f.Rig.Rounds.Asked);
    }

    /// <summary>Waits until the condition holds (the dry-gun handler runs apart from the test, as the link's answers do).</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
    }

    [PgFact]
    public async Task A_gun_that_runs_dry_is_loaded_from_the_hold_when_the_reload_time_has_run_and_the_simulation_is_answered_with_the_rounds()
    {
        await using var f = await InSpaceAsync();

        // the simulation says the gun has no round for its next cycle: the load starts as a load in space does, and waits for the reload time
        f.Rig.Rounds.RunDry(new SpentGun(f.Session, f.Character.CharacterId, f.Character.ShipId, f.Gun, Antimatter));
        await WaitForAsync(() => f.Rig.Timers.IsSet($"dogma.reload:{f.Gun}"));
        Assert.True(f.Rig.Timers.IsSet($"dogma.reload:{f.Gun}"));
        Assert.Empty(f.Rig.Rounds.Asked);
        Assert.Equal((200L, (long?)null), (await HoldAsync(f), await InGunAsync(f)));

        // when the time has run the rounds are asked of the simulation (the gun takes what fits) and the hold gives them
        f.Rig.Delay.Release();
        await WaitForAsync(() => f.Rig.Rounds.Asked.Count > 0);
        Assert.Contains(f.Rig.Rounds.Asked, asked => asked.StartsWith($"{f.Gun} {Antimatter} Add"));
    }

    [PgFact]
    public async Task A_gun_that_runs_dry_with_nothing_of_its_kind_to_load_is_answered_with_no_rounds_so_that_it_stops()
    {
        await using var f = await InSpaceAsync();

        // a module that is not fitted has nothing to load: the simulation is answered with none, and never waits on the host
        const long NotFitted = 999;
        f.Rig.Rounds.RunDry(new SpentGun(f.Session, f.Character.CharacterId, f.Character.ShipId, NotFitted, Antimatter));
        await WaitForAsync(() => f.Rig.Rounds.Asked.Count > 0);
        Assert.Equal([$"{NotFitted} 0 Add {{ Capacity = 0, Available = 0 }}"], f.Rig.Rounds.Asked);
        Assert.False(f.Rig.Timers.IsSet($"dogma.reload:{NotFitted}"));
    }
}
