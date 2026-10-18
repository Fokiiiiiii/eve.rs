namespace Eve.Services.Slash;

/// <summary>
/// <c>/asteroid rewards</c>: the asteroid rocks in the pilot's solar system, each with its ore and the units it still holds, then the units of each ore in total.
/// The rocks are the space items the host seeded for the system; the pilot's solar system is read by the composition (the pilot must be in space).
/// </summary>
public sealed class AsteroidCommands(Func<ClientSession, CancellationToken, Task<IReadOnlyList<(long ItemId, int TypeId, string Name, long Units)>?>> rocks)
{
    public const string Usage = "Usage: /asteroid rewards";

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (!argument.Trim().Equals("rewards", StringComparison.OrdinalIgnoreCase)) return Usage;
        if (session.CharacterId is null) return "Select a character before using /asteroid.";
        if (await rocks(session, cancellationToken) is not { } found) return "You must be in space before using /asteroid.";
        if (found.Count == 0) return "No asteroid with units left is in your solar system.";
        var lines = found.Select(rock => $"{rock.ItemId}: {rock.Name} ({rock.TypeId}) reward {rock.Units} units");
        var totals = found.GroupBy(rock => rock.Name).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}: {group.Sum(rock => rock.Units)} units");
        return $"Asteroid rewards in your solar system ({found.Count} rocks):\n{string.Join("\n", lines)}\nTotals: {string.Join(", ", totals)}";
    }
}
