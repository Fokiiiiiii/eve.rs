using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

/// <summary>The jump clone service's store: the clone rows of the characters schema, and the character's skill levels from PostgreSQL.</summary>
internal sealed class HostJumpCloneStore(PgJumpCloneStore clones, NpgsqlDataSource source, PgSkillStore skills,
    WalletTransactions wallet, PgWalletStore walletStore, PgCharacterStore characters, PgItemStore items, DogmaStatic statics,
    StationCatalog stations) : IJumpCloneStore
{
    /// <summary>The wallet reference of a jump clone installation fee (EveJS <c>REF_JUMP_CLONE_INSTALLATION_FEE</c>).</summary>
    private const int JumpCloneInstallationReference = 55;

    /// <summary>The wallet reference of a jump clone activation fee (EveJS <c>REF_JUMP_CLONE_ACTIVATION_FEE</c>).</summary>
    private const int JumpCloneActivationReference = 128;

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

    public async ValueTask<CloneJumpResult> JumpAsync(long characterId, long targetCloneId, long originLocationId, decimal fee, long nowFileTime,
        CancellationToken cancellationToken)
    {
        // the zero balances are opened on their own, so that a refused jump keeps them
        await using (var setup = await source.OpenConnectionAsync(cancellationToken))
        await using (var setupTransaction = await setup.BeginTransactionAsync(cancellationToken))
        {
            await wallet.EnsureAsync(setup, setupTransaction, characterId, cancellationToken);
            await setupTransaction.CommitAsync(cancellationToken);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await characters.LockAndGetAsync(connection, transaction, characterId, cancellationToken) is not { } character)
            return new CloneJumpResult("CLONE_NOT_FOUND");
        // the pilot leaves its ship before the jump (the client does that first): only a capsule can jump
        if (character.ShipId is not { } shipId || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.TypeId != CharacterShips.CapsuleTypeId)
            return new CloneJumpResult("MUST_LEAVE_SHIP");
        // the implant changes of a pilot take the same lock (PilotImplants), so the body read here is the one that jumps
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction) { Parameters = { new() { Value = characterId } } })
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        var body = (await items.InLocationAsync(characterId, characterId, cancellationToken))
            .Where(item => item.Flag == PilotAugmentations.ImplantFlag).ToList();
        await statics.EnsureTypesAsync(body.Select(item => item.TypeId).ToArray(), cancellationToken);
        var bodyImplants = body.Select(item => (PilotAugmentations.SlotOf(statics.Type(item.TypeId)), item.TypeId)).ToList();

        var target = await clones.JumpAsync(connection, transaction, characterId, targetCloneId, originLocationId, bodyImplants, nowFileTime, cancellationToken);
        if (target is null || target.LocationKind != "station" || stations.Get(target.LocationId) is not { } station)
            return new CloneJumpResult("CLONE_NOT_FOUND");
        try
        {
            // shortcut: the fee goes to no counterparty (0), as the installation fee does; the NPC station's owner is not credited.
            await wallet.AdjustAsync(connection, transaction, characterId, -fee, 0, JumpCloneActivationReference,
                "jump clone activation", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), $"jump-clone-activation:{targetCloneId}", cancellationToken);
        }
        catch (InsufficientFundsException)
        {
            return new CloneJumpResult("INSUFFICIENT_FUNDS");
        }
        // the body that left keeps its implants in the clone at the origin; the target's implants are the body's now
        foreach (var implant in body)
            await items.DeleteAsync(connection, transaction, implant.Id, cancellationToken);
        foreach (var typeId in target.ImplantTypeIds)
            await items.CreateAsync(connection, transaction, new NewItem(typeId, characterId, characterId, PilotAugmentations.ImplantFlag, 1, Singleton: true), cancellationToken);
        await characters.SetStationAsync(connection, transaction, characterId, station.Id, cancellationToken);
        await characters.SetLocationAsync(connection, transaction, characterId, station.SolarSystemId, station.ConstellationId, station.RegionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CloneJumpResult(null, station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId);
    }

    private static JumpCloneRecord Map(InstalledJumpClone clone) =>
        new(clone.JumpCloneId, clone.LocationId, clone.LocationKind, clone.Name, clone.ImplantTypeIds);
}
