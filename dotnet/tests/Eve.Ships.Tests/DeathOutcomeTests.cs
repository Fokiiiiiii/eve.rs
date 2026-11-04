using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>What a destroyed ship's contents come to: the coin and the draw are the reference's (<c>oracle/fixtures/death/drop_counts.json</c>), the tree walk is checked here.</summary>
public class DeathOutcomeTests
{
    private static long Wide(JsonNode node, string field) => long.Parse((string)node[field]!, CultureInfo.InvariantCulture);

    [Fact]
    public void Every_drop_count_of_the_reference_is_the_same_here()
    {
        var fixture = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "death", "drop_counts.json")))!;
        var cases = (JsonArray)fixture["cases"]!;
        Assert.True(cases.Count > 5000);
        var wrong = new List<string>();
        foreach (var c in cases)
        {
            var options = new DeathOptions((bool?)c!["forceAllDestroyed"] ?? false, (bool?)c["forceAllDropped"] ?? false);
            var mine = DeathOutcome.StackDropCounts(Wide(c, "itemID"), (int)c["flag"]!, Wide(c, "quantity"), (string)c["seed"]!, options);
            var theirs = (Wide(c, "qtyDropped"), Wide(c, "qtyDestroyed"));
            if (mine != theirs)
            {
                wrong.Add($"{c["seed"]} item {c["itemID"]} x{c["quantity"]} flag {c["flag"]} {options}: mine {mine}, the reference's {theirs}");
            }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {cases.Count} differ:\n{string.Join('\n', wrong.Take(20))}");
    }

    private static DeathNode Node(long id, int flag, DeathNode[]? children = null, bool singleton = true, long quantity = 1) =>
        new(id, 1000 + (int)id, flag, singleton, quantity, children ?? []);

    private const string Seed = "ship:9988400002411:1790774257401";

    [Fact]
    public void An_item_that_drops_is_moved_and_one_that_does_not_is_destroyed_and_a_rig_never_drops()
    {
        DeathNode[] contents = [Node(10, 27), Node(11, 19), Node(12, 93)];
        Assert.Equal(
            [new DeathMove(10, 1), new DeathMove(11, 1), new DeathDestroy(12)],
            DeathOutcome.Resolve(contents, Seed, new DeathOptions(ForceAllDropped: true)));
        Assert.Equal(
            [new DeathDestroy(10), new DeathDestroy(11), new DeathDestroy(12)],
            DeathOutcome.Resolve(contents, Seed, new DeathOptions(ForceAllDestroyed: true)));
        // by the coin: what the seed says of each, in the order of the items
        var coins = contents.Select(node => node.Flag is >= 92 and <= 99 ? false : DeathOutcome.HashToDrop(Seed, node.ItemId, 0)).ToArray();
        Assert.Equal(
            contents.Select((node, i) => coins[i] ? (DeathAction)new DeathMove(node.ItemId, 1) : new DeathDestroy(node.ItemId)),
            DeathOutcome.Resolve(contents, Seed));
    }

    [Fact]
    public void What_is_inside_a_dropped_container_goes_with_it_and_what_is_inside_a_destroyed_one_is_moved_out_or_destroyed_one_by_one()
    {
        var content = Node(21, 5);
        var container = Node(20, 5, [content]);
        Assert.Equal([new DeathMove(20, 1)], DeathOutcome.Resolve([container], Seed, new DeathOptions(ForceAllDropped: true)));
        Assert.Equal([new DeathDestroy(21), new DeathDestroy(20)], DeathOutcome.Resolve([container], Seed, new DeathOptions(ForceAllDestroyed: true)));

        // a seed under which the container is destroyed and what it held drops: the content is taken out first, then the container goes
        var seed = Enumerable.Range(0, 1000).Select(n => $"probe:{n}").First(s => !DeathOutcome.HashToDrop(s, 20, 0) && DeathOutcome.HashToDrop(s, 21, 0));
        Assert.Equal([new DeathMove(21, 1), new DeathDestroy(20)], DeathOutcome.Resolve([container], seed));
        // and one under which the container drops and its content would have too: only the container is moved
        var both = Enumerable.Range(0, 1000).Select(n => $"probe:{n}").First(s => DeathOutcome.HashToDrop(s, 20, 0) && DeathOutcome.HashToDrop(s, 21, 0));
        Assert.Equal([new DeathMove(20, 1)], DeathOutcome.Resolve([container], both));
    }

    [Fact]
    public void A_stack_is_split_between_the_wreck_and_the_void_and_a_stack_that_drops_whole_or_not_at_all_is_one_action()
    {
        var stack = Node(30, 5, singleton: false, quantity: 1000);
        var (dropped, destroyed) = DeathOutcome.StackDropCounts(30, 5, 1000, Seed);
        Assert.Equal(1000, dropped + destroyed);
        Assert.Equal(
            [new DeathMove(30, dropped), new DeathDestroy(30)],
            DeathOutcome.Resolve([stack], Seed));
        Assert.Equal([new DeathMove(30, 1000)], DeathOutcome.Resolve([stack], Seed, new DeathOptions(ForceAllDropped: true)));
        Assert.Equal([new DeathDestroy(30)], DeathOutcome.Resolve([stack], Seed, new DeathOptions(ForceAllDestroyed: true)));
        // what a stack "holds" is not looked at: a stack is not a container
        var withChild = new DeathNode(31, 34, 5, false, 5, [Node(32, 5)]);
        Assert.Equal([new DeathMove(31, 5)], DeathOutcome.Resolve([withChild], Seed, new DeathOptions(ForceAllDropped: true)));
    }

    [Fact]
    public void The_same_seed_ends_the_same_way_and_another_seed_may_not()
    {
        DeathNode[] contents = [.. Enumerable.Range(1, 40).Select(n => Node(n, 27))];
        Assert.Equal(DeathOutcome.Resolve(contents, Seed), DeathOutcome.Resolve(contents, Seed));
        Assert.NotEqual(DeathOutcome.Resolve(contents, Seed), DeathOutcome.Resolve(contents, Seed + "1"));
    }
}
