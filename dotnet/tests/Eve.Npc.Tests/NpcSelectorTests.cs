using Eve.Npc;

namespace Eve.Npc.Tests;

public sealed class NpcSelectorTests
{
    private static NpcProfile Profile(string id, string name, int ship, string entityType = "npc", params string[] aliases) =>
        new(id, name, aliases, entityType, ship, 1000134, 0, 500012, NpcAuthority.RetailEmptyLoadoutId, "", name, -5, 1000, 8000);

    private static NpcAuthority Authority(params NpcPool[] extraPools)
    {
        NpcProfile[] profiles =
        [
            Profile("generic_hostile", "Blood Visionary", 100, "npc", "default", "blood"),
            Profile("alpha_frigate", "Alpha Frigate", 101, "npc", "alpha", "little alpha"),
            Profile("beta_cruiser", "Beta Cruiser", 102),
            Profile("beta_frigate", "Beta Frigate", 103),
            Profile("police", "Concord Police", 104, "concord", "cops"),
            Profile("hopeless", "Hopeless Hulk", 105),
            Profile("parity_blood_raider_pulse_frigate", "Blood Pulse Frigate", 106),
            Profile("parity_blood_raider_beam_commander_frigate", "Blood Beam Commander", 107),
            Profile("parity_blood_raider_officer", "Blood Officer", 108),
        ];
        NpcPool[] pools =
        [
            new("npc_hostiles", "Standard Pirate Hostiles", ["pirates", "hostiles"], "npc", [new("alpha_frigate", 6), new("beta_cruiser", 3), new("beta_frigate", 1)]),
            new("blood", "Blood Raiders", [], "npc", [new("parity_blood_raider_pulse_frigate", 2), new("parity_blood_raider_beam_commander_frigate", 2), new("parity_blood_raider_officer", 1)]),
            new("police_pool", "Police", ["cops pool"], "concord", [new("police", 1)]),
            new("mixed", "Mixed Bag", ["bag"], "npc", [new("police", 1), new("hopeless", 1), new("ghost_profile", 4), new("beta_cruiser", 1)]),
            .. extraPools,
        ];
        return new NpcAuthority(profiles, [], pools);
    }

    private static readonly Func<int, bool> Everything = _ => true;

    private static Func<double> Sequence(params double[] values)
    {
        var queue = new Queue<double>(values);
        return () => queue.Dequeue();
    }

    private static NpcSelection Selected(NpcSelector selector, string? query, NpcSelectionOptions? options = null)
    {
        var (selection, error, _) = selector.Select(query, options);
        Assert.True(selection is not null, $"'{query}': {error}");
        return selection;
    }

    [Theory]
    [InlineData("alpha_frigate")]
    [InlineData("Alpha Frigate")]
    [InlineData("ALPHA-FRIGATE")]
    [InlineData("  little   alpha ")]
    public void A_whole_id_name_or_alias_names_its_profile(string query)
    {
        var selection = Selected(new NpcSelector(Authority()), query);

        Assert.Equal(NpcSelection.ProfileKind, selection.Kind);
        Assert.Equal("alpha_frigate", selection.Id);
        Assert.Equal("Alpha Frigate", selection.Name);
    }

    [Fact]
    public void A_part_that_only_one_profile_has_names_it()
    {
        var selection = Selected(new NpcSelector(Authority()), "cruis");

        Assert.Equal("beta_cruiser", selection.Id);
    }

    [Fact]
    public void A_part_that_several_have_is_ambiguous_and_the_suggestions_name_them()
    {
        var (selection, error, suggestions) = new NpcSelector(Authority()).Select("beta");

        Assert.Null(selection);
        Assert.Equal("PROFILE_AMBIGUOUS", error);
        Assert.Equal(["Beta Cruiser (beta_cruiser)", "Beta Frigate (beta_frigate)"], suggestions);
    }

    [Fact]
    public void What_nothing_has_is_not_found()
    {
        var (selection, error, suggestions) = new NpcSelector(Authority()).Select("zzz");

        Assert.Null(selection);
        Assert.Equal("PROFILE_NOT_FOUND", error);
        Assert.Empty(suggestions);
    }

    [Fact]
    public void A_name_of_a_pool_names_the_pool()
    {
        var selection = Selected(new NpcSelector(Authority()), "Pirates");

        Assert.Equal(NpcSelection.PoolKind, selection.Kind);
        Assert.Equal("npc_hostiles", selection.Id);
        Assert.Equal("Standard Pirate Hostiles", selection.Name);
    }

    [Fact]
    public void Nothing_named_is_the_default_pool()
    {
        var selection = Selected(new NpcSelector(Authority()), "  ");

        Assert.Equal("npc_hostiles", selection.Id);
    }

    [Fact]
    public void With_no_default_pool_nothing_named_is_the_fallback_profile()
    {
        var selection = Selected(new NpcSelector(Authority()), null, new NpcSelectionOptions(DefaultPoolId: "no_such_pool"));

        Assert.Equal(NpcSelection.ProfileKind, selection.Kind);
        Assert.Equal("generic_hostile", selection.Id);
    }

    [Fact]
    public void With_neither_nothing_named_finds_nothing()
    {
        var (selection, error, _) = new NpcSelector(Authority()).Select("", new NpcSelectionOptions(DefaultPoolId: "", FallbackProfileId: ""));

        Assert.Null(selection);
        Assert.Equal("PROFILE_NOT_FOUND", error);
    }

    [Fact]
    public void A_fallback_profile_that_is_not_authored_is_an_incomplete_definition()
    {
        var (selection, error, _) = new NpcSelector(Authority()).Select("", new NpcSelectionOptions(DefaultPoolId: "no_such_pool", FallbackProfileId: "ghost"));

        Assert.Null(selection);
        Assert.Equal("NPC_DEFINITION_INCOMPLETE", error);
    }

    [Fact]
    public void When_a_profile_and_a_pool_match_the_one_with_the_whole_name_wins()
    {
        // "blood": an alias of the generic profile, the id of a pool
        var authority = Authority();
        var selector = new NpcSelector(authority);

        Assert.Equal(NpcSelection.ProfileKind, Selected(selector, "default").Kind);
        Assert.Equal(NpcSelection.PoolKind, Selected(selector, "blood raiders").Kind);
        // both whole ("blood" is the alias of one and the id of the other): pools are preferred, unless the options say not
        Assert.Equal(NpcSelection.PoolKind, Selected(selector, "blood").Kind);
        Assert.Equal(NpcSelection.ProfileKind, Selected(selector, "blood", new NpcSelectionOptions(PreferPools: false)).Kind);
    }

    [Fact]
    public void A_pool_that_is_not_a_whole_match_loses_to_a_profile_that_is()
    {
        var authority = new NpcAuthority(
            [Profile("wolf", "Wolf", 1)], [], [new NpcPool("wolf_pack", "Wolf Pack", [], "npc", [new("wolf", 1)])]);
        var selector = new NpcSelector(authority);

        Assert.Equal(NpcSelection.ProfileKind, Selected(selector, "wolf").Kind);
        Assert.Equal(NpcSelection.PoolKind, Selected(selector, "pack").Kind);
    }

    [Fact]
    public void The_first_row_of_a_token_is_the_one_a_query_finds_and_the_last_row_of_an_id_is_the_one_found_by_it()
    {
        NpcProfile[] profiles = [Profile("one", "Twin", 1), Profile("two", "Twin", 2), Profile("one", "Again", 3)];
        var authority = new NpcAuthority(profiles, [], []);

        Assert.Equal(1, authority.ResolveProfile("twin").Match!.Row.ShipTypeId);
        Assert.Equal(3, authority.Profile("one")!.ShipTypeId);
    }

    [Fact]
    public void Suggestions_are_each_named_once_and_at_most_eight()
    {
        var profiles = Enumerable.Range(0, 12).Select(i => Profile($"rat_{i}", $"Rat {i}", i)).ToList();
        var pools = new[] { new NpcPool("hb", "Hostile Bag", [], "npc", []) };
        var (_, error, suggestions) = new NpcSelector(new NpcAuthority(profiles, [], pools)).Select("rat");

        Assert.Equal("PROFILE_AMBIGUOUS", error);
        Assert.Equal(8, suggestions.Count);
        Assert.Equal("Rat 0 (rat_0)", suggestions[0]);
        Assert.Equal(["a", "b"], NpcSelector.DedupeSuggestions([" a", "a ", "", "b", "a"]));
    }

    [Fact]
    public void A_profile_is_made_as_many_times_as_asked()
    {
        var selector = new NpcSelector(Authority());

        var plan = selector.Roll(Selected(selector, "alpha"), 3, Everything, Sequence());

        Assert.True(plan.Succeeded);
        Assert.Equal(["alpha_frigate", "alpha_frigate", "alpha_frigate"], plan.Profiles.Select(p => p.Id));
    }

    [Fact]
    public void An_amount_below_one_is_one()
    {
        var selector = new NpcSelector(Authority());

        Assert.Single(selector.Roll(Selected(selector, "alpha"), 0, Everything, Sequence()).Profiles);
    }

    [Theory]
    [InlineData(0.0, "alpha_frigate")]
    [InlineData(0.59, "alpha_frigate")]
    [InlineData(0.6, "beta_cruiser")]
    [InlineData(0.89, "beta_cruiser")]
    [InlineData(0.9, "beta_frigate")]
    [InlineData(0.9999, "beta_frigate")]
    // a number that is not below one leaves no share to fall in: the last entry
    [InlineData(1.0, "beta_frigate")]
    public void A_pool_is_rolled_by_weight(double roll, string expected)
    {
        var selector = new NpcSelector(Authority());

        var plan = selector.Roll(Selected(selector, "pirates"), 1, Everything, Sequence(roll));

        Assert.Equal([expected], plan.Profiles.Select(p => p.Id));
    }

    [Fact]
    public void Every_amount_is_a_roll()
    {
        var selector = new NpcSelector(Authority());

        var plan = selector.Roll(Selected(selector, "pirates"), 3, Everything, Sequence(0.0, 0.7, 0.95));

        Assert.Equal(["alpha_frigate", "beta_cruiser", "beta_frigate"], plan.Profiles.Select(p => p.Id));
    }

    [Fact]
    public void A_weight_below_one_counts_as_one()
    {
        NpcProfile[] profiles = [Profile("a", "A", 1), Profile("b", "B", 2)];
        var authority = new NpcAuthority(profiles, [], [new NpcPool("ab", "AB", [], "npc", [new("a", 0), new("b", -3)])]);
        var selector = new NpcSelector(authority);

        Assert.Equal("a", selector.Roll(Selected(selector, "ab"), 1, Everything, Sequence(0.49)).Profiles[0].Id);
        Assert.Equal("b", selector.Roll(Selected(selector, "ab"), 1, Everything, Sequence(0.5)).Profiles[0].Id);
    }

    [Fact]
    public void Only_the_kind_of_npc_asked_for_is_made()
    {
        var selector = new NpcSelector(Authority());

        var police = selector.Roll(Selected(selector, "cops"), 1, Everything, Sequence());
        var policePool = selector.Roll(Selected(selector, "cops pool"), 1, Everything, Sequence());
        var any = selector.Roll(Selected(selector, "cops"), 1, Everything, Sequence(), entityType: "");

        Assert.Equal("PROFILE_NOT_FOUND", police.Error);
        Assert.Equal("POOL_EMPTY", policePool.Error);
        Assert.True(any.Succeeded);
    }

    [Fact]
    public void A_hull_that_cannot_be_hurt_is_not_made()
    {
        var selector = new NpcSelector(Authority());

        var plan = selector.Roll(Selected(selector, "hopeless"), 1, type => type != 105, Sequence());

        Assert.Equal("PROFILE_HULL_NOT_DAMAGEABLE", plan.Error);
    }

    [Fact]
    public void A_pool_leaves_out_what_cannot_be_rolled_and_entries_of_profiles_that_are_not_authored()
    {
        var selector = new NpcSelector(Authority());

        // mixed: police (concord), hopeless (not damageable), ghost_profile (not authored), beta_cruiser
        var plan = selector.Roll(Selected(selector, "bag"), 4, type => type != 105, Sequence(0.0, 0.3, 0.6, 0.99));

        Assert.True(plan.Succeeded);
        Assert.All(plan.Profiles, profile => Assert.Equal("beta_cruiser", profile.Id));
    }

    [Fact]
    public void A_pool_with_nothing_to_roll_is_empty()
    {
        var selector = new NpcSelector(Authority());

        var plan = selector.Roll(Selected(selector, "pirates"), 2, _ => false, Sequence());

        Assert.Equal("POOL_EMPTY", plan.Error);
        Assert.Empty(plan.Profiles);
    }

    [Fact]
    public void The_ship_types_to_know_are_those_of_the_candidates_once_each()
    {
        var selector = new NpcSelector(Authority());

        Assert.Equal([101, 102, 103], Selected(selector, "pirates").ShipTypes.Order());
        Assert.Equal([101], Selected(selector, "alpha").ShipTypes);
        // the profile that is not authored has no ship type
        Assert.DoesNotContain(0, Selected(selector, "bag").ShipTypes);
    }

    [Theory]
    [InlineData("  Blood-Raider! ", "blood raider ")]
    [InlineData("!!!", " ")]
    [InlineData("  a  b  ", "a b")]
    [InlineData("ÄÖ Ship", " ship")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_query_is_trimmed_then_every_run_of_what_is_not_a_letter_or_digit_is_one_space_and_not_trimmed_again(string? query, string expected) =>
        Assert.Equal(expected, NpcAuthority.NormalizeQuery(query));

    [Fact]
    public void The_empty_loadout_of_a_retail_entity_is_there_when_the_tables_do_not_have_it()
    {
        var authority = new NpcAuthority([], [], []);

        var loadout = authority.Loadout(NpcAuthority.RetailEmptyLoadoutId);

        Assert.NotNull(loadout);
        Assert.Empty(loadout.Modules);
        Assert.Empty(loadout.Charges);
        Assert.Null(authority.Loadout("no_such_loadout"));
    }

    [Fact]
    public void A_standard_pool_is_its_base_pool_without_commanders_and_officers()
    {
        var authority = Authority();

        var standard = authority.Pool("blood_standard");

        Assert.NotNull(standard);
        Assert.True(standard.Derived);
        Assert.Equal("Blood Raider Standard Hostiles", standard.Name);
        Assert.Equal(["parity_blood_raider_pulse_frigate"], standard.Entries.Select(e => e.ProfileId));
        Assert.Equal("blood_standard", Selected(new NpcSelector(authority), "blood anomaly").Id);
        // the pools that have no base here are not made
        Assert.Null(authority.Pool("sanshas_standard"));
    }

    [Fact]
    public void The_authored_data_is_in_the_assembly_and_whole()
    {
        var authority = NpcAuthority.Load();

        Assert.Equal(5620, authority.Profiles.Count);
        Assert.NotNull(authority.Profile("generic_hostile"));
        Assert.Equal(23245, authority.Profile("generic_hostile")!.ShipTypeId);
        // every profile has the loadout it names, and the pool the command rolls by default is there
        Assert.All(authority.Profiles, profile => Assert.NotNull(authority.Loadout(profile.LoadoutId)));
        Assert.NotNull(authority.Pool("npc_hostiles"));
        Assert.Equal(372, authority.Pool("npc_hostiles")!.Entries.Count);
        Assert.True(authority.Pool("npc_hostiles")!.Entries.All(e => authority.Loadout(authority.Profile(e.ProfileId)!.LoadoutId)!.Modules.Count == 0));
        Assert.Contains(authority.Pools, pool => pool.Derived);
    }
}
