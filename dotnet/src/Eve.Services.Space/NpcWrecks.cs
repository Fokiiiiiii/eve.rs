using Eve.Inventory;
using Eve.Npc;
using Eve.Ships;

namespace Eve.Services.Space;

/// <summary>
/// The wreck an NPC leaves: an item in space that holds what the NPC's loot table gave this time (the reference rolls the table when the NPC dies, <c>destroyNativeNpcEntityWithWreck</c>).
/// The simulation tells the host that the ship is gone and what kind of wreck it leaves; the host makes the item, with its contents, and puts it in space like a player's.
/// </summary>
public sealed class NpcWrecks
{
    private readonly Func<string, LootTable?> _tableOf;
    private readonly Func<Task<NpcLoot>> _load;
    private readonly object _gate = new();
    private Task<NpcLoot>? _loading;
    private readonly CharacterShips _ships;
    private readonly Func<double> _random;

    /// <param name="tableOf">The loot table of a profile's loot table id.</param>
    /// <param name="loot">What a table is rolled against (the types of the static data); read when the first NPC dies.</param>
    public NpcWrecks(Func<string, LootTable?> tableOf, Func<Task<NpcLoot>> loot, CharacterShips ships, Func<double>? random = null)
    {
        (_tableOf, _ships) = (tableOf, ships);
        _load = loot;
        _random = random ?? Random.Shared.NextDouble;
    }

    public NpcWrecks(NpcAuthority authority, Func<Task<NpcLoot>> loot, CharacterShips ships, Func<double>? random = null)
        : this(authority.LootTable, loot, ships, random)
    {
    }

    /// <summary>Starts reading the types the loot is drawn from, so the first kill does not wait for it.</summary>
    public void Warm() => _ = LootAsync();

    /// <summary>The types are read once; a read that failed (the database was away) is made again by the next kill, not remembered.</summary>
    private Task<NpcLoot> LootAsync()
    {
        lock (_gate)
        {
            if (_loading is null || _loading.IsFaulted || _loading.IsCanceled)
            {
                _loading = _load();
            }
            return _loading;
        }
    }

    /// <summary>Makes the wreck of type <paramref name="wreckTypeId"/> in the solar system, held by <paramref name="ownerId"/>, with what <paramref name="lootTableId"/> gives.</summary>
    public async Task<MadeWreck> LeaveAsync(int wreckTypeId, string lootTableId, long ownerId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        var drops = (await LootAsync()).Roll(_tableOf(lootTableId), _random);
        return await _ships.LeaveWreckAsync(wreckTypeId, ownerId, solarSystemId, [.. drops.Select(d => new WreckContent(d.TypeId, d.Quantity, d.Singleton))], cancellationToken);
    }
}
