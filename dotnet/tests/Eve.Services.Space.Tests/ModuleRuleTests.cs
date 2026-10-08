using System.Text.Json;
using Eve.Data;
using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>The module rules the static data states as attributes (docs/project/TODO.md section 9): the constants name the SDE's attributes, and what is not run is refused.</summary>
public class ModuleRuleTests
{
    /// <summary>The constants of <see cref="ModuleAttributes"/> for the module rules, each with the SDE name it stands for.</summary>
    private static readonly (int Id, string Name)[] RuleAttributes =
    [
        (ModuleAttributes.WarpScrambleStatus, "warpScrambleStatus"),
        (ModuleAttributes.WarpScrambleStrength, "warpScrambleStrength"),
        (ModuleAttributes.SiegeModeWarpStatus, "siegeModeWarpStatus"),
        (ModuleAttributes.DisallowDocking, "disallowDocking"),
        (ModuleAttributes.DisallowTethering, "disallowTethering"),
        (ModuleAttributes.DisallowEarlyDeactivation, "disallowEarlyDeactivation"),
        (ModuleAttributes.DisallowActivateOnWarp, "disallowActivateOnWarp"),
        (ModuleAttributes.DeactivateIfOffensive, "deactivateIfOffensive"),
        (ModuleAttributes.ActivationBlocked, "activationBlocked"),
        (ModuleAttributes.ActivationBlockedStrength, "activationBlockedStrenght"),
        (ModuleAttributes.ActivationRequiresActiveIndustrialCore, "activationRequiresActiveIndustrialCore"),
        (ModuleAttributes.ActivationRequiresActiveSiegeModule, "activationRequiresActiveSiegeModule"),
        (ModuleAttributes.NetworkedSensorArrayDisallowCapitalMicroJump, "networkedSensorArrayDisallowCapitalMicroJump"),
        (ModuleAttributes.CanActivateInGateCloak, "canActivateInGateCloak"),
        (ModuleAttributes.MaxGroupActive, "maxGroupActive"),
        (ModuleAttributes.MaxGroupOnline, "maxGroupOnline"),
        (ModuleAttributes.MaxGroupFitted, "maxGroupFitted"),
        (ModuleAttributes.MaxVelocityActivationLimit, "maxVelocityActivationLimit"),
        (ModuleAttributes.MinVelocityActivationLimit, "minVelocityActivationLimit"),
        (ModuleAttributes.ConsumptionType, "consumptionType"),
        (ModuleAttributes.ConsumptionQuantity, "consumptionQuantity"),
        (ModuleAttributes.ModuleReactivationDelay, "moduleReactivationDelay"),
        (ModuleAttributes.DisallowAssistance, "disallowAssistance"),
        (ModuleAttributes.DisallowOffensiveModifiers, "disallowOffensiveModifiers"),
        (ModuleAttributes.DisallowAgainstEwImmuneTarget, "disallowAgainstEwImmuneTarget"),
        (ModuleAttributes.DisallowInHazardSystem, "disallowInHazardSystem"),
        (ModuleAttributes.DisallowActivateInForcefield, "disallowActivateInForcefield"),
        (ModuleAttributes.RemoteRepairImpedance, "remoteRepairImpedance"),
        (ModuleAttributes.RemoteAssistanceImpedance, "remoteAssistanceImpedance"),
        (ModuleAttributes.SensorDampenerResistance, "sensorDampenerResistance"),
        (ModuleAttributes.WeaponDisruptionResistance, "weaponDisruptionResistance"),
        (ModuleAttributes.TargetPainterResistance, "targetPainterResistance"),
        (ModuleAttributes.EcmResistance, "ECMResistance"),
    ];

    [PgFact]
    public async Task Each_module_rule_constant_is_the_attribute_of_the_static_data_it_names()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        foreach (var (id, name) in RuleAttributes)
        {
            Assert.True(statics.AttributeId(name) == id, $"{name} is attribute {statics.AttributeId(name)} in the static data, not {id}.");
        }
    }

    [PgFact]
    public async Task A_module_that_burns_fuel_is_built_to_ask_for_its_fuel_before_each_cycle_and_is_not_run_free()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var carriers = await PublishedCarriersAsync(sde, statics, "moduleBonusSiegeModule");
        Assert.NotEmpty(carriers);
        await statics.EnsureTypesAsync(carriers);
        // the siege family is not uniform: some carriers burn a resource (consumptionType 713), some do not (the Abyssal Siege Module)
        var burning = carriers.Where(id => statics.Type(id)!.Attributes.GetValueOrDefault(ModuleAttributes.ConsumptionType) > 0).ToArray();
        var free = carriers.Where(id => !burning.Contains(id)).ToArray();
        Assert.NotEmpty(burning);
        var shipTypeId = await ShipTypeIdAsync(sde, statics);
        foreach (var moduleTypeId in burning.Take(3))
        {
            var fixture = FittedModule(statics, shipTypeId, moduleTypeId);
            var result = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, "moduleBonusSiegeModule", 0, 1);
            Assert.Null(result.Reason);
            Assert.NotNull(result.Effect);
            // the common mechanism marks it: the host takes the fuel of each cycle (WP18), it is not run free
            Assert.True(result.Effect!.BurnsFuel);
        }
        // the edge: a carrier that burns nothing is not marked, and is built as before
        foreach (var moduleTypeId in free.Take(1))
        {
            var fixture = FittedModule(statics, shipTypeId, moduleTypeId);
            var result = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, "moduleBonusSiegeModule", 0, 1);
            Assert.Null(result.Reason);
            Assert.NotNull(result.Effect);
            Assert.False(result.Effect!.BurnsFuel);
        }
    }

    [PgFact]
    public async Task Each_fuel_family_with_a_body_is_built_with_its_fuel_and_the_bodies_not_yet_run_stay_refused_by_name()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var shipTypeId = await ShipTypeIdAsync(sde, statics);
        await statics.EnsureTypesAsync([27951, 28583, 21096, 23953, 23735, 40633, 42522]);

        // the modifier bodies that run: the triage module and the capital industrial core burn their fuel and are built
        foreach (var (moduleTypeId, effectName) in new[] { (27951, "moduleBonusTriageModule"), (28583, "industrialCoreEffect2") })
        {
            var fixture = FittedModule(statics, shipTypeId, moduleTypeId);
            var result = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, effectName, 0, 1);
            Assert.Null(result.Reason);
            Assert.True(result.Effect!.BurnsFuel, $"{moduleTypeId} burns its fuel");
        }

        // the bodies the host does not run yet (the cyno field, the jump portal, the clone vat's acceptance) are refused by name: their modifiers alone
        // would be a success the reference does not give, fuel or not
        foreach (var (moduleTypeId, effectName) in new[] { (21096, "cynosuralGeneration"), (23953, "jumpPortalGeneration"), (23735, "cloneJumpAccepting") })
        {
            var fixture = FittedModule(statics, shipTypeId, moduleTypeId);
            var result = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, effectName, 0, 1);
            Assert.Equal("EFFECT_NOT_IMPLEMENTED", result.Reason);
            Assert.Null(result.Effect);
        }

        // a DDD-class module (Bosonic Field Generator, 713 16275 x 714 2000): its cone is aimed at a point, and without one it is refused (no free run);
        // aimed, its package burns the fuel of its cycle through the shared mechanism
        var ddd = FittedModule(statics, shipTypeId, 40633);
        var unaimed = ModulePackages.Of(ddd.Snapshot, statics, ddd.ModuleId, "doomsdayConeDOT", 0, 1);
        Assert.Equal("TARGET_POINT_REQUIRED", unaimed.Reason);
        Assert.Null(unaimed.Effect);
        var dddResult = ModulePackages.Of(ddd.Snapshot, statics, ddd.ModuleId, "doomsdayConeDOT", 0, 1, [0.6, 0.0, 0.8]);
        Assert.Null(dddResult.Reason);
        Assert.True(dddResult.Effect!.BurnsFuel, "the cone burns its fuel");

        // P.A.N.I.C. has no fuel attributes in the static data: nothing is invented for it
        var panic = FittedModule(statics, shipTypeId, 42522);
        Assert.Equal(0, statics.Type(42522)!.Attributes.GetValueOrDefault(ModuleAttributes.ConsumptionType));
        Assert.Equal(0, statics.Type(42522)!.Attributes.GetValueOrDefault(ModuleAttributes.ConsumptionQuantity));
        Assert.Equal("EFFECT_NOT_IMPLEMENTED", ModulePackages.Of(panic.Snapshot, statics, panic.ModuleId, "moduleBonusIndustrialInvulnerability", 0, 1).Reason);
    }

    [PgFact]
    public async Task A_module_whose_effect_has_no_body_yet_is_refused_and_stays_uncovered()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var carriers = await PublishedCarriersAsync(sde, statics, "barrage");
        Assert.NotEmpty(carriers);
        var moduleTypeId = carriers[0];
        await statics.EnsureTypesAsync([moduleTypeId]);
        var fixture = FittedModule(statics, await ShipTypeIdAsync(sde, statics), moduleTypeId);

        var result = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, "barrage", 1, 1);
        // a barrage is not fired as a plain shot: its handler only refuses (PendingModuleEffect), so the effect is not coverage
        Assert.Equal(PendingModuleEffect.NotImplemented, result.Reason);
        Assert.Null(result.Effect);
        Assert.Contains(ModuleEffects.Default.Uncovered(statics), effect => effect.Name.Equals("barrage", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The published module types that carry an effect, by its name (the static data's rows).</summary>
    private static async Task<int[]> PublishedCarriersAsync(SdeReader sde, DogmaStatic statics, string effectName)
    {
        var effect = statics.Effects.Single(candidate => candidate.Name.Equals(effectName, StringComparison.OrdinalIgnoreCase));
        var moduleGroupIds = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7)
            .Select(row => row.Key)
            .ToArray();
        var published = (await sde.ReadByFieldAsync("types", "groupID", moduleGroupIds))
            .Where(row => row.Doc.TryGetProperty("published", out var flag) && flag.ValueKind == JsonValueKind.True)
            .Select(row => row.Key)
            .ToArray();
        return (await sde.ReadAsync("typeDogma", published))
            .Where(row => row.Doc.TryGetProperty("dogmaEffects", out var effects)
                && effects.ValueKind == JsonValueKind.Array
                && effects.EnumerateArray().Any(typeEffect => typeEffect.GetProperty("effectID").GetInt32() == effect.Id))
            .Select(row => checked((int)row.Key))
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>A published ship type, the hull the module is fitted to.</summary>
    private static async Task<int> ShipTypeIdAsync(SdeReader sde, DogmaStatic statics)
    {
        var shipGroupIds = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 6)
            .Select(row => row.Key)
            .ToArray();
        return checked((int)(await sde.ReadByFieldAsync("types", "groupID", shipGroupIds))
            .First(row => row.Doc.TryGetProperty("published", out var flag) && flag.ValueKind == JsonValueKind.True)
            .Key);
    }

    /// <summary>A ship with one module fitted and online, as the host's snapshot has them (the character is not needed by these rules).</summary>
    private static (Eve.Ships.ShipSnapshot Snapshot, long ModuleId) FittedModule(DogmaStatic statics, int shipTypeId, int moduleTypeId)
    {
        const long characterId = 74001;
        const long shipId = 74002;
        const long moduleId = 74003;
        const int moduleFlag = 11;
        var ship = new Eve.Inventory.Item(shipId, shipTypeId, characterId, 60003760, 0, 1, true, null);
        var module = new Eve.Inventory.Item(moduleId, moduleTypeId, characterId, shipId, moduleFlag, 1, true, null);
        var world = new DogmaWorld(statics, characterId, shipId);
        world.Add(new DogmaItem(shipId, shipTypeId, characterId, ship.LocationId, ship.Flag, ItemState.Online));
        world.Add(new DogmaItem(moduleId, moduleTypeId, characterId, shipId, moduleFlag, ItemState.Online));
        var snapshot = new Eve.Ships.ShipSnapshot
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
