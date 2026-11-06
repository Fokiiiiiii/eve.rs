using Eve.Characters;
using Eve.Relations;
using Npgsql;

namespace Eve.Host;

internal sealed class FwCharacterAffiliationWriter(
    NpgsqlDataSource source,
    PgCharacterStore characters,
    RelationStore relations)
{
    public async Task<bool> SaveAsync(
        long characterId,
        long? factionId,
        long cooldownFileTime,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await characters.SetFactionWarfareStateAsync(connection, transaction, characterId, factionId, cooldownFileTime, cancellationToken))
            return false;
        await SetAffiliationAsync(connection, transaction, characterId, factionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReconcileAsync(long characterId, long? factionId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetAffiliationAsync(connection, transaction, characterId, factionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetAffiliationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long? factionId,
        CancellationToken cancellationToken)
    {
        var existing = await relations.FromAsync(RelationKinds.FactionMember.Name, characterId, connection, transaction, cancellationToken);
        if (factionId is > 0)
        {
            if (existing.Count == 1 && existing[0].Object == factionId.Value)
                return;
            await relations.SetAsync(connection, transaction, RelationKinds.FactionMember.Name,
                characterId, factionId.Value, 1, "faction warfare enlistment", cancellationToken: cancellationToken);
            return;
        }

        foreach (var relation in existing)
            await relations.EndAsync(connection, transaction, RelationKinds.FactionMember.Name, characterId, relation.Object,
                "faction warfare enlistment removed", cancellationToken);
    }
}
