using Eve.Inventory;
using Eve.Ships;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The superweapons through the host (docs/project/TODO.md section 12): a cone, a HOG, a titan doomsday, a reaper and an unknown effect are refused by name, both
/// through the module order and through the <c>superWeaponMgr</c> route, take no fuel from the ship and send nothing to the simulation.
/// </summary>
public class SuperWeaponHostTests
{
    private const int BosonicFieldGenerator = 40633;
    private const int DivineHarvestReaper = 40632;
    private const int GravitationalOscillator = 40634;
    private const int JudgmentDoomsday = 24550;
    private const int StrontiumClathrates = 16275;
    /// <summary>The effect ids of the static data: superWeaponAmarr (the Judgment's) and doomsdayHOG (the Gravitational Oscillator's).</summary>
    private const long SuperWeaponAmarrEffect = 4489;
    private const long DoomsdayHogEffect = 6474;
    private const long DoomsdayConeEffect = 6473;
    private const long DoomsdayBeamEffect = 6472;
    private const int HolyDestinyLance = 40631;
    private const int LanceFuel = 16274;
    /// <summary>The consumption of the lance (714): 50000 of the fuel per cycle.</summary>
    private const long LanceFuelPerCycle = 50000;
    /// <summary>LZ-6SU (30000208, security -0.17): zero security, where a lance and a cone may be used (disallowInEmpireSpace refuses low and high security).</summary>
    private const int ZeroSecSystem = 30000208;
    /// <summary>A system of low security (30000012, 0.32): the empire-space rule refuses a lance and a cone there.</summary>
    private const int LowSecSystem = 30000012;

    [PgFact]
    public async Task A_cone_is_refused_by_the_empire_space_rule_it_states_before_its_effect_is_asked_for_and_takes_no_fuel()
    {
        // the Bosonic Field Generator says disallowInEmpireSpace (1074): the pilot of a high-security system is refused by the rule the client applies
        // (ModuleRestrictions), before the effect and its refusal are looked at. Its named refusal is checked on the package (SuperWeaponEffectsTests).
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: BosonicFieldGenerator);
        await PutStrontiumAsync(rig, flying, 2000);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.Space.ActivateAsync(flying.Session, flying.Module, "doomsdayConeDOT", 0, 1, CancellationToken.None));

        Assert.Equal("CantInEmpireSpace", UserErrorName(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        Assert.Equal(2000, await QuantityAsync(rig, flying));
    }

    [PgFact]
    public async Task A_hog_aimed_at_a_point_goes_to_the_simulation_as_a_transport_field_and_its_fuel_is_taken_when_it_asks()
    {
        // the Gravitational Transportation Field Oscillator (protocol 29): its sphere, centred on the aimed point, goes to the simulation as a field; the
        // fuel is the shared mechanism's (consumption 2000 of Strontium Clathrates per cycle), taken cycle by cycle when the simulation asks
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: GravitationalOscillator);
        await PutStrontiumAsync(rig, flying, 2000);
        var route = new SuperWeaponService(rig.Space);

        var accepted = await route.ActivatePointTargetedAsync(flying.Session, flying.Module, DoomsdayHogEffect, Point(6000, 0, 8000), CancellationToken.None).AsTask();

        Assert.True(accepted);
        var activate = Assert.Single(rig.Sim.Heard.OfType<SimActivate>());
        var field = Assert.IsType<SimAction.Field>(activate.Effect.Action);
        Assert.True(field.IsValid);
        // the sphere: centred on the aim point 10 km away, radius doomsdayAOERange (10 km), one pulse at the 15 s warning
        Assert.Equal((4, 10000.0, 15000.0, 1), (field.Sphere.Shape, field.Sphere.Length, field.Sphere.WarningMs, field.Sphere.Pulses));
        Assert.Equal(10000.0, field.Sphere.Radius);
        Assert.Equal(new[] { 6000.0, 0.0, 8000.0 }, field.Destination.ToArray());
        Assert.Equal(60000.0, field.WarpBlockMs);
        // the rig has no other ship in the system: nothing is carried or held
        Assert.Empty(field.Moved);
        Assert.Empty(field.Held);
        Assert.Equal(2000, await QuantityAsync(rig, flying));
        var paid = await ModuleFuelHostTests.SettleAsync(rig, ModuleFuelHostTests.FuelCycle(flying, flying.Module, 1));
        Assert.True(paid.Accepted, paid.Reason);
        Assert.Equal(2000, paid.DeliveredUnits);
        Assert.Equal(0, await QuantityAsync(rig, flying));
    }

    [PgFact]
    public async Task A_lance_is_refused_by_the_empire_space_rule_of_high_security_and_takes_no_fuel()
    {
        // disallowInEmpireSpace (1074) is set on the lance: the client's own rule (ModuleRestrictions) refuses it in high security as in low
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: HolyDestinyLance);
        await PutFuelAsync(rig, flying, LanceFuel, LanceFuelPerCycle);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            new SuperWeaponService(rig.Space).ActivatePointTargetedAsync(flying.Session, flying.Module, DoomsdayBeamEffect, Point(0.6, 0, 0.8), CancellationToken.None).AsTask());

        Assert.Equal("CantInEmpireSpace", UserErrorName(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        Assert.Equal(LanceFuelPerCycle, await QuantityOfAsync(rig, flying, LanceFuel));
    }

    [PgFact]
    public async Task A_lance_is_refused_by_the_empire_space_rule_of_low_security_and_takes_no_fuel()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: HolyDestinyLance, solarSystemId: LowSecSystem);
        await PutFuelAsync(rig, flying, LanceFuel, LanceFuelPerCycle);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            new SuperWeaponService(rig.Space).ActivatePointTargetedAsync(flying.Session, flying.Module, DoomsdayBeamEffect, Point(0.6, 0, 0.8), CancellationToken.None).AsTask());

        Assert.Equal("CantInEmpireSpace", UserErrorName(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        Assert.Equal(LanceFuelPerCycle, await QuantityOfAsync(rig, flying, LanceFuel));
    }

    [PgFact]
    public async Task A_lance_in_zero_security_starts_through_the_host_and_its_fuel_is_taken_when_the_simulation_asks()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: HolyDestinyLance, solarSystemId: ZeroSecSystem);
        await PutFuelAsync(rig, flying, LanceFuel, LanceFuelPerCycle);
        var route = new SuperWeaponService(rig.Space);

        var accepted = await route.ActivatePointTargetedAsync(flying.Session, flying.Module, DoomsdayBeamEffect, Point(0.6, 0, 0.8), CancellationToken.None).AsTask();

        Assert.True(accepted);
        var activate = Assert.Single(rig.Sim.Heard.OfType<SimActivate>());
        var strike = Assert.IsType<SimAction.Strike>(activate.Effect.Action);
        Assert.Equal(1, strike.Shape);
        Assert.Equal((16, 2500.0), (strike.Pulses, strike.Radius));
        // the fuel is not taken by the activation: the simulation asks for it, cycle by cycle (the shared mechanism)
        Assert.Equal(LanceFuelPerCycle, await QuantityOfAsync(rig, flying, LanceFuel));
        var paid = await ModuleFuelHostTests.SettleAsync(rig, ModuleFuelHostTests.FuelCycle(flying, flying.Module, 1));
        Assert.True(paid.Accepted, paid.Reason);
        Assert.Equal(LanceFuelPerCycle, paid.DeliveredUnits);
        Assert.Equal(0, await QuantityOfAsync(rig, flying, LanceFuel));
    }

    [PgFact]
    public async Task A_cone_in_zero_security_starts_through_the_host_and_its_fuel_is_taken_when_the_simulation_asks()
    {
        // the accepted path of the Bosonic cone through the host (B2's gap): the pilot is in zero security, so the rule passes, and the strike goes to the sim
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: BosonicFieldGenerator, solarSystemId: ZeroSecSystem);
        await PutStrontiumAsync(rig, flying, 2000);
        var route = new SuperWeaponService(rig.Space);

        var accepted = await route.ActivatePointTargetedAsync(flying.Session, flying.Module, DoomsdayConeEffect, Point(0, 0, 1), CancellationToken.None).AsTask();

        Assert.True(accepted);
        var activate = Assert.Single(rig.Sim.Heard.OfType<SimActivate>());
        var strike = Assert.IsType<SimAction.Strike>(activate.Effect.Action);
        Assert.Equal(3, strike.Shape);
        Assert.Equal(21, strike.Pulses);
        Assert.Equal(2000, await QuantityAsync(rig, flying));
        var paid = await ModuleFuelHostTests.SettleAsync(rig, ModuleFuelHostTests.FuelCycle(flying, flying.Module, 1));
        Assert.True(paid.Accepted, paid.Reason);
        Assert.Equal(2000, paid.DeliveredUnits);
        Assert.Equal(0, await QuantityAsync(rig, flying));
    }

    [PgFact]
    public async Task A_titan_doomsday_aimed_at_a_point_through_superWeaponMgr_is_refused_for_want_of_a_target()
    {
        // the client never aims a titan at a point (no isPointTargeted): a point that reaches the route has no target, so it is refused by name
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: JudgmentDoomsday);
        var route = new SuperWeaponService(rig.Space);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.ActivatePointTargetedAsync(flying.Session, flying.Module, SuperWeaponAmarrEffect, Point(0.6, 0, 0.8), CancellationToken.None).AsTask());

        Assert.Equal("You need an active target to activate that module.", NotifyText(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
    }

    [PgFact]
    public async Task A_titan_doomsday_on_a_locked_target_starts_through_the_host_and_takes_its_fuel_when_the_simulation_asks()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: JudgmentDoomsday);
        await PutFuelAsync(rig, flying, LanceFuel, LanceFuelPerCycle);
        const long target = 1000000000002;

        var result = await rig.Space.ActivateAsync(flying.Session, flying.Module, "superWeaponAmarr", target, 1, CancellationToken.None);

        Assert.True(result is { Ok: true }, result?.Reason);
        var activate = Assert.Single(rig.Sim.Heard.OfType<SimActivate>());
        Assert.Equal(target, activate.Effect.TargetId);
        Assert.Equal(9000.0, Assert.IsType<SimAction.Doomsday>(activate.Effect.Action).DelayMs);
        Assert.Equal(LanceFuelPerCycle, await QuantityOfAsync(rig, flying, LanceFuel));
        var paid = await ModuleFuelHostTests.SettleAsync(rig, ModuleFuelHostTests.FuelCycle(flying, flying.Module, 1));
        Assert.True(paid.Accepted, paid.Reason);
        Assert.Equal(0, await QuantityOfAsync(rig, flying, LanceFuel));
    }

    [PgFact]
    public async Task A_titan_doomsday_without_a_target_is_refused_by_name_and_sends_nothing_and_takes_no_fuel()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: JudgmentDoomsday);
        await PutFuelAsync(rig, flying, LanceFuel, LanceFuelPerCycle);

        var result = await rig.Space.ActivateAsync(flying.Session, flying.Module, "superWeaponAmarr", 0, 1, CancellationToken.None);

        Assert.Equal("TARGET_REQUIRED", result?.Reason);
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        Assert.Equal(LanceFuelPerCycle, await QuantityOfAsync(rig, flying, LanceFuel));
    }

    [PgFact]
    public async Task A_cone_through_superWeaponMgr_reaches_the_empire_space_rule_with_its_point_and_takes_no_fuel()
    {
        // the point reaches the package (the aim is parsed from the client's tuple); the pilot is in high security, so the rule that the cone states refuses it
        // before any simulation or fuel: the route is wired to the same checks as the module order
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: BosonicFieldGenerator);
        await PutStrontiumAsync(rig, flying, 2000);
        var route = new SuperWeaponService(rig.Space);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.ActivatePointTargetedAsync(flying.Session, flying.Module, DoomsdayConeEffect, Point(0, 0, 1), CancellationToken.None).AsTask());

        Assert.Equal("CantInEmpireSpace", UserErrorName(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        Assert.Equal(2000, await QuantityAsync(rig, flying));
    }

    [PgFact]
    public async Task A_reaper_slash_through_superWeaponMgr_takes_both_points_and_is_refused_by_the_empire_rule_before_any_simulation()
    {
        // ActivateSlashModule carries the arc's two points; both reach the package (the start is the aim, the end the second point), and the reaper's
        // empire-space rule (disallowInEmpireSpace) refuses it in high security before any simulation action or fuel
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: DivineHarvestReaper);
        var route = new SuperWeaponService(rig.Space);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.ActivateSlashAsync(flying.Session, flying.Module, Point(150000, -40000, 0), Point(150000, 40000, 0), CancellationToken.None).AsTask());

        Assert.Equal("CantInEmpireSpace", UserErrorName(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
    }

    [Fact]
    public void The_client_point_is_a_tuple_of_three_numbers_and_anything_else_is_no_aim()
    {
        Assert.Equal(new double[] { 0.5, -1.0, 2.0 }, SuperWeaponService.AimOf(new PyTuple(new PyValue[] { new PyReal(0.5), new PyInt(-1), new PyReal(2.0) })) ?? []);
        Assert.Null(SuperWeaponService.AimOf(new PyTuple(new PyValue[] { new PyReal(1), new PyReal(2) })));
        Assert.Null(SuperWeaponService.AimOf(new PyStr("north")));
    }

    [PgFact]
    public async Task An_effect_id_the_static_data_does_not_have_is_refused_as_an_unsupported_effect()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: JudgmentDoomsday);
        var route = new SuperWeaponService(rig.Space);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.ActivatePointTargetedAsync(flying.Session, flying.Module, 999_999_999, Point(0.6, 0, 0.8), CancellationToken.None).AsTask());

        Assert.Equal("module cannot be activated.", NotifyText(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
    }

    /// <summary>The point the client sends: a tuple of three numbers, from the ship.</summary>
    private static PyTuple Point(double x, double y, double z) => new(new PyValue[] { new PyReal(x), new PyReal(y), new PyReal(z) });

    /// <summary>The name of a user error (the client's message key, e.g. CantInEmpireSpace).</summary>
    private static string? UserErrorName(MachoWrappedException error) =>
        error.Error.Payload is PyObjectEx { Header: PyTuple { Items: [_, PyTuple { Items: [PyStr name, _] }, ..] } } ? name.Value : null;

    /// <summary>The text a CustomNotify user error shows the client (its "notify" argument).</summary>
    private static string? NotifyText(MachoWrappedException error) =>
        error.Error.Payload is PyObjectEx { Header: PyTuple { Items: [_, PyTuple { Items: [_, PyDict arguments] }, ..] } }
            && arguments.Entries.FirstOrDefault(entry => entry.Key is PyStr { Value: "notify" }).Value is PyStr text
                ? text.Value
                : null;

    private static Task PutStrontiumAsync(Rig rig, DroneHostTests.Flying flying, long quantity) => PutFuelAsync(rig, flying, StrontiumClathrates, quantity);

    /// <summary>Puts a quantity of a fuel in the cargo of the ship.</summary>
    private static async Task PutFuelAsync(Rig rig, DroneHostTests.Flying flying, int fuelTypeId, long quantity)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction,
            new NewItem(fuelTypeId, flying.Character, ShipIdOf(flying), CharacterShips.CargoFlag, quantity));
        await transaction.CommitAsync();
    }

    /// <summary>The units of a fuel the ship holds in its cargo.</summary>
    private static async Task<long> QuantityOfAsync(Rig rig, DroneHostTests.Flying flying, int fuelTypeId) =>
        (await rig.Items.InLocationAsync(ShipIdOf(flying), flying.Character))
            .Where(item => item.TypeId == fuelTypeId && item.Flag == CharacterShips.CargoFlag)
            .Sum(item => item.Quantity);

    /// <summary>The units of strontium the ship holds in its cargo.</summary>
    private static async Task<long> QuantityAsync(Rig rig, DroneHostTests.Flying flying) =>
        (await rig.Items.InLocationAsync(ShipIdOf(flying), flying.Character))
            .Where(item => item.TypeId == StrontiumClathrates && item.Flag == CharacterShips.CargoFlag)
            .Sum(item => item.Quantity);

    private static long ShipIdOf(DroneHostTests.Flying flying) => flying.Session.GetInt("shipid")!.Value;
}
