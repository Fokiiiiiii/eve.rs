using Eve.Fitting;
using Eve.Inventory;
using Eve.Services.CharacterSelect;
using Eve.Skills;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// A deleted character's assets and private state, in one PostgreSQL transaction (EveJS <c>characterDeletionRuntime.js</c>).
/// An asset tree (a root item and everything inside it) goes into the corporation's deliveries, or is destroyed when the
/// character's corporation was an NPC corporation. Its skills and saved fittings are cleared.
/// </summary>
internal sealed class HostCharacterDeletionPurge(
    NpgsqlDataSource source, PgItemStore items, PgSkillStore skills, string fittingSchema,
    Func<long, CancellationToken, Task<bool>> isNpcCorporation, CharacterPrivateStatePurge privateState) : ICharacterDeletionPurge
{
    /// <summary>The corporation deliveries hangar (EveJS <c>CORPORATION_DELIVERIES_FLAG_ID</c>).</summary>
    private const int CorporationDeliveriesFlag = 62;

    public async Task PurgeAsync(long characterId, long corporationId, CancellationToken cancellationToken)
    {
        var npc = corporationId > 0 && await isNpcCorporation(corporationId, cancellationToken);
        var owned = await items.OwnedByAsync(characterId, cancellationToken);
        var ownedIds = owned.Select(item => item.Id).ToHashSet();
        var children = owned.GroupBy(item => item.LocationId).ToDictionary(group => group.Key, group => group.ToArray());
        // a root sits outside the character's own items: in a station, a structure or a space location
        var roots = owned.Where(item => !ownedIds.Contains(item.LocationId)).OrderBy(item => item.Id).ToArray();

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var root in roots)
        {
            var tree = Tree(root, children);
            if (npc)
            {
                // contents first, so that nothing is left pointing at a removed item
                foreach (var item in Enumerable.Reverse(tree))
                    await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
                continue;
            }
            // shortcut: the root stays at its own location; EveJS falls back to the home station when that location is neither a station nor a structure.
            await items.MoveToOwnerAsync(connection, transaction, root.Id, corporationId, root.LocationId, CorporationDeliveriesFlag, cancellationToken);
            foreach (var child in tree.Skip(1))
                await items.MoveToOwnerAsync(connection, transaction, child.Id, corporationId, child.LocationId, child.Flag, cancellationToken);
        }

        await skills.ClearCharacterAsync(connection, transaction, characterId, cancellationToken);
        await using var fittings = new NpgsqlCommand(
            $"DELETE FROM {fittingSchema}.saved_fitting WHERE owner_scope = $1 AND owner_id = $2", connection, transaction)
        {
            Parameters = { new() { Value = (short)FittingOwnerScope.Character }, new() { Value = characterId } },
        };
        await fittings.ExecuteNonQueryAsync(cancellationToken);
        await privateState.PurgePostgresAsync(connection, transaction, characterId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await privateState.PurgeGameStoreAsync(characterId, cancellationToken);
    }

    /// <summary>The root and every item inside it, parents before their contents.</summary>
    private static List<Item> Tree(Item root, IReadOnlyDictionary<long, Item[]> children)
    {
        var tree = new List<Item> { root };
        for (var i = 0; i < tree.Count; i++)
        {
            if (children.TryGetValue(tree[i].Id, out var inside))
                tree.AddRange(inside);
        }
        return tree;
    }
}
