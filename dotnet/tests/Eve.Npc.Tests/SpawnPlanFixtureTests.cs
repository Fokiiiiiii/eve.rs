using System.Text.Json;
using Eve.Npc;

namespace Eve.Npc.Tests;

/// <summary>
/// What a query after <c>/npc</c> names, and what a pool rolls for the numbers it is given, is what the reference's <c>resolveNpcSpawnPlan</c> made of it
/// (<c>oracle/fixtures/npc/spawn_plans.json</c>, made by <c>tools/dev/gen-npc-selection-fixture.js</c> from the authored tables).
/// </summary>
public sealed class SpawnPlanFixtureTests
{
    [Fact]
    public void Every_query_makes_what_the_reference_made_of_it()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "npc", "spawn_plans.json")));
        var root = fixture.RootElement;
        var notDamageable = root.GetProperty("notDamageable").EnumerateArray().Select(type => type.GetInt32()).ToHashSet();
        var selector = new NpcSelector(NpcAuthority.Load());
        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count > 300, $"{cases.Count} cases");

        var wrong = new List<string>();
        var successes = 0;
        var pools = 0;
        foreach (var c in cases)
        {
            var query = c.GetProperty("query").GetString()!;
            var amount = c.GetProperty("amount").GetInt32();
            var randoms = new Queue<double>(c.GetProperty("randoms").EnumerateArray().Select(r => r.GetDouble()));
            var expectedError = c.GetProperty("error").GetString();
            var expectedSuggestions = c.GetProperty("suggestions").EnumerateArray().Select(s => s.GetString()!).ToList();

            string? error;
            IReadOnlyList<string> suggestions;
            IReadOnlyList<NpcProfile> profiles = [];
            NpcSelection? selection;
            (selection, error, suggestions) = selector.Select(query);
            if (selection is not null)
            {
                var plan = selector.Roll(selection, amount, type => !notDamageable.Contains(type), () => randoms.Dequeue());
                error = plan.Error;
                suggestions = plan.Suggestions;
                profiles = plan.Profiles;
            }

            var same = error == expectedError && suggestions.SequenceEqual(expectedSuggestions);
            if (expectedError is null)
            {
                successes++;
                pools += c.GetProperty("kind").GetString() == "pool" ? 1 : 0;
                same = same && selection!.Kind == c.GetProperty("kind").GetString()
                    && selection.Id == c.GetProperty("id").GetString()
                    && selection.Name == c.GetProperty("name").GetString()
                    && profiles.Select(p => p.Id).SequenceEqual(c.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()!));
            }
            if (!same)
            {
                wrong.Add($"'{query}' x{amount}: mine {selection?.Kind}/{selection?.Id} {error} [{string.Join("; ", suggestions)}] {string.Join(",", profiles.Select(p => p.Id))}, the reference's {c}");
            }
        }
        Assert.True(successes > 200 && pools > 20, $"{successes} plans, {pools} of pools");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {cases.Count} differ:\n{string.Join("\n", wrong.Take(15))}");
    }
}
