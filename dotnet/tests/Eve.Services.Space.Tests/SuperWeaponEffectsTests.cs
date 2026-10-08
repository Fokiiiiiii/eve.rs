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
/// its family names, before a package is built, so no simulation action, no fuel and no success. P.A.N.I.C. (42522) keeps its own refusal.
/// </summary>
public class SuperWeaponEffectsTests
{
    /// <summary>The reason each activation effect of the group is refused with. The reason names the family whose body is missing.</summary>
    private static readonly IReadOnlyDictionary<string, string> ReasonByEffect = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["superWeaponAmarr"] = "SUPERWEAPON_DOOMSDAY_NOT_IMPLEMENTED",
        ["superWeaponCaldari"] = "SUPERWEAPON_DOOMSDAY_NOT_IMPLEMENTED",
        ["superWeaponGallente"] = "SUPERWEAPON_DOOMSDAY_NOT_IMPLEMENTED",
        ["superWeaponMinmatar"] = "SUPERWEAPON_DOOMSDAY_NOT_IMPLEMENTED",
        ["doomsdayBeamDOT"] = "SUPERWEAPON_LANCE_NOT_IMPLEMENTED",
        ["doomsdaySlash"] = "SUPERWEAPON_REAPER_NOT_IMPLEMENTED",
        ["debuffLance"] = "SUPERWEAPON_DISRUPTIVE_LANCE_NOT_IMPLEMENTED",
        // the cone has a body: without a point it is refused for the point (its aimed package is tested in A_cone...)
        ["doomsdayConeDOT"] = "TARGET_POINT_REQUIRED",
        ["doomsdayHOG"] = "SUPERWEAPON_HOG_NOT_IMPLEMENTED",
        // P.A.N.I.C. 42522: its own handler (IndustrialInvulnerabilityEffect), not part of this family
        ["moduleBonusIndustrialInvulnerability"] = PendingModuleEffect.NotImplemented,
    };

    /// <summary>The slot effects every module carries (power grid class and online): they are not an activation of the module.</summary>
    private static readonly HashSet<string> SlotEffects = new(StringComparer.OrdinalIgnoreCase) { "hiPower", "medPower", "lowPower", "online" };

    private const int Ship = 587;
    private const int CatmageddonType = 37474;

    [PgFact]
    public async Task Every_activation_of_every_group_588_type_is_refused_by_the_reason_its_family_names_and_builds_no_package()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        // the group listing keeps published types only, and Catmageddon (37474, a reaper of the family) is not published: it is added by its id
        var typeIds = (await statics.TypesInGroupsAsync(["Super Weapon"])).Select(type => type.TypeId).Append(CatmageddonType).ToArray();
        // 20 types in the group: 4 titan doomsdays, Catmageddon and 4 reapers, 4 lances, 4 disruptive lances, the Bosonic cone, the HOG and P.A.N.I.C.
        Assert.Equal(20, typeIds.Length);
        await statics.EnsureTypesAsync(typeIds);
        await statics.EnsureTypesAsync([Ship]);

        var types = typeIds.Select(id => statics.Type(id)!).ToArray();
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
                Assert.True(result.Reason == ReasonByEffect[effect.Name], $"type {type.TypeId} {effect.Name}: refused as {result.Reason}");
                Assert.Null(result.Effect);
                seen.Add($"{type.TypeId}:{effect.Name}");
            }
        }
        // one activation per type: no type of the group is left out of the check
        Assert.True(types.Length == seen.Count, $"{seen.Count} activations checked for {types.Length} types: {string.Join(", ", seen)}");
    }

    [PgFact]
    public async Task A_hog_is_refused_before_any_fuel_is_read_and_a_cone_is_built_from_its_static_data_when_aimed()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Ship, 40633, 40634]);

        // the HOG's transport has no body (its semantics are not verified): refused before ShipEffect.Build, so no BurnsFuel is set
        Assert.Equal(2000, statics.Type(40634)!.Attributes[ModuleAttributes.ConsumptionQuantity]);
        var hog = FittedModule(statics, 40634);
        var hogResult = ModulePackages.Of(hog.Snapshot, statics, hog.ModuleId, "doomsdayHOG", 0, 1, [0.6, 0.0, 0.8]);
        Assert.Equal("SUPERWEAPON_HOG_NOT_IMPLEMENTED", hogResult.Reason);
        Assert.Null(hogResult.Effect);

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
}
