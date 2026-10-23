using Eve.Dogma;

namespace Eve.Fitting.Tests;

/// <summary>The rules of fitting, one at a time, on a small made-up ship.</summary>
public class RulesTests
{
    private const int Char = 1, ShipId = 1000, ShipType = 601, ShipGroup = 237, Skill = 3300;
    private const long Station = 60_000_001;

    private static readonly AttributeDef[] Attributes =
    [
        new(12, "lowSlots", 0, true, true, 4), new(13, "medSlots", 0, true, true, 4), new(14, "hiSlots", 0, true, true, 4), new(1137, "rigSlots", 0, true, true, 4),
        new(1366, "subSystemSlot", 0, true, true, 4), new(1367, "maxSubSystems", 0, true, true, 4), new(101, "launcherSlotsLeft", 0, true, true, 4), new(102, "turretSlotsLeft", 0, true, true, 4),
        new(47, "slots", 1, true, true, 4), new(280, "skillLevel", 0, true, true, 4), new(1547, "rigSize", 0, true, true, 4),
        new(1132, "upgradeCapacity", 0, true, true, 4), new(1153, "upgradeCost", 0, true, true, 4), new(2431, "maxTypeFitted", 0, true, true, 4),
        new(1544, "maxGroupFitted", 0, true, true, 4), new(1298, "canFitShipGroup01", 0, true, true, 4), new(1302, "canFitShipType1", 0, true, true, 4),
        new(182, "requiredSkill1", 0, true, true, 4), new(277, "requiredSkill1Level", 0, true, true, 4),
    ];

    private const int HighModule = 5001, MediumModule = 5002, LowModule = 5003, Rig = 5004, Turret = 5005, Launcher = 5006, Charge = 5007, Plain = 5008, SkilledModule = 5009, SubsystemModule = 5010;

    private static TypeDef Module(int type, int group, int category, Dictionary<int, double>? attributes, params int[] effects) =>
        new(type, group, category, attributes ?? new Dictionary<int, double>(), effects);

    private static DogmaStatic Statics(Dictionary<int, double>? shipAttributes = null) => new(
        Attributes, BuiltInEffects.Merge([]),
        [
            new TypeDef(ShipType, ShipGroup, 6, shipAttributes ?? new Dictionary<int, double> { [14] = 3, [13] = 2, [12] = 2, [1137] = 3, [102] = 2, [101] = 1, [1547] = 1, [1132] = 400 }, []),
            new TypeDef(1373, 1, 1, new Dictionary<int, double>(), []),
            new TypeDef(Skill, 255, 16, new Dictionary<int, double>(), []),
            Module(HighModule, 53, 7, null, 12),
            Module(MediumModule, 46, 7, null, 13),
            Module(LowModule, 60, 7, null, 11),
            Module(Rig, 773, 7, new Dictionary<int, double> { [1547] = 1, [1153] = 150 }, BuiltInEffects.RigSlot),
            Module(Turret, 55, 7, new Dictionary<int, double> { [47] = 1 }, 12, BuiltInEffects.TurretFitted),
            Module(Launcher, 507, 7, new Dictionary<int, double> { [47] = 1 }, 12, BuiltInEffects.LauncherFitted),
            Module(Charge, 86, 8, null, 12),
            Module(Plain, 1, 4, null),
            Module(SkilledModule, 53, 7, new Dictionary<int, double> { [182] = Skill, [277] = 3 }, 12),
            Module(SubsystemModule, 954, 7, new Dictionary<int, double> { [1366] = 126 }, BuiltInEffects.SubSystem),
        ]);

    private static (DogmaWorld World, DogmaStatic Statics) Ship(Dictionary<int, double>? shipAttributes = null, int skillLevel = 0, DogmaStatic? statics = null)
    {
        statics ??= Statics(shipAttributes);
        var world = new DogmaWorld(statics, Char, ShipId);
        world.Add(new DogmaItem(Char, 1373, Char, ShipId, 57));
        world.Add(new DogmaItem(ShipId, ShipType, Char, Station, 4));
        if (skillLevel > 0)
        {
            world.Add(new DogmaItem(50, Skill, Char, Char, 7));
            world.SetValue(50, DogmaWorld.SkillLevel, skillLevel);
        }
        return (world, statics);
    }

    private static DogmaItem Candidate(DogmaWorld world, int type, long id = 2000)
    {
        var item = new DogmaItem(id, type, Char, Station, 4);
        world.Add(item);
        return item;
    }

    private static FitError? Why(DogmaWorld world, DogmaStatic statics, DogmaItem item, int flag) => FittingRules.Check(world, statics, ShipId, item, flag)?.Error;

    private static void Fit(DogmaWorld world, int type, int flag, long id)
    {
        world.Add(new DogmaItem(id, type, Char, ShipId, flag, ItemState.Offline));
    }

    [Fact]
    public void A_module_that_fits_is_accepted_in_a_slot_of_its_kind_the_ship_has()
    {
        var (world, statics) = Ship();
        Assert.Null(FittingRules.Check(world, statics, ShipId, Candidate(world, HighModule), Slots.HighFirst));
        Assert.Null(FittingRules.Check(world, statics, ShipId, Candidate(world, MediumModule, 2001), Slots.MediumFirst + 1));
        Assert.Null(FittingRules.Check(world, statics, ShipId, Candidate(world, LowModule, 2002), Slots.LowFirst));
        Assert.Null(FittingRules.Check(world, statics, ShipId, Candidate(world, Rig, 2003), Slots.RigFirst + 2));
    }

    [Fact]
    public void Nothing_is_fitted_without_a_ship_a_module_or_a_slot_flag()
    {
        var (world, statics) = Ship();
        var module = Candidate(world, HighModule);
        Assert.Equal(FitError.InvalidRequest, FittingRules.Check(world, statics, 9999, module, Slots.HighFirst)?.Error);
        Assert.Equal(FitError.InvalidRequest, Why(world, statics, module, 0));
        Assert.Equal(FitError.InvalidRequest, Why(world, statics, new DogmaItem(2222, 987654, Char, Station, 4), Slots.HighFirst)); // a type nobody knows
        Assert.Equal(FitError.InvalidFlag, Why(world, statics, module, 4));   // the hangar is no slot
        Assert.Equal(FitError.InvalidFlag, Why(world, statics, module, 5));   // nor is the cargo
        Assert.Equal(FitError.InvalidFlag, Why(world, statics, module, 35));  // one past the high slots
    }

    [Fact]
    public void A_charge_is_loaded_not_fitted_and_a_thing_that_is_no_module_is_not_fittable()
    {
        var (world, statics) = Ship();
        Assert.Equal(FitError.ChargesUseLoadAmmo, Why(world, statics, Candidate(world, Charge), Slots.HighFirst));
        Assert.Equal(FitError.TypeNotFittable, Why(world, statics, Candidate(world, Plain, 2001), Slots.HighFirst));
    }

    [Theory]
    [InlineData(HighModule, 27, null)]
    [InlineData(HighModule, 29, null)]
    [InlineData(HighModule, 30, FitError.InvalidSlot)]      // the ship has three high slots: 27, 28, 29
    [InlineData(HighModule, 19, FitError.InvalidSlot)]      // a medium slot is no place for a high module
    [InlineData(MediumModule, 20, null)]
    [InlineData(MediumModule, 21, FitError.InvalidSlot)]    // two medium slots
    [InlineData(LowModule, 12, null)]
    [InlineData(LowModule, 13, FitError.InvalidSlot)]       // two low slots
    [InlineData(Rig, 94, null)]
    [InlineData(Rig, 95, FitError.InvalidSlot)]             // three rig slots
    public void A_module_goes_in_the_slots_of_its_kind_the_ship_has(int type, int flag, FitError? expected)
    {
        var (world, statics) = Ship();
        Assert.Equal(expected, Why(world, statics, Candidate(world, type), flag));
    }

    [Fact]
    public void A_subsystem_must_match_its_designated_slot_role()
    {
        var (world, statics) = Ship(new Dictionary<int, double> { [1367] = 5 });
        Assert.Equal(FitError.InvalidSlot, Why(world, statics, Candidate(world, SubsystemModule), 125));
    }

    [Fact]
    public void An_occupied_slot_refuses_and_the_module_itself_does_not_count_as_in_its_way()
    {
        var (world, statics) = Ship();
        Fit(world, HighModule, Slots.HighFirst, 3000);
        var other = Candidate(world, HighModule);
        Assert.Equal(FitError.SlotOccupied, Why(world, statics, other, Slots.HighFirst));
        Assert.Null(Why(world, statics, other, Slots.HighFirst + 1));
        // moving a fitted module to another slot: it is not its own obstacle
        Assert.Null(FittingRules.Check(world, statics, ShipId, world.Item(3000)!, Slots.HighFirst));
    }

    [Fact]
    public void A_module_needs_the_skills_it_names_at_the_level_it_names()
    {
        var (none, s0) = Ship();
        var failure = FittingRules.Check(none, s0, ShipId, Candidate(none, SkilledModule), Slots.HighFirst);
        Assert.Equal(FitError.SkillRequired, failure?.Error);
        Assert.Equal((Skill, 3, 0), (failure!.Data["skillTypeID"], failure.Data["requiredLevel"], failure.Data["currentLevel"]));

        var (two, s2) = Ship(skillLevel: 2);
        Assert.Equal(2, FittingRules.Check(two, s2, ShipId, Candidate(two, SkilledModule), Slots.HighFirst)!.Data["currentLevel"]);

        var (three, s3) = Ship(skillLevel: 3);
        Assert.Null(FittingRules.Check(three, s3, ShipId, Candidate(three, SkilledModule), Slots.HighFirst));
    }

    [Fact]
    public void Boarding_skill_check_requires_each_hull_skill_at_its_published_level()
    {
        var shipAttributes = new Dictionary<int, double> { [182] = Skill, [277] = 3 };
        var type = Statics(shipAttributes).Type(ShipType)!;

        Assert.False(FittingRules.HasRequiredSkills(type, _ => 2));
        Assert.True(FittingRules.HasRequiredSkills(type, _ => 3));
    }

    [Fact]
    public void A_module_that_fits_only_some_groups_or_types_of_ship_says_so()
    {
        var restricted = new DogmaStatic(Attributes, BuiltInEffects.Merge([]),
        [
            new TypeDef(ShipType, ShipGroup, 6, new Dictionary<int, double> { [14] = 3 }, []),
            new TypeDef(1373, 1, 1, new Dictionary<int, double>(), []),
            Module(6001, 53, 7, new Dictionary<int, double> { [1298] = 999 }, 12),        // only ships of group 999
            Module(6002, 53, 7, new Dictionary<int, double> { [1298] = ShipGroup }, 12),   // ships of this group
            Module(6003, 53, 7, new Dictionary<int, double> { [1302] = 12345 }, 12),      // only the ship of type 12345
            Module(6004, 53, 7, new Dictionary<int, double> { [1302] = ShipType }, 12),
        ]);
        var (world, statics) = Ship(statics: restricted);
        Assert.Equal(FitError.InvalidShipGroup, Why(world, statics, Candidate(world, 6001), Slots.HighFirst));
        Assert.Null(Why(world, statics, Candidate(world, 6002, 2001), Slots.HighFirst));
        Assert.Equal(FitError.InvalidShipType, Why(world, statics, Candidate(world, 6003, 2002), Slots.HighFirst));
        Assert.Null(Why(world, statics, Candidate(world, 6004, 2003), Slots.HighFirst));
    }

    [Fact]
    public void A_module_that_names_both_groups_and_types_of_ship_fits_a_ship_of_either()
    {
        // the groups and the single types a module names are alternatives: a command burst names its groups and some types
        var restricted = new DogmaStatic(Attributes, BuiltInEffects.Merge([]),
        [
            new TypeDef(ShipType, ShipGroup, 6, new Dictionary<int, double> { [14] = 3 }, []),
            new TypeDef(1373, 1, 1, new Dictionary<int, double>(), []),
            Module(6005, 53, 7, new Dictionary<int, double> { [1298] = 999, [1302] = ShipType }, 12),   // the ship's type, not its group
            Module(6006, 53, 7, new Dictionary<int, double> { [1298] = ShipGroup, [1302] = 12345 }, 12), // the ship's group, not its type
            Module(6007, 53, 7, new Dictionary<int, double> { [1298] = 999, [1302] = 12345 }, 12),      // neither
        ]);
        var (world, statics) = Ship(statics: restricted);
        Assert.Null(Why(world, statics, Candidate(world, 6005, 2004), Slots.HighFirst));
        Assert.Null(Why(world, statics, Candidate(world, 6006, 2005), Slots.HighFirst));
        Assert.Equal(FitError.InvalidShipType, Why(world, statics, Candidate(world, 6007, 2006), Slots.HighFirst));
    }

    [Fact]
    public void Only_so_many_of_a_type_or_of_a_group_may_be_fitted()
    {
        var limited = new DogmaStatic(Attributes, BuiltInEffects.Merge([]),
        [
            new TypeDef(ShipType, ShipGroup, 6, new Dictionary<int, double> { [14] = 4 }, []),
            new TypeDef(1373, 1, 1, new Dictionary<int, double>(), []),
            Module(6100, 53, 7, new Dictionary<int, double> { [2431] = 1 }, 12),   // one of this type
            Module(6101, 77, 7, new Dictionary<int, double> { [1544] = 2 }, 12),   // two of this group
            Module(6102, 77, 7, new Dictionary<int, double> { [1544] = 2 }, 12),   // the same group, another type
        ]);
        var (world, statics) = Ship(statics: limited);
        Fit(world, 6100, Slots.HighFirst, 3000);
        var second = FittingRules.Check(world, statics, ShipId, Candidate(world, 6100), Slots.HighFirst + 1);
        Assert.Equal(FitError.MaxTypeFitted, second?.Error);
        Assert.Equal((1, 1), (second!.Data["maxTypeFitted"], second.Data["existingTypeCount"]));

        Fit(world, 6101, Slots.HighFirst + 1, 3001);
        Fit(world, 6102, Slots.HighFirst + 2, 3002);
        var third = FittingRules.Check(world, statics, ShipId, Candidate(world, 6101, 2001), Slots.HighFirst + 3);
        Assert.Equal(FitError.MaxGroupFitted, third?.Error);
        Assert.Equal((2, 2), (third!.Data["maxGroupFitted"], third.Data["existingGroupCount"]));
    }

    [Fact]
    public void A_rig_must_be_the_size_of_the_ship_and_within_what_the_ship_can_calibrate()
    {
        var rigs = new DogmaStatic(Attributes, BuiltInEffects.Merge([]),
        [
            new TypeDef(ShipType, ShipGroup, 6, new Dictionary<int, double> { [1137] = 3, [1547] = 1, [1132] = 400 }, []),
            new TypeDef(1373, 1, 1, new Dictionary<int, double>(), []),
            Module(7001, 773, 7, new Dictionary<int, double> { [1547] = 2, [1153] = 50 }, BuiltInEffects.RigSlot),   // a medium rig
            Module(7002, 773, 7, new Dictionary<int, double> { [1547] = 1, [1153] = 150 }, BuiltInEffects.RigSlot),  // a small one costing 150
        ]);
        var (world, statics) = Ship(statics: rigs);
        Assert.Equal(FitError.RigSizeMismatch, Why(world, statics, Candidate(world, 7001), Slots.RigFirst));

        Fit(world, 7002, Slots.RigFirst, 3000);
        Fit(world, 7002, Slots.RigFirst + 1, 3001);
        var third = FittingRules.Check(world, statics, ShipId, Candidate(world, 7002), Slots.RigFirst + 2);
        Assert.Equal(FitError.InsufficientCalibration, third?.Error); // 450 of 400
        Assert.Equal((400, 450), (third!.Data["upgradeCapacity"], third.Data["upgradeLoad"]));
    }

    [Fact]
    public void A_rig_that_fills_the_calibration_exactly_is_allowed()
    {
        var rigs = new DogmaStatic(Attributes, BuiltInEffects.Merge([]),
        [
            new TypeDef(ShipType, ShipGroup, 6, new Dictionary<int, double> { [1137] = 3, [1547] = 1, [1132] = 450 }, []),
            new TypeDef(1373, 1, 1, new Dictionary<int, double>(), []),
            Module(7002, 773, 7, new Dictionary<int, double> { [1547] = 1, [1153] = 150 }, BuiltInEffects.RigSlot),
        ]);
        var (world, statics) = Ship(statics: rigs);
        Fit(world, 7002, Slots.RigFirst, 3000);
        Fit(world, 7002, Slots.RigFirst + 1, 3001);
        Assert.Null(Why(world, statics, Candidate(world, 7002), Slots.RigFirst + 2)); // 450 of 450
    }

    [Fact]
    public void Current_sde_rig_sizes_must_match_and_installed_rigs_share_calibration()
    {
        // CCP SDE build 3569502: Rifter (587) is size 1 with 400 calibration;
        // Small Projectile Collision Accelerator II (31686) costs 300, while
        // Medium Hybrid Collision Accelerator II (31546) is size 2 and costs 300.
        var statics = new DogmaStatic(Attributes, BuiltInEffects.Merge([]),
        [
            new TypeDef(587, 25, 6, new Dictionary<int, double> { [1137] = 3, [1547] = 1, [1132] = 400 }, []),
            new TypeDef(1373, 1, 1, new Dictionary<int, double>(), []),
            Module(31686, 777, 7, new Dictionary<int, double> { [1547] = 1, [1153] = 300 }, BuiltInEffects.RigSlot),
            Module(31546, 776, 7, new Dictionary<int, double> { [1547] = 2, [1153] = 300 }, BuiltInEffects.RigSlot),
        ]);
        var world = new DogmaWorld(statics, Char, ShipId);
        world.Add(new DogmaItem(Char, 1373, Char, ShipId, 57));
        world.Add(new DogmaItem(ShipId, 587, Char, Station, 4));

        Assert.Null(Why(world, statics, Candidate(world, 31686, 2000), Slots.RigFirst));
        Fit(world, 31686, Slots.RigFirst, 3000);
        Assert.Equal(FitError.RigSizeMismatch, Why(world, statics, Candidate(world, 31546, 2001), Slots.RigFirst + 1));

        var second = FittingRules.Check(world, statics, ShipId, Candidate(world, 31686, 2002), Slots.RigFirst + 1);
        Assert.Equal(FitError.InsufficientCalibration, second?.Error);
        Assert.Equal((400d, 600d), (second!.Data["upgradeCapacity"], second.Data["upgradeLoad"]));
    }

    [Fact]
    public void A_ship_never_has_more_than_eight_slots_of_a_kind()
    {
        var (world, statics) = Ship(new Dictionary<int, double> { [14] = 12, [13] = 2, [12] = 2 });
        Assert.Equal(8, Slots.Count(world, ShipId, SlotFamily.High));
        Assert.Equal(Enumerable.Range(Slots.HighFirst, 8), Slots.Flags(world, ShipId, SlotFamily.High));
    }

    private static (DogmaWorld World, DogmaStatic Statics) Loaded()
    {
        var statics = new DogmaStatic(Attributes, BuiltInEffects.Merge([]),
        [
            new TypeDef(ShipType, ShipGroup, 6, new Dictionary<int, double> { [48] = 100, [11] = 50, [14] = 4 }, []),
            new TypeDef(1373, 1, 1, new Dictionary<int, double>(), []),
            new TypeDef(Skill, 255, 16, new Dictionary<int, double>(), []),
            Module(7101, 53, 7, new Dictionary<int, double> { [50] = 60, [30] = 10 }, 12, BuiltInEffects.Online),
            Module(7102, 53, 7, new Dictionary<int, double> { [50] = 40, [30] = 40 }, 12, BuiltInEffects.Online),
            Module(7103, 53, 7, new Dictionary<int, double> { [50] = 40.5 }, 12, BuiltInEffects.Online),
            Module(7104, 53, 7, new Dictionary<int, double> { [30] = 40.5 }, 12, BuiltInEffects.Online),
            Module(7105, 53, 7, null, 12, BuiltInEffects.Online),
            Module(7106, 53, 7, new Dictionary<int, double> { [182] = Skill, [277] = 3, [50] = 1 }, 12, BuiltInEffects.Online),
        ]);
        var (world, _) = Ship(statics: statics);
        world.Add(new DogmaItem(3000, 7101, Char, ShipId, Slots.HighFirst, ItemState.Online));
        foreach (var (id, type, flag) in new[] { (3001L, 7102, 1), (3002L, 7103, 2), (3003L, 7104, 3), (3004L, 7105, 4) })
        {
            world.Add(new DogmaItem(id, type, Char, ShipId, Slots.HighFirst + flag, ItemState.Offline));
        }
        return (world, statics);
    }

    [Theory]
    [InlineData(3001, null)]                       // 60 + 40 is 100 of 100, 10 + 40 is 50 of 50
    [InlineData(3002, FitError.NotEnoughCpu)]
    [InlineData(3003, FitError.NotEnoughPower)]
    [InlineData(3004, null)]
    public void A_module_is_allowed_the_processor_and_power_it_exactly_fills_and_refused_what_it_would_overrun(long module, FitError? expected)
    {
        var (world, _) = Loaded();
        Assert.Equal(expected, FittingRules.OnlineShortage(world, ShipId, module));
    }

    [Fact]
    public void The_first_skill_a_module_needs_and_the_owner_lacks_is_named()
    {
        var (world, statics) = Ship(skillLevel: 2);
        var needs3 = statics.Type(SkilledModule)!;
        var failure = FittingRules.MissingSkill(world, Char, needs3)!;
        Assert.Equal(FitError.SkillRequired, failure.Error);
        Assert.Equal((Skill, 3, 2), (failure.Data["skillTypeID"], failure.Data["requiredLevel"], failure.Data["currentLevel"]));
        var (untrained, _) = Ship();
        untrained.Add(new DogmaItem(51, Skill, 2, 2, 7)); // another character's Gunnery, trained to the top, is not the pilot's
        untrained.SetValue(51, DogmaWorld.SkillLevel, 5);
        Assert.Equal(FitError.SkillRequired, FittingRules.MissingSkill(untrained, Char, needs3)?.Error);
        Assert.Null(FittingRules.MissingSkill(world, Char, statics.Type(Plain)!));
        Assert.Null(FittingRules.MissingSkill(Ship(skillLevel: 3).World, Char, needs3));
        Assert.Equal(0, FittingRules.MissingSkill(Ship().World, Char, needs3)!.Data["currentLevel"]); // not trained at all
    }

    [Fact]
    public void Turrets_and_launchers_need_a_hard_point_each()
    {
        var (world, statics) = Ship(); // two turret hard points, one launcher
        Assert.Null(Why(world, statics, Candidate(world, Turret), Slots.HighFirst));
        Fit(world, Turret, Slots.HighFirst, 3000);
        Assert.Null(Why(world, statics, Candidate(world, Turret, 2001), Slots.HighFirst + 1));
        Fit(world, Turret, Slots.HighFirst + 1, 3001);
        Assert.Equal(FitError.NoTurretHardpoints, Why(world, statics, Candidate(world, Turret, 2002), Slots.HighFirst + 2));
        Assert.Null(Why(world, statics, Candidate(world, HighModule, 2003), Slots.HighFirst + 2)); // a module that is no turret does not need one

        Assert.Null(Why(world, statics, Candidate(world, Launcher, 2004), Slots.HighFirst + 2));
        Fit(world, Launcher, Slots.HighFirst + 2, 3002);
        var (full, s) = Ship(new Dictionary<int, double> { [14] = 3, [101] = 0, [102] = 0 });
        Assert.Equal(FitError.NoLauncherHardpoints, Why(full, s, Candidate(full, Launcher), Slots.HighFirst));
    }

    [Fact]
    public void The_first_free_slot_of_the_right_kind_is_found_and_none_is_found_when_all_are_taken()
    {
        var (world, statics) = Ship();
        var module = Candidate(world, HighModule);
        Assert.Equal(Slots.HighFirst, FittingRules.FirstFreeSlot(world, statics, ShipId, module));
        Fit(world, HighModule, Slots.HighFirst, 3000);
        Assert.Equal(Slots.HighFirst + 1, FittingRules.FirstFreeSlot(world, statics, ShipId, module));
        Fit(world, HighModule, Slots.HighFirst + 1, 3001);
        Fit(world, HighModule, Slots.HighFirst + 2, 3002);
        Assert.Null(FittingRules.FirstFreeSlot(world, statics, ShipId, module));
        Assert.Null(FittingRules.FirstFreeSlot(world, statics, ShipId, Candidate(world, Plain, 2001))); // not a module
    }

    [Theory]
    [InlineData(11, SlotFamily.Low)]
    [InlineData(18, SlotFamily.Low)]
    [InlineData(19, SlotFamily.Medium)]
    [InlineData(26, SlotFamily.Medium)]
    [InlineData(27, SlotFamily.High)]
    [InlineData(34, SlotFamily.High)]
    [InlineData(92, SlotFamily.Rig)]
    [InlineData(99, SlotFamily.Rig)]
    [InlineData(125, SlotFamily.Subsystem)]
    [InlineData(132, SlotFamily.Subsystem)]
    public void The_flags_of_the_slots(int flag, SlotFamily family)
    {
        Assert.Equal(family, Slots.FamilyOfFlag(flag));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(35)]
    [InlineData(57)]
    [InlineData(87)]
    [InlineData(100)]
    [InlineData(133)]
    public void Other_flags_are_no_slot(int flag)
    {
        Assert.Null(Slots.FamilyOfFlag(flag));
    }

    [Fact]
    public void Pilot_exit_removes_skill_sources_and_offlines_only_illegal_or_overbudget_modules()
    {
        var (world, statics) = Loaded();
        world.Add(new DogmaItem(50, Skill, Char, Char, 7));
        world.SetValue(50, DogmaWorld.SkillLevel, 3);
        world.SetState(3001, ItemState.Online);
        world.SetState(3002, ItemState.Online);
        world.SetState(3003, ItemState.Online);
        world.SetState(3004, ItemState.Active);
        world.Add(new DogmaItem(3005, 7106, Char, ShipId, Slots.HighFirst + 5, ItemState.Online));

        var changes = FittingRules.RemovePilotModifiersAndRevalidate(
            world, statics, Char, ShipId, [50], [3000, 3001, 3002, 3003, 3004, 3005]);

        Assert.Null(world.Item(Char));
        Assert.Null(world.Item(50));
        Assert.Equal(ItemState.Online, world.Item(3000)!.State);
        Assert.Equal(ItemState.Online, world.Item(3001)!.State);
        Assert.Equal(ItemState.Offline, world.Item(3002)!.State);
        Assert.Equal(ItemState.Offline, world.Item(3003)!.State);
        Assert.Equal(ItemState.Online, world.Item(3004)!.State);
        Assert.Equal(ItemState.Offline, world.Item(3005)!.State);
        Assert.Equal([3002L, 3003L, 3004L, 3005L], changes.Select(change => change.ModuleId));
    }

    [Fact]
    public void Rebased_hitpoints_keep_absolute_layer_damage_and_identify_hull_collapse()
    {
        var prior = new ShipCondition(0.9, 0.25, 0.75, 0.4, false);

        var (collapsed, hullCollapsed) = prior.RebaseHitpoints(
            oldHull: 100, oldArmor: 200, oldShield: 300,
            newHull: 80, newArmor: 250, newShield: 400);

        Assert.True(hullCollapsed);
        Assert.Equal(1.125, collapsed.Damage, 10);
        Assert.Equal(0.32, collapsed.ArmorDamage, 10);
        Assert.Equal(0.8125, collapsed.ShieldCharge, 10);
        Assert.Equal(prior.Charge, collapsed.Charge);
        Assert.False(collapsed.Incapacitated);

        var (surviving, stillAlive) = new ShipCondition(0.5, 0.4, 0.9, 0.2, false)
            .RebaseHitpoints(oldHull: 100, oldArmor: 200, oldShield: 300, newHull: 150, newArmor: 250, newShield: 400);
        Assert.False(stillAlive);
        Assert.Equal(50, surviving.Damage * 150, 10);
        Assert.Equal(40, surviving.ArmorDamage * 250, 10);
        Assert.Equal(30, (1 - surviving.ShieldCharge) * 400, 10);
        Assert.Equal(0.4, surviving.Charge);
    }

    [Fact]
    public void Ship_condition_rebases_absolute_layer_damage_but_keeps_capacitor_fraction()
    {
        var before = new ShipCondition(0.6, 0.37, 0.4, 0.5, false);

        var (after, hullCollapsed) = before.RebaseHitpoints(
            oldHull: 1_000, oldArmor: 2_000, oldShield: 1_200,
            newHull: 800, newArmor: 1_500, newShield: 900);

        Assert.False(hullCollapsed);
        Assert.InRange(Math.Abs(after.Damage - 0.75), 0, 1e-9);
        Assert.InRange(Math.Abs(after.ArmorDamage - (2.0 / 3.0)), 0, 1e-9);
        Assert.InRange(Math.Abs(after.ShieldCharge - 0.2), 0, 1e-9);
        Assert.Equal(before.Charge, after.Charge);
    }

    [Fact]
    public void Ship_condition_marks_hull_at_zero_or_below_for_eject_destruction()
    {
        var nearlyDestroyed = new ShipCondition(0.8, 0.5, 0.5, 0.5, false);

        var (after, hullCollapsed) = nearlyDestroyed.RebaseHitpoints(
            oldHull: 1_250, oldArmor: 1_000, oldShield: 1_000,
            newHull: 1_000, newArmor: 1_000, newShield: 1_000);

        Assert.True(hullCollapsed);
        Assert.Equal(1.0, after.Damage);
    }

    [Fact]
    public void Pilot_exit_removes_skills_and_offlines_only_modules_that_no_longer_fit()
    {
        var (world, statics) = Loaded();
        world.Add(new DogmaItem(50, Skill, Char, Char, 7));
        world.SetValue(50, DogmaWorld.SkillLevel, 3);
        world.SetState(3001, ItemState.Online);
        world.SetState(3002, ItemState.Online);
        world.SetState(3003, ItemState.Online);
        world.SetState(3004, ItemState.Active);
        world.Add(new DogmaItem(3005, 7106, Char, ShipId, Slots.HighFirst + 5, ItemState.Online));

        var changes = FittingRules.RemovePilotModifiersAndRevalidate(
            world, statics, Char, ShipId, [50], [3000, 3001, 3002, 3003, 3004, 3005]);

        Assert.Null(world.Item(Char));
        Assert.Null(world.Item(50));
        Assert.Equal(ItemState.Online, world.Item(3000)!.State);
        Assert.Equal(ItemState.Online, world.Item(3001)!.State);
        Assert.Equal(ItemState.Offline, world.Item(3002)!.State);
        Assert.Equal(ItemState.Offline, world.Item(3003)!.State);
        Assert.Equal(ItemState.Online, world.Item(3004)!.State);
        Assert.Equal(ItemState.Offline, world.Item(3005)!.State);
        Assert.Equal([3002L, 3003L, 3004L, 3005L], changes.Select(change => change.ModuleId));
    }

    [Theory]
    [InlineData(963, true)]
    [InlineData(26, false)]
    public void Rigs_can_be_removed_without_destruction_only_from_strategic_cruisers(int shipGroupId, bool expected)
    {
        var shipType = new TypeDef(1, shipGroupId, 6, new Dictionary<int, double>(), []);

        Assert.Equal(expected, FittingRules.CanUnfitRigWithoutDestruction(shipType));
    }
}
