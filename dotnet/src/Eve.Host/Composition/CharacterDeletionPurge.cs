using Eve.Characters;
using Eve.Corporations;
using Eve.Events;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Services.CharacterSelect;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

internal sealed record CharacterBiomassed(long CharacterId, long CorporationId, long[] RecipientIds);

/// <summary>Durable notice that the corporation member listed in the event was permanently deleted.</summary>
internal static class CharacterBiomassEvents
{
    public const string Topic = "character.biomassed";
}

/// <summary>
/// Destroys a character's assets for NPC corporations and corporations without an office. Otherwise transfers the asset
/// trees to the corporation's first office and first hangar division. Skills, ISK and the durable member-mail event share
/// the deletion transaction.
/// </summary>
internal sealed class HostCharacterDeletionPurge(
    NpgsqlDataSource source,
    PgItemStore items,
    PgSkillStore skills,
    PgCharacterStore characters,
    string fittingSchema,
    Func<long, CancellationToken, Task<bool>> isNpcCorporation,
    CharacterPrivateStatePurge privateState,
    CorporationOfficeStore offices,
    WalletTransactions wallets,
    EventBus events,
    Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<IReadOnlyList<long>>> activeMemberCharacterIds) : ICharacterDeletionPurge
{
    // CCP's wallet journal mapping names ref type 9 "inheritance".
    private const int InheritanceRefTypeId = 9;

    public async Task PurgeAsync(long characterId, long corporationId, CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0)
            throw new ArgumentOutOfRangeException(characterId <= 0 ? nameof(characterId) : nameof(corporationId));

        var npc = await isNpcCorporation(corporationId, cancellationToken);
        var playerCorporation = !npc;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var characterLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction))
        {
            characterLock.Parameters.Add(new NpgsqlParameter { Value = $"character-biomass:{characterId}" });
            await characterLock.ExecuteNonQueryAsync(cancellationToken);
        }

        var owned = await items.LockOwnedAsync(connection, transaction, characterId, cancellationToken);
        var ownedIds = owned.Select(item => item.Id).ToHashSet();
        var children = owned.GroupBy(item => item.LocationId).ToDictionary(group => group.Key, group => group.ToArray());
        // Roots can be in stations, structures, or space locations, outside the character inventory.
        var roots = owned.Where(item => !ownedIds.Contains(item.LocationId)).OrderBy(item => item.Id).ToArray();

        var firstOffice = playerCorporation
            ? (await offices.ForCorporationAsync(connection, transaction, corporationId, cancellationToken))
                .OrderBy(office => office.StationId)
                .FirstOrDefault()
            : null;

        decimal iskBalance = 0;
        decimal corporationBalance = 0;
        await wallets.EnsureAsync(connection, transaction, characterId, cancellationToken);
        if (playerCorporation)
        {
            await wallets.EnsureAsync(connection, transaction, corporationId, cancellationToken);
            var characterWallet = new WalletAccount(characterId);
            var corporationWallet = new WalletAccount(corporationId);
            var balances = await wallets.LockBalancesAsync(
                connection, transaction, [characterWallet, corporationWallet], cancellationToken);
            iskBalance = balances[characterWallet];
            corporationBalance = balances[corporationWallet];
            if (iskBalance < 0 || corporationBalance < 0)
                throw new InvalidOperationException("Biomass cannot settle a wallet with a negative balance.");
            if (iskBalance > 0)
            {
                await wallets.TransferAsync(
                    connection,
                    transaction,
                    characterWallet,
                    corporationWallet,
                    iskBalance,
                    InheritanceRefTypeId,
                    string.Empty,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    $"character-biomass:{characterId}:isk",
                    $"character-biomass:{characterId}",
                    cancellationToken);
                corporationBalance += iskBalance;
            }
        }
        else
        {
            iskBalance = await wallets.DestroyIskAsync(connection, transaction, characterId, cancellationToken);
        }

        foreach (var root in roots)
        {
            var tree = Tree(root, children);
            if (npc || firstOffice is null)
            {
                // Contents first, so that nothing is left pointing at a removed item.
                foreach (var item in Enumerable.Reverse(tree))
                    await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
                continue;
            }

            await items.MoveToOwnerAsync(
                connection, transaction, root.Id, corporationId, firstOffice.OfficeFolderId, ItemFlags.CorpSAG1, cancellationToken);
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
        await characters.ClearAppearanceAsync(connection, transaction, characterId, cancellationToken);

        var publishedMail = false;
        if (playerCorporation)
        {
            var recipientIds = (await activeMemberCharacterIds(connection, transaction, corporationId, cancellationToken))
                .Where(id => id > 0 && id != characterId)
                .Distinct()
                .Order()
                .ToArray();
            if (recipientIds.Length > 0)
            {
                await events.PublishOnceInTransactionAsync(
                    connection,
                    transaction,
                    CharacterBiomassEvents.Topic,
                    new CharacterBiomassed(characterId, corporationId, recipientIds),
                    $"character-biomass:{characterId}",
                    cancellationToken);
                publishedMail = true;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        if (iskBalance > 0 && playerCorporation)
        {
            wallets.AnnounceCommitted(new WalletBalanceChange(characterId, 1000, 0));
            wallets.AnnounceCommitted(new WalletBalanceChange(corporationId, 1000, corporationBalance));
        }
        if (publishedMail)
            events.Wake();
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
