using System.Text.RegularExpressions;

namespace Eve.GameStore;

/// <summary>SQL identifiers are interpolated into statements, so only plain names are accepted.</summary>
public static partial class TableNames
{
    public const string Migrations = "_migrations";
    public const string Outbox = "_persistence_outbox";
    public const string Owners = "_persistence_owners";

    // PostgreSQL silently truncates identifiers longer than 63 bytes.
    public const int MaxLength = 63;

    [GeneratedRegex(@"\A[A-Za-z0-9_]+\z")]
    private static partial Regex SafePattern();

    public static bool IsSafe(string? name) =>
        name is { Length: > 0 and <= MaxLength } && SafePattern().IsMatch(name);

    public static bool IsInternal(string name) =>
        name.Equals(Migrations, StringComparison.OrdinalIgnoreCase)
        || name.Equals(Outbox, StringComparison.OrdinalIgnoreCase)
        || name.Equals(Owners, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    public static string RequireSafe(string? name)
    {
        if (!IsSafe(name))
        {
            throw new ArgumentException($"unsafe table name: {name ?? "<null>"}", nameof(name));
        }
        return name!;
    }

    /// <summary>A table a caller may journal or mutate: safe, and not one of the store's own tables.</summary>
    public static string RequirePersistence(string? name, string label = "persistence table")
    {
        if (name is null)
        {
            throw new ArgumentException($"{label} must be a string", nameof(name));
        }
        RequireSafe(name);
        if (IsInternal(name))
        {
            throw new ArgumentException($"{label} may not name an internal table", nameof(name));
        }
        return name;
    }
}
