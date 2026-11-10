using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Ships;
using Eve.Universe;
using Eve.Services.Core;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

/// <summary>The jump clone service's store: the clone rows of the characters schema, and the character's skill levels from PostgreSQL.</summary>
internal sealed class HostJumpCloneStore(PgJumpCloneStore clones, NpgsqlDataSource source, PgSkillStore skills,
    WalletTransactions wallet, PgWalletStore walletStore, PgCharacterStore characters, PgItemStore items, DogmaStatic statics,
    StationCatalog stations, Eve.Structures.StructureStore structures, SolarSystemCatalog solarSystems,
    Eve.Services.Skills.SkillBoosterLapse boosters, CharacterTraining training) : IJumpCloneStore
{

    private const int JumpCloneInstallationReference = 55;


    private const int JumpCloneActivationReference = 128;


    private const int CorporationWalletKey = 1000;

    private const int FollowsJumpClonesAttributeId = 1916;

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

    public async ValueTask<JumpCloneRecord?> InstallAsync(long characterId, JumpCloneInstall install, CancellationToken cancellationToken)
    {
        // the zero balances are opened on their own, so that a refused install keeps them
        await using (var setup = await source.OpenConnectionAsync(cancellationToken))
        await using (var setupTransaction = await setup.BeginTransactionAsync(cancellationToken))
        {
            await wallet.EnsureAsync(setup, setupTransaction, characterId, cancellationToken);
            if (install.OwnerCorporationId > 0)
                await wallet.EnsureAsync(setup, setupTransaction, install.OwnerCorporationId, cancellationToken);
            await setupTransaction.CommitAsync(cancellationToken);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await characters.LockAndGetAsync(connection, transaction, characterId, cancellationToken) is null)
            throw new JumpCloneInstallRefusedException("CHARACTER_NOT_FOUND");

        // Every install by this character takes the character row lock before reading limits or inserting. Ship installs also
        // serialize on the physical bay so different recipients cannot consume the same last slot concurrently.
        if (install.LocationKind == "ship")
            await LockShipCloneBayAsync(connection, transaction, install.LocationId, cancellationToken);

        var cloneLimit = 0;
        foreach (var skillId in CloneJumpRules.CloneLimitSkillIds)
            cloneLimit += await skills.LevelAsync(connection, transaction, characterId, skillId, cancellationToken);
        if (cloneLimit <= 0)
            throw new JumpCloneInstallRefusedException("SKILL_REQUIRED");

        if (await clones.CountForCharacterAsync(connection, transaction, characterId, cancellationToken) >= cloneLimit)
            throw new JumpCloneInstallRefusedException("CLONE_LIMIT");

        if (install.LocationKind is "station" or "structure" &&
            await clones.CountAtForCharacterAsync(connection, transaction, characterId, install.LocationId, install.LocationKind, cancellationToken) > 0)
            throw new JumpCloneInstallRefusedException("LOCATION_OCCUPIED");

        if (install.LocationKind == "ship" &&
            await clones.CountAtAsync(connection, transaction, install.LocationId, install.LocationKind, cancellationToken) >= install.ShipCapacity)
            throw new JumpCloneInstallRefusedException("SHIP_CAPACITY");

        var cloneId = await clones.InsertAsync(connection, transaction, characterId, install.LocationId, install.LocationKind, cancellationToken);
        var timeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var operationKey = $"jump-clone-install:{cloneId}";
        try
        {
            if (install.Fee > 0 && install.OwnerCorporationId > 0)
            {

                await wallet.TransferAsync(connection, transaction, new WalletAccount(characterId), new WalletAccount(install.OwnerCorporationId, CorporationWalletKey),
                    install.Fee, JumpCloneInstallationReference, "jump clone installation", timeMs, operationKey, operationKey, cancellationToken, install.LocationId);
            }
            else if (install.Fee > 0)
            {

                await wallet.AdjustAsync(connection, transaction, characterId, -install.Fee, install.OfferingCharacterId, JumpCloneInstallationReference,
                    "jump clone installation", timeMs, operationKey, cancellationToken);
            }
        }
        catch (InsufficientFundsException)
        {
            return null;
        }
        await clones.InsertSettlementAsync(connection, transaction, operationKey, characterId, cloneId, new JumpCloneInstallRecord(
            install.LocationId, install.LocationKind, install.Fee, install.OwnerCorporationId, install.OfferingCharacterId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new JumpCloneRecord(cloneId, install.LocationId, install.LocationKind, "", []);
    }

    private static async Task LockShipCloneBayAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long shipId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('jump-clone-ship:' || $1::text, 0))", connection, transaction);
        command.Parameters.AddWithValue(shipId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<decimal> IskBalanceAsync(long characterId, CancellationToken cancellationToken) =>
        (await walletStore.GetAsync(characterId, cancellationToken))?.Isk ?? 0m;

    public async ValueTask<CloneJumpResult> JumpAsync(long characterId, long targetCloneId, long originLocationId, string originLocationKind,
        decimal fee, long ownerCorporationId, int cooldownHours, long nowFileTime,
        CancellationToken cancellationToken)
    {
        // A changed implant set changes learning attributes. Rebase the active skill at the old
        // rate before the transaction swaps the body, following PilotImplants' ordering.
        var learningUpdate = await RebaseForJumpAsync(characterId, targetCloneId, fee, cooldownHours, nowFileTime, cancellationToken);
        // the zero balances are opened on their own, so that a refused jump keeps them
        await using (var setup = await source.OpenConnectionAsync(cancellationToken))
        await using (var setupTransaction = await setup.BeginTransactionAsync(cancellationToken))
        {
            await wallet.EnsureAsync(setup, setupTransaction, characterId, cancellationToken);
            if (fee > 0 && ownerCorporationId > 0)
                await wallet.EnsureAsync(setup, setupTransaction, ownerCorporationId, cancellationToken);
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
        var followingImplants = body.Where(FollowsJumpClones).ToList();
        var bodyImplants = body.Where(item => !FollowsJumpClones(item))
            .Select(item => (PilotAugmentations.SlotOf(statics.Type(item.TypeId)), item.TypeId)).ToList();

        // A ship clone can be activated only while its physical ship is still in the destination system. Keep the item row locked
        // until settlement commits, so a concurrent ship relocation cannot make the client arrive at a stale destination.
        var requestedTarget = await clones.FindAsync(connection, transaction, characterId, targetCloneId, cancellationToken);
        if (requestedTarget?.LocationKind == "ship")
        {
            var targetShip = await items.LockAndGetAsync(connection, transaction, requestedTarget.LocationId, cancellationToken);
            if (targetShip is not null)
                await statics.EnsureTypesAsync([targetShip.TypeId], cancellationToken);
            if (targetShip is null || targetShip.Flag != CharacterShips.InSpaceFlag || targetShip.LocationId != character.SolarSystemId ||
                statics.Type(targetShip.TypeId)?.CategoryId != 6)
                return new CloneJumpResult("CLONE_NOT_FOUND");
        }

        var jump = await clones.JumpAsync(connection, transaction, characterId, targetCloneId, originLocationId, originLocationKind,
            bodyImplants, cooldownHours, nowFileTime, cancellationToken);
        if (jump.CooldownActive)
            return new CloneJumpResult("COOLDOWN_ACTIVE");
        var target = jump.Target;
        if (target is null)
            return new CloneJumpResult("CLONE_NOT_FOUND");
        var station = target.LocationKind == "station" ? stations.Get(target.LocationId) : null;
        Eve.Structures.StructureRecord? structure = null;
        if (target.LocationKind == "structure")
        {
            structure = await structures.GetAsync(connection, transaction, target.LocationId, false, cancellationToken);
            if (structure is null || structure.Snapshot.DestroyedMs > 0 || solarSystems.Get(structure.SolarSystemId) is null)
                return new CloneJumpResult("CLONE_NOT_FOUND");
        }
        if (station is null && structure is null && target.LocationKind != "ship")
            return new CloneJumpResult("CLONE_NOT_FOUND");
        try
        {
            if (fee > 0)
            {
                var operationKey = $"jump-clone-activation:{characterId}:{nowFileTime}";
                if (ownerCorporationId > 0)
                    await wallet.TransferAsync(connection, transaction, new WalletAccount(characterId), new WalletAccount(ownerCorporationId, CorporationWalletKey),
                        fee, JumpCloneActivationReference, "jump clone activation", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), operationKey,
                        "jump-clone-activation", cancellationToken, target.LocationId);
                else
                    await wallet.AdjustAsync(connection, transaction, characterId, -fee, 0, JumpCloneActivationReference,
                        "jump clone activation", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), operationKey, cancellationToken);
            }
        }
        catch (InsufficientFundsException)
        {
            return new CloneJumpResult("INSUFFICIENT_FUNDS");
        }
        // Only body implants without followsJumpClones are stored in the origin clone. Following
        // implants remain the same inventory items and override a destination implant in their slot.
        foreach (var implant in body.Except(followingImplants))
            await items.DeleteAsync(connection, transaction, implant.Id, cancellationToken);
        var implantsForNewBody = MergeImplantsBySlot(target.ImplantTypeIds, followingImplants);
        var preservedFollowers = followingImplants.ToDictionary(
            implant => PilotAugmentations.SlotOf(statics.Type(implant.TypeId)), implant => implant.TypeId);
        foreach (var (slot, typeId) in implantsForNewBody)
        {
            if (preservedFollowers.TryGetValue(slot, out var existingTypeId) && existingTypeId == typeId)
                continue;
            await items.CreateAsync(connection, transaction, new NewItem(typeId, characterId, characterId, PilotAugmentations.ImplantFlag, 1, Singleton: true), cancellationToken);
        }
        var solarSystem = station is not null ? solarSystems.Get(station.SolarSystemId)
            : structure is not null ? solarSystems.Get(structure.SolarSystemId)
            : solarSystems.Get(character.SolarSystemId);
        if (solarSystem is null)
            return new CloneJumpResult("CLONE_NOT_FOUND");
        if (station is not null)
            await characters.SetStationAsync(connection, transaction, characterId, station.Id, cancellationToken);
        else if (structure is not null)
            await characters.SetStructureAsync(connection, transaction, characterId, structure!.Snapshot.StructureId, cancellationToken);
        else
            await characters.SetStructureAsync(connection, transaction, characterId, null, cancellationToken);
        await characters.SetLocationAsync(connection, transaction, characterId, solarSystem!.Id, solarSystem.ConstellationId, solarSystem.RegionId, cancellationToken);

        var endedBoosters = await boosters.EndNormalBoostersAsync(connection, transaction, characterId, nowFileTime, statics, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (endedBoosters)
            await boosters.AnnounceEndedAsync(characterId, cancellationToken);
        return station is not null
            ? new CloneJumpResult(null, station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, LearningUpdate: learningUpdate)
            : structure is not null
                ? new CloneJumpResult(null, SolarSystemId: solarSystem.Id, ConstellationId: solarSystem.ConstellationId,
                    RegionId: solarSystem.RegionId, StructureId: structure.Snapshot.StructureId, LearningUpdate: learningUpdate)
                : new CloneJumpResult(null, SolarSystemId: solarSystem.Id, ConstellationId: solarSystem.ConstellationId,
                    RegionId: solarSystem.RegionId, LearningUpdate: learningUpdate);
    }

    private async Task<TrainingUpdate?> RebaseForJumpAsync(long characterId, long targetCloneId, decimal fee,
        int cooldownHours, long nowFileTime, CancellationToken cancellationToken)
    {
        var character = await characters.GetAsync(characterId, cancellationToken);
        if (character?.ShipId is not { } currentShipId ||
            await items.GetAsync(currentShipId, cancellationToken) is not { TypeId: CharacterShips.CapsuleTypeId } ||
            fee > 0 && await IskBalanceAsync(characterId, cancellationToken) < fee)
            return null;

        var snapshot = await clones.ReadAsync(characterId, cancellationToken);
        var target = snapshot.Clones.FirstOrDefault(clone => clone.JumpCloneId == targetCloneId);
        if (target is null || target.LocationKind is not ("station" or "structure" or "ship") ||
            CloneJumpRules.CooldownActive(snapshot.TimeLastJumpFileTime, cooldownHours, nowFileTime) ||
            target.LocationKind == "station" && stations.Get(target.LocationId) is null)
            return null;

        if (target.LocationKind == "ship" &&
            (await items.GetAsync(target.LocationId, cancellationToken) is not { } targetShip ||
             targetShip.Flag != CharacterShips.InSpaceFlag || targetShip.LocationId != character.SolarSystemId))
            return null;

        var body = (await items.InLocationAsync(characterId, characterId, cancellationToken))
            .Where(item => item.Flag == PilotAugmentations.ImplantFlag).ToArray();
        await statics.EnsureTypesAsync([.. body.Select(item => item.TypeId), .. target.ImplantTypeIds], cancellationToken);
        var followers = body.Where(FollowsJumpClones).ToArray();
        var currentTypes = body.Select(item => (PilotAugmentations.SlotOf(statics.Type(item.TypeId)), item.TypeId))
            .OrderBy(implant => implant.Item1).ThenBy(implant => implant.Item2).ToArray();
        var nextTypes = MergeImplantsBySlot(target.ImplantTypeIds, followers)
            .OrderBy(implant => implant.Slot).ThenBy(implant => implant.TypeId).ToArray();
        if (currentTypes.SequenceEqual(nextTypes))
            return null;

        return await training.RebaseRunningAsync(characterId, character.AccountId, cancellationToken);
    }

    private bool FollowsJumpClones(Item item) =>
        statics.Type(item.TypeId)?.Attributes.GetValueOrDefault(FollowsJumpClonesAttributeId, 0) > 0;

    private List<(int Slot, int TypeId)> MergeImplantsBySlot(IReadOnlyList<int> targetImplantTypeIds,
        IReadOnlyList<Item> followingImplants)
    {
        var bySlot = new SortedDictionary<int, int>();
        foreach (var typeId in targetImplantTypeIds)
            bySlot[PilotAugmentations.SlotOf(statics.Type(typeId))] = typeId;
        foreach (var implant in followingImplants)
            bySlot[PilotAugmentations.SlotOf(statics.Type(implant.TypeId))] = implant.TypeId;
        return [.. bySlot.Select(pair => (pair.Key, pair.Value))];
    }

    private static JumpCloneRecord Map(InstalledJumpClone clone) =>
        new(clone.JumpCloneId, clone.LocationId, clone.LocationKind, clone.Name, clone.ImplantTypeIds);
}
