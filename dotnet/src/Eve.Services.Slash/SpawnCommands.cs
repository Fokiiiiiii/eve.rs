using System.Globalization;
using Eve.Services;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's spawns: <c>/spawn &lt;ship&gt;</c> and <c>/spawnn &lt;amount&gt; &lt;deviation&gt; &lt;ship&gt;</c> bring ship hulls into space around the pilot's
/// ship, <c>/unspawn &lt;itemID|range=metres only=categoryShip&gt;</c> takes them out again, and <c>/nuke</c> takes out every NPC ship the host made in the pilot's
/// solar system. Only ship types are made here (the reference's other categories are not served, and the reply says so).
/// </summary>
public sealed class SpawnCommands(ItemTypeNames names, ISpaceSpawns spawns)
{
    public const string SpawnUsage = "Usage: /spawn <ship type|typeID>";
    public const string SpawnnUsage = "Usage: /spawnn <amount> <deviation metres> <ship type|typeID>";
    public const string UnspawnUsage = "Usage: /unspawn <itemID> or /unspawn range=<metres> only=categoryShip";
    public const int MaxAmount = 50;
    private const int ShipCategory = 6;

    public async Task<string> SpawnAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is null) return "Select a character before using /spawn.";
        var text = argument.Trim();
        return text.Length == 0 ? SpawnUsage : await MakeAsync(session, text, 1, 0, cancellationToken);
    }

    public async Task<string> SpawnnAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is null) return "Select a character before using /spawnn.";
        var parts = argument.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var deviation) || !double.IsFinite(deviation) || deviation < 0)
            return SpawnnUsage;
        if (amount < 1 || amount > MaxAmount) return $"Spawn amount must be between 1 and {MaxAmount}.";
        return await MakeAsync(session, parts[2], amount, deviation, cancellationToken);
    }

    private async Task<string> MakeAsync(ClientSession session, string type, int count, double deviation, CancellationToken cancellationToken)
    {
        // a name is often several types (a ship and its wreck, a skin): the ship of that name is the one /spawn makes
        var named = (await names.ExactAsync(type, cancellationToken)).Where(candidate => candidate.CategoryId == ShipCategory).ToList();
        if (named.Count > 1)
            return $"Ship type is ambiguous: {type}. Use the typeID.";
        if (named.Count == 0 && await names.FindAsync(type, cancellationToken) is { Type: null } missing)
            return $"Ship type not found: {type}.{(missing.Suggestions.Count == 0 ? "" : $" Suggestions: {string.Join(", ", missing.Suggestions)}")}";
        var entry = named.Count == 1 ? named[0] : (await names.FindAsync(type, cancellationToken)).Type!;
        if (entry.CategoryId != ShipCategory)
            return $"{entry.Name} is not a ship type. /spawn makes ship hulls only.";
        var made = await spawns.SpawnHullsAsync(session, entry.TypeId, count, deviation, cancellationToken);
        return made.Error switch
        {
            SpawnResult.NotInSpace => "You must be in space before using /spawn.",
            SpawnResult.SpaceUnavailable => "Space is not available right now. Try again in a moment.",
            SpawnResult.SpawnFailed => $"Spawn failed for {entry.Name}.",
            _ => $"Spawned {made.ShipIds.Count} {entry.Name} in space around you: {string.Join(", ", made.ShipIds)}.",
        };
    }

    public async Task<string> UnspawnAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is null) return "Select a character before using /unspawn.";
        var tokens = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return UnspawnUsage;
        long? wanted = null;
        double? range = null;
        string? only = null;
        foreach (var token in tokens)
        {
            if (token.StartsWith("range=", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(token[6..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed < 0)
                    return UnspawnUsage;
                range = parsed;
            }
            else if (token.StartsWith("only=", StringComparison.OrdinalIgnoreCase))
                only = token[5..];
            else if (tokens.Length == 1 && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
                wanted = itemId;
            else
                return UnspawnUsage;
        }
        if (await spawns.SpawnedAsync(session, cancellationToken) is not { } view)
            return "You must be in space before using /unspawn.";
        // every ship the host makes is a ship: a category other than ship matches none of them
        if (only is not null && !only.Equals("categoryShip", StringComparison.OrdinalIgnoreCase) && !only.Equals("ship", StringComparison.OrdinalIgnoreCase))
            return "Nothing to unspawn.";
        var ships = view.Ships.AsEnumerable();
        if (wanted is { } id)
        {
            if (!view.Ships.Any(ship => ship.ShipId == id))
                return $"Not spawned in your solar system: {id}.";
            ships = ships.Where(ship => ship.ShipId == id);
        }
        if (range is { } limit)
        {
            ships = ships.Where(ship => view.Own is { } own && ship.Position is { } at && Distance(own, at) <= limit);
        }
        var removed = await spawns.RemoveAsync(ships.Select(ship => ship.ShipId).ToArray(), cancellationToken);
        return removed == 0 ? "Nothing to unspawn." : $"Unspawned {removed} {(removed == 1 ? "ship" : "ships")}.";
    }

    /// <summary><c>/nuke</c>: every NPC ship the host made in the pilot's solar system goes out of space.</summary>
    public async Task<string> NukeAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.CharacterId is null) return "Select a character before using /nuke.";
        if (await spawns.SpawnedAsync(session, cancellationToken) is not { } view)
            return "You must be in space before using /nuke.";
        if (view.Ships.Count == 0) return "No spawned NPC ship is in your solar system.";
        var removed = await spawns.RemoveAsync(view.Ships.Select(ship => ship.ShipId).ToArray(), cancellationToken);
        return $"Removed {removed} NPC {(removed == 1 ? "ship" : "ships")} from your solar system.";
    }

    private static double Distance(IReadOnlyList<double> from, IReadOnlyList<double> to) =>
        Math.Sqrt(Math.Pow(to[0] - from[0], 2) + Math.Pow(to[1] - from[1], 2) + Math.Pow(to[2] - from[2], 2));
}
