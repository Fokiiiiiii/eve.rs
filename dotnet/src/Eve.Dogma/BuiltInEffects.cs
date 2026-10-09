namespace Eve.Dogma;

/// <summary>
/// The effects the game implements in code rather than in the static data (the client's <c>dogma/fitting.py</c>, <c>online.py</c>,
/// <c>slotModifier.py</c>, <c>hardPointModifierEffect.py</c>): what running a module costs the ship and which slots and hard points it uses.
/// Here they are modifiers like any other, so that one engine works out every number.
/// </summary>
public static class BuiltInEffects
{
    // effects
    public const int Online = 16;
    public const int LauncherFitted = 40;
    public const int TurretFitted = 42;
    public const int RigSlot = 2663;
    public const int SubSystem = 3772;
    public const int HardPointModifier = 3773;
    public const int SlotModifier = 3774;
    public const int SelfRof = 1851;
    public const int DroneDamageBonus = 1730;
    public const int MissileEmDamageBonus = 660;
    public const int MissileExplosiveDamageBonus = 661;
    public const int MissileThermalDamageBonus = 662;
    public const int MissileKineticDamageBonus = 668;

    // attributes
    public const int IsOnline = 2;
    public const int PowerLoad = 15;
    public const int Power = 30;
    public const int Slots = 47;
    public const int CpuLoad = 49;
    public const int Cpu = 50;
    public const int LauncherSlotsLeft = 101;
    public const int TurretSlotsLeft = 102;
    public const int LowSlots = 12;
    public const int MedSlots = 13;
    public const int HiSlots = 14;
    public const int UpgradeLoad = 1152;
    public const int UpgradeCost = 1153;
    public const int UpgradeSlotsLeft = 1154;
    public const int TurretHardPointModifier = 1368;
    public const int LauncherHardPointModifier = 1369;
    public const int HiSlotModifier = 1374;
    public const int MedSlotModifier = 1375;
    public const int LowSlotModifier = 1376;
    public const int RateOfFire = 51;
    public const int DamageMultiplier = 64;
    public const int DamageMultiplierBonus = 292;
    public const int RofBonus = 293;
    public const int EmDamage = 114;
    public const int ExplosiveDamage = 116;
    public const int KineticDamage = 117;
    public const int ThermalDamage = 118;

    private static Modifier OnShip(Operation op, int modified, int modifying) => new(ModifierFunc.Item, "shipID", op, modified, modifying);

    /// <summary>What a skill with a bonus of this kind does: what needs the skill that carries the effect, on the character's things.</summary>
    private static Modifier OwnedBySkill(int modified, int modifying) =>
        new(ModifierFunc.OwnerRequiredSkill, "charID", Operation.PostPercent, modified, modifying, SkillTypeId: Modifier.OwnSkill);

    /// <summary>The effects, as the static data would describe them if it did.</summary>
    public static IReadOnlyList<EffectDef> All { get; } =
    [
        // running a module loads the ship's processor and power grid; the static data files this effect under activation, but it runs when the module is online
        new(Online, "online", EffectDef.Online, [OnShip(Operation.ModAdd, CpuLoad, Cpu), OnShip(Operation.ModAdd, PowerLoad, Power)]),
        new(TurretFitted, "turretFitted", EffectDef.Passive, [OnShip(Operation.ModSub, TurretSlotsLeft, Slots)]),
        new(LauncherFitted, "launcherFitted", EffectDef.Passive, [OnShip(Operation.ModSub, LauncherSlotsLeft, Slots)]),
        new(RigSlot, "rigSlot", EffectDef.Passive, [OnShip(Operation.ModSub, UpgradeSlotsLeft, Slots), OnShip(Operation.ModAdd, UpgradeLoad, UpgradeCost)]),
        new(SubSystem, "subSystem", EffectDef.Passive, []),
        new(HardPointModifier, "hardPointModifierEffect", EffectDef.Passive,
            [OnShip(Operation.ModAdd, LauncherSlotsLeft, LauncherHardPointModifier), OnShip(Operation.ModAdd, TurretSlotsLeft, TurretHardPointModifier)]),
        new(SlotModifier, "slotModifier", EffectDef.Passive,
            [OnShip(Operation.ModAdd, HiSlots, HiSlotModifier), OnShip(Operation.ModAdd, MedSlots, MedSlotModifier), OnShip(Operation.ModAdd, LowSlots, LowSlotModifier)]),

        // skill bonuses the static data leaves to code: a weapon skill's rate of fire, and the damage skills of drones and missiles
        new(SelfRof, "selfRof", EffectDef.Passive,
            [new Modifier(ModifierFunc.LocationRequiredSkill, "shipID", Operation.PostPercent, RateOfFire, RofBonus, SkillTypeId: Modifier.OwnSkill)]),
        new(DroneDamageBonus, "droneDmgBonus", EffectDef.Passive, [OwnedBySkill(DamageMultiplier, DamageMultiplierBonus)]),
        new(MissileEmDamageBonus, "missileEMDmgBonus", EffectDef.Passive, [OwnedBySkill(EmDamage, DamageMultiplierBonus)]),
        new(MissileExplosiveDamageBonus, "missileExplosiveDmgBonus", EffectDef.Passive, [OwnedBySkill(ExplosiveDamage, DamageMultiplierBonus)]),
        new(MissileThermalDamageBonus, "missileThermalDmgBonus", EffectDef.Passive, [OwnedBySkill(ThermalDamage, DamageMultiplierBonus)]),
        new(MissileKineticDamageBonus, "missileKineticDmgBonus2", EffectDef.Passive, [OwnedBySkill(KineticDamage, DamageMultiplierBonus)]),
    ];

    /// <summary>The effects the client throws away: the slot-use markers of the static data and the skill effect (a skill's level is given).</summary>
    public static readonly IReadOnlySet<int> Unwanted = new HashSet<int> { 11, 12, 13, 132 };

    /// <summary>The bastion module's effect (SDE 6658): the client's <c>bastionMode.py</c> blocks the micro jump drives of the ship with it, and the SDE carries no such modifier.</summary>
    public const int BastionMode = 6658;
    /// <summary>The skill a micro jump drive needs (type 4385, Micro Jump Drive Operation).</summary>
    public const int MicroJumpDriveOperation = 4385;
    /// <summary>The activation block a running module puts on what needs its skill, and the strength of it (the client's 1349 and 1350).</summary>
    public const int ActivationBlocked = 1349, ActivationBlockedStrength = 1350;

    /// <summary>The modifiers the client adds to an effect the static data leaves out, for the effects the loader reads (the client's table, approved for WP16: EVE University and the SDE agree that the bastion stops micro jump drives; the client's row is <c>SKILL_MODIFIER_CHANGES_FOR_LOCATION</c>, 4385 to 1349, in <c>bastionMode.py</c>).</summary>
    public static IReadOnlyList<Modifier> ClientModifiers(int effectId) =>
        effectId == BastionMode
            ? [new Modifier(ModifierFunc.LocationRequiredSkill, "shipID", Operation.ModAdd, ActivationBlocked, ActivationBlockedStrength, SkillTypeId: MicroJumpDriveOperation)]
            : [];

    /// <summary>The static data's effects with these in place of the ones they replace, and without the unwanted ones.</summary>
    public static IEnumerable<EffectDef> Merge(IEnumerable<EffectDef> fromStaticData)
    {
        var builtIn = All.ToDictionary(e => e.Id);
        foreach (var effect in fromStaticData)
        {
            if (!builtIn.ContainsKey(effect.Id) && !Unwanted.Contains(effect.Id))
            {
                yield return effect;
            }
        }
        foreach (var effect in builtIn.Values)
        {
            yield return effect;
        }
    }
}
