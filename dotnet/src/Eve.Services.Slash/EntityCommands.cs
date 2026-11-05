using System.Globalization;
using Eve.Services;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/entity deploy &lt;amount&gt; &lt;entity type|typeID&gt;</c> (the client's Insider and the menu, <c>svc_slash.py</c>): makes NPC entities of the entity category
/// (SDE category 11: the NPC ship groups such as the Blood Raider frigate) in space around the pilot's ship, as <c>/spawn</c> makes ship hulls. The entities are
/// the spawned NPCs <c>/unspawn</c> and <c>/nuke</c> take out again.
/// </summary>
public sealed class EntityCommands(ItemTypeNames names, ISpaceSpawns spawns)
{
    public const string DeployUsage = "Usage: /entity deploy <amount> <entity type|typeID>";
    private const int EntityCategory = 11;

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is null) return "Select a character before using /entity.";
        var parts = argument.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("deploy", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            return DeployUsage;
        if (amount < 1 || amount > SpawnCommands.MaxAmount) return $"Spawn amount must be between 1 and {SpawnCommands.MaxAmount}.";
        var type = parts[2];
        // a name is often several types (a ship and its wreck): the entity of that name is the one that is deployed
        var named = (await names.ExactAsync(type, cancellationToken)).Where(candidate => candidate.CategoryId == EntityCategory).ToList();
        if (named.Count > 1)
            return $"Entity type is ambiguous: {type}. Use the typeID.";
        if (named.Count == 0 && await names.FindAsync(type, cancellationToken) is { Type: null } missing)
            return $"Entity type not found: {type}.{(missing.Suggestions.Count == 0 ? "" : $" Suggestions: {string.Join(", ", missing.Suggestions)}")}";
        var entry = named.Count == 1 ? named[0] : (await names.FindAsync(type, cancellationToken)).Type!;
        if (entry.CategoryId != EntityCategory)
            return $"{entry.Name} is not an entity type. /entity deploy makes entities only.";
        var made = await spawns.SpawnHullsAsync(session, entry.TypeId, amount, 0, cancellationToken);
        return made.Error switch
        {
            SpawnResult.NotInSpace => "You must be in space before using /entity.",
            SpawnResult.SpaceUnavailable => "Space is not available right now. Try again in a moment.",
            SpawnResult.SpawnFailed => $"Deploy failed for {entry.Name}.",
            _ => $"Deployed {made.ShipIds.Count} {entry.Name} in space around you: {string.Join(", ", made.ShipIds)}.",
        };
    }
}
