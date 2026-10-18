using Eve.Sde;

namespace Eve.Services.Slash;

/// <summary>
/// Every item type by name, for the commands that take one (<c>/giveitem Antimatter Charge S 500</c>). A port, as it is, of <c>resolveItemByName</c> of EVE.js
/// (<c>services/inventory/itemTypeRegistry.js</c>, ADR 0009): a type id, else the one type whose name is the query (case and punctuation aside), else the one whose name
/// holds it; several are ambiguous and the reference lists the first five. The registry is read from the static data when first asked for.
/// </summary>
public sealed class ItemTypeNames(SdeReader sde)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ItemTypeEntry>? _types;

    private async Task<List<ItemTypeEntry>> TypesAsync(CancellationToken cancellationToken)
    {
        if (_types is { } known)
        {
            return known;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_types is { } ready)
            {
                return ready;
            }
            var categories = (await sde.ReadAsync("groups", cancellationToken: cancellationToken)).ToDictionary(row => (int)row.Key, row => row.Doc.GetProperty("categoryID").GetInt32());
            return _types = (await sde.ReadAsync("types", cancellationToken: cancellationToken))
                .Where(row => categories.ContainsKey(row.Doc.GetProperty("groupID").GetInt32()))
                .Select(row => new ItemTypeEntry(
                    (int)row.Key, SdeReader.English(row.Doc.GetProperty("name")).Trim(), row.Doc.GetProperty("groupID").GetInt32(), categories[row.Doc.GetProperty("groupID").GetInt32()],
                    !row.Doc.TryGetProperty("published", out var published) || published.ValueKind != System.Text.Json.JsonValueKind.False))
                .Where(entry => entry.Name.Length > 0)
                .OrderBy(entry => entry.TypeId)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Label(ItemTypeEntry entry) => $"{entry.Name} ({entry.TypeId}{(entry.Published ? "" : ", unpublished")})";

    /// <summary>Every type whose name is the query (case and punctuation aside), in type order; empty when none is.</summary>
    public async Task<IReadOnlyList<ItemTypeEntry>> ExactAsync(string query, CancellationToken cancellationToken = default)
    {
        var normalized = ShipTypeNames.Normalize(query);
        if (normalized.Length == 0)
        {
            return [];
        }
        return (await TypesAsync(cancellationToken)).Where(entry => ShipTypeNames.Normalize(entry.Name) == normalized).ToArray();
    }

    /// <summary>The item type a name (or a type id) is; <see cref="ItemMatch.Error"/> says why there is none.</summary>
    public async Task<ItemMatch> FindAsync(string query, CancellationToken cancellationToken = default)
    {
        var normalized = ShipTypeNames.Normalize(query);
        if (normalized.Length == 0)
        {
            return new ItemMatch(null, "ITEM_NAME_REQUIRED", []);
        }
        var types = await TypesAsync(cancellationToken);
        if (int.TryParse(normalized, out var typeId) && types.FirstOrDefault(entry => entry.TypeId == typeId) is { } byId)
        {
            return new ItemMatch(byId, null, []);
        }
        var exact = types.Where(entry => ShipTypeNames.Normalize(entry.Name) == normalized).ToList();
        if (exact.Count == 1)
        {
            return new ItemMatch(exact[0], null, []);
        }
        if (exact.Count > 1)
        {
            return new ItemMatch(null, "AMBIGUOUS_ITEM_NAME", [.. exact.Take(5).Select(Label)]);
        }
        var partial = types.Where(entry => ShipTypeNames.Normalize(entry.Name).Contains(normalized, StringComparison.Ordinal)).ToList();
        if (partial.Count == 1)
        {
            return new ItemMatch(partial[0], null, []);
        }
        return new ItemMatch(null, partial.Count > 1 ? "AMBIGUOUS_ITEM_NAME" : "ITEM_NOT_FOUND", [.. partial.Take(5).Select(Label)]);
    }
}

public sealed record ItemTypeEntry(int TypeId, string Name, int GroupId, int CategoryId, bool Published);

/// <summary>An item type found by name, or why none was.</summary>
public sealed record ItemMatch(ItemTypeEntry? Type, string? Error, IReadOnlyList<string> Suggestions);
