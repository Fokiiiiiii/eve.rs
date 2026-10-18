namespace Eve.Services.Space;

/// <summary>An asteroid rock in space: its item, its ore (the rock's own type, as a mineable asteroid's type is its ore) and the units it still holds.</summary>
public sealed record AsteroidRock(long ItemId, int TypeId, string Name, long Units);

public sealed partial class SpaceContainers
{
    /// <summary>The asteroid rocks in a solar system with what each holds (<c>/asteroid rewards</c>). A rock with no units left is not listed.</summary>
    public async Task<IReadOnlyList<AsteroidRock>> RocksInSystemAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        var rocks = new List<AsteroidRock>();
        foreach (var spaceItem in await _spaceItems.InSystemAsync(solarSystemId, cancellationToken))
        {
            var stored = await _items.GetAsync(spaceItem.ItemId);
            if (stored is null || stored.Quantity <= 0) continue;
            await _statics.EnsureTypesAsync([stored.TypeId]);
            if (_statics.Type(stored.TypeId) is not { } type || _statics.CategoryName(type.CategoryId) != "Asteroid") continue;
            rocks.Add(new AsteroidRock(spaceItem.ItemId, stored.TypeId, stored.CustomName ?? type.Name ?? "", stored.Quantity));
        }
        return rocks;
    }
}
