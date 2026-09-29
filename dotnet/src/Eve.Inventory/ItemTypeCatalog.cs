using System.Collections.Concurrent;
using Eve.Sde;

namespace Eve.Inventory;

/// <summary>An item type as the client sorts it: the group and category it belongs to.</summary>
public sealed record ItemType(int TypeId, int GroupId, int CategoryId);

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
                    _types[(int)row.Key] = new ItemType((int)row.Key, group, category);
                }
            }
        }
        return wanted.Where(_types.ContainsKey).ToDictionary(id => id, id => _types[id]);
    }

    public async Task<ItemType?> GetAsync(int typeId, CancellationToken cancellationToken = default) =>
        (await GetAsync([typeId], cancellationToken)).GetValueOrDefault(typeId);

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
