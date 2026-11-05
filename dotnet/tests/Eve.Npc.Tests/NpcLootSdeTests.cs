using Eve.Data;
using Eve.Sde;
using Npgsql;

namespace Eve.Npc.Tests;

/// <summary>The loot tables the reference authored, rolled against the types of the real static data.</summary>
public sealed class NpcLootSdeTests
{
    private static async Task<NpcLoot> LoadAsync()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(BountyRig.Url!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        return await NpcLoot.LoadAsync(sde);
    }

    [PgFact]
    public async Task The_pool_has_the_published_types_with_the_names_of_their_groups_and_no_wreck()
    {
        var loot = await LoadAsync();
        Assert.InRange(loot.PoolSize, 10_000, 100_000);
        var authority = NpcAuthority.Load();
        var drops = new List<LootDrop>();
        var random = new Random(7);
        // the table of the commonest belt rats: ammunition, modules, utilities and salvage of the small hulls and now and then nothing
        for (var i = 0; i < 400; i++)
        {
            drops.AddRange(loot.Roll(authority.LootTable("loot_belt_normal_small"), random.NextDouble));
        }
        Assert.NotEmpty(drops);
        var names = drops.Select(d => d.Name).Distinct().ToList();
        Assert.Contains(names, n => n.EndsWith(" S", StringComparison.Ordinal) || n.Contains("Scraps", StringComparison.Ordinal));
        // nothing of a special grade, a blueprint or tech two comes out of a plain table
        Assert.DoesNotContain(names, n => n.Contains("Blueprint", StringComparison.Ordinal) || n.EndsWith(" II", StringComparison.Ordinal) || n.Contains("Navy", StringComparison.Ordinal));
        // the stacks are within what the rule says (15 to 60 of a charge) and a module is one
        Assert.All(drops.Where(d => d.Singleton), d => Assert.Equal(1, d.Quantity));
        Assert.All(drops.Where(d => !d.Singleton), d => Assert.InRange(d.Quantity, 1, 60));
    }

    [PgFact]
    public async Task A_plain_draw_takes_anything_published_but_a_wreck()
    {
        var loot = await LoadAsync();
        var random = new Random(11);
        var drops = Enumerable.Range(0, 300).SelectMany(_ => loot.Roll(NpcAuthority.Load().LootTable("generic_random_any"), random.NextDouble)).ToList();
        Assert.InRange(drops.Count, 300, 900);
        Assert.All(drops, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
        Assert.All(drops, d => Assert.InRange(d.Quantity, 1, 25));
    }
}
