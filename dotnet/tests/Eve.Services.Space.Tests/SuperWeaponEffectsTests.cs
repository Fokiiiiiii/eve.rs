using Eve.Data;
using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Space;
using Eve.Sde;
using Eve.Ships;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The superweapon family of static data group 588 (docs/project/TODO.md section 12): every activation effect of every type in the group is refused by the reason
/// its family names, before a package is built, so no simulation action, no fuel and no success. P.A.N.I.C. (42522) has its own handler and is
/// checked in IndustrialInvulnerabilityTests.
/// </summary>
public class SuperWeaponEffectsTests
{
    /// <summary>The reason each activation effect of the group is refused with. The reason names the family whose body is missing.</summary>
    private static readonly IReadOnlyDictionary<string, string> ReasonByEffect = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // the titan doomsdays have a body: without a locked target they are refused for it (their built hit is tested in A_titan...)
        ["superWeaponAmarr"] = "TARGET_REQUIRED",
        ["superWeaponCaldari"] = "TARGET_REQUIRED",
        ["superWeaponGallente"] = "TARGET_REQUIRED",
        ["superWeaponMinmatar"] = "TARGET_REQUIRED",
        // the lance has a body: without a point it is refused for the point (its aimed package is tested in A_lance...)
        ["doomsdayBeamDOT"] = "TARGET_POINT_REQUIRED",
        // the reaper has a body: without its two points it is refused for them (A_reaper...)
        ["doomsdaySlash"] = "SLASH_POINTS_REQUIRED",
        // the disruptive lance needs an active siege module on the ship; alone it is refused for the siege (A_disruptive_lance...)
        ["debuffLance"] = "ACTIVE_SIEGE_MODULE_REQUIRED",
        // the cone has a body: without a point it is refused for the point (its aimed package is tested in A_cone...)
        ["doomsdayConeDOT"] = "TARGET_POINT_REQUIRED",
        ["doomsdayHOG"] = "TARGET_POINT_REQUIRED",
    };

    /// <summary>The slot effects every module carries (power grid class and online): they are not an activation of the module.</summary>
    private static readonly HashSet<string> SlotEffects = new(StringComparer.OrdinalIgnoreCase) { "hiPower", "medPower", "lowPower", "online" };

    private const int Ship = 587;
    private const int CatmageddonType = 37474;
    private const int PanicType = 42522;

    [PgFact]
    public async Task Every_activation_of_every_group_588_type_is_refused_by_the_reason_its_family_names_and_builds_no_package()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        // the group listing keeps published types only, and Catmageddon (37474, a reaper of the family) is not published: it is added by its id
        var typeIds = (await statics.TypesInGroupsAsync(["Super Weapon"])).Select(type => type.TypeId).Append(CatmageddonType).ToArray();
        // 20 types in the group: 4 titan doomsdays, Catmageddon and 4 reapers, 4 lances, 4 disruptive lances, the Bosonic cone, the HOG and P.A.N.I.C.
        Assert.Equal(20, typeIds.Length);
        await statics.EnsureTypesAsync(typeIds);
        await statics.EnsureTypesAsync([Ship]);

        // P.A.N.I.C. (42522) is in the group but is not a family activation: it has its own handler, checked in IndustrialInvulnerabilityTests
        var types = typeIds.Where(id => id != PanicType).Select(id => statics.Type(id)!).ToArray();
        var seen = new List<string>();
        foreach (var type in types)
        {
            var activations = type.EffectIds.Select(statics.Effect).OfType<EffectDef>()
                .Where(effect => effect.Category is EffectDef.Activation or EffectDef.Target && !SlotEffects.Contains(effect.Name));
            foreach (var effect in activations)
            {
                Assert.True(ReasonByEffect.ContainsKey(effect.Name), $"type {type.TypeId} has the activation {effect.Name}, which no family names");
                var fixture = FittedModule(statics, type.TypeId);
                var result = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, effect.Name, 0, 1);
                if (effect.Name == "moduleBonusIndustrialInvulnerability")
                {
                    // the Nexus invulnerability core is built (track C6, IndustrialInvulnerabilityEffect): its activation packages
                    Assert.Null(result.Reason);
                    Assert.NotNull(result.Effect);
                }
                else
                {
                    Assert.True(result.Reason == ReasonByEffect[effect.Name], $"type {type.TypeId} {effect.Name}: refused as {result.Reason}");
                    Assert.Null(result.Effect);
                }
                seen.Add($"{type.TypeId}:{effect.Name}");
            }
        }
        // one activation per type: no type of the group is left out of the check
        Assert.True(types.Length == seen.Count, $"{seen.Count} activations checked for {types.Length} types: {string.Join(", ", seen)}");
    }

    [PgFact]
    public async Task A_reaper_is_a_sweep_of_its_two_points_on_the_fixed_arc_and_refused_off_it()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Ship, 40632]);

        // the Divine Harvest (40632): the client's arc has its centre maxRange (150 km) ahead and a half span of tan(15 degrees) * maxRange, perpendicular
        // to it. The cylinder (doomsdayDamageRadius 5000) sweeps from the start point to the end point over a 10 s window (11 pulses, closed at its end)
        var reaper = FittedModule(statics, 40632);
        var half = Math.Tan(15 * Math.PI / 180) * 150000.0;
        double[] start = [150000.0, -half, 0.0];
        double[] end = [150000.0, half, 0.0];
        Assert.Equal("SLASH_POINTS_REQUIRED", ModulePackages.Of(reaper.Snapshot, statics, reaper.ModuleId, "doomsdaySlash", 0, 1, start).Reason);
        var result = ModulePackages.Of(reaper.Snapshot, statics, reaper.ModuleId, "doomsdaySlash", 0, 1, start, end);
        Assert.Null(result.Reason);
        Assert.True(result.Effect!.BurnsFuel, "the fuel is the shared mechanism's (consumption 714 50000 of 713 16274)");
        var strike = Assert.IsType<SimAction.Strike>(result.Effect.Action);
        Assert.True(strike.IsValid);
        Assert.Equal(1, strike.Shape);
        Assert.Equal((5000.0, 10000.0, 1000.0, 11), (strike.Radius, strike.WarningMs, strike.PulseMs, strike.Pulses));
        // the builder rounds the endpoints to six decimals (NpcArmament.Round6), so the sweep's end is the client's point to a micrometre
        Assert.True(end.Zip(strike.SweepEnd!).All(pair => Math.Abs(pair.First - pair.Second) < 1e-3), $"sweep end {string.Join(", ", strike.SweepEnd!)}");
        Assert.Equal((35000.0, 10000.0, 20000.0), (strike.NeutAmount, strike.NeutRadius, strike.NeutSignatureRadius));
        Assert.Equal(0.0, strike.DebuffMs);

        // a slash that is not the client's arc is refused by name, and so is one with no length
        Assert.Equal("SLASH_POINTS_INVALID", ModulePackages.Of(reaper.Snapshot, statics, reaper.ModuleId, "doomsdaySlash", 0, 1, [30000.0, -5000.0, 0.0], [30000.0, 5000.0, 0.0]).Reason);
        Assert.Equal("SLASH_POINTS_INVALID", ModulePackages.Of(reaper.Snapshot, statics, reaper.ModuleId, "doomsdaySlash", 0, 1, start, start).Reason);
    }

    [PgFact]
    public async Task A_disruptive_lance_needs_an_active_siege_module_and_its_strike_carries_the_debuff()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Ship, 77398, SiegeModuleI]);

        // the Atgeir (77398) carries activationRequiresActiveSiegeModule (5426) = 1: alone, or with a siege module that is fitted but not switched on, it is refused
        var alone = FittedModules(statics, (77398, ItemState.Online));
        Assert.Equal("ACTIVE_SIEGE_MODULE_REQUIRED", ModulePackages.Of(alone.Snapshot, statics, alone.ModuleIds[0], "debuffLance", 0, 1, [1.0, 0.0, 0.0]).Reason);
        Assert.Null(ModulePackages.Of(alone.Snapshot, statics, alone.ModuleIds[0], "debuffLance", 0, 1, [1.0, 0.0, 0.0]).Effect);
        var offline = FittedModules(statics, (77398, ItemState.Online), (SiegeModuleI, ItemState.Online));
        Assert.Equal("ACTIVE_SIEGE_MODULE_REQUIRED", ModulePackages.Of(offline.Snapshot, statics, offline.ModuleIds[0], "debuffLance", 0, 1, [1.0, 0.0, 0.0]).Reason);

        // with the siege module switched on, the lance builds its strike: the static data's cylinder, warm-up, debuff and windows
        var sieged = FittedModules(statics, (77398, ItemState.Online), (SiegeModuleI, ItemState.Active));
        var result = ModulePackages.Of(sieged.Snapshot, statics, sieged.ModuleIds[0], "debuffLance", 0, 1, [1.0, 0.0, 0.0]);
        Assert.Null(result.Reason);
        var strike = Assert.IsType<SimAction.Strike>(result.Effect!.Action);
        Assert.True(strike.IsValid);
        Assert.Equal(1, strike.Shape);
        Assert.Equal((2500.0, 15000.0, 1000.0, 16), (strike.Radius, strike.WarningMs, strike.PulseMs, strike.Pulses));
        Assert.Equal((30000.0, 120000.0, 60000.0), (strike.ImmobileMs, strike.NoJumpMs, strike.DebuffMs));
    }

    [PgFact]
    public async Task A_fitted_disruptive_lance_disallows_the_cloak_while_it_is_fitted()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Ship, 77398]);
        var cloak = (await statics.TypesInGroupsAsync(["Cloaking Device"]))
            .First(type => type.EffectIds.Select(statics.Effect).Any(effect => effect is { Name: "cloaking" or "cloakingWarpSafe" or "cloakingPrototype" }));
        await statics.EnsureTypesAsync([cloak.TypeId]);
        var canCloak = statics.AttributeId("canCloak") ?? throw new InvalidOperationException("The SDE is missing canCloak.");
        var cloakEffect = cloak.EffectIds.Select(statics.Effect).First(effect => effect is { Name: "cloaking" or "cloakingWarpSafe" or "cloakingPrototype" })!.Name;

        var bare = FittedModules(statics, (cloak.TypeId, ItemState.Online));
        bare.Snapshot.World.SetValue(ShipItemId, canCloak, 1);
        Assert.Null(ModulePackages.Of(bare.Snapshot, statics, bare.ModuleIds[0], cloakEffect, 0, 1).Reason);

        // the same ship with a disruptive lance fitted (switched off): the cloak is refused, whatever the lance's state
        var withLance = FittedModules(statics, (cloak.TypeId, ItemState.Online), (77398, ItemState.Offline));
        withLance.Snapshot.World.SetValue(ShipItemId, canCloak, 1);
        Assert.Equal("CLOAK_DISALLOWED", ModulePackages.Of(withLance.Snapshot, statics, withLance.ModuleIds[0], cloakEffect, 0, 1).Reason);
    }

    [PgFact]
    public async Task A_hog_builds_a_transport_field_from_its_static_data_when_aimed_and_a_cone_is_built_from_its_data()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Ship, 40633, 40634]);

        // the Gravitational Transportation Field Oscillator (consumption 2000): unaimed it is refused for the point; aimed, it builds a transport field
        Assert.Equal(2000, statics.Type(40634)!.Attributes[ModuleAttributes.ConsumptionQuantity]);
        var hog = FittedModule(statics, 40634);
        Assert.Equal("TARGET_POINT_REQUIRED", ModulePackages.Of(hog.Snapshot, statics, hog.ModuleId, "doomsdayHOG", 0, 1).Reason);
        var hogResult = ModulePackages.Of(hog.Snapshot, statics, hog.ModuleId, "doomsdayHOG", 0, 1, [6000.0, 0.0, 8000.0]);
        Assert.Null(hogResult.Reason);
        Assert.True(hogResult.Effect!.BurnsFuel);
        var field = Assert.IsType<SimAction.Field>(hogResult.Effect.Action);
        Assert.True(field.IsValid);
        Assert.Equal((4, 10000.0, 10000.0, 15000.0), (field.Sphere.Shape, field.Sphere.Length, field.Sphere.Radius, field.Sphere.WarningMs));
        Assert.Equal((30000.0, 300000.0, 30000.0), (field.Sphere.ImmobileMs, field.Sphere.NoJumpMs, field.Sphere.NeutAmount));
        Assert.Equal((10000.0, 20000.0), (field.Sphere.NeutRadius, field.Sphere.NeutSignatureRadius));
        Assert.Equal(new[] { 6000.0, 0.0, 8000.0 }, field.Destination.ToArray());
        Assert.Equal(60000.0, field.WarpBlockMs);
        Assert.Empty(field.Moved);
        Assert.Empty(field.Held);

        // the host's list of the system: the capsuleer ship that is not a capital is carried, the capital is held
        var listed = ModulePackages.Of(hog.Snapshot.WithNeighbours([new TransportNeighbour(91, false), new TransportNeighbour(92, true)]), statics, hog.ModuleId, "doomsdayHOG", 0, 1, [6000.0, 0.0, 8000.0]);
        var listedField = Assert.IsType<SimAction.Field>(listed.Effect!.Action);
        Assert.Equal(new long[] { 91 }, listedField.Moved.ToArray());
        Assert.Equal(new long[] { 92 }, listedField.Held.ToArray());

        // the structure variant (category 66, 47068): module and wiring only, refused by name, never a success
        await statics.EnsureTypesAsync([47068]);
        var structure = FittedModule(statics, 47068);
        var structureResult = ModulePackages.Of(structure.Snapshot, statics, structure.ModuleId, "doomsdayHOG", 0, 1, [6000.0, 0.0, 8000.0]);
        Assert.Equal("STRUCTURE_SUPERWEAPON_NOT_IMPLEMENTED", structureResult.Reason);
        Assert.Null(structureResult.Effect);

        // the Bosonic Field Generator (713 16275 x 714 2000): aimed, it builds the strike of its data, and the fuel is the shared mechanism's
        Assert.Equal(2000, statics.Type(40633)!.Attributes[ModuleAttributes.ConsumptionQuantity]);
        var cone = FittedModule(statics, 40633);
        var result = ModulePackages.Of(cone.Snapshot, statics, cone.ModuleId, "doomsdayConeDOT", 0, 1, [3.0, 0.0, 4.0]);
        Assert.Null(result.Reason);
        Assert.True(result.Effect!.BurnsFuel);
        var strike = Assert.IsType<SimAction.Strike>(result.Effect.Action);
        Assert.True(strike.IsValid);
        Assert.Equal(3, strike.Shape);
        Assert.Equal([0.6, 0.0, 0.8], strike.Direction);
        // the SDE's numbers (checked in attrs of 40633): range 30000, warning 10000 ms, cycle 1000 ms, damage 20000 ms (21 pulses, the window closed at its end),
        // em 114, thermal 118, kinetic 117, explosive 116 at 10000 each, signature 552 (10000), immobility 30000 ms, no jump or cloak 300000 ms, energy
        // neutralisation 30000 within 10000 m against signature 20000
        Assert.Equal((30000.0, 10000.0, 1000.0, 21), (strike.Length, strike.WarningMs, strike.PulseMs, strike.Pulses));
        Assert.Equal([10000.0, 10000.0, 10000.0, 10000.0], strike.Damage);
        Assert.Equal((10000.0, 30000.0, 300000.0), (strike.SignatureRadius, strike.ImmobileMs, strike.NoJumpMs));
        Assert.Equal((30000.0, 10000.0, 20000.0), (strike.NeutAmount, strike.NeutRadius, strike.NeutSignatureRadius));
        Assert.Equal(0, strike.Radius);
    }

    [PgFact]
    public async Task A_lance_is_built_from_its_static_data_when_aimed_and_is_refused_without_a_point()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Ship, 40631, 41439, 41440, 41441]);

        // the Holy Destiny (40631): a cylinder (doomsdayAOEShape 1) of 2500 m along maxRange 200 km, pulses from a warning of 15 s every second through a
        // 15 s window (closed at its end: 16 pulses), 50000 EM each, signature 5000, the immobility and no-jump windows, and the energy neutralisation
        var lance = FittedModule(statics, 40631);
        Assert.Equal("TARGET_POINT_REQUIRED", ModulePackages.Of(lance.Snapshot, statics, lance.ModuleId, "doomsdayBeamDOT", 0, 1).Reason);
        var result = ModulePackages.Of(lance.Snapshot, statics, lance.ModuleId, "doomsdayBeamDOT", 0, 1, [0.6, 0.0, 0.8]);
        Assert.Null(result.Reason);
        Assert.True(result.Effect!.BurnsFuel, "the fuel is the shared mechanism's (consumption 713 16274 x 714 50000)");
        var strike = Assert.IsType<SimAction.Strike>(result.Effect.Action);
        Assert.True(strike.IsValid);
        Assert.Equal(1, strike.Shape);
        Assert.Equal([0.6, 0.0, 0.8], strike.Direction);
        Assert.Equal((200000.0, 2500.0, 15000.0, 1000.0, 16), (strike.Length, strike.Radius, strike.WarningMs, strike.PulseMs, strike.Pulses));
        Assert.Equal([50000.0, 0.0, 0.0, 0.0], strike.Damage);
        Assert.Equal((5000.0, 30000.0, 300000.0), (strike.SignatureRadius, strike.ImmobileMs, strike.NoJumpMs));
        Assert.Equal((35000.0, 10000.0, 20000.0), (strike.NeutAmount, strike.NeutRadius, strike.NeutSignatureRadius));

        // the racial lances are the same effect with their own numbers: each builds a valid cylinder
        foreach (var racial in new[] { 41439, 41440, 41441 })
        {
            var fixture = FittedModule(statics, racial);
            var built = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, "doomsdayBeamDOT", 0, 1, [0.6, 0.0, 0.8]);
            Assert.Null(built.Reason);
            Assert.Equal(1, Assert.IsType<SimAction.Strike>(built.Effect!.Action).Shape);
        }
    }

    [PgFact]
    public async Task A_titan_doomsday_is_built_as_one_hit_on_its_target_after_its_delay_with_its_one_damage_kind()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        // the Judgment (24550, EM 2 400 000, damageDelayDuration 9000, immobility 30 s, no jump or cloak 300 s) and the Gjallarhorn (23674, explosive, 7500 ms)
        await statics.EnsureTypesAsync([Ship, 24550, 23674]);
        const long target = 1000000000002;

        var judgment = FittedModule(statics, 24550);
        Assert.Equal("TARGET_REQUIRED", ModulePackages.Of(judgment.Snapshot, statics, judgment.ModuleId, "superWeaponAmarr", 0, 1).Reason);
        var result = ModulePackages.Of(judgment.Snapshot, statics, judgment.ModuleId, "superWeaponAmarr", target, 1);
        Assert.Null(result.Reason);
        Assert.True(result.Effect!.BurnsFuel, "the fuel is the shared mechanism's (consumption 713 16274 x 714 50000)");
        Assert.Equal(target, result.Effect.TargetId);
        var hit = Assert.IsType<SimAction.Doomsday>(result.Effect.Action);
        Assert.True(hit.IsValid);
        Assert.Equal((9000.0, 30000.0, 300000.0), (hit.DelayMs, hit.ImmobileMs, hit.NoJumpMs));
        Assert.Equal([2400000.0, 0.0, 0.0, 0.0], hit.Damage);

        var gjallarhorn = FittedModule(statics, 23674);
        var explosive = Assert.IsType<SimAction.Doomsday>(ModulePackages.Of(gjallarhorn.Snapshot, statics, gjallarhorn.ModuleId, "superWeaponMinmatar", target, 1).Effect!.Action);
        Assert.Equal(7500.0, explosive.DelayMs);
        Assert.Equal([0.0, 0.0, 0.0, 2400000.0], explosive.Damage);
    }

    /// <summary>A ship with one module fitted and online, as the host's snapshot has them (the character is not needed by these rules).</summary>
    private static (ShipSnapshot Snapshot, long ModuleId) FittedModule(DogmaStatic statics, int moduleTypeId)
    {
        const long characterId = 75001;
        const long shipId = 75002;
        const long moduleId = 75003;
        const int moduleFlag = 11;
        var ship = new Eve.Inventory.Item(shipId, Ship, characterId, 60003760, 0, 1, true, null);
        var module = new Eve.Inventory.Item(moduleId, moduleTypeId, characterId, shipId, moduleFlag, 1, true, null);
        var world = new DogmaWorld(statics, characterId, shipId);
        world.Add(new DogmaItem(shipId, Ship, characterId, ship.LocationId, ship.Flag, ItemState.Online));
        world.Add(new DogmaItem(moduleId, moduleTypeId, characterId, shipId, moduleFlag, ItemState.Online));
        var snapshot = new ShipSnapshot
        {
            Character = null!,
            Ship = ship,
            Fitted = [module],
            Charges = [],
            Skills = [],
            World = world,
        };
        return (snapshot, moduleId);
    }

    /// <summary>The Siege Module I (20280), whose <c>moduleBonusSiegeModule</c> is the active siege a disruptive lance needs.</summary>
    private const int SiegeModuleI = 20280;

    /// <summary>The ship item id of <see cref="FittedModules"/>.</summary>
    private const long ShipItemId = 75002;

    /// <summary>
    /// A ship with each module fitted in the state given. A started module is active in the snapshot, as the space host sets it (<c>FlownShipAsync</c>).
    /// </summary>
    private static (ShipSnapshot Snapshot, long[] ModuleIds) FittedModules(DogmaStatic statics, params (int TypeId, ItemState State)[] modules)
    {
        const long characterId = 75001;
        const int moduleFlag = 11;
        var ship = new Eve.Inventory.Item(ShipItemId, Ship, characterId, 60003760, 0, 1, true, null);
        var world = new DogmaWorld(statics, characterId, ShipItemId);
        world.Add(new DogmaItem(ShipItemId, Ship, characterId, ship.LocationId, ship.Flag, ItemState.Online));
        var fitted = new List<Eve.Inventory.Item>();
        var ids = new long[modules.Length];
        for (var index = 0; index < modules.Length; index++)
        {
            var moduleId = 75003 + index;
            ids[index] = moduleId;
            fitted.Add(new Eve.Inventory.Item(moduleId, modules[index].TypeId, characterId, ShipItemId, moduleFlag + index, 1, true, null));
            world.Add(new DogmaItem(moduleId, modules[index].TypeId, characterId, ShipItemId, moduleFlag + index, modules[index].State));
        }
        var snapshot = new ShipSnapshot { Character = null!, Ship = ship, Fitted = fitted, Charges = [], Skills = [], World = world };
        return (snapshot, ids);
    }
}
