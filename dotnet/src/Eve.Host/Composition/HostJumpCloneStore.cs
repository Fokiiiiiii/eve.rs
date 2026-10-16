using Eve.Characters;
using Eve.Services.Core;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

/// <summary>The jump clone service's store: the clone rows of the characters schema, and the character's skill levels from PostgreSQL.</summary>
internal sealed class HostJumpCloneStore(PgJumpCloneStore clones, NpgsqlDataSource source, PgSkillStore skills,
    WalletTransactions wallet, PgWalletStore walletStore) : IJumpCloneStore
{
    /// <summary>The wallet reference of a jump clone installation fee (EveJS <c>REF_JUMP_CLONE_INSTALLATION_FEE</c>).</summary>
    private const int JumpCloneInstallationReference = 55;

    public async ValueTask<JumpCloneSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken)
    {
        var snapshot = await clones.ReadAsync(characterId, cancellationToken);
        return new JumpCloneSnapshot([.. snapshot.Clones.Select(Map)], snapshot.TimeLastJumpFileTime);
    }

    public async ValueTask<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken) =>
        await clones.RenameAsync(characterId, jumpCloneId, name, cancellationToken);

    public async ValueTask<JumpCloneRecord?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken) =>
        await clones.DestroyAsync(characterId, jumpCloneId, cancellationToken) is { } removed ? Map(removed) : null;

    public async ValueTask ResetLastJumpAsync(long characterId, CancellationToken cancellationToken) =>
        await clones.ResetLastJumpAsync(characterId, cancellationToken);

    public async ValueTask<int> SkillLevelAsync(long characterId, int typeId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return await skills.LevelAsync(connection, transaction, characterId, typeId, cancellationToken);
    }

    public async ValueTask<JumpCloneRecord?> InstallAsync(long characterId, long locationId, string locationKind, decimal fee, CancellationToken cancellationToken)
    {
        // the zero balances are opened on their own, so that a refused install keeps them
        await using (var setup = await source.OpenConnectionAsync(cancellationToken))
        await using (var setupTransaction = await setup.BeginTransactionAsync(cancellationToken))
        {
            await wallet.EnsureAsync(setup, setupTransaction, characterId, cancellationToken);
            await setupTransaction.CommitAsync(cancellationToken);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var cloneId = await clones.InsertAsync(connection, transaction, characterId, locationId, locationKind, cancellationToken);
        try
        {
            // shortcut: the fee goes to no counterparty (0); the NPC station's owner is not credited.
            await wallet.AdjustAsync(connection, transaction, characterId, -fee, 0, JumpCloneInstallationReference,
                "jump clone installation", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), $"jump-clone-install:{cloneId}", cancellationToken);
        }
        catch (InsufficientFundsException)
        {
            return null;
        }
        await transaction.CommitAsync(cancellationToken);
        return new JumpCloneRecord(cloneId, locationId, locationKind, "", []);
    }

    public async ValueTask<decimal> IskBalanceAsync(long characterId, CancellationToken cancellationToken) =>
        (await walletStore.GetAsync(characterId, cancellationToken))?.Isk ?? 0m;

    private static JumpCloneRecord Map(InstalledJumpClone clone) =>
        new(clone.JumpCloneId, clone.LocationId, clone.LocationKind, clone.Name, clone.ImplantTypeIds);
}
