using Eve.Npc;

namespace Eve.Npc.Tests;

/// <summary>What an NPC leaves in its wreck: the rules of <c>npcLoot.js</c> of the reference, on a small set of types and numbers that are given.</summary>
public sealed class NpcLootTests
{
    private const int AntimatterS = 1, IridiumS = 2, AntimatterM = 3, NavyAntimatterS = 4, VoidSII = 5, RailgunS = 6, RailgunM = 7, Scraps = 8, RailgunBlueprint = 9, Wreck = 10, Hulk = 11;

    private static readonly LootType[] Types =
    [
        new(AntimatterS, "Antimatter Charge S", "Hybrid Charge", 8),
        new(IridiumS, "Iridium Charge S", "Hybrid Charge", 8),
        new(AntimatterM, "Antimatter Charge M", "Hybrid Charge", 8),
        new(NavyAntimatterS, "Caldari Navy Antimatter Charge S", "Hybrid Charge", 8),
        new(VoidSII, "Void S II", "Hybrid Charge", 8),
        new(RailgunS, "125mm Railgun I", "Hybrid Weapon", 7),
        new(RailgunM, "250mm Railgun I", "Hybrid Weapon", 7),
        new(Scraps, "Metal Scraps", "Salvaged Materials", 25),
        new(RailgunBlueprint, "125mm Railgun I Blueprint", "Hybrid Weapon Blueprint", 9),
        new(Wreck, "Small Wreck", "Wreck", 6),
        new(Hulk, "Hulk", "Mining Barge", 6),
        new(0, "Not A Type", "Hybrid Charge", 8),
        new(12, "  ", "Hybrid Charge", 8),
    ];

    private static LootSelector Rule(string kind, string size = "", string grade = "", int? weight = null, int? min = null, int? max = null) =>
        new(kind, size, grade, [], [], [], [], weight, min, max);

    private static LootTable Rules(int min, int max, double empty, bool duplicates, params LootSelector[] selectors) =>
        new("rules", "rule", min, max, empty, duplicates, false, 1, 25, selectors, true);

    /// <summary>The numbers a roll asks for, in the order it asks; it is a mistake to ask for more or for fewer.</summary>
    private sealed class Dice(params double[] values)
    {
        private readonly Queue<double> _values = new(values);

        public double Next() => _values.Dequeue();

        public int Left => _values.Count;
    }

    private static IReadOnlyList<(int Type, long Quantity, bool Singleton)> Roll(LootTable? table, Dice dice, LootType[]? types = null)
    {
        var drops = new NpcLoot(types ?? Types).Roll(table, dice.Next);
        Assert.Equal(0, dice.Left);
        return [.. drops.Select(d => (d.TypeId, d.Quantity, d.Singleton))];
    }

    [Fact]
    public void A_type_without_an_id_or_a_name_and_a_wreck_are_never_drawn()
    {
        Assert.Equal(Types.Length - 3, new NpcLoot(Types).PoolSize);
    }

    [Fact]
    public void A_table_that_is_not_there_leaves_nothing()
    {
        Assert.Empty(Roll(null, new Dice()));
        Assert.Empty(new NpcLoot([]).Roll(Rules(1, 1, 0, false, Rule("any")), () => 0));
    }

    [Fact]
    public void An_empty_chance_leaves_a_wreck_empty_when_the_first_number_is_below_it_and_does_not_ask_for_more()
    {
        var table = Rules(1, 1, 0.22, false, Rule("ammo", "small"));
        Assert.Empty(Roll(table, new Dice(0.21)));
        // 0.22 is not below 0.22: one draw is made (the count, the rule, the type, the quantity)
        Assert.Single(Roll(table, new Dice(0.22, 0, 0, 0, 0)));
    }

    [Fact]
    public void A_rule_for_small_ammunition_draws_only_the_small_plain_charges_never_a_faction_one_nor_tech_two()
    {
        var table = Rules(2, 2, 0, true, Rule("ammo", "small", min: 15, max: 60));
        var seen = new HashSet<int>();
        for (var i = 0; i < 40; i++)
        {
            foreach (var drop in Roll(table, new Dice(0, 0.0, i / 40.0, 0.5, 0.0, i / 40.0, 0.5)))
            {
                seen.Add(drop.Type);
            }
        }
        // a stack of 15 to 60 rounds; the draw of a type reaches both small charges
        Assert.Equal([AntimatterS, IridiumS], seen.Order());
    }

    [Fact]
    public void A_stack_is_as_many_as_the_rule_says_and_a_module_is_one_unit_of_its_own()
    {
        var charge = Roll(Rules(1, 1, 0, false, Rule("ammo", "small", min: 15, max: 60)), new Dice(0, 0, 0, 0.5)).Single();
        Assert.Equal((AntimatterS, 15 + (long)Math.Floor(0.5 * 46), false), charge);
        Assert.Equal((RailgunS, 1L, true), Roll(Rules(1, 1, 0, false, Rule("module", "small")), new Dice(0, 0, 0)).Single());
        // from the table when the rule has no quantity of its own (25 at the most)
        Assert.Equal((Scraps, 1 + (long)Math.Floor(0.999 * 25), false), Roll(Rules(1, 1, 0, false, Rule("trash")), new Dice(0, 0, 0, 0.999)).Single());
    }

    [Fact]
    public void Medium_and_large_bands_pick_their_own_names()
    {
        Assert.Equal(AntimatterM, Roll(Rules(1, 1, 0, false, Rule("ammo", "medium")), new Dice(0, 0, 0, 0)).Single().Type);
        Assert.Equal(RailgunM, Roll(Rules(1, 1, 0, false, Rule("module", "medium")), new Dice(0, 0, 0)).Single().Type);
    }

    [Fact]
    public void A_special_grade_is_drawn_only_by_a_rule_that_lets_it_in()
    {
        var navy = Types.Where(t => t.TypeId == NavyAntimatterS).ToArray();
        Assert.Empty(new NpcLoot(navy).Roll(Rules(1, 1, 0, false, Rule("ammo", "small")), () => 0));
        Assert.Equal(NavyAntimatterS, new NpcLoot(navy).Roll(Rules(1, 1, 0, false, Rule("ammo", "small", "faction")), () => 0).Single().TypeId);
    }

    [Fact]
    public void A_blueprint_and_tech_two_are_never_drawn_by_a_rule_even_one_that_lets_the_grades_in()
    {
        LootType[] only = [Types[RailgunBlueprint - 1], Types[VoidSII - 1]];
        Assert.Empty(new NpcLoot(only).Roll(Rules(3, 3, 0, true, Rule("any", grade: "special")), () => 0));
    }

    [Fact]
    public void A_type_is_not_given_twice_unless_the_table_allows_it_and_a_rule_with_nothing_left_is_not_asked_again()
    {
        // two small charges, three draws: the third finds nothing left in the first rule, drops it and is made again on the second, whose only unit is a railgun
        var table = Rules(3, 3, 0, false, Rule("ammo", "small", weight: 100), Rule("module", "small", weight: 1));
        var rolled = Roll(table, new Dice(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.Equal([AntimatterS, IridiumS, RailgunS], rolled.Select(d => d.Type));
        // with duplicates allowed the first rule is asked again
        var again = Roll(Rules(3, 3, 0, true, Rule("ammo", "small", weight: 100), Rule("module", "small", weight: 1)), new Dice(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.Equal([AntimatterS, AntimatterS, AntimatterS], again.Select(d => d.Type));
    }

    [Fact]
    public void Draws_follow_the_weights()
    {
        var table = Rules(1, 1, 0, false, Rule("ammo", "small", weight: 6), Rule("module", "small", weight: 2));
        // 0 to 6 of 8 is the ammunition, the rest the module
        Assert.Equal(AntimatterS, Roll(table, new Dice(0, 0.74, 0, 0)).Single().Type);
        Assert.Equal(RailgunS, Roll(table, new Dice(0, 0.76, 0)).Single().Type);
        // exactly 6 of 8 is not below the weight of the first: it is the second's
        Assert.Equal(RailgunS, Roll(table, new Dice(0, 0.75, 0)).Single().Type);
    }

    [Fact]
    public void The_counts_are_what_the_numbers_say_between_the_least_and_the_most()
    {
        var table = Rules(1, 3, 0, true, Rule("module", "small"));
        Assert.Single(Roll(table, new Dice(0.0, 0, 0)));
        Assert.Equal(2, Roll(table, new Dice(0.4, 0, 0, 0, 0)).Count);
        Assert.Equal(3, Roll(table, new Dice(0.99, 0, 0, 0, 0, 0, 0)).Count);
    }

    [Fact]
    public void A_plain_draw_takes_any_type_and_makes_a_stack_of_what_stacks()
    {
        var table = new LootTable("generic_random_any", "", 1, 3, 0, false, false, 1, 25, [], false);
        var rolled = Roll(table, new Dice(0.0, 0, 0.0));
        Assert.Equal([(AntimatterS, 1L, false)], rolled);
        var several = Roll(table, new Dice(0.99, 0.99, 0.5, 0.0, 0.9));
        Assert.Equal(3, several.Count);
        Assert.All(several, d => Assert.NotEqual(Wreck, d.Type));
        Assert.Equal((Hulk, 1L, true), several[0]);
    }

    [Fact]
    public void A_table_that_is_switched_off_or_has_no_entries_leaves_nothing_and_asks_for_nothing()
    {
        Assert.Empty(Roll(new LootTable("off", "", 0, 0, 0, false, true, null, null, [], false), new Dice()));
        Assert.Empty(Roll(new LootTable("zero", "", 0, 0, 0, false, false, null, null, [], false), new Dice()));
        // no bounds at all is no draw either
        Assert.Empty(Roll(new LootTable("unbounded", "", null, null, 0, false, false, null, null, [], false), new Dice()));
    }

    [Fact]
    public void A_table_with_rules_that_has_none_leaves_nothing_and_asks_for_nothing()
    {
        Assert.Empty(Roll(new LootTable("rule_without_rules", "rule", 0, 0, 0, false, true, null, null, [], true), new Dice()));
        // an empty chance is rolled first, and what it says is the end of it
        Assert.Empty(Roll(Rules(1, 3, 0.5, false), new Dice(0.9)));
    }

    [Fact]
    public void The_authored_tables_load_and_every_profile_names_one_of_them()
    {
        var authority = NpcAuthority.Load();
        Assert.Equal(61, authority.LootTableCount);
        Assert.All(authority.Profiles, p => Assert.NotNull(authority.LootTable(p.LootTableId)));

        var small = authority.LootTable("loot_belt_normal_small")!;
        Assert.Equal(("rule", 1, 3, 0.22, false), (small.Mode, small.MinEntries, small.MaxEntries, small.EmptyChance, small.AllowDuplicates));
        Assert.Equal(["ammo", "module", "utility", "trash"], small.Selectors.Select(s => s.Kind));
        Assert.Equal([6, 2, 2, 1], small.Selectors.Select(s => s.Weight ?? 0));
        Assert.Equal((15, 60), (small.Selectors[0].QuantityMin ?? 0, small.Selectors[0].QuantityMax ?? 0));

        var generic = authority.LootTable("generic_random_any")!;
        Assert.Equal((1, 3, 1, 25, false), (generic.MinEntries, generic.MaxEntries, generic.StackableMin, generic.StackableMax, generic.HasSelectors));
        var empty = authority.LootTable("parity_rogue_drone_standard")!;
        Assert.True(empty.DisableRandomLoot);
        Assert.Null(authority.LootTable("nothing_like_it"));
    }
}
