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
    private const int GravitationalOscillator = 40634;
    private const int JudgmentDoomsday = 24550;
    private const int StrontiumClathrates = 16275;
    /// <summary>The effect ids of the static data: superWeaponAmarr (the Judgment's) and doomsdayHOG (the Gravitational Oscillator's).</summary>
    private const long SuperWeaponAmarrEffect = 4489;
    private const long DoomsdayHogEffect = 6474;
    private const long DoomsdayConeEffect = 6473;

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
    public async Task A_hog_is_refused_by_name_through_the_module_order_and_takes_no_fuel_and_sends_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: GravitationalOscillator);
        await PutStrontiumAsync(rig, flying, 2000);

        var result = await rig.Space.ActivateAsync(flying.Session, flying.Module, "doomsdayHOG", 0, 1, CancellationToken.None);

        Assert.Equal("SUPERWEAPON_HOG_NOT_IMPLEMENTED", result?.Reason);
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        Assert.Equal(2000, await QuantityAsync(rig, flying));
    }

    [PgFact]
    public async Task A_point_targeted_doomsday_through_superWeaponMgr_is_refused_with_the_user_error_that_names_it()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: JudgmentDoomsday);
        var route = new SuperWeaponService(rig.Space);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.ActivatePointTargetedAsync(flying.Session, flying.Module, SuperWeaponAmarrEffect, Point(0.6, 0, 0.8), CancellationToken.None).AsTask());

        Assert.Equal("'Judgment' Electromagnetic Doomsday cannot be activated.", NotifyText(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
    }

    [PgFact]
    public async Task A_hog_through_superWeaponMgr_is_refused_with_the_user_error_that_names_it_and_takes_no_fuel()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: GravitationalOscillator);
        await PutStrontiumAsync(rig, flying, 2000);
        var route = new SuperWeaponService(rig.Space);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.ActivatePointTargetedAsync(flying.Session, flying.Module, DoomsdayHogEffect, Point(0.6, 0, 0.8), CancellationToken.None).AsTask());

        Assert.Equal("Gravitational Transportation Field Oscillator cannot be activated.", NotifyText(error));
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        Assert.Equal(2000, await QuantityAsync(rig, flying));
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

    private static async Task PutStrontiumAsync(Rig rig, DroneHostTests.Flying flying, long quantity)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction,
            new NewItem(StrontiumClathrates, flying.Character, ShipIdOf(flying), CharacterShips.CargoFlag, quantity));
        await transaction.CommitAsync();
    }

    /// <summary>The units of strontium the ship holds in its cargo.</summary>
    private static async Task<long> QuantityAsync(Rig rig, DroneHostTests.Flying flying) =>
        (await rig.Items.InLocationAsync(ShipIdOf(flying), flying.Character))
            .Where(item => item.TypeId == StrontiumClathrates && item.Flag == CharacterShips.CargoFlag)
            .Sum(item => item.Quantity);

    private static long ShipIdOf(DroneHostTests.Flying flying) => flying.Session.GetInt("shipid")!.Value;
}
