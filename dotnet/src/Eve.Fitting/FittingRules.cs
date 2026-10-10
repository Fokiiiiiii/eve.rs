using Eve.Dogma;

namespace Eve.Fitting;

/// <summary>Why a module cannot be fitted where it was put.</summary>
public enum FitError
{
    InvalidRequest,
    InvalidFlag,
    ChargesUseLoadAmmo,
    TypeNotFittable,
    InvalidSlot,
    SlotOccupied,
    SkillRequired,
    InvalidShipType,
    InvalidShipGroup,
    MaxTypeFitted,
    MaxGroupFitted,
    RigSizeMismatch,
    InsufficientCalibration,
    NoTurretHardpoints,
    NoLauncherHardpoints,
    NotEnoughCpu,
    NotEnoughPower,
}

/// <summary>A refusal, with the numbers the message about it names (the skill and its levels, how many are fitted of how many allowed...).</summary>
public sealed record FitFailure(FitError Error, IReadOnlyDictionary<string, double> Data)
{
    public static FitFailure Of(FitError error, params (string Key, double Value)[] data) =>
        new(error, data.ToDictionary(d => d.Key, d => d.Value));
}

/// <summary>The rules of fitting a module to a ship, as the game keeps them (and the reference's validator lists them).</summary>
public static class FittingRules
{
    private const int ChargeCategory = 8;

    private const int TurretFittedEffect = BuiltInEffects.TurretFitted;
    private const int LauncherFittedEffect = BuiltInEffects.LauncherFitted;

    private const int RigSize = 1547;
    private const int UpgradeCapacity = 1132;
    private const int UpgradeCost = 1153;
    private const int MaxTypeFitted = 2431;
    private const int MaxGroupFitted = 1544;
    private const int CpuOutput = 48;
    private const int PowerOutput = 11;

    /// <summary>What a fitting may overshoot the processor or the power grid by and still be online (the values are kept to two places).</summary>
    private const double LoadSlack = 1e-6;

    /// <summary>The attributes that name the groups and the types of ship a module can be fitted to.</summary>
    private static readonly int[] CanFitShipGroups =
        [1298, 1299, 1300, 1301, 1872, 1879, 1880, 1881, 2065, 2396, 2476, 2477, 2478, 2479, 2480, 2481, 2482, 2483, 2484, 2485];

    private static readonly int[] CanFitShipTypes = [1302, 1303, 1304, 1305, 1944, 2103, 2463, 2486, 2487, 2488];

    /// <summary>The modules fitted to the ship now (not counting <paramref name="candidate"/>).</summary>
    public static IEnumerable<DogmaItem> Fitted(DogmaWorld world, long shipId) =>
        world.Items.Where(i => i.LocationId == shipId && Slots.IsSlotFlag(i.FlagId) && !world.IsCharge(i));

    /// <summary>The skills a type needs with the level of each.</summary>
    public static IEnumerable<(int Skill, int Level)> RequiredSkills(TypeDef type)
    {
        for (var i = 0; i < TypeDef.RequiredSkillAttributes.Length; i++)
        {
            if (type.Attributes.TryGetValue(TypeDef.RequiredSkillAttributes[i], out var skill) && skill > 0)
            {
                yield return ((int)skill, type.Attributes.TryGetValue(TypeDef.RequiredSkillLevelAttributes[i], out var level) ? (int)level : 1);
            }
        }
    }

    /// <summary>The first skill a type needs that its owner has not trained far enough, with the level it needs and the level the owner has.</summary>
    public static FitFailure? MissingSkill(DogmaWorld world, long ownerId, TypeDef type)
    {
        foreach (var (skill, level) in RequiredSkills(type))
        {
            var trained = world.Items.FirstOrDefault(i => i.OwnerId == ownerId && i.TypeId == skill) is { } item ? (int)world.Get(item.Id, DogmaWorld.SkillLevel) : 0;
            if (trained < level)
            {
                return FitFailure.Of(FitError.SkillRequired, ("skillTypeID", skill), ("requiredLevel", level), ("currentLevel", trained));
            }
        }
        return null;
    }

    /// <summary>Whether the ship's processor and power grid can run a fitted module that is offline now, on top of what already runs.</summary>
    public static FitError? OnlineShortage(DogmaWorld world, long shipId, long moduleId)
    {
        var cpu = world.Get(moduleId, BuiltInEffects.Cpu);
        if (cpu != 0 && world.Get(shipId, BuiltInEffects.CpuLoad) + cpu > world.Get(shipId, CpuOutput) + LoadSlack)
        {
            return FitError.NotEnoughCpu;
        }
        var power = world.Get(moduleId, BuiltInEffects.Power);
        if (power != 0 && world.Get(shipId, BuiltInEffects.PowerLoad) + power > world.Get(shipId, PowerOutput) + LoadSlack)
        {
            return FitError.NotEnoughPower;
        }
        return null;
    }

    /// <summary>Removes the pilot and skill sources, then keeps legal online modules while taking illegal ones offline.</summary>
    public static IReadOnlyList<(long ModuleId, ItemState Before, ItemState After)> RemovePilotModifiersAndRevalidate(
        DogmaWorld world, DogmaStatic statics, long characterId, long shipId,
        IEnumerable<long> skillSourceItemIds, IEnumerable<long> fittedModuleIds)
    {
        world.Remove(characterId);
        foreach (var skillId in skillSourceItemIds)
        {
            world.Remove(skillId);
        }

        var modules = fittedModuleIds.Distinct()
            .Select(id => (Id: id, State: world.Item(id)?.State ?? ItemState.Offline))
            .ToArray();
        foreach (var module in modules)
        {
            world.SetState(module.Id, ItemState.Offline);
        }

        var changes = new List<(long ModuleId, ItemState Before, ItemState After)>();
        foreach (var module in modules)
        {
            if (module.State < ItemState.Online || world.Item(module.Id) is not { } item)
            {
                continue;
            }

            var type = statics.Type(item.TypeId);
            var invalid = type is null
                || MissingSkill(world, characterId, type) is not null
                || OnlineShortage(world, shipId, module.Id) is not null;
            var next = invalid ? ItemState.Offline : ItemState.Online;
            world.SetState(module.Id, next);
            if (module.State != next)
            {
                changes.Add((module.Id, module.State, next));
            }
        }
        return changes;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> (not yet in the ship's fitting: the world holds what is already fitted) can be fitted to the
    /// ship in the slot <paramref name="flag"/>. The first rule broken is the one reported.
    /// </summary>
    public static FitFailure? Check(DogmaWorld world, DogmaStatic statics, long shipId, DogmaItem candidate, int flag)
    {
        if (world.Item(shipId) is not { } ship || statics.Type(candidate.TypeId) is not { } type || statics.Type(ship.TypeId) is not { } shipType || flag <= 0)
        {
            return FitFailure.Of(FitError.InvalidRequest);
        }
        if (!Slots.IsSlotFlag(flag))
        {
            return FitFailure.Of(FitError.InvalidFlag);
        }
        if (type.CategoryId == ChargeCategory)
        {
            return FitFailure.Of(FitError.ChargesUseLoadAmmo);
        }
        if (Slots.FamilyOfType(type) is not { } family)
        {
            return FitFailure.Of(FitError.TypeNotFittable);
        }
        if (!Slots.Flags(world, shipId, family).Contains(flag))
        {
            return FitFailure.Of(FitError.InvalidSlot);
        }
        if (family == SlotFamily.Subsystem &&
            type.Attributes.TryGetValue(1366, out var subsystemSlot) && (int)subsystemSlot != flag)
        {
            return FitFailure.Of(FitError.InvalidSlot);
        }
        var others = Fitted(world, shipId).Where(i => i.Id != candidate.Id).ToList();
        if (others.Any(i => i.FlagId == flag))
        {
            return FitFailure.Of(FitError.SlotOccupied);
        }

        if (MissingSkill(world, candidate.OwnerId, type) is { } missing)
        {
            return missing;
        }

        var groups = CanFitShipGroups.Where(type.Attributes.ContainsKey).Select(a => (int)type.Attributes[a]).Where(g => g > 0).ToList();
        if (groups.Count > 0 && !groups.Contains(shipType.GroupId))
        {
            return FitFailure.Of(FitError.InvalidShipGroup);
        }
        var types = CanFitShipTypes.Where(type.Attributes.ContainsKey).Select(a => (int)type.Attributes[a]).Where(t => t > 0).ToList();
        if (types.Count > 0 && !types.Contains(ship.TypeId))
        {
            return FitFailure.Of(FitError.InvalidShipType);
        }
        // a subsystem fits one hull (SDE fitsToShipType): a Legion's defensive subsystem is no Tengu's
        if (type.Attributes.TryGetValue(StrategicCruisers.FitsToShipTypeAttribute, out var fitsToShip) && (int)fitsToShip > 0 && (int)fitsToShip != ship.TypeId)
        {
            return FitFailure.Of(FitError.InvalidShipType);
        }

        var maxType = (int)world.Get(candidate.Id, MaxTypeFitted);
        if (maxType > 0)
        {
            var fittedOfType = others.Count(i => i.TypeId == candidate.TypeId);
            if (fittedOfType >= maxType)
            {
                return FitFailure.Of(FitError.MaxTypeFitted, ("maxTypeFitted", maxType), ("existingTypeCount", fittedOfType), ("shipTypeID", ship.TypeId), ("moduleTypeID", candidate.TypeId));
            }
        }
        var maxGroup = (int)world.Get(candidate.Id, MaxGroupFitted);
        if (maxGroup > 0)
        {
            var fittedOfGroup = others.Count(i => statics.Type(i.TypeId)?.GroupId == type.GroupId);
            if (fittedOfGroup >= maxGroup)
            {
                return FitFailure.Of(FitError.MaxGroupFitted, ("maxGroupFitted", maxGroup), ("existingGroupCount", fittedOfGroup), ("shipTypeID", ship.TypeId), ("moduleTypeID", candidate.TypeId));
            }
        }

        if (family == SlotFamily.Rig)
        {
            var shipRigSize = (int)world.Get(shipId, RigSize);
            var moduleRigSize = (int)world.Get(candidate.Id, RigSize);
            if (shipRigSize > 0 && moduleRigSize > 0 && shipRigSize != moduleRigSize)
            {
                return FitFailure.Of(FitError.RigSizeMismatch, ("shipRigSize", shipRigSize), ("moduleRigSize", moduleRigSize));
            }
            var capacity = world.Get(shipId, UpgradeCapacity);
            var load = others.Where(i => Slots.FamilyOfFlag(i.FlagId) == SlotFamily.Rig).Sum(i => world.Get(i.Id, UpgradeCost)) + world.Get(candidate.Id, UpgradeCost);
            if (capacity > 0 && load > capacity)
            {
                return FitFailure.Of(FitError.InsufficientCalibration, ("upgradeCapacity", capacity), ("upgradeLoad", load));
            }
        }

        if (type.EffectIds.Contains(TurretFittedEffect) && world.Get(shipId, BuiltInEffects.TurretSlotsLeft) < 1)
        {
            return FitFailure.Of(FitError.NoTurretHardpoints, ("turretHardpoints", 0));
        }
        if (type.EffectIds.Contains(LauncherFittedEffect) && world.Get(shipId, BuiltInEffects.LauncherSlotsLeft) < 1)
        {
            return FitFailure.Of(FitError.NoLauncherHardpoints, ("launcherHardpoints", 0));
        }
        return null;
    }

    /// <summary>The first free slot a module can go in, or null when there is none.</summary>
    public static int? FirstFreeSlot(DogmaWorld world, DogmaStatic statics, long shipId, DogmaItem candidate)
    {
        if (statics.Type(candidate.TypeId) is not { } type || Slots.FamilyOfType(type) is not { } family)
        {
            return null;
        }
        var taken = Fitted(world, shipId).Where(i => i.Id != candidate.Id).Select(i => i.FlagId).ToHashSet();
        foreach (var flag in Slots.Flags(world, shipId, family))
        {
            if (!taken.Contains(flag) && Check(world, statics, shipId, candidate, flag) is null)
            {
                return flag;
            }
        }
        return null;
    }
}
