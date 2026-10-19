using Eve.GameStore;
using Npgsql;

namespace Eve.Host;

/// <summary>

/// go in the deletion's transaction. The game-store paths go through the cache: a row removed underneath the cache would be written
/// back by its next flush. Only rows that belong to the character are removed; corporation-owned data stays.
/// </summary>
internal sealed class CharacterPrivateStatePurge(GameStoreCache cache, string agentsSchema, string bookmarksSchema, string spaceSchema, string skillsSchema = "skills", string charactersSchema = "characters")
{
    private const string CalendarSchema = Eve.Calendar.CalendarStore.DefaultSchema;

    /// <summary>The character's rows: agents' missions, journal, storyline counters, research projects and locate requests; its scanned probes.</summary>
    private string[] PostgresStatements() =>
    [
        $"DELETE FROM {agentsSchema}.missions WHERE character_id = $1",
        $"DELETE FROM {agentsSchema}.journal WHERE character_id = $1",
        $"DELETE FROM {agentsSchema}.storyline_counters WHERE character_id = $1",
        $"DELETE FROM {agentsSchema}.research_projects WHERE character_id = $1",
        $"DELETE FROM {agentsSchema}.locate_requests WHERE character_id = $1",
        $"DELETE FROM {spaceSchema}.scan_probes WHERE character_id = $1",
        // These per-character records outlive ordinary session state and do not cascade from the tombstoned character row.
        $"DELETE FROM {charactersSchema}.nes_intro_states WHERE character_id = $1",
        $"DELETE FROM {charactersSchema}.project_discovery_tutorials WHERE character_id = $1",
        // The account-scoped login milestone remains available to the account's other characters.
        // Skill plans and their dependent requirements/milestones/active selection belong to the character.
        $"DELETE FROM {skillsSchema}.skill_plans WHERE character_id = $1",
        // Achievement children reference their tracker header without cascade.
        $"DELETE FROM {charactersSchema}.achievement_completions WHERE character_id = $1",
        $"DELETE FROM {charactersSchema}.achievement_event_counts WHERE character_id = $1",
        $"DELETE FROM {charactersSchema}.achievement_trackers WHERE character_id = $1",
        // Remove the equipped row first; the owned-title row has no character-delete cascade.
        $"DELETE FROM {charactersSchema}.character_equipped_titles WHERE character_id = $1",
        $"DELETE FROM {charactersSchema}.character_titles WHERE character_id = $1",
        // Clone implants cascade from the clone row. Settlement idempotency rows reference either participant.
        $"DELETE FROM {charactersSchema}.character_jump_clones WHERE character_id = $1",
        $"DELETE FROM {charactersSchema}.character_jump_clone_settlements WHERE character_id = $1 OR offering_character_id = $1",
        // The last-jump timestamp lives on the tombstoned character row.
        $"UPDATE {charactersSchema}.characters SET time_last_clone_jump_filetime = 0 WHERE id = $1",
        // Owned bookmark folders cascade to bookmarks/subfolders; owned calendar events cascade responses.
        $"DELETE FROM {bookmarksSchema}.folders WHERE owner_id = $1",
        $"DELETE FROM {CalendarSchema}.events WHERE owner_id = $1",
        $"DELETE FROM {CalendarSchema}.responses WHERE character_id = $1",
    ];


    private static (string Table, string Path)[] GameStorePaths(long id) =>
    [
        ("mail", $"/mailboxes/{id}"),
        ("notifications", $"/boxes/{id}"),
        ("lpWallets", $"/characterWallets/{id}"),
        ("characterNotes", $"/characters/{id}"),
        ("characters", $"/{id}/characterSettings"),
        ("skillQueues", $"/{id}"),
        ("skillPlans", $"/{id}"),
        ("skillTradingState", $"/{id}"),
        ("characterExpertSystems", $"/{id}"),
        ("missionRuntimeState", $"/charactersByID/{id}"),
        ("researchRuntimeState", $"/charactersByID/{id}"),
        ("probeRuntimeState", $"/charactersByID/{id}"),
    ];

    public async Task PurgePostgresAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        foreach (var statement in PostgresStatements())
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction)
            {
                Parameters = { new() { Value = characterId } },
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task PurgeGameStoreAsync(long characterId, CancellationToken cancellationToken)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (table, path) in GameStorePaths(characterId))
        {
            await cache.LoadAsync(table, cancellationToken);
            if (table == "skillPlans")
            {

                var plans = cache.Read(table, "/");
                if (!plans.Success || plans.Data is not System.Text.Json.Nodes.JsonObject root)
                    throw new InvalidOperationException($"Could not read {table} before purging character {characterId}.");
                if (root.Remove(characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                {
                    var write = cache.Write(table, "/", root);
                    if (!write.Success)
                        throw new InvalidOperationException($"Could not purge {table}/{characterId}: {write.ErrorMsg}");
                }
            }
            else
            {
                var removed = cache.Remove(table, path);
                if (!removed.Success && removed.ErrorMsg != "ENTRY_NOT_FOUND")
                    throw new InvalidOperationException($"Could not purge {table}{path}: {removed.ErrorMsg}");
            }
            tables.Add(table);
        }
        foreach (var table in tables)
            await cache.FlushTableAsync(table, cancellationToken);
    }
}
