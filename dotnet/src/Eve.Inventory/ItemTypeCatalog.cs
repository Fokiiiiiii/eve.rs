using System.Collections.Concurrent;
using Eve.Sde;

namespace Eve.Inventory;

/// <summary>An item type as the client sorts it: the group and category it belongs to.</summary>
public sealed record ItemType(int TypeId, int GroupId, int CategoryId, decimal BasePrice = 0m);

/// <summary>
/// The group and category of item types, read from the static data when first asked for and kept. A type the data does not
/// have is left out of the answer.
/// </summary>
public sealed class ItemTypeCatalog(SdeReader sde)
{
    private readonly ConcurrentDictionary<int, ItemType> _types = new();
    private readonly SemaphoreSlim _groupsGate = new(1, 1);
    private Dictionary<int, int>? _categoryOfGroup;

    /// <summary>The types with these ids, by id.</summary>
    public async Task<IReadOnlyDictionary<int, ItemType>> GetAsync(IEnumerable<int> typeIds, CancellationToken cancellationToken = default)
    {
        var wanted = typeIds.Distinct().ToArray();
        var missing = wanted.Where(id => !_types.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            var groups = await GroupsAsync(cancellationToken);
            foreach (var row in await sde.ReadAsync("types", missing.Select(id => (long)id), cancellationToken))
            {
                var group = row.Doc.GetProperty("groupID").GetInt32();
                if (groups.TryGetValue(group, out var category))
                {
                    var basePrice = row.Doc.TryGetProperty("basePrice", out var price) && price.TryGetDecimal(out var amount) ? amount : 0m;
                    _types[(int)row.Key] = new ItemType((int)row.Key, group, category, basePrice);
                }
            }
        }
        return wanted.Where(_types.ContainsKey).ToDictionary(id => id, id => _types[id]);
    }

    public async Task<ItemType?> GetAsync(int typeId, CancellationToken cancellationToken = default) =>
        (await GetAsync([typeId], cancellationToken)).GetValueOrDefault(typeId);

    private IReadOnlyList<SdeRow>? _tradeRestrictionLists;
    private readonly SemaphoreSlim _tradeRestrictionsGate = new(1, 1);

    /// <summary>Whether the pinned SDE's direct-trade restriction lists allow this type.</summary>
    public async Task<bool> IsTradableAsync(int typeId, CancellationToken cancellationToken = default)
    {
        var type = await GetAsync(typeId, cancellationToken).ConfigureAwait(false);
        if (type is null) return false;
        var lists = _tradeRestrictionLists;
        if (lists is null)
        {
            await _tradeRestrictionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lists = _tradeRestrictionLists ??= await sde.ReadAsync("typeLists", [36L, 142L], cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _tradeRestrictionsGate.Release();
            }
        }
        if (lists.Count != 2) throw new InvalidOperationException("The SDE is missing a direct-trade restriction type list.");

        static bool Names(System.Text.Json.JsonElement list, string field, int id) => id > 0
            && list.TryGetProperty(field, out var values) && values.ValueKind == System.Text.Json.JsonValueKind.Array
            && values.EnumerateArray().Any(value => value.TryGetInt32(out var candidate) && candidate == id);

        foreach (var list in lists)
        {
            var included = Names(list.Doc, "includedTypeIDs", typeId)
                || Names(list.Doc, "includedGroupIDs", type.GroupId)
                || Names(list.Doc, "includedCategoryIDs", type.CategoryId);
            var excluded = Names(list.Doc, "excludedTypeIDs", typeId)
                || Names(list.Doc, "excludedGroupIDs", type.GroupId)
                || Names(list.Doc, "excludedCategoryIDs", type.CategoryId);
            if (included && !excluded) return false;
        }
        return true;
    }

    private async Task<Dictionary<int, int>> GroupsAsync(CancellationToken cancellationToken)
    {
        if (_categoryOfGroup is { } known)
        {
            return known;
        }
        await _groupsGate.WaitAsync(cancellationToken);
        try
        {
            return _categoryOfGroup ??= (await sde.ReadAsync("groups", cancellationToken: cancellationToken))
                .ToDictionary(row => (int)row.Key, row => row.Doc.GetProperty("categoryID").GetInt32());
        }
        finally
        {
            _groupsGate.Release();
        }
    }
}
