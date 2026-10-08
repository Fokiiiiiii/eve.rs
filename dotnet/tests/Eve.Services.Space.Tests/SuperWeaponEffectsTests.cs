using Eve.Data;
using Eve.Dogma;
using Eve.Services.Space.Effects;
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
        ["doomsdayConeDOT"] = "SUPERWEAPON_CONE_NOT_IMPLEMENTED",
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
    public async Task A_cone_and_a_hog_are_refused_before_their_target_is_asked_for_and_before_any_fuel_is_read()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Ship, 40633, 40634]);

        // the Bosonic Field Generator burns 2000 of 16275 a cycle (713 and 714 in the static data): its body would be a fuel-burning effect, but the refusal stops
        // the package first, so no ShipEffect.Build runs and no BurnsFuel is set
        foreach (var (typeId, effectName, reason) in new[]
                 {
                     (40633, "doomsdayConeDOT", "SUPERWEAPON_CONE_NOT_IMPLEMENTED"),
                     (40634, "doomsdayHOG", "SUPERWEAPON_HOG_NOT_IMPLEMENTED"),
                 })
        {
            Assert.Equal(2000, statics.Type(typeId)!.Attributes[ModuleAttributes.ConsumptionQuantity]);
            var fixture = FittedModule(statics, typeId);
            var result = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, effectName, 0, 1);
            Assert.Equal(reason, result.Reason);
            Assert.Null(result.Effect);
        }
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
