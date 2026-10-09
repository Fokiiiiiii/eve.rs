using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The module effects the space host runs. To run another effect: write a <see cref="ShipEffect"/> that names it and builds its <see cref="SimEffect"/>, and add it here; to run
/// another projected effect add a line to <see cref="ProjectedEffect"/>. <see cref="EffectRegistry{THost, TResult}.Uncovered"/> lists what the static data has that is not here.
/// </summary>
internal static class ModuleEffects
{
    public static SensorBoosterEffect SensorBooster { get; } = new();

    /// <summary>
    /// The ship's values of the attributes with the item in the given state (and the extra modifiers), as dogma works them out: what an effect tells the client
    /// (<c>round</c>: to six places, as the reference tells them).
    /// </summary>
    internal static List<SimChange> ToldValues(DogmaWorld world, long itemId, ItemState state, IReadOnlyList<Modifier> modifiers, long shipId, int[] attributeIds, bool round)
    {
        using var assumed = world.Assume(itemId, state, modifiers);
        return [.. attributeIds.Select(id => new SimChange(shipId, id, round ? ModuleAttributes.Six(world.Get(shipId, id)) : world.Get(shipId, id)))];
    }

    public static EffectRegistry<ShipSnapshot, SimEffect> Default { get; } = new EffectRegistry<ShipSnapshot, SimEffect>()
        .Register(SensorBooster)
        .Register(new MiningLaserEffect())
        .Register(new GasHarvesterEffect())
        .Register(new SalvagerEffect())
        .Register(new PassiveTargetingEffect())
        .Register(new AutoTargetingEffect())
        .Register(new PropulsionEffect())
        .Register(new TurretEffect())
        .Register(new BarragePendingEffect())
        .Register(new MissileEffect())
        .Register(new RefusedLauncherEffect())
        .Register(new WarpDisruptSphereEffect())
        .Register(new RepairEffect())
        .Register(new ProjectedEffect())
        .Register(new SmartbombEffect())
        .Register(new EcmBurstEffect())
        .Register(new CloakingEffect())
        .Register(new MicroJumpDriveEffect())
        .Register(new DogmaModifierEffect())
        .Register(new TypeAttributeResistanceEffect())
        .Register(new ReactiveArmorHardenerEffect())
        .Register(new AncillaryRemoteArmorRepairEffect())
        .Register(new AncillaryRemoteShieldBoosterEffect())
        .Register(new PowerBoosterEffect())
        .Register(new FueledArmorRepairEffect())
        .Register(new FueledShieldBoostingEffect())
        // the Nexus invulnerability core: wired, its rules checked, refused as EFFECT_NOT_IMPLEMENTED until the fleet effect has a body (needs wire bump W2)
        .Register(new IndustrialInvulnerabilityEffect())
        // the superweapons of group 588 (not P.A.N.I.C.): wired, each refused by its own name until its body exists (Effects/SuperWeaponEffects.cs, docs/project/TODO.md section 12)
        .Register(new DoomsdayTitanEffect())
        .Register(new LanceEffect())
        .Register(new ReaperEffect())
        .Register(new DisruptiveLanceEffect())
        .Register(new ConeEffect())
        .Register(new HogEffect())
        .Register(new StructureHogEffect())
        // exploration, hacking and scanner effects: wired, refused as EFFECT_NOT_IMPLEMENTED until their bodies are written (see ExplorationModuleEffects.cs)
        .Register(new DataAnalyzerEffect())
        .Register(new DataSubverterEffect())
        .Register(new CargoScanEffect())
        .Register(new ShipScanEffect())
        .Register(new ScanStrengthActivateEffect())
        .Register(new ScanStrengthTargetEffect());
}

internal sealed class SensorBoosterEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["sensorBoosterActivePercentage"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var moduleId = module.ModuleId;
        var shipId = module.Snapshot.Ship.Id;
        var rangeBonus = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.MaxTargetRangeBonus, 0)));
        var resolutionBonus = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ScanResolutionBonus, 0)));
        SimChange[] told =
        [
            new(moduleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(moduleId, durationAttribute, ModuleAttributes.Three(duration)),
            new(moduleId, ModuleAttributes.MaxTargetRangeBonus, rangeBonus),
            new(moduleId, ModuleAttributes.ScanResolutionBonus, resolutionBonus),
        ];

        return new SimEffect(
            moduleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), shipId, null, null, told, told, null,
            new SimAction.Remote(new SimRemote.Damp(rangeBonus, resolutionBonus), 0, 0, false, "remoteSensorBoost"));
    }
}

internal sealed class MicroJumpDriveEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["microJumpDrive"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, _) = module.Cycle();
        var distance = module.Value(ModuleAttributes.MicroJumpDriveRange, 100_000);
        var cooldown = (long)module.Value(ModuleAttributes.ModuleReactivationDelay, 180_000);
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, 1, Math.Max(0, module.TargetId), null, null, [], [],
            Action: new SimAction.MicroJumpDrive(distance, cooldown));
    }
}

/// <summary>Starts the explicitly supported effects whose active behavior is supplied by SDE Dogma modifiers: the emergency hull energizer.</summary>
internal sealed class TypeAttributeResistanceEffect : ShipEffect
{
    private const string EmergencyHullEnergizer = "emergencyHullEnergizer";

    /// <summary>
    /// The hull resonances of the emergency hull energizer: the module's own hull attribute multiplies (PostMul) the ship's resonance of the same damage type
    /// (the client's <c>MODIFIER_CHANGES</c>, <c>emergencyHullEnergizer.py</c>). Both are found by name in the static data.
    /// </summary>
    private static readonly (string Module, string Ship)[] HullResonanceMultipliers =
    [
        ("hullEmDamageResonance", "emDamageResonance"),
        ("hullExplosiveDamageResonance", "explosiveDamageResonance"),
        ("hullKineticDamageResonance", "kineticDamageResonance"),
        ("hullThermalDamageResonance", "thermalDamageResonance"),
    ];

    public override IReadOnlyCollection<string> EffectNames { get; } = [EmergencyHullEnergizer];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var shipId = module.Snapshot.Ship.Id;
        // the (module attribute, ship attribute) pairs: the energizer's hull value multiplies the ship's resonance of the same damage type
        var pairs = HullResonanceMultipliers
            .Select(pair => (Module: module.Statics.AttributeId(pair.Module), Ship: module.Statics.AttributeId(pair.Ship)))
            .Where(pair => pair.Module is not null && pair.Ship is not null)
            .Select(pair => (Module: pair.Module!.Value, Ship: pair.Ship!.Value))
            .ToArray();
        var shipAttributeIds = pairs.Select(pair => pair.Ship).ToArray();
        // each resonance is multiplied by the module's own value, as a modifier of the world: a resonance that is pinned (a PostAssign, a Polarized
        // weapon fitted) keeps its pinned value, because the world applies its modifiers in order and the assignment comes last
        var multipliers = pairs.Select(pair => new Modifier(ModifierFunc.Item, "shipID", Operation.PostMul, pair.Ship, pair.Module)).ToArray();
        var on = ModuleEffects.ToldValues(module.World, module.ModuleId, ItemState.Active, multipliers, shipId, shipAttributeIds, round: true);
        var off = ModuleEffects.ToldValues(module.World, module.ModuleId, ItemState.Online, [], shipId, shipAttributeIds, round: false);
        // the energizer burns its own module out as it starts (emergencyHullEnergizer.py: damage = hp), and the module stays burned out when it stops
        var burnOut = new SimChange(module.ModuleId, ModuleAttributes.ModuleDamage, module.World.Get(module.ModuleId, ModuleAttributes.ModuleHitPoints));
        on.Add(burnOut);
        off.Add(burnOut);

        var repeatAttribute = module.Statics.AttributeId("disallowRepeatingActivation");
        var repeat = repeatAttribute is { } id && module.Value(id, 0) > 0
            ? 1
            : Math.Max(1, module.Repeat);
        SimChange[] told =
        [
            new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, repeat, Math.Max(0, module.TargetId),
            null, null, [.. told, .. on], [.. told, .. off]);
    }
}

internal sealed class DogmaModifierEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } =
    [
        "cloneJumpAccepting",
        "cynosuralGeneration",
        "gunneryMaxRangeFalloffTrackingSpeedBonus",
        "industrialCompactCoreEffect2",
        "industrialCoreEffect2",
        "interdictionNullifierBonus",
        "jumpPortalGeneration",
        "jumpPortalGenerationBO",
        "massEntanglerEffect5",
        "missileGuidanceComputerBonus4",
        "modifyActiveArmorResonancePostPercent",
        "modifyActiveShieldResonancePostPercent",
        "moduleBonusAssaultDamageControl",
        "moduleBonusBastionModule",
        "moduleBonusIntegratedSensorArray",
        "moduleBonusNetworkedSensorArray",
        "moduleBonusOmnidirectionalTrackingLink",
        "moduleBonusSiegeModule",
        "moduleBonusTriageModule",
        "targetSpectrumBreakerBonus",
        "warpCoreStabilizerActive",
    ];

    /// <summary>
    /// The modifier effects whose body is not in the space host yet: the cyno field, the jump portal and the clone vat's acceptance. Their modifiers alone would be a
    /// success the reference does not give (the field or portal is never made), so they are refused by name (EFFECT_NOT_IMPLEMENTED), fuel or not.
    /// </summary>
    private static readonly HashSet<string> BodiesNotImplemented = new(StringComparer.OrdinalIgnoreCase)
    {
        "cynosuralGeneration",
        "jumpPortalGeneration",
        "jumpPortalGenerationBO",
        "cloneJumpAccepting",
    };

    protected override string? Refusal(ModuleEffectContext module) =>
        BodiesNotImplemented.Contains(module.Effect.Name) ? PendingModuleEffect.NotImplemented : null;

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var shipId = module.Snapshot.Ship.Id;
        var candidates = module.Snapshot.Fitted
            .Select(fitted => module.World.Item(fitted.Id))
            .Append(module.World.Item(shipId))
            .Concat(module.Snapshot.Charges.Select(charge => module.World.Item(charge.Id)))
            .Append(module.Snapshot.Character is { } character ? module.World.Item(character.Id) : null)
            .OfType<DogmaItem>()
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToArray();

        long? DomainItemId(string? domain) => domain switch
        {
            "itemID" => module.ModuleId,
            "shipID" => shipId,
            "charID" => module.Snapshot.Character?.Id,
            "otherID" => module.Snapshot.ChargeOf(module.Module)?.Id,
            _ => null,
        };
        int SkillTypeId(Modifier modifier) =>
            modifier.SkillTypeId == Modifier.OwnSkill ? module.Module.TypeId : modifier.SkillTypeId;
        bool Reaches(Modifier modifier, DogmaItem target, long domainItemId)
        {
            var targetType = module.Statics.Type(target.TypeId);
            return modifier.Func switch
            {
                ModifierFunc.Item => target.Id == domainItemId,
                ModifierFunc.Location => target.LocationId == domainItemId,
                ModifierFunc.LocationGroup => target.LocationId == domainItemId && targetType?.GroupId == modifier.GroupId,
                ModifierFunc.LocationRequiredSkill => target.LocationId == domainItemId
                    && targetType?.RequiredSkills.Contains(SkillTypeId(modifier)) == true,
                ModifierFunc.OwnerRequiredSkill => target.OwnerId == domainItemId
                    && targetType?.RequiredSkills.Contains(SkillTypeId(modifier)) == true,
                _ => false,
            };
        }

        var applicable = new List<(long ItemId, Modifier Modifier)>();
        foreach (var modifier in module.Effect.Modifiers)
        {
            if (DomainItemId(modifier.Domain) is not { } domainItemId)
            {
                continue;
            }
            applicable.AddRange(candidates
                .Where(target => Reaches(modifier, target, domainItemId))
                .Select(target => (target.Id, modifier)));
        }

        var changed = applicable.Select(x => (x.ItemId, AttributeId: x.Modifier.ModifiedAttributeId)).Distinct().ToArray();
        var hasPhysicalChange = changed.Any(x => x.ItemId == shipId
            && x.AttributeId is ModuleAttributes.Mass or ModuleAttributes.MaxVelocity or ModuleAttributes.SignatureRadius);
        // what the module's own modifiers make of the world, as dogma works it out (precedence, stacking, a pinned value): active, and back to offline
        SimNumbers? Numbers(ItemState state)
        {
            if (!hasPhysicalChange)
            {
                return null;
            }
            using var assumed = module.World.Assume(module.ModuleId, state);
            return new SimNumbers(
                module.World.Get(shipId, ModuleAttributes.Mass),
                module.World.Get(shipId, ModuleAttributes.MaxVelocity),
                module.World.Get(shipId, ModuleAttributes.SignatureRadius));
        }
        IReadOnlyList<SimChange> Changes(ItemState state)
        {
            using var assumed = module.World.Assume(module.ModuleId, state);
            return
            [
                new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor),
                new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
                .. changed.Select(x => new SimChange(x.ItemId, x.AttributeId, module.World.Get(x.ItemId, x.AttributeId))),
            ];
        }

        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId),
            Numbers(ItemState.Active), Numbers(ItemState.Online), Changes(ItemState.Active), Changes(ItemState.Online));
    }
}

internal sealed class AncillaryRemoteArmorRepairEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["shipModuleAncillaryRemoteArmorRepairer"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var charge = module.Snapshot.ChargeOf(module.Module);
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        var amount = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ArmorDamageAmount, 0)));
        var chargedAmount = FueledArmorRepairEffect.ChargedAmount(module);
        var optimal = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.MaxRange, 0)));
        var falloff = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.FalloffEffectiveness, 0)));
        SimAction Remote(double repair) =>
            new SimAction.Remote(new SimRemote.Repair(SimLayer.Armor, repair), optimal, falloff, true, "remoteArmorRepair");
        var fallback = Remote(amount);
        SimAction action = charge is { Quantity: > 0 }
            ? new SimAction.Charged(Remote(chargedAmount), fallback, charge.TypeId, module.Module.Flag, (int)chargeRate, charge.Quantity)
            : fallback;
        SimChange[] told =
        [
            new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
            new(module.ModuleId, ModuleAttributes.MaxRange, optimal),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId),
            null, null, told, told, null, action);
    }
}


internal sealed class AncillaryRemoteShieldBoosterEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["shipModuleAncillaryRemoteShieldBooster"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var charge = module.Snapshot.ChargeOf(module.Module);
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        var amount = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ShieldBonus, 0)));
        var optimal = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.MaxRange, 0)));
        var falloff = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.FalloffEffectiveness, 0)));
        var remote = new SimAction.Remote(
            new SimRemote.Repair(SimLayer.Shield, amount), optimal, falloff, false, "shieldTransfer");
        SimAction action = charge is { Quantity: > 0 }
            ? new SimAction.Charged(remote, null, charge.TypeId, module.Module.Flag, (int)chargeRate, charge.Quantity)
            : remote;
        SimChange[] told =
        [
            new(module.ModuleId, ModuleAttributes.CapacitorNeed, charge is { Quantity: > 0 } ? 0 : capacitor),
            new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
            new(module.ModuleId, ModuleAttributes.MaxRange, optimal),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, charge is { Quantity: > 0 } ? 0 : capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId),
            null, null, told, told, null, action);
    }
}

/// <summary>Starts effects whose activation behavior is defined by the SDE's Dogma modifiers.</summary>
