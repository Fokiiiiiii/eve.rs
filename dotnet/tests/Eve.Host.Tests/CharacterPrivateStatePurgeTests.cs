using Eve.Data;
using Eve.GameStore;
using Eve.Host;
using Npgsql;
using System.Text.Json.Nodes;

namespace Eve.Host.Tests;

/// <summary>A deleted character's game-store rows are removed (EveJS purgePrivateCharacterState), the other characters' rows stay, and the removal is
/// written: a fresh cache after a flush and a restart does not see them.</summary>
public sealed class CharacterPrivateStatePurgeTests
{
    [PgFact]
    public async Task A_deleted_characters_game_store_rows_are_removed_and_the_removal_survives_a_flush_and_a_restart()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl!));
        var schema = "priv_purge_" + Guid.NewGuid().ToString("N");
        var store = new PersistenceStore(source, schema);
        OwnerSession? owner = null;
        GameStoreCache? cache = null;
        try
        {
            await store.EnsureSchemaAsync();
            owner = await OwnerSession.StartAsync(store, PersistenceRoles.World, "priv-purge-" + Guid.NewGuid().ToString("N"));
            cache = Cache(store, owner);
            await cache.RecoverAsync();

            await Seed(cache, "mail", "/mailboxes/7", new JsonObject { ["statuses"] = new JsonObject() });
            await Seed(cache, "mail", "/mailboxes/8", new JsonObject { ["statuses"] = new JsonObject() });
            await Seed(cache, "notifications", "/boxes/7", new JsonObject { ["byID"] = new JsonObject() });
            await Seed(cache, "lpWallets", "/characterWallets/7", new JsonObject { ["balance"] = 5 });
            await Seed(cache, "characterNotes", "/characters/7", new JsonObject { ["note"] = "private" });
            await Seed(cache, "characterNotes", "/characters/8", new JsonObject { ["note"] = "other" });
            await Seed(cache, "characters", "/7", new JsonObject { ["name"] = "gone", ["characterSettings"] = new JsonObject { ["x"] = 1 } });
            await Seed(cache, "characters", "/8", new JsonObject { ["name"] = "kept" });
            await Seed(cache, "skillPlans", "/7", new JsonObject { ["plans"] = new JsonArray() });
            await Seed(cache, "skillPlans", "/8", new JsonObject { ["plans"] = new JsonArray() });
            await Seed(cache, "skillQueues", "/7", new JsonObject { ["queue"] = new JsonArray() });
            await Seed(cache, "skillQueues", "/8", new JsonObject { ["queue"] = new JsonArray() });
            await Seed(cache, "skillTradingState", "/7", new JsonObject { ["offers"] = new JsonArray() });
            await Seed(cache, "skillTradingState", "/8", new JsonObject { ["offers"] = new JsonArray() });
            await Seed(cache, "missionRuntimeState", "/charactersByID/7", new JsonObject { ["missions"] = 1 });
            await Seed(cache, "researchRuntimeState", "/charactersByID/7", new JsonObject { ["projects"] = 1 });

            await new CharacterPrivateStatePurge(cache, "agents", "bookmarks", "space").PurgeGameStoreAsync(7, default);

            Assert.False(cache.Read("mail", "/mailboxes/7").Success);
            Assert.True(cache.Read("mail", "/mailboxes/8").Success);
            Assert.False(cache.Read("notifications", "/boxes/7").Success);
            Assert.False(cache.Read("lpWallets", "/characterWallets/7").Success);
            Assert.False(cache.Read("characterNotes", "/characters/7").Success);
            Assert.True(cache.Read("characterNotes", "/characters/8").Success);
            Assert.False(cache.Read("characters", "/7/characterSettings").Success);
            Assert.Equal("gone", cache.Read("characters", "/7/name").Data?.GetValue<string>(), ignoreCase: false);
            Assert.True(cache.Read("characters", "/8").Success);
            Assert.False(cache.Read("skillPlans", "/7").Success);
            Assert.True(cache.Read("skillPlans", "/8").Success);
            Assert.False(cache.Read("skillQueues", "/7").Success);
            Assert.True(cache.Read("skillQueues", "/8").Success);
            Assert.False(cache.Read("skillTradingState", "/7").Success);
            Assert.True(cache.Read("skillTradingState", "/8").Success);
            Assert.False(cache.Read("missionRuntimeState", "/charactersByID/7").Success);
            Assert.False(cache.Read("researchRuntimeState", "/charactersByID/7").Success);

            // written, not only in memory: a new cache loads the removal
            await cache.DisposeAsync();
            cache = Cache(store, owner);
            await cache.RecoverAsync();
            await cache.LoadAsync("mail");
            await cache.LoadAsync("skillPlans");
            await cache.LoadAsync("skillQueues");
            await cache.LoadAsync("skillTradingState");
            await cache.LoadAsync("characterNotes");
            await cache.LoadAsync("characters");
            Assert.False(cache.Read("mail", "/mailboxes/7").Success);
            Assert.False(cache.Read("skillPlans", "/7").Success);
            Assert.True(cache.Read("skillPlans", "/8").Success);
            Assert.False(cache.Read("skillQueues", "/7").Success);
            Assert.False(cache.Read("skillTradingState", "/7").Success);
            Assert.False(cache.Read("characterNotes", "/characters/7").Success);
            Assert.False(cache.Read("characters", "/7/characterSettings").Success);
            Assert.True(cache.Read("mail", "/mailboxes/8").Success);
        }
        finally
        {
            if (cache is not null) await cache.DisposeAsync();
            if (owner is not null) await owner.DisposeAsync();
            await store.DisposeAsync();
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static GameStoreCache Cache(PersistenceStore store, OwnerSession owner) =>
        new(store, owner, new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });

    private static async Task Seed(GameStoreCache cache, string table, string path, JsonNode value)
    {
        await cache.LoadAsync(table);
        Assert.True(cache.Write(table, path, value).Success, $"{table}{path}");
    }
}
