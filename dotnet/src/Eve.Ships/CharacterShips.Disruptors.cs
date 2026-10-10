using Eve.Dogma;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Eve.Skills;
using Npgsql;

namespace Eve.Ships;

/// <summary>
/// The mobile warp disruptors the pilot drops from the hold (EVE.js <c>mobileWarpDisruptorRuntime</c>): the custody of the item, which is the host's: it leaves the hold
/// for the solar system (the item is in space as a singleton, as a drone is) and is deleted when its time is up.
/// </summary>
public sealed partial class CharacterShips
{
    /// <summary>The skill a disruptor needs, its level (EVE.js <c>validateRequiredSkills</c>: the requiredSkill1/2 attributes 182 and 183 with the levels 277 and 278).</summary>
    public async Task<(int SkillTypeId, int Level)?> MissingSkillAsync(long characterId, IReadOnlyDictionary<int, double> attributes, CancellationToken cancellationToken = default)
    {
        var known = await skills.ForCharacterAsync(characterId, cancellationToken);
        foreach (var (skillAttribute, levelAttribute) in new[] { (182, 277), (183, 278) })
        {
            if (!attributes.TryGetValue(skillAttribute, out var skill) || skill <= 0
                || !attributes.TryGetValue(levelAttribute, out var level) || level <= 0)
            {
                continue;
            }
            var trained = known.FirstOrDefault(s => s.TypeId == (int)skill)?.Level ?? 0;
            if (trained < level)
            {
                return ((int)skill, (int)level);
            }
        }
        return null;
    }

    /// <summary>
    /// The disruptor leaves the hold for the solar system, as a singleton of its own (a stack gives one unit and keeps the rest). Answers the item in space and what the
    /// client is told changed, in order; null when the item is no longer in the pilot's hold.
    /// </summary>
    public async Task<(Item Disruptor, IReadOnlyList<ItemChanged> Changes)?> DropDisruptorAsync(long characterId, long shipId, long itemId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } item
            || item.OwnerId != characterId || item.LocationId != shipId || item.Flag != CargoFlag)
        {
            return null;
        }
        (Item Disruptor, IReadOnlyList<ItemChanged> Changes) dropped;
        if (!item.Singleton && item.Quantity > 1)
        {
            var created = await items.MoveStackAsync(connection, transaction, item, 1, characterId, solarSystemId, InSpaceFlag, cancellationToken);
            await items.MakeSingletonAsync(connection, transaction, created.Id, cancellationToken);
            var disruptor = created with { Singleton = true };
            dropped = (disruptor, [new ItemChanged(item with { Quantity = item.Quantity - 1 }, shipId, CargoFlag, item.Quantity), new ItemChanged(disruptor, 0, InSpaceFlag, 0)]);
        }
        else
        {
            await items.MoveToOwnerAsync(connection, transaction, item.Id, characterId, solarSystemId, InSpaceFlag, cancellationToken);
            await items.MakeSingletonAsync(connection, transaction, item.Id, cancellationToken);
            var disruptor = item with { LocationId = solarSystemId, Flag = InSpaceFlag, Singleton = true, Quantity = 1 };
            dropped = (disruptor, [new ItemChanged(disruptor, shipId, CargoFlag, item.Quantity)]);
        }
        await transaction.CommitAsync(cancellationToken);
        return dropped;
    }

    /// <summary>A disruptor whose time is up leaves with its item, which is deleted (EVE.js <c>expireInSpace</c>). A disruptor that is no longer there is left alone.</summary>
    public async Task DeleteDisruptorAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await items.DeleteAsync(connection, transaction, itemId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
