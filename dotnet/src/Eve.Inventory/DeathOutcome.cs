using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Eve.Inventory;

/// <summary>An item in what a destroyed place held, with what it holds in turn.</summary>
public sealed record DeathNode(long ItemId, int TypeId, int Flag, bool Singleton, long Quantity, IReadOnlyList<DeathNode> Children);

/// <summary>What is done with an item of a destroyed place: moved to the wreck (a stack: this many of it), or gone.</summary>
public abstract record DeathAction(long ItemId);

public sealed record DeathMove(long ItemId, long Quantity) : DeathAction(ItemId);

public sealed record DeathDestroy(long ItemId) : DeathAction(ItemId);

/// <summary>Options that decide everything for everything (the reference's <c>forceAllDestroyed</c> and <c>forceAllDropped</c>; destroying wins).</summary>
public readonly record struct DeathOptions(bool ForceAllDestroyed = false, bool ForceAllDropped = false);

/// <summary>
/// What becomes of what a destroyed ship held: each item that is not a rig or a subsystem is dropped into the wreck or destroyed, by a coin
/// that the seed decides (so the same death always ends the same way), a stack of many units one draw for how many drop. A port, as it is, of
/// <c>resolveLocationDeathOutcome</c> and what it calls in <c>server/src/services/killmail/deathOutcomeResolver.js</c> of EVE.js (ADR 0009);
/// <c>oracle/fixtures/death/drop_counts.json</c> holds it to the reference.
/// </summary>
public static class DeathOutcome
{
    private const int RigFlagMin = 92;
    private const int RigFlagMax = 99;
    private const int SubsystemFlagMin = 125;
    private const int SubsystemFlagMax = 132;

    /// <summary>
    /// microsecond, and a hold full of tritanium is tens of millions of units).
    /// </summary>
    public const long PerUnitDropRollLimit = 100;

    /// <summary>Whether the flag is a slot of rigs or subsystems: what is fitted there never survives.</summary>
    public static bool IsAlwaysDestroyedFlag(int flag) =>
        (flag >= RigFlagMin && flag <= RigFlagMax) || (flag >= SubsystemFlagMin && flag <= SubsystemFlagMax);

    private static byte[] Digest(string seed, long itemId, string tail) =>
        SHA1.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{seed}:{itemId}:{tail}")));

    private static uint BigEndian(byte[] digest, int offset) =>
        (uint)((digest[offset] << 24) | (digest[offset + 1] << 16) | (digest[offset + 2] << 8) | digest[offset + 3]);

    /// <summary>The coin of one unit of an item: true when it drops (a chance of one half).</summary>
    public static bool HashToDrop(string seed, long itemId, long unitIndex) =>
        BigEndian(Digest(seed, itemId, unitIndex.ToString(CultureInfo.InvariantCulture)), 0) / (double)0xffffffffu < 0.5;

    /// <summary>
    /// How many units of a large stack drop: a normal draw with the per-unit roll's mean and spread, n/2 and sqrt(n)/2, rounded to a whole unit
    /// (JavaScript's rounding: halves go up). It hashes once per stack, so it costs the same for 101 units as for two billion.
    /// </summary>
    public static long DrawStackDropCount(string seed, long itemId, long quantity)
    {
        var digest = Digest(seed, itemId, "stack");
        var u1 = (BigEndian(digest, 0) + 1.0) / 4294967296.0;
        var u2 = BigEndian(digest, 4) / 4294967296.0;
        var z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        var dropped = (long)Math.Floor(quantity / 2.0 + z * Math.Sqrt(quantity) / 2 + 0.5);
        return Math.Min(quantity, Math.Max(0, dropped));
    }

    /// <summary>How many units of a stack (or one item: a stack of one) drop and how many are destroyed.</summary>
    public static (long Dropped, long Destroyed) StackDropCounts(long itemId, int flag, long quantity, string seed, DeathOptions options = default)
    {
        quantity = Math.Max(1, quantity);
        if (options.ForceAllDestroyed || IsAlwaysDestroyedFlag(flag))
        {
            return (0, quantity);
        }
        if (options.ForceAllDropped)
        {
            return (quantity, 0);
        }
        if (quantity > PerUnitDropRollLimit)
        {
            var drawn = DrawStackDropCount(seed, itemId, quantity);
            return (drawn, quantity - drawn);
        }
        long dropped = 0;
        for (long unit = 0; unit < quantity; unit++)
        {
            if (HashToDrop(seed, itemId, unit))
            {
                dropped++;
            }
        }
        return (dropped, quantity - dropped);
    }

    private sealed record Resolved(DeathNode Node, bool Singleton, long Dropped, long Destroyed, IReadOnlyList<Resolved> Children);

    private static Resolved Resolve(DeathNode node, string seed, DeathOptions options)
    {
        var quantity = Math.Max(1, node.Quantity);
        var singleton = node.Singleton || quantity == 1;
        var stack = StackDropCounts(node.ItemId, node.Flag, quantity, seed, options);
        var children = node.Children.Select(child => Resolve(child, seed, options)).ToList();
        if (!singleton)
        {
            return new Resolved(node, false, stack.Dropped, stack.Destroyed, []);
        }
        var dropped = options.ForceAllDestroyed || IsAlwaysDestroyedFlag(node.Flag) ? 0
            : options.ForceAllDropped ? 1
            : HashToDrop(seed, node.ItemId, 0) ? 1 : 0;
        return new Resolved(node, true, dropped, 1 - dropped, children);
    }

    private static void Apply(Resolved resolution, bool parentDropped, List<DeathAction> actions)
    {
        if (!resolution.Singleton)
        {
            if (resolution.Dropped > 0)
            {
                actions.Add(new DeathMove(resolution.Node.ItemId, resolution.Dropped));
            }
            if (resolution.Destroyed > 0)
            {
                actions.Add(new DeathDestroy(resolution.Node.ItemId));
            }
            return;
        }
        var dropped = resolution.Dropped > 0;
        if (dropped && !parentDropped)
        {
            actions.Add(new DeathMove(resolution.Node.ItemId, 1));
        }
        foreach (var child in resolution.Children)
        {
            Apply(child, dropped, actions);
        }
        if (!dropped)
        {
            actions.Add(new DeathDestroy(resolution.Node.ItemId));
        }
    }

    /// <summary>
    /// What is done with what a place held, in the order the reference does it: for each top item, in the order of the ids, the item itself is moved
    /// to the wreck if it drops and is not inside something that does, then what it holds, then it is destroyed if it did not drop.
    /// </summary>
    public static IReadOnlyList<DeathAction> Resolve(IEnumerable<DeathNode> contents, string seed, DeathOptions options = default)
    {
        var actions = new List<DeathAction>();
        foreach (var node in contents)
        {
            Apply(Resolve(node, seed, options), false, actions);
        }
        return actions;
    }
}
