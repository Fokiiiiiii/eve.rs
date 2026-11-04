using System.Text.Json.Nodes;

namespace Eve.GameStore;

/// <summary>
/// Splits a table object into the rows stored in the database and back.
///
/// Most tables are flat: each top-level key is one row. A few nest entities one
/// level under a named group (<c>mail.messages[id]</c>). For those the group's
/// entities are stored as one row each (key <c>group\u001fentity</c>) plus a
/// skeleton <c>{}</c> row under the group name, so one changed entity is one
/// small row. Behaviour matches the reference server's explodeToRows /
/// assembleFromRows / rowValueForKey, which the golden fixtures pin.
///
/// Object key order is not preserved across a round trip: PostgreSQL jsonb
/// orders keys by length then bytes, and the reference iterates integer-like
/// keys first. Callers must not depend on it.
/// </summary>
public static partial class GameStoreTables
{
    /// <summary>Separator between group and entity in a stored row key.</summary>
    public const char RowKeySeparator = '\u001f';

    /// <summary>The row groups of <paramref name="table"/>, or null for a flat table.</summary>
    public static IReadOnlyList<string>? RowGroupsFor(string? table) =>
        table is not null && RowGroups.TryGetValue(table, out var groups) ? groups : null;
}

public static class RowCodec
{
    private static bool IsGroupValue(IReadOnlyList<string>? groups, string key, JsonNode? value) =>
        groups is not null && value is JsonObject && groups.Contains(key);

    /// <summary>
    /// The stored rows of a table object, without copying: the values are the
    /// live nodes of <paramref name="table"/> (group entities and flat values)
    /// or fresh <c>{}</c> skeletons. Throws while enumerating on an entity key
    /// that contains the separator.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, JsonNode?>> EnumerateRows(string tableName, JsonObject? table)
    {
        if (table is null)
        {
            yield break;
        }
        var groups = GameStoreTables.RowGroupsFor(tableName);
        foreach (var (key, value) in table)
        {
            if (IsGroupValue(groups, key, value))
            {
                yield return new(key, new JsonObject()); // records that the group exists, even when empty
                foreach (var (entityKey, entity) in (JsonObject)value!)
                {
                    if (entityKey.Contains(GameStoreTables.RowKeySeparator))
                    {
                        throw new InvalidOperationException(
                            $"entity key contains reserved separator: {tableName}.{key}.{entityKey}");
                    }
                    yield return new($"{key}{GameStoreTables.RowKeySeparator}{entityKey}", entity);
                }
            }
            else
            {
                yield return new(key, value);
            }
        }
    }

    /// <summary>The stored rows of a table object, with deep-copied values.</summary>
    public static OrderedDictionary<string, JsonNode?> Explode(string tableName, JsonObject? table)
    {
        var rows = new OrderedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in EnumerateRows(tableName, table))
        {
            rows[key] = value?.DeepClone();
        }
        return rows;
    }

    /// <summary>
    /// The inverse of <see cref="Explode"/>. Order independent; legacy whole-group
    /// blobs are merged without overriding per-entity rows.
    /// </summary>
    public static JsonObject Assemble(string tableName, IEnumerable<KeyValuePair<string, JsonNode?>> rows)
    {
        var groups = GameStoreTables.RowGroupsFor(tableName);
        var result = new JsonObject();
        foreach (var (rowKey, value) in rows)
        {
            var separator = rowKey.IndexOf(GameStoreTables.RowKeySeparator);
            if (separator >= 0)
            {
                var group = rowKey[..separator];
                var entityKey = rowKey[(separator + 1)..];
                if (result[group] is not JsonObject container)
                {
                    container = new JsonObject();
                    result[group] = container;
                }
                container[entityKey] = value;
            }
            else if (groups is not null && groups.Contains(rowKey))
            {
                var merged = new JsonObject();
                if (value is JsonObject blob)
                {
                    foreach (var (blobKey, blobValue) in blob)
                    {
                        merged[blobKey] = blobValue?.DeepClone();
                    }
                }
                if (result[rowKey] is JsonObject existing)
                {
                    foreach (var (entityKey, entity) in existing.ToArray())
                    {
                        existing.Remove(entityKey);
                        merged[entityKey] = entity;
                    }
                }
                result[rowKey] = merged;
            }
            else
            {
                result[rowKey] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// The value of one stored row read straight from a table object, without
    /// exploding it: the counterpart used by the dirty-row flush path. False
    /// means the row does not exist (it was removed).
    /// </summary>
    public static bool TryGetRowValue(string tableName, JsonObject? table, string rowKey, out JsonNode? value)
    {
        value = null;
        if (table is null)
        {
            return false;
        }
        var separator = rowKey.IndexOf(GameStoreTables.RowKeySeparator);
        if (separator >= 0)
        {
            var group = rowKey[..separator];
            var entityKey = rowKey[(separator + 1)..];
            if (table.TryGetPropertyValue(group, out var groupValue)
                && groupValue is JsonObject entities
                && entities.TryGetPropertyValue(entityKey, out value))
            {
                return true;
            }
            value = null;
            return false;
        }
        if (!table.TryGetPropertyValue(rowKey, out value))
        {
            return false;
        }
        if (IsGroupValue(GameStoreTables.RowGroupsFor(tableName), rowKey, value))
        {
            value = new JsonObject();
        }
        return true;
    }
}
