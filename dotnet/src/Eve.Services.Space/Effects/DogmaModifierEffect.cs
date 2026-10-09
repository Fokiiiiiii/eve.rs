using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

internal sealed class DogmaModifierEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } =
    [
        "cloneJumpAccepting",
        "gunneryMaxRangeFalloffTrackingSpeedBonus",
        "industrialCompactCoreEffect2",
        "industrialCoreEffect2",
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
        "scanStrengthBonusPercentActivate",
        "targetSpectrumBreakerBonus",
    ];

    /// <summary>
    /// The modifier effects whose body is not in the space host yet: the jump portal and the clone vat's acceptance. Their modifiers alone would be a
    /// success the reference does not give (the portal is never made), so they are refused by name (EFFECT_NOT_IMPLEMENTED), fuel or not. (The cynosural
    /// field generator has its body in <see cref="CynoGeneratorEffect"/>.)
    /// </summary>
    private static readonly HashSet<string> BodiesNotImplemented = new(StringComparer.OrdinalIgnoreCase)
    {
        "jumpPortalGeneration",
        "jumpPortalGenerationBO",
        "cloneJumpAccepting",
    };

    protected override string? Refusal(ModuleEffectContext module) =>
        BodiesNotImplemented.Contains(module.Effect.Name) ? PendingModuleEffect.NotImplemented : null;

    protected override SimEffect Build(ModuleEffectContext module) => BuildModifier(module);

    /// <summary>
    /// The effect of a module whose modifiers change the ship and the items of the ship as dogma works them out, as the client is told them. Shared with the
    /// warp core stabilizer and the interdiction nullifier (<see cref="WarpModifierEffect"/>), which add the simulation's part of their modifiers to it.
    /// </summary>
    internal static SimEffect BuildModifier(ModuleEffectContext module)
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
