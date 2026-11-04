using System.Text.RegularExpressions;

namespace Eve.GameStore;

/// <summary>
/// Which process role may mutate which table. Every table belongs to the
/// <c>world</c> role unless listed in <see cref="TableRole"/>; maintenance and
/// standalone processes may mutate anything, readers nothing.
/// </summary>
public static partial class PersistenceRoles
{
    public const string World = "world";
    public const string Wallet = "wallet";
    public const string Scheduler = "scheduler";
    public const string Maintenance = "maintenance";
    public const string Reader = "reader";
    public const string Standalone = "standalone";

    public static readonly IReadOnlySet<string> ConfiguredRoles =
        new HashSet<string> { World, Wallet, Scheduler, Maintenance, Reader, Standalone };

    public static readonly IReadOnlyDictionary<string, string> TableRole = new Dictionary<string, string>
    {
        ["walletAuthorityState"] = Wallet,
        ["scheduledJobs"] = Scheduler,
    };

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex Disallowed();

    // JavaScript's String.prototype.trim whitespace, which differs from .NET's.
    private static readonly char[] TrimChars =
    [
        '\t', '\n', '\v', '\f', '\r', ' ', '\u00a0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a',
        '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff',
    ];

    /// <summary>Lower-cases, replaces runs of characters outside <c>[a-z0-9._-]</c> with '-', trims '-'.</summary>
    public static string Normalize(string? value, string fallback = Reader)
    {
        var trimmed = (value ?? "").AsSpan().Trim(TrimChars).ToString();
        var normalized = Disallowed().Replace(trimmed.ToLowerInvariant(), "-").Trim('-');
        return normalized.Length == 0 ? fallback : normalized;
    }

    public static string OwnerRoleForTable(string? table) =>
        table is not null && TableRole.TryGetValue(table, out var role) ? role : World;

    private static bool Unrestricted(string role) => role is Standalone or Maintenance;

    public static bool MayMutateTable(string? role, string? table)
    {
        var normalized = Normalize(role);
        if (Unrestricted(normalized))
        {
            return true;
        }
        return normalized != Reader && normalized == OwnerRoleForTable(table);
    }

    /// <summary>Loops over the broad world surface (tick, expiry sweeps) ask this instead of probing one table.</summary>
    public static bool MayDriveWorldState(string? role)
    {
        var normalized = Normalize(role);
        return Unrestricted(normalized) || normalized == World;
    }

    public static void AssertMayMutateTable(string? role, string? table, string operation = "mutate")
    {
        if (MayMutateTable(role, table))
        {
            return;
        }
        var normalized = Normalize(role);
        throw new PersistenceException(
            $"gameStore process role {normalized} may not {operation} {table}; durable owner is {OwnerRoleForTable(table)}",
            PersistenceCodes.ProcessOwnershipViolation,
            new Dictionary<string, object?>
            {
                ["ownerRole"] = OwnerRoleForTable(table),
                ["processRole"] = normalized,
                ["table"] = table ?? "",
            });
    }

    /// <summary>The role from a configuration value; an unknown value is an error, an empty one is a passive reader.</summary>
    public static string Configured(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Reader;
        }
        var role = Normalize(configured);
        if (!ConfiguredRoles.Contains(role))
        {
            throw new PersistenceException($"unknown owner role: \"{configured}\"", PersistenceCodes.ProcessRoleInvalid);
        }
        return role;
    }
}
