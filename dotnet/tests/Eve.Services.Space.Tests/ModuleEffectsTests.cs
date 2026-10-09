using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Space.Tests;

public class ModuleEffectsTests
{
    [PgFact]
    public async Task Every_effect_the_space_host_serves_exists_in_the_static_data_and_is_not_listed_as_uncovered()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var registry = Eve.Services.Space.Effects.ModuleEffects.Default;
        var known = statics.Effects.Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // a typo in a handler's name would make it serve nothing, silently
        Assert.All(registry.Names, name => Assert.True(known.Contains(name), $"SDE build {catalogBuild} has no effect named {name}."));
        var uncovered = registry.Uncovered(statics).Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // only implemented handlers cover an effect: a pending handler refuses, and its effect stays listed as uncovered
        Assert.All(registry.ImplementedNames, name => Assert.False(uncovered.Contains(name), $"SDE build {catalogBuild} leaves {name} uncovered."));
        // Names are validated against the catalog opened by this test, whose build is reported above.
        foreach (var name in new[]
        {
            "moduleBonusAfterburner", "moduleBonusMicrowarpdrive", "microJumpDrive", "targetAttack", "projectileFired", "barrage", "targetDisintegratorAttack", "armorRepair", "shieldBoosting", "structureRepair", "empWave", "remoteWebifierFalloff",
            "remoteTargetPaintFalloff", "remoteSensorDampFalloff", "remoteSensorBoostFalloff", "warpScrambleBlockMWDWithNPCEffect", "warpDisrupt", "shipModuleTrackingDisruptor", "shipModuleRemoteTrackingComputer", "shipModuleGuidanceDisruptor", "remoteECMFalloff",
            "ecmBurstJammer", "cloaking", "cloakingWarpSafe", "cloakingPrototype", "shipModuleRemoteShieldBooster", "shipModuleRemoteArmorRepairer", "ShipModuleRemoteArmorMutadaptiveRepairer", "shipModuleRemoteHullRepairer", "shipModuleRemoteCapacitorTransmitter", "energyNeutralizerFalloff", "energyNosferatuFalloff",
            "sensorBoosterActivePercentage", "shipModuleAncillaryRemoteArmorRepairer", "shipModuleAncillaryRemoteShieldBooster", "cloneJumpAccepting", "cynosuralGeneration", "industrialCompactCoreEffect2", "industrialCoreEffect2", "jumpPortalGeneration", "jumpPortalGenerationBO", "moduleBonusSiegeModule", "moduleBonusTriageModule", "adaptiveArmorHardener", "emergencyHullEnergizer", "moduleBonusAssaultDamageControl", "gunneryMaxRangeFalloffTrackingSpeedBonus", "interdictionNullifierBonus", "massEntanglerEffect5",
            "missileGuidanceComputerBonus4", "modifyActiveArmorResonancePostPercent", "modifyActiveShieldResonancePostPercent", "moduleBonusAssaultDamageControl", "moduleBonusBastionModule",
            "moduleBonusIntegratedSensorArray", "moduleBonusNetworkedSensorArray", "moduleBonusOmnidirectionalTrackingLink", "targetSpectrumBreakerBonus", "warpCoreStabilizerActive",
        })
        {
            Assert.True(registry.Names.Contains(name, StringComparer.OrdinalIgnoreCase), $"SDE build {catalogBuild} is missing handler name {name}.");
        }
    }


    [Fact]
    public void A_module_carries_the_rules_the_simulation_enforces_from_its_type()
    {
        // maxGroupActive 763, disallowEarlyDeactivation 906, activationRequiresActiveIndustrialCore 3265, moduleReactivationDelay 669; a core effect says it is one
        var fixture = Fixture(
            5230, "modifyActiveShieldResonancePostPercent", EffectDef.Activation,
            new Dictionary<int, double> { [6] = 5, [73] = 1000, [763] = 1, [906] = 1, [3265] = 1, [669] = 180000 },
            [],
            new Dictionary<int, double> { [900] = 100 });
        var result = Eve.Services.Space.ModulePackages.Of(
            fixture.Snapshot, fixture.Statics, fixture.ModuleId, "modifyActiveShieldResonancePostPercent", 0, 1);

        Assert.Null(result.Reason);
        var rules = Assert.IsType<Eve.Space.SimEffect>(result.Effect).Rules;
        Assert.NotNull(rules);
        Assert.Equal(1, rules.MaxGroupActive);
        Assert.True(rules.DisallowEarlyDeactivation);
        Assert.True(rules.RequiresIndustrialCore);
        Assert.False(rules.ProvidesIndustrialCore);
        Assert.Equal(180000, rules.ReactivationDelayMs);
    }

    [Fact]
    public void A_module_that_disallows_repeating_activation_has_one_cycle_whatever_repeat_is_asked()
    {
        // disallowRepeatingActivation (1014): the server's cycle budget is one, so a module that must fire once cannot be held on by a repeat (EveJS
        // moduleCycleSchedule.js); a module without it keeps the repeat asked for
        foreach (var (disallows, expected) in new[] { (1.0, 1), (0.0, 3) })
        {
            var fixture = Fixture(
                5230, "modifyActiveShieldResonancePostPercent", EffectDef.Activation,
                new Dictionary<int, double> { [6] = 5, [73] = 1000, [1014] = disallows },
                [],
                new Dictionary<int, double> { [900] = 100 });
            var result = Eve.Services.Space.ModulePackages.Of(
                fixture.Snapshot, fixture.Statics, fixture.ModuleId, "modifyActiveShieldResonancePostPercent", 0, 3);

            Assert.Null(result.Reason);
            Assert.Equal(expected, Assert.IsType<Eve.Space.SimEffect>(result.Effect).Repeat);
        }
    }

    [Fact]
    public void A_modifier_backed_effect_packages_cycles_and_activates_its_sde_dogma_modifiers()
    {
        var modifier = new Modifier(ModifierFunc.Item, "shipID", Operation.PostPercent, 900, 901);
        var fixture = Fixture(
            5230, "modifyActiveShieldResonancePostPercent", EffectDef.Activation,
            new Dictionary<int, double> { [6] = 5, [73] = 1000, [901] = 20 },
            [modifier],
            new Dictionary<int, double> { [900] = 100 });
        var result = Eve.Services.Space.ModulePackages.Of(
            fixture.Snapshot, fixture.Statics, fixture.ModuleId, "modifyActiveShieldResonancePostPercent", 0, 3);

        Assert.Null(result.Reason);
        var package = Assert.IsType<Eve.Space.SimEffect>(result.Effect);
        Assert.Equal(5230, package.EffectId);
        Assert.Equal(3, package.Repeat);
        Assert.Equal(1000, package.DurationMs);
        Assert.Equal(5, package.CapacitorNeed);
        Assert.Null(package.Action);
        Assert.Equal(100, fixture.Snapshot.World.Get(fixture.ShipId, 900));

        fixture.Snapshot.World.SetState(fixture.ModuleId, ItemState.Active);
        Assert.Equal(120, fixture.Snapshot.World.Get(fixture.ShipId, 900));
        Assert.Contains(package.OnChanges, change => change.ItemId == fixture.ShipId && change.AttributeId == 900 && change.Value == 120);
    }

    [Fact]
    public void The_current_sensor_booster_effect_name_builds_its_local_boost_action()
    {
        var fixture = Fixture(
            2670, "sensorBoosterActivePercentage", EffectDef.Activation,
            new Dictionary<int, double> { [6] = 10, [73] = 12000, [309] = 25, [566] = 25 });
        var result = Eve.Services.Space.ModulePackages.Of(
            fixture.Snapshot, fixture.Statics, fixture.ModuleId, "sensorBoosterActivePercentage", 0, 1);

        Assert.Null(result.Reason);
        var package = Assert.IsType<Eve.Space.SimEffect>(result.Effect);
        Assert.Equal(fixture.ShipId, package.TargetId);
        var action = Assert.IsType<Eve.Space.SimAction.Remote>(package.Action);
        Assert.Equal(new Eve.Space.SimRemote.Damp(25, 25), action.What);
    }

    [Fact]
    public void The_mass_entangler_uses_its_static_modifier_values_for_the_space_numbers()
    {
        var modifiers = new[]
        {
            new Modifier(ModifierFunc.Item, "shipID", Operation.PostPercent, 4, 1131),
            new Modifier(ModifierFunc.Item, "shipID", Operation.PostPercent, 37, 2603),
        };
        var fixture = Fixture(
            7142, "massEntanglerEffect5", EffectDef.Activation,
            new Dictionary<int, double> { [6] = 10, [73] = 5000, [1131] = -80, [2603] = -95 },
            modifiers,
            new Dictionary<int, double> { [4] = 1_000_000, [37] = 200, [552] = 100 });
        var result = Eve.Services.Space.ModulePackages.Of(
            fixture.Snapshot, fixture.Statics, fixture.ModuleId, "massEntanglerEffect5", 0, 1);

        Assert.Null(result.Reason);
        var package = Assert.IsType<Eve.Space.SimEffect>(result.Effect);
        Assert.Equal(new Eve.Space.SimNumbers(200_000, 10, 100), package.On);
        Assert.Equal(new Eve.Space.SimNumbers(1_000_000, 200, 100), package.Off);
    }


    [Fact]
    public void An_entropic_disintegrator_builds_the_existing_turret_package_for_its_first_volley()
    {
        var charge = new Eve.Inventory.Item(70004, 91002, 70001, 70002, 11, 1, false, null);
        var turretFitted = new EffectDef(42, "turretFitted", EffectDef.Passive, []);
        var fixture = Fixture(
            6995, "targetDisintegratorAttack", EffectDef.Target,
            new Dictionary<int, double>
            {
                [6] = 4.5, [51] = 3500, [54] = 6000, [56] = 1, [64] = 0.8,
                [158] = 0, [160] = 410, [620] = 40000, [604] = 1987,
            },
            charge: charge,
            chargeAttributes: new Dictionary<int, double> { [114] = 10, [116] = 20, [117] = 30, [118] = 40 },
            additionalEffectIds: [42],
            additionalEffects: [turretFitted]);
        var result = Eve.Services.Space.ModulePackages.Of(
            fixture.Snapshot, fixture.Statics, fixture.ModuleId, "targetDisintegratorAttack", 70005, 1);

        Assert.Null(result.Reason);
        var package = Assert.IsType<Eve.Space.SimEffect>(result.Effect);
        var turret = Assert.IsType<Eve.Space.SimTurret>(package.Turret);
        Assert.Equal(6995, package.EffectId);
        Assert.Equal(new[] { 8d, 32d, 24d, 16d }, turret.RawDamage);
        Assert.Equal(6000, turret.OptimalRange);
    }


    [Fact]
    public void The_ancillary_remote_armor_repairer_spends_paste_then_keeps_its_unloaded_repair()
    {
        var charge = new Eve.Inventory.Item(70004, 91001, 70001, 70002, 11, 2, false, null);
        var fixture = Fixture(
            6651, "shipModuleAncillaryRemoteArmorRepairer", EffectDef.Target,
            new Dictionary<int, double>
            {
                [6] = 38, [56] = 1, [73] = 3000, [54] = 7000, [84] = 30, [1886] = 3, [2044] = 2500,
            },
            charge: charge);
        var result = Eve.Services.Space.ModulePackages.Of(
            fixture.Snapshot, fixture.Statics, fixture.ModuleId, "shipModuleAncillaryRemoteArmorRepairer", 70005, 3);

        Assert.Null(result.Reason);
        var package = Assert.IsType<Eve.Space.SimEffect>(result.Effect);
        var charged = Assert.IsType<Eve.Space.SimAction.Charged>(package.Action);
        Assert.Equal(91001, charged.ChargeTypeId);
        Assert.Equal(11, charged.ChargeFlag);
        Assert.Equal(1, charged.ChargePerCycle);
        Assert.Equal(2, charged.Charges);
        var powered = Assert.IsType<Eve.Space.SimAction.Remote>(charged.Action);
        Assert.Equal(new Eve.Space.SimRemote.Repair(Eve.Space.SimLayer.Armor, 90), powered.What);
        Assert.True(powered.AtCycleEnd);
        var fallback = Assert.IsType<Eve.Space.SimAction.Remote>(charged.Fallback);
        Assert.Equal(new Eve.Space.SimRemote.Repair(Eve.Space.SimLayer.Armor, 30), fallback.What);
    }


    [Fact]
    public void The_ancillary_remote_shield_booster_switches_between_cap_free_charges_and_capacitor()
    {
        var charge = new Eve.Inventory.Item(70004, 91002, 70001, 70002, 11, 9, false, null);
        var loaded = Fixture(
            6652, "shipModuleAncillaryRemoteShieldBooster", EffectDef.Target,
            new Dictionary<int, double> { [6] = 135, [56] = 1, [68] = 125, [73] = 4000, [54] = 4000, [2044] = 6500 },
            charge: charge);
        var loadedResult = Eve.Services.Space.ModulePackages.Of(
            loaded.Snapshot, loaded.Statics, loaded.ModuleId, "shipModuleAncillaryRemoteShieldBooster", 70005, 10);

        Assert.Null(loadedResult.Reason);
        var loadedPackage = Assert.IsType<Eve.Space.SimEffect>(loadedResult.Effect);
        Assert.Equal(0, loadedPackage.CapacitorNeed);
        var charged = Assert.IsType<Eve.Space.SimAction.Charged>(loadedPackage.Action);
        Assert.Equal(9, charged.Charges);
        Assert.Null(charged.Fallback);
        var chargedRemote = Assert.IsType<Eve.Space.SimAction.Remote>(charged.Action);
        Assert.Equal(new Eve.Space.SimRemote.Repair(Eve.Space.SimLayer.Shield, 125), chargedRemote.What);
        Assert.False(chargedRemote.AtCycleEnd);

        var empty = Fixture(
            6652, "shipModuleAncillaryRemoteShieldBooster", EffectDef.Target,
            new Dictionary<int, double> { [6] = 135, [56] = 1, [68] = 125, [73] = 4000, [54] = 4000, [2044] = 6500 });
        var emptyResult = Eve.Services.Space.ModulePackages.Of(
            empty.Snapshot, empty.Statics, empty.ModuleId, "shipModuleAncillaryRemoteShieldBooster", 70005, 1);

        Assert.Null(emptyResult.Reason);
        var emptyPackage = Assert.IsType<Eve.Space.SimEffect>(emptyResult.Effect);
        Assert.Equal(135, emptyPackage.CapacitorNeed);
        var unchargedRemote = Assert.IsType<Eve.Space.SimAction.Remote>(emptyPackage.Action);
        Assert.Equal(new Eve.Space.SimRemote.Repair(Eve.Space.SimLayer.Shield, 125), unchargedRemote.What);
    }

    [Fact]
    public void The_booster_and_the_fueled_repairers_are_registered_effect_handlers()
    {
        foreach (var name in new[] { "powerBooster", "fueledArmorRepair", "fueledShieldBoosting" })
        {
            Assert.Contains(name, Eve.Services.Space.Effects.ModuleEffects.Default.Names, StringComparer.OrdinalIgnoreCase);
        }
    }

    // The exploration, hacking and scanner effects that are wired but have no body yet (ExplorationModuleEffects.cs): an online module is refused as pending,
    // never packaged (no cycle, no success), and never as UNSUPPORTED_EFFECT, which means there is no handler at all.
    [Theory]
    [InlineData(1738, "doHacking", EffectDef.Target)]
    [InlineData(4773, "hackOrbital", EffectDef.Target)]
    [InlineData(47, "cargoScan", EffectDef.Target)]
    [InlineData(46, "shipScan", EffectDef.Target)]
    [InlineData(2231, "scanStrengthBonusPercentActivate", EffectDef.Activation)]
    [InlineData(2246, "scanStrengthTargetPercentBonus", EffectDef.Target)]
    public void A_scanner_or_hacking_effect_with_no_body_yet_is_refused_as_not_implemented_and_packages_nothing(int effectId, string effectName, int category)
    {
        var fixture = Fixture(effectId, effectName, category, new Dictionary<int, double> { [6] = 20, [73] = 10000, [54] = 5000 });
        // a target effect is refused the same way with or without a target: the pending refusal is asked before the target is
        foreach (var targetId in new long[] { 0, 70005 })
        {
            var result = Eve.Services.Space.ModulePackages.Of(fixture.Snapshot, fixture.Statics, fixture.ModuleId, effectName, targetId, 1);

            Assert.Null(result.Effect);
            Assert.Equal("EFFECT_NOT_IMPLEMENTED", result.Reason);
        }
    }

    [Fact]
    public void A_pending_effect_of_an_offline_module_is_refused_as_offline_before_it_is_refused_as_pending()
    {
        var fixture = Fixture(1738, "doHacking", EffectDef.Target, new Dictionary<int, double> { [6] = 20 });
        fixture.Snapshot.World.SetState(fixture.ModuleId, ItemState.Offline);

        var result = Eve.Services.Space.ModulePackages.Of(fixture.Snapshot, fixture.Statics, fixture.ModuleId, "doHacking", 70005, 1);

        Assert.Null(result.Effect);
        Assert.Equal("MODULE_NOT_ONLINE", result.Reason);
    }

    [PgFact]
    public async Task The_scanner_and_hacking_effects_without_a_body_are_registered_listed_as_uncovered_and_assigned_to_module_types_in_the_current_sde()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var registry = Eve.Services.Space.Effects.ModuleEffects.Default;
        string[] pending = ["doHacking", "hackOrbital", "cargoScan", "shipScan", "scanStrengthBonusPercentActivate", "scanStrengthTargetPercentBonus"];
        var uncovered = registry.Uncovered(statics).Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moduleGroups = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7)
            .Select(row => row.Key)
            .ToArray();
        var moduleTypes = (await sde.ReadByFieldAsync("types", "groupID", moduleGroups))
            .Select(row => row.Key)
            .ToArray();
        var assignedModuleEffects = new HashSet<int>();
        foreach (var row in await sde.ReadAsync("typeDogma", moduleTypes))
        {
            if (!row.Doc.TryGetProperty("dogmaEffects", out var effects) || effects.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }
            foreach (var typeEffect in effects.EnumerateArray())
            {
                assignedModuleEffects.Add(typeEffect.GetProperty("effectID").GetInt32());
            }
        }

        foreach (var name in pending)
        {
            var effect = Assert.Single(statics.Effects, candidate => candidate.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            Assert.True(registry.Names.Contains(name, StringComparer.OrdinalIgnoreCase), $"SDE build {catalogBuild} has no handler for {name}.");
            // a pending handler is registered but is not coverage: the effect stays listed as uncovered
            Assert.False(registry.ImplementedNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"SDE build {catalogBuild} counts pending {name} as implemented.");
            Assert.True(uncovered.Contains(name), $"SDE build {catalogBuild} does not list pending {name} as uncovered.");
            Assert.True(assignedModuleEffects.Contains(effect.Id), $"SDE build {catalogBuild} assigns {name} to no module type.");
        }
    }

    [PgFact]
    public async Task Dogma_modifier_handlers_cover_their_published_module_effects_in_the_current_sde()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var moduleGroups = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7)
            .Select(row => row.Key)
            .ToArray();
        var moduleTypes = (await sde.ReadByFieldAsync("types", "groupID", moduleGroups))
            .Where(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(row => row.Key)
            .ToArray();
        var assignedModuleEffects = new HashSet<int>();
        foreach (var row in await sde.ReadAsync("typeDogma", moduleTypes))
        {
            if (!row.Doc.TryGetProperty("dogmaEffects", out var effects) || effects.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }
            foreach (var typeEffect in effects.EnumerateArray())
            {
                assignedModuleEffects.Add(typeEffect.GetProperty("effectID").GetInt32());
            }
        }

        var dogmaNames = new Eve.Services.Space.Effects.DogmaModifierEffect().EffectNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supported = dogmaNames.Concat(["sensorBoosterActivePercentage", "shipModuleAncillaryRemoteArmorRepairer", "shipModuleAncillaryRemoteShieldBooster"]);
        foreach (var name in supported)
        {
            var effect = Assert.Single(statics.Effects, candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Assert.True(assignedModuleEffects.Contains(effect.Id), $"SDE build {catalogBuild} has no published module type assigned effect {name}.");
            Assert.True(Eve.Services.Space.Effects.ModuleEffects.Default.Names.Contains(name, StringComparer.OrdinalIgnoreCase), $"SDE build {catalogBuild} does not register effect {name}.");
            if (dogmaNames.Contains(name))
            {
                Assert.NotEmpty(effect.Modifiers);
            }
        }
    }


    [PgFact]
    public async Task Sde_module_effect_handlers_use_actual_catalog_types_and_attributes()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var groups = await sde.ReadAsync("groups");
        var moduleGroupIds = groups
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7)
            .Select(row => row.Key)
            .ToArray();
        var publishedModuleTypeIds = (await sde.ReadByFieldAsync("types", "groupID", moduleGroupIds))
            .Where(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(row => row.Key)
            .ToArray();
        var moduleDogma = await sde.ReadAsync("typeDogma", publishedModuleTypeIds);
        var shipGroupIds = groups
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 6)
            .Select(row => row.Key)
            .ToArray();
        var shipTypeId = checked((int)(await sde.ReadByFieldAsync("types", "groupID", shipGroupIds))
            .First(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Key);

        foreach (var effectName in new[]
        {
            "emergencyHullEnergizer",
            "moduleBonusAssaultDamageControl",
            "ShipModuleRemoteArmorMutadaptiveRepairer",
        })
        {
            var effect = Assert.Single(statics.Effects, candidate => candidate.Name.Equals(effectName, StringComparison.OrdinalIgnoreCase));
            var moduleDogmaRow = moduleDogma.First(row =>
                row.Doc.TryGetProperty("dogmaEffects", out var effects)
                && effects.ValueKind == System.Text.Json.JsonValueKind.Array
                && effects.EnumerateArray().Any(typeEffect => typeEffect.GetProperty("effectID").GetInt32() == effect.Id));
            var moduleTypeId = checked((int)moduleDogmaRow.Key);
            await statics.EnsureTypesAsync([shipTypeId, moduleTypeId]);
            var moduleType = statics.Type(moduleTypeId)
                ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing module type {moduleTypeId}.");

            const long characterId = 72001;
            const long shipId = 72002;
            const long moduleId = 72003;
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

            var mutadaptive = effectName.Equals("ShipModuleRemoteArmorMutadaptiveRepairer", StringComparison.OrdinalIgnoreCase);
            var remoteTrackingComputer = effectName.Equals("shipModuleRemoteTrackingComputer", StringComparison.OrdinalIgnoreCase);
            var assaultDamageControl = effectName.Equals("moduleBonusAssaultDamageControl", StringComparison.OrdinalIgnoreCase);
            var targetId = mutadaptive || remoteTrackingComputer ? 72004L : 0L;
            var result = Eve.Services.Space.ModulePackages.Of(snapshot, statics, moduleId, effectName, targetId, 1);
            Assert.True(result.Reason is null, $"SDE build {catalogBuild}, effect {effectName}: {result.Reason}");
            var package = Assert.IsType<Eve.Space.SimEffect>(result.Effect);
            Assert.Equal(1, package.Repeat);

            if (remoteTrackingComputer)
            {
                var maxRange = statics.AttributeId("maxRange")
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing maxRange.");
                var falloffEffectiveness = statics.AttributeId("falloffEffectiveness")
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing falloffEffectiveness.");
                var maxRangeBonus = statics.AttributeId("maxRangeBonus")
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing maxRangeBonus.");
                var falloffBonus = statics.AttributeId("falloffBonus")
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing falloffBonus.");
                var trackingSpeedBonus = statics.AttributeId("trackingSpeedBonus")
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing trackingSpeedBonus.");
                var remote = Assert.IsType<Eve.Space.SimAction.Remote>(package.Action);
                Assert.Equal(new Eve.Space.SimRemote.Disrupt(
                    Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, maxRangeBonus)),
                    Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, falloffBonus)),
                    Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, trackingSpeedBonus))), remote.What);
                Assert.Equal(Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, maxRange)), remote.Optimal);
                Assert.Equal(Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, falloffEffectiveness)), remote.Falloff);
                Assert.Equal(72004L, package.TargetId);
                Assert.False(remote.AtCycleEnd);
                Assert.Equal("ewTrackingDisrupt", remote.Jam);
                continue;
            }

            if (mutadaptive)
            {
                var amountId = statics.AttributeId("armorDamageAmount")
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing armorDamageAmount.");
                var perCycleId = statics.AttributeId("repairMultiplierBonusPerCycle")
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing repairMultiplierBonusPerCycle.");
                var maximumId = statics.AttributeId("repairMultiplierBonusMax")
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing repairMultiplierBonusMax.");
                var amount = Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, amountId));
                var perCycle = world.Get(moduleId, perCycleId);
                var maximum = world.Get(moduleId, maximumId);
                var remote = Assert.IsType<Eve.Space.SimAction.Remote>(package.Action);
                Assert.Equal(new Eve.Space.SimRemote.MutadaptiveRepair(
                    Eve.Space.SimLayer.Armor, amount, perCycle, maximum), remote.What);
                Assert.True(remote.AtCycleEnd);
                Assert.Equal("remoteArmorRepair", remote.Jam);
                continue;
            }

            if (assaultDamageControl)
            {
                Assert.Equal(12, effect.Modifiers.Select(modifier => modifier.ModifiedAttributeId).Distinct().Count());
                Assert.All(effect.Modifiers, modifier =>
                {
                    Assert.Equal(Eve.Dogma.ModifierFunc.Item, modifier.Func);
                    Assert.Equal("itemID", modifier.Domain);
                });
                foreach (var group in effect.Modifiers.GroupBy(modifier => modifier.ModifiedAttributeId))
                {
                    var before = world.Get(moduleId, group.Key);
                    var expected = group.Aggregate(before, (value, modifier) =>
                        Eve.Dogma.Operations.Apply(modifier.Operation, value, world.Get(moduleId, modifier.ModifyingAttributeId)));
                    Assert.Contains(package.OnChanges, change => change.ItemId == moduleId
                        && change.AttributeId == group.Key && Math.Abs(change.Value - expected) < 0.000001);
                    Assert.Contains(package.OffChanges, change => change.ItemId == moduleId
                        && change.AttributeId == group.Key && Math.Abs(change.Value - before) < 0.000001);
                }
                continue;
            }

            if (effectName.Equals("emergencyHullEnergizer", StringComparison.OrdinalIgnoreCase))
            {
                // the ship's resonance of each damage type is multiplied (PostMul) by the module's own hull resonance of that type, never the module's attribute
                // on the ship; the module is burned out to its hit points as it starts (emergencyHullEnergizer.py)
                foreach (var (moduleName, shipName) in new[]
                {
                    ("hullEmDamageResonance", "emDamageResonance"),
                    ("hullExplosiveDamageResonance", "explosiveDamageResonance"),
                    ("hullKineticDamageResonance", "kineticDamageResonance"),
                    ("hullThermalDamageResonance", "thermalDamageResonance"),
                })
                {
                    var moduleAttribute = statics.AttributeId(moduleName)
                        ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing Dogma attribute {moduleName}.");
                    var shipAttribute = statics.AttributeId(shipName)
                        ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing Dogma attribute {shipName}.");
                    Assert.True(moduleType.Attributes.TryGetValue(moduleAttribute, out var multiplier), $"SDE build {catalogBuild}, type {moduleTypeId} is missing {moduleName}.");
                    var before = world.Get(shipId, shipAttribute);
                    var expected = Eve.Services.Space.Effects.ModuleAttributes.Six(before * multiplier);
                    Assert.Contains(package.OnChanges, change => change.ItemId == shipId && change.AttributeId == shipAttribute && Math.Abs(change.Value - expected) < 0.000001);
                    Assert.Contains(package.OffChanges, change => change.ItemId == shipId && change.AttributeId == shipAttribute && Math.Abs(change.Value - before) < 0.000001);
                    Assert.DoesNotContain(package.OnChanges, change => change.ItemId == shipId && change.AttributeId == moduleAttribute);
                }
                var hitPoints = world.Get(moduleId, Eve.Services.Space.Effects.ModuleAttributes.ModuleHitPoints);
                Assert.True(hitPoints > 0, $"SDE build {catalogBuild}, type {moduleTypeId} has no hit points.");
                Assert.Contains(package.OnChanges, change => change.ItemId == moduleId
                    && change.AttributeId == Eve.Services.Space.Effects.ModuleAttributes.ModuleDamage && Math.Abs(change.Value - hitPoints) < 0.000001);
                continue;
            }

            var attributeNames = new[]
            {
                "armorEmDamageResonance", "armorExplosiveDamageResonance", "armorKineticDamageResonance", "armorThermalDamageResonance",
                "shieldEmDamageResonance", "shieldExplosiveDamageResonance", "shieldKineticDamageResonance", "shieldThermalDamageResonance",
                "hullEmDamageResonance", "hullExplosiveDamageResonance", "hullKineticDamageResonance", "hullThermalDamageResonance",
            };
            foreach (var name in attributeNames)
            {
                var attributeId = statics.AttributeId(name)
                    ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing Dogma attribute {name}.");
                Assert.True(moduleType.Attributes.TryGetValue(attributeId, out var multiplier), $"SDE build {catalogBuild}, type {moduleTypeId} is missing {name}.");
                var before = world.Get(shipId, attributeId);
                var expected = Eve.Services.Space.Effects.ModuleAttributes.Six(before * multiplier);
                Assert.Contains(package.OnChanges, change => change.ItemId == shipId && change.AttributeId == attributeId && Math.Abs(change.Value - expected) < 0.000001);
                Assert.Contains(package.OffChanges, change => change.ItemId == shipId && change.AttributeId == attributeId && Math.Abs(change.Value - before) < 0.000001);
            }

        }
    }


    [PgFact]
    public async Task Mutadaptive_repair_spool_uses_each_published_modules_sde_cycle_bonus_and_resets_at_boundaries()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var effect = Assert.Single(statics.Effects, candidate =>
            candidate.Name.Equals("ShipModuleRemoteArmorMutadaptiveRepairer", StringComparison.OrdinalIgnoreCase));
        var moduleGroupIds = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7)
            .Select(row => row.Key)
            .ToArray();
        var publishedModuleTypeIds = (await sde.ReadByFieldAsync("types", "groupID", moduleGroupIds))
            .Where(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(row => row.Key)
            .ToArray();
        var moduleDogma = await sde.ReadAsync("typeDogma", publishedModuleTypeIds);
        var moduleTypeIds = moduleDogma
            .Where(row => row.Doc.TryGetProperty("dogmaEffects", out var effects)
                && effects.ValueKind == System.Text.Json.JsonValueKind.Array
                && effects.EnumerateArray().Any(typeEffect => typeEffect.GetProperty("effectID").GetInt32() == effect.Id))
            .Select(row => checked((int)row.Key))
            .OrderBy(id => id)
            .ToArray();
        Assert.NotEmpty(moduleTypeIds);

        var repairAmountAttribute = statics.AttributeId("armorDamageAmount")
            ?? throw new InvalidOperationException("The SDE is missing armorDamageAmount.");
        var bonusPerCycleAttribute = statics.AttributeId("repairMultiplierBonusPerCycle")
            ?? throw new InvalidOperationException("The SDE is missing repairMultiplierBonusPerCycle.");
        var maximumBonusAttribute = statics.AttributeId("repairMultiplierBonusMax")
            ?? throw new InvalidOperationException("The SDE is missing repairMultiplierBonusMax.");
        var durationAttribute = statics.AttributeId("duration")
            ?? throw new InvalidOperationException("The SDE is missing duration.");
        var moduleId = 73001L;
        const long targetId = 73002L;

        foreach (var moduleTypeId in moduleTypeIds)
        {
            await statics.EnsureTypesAsync([moduleTypeId]);
            var moduleType = statics.Type(moduleTypeId)
                ?? throw new InvalidOperationException("The SDE is missing a published mutadaptive module type.");
            Assert.True(moduleType.Attributes.TryGetValue(repairAmountAttribute, out var baseAmount),
                "The SDE module is missing armorDamageAmount.");
            Assert.True(moduleType.Attributes.TryGetValue(bonusPerCycleAttribute, out var bonusPerCycle),
                "The SDE module is missing repairMultiplierBonusPerCycle.");
            Assert.True(moduleType.Attributes.TryGetValue(maximumBonusAttribute, out var maximumBonus),
                "The SDE module is missing repairMultiplierBonusMax.");
            Assert.True(moduleType.Attributes.TryGetValue(durationAttribute, out var durationMs) && durationMs > 0,
                "The SDE module is missing a positive cycle duration.");

            var cycleDurationMs = checked((long)Math.Round(durationMs));
            Assert.True(bonusPerCycle > 0, "The SDE module must have a positive repair bonus per cycle.");
            var maximumCycles = 1;
            while (maximumCycles * bonusPerCycle < maximumBonus - 0.000000001)
            {
                maximumCycles++;
            }

            var spool = new Eve.Services.Space.Effects.MutadaptiveRepairSpool();
            spool.Start(moduleId, targetId);
            long atMs = 0;
            ulong cycleId = 1;
            var cycleNumber = 1;
            var firstAmount = 0d;
            for (var cycle = 1; cycle <= maximumCycles + 1; cycle++)
            {
                atMs += cycleDurationMs;
                var accepted = spool.TryNextCycle(
                    moduleId, targetId, cycleId, cycleNumber, baseAmount, bonusPerCycle, maximumBonus,
                    atMs, cycleDurationMs, out var actual);
                Assert.True(accepted, "SDE build " + catalogBuild + " rejected cycle " + cycleNumber + ".");
                // the first delivery is x1.0 (the reference applies the bonus, then advances it): cycle n carries n - 1 bonuses
                var expectedBonus = Math.Min((cycle - 1) * bonusPerCycle, maximumBonus);
                var expected = Eve.Services.Space.Effects.ModuleAttributes.Six(baseAmount * (1 + expectedBonus));
                Assert.True(Math.Abs(actual - expected) < 0.000001,
                    "SDE build " + catalogBuild + " produced an incorrect mutadaptive amount at cycle " + cycle + ".");
                if (cycle == 1)
                {
                    firstAmount = actual;
                    Assert.Equal(Eve.Services.Space.Effects.ModuleAttributes.Six(baseAmount), actual);
                    Assert.True(spool.TryNextCycle(
                        moduleId, targetId, cycleId, cycleNumber, baseAmount, bonusPerCycle, maximumBonus,
                        atMs, cycleDurationMs, out var duplicate));
                    Assert.Equal(actual, duplicate);
                }
                cycleId++;
                cycleNumber++;
            }

            spool.Start(moduleId, targetId + 1);
            atMs += cycleDurationMs;
            cycleNumber = 1;
            Assert.True(spool.TryNextCycle(
                moduleId, targetId + 1, cycleId, cycleNumber, baseAmount, bonusPerCycle, maximumBonus,
                atMs, cycleDurationMs, out var switchedTarget));
            Assert.Equal(firstAmount, switchedTarget);
            cycleId++;

            spool.StopAfterCurrentCycle(moduleId);
            atMs += cycleDurationMs;
            cycleNumber = 2;
            Assert.True(spool.TryNextCycle(
                moduleId, targetId + 1, cycleId, cycleNumber, baseAmount, bonusPerCycle, maximumBonus,
                atMs, cycleDurationMs, out var finalCycle));
            // the second cycle of this run carries one bonus (the first of the run is x1.0)
            var finalBonus = Math.Min(1 * bonusPerCycle, maximumBonus);
            Assert.Equal(Eve.Services.Space.Effects.ModuleAttributes.Six(baseAmount * (1 + finalBonus)), finalCycle);
            spool.CompleteCycle(moduleId, cycleId);
            Assert.False(spool.TryNextCycle(
                moduleId, targetId + 1, cycleId, cycleNumber, baseAmount, bonusPerCycle, maximumBonus,
                atMs, cycleDurationMs, out _));
            cycleId++;

            spool.Start(moduleId, targetId + 1);
            atMs += cycleDurationMs;
            cycleNumber = 1;
            Assert.True(spool.TryNextCycle(
                moduleId, targetId + 1, cycleId, cycleNumber, baseAmount, bonusPerCycle, maximumBonus,
                atMs, cycleDurationMs, out var afterStop));
            Assert.Equal(firstAmount, afterStop);
            cycleId++;

            spool.Reset(moduleId);
            Assert.False(spool.TryNextCycle(
                moduleId, targetId + 1, cycleId, cycleNumber + 1, baseAmount, bonusPerCycle, maximumBonus,
                atMs + cycleDurationMs, cycleDurationMs, out _));
            spool.Start(moduleId, targetId + 1);
            atMs += cycleDurationMs;
            cycleNumber = 1;
            Assert.True(spool.TryNextCycle(
                moduleId, targetId + 1, cycleId, cycleNumber, baseAmount, bonusPerCycle, maximumBonus,
                atMs, cycleDurationMs, out var afterOffline));
            Assert.Equal(firstAmount, afterOffline);
            cycleId++;
            cycleNumber++;

            atMs += cycleDurationMs + 1;
            Assert.True(spool.TryNextCycle(
                moduleId, targetId + 1, cycleId, cycleNumber, baseAmount, bonusPerCycle, maximumBonus,
                atMs, cycleDurationMs, out var afterExpiredGap));
            Assert.Equal(firstAmount, afterExpiredGap);

            var stale = spool.TryNextCycle(
                moduleId, targetId + 1, cycleId - 1, cycleNumber - 1, baseAmount, bonusPerCycle, maximumBonus,
                atMs - cycleDurationMs - 1, cycleDurationMs, out _);
            Assert.False(stale);
            moduleId++;
        }
    }

    [PgFact]
    public async Task Published_module_activation_and_target_effect_gaps_are_enumerated_from_the_current_sde()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var moduleGroupIds = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7)
            .Select(row => row.Key)
            .ToArray();
        var publishedModuleTypeIds = (await sde.ReadByFieldAsync("types", "groupID", moduleGroupIds))
            .Where(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(row => row.Key)
            .ToArray();
        var assignedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await sde.ReadAsync("typeDogma", publishedModuleTypeIds))
        {
            if (!row.Doc.TryGetProperty("dogmaEffects", out var effects) || effects.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }
            foreach (var typeEffect in effects.EnumerateArray())
            {
                var effectId = typeEffect.GetProperty("effectID").GetInt32();
                if (statics.Effect(effectId) is { } effect
                    && (effect.Category == EffectDef.Activation || effect.Category == EffectDef.Target))
                {
                    assignedNames.Add(effect.Name.Trim());
                }
            }
        }

        // pending handlers (refusals) are not coverage, so their effects stay in currentSdeGaps
        var handledNames = Eve.Services.Space.Effects.ModuleEffects.Default.ImplementedNames
            .Concat(["missileLaunching", "torpedoLaunching"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncovered = assignedNames.Except(handledNames, StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] currentSdeGaps =
        [
            "barrage",
            "cargoScan",
            "cloneRespawnBay",
            "doHacking",
            "doomsdayAOEBubble",
            "doomsdayAOEDamp",
            "doomsdayAOEECM",
            "doomsdayAOENeut",
            "doomsdayAOEPaint",
            "doomsdayAOETrack",
            "doomsdayAOEWeb",
            "entosisLink",
            "hackOrbital",
            "industrialItemCompression",
            "microJumpPortalDrive",
            "microJumpPortalDriveCapital",
            "moduleBonusIndustrialInvulnerability",
            "moduleBonusWarfareLinkArmor",
            "moduleBonusWarfareLinkInfo",
            "moduleBonusWarfareLinkMining",
            "moduleBonusWarfareLinkShield",
            "moduleBonusWarfareLinkSkirmish",
            "moduleTitanEffectGenerator",
            "scanStrengthBonusPercentActivate",
            "scanStrengthTargetPercentBonus",
            "shipScan",
            
            "warpDisruptSphere",
        ];
        // Reconcile the manifest against the catalog this test actually opened. A newer unhandled
        // effect adds a failure; an older catalog simply does not assign effects introduced later.
        var expectedForCatalog = currentSdeGaps.Where(assignedNames.Contains).ToArray();
        Assert.True(
            expectedForCatalog.SequenceEqual(uncovered),
            $"Unexpected module effect gaps for SDE build {catalogBuild}: {string.Join(", ", uncovered)}");
    }

    [PgFact]
    public async Task A_precursor_weapon_carries_the_damage_ramp_its_sde_type_declares_on_its_turret()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var precursorTypes = await statics.TypesInGroupsAsync(["Precursor Weapon"]);
        var charge = (await statics.TypesInGroupsAsync(["Exotic Plasma Charge"])).First();
        var perCycleAttribute = statics.AttributeId("damageMultiplierBonusPerCycle")
            ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing damageMultiplierBonusPerCycle.");
        var maximumAttribute = statics.AttributeId("damageMultiplierBonusMax")
            ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing damageMultiplierBonusMax.");
        // the values of SDE build 3396210 that the ramp is pinned to: a Light Entropic Disintegrator I builds 0.07 a cycle up to 2.125, an Ultratidal one 0.05 up to 1.5
        var pinned = new Dictionary<int, (double PerCycle, double Maximum)> { [47272] = (0.07, 2.125), [52915] = (0.05, 1.5) };
        Assert.Contains(47272, precursorTypes.Select(type => type.TypeId));

        const long characterId = 73001;
        const long shipId = 73002;
        const long moduleId = 73003;
        const long chargeId = 73004;
        const long targetId = 73005;
        const int shipTypeId = 587;
        const int moduleFlag = 11;
        await statics.EnsureTypesAsync([shipTypeId]);
        foreach (var precursor in precursorTypes)
        {
            await statics.EnsureTypesAsync([precursor.TypeId, charge.TypeId]);
            var ship = new Eve.Inventory.Item(shipId, shipTypeId, characterId, 60003760, 0, 1, true, null);
            var module = new Eve.Inventory.Item(moduleId, precursor.TypeId, characterId, shipId, moduleFlag, 1, true, null);
            var loaded = new Eve.Inventory.Item(chargeId, charge.TypeId, characterId, shipId, moduleFlag, 1000, false, null);
            var world = new DogmaWorld(statics, characterId, shipId);
            world.Add(new DogmaItem(shipId, shipTypeId, characterId, ship.LocationId, ship.Flag, ItemState.Online));
            world.Add(new DogmaItem(moduleId, precursor.TypeId, characterId, shipId, moduleFlag, ItemState.Online));
            world.Add(new DogmaItem(chargeId, charge.TypeId, characterId, shipId, moduleFlag, ItemState.Online));
            var snapshot = new Eve.Ships.ShipSnapshot
            {
                Character = null!,
                Ship = ship,
                Fitted = [module],
                Charges = [loaded],
                Skills = [],
                World = world,
            };
            var result = Eve.Services.Space.ModulePackages.Of(snapshot, statics, moduleId, "targetDisintegratorAttack", targetId, 1);
            Assert.True(result.Reason is null, $"SDE build {catalogBuild}, type {precursor.TypeId}: {result.Reason}");
            var turret = Assert.IsType<Eve.Space.SimEffect>(result.Effect).Turret
                ?? throw new InvalidOperationException($"SDE build {catalogBuild}: type {precursor.TypeId} has no turret.");
            var perCycle = world.Get(moduleId, perCycleAttribute);
            var maximum = world.Get(moduleId, maximumAttribute);
            Assert.True(perCycle > 0, $"SDE build {catalogBuild}, type {precursor.TypeId} declares no bonus per cycle.");
            Assert.Equal(
                new Eve.Space.SimSpool(Eve.Services.Space.Effects.ModuleAttributes.Six(perCycle), Eve.Services.Space.Effects.ModuleAttributes.Six(maximum)),
                turret.Spool);
            if (pinned.TryGetValue(precursor.TypeId, out var expected))
            {
                Assert.Equal(new Eve.Space.SimSpool(expected.PerCycle, expected.Maximum), turret.Spool);
            }
        }
    }

    [PgFact]
    public async Task A_vorton_projector_explodes_without_a_roll_and_arcs_with_the_sde_numbers_its_type_declares()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var vortons = await statics.TypesInGroupsAsync(["Vorton Projector"]);
        var charge = (await statics.TypesInGroupsAsync(["Condenser Pack"])).First();
        int Attribute(string name) => statics.AttributeId(name)
            ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing {name}.");
        var cloudAttribute = Attribute("aoeCloudSize");
        var velocityAttribute = Attribute("aoeVelocity");
        var factorAttribute = Attribute("aoeDamageReductionFactor");
        var sensitivityAttribute = Attribute("aoeDamageReductionSensitivity");
        var rangeAttribute = Attribute("VortonArcRange");
        var targetsAttribute = Attribute("VortonArcTargets");
        var trackingAttribute = Attribute("trackingSpeed");
        Assert.NotEmpty(vortons);

        const long characterId = 74001;
        const long shipId = 74002;
        const long moduleId = 74003;
        const long chargeId = 74004;
        const long targetId = 74005;
        const int shipTypeId = 587;
        const int moduleFlag = 11;
        await statics.EnsureTypesAsync([shipTypeId]);
        foreach (var vorton in vortons)
        {
            await statics.EnsureTypesAsync([vorton.TypeId, charge.TypeId]);
            // a vorton carries no tracking speed, and the reference's defaults fill what the type does not declare
            Assert.False(vorton.Attributes.TryGetValue(trackingAttribute, out var tracking) && tracking > 0,
                $"SDE build {catalogBuild}, type {vorton.TypeId} declares a tracking speed.");
            var sensitivity = vorton.Attributes.TryGetValue(sensitivityAttribute, out var declared) ? declared : 5.5;
            var ship = new Eve.Inventory.Item(shipId, shipTypeId, characterId, 60003760, 0, 1, true, null);
            var module = new Eve.Inventory.Item(moduleId, vorton.TypeId, characterId, shipId, moduleFlag, 1, true, null);
            var loaded = new Eve.Inventory.Item(chargeId, charge.TypeId, characterId, shipId, moduleFlag, 1000, false, null);
            var world = new DogmaWorld(statics, characterId, shipId);
            world.Add(new DogmaItem(shipId, shipTypeId, characterId, ship.LocationId, ship.Flag, ItemState.Online));
            world.Add(new DogmaItem(moduleId, vorton.TypeId, characterId, shipId, moduleFlag, ItemState.Online));
            world.Add(new DogmaItem(chargeId, charge.TypeId, characterId, shipId, moduleFlag, ItemState.Online));
            var snapshot = new Eve.Ships.ShipSnapshot
            {
                Character = null!,
                Ship = ship,
                Fitted = [module],
                Charges = [loaded],
                Skills = [],
                World = world,
            };
            var result = Eve.Services.Space.ModulePackages.Of(snapshot, statics, moduleId, "ChainLightning", targetId, 1);
            Assert.True(result.Reason is null, $"SDE build {catalogBuild}, type {vorton.TypeId}: {result.Reason}");
            var turret = Assert.IsType<Eve.Space.SimEffect>(result.Effect).Turret
                ?? throw new InvalidOperationException($"SDE build {catalogBuild}: type {vorton.TypeId} has no turret.");
            Assert.Null(turret.Spool);
            Assert.Equal(
                new Eve.Space.SimAoe(
                    Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, cloudAttribute)),
                    Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, velocityAttribute)),
                    Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, factorAttribute)),
                    Eve.Services.Space.Effects.ModuleAttributes.Six(sensitivity)),
                turret.Aoe);
            Assert.Equal(
                new Eve.Space.SimArc(
                    Eve.Services.Space.Effects.ModuleAttributes.Six(world.Get(moduleId, rangeAttribute)),
                    (int)world.Get(moduleId, targetsAttribute)),
                turret.Arc);
        }
    }

    /// <summary>
    /// Two modules that modify the same non-stackable resonance (two armor hardeners of one kind on the armor's EM resonance) are told the value the world holds once
    /// both run: the second takes the stacking penalty the world applies, so it is not the first times its own, unpenalised, percentage.
    /// </summary>
    [PgFact]
    public async Task Two_modules_on_the_same_non_stackable_resonance_are_told_the_value_the_world_holds_with_both_running()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var catalogBuild = await sde.BuildAsync() ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var statics = await DogmaStatic.LoadAsync(sde);
        var effect = Assert.Single(statics.Effects, candidate => candidate.Name.Equals("modifyActiveArmorResonancePostPercent", StringComparison.OrdinalIgnoreCase));
        var resonance = statics.AttributeId("armorEmDamageResonance") ?? throw new InvalidOperationException($"SDE build {catalogBuild} is missing armorEmDamageResonance.");
        Assert.False(statics.Attribute(resonance)?.Stackable ?? true, $"SDE build {catalogBuild}: the penalty of this test needs a non-stackable resonance.");
        var modifying = effect.Modifiers.Single(modifier => modifier.ModifiedAttributeId == resonance).ModifyingAttributeId;

        var moduleGroupIds = (await sde.ReadAsync("groups")).Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7).Select(row => row.Key).ToArray();
        var moduleDogma = await sde.ReadAsync("typeDogma", (await sde.ReadByFieldAsync("types", "groupID", moduleGroupIds))
            .Where(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(row => row.Key).ToArray());
        // a hardener of the resonance that it modifies (the EM one: the others carry no value of this modifier), the first of the published ones in the build
        var candidates = moduleDogma.Where(row => row.Doc.TryGetProperty("dogmaEffects", out var effects)
                && effects.ValueKind == System.Text.Json.JsonValueKind.Array
                && effects.EnumerateArray().Any(typeEffect => typeEffect.GetProperty("effectID").GetInt32() == effect.Id))
            .Select(row => checked((int)row.Key)).OrderBy(id => id).ToArray();
        await statics.EnsureTypesAsync(candidates);
        var moduleTypeId = candidates.First(id => statics.Type(id) is { } type && type.Attributes.TryGetValue(modifying, out var value) && value != 0);
        var shipGroupIds = (await sde.ReadAsync("groups")).Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 6).Select(row => row.Key).ToArray();
        var shipTypeId = checked((int)(await sde.ReadByFieldAsync("types", "groupID", shipGroupIds))
            .First(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True).Key);
        await statics.EnsureTypesAsync([shipTypeId, moduleTypeId]);

        const long characterId = 76001, shipId = 76002, first = 76003, second = 76004;
        var ship = new Eve.Inventory.Item(shipId, shipTypeId, characterId, 60003760, 0, 1, true, null);
        var world = new DogmaWorld(statics, characterId, shipId);
        world.Add(new DogmaItem(shipId, shipTypeId, characterId, ship.LocationId, ship.Flag, ItemState.Online));
        world.Add(new DogmaItem(first, moduleTypeId, characterId, shipId, 11, ItemState.Online));
        world.Add(new DogmaItem(second, moduleTypeId, characterId, shipId, 12, ItemState.Online));
        var base0 = world.Get(shipId, resonance);
        var snapshot = new Eve.Ships.ShipSnapshot
        {
            Character = null!,
            Ship = ship,
            Fitted = [new Eve.Inventory.Item(first, moduleTypeId, characterId, shipId, 11, 1, true, null), new Eve.Inventory.Item(second, moduleTypeId, characterId, shipId, 12, 1, true, null)],
            Charges = [],
            Skills = [],
            World = world,
        };

        Assert.IsType<Eve.Space.SimEffect>(Eve.Services.Space.ModulePackages.Of(snapshot, statics, first, effect.Name, 0, 1).Effect);
        world.SetState(first, ItemState.Active);
        var secondPackage = Assert.IsType<Eve.Space.SimEffect>(Eve.Services.Space.ModulePackages.Of(snapshot, statics, second, effect.Name, 0, 1).Effect);
        var toldSecond = secondPackage.OnChanges.Single(change => change.ItemId == shipId && change.AttributeId == resonance).Value;
        world.SetState(second, ItemState.Active);
        // what the second is told is what the world holds with both running
        Assert.Equal(world.Get(shipId, resonance), toldSecond, 9);
        // and the penalty is in play: it is not the first's value times the second's own, unpenalised, percentage
        var unpenalised = base0 * (100 + world.Get(first, modifying)) / 100 * (100 + world.Get(second, modifying)) / 100;
        Assert.NotEqual(unpenalised, toldSecond, 9);
    }

    /// <summary>
    /// Every cloaking device of the static data (group "Cloaking Device") is refused on a ship that cannot cloak (canCloak 0) or disallows it (disallowCloaking), and is
    /// packaged as a cloak on a ship that does not. It tells the client no speed: no cloak type carries attribute 51, which the old package fabricated.
    /// </summary>
    [PgFact]
    public async Task Every_cloaking_device_is_refused_on_a_ship_that_cannot_cloak_or_disallows_it_and_tells_no_speed()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var cloaks = await statics.TypesInGroupsAsync(["Cloaking Device"]);
        Assert.NotEmpty(cloaks);
        var canCloak = statics.AttributeId("canCloak") ?? throw new InvalidOperationException("The SDE is missing canCloak.");
        var disallowCloaking = statics.AttributeId("disallowCloaking") ?? throw new InvalidOperationException("The SDE is missing disallowCloaking.");
        var shipGroupIds = (await sde.ReadAsync("groups")).Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 6).Select(row => row.Key).ToArray();
        var shipTypeId = checked((int)(await sde.ReadByFieldAsync("types", "groupID", shipGroupIds))
            .First(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Key);
        await statics.EnsureTypesAsync([shipTypeId]);

        foreach (var cloak in cloaks)
        {
            var cloakEffect = cloak.EffectIds.Select(statics.Effect).First(e => e is { Name: "cloaking" or "cloakingWarpSafe" or "cloakingPrototype" })!;
            const long characterId = 75001, shipId = 75002, moduleId = 75003;
            var ship = new Eve.Inventory.Item(shipId, shipTypeId, characterId, 60003760, 0, 1, true, null);
            var module = new Eve.Inventory.Item(moduleId, cloak.TypeId, characterId, shipId, 11, 1, true, null);
            var world = new DogmaWorld(statics, characterId, shipId);
            world.Add(new DogmaItem(shipId, shipTypeId, characterId, ship.LocationId, ship.Flag, ItemState.Online));
            world.Add(new DogmaItem(moduleId, cloak.TypeId, characterId, shipId, 11, ItemState.Online));
            var snapshot = new Eve.Ships.ShipSnapshot { Character = null!, Ship = ship, Fitted = [module], Charges = [], Skills = [], World = world };

            var packaged = Eve.Services.Space.ModulePackages.Of(snapshot, statics, moduleId, cloakEffect.Name, 0, 1);
            Assert.Null(packaged.Reason);
            var package = Assert.IsType<Eve.Space.SimEffect>(packaged.Effect);
            Assert.IsType<Eve.Space.SimAction.Cloak>(package.Action);
            Assert.DoesNotContain(package.OnChanges, change => change.AttributeId == Eve.Services.Space.Effects.ModuleAttributes.Speed);

            world.SetValue(shipId, canCloak, 0);
            Assert.Equal("CLOAK_SHIP_UNSUITABLE", Eve.Services.Space.ModulePackages.Of(snapshot, statics, moduleId, cloakEffect.Name, 0, 1).Reason);

            world.SetValue(shipId, canCloak, 1);
            world.SetValue(shipId, disallowCloaking, 1);
            Assert.Equal("CLOAK_DISALLOWED", Eve.Services.Space.ModulePackages.Of(snapshot, statics, moduleId, cloakEffect.Name, 0, 1).Reason);
        }
    }

    private static (Eve.Ships.ShipSnapshot Snapshot, DogmaStatic Statics, long ModuleId, long ShipId) Fixture(
    int effectId,
    string effectName,
    int category,
    IReadOnlyDictionary<int, double> moduleAttributes,
    IReadOnlyList<Modifier>? modifiers = null,
    IReadOnlyDictionary<int, double>? shipAttributes = null,
    Eve.Inventory.Item? charge = null,
    IReadOnlyDictionary<int, double>? chargeAttributes = null,
    IReadOnlyList<int>? additionalEffectIds = null,
    IReadOnlyList<EffectDef>? additionalEffects = null)
{
    const long characterId = 70001;
    const long shipId = 70002;
    const long moduleId = 70003;
    const int moduleFlag = 11;
    const int shipTypeId = 587;
    const int moduleTypeId = 90001;
    var effect = new EffectDef(effectId, effectName, category, modifiers ?? [])
    {
        Guid = $"effects.{char.ToUpperInvariant(effectName[0])}{effectName[1..]}",
        DurationAttributeId = 73,
        DischargeAttributeId = 6,
    };
    var shipType = new TypeDef(shipTypeId, 1, 6, shipAttributes ?? new Dictionary<int, double>(), []);
    var moduleEffectIds = new List<int> { effectId };
    if (additionalEffectIds is not null)
    {
        moduleEffectIds.AddRange(additionalEffectIds);
    }
    var moduleType = new TypeDef(moduleTypeId, 1, 7, moduleAttributes, moduleEffectIds, "Module");
    var types = new List<TypeDef> { shipType, moduleType };
    if (charge is { } loaded)
    {
        types.Add(new TypeDef(loaded.TypeId, 2, 8, chargeAttributes ?? new Dictionary<int, double>(), [], "Test charge"));
    }
    var effects = new List<EffectDef> { effect };
    if (additionalEffects is not null)
    {
        effects.AddRange(additionalEffects);
    }
    var statics = new DogmaStatic([], effects, types);
    var ship = new Eve.Inventory.Item(shipId, shipTypeId, characterId, 60003760, 0, 1, true, null);
    var module = new Eve.Inventory.Item(moduleId, moduleTypeId, characterId, shipId, moduleFlag, 1, true, null);
    var world = new DogmaWorld(statics, characterId, shipId);
    world.Add(new DogmaItem(shipId, shipTypeId, characterId, ship.LocationId, ship.Flag, ItemState.Online));
    world.Add(new DogmaItem(moduleId, moduleTypeId, characterId, shipId, moduleFlag, ItemState.Online));
    IReadOnlyList<Eve.Inventory.Item> charges = charge is { } item ? [item] : [];
    if (charge is { } loadedCharge)
    {
        world.Add(new DogmaItem(loadedCharge.Id, loadedCharge.TypeId, characterId, shipId, loadedCharge.Flag, ItemState.Online));
    }
    var snapshot = new Eve.Ships.ShipSnapshot
    {
        Character = null!,
        Ship = ship,
        Fitted = [module],
        Charges = charges,
        Skills = [],
        World = world,
    };
    return (snapshot, statics, moduleId, shipId);
}
}
