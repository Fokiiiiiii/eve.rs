using System.Text;
using Eve.Dungeons;

namespace Eve.Dungeons.Tests;

/// <summary>The wave planner: what the owner data and the rule file say about the order and the triggers of a dungeon's waves, and what it refuses.</summary>
public sealed class WavePlannerTests
{
    private static readonly DungeonCatalog Catalog = DungeonCatalog.LoadEmbedded();
    private static readonly WaveRules Rules = WaveRules.LoadEmbedded();

    private static Dungeon Official(long id) => ((DungeonFound)Catalog.GetDungeon(id)).Dungeon;

    private static Dungeon WithWaves(long id, params DungeonWave[] waves) =>
        new(id, null, null, null, null, null, null, null, new DungeonResources([], [], []), [], [], waves, [], [], [], [], []);

    private static DungeonWave Wave(int index, string? trigger, string query = "npc_deadspace_hostiles", int amount = 3, int? countdown = null) =>
        new(null, index, trigger, null, query, amount, true, countdown, null, [], []);

    private static WaveRules RulesOf(string json) => WaveRules.Load(_ => new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void The_Breeding_Facility_starts_its_first_wave_after_the_wiki_timer_and_each_later_wave_when_the_one_before_is_cleared()
    {
        var answer = WavePlanner.Plan(Official(2158), Rules);

        Assert.True(answer.Planned, answer.Refusal);
        var waves = answer.Plan!.Waves;
        Assert.Equal([7, 8, 9, 10], waves.Select(wave => wave.Amount).ToArray());
        Assert.Equal(WaveTrigger.Timer, waves[0].Trigger);
        Assert.Equal(240, waves[0].DelaySeconds);
        Assert.StartsWith("rule: https://wiki.eveuniversity.org/Breeding_Facility", waves[0].TriggerSource);
        Assert.Equal([WaveTrigger.WaveCleared, WaveTrigger.WaveCleared, WaveTrigger.WaveCleared], waves.Skip(1).Select(wave => wave.Trigger).ToArray());
        Assert.All(waves, wave => Assert.Equal("npc_deadspace_hostiles", wave.SpawnQuery));
    }

    [Fact]
    public void A_dungeon_whose_owner_data_has_one_wave_on_load_plans_that_wave_alone_with_its_count()
    {
        var answer = WavePlanner.Plan(Official(2173), Rules);

        Assert.True(answer.Planned, answer.Refusal);
        var wave = Assert.Single(answer.Plan!.Waves);
        Assert.Equal((WaveTrigger.OnLoad, 8, "owner data"), (wave.Trigger, wave.Amount, wave.TriggerSource));
    }

    [Fact]
    public void A_wave_that_needs_a_room_trigger_the_runtime_does_not_have_refuses_the_dungeon_with_the_trigger_named()
    {
        var answer = WavePlanner.Plan(Official(283), Rules);

        Assert.False(answer.Planned);
        Assert.Contains("'on_room_active'", answer.Refusal);
        Assert.Contains("does not implement", answer.Refusal);
    }

    [Fact]
    public void A_wave_whose_owner_timer_has_no_delay_is_refused_because_no_rule_gives_one()
    {
        var answer = WavePlanner.Plan(WithWaves(1, Wave(1, "on_load"), Wave(2, "timer")), Rules);

        Assert.False(answer.Planned);
        Assert.Contains("wave 2 is a timer and the owner data gives no delay", answer.Refusal);
    }

    [Fact]
    public void A_timer_wave_takes_the_owner_countdown_when_the_owner_data_gives_one()
    {
        var answer = WavePlanner.Plan(WithWaves(1, Wave(1, "on_load"), Wave(2, "timer", countdown: 90)), Rules);

        Assert.True(answer.Planned, answer.Refusal);
        Assert.Equal((WaveTrigger.Timer, 90), (answer.Plan!.Waves[1].Trigger, answer.Plan.Waves[1].DelaySeconds));
    }

    [Fact]
    public void Without_a_trigger_the_first_wave_starts_on_load_and_the_later_ones_when_the_one_before_is_cleared()
    {
        var answer = WavePlanner.Plan(WithWaves(1, Wave(1, null), Wave(2, null), Wave(3, null)), Rules);

        Assert.True(answer.Planned, answer.Refusal);
        Assert.Equal([WaveTrigger.OnLoad, WaveTrigger.WaveCleared, WaveTrigger.WaveCleared], answer.Plan!.Waves.Select(wave => wave.Trigger).ToArray());
        Assert.All(answer.Plan.Waves, wave => Assert.Equal("default policy (no trigger in the owner data)", wave.TriggerSource));
    }

    [Fact]
    public void A_first_wave_that_waits_for_a_wave_before_it_is_refused()
    {
        var answer = WavePlanner.Plan(WithWaves(1, Wave(1, "wave_cleared")), Rules);

        Assert.False(answer.Planned);
        Assert.Contains("the first wave waits for a wave before it", answer.Refusal);
    }

    [Fact]
    public void Wave_numbers_out_of_order_or_with_a_gap_are_refused_rather_than_renumbered()
    {
        var answer = WavePlanner.Plan(WithWaves(1, Wave(1, "on_load"), Wave(3, "wave_cleared")), Rules);

        Assert.False(answer.Planned);
        Assert.Contains("numbers its waves 3 where 2 is due", answer.Refusal);
    }

    [Fact]
    public void A_dungeon_with_no_waves_or_a_wave_without_a_spawn_query_is_refused()
    {
        Assert.Contains("no NPC waves", WavePlanner.Plan(WithWaves(1), Rules).Refusal);
        Assert.Contains("no spawn query or no count", WavePlanner.Plan(WithWaves(1, Wave(1, "on_load", query: "", amount: 3)), Rules).Refusal);
    }

    [Fact]
    public void A_rule_that_names_a_wave_the_dungeon_does_not_have_refuses_the_dungeon()
    {
        var rules = RulesOf("""{"schema":"eve.dungeon-waves.rules.v1","policy":"p","dungeons":[{"dungeonID":7,"name":"n","source":"s","note":"","waves":[{"waveIndex":4,"trigger":"timer","delaySeconds":60}]}]}""");

        var answer = WavePlanner.Plan(WithWaves(7, Wave(1, "on_load"), Wave(2, "wave_cleared")), rules);

        Assert.False(answer.Planned);
        Assert.Contains("names wave 4, and the owner data has 2 waves", answer.Refusal);
    }

    [Fact]
    public void A_rule_replaces_only_the_wave_it_names_and_keeps_the_source_it_gives()
    {
        var rules = RulesOf("""{"schema":"eve.dungeon-waves.rules.v1","policy":"p","dungeons":[{"dungeonID":7,"name":"n","source":"https://example.test/x","note":"","waves":[{"waveIndex":2,"trigger":"timer","delaySeconds":60}]}]}""");

        var answer = WavePlanner.Plan(WithWaves(7, Wave(1, "on_load"), Wave(2, "wave_cleared"), Wave(3, "wave_cleared")), rules);

        Assert.True(answer.Planned, answer.Refusal);
        Assert.Equal([WaveTrigger.OnLoad, WaveTrigger.Timer, WaveTrigger.WaveCleared], answer.Plan!.Waves.Select(wave => wave.Trigger).ToArray());
        Assert.Equal("rule: https://example.test/x", answer.Plan.Waves[1].TriggerSource);
        Assert.Equal("owner data", answer.Plan.Waves[2].TriggerSource);
    }

    [Theory]
    [InlineData("""{"schema":"eve.dungeon-waves.rules.v0","policy":"p","dungeons":[]}""", "expected 'eve.dungeon-waves.rules.v1'")]
    [InlineData("""{"schema":"eve.dungeon-waves.rules.v1","policy":"p","dungeons":[{"dungeonID":7,"name":"n","source":"s","note":"","waves":[{"waveIndex":1,"trigger":"timer"}]}]}""", "timer without a positive delaySeconds")]
    [InlineData("""{"schema":"eve.dungeon-waves.rules.v1","policy":"p","dungeons":[{"dungeonID":7,"name":"n","source":"s","note":"","waves":[{"waveIndex":1,"trigger":"gate_use"}]}]}""", "is not one of on_load")]
    [InlineData("""{"schema":"eve.dungeon-waves.rules.v1","policy":"p","dungeons":[{"dungeonID":7,"name":"n","source":"","note":"","waves":[]}]}""", "names no source")]
    [InlineData("""{"schema":"eve.dungeon-waves.rules.v1","policy":"p","dungeons":[{"dungeonID":7,"name":"n","source":"s","note":"","waves":[]},{"dungeonID":7,"name":"n","source":"s","note":"","waves":[]}]}""", "appears twice")]
    public void The_rule_file_refuses_what_it_cannot_state_exactly(string json, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => RulesOf(json));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void The_embedded_rule_file_names_the_Breeding_Facility_with_its_source_and_nothing_else()
    {
        var rule = Assert.Single(Rules.File.Dungeons);

        Assert.Equal((2158L, "https://wiki.eveuniversity.org/Breeding_Facility"), (rule.DungeonID, rule.Source));
        Assert.Equal([(1, "timer", 240)], rule.Waves.Select(wave => (wave.WaveIndex, wave.Trigger, wave.DelaySeconds)).ToArray());
    }
}
