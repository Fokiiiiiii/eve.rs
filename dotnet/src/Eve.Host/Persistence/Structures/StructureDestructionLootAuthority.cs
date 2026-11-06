using Eve.Dogma;
using Eve.Inventory;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Persistence.Structures;

/// <summary>


/// the wrap did not take (a FLEX hull, a wormhole or Pochven system, a refused wrap), and the Quantum Core move into one Abandoned Container at the structure's position.

/// </summary>
public sealed class StructureDestructionLootAuthority(
    PgItemStore items,
    PgSpaceItemStore spaceItems,
    DogmaStatic statics,
    Func<NpgsqlConnection, NpgsqlTransaction, long, long, long, bool, CancellationToken, Task<Eve.Services.Core.AssetSafetyActionState>> wrapOwner)
{
    /// <summary>
    /// The asset safety wrap of a destruction over a read model that the composition creates later than the timers: the model is read when the destruction runs

    /// </summary>
    public static Func<NpgsqlConnection, NpgsqlTransaction, long, long, long, bool, CancellationToken, Task<Eve.Services.Core.AssetSafetyActionState>> WrapWith(
        Func<PersistenceStructureAssetSafetyReadModel> readModel) =>
        (connection, transaction, structureId, solarSystemId, ownerId, corporation, token) =>
            readModel().WrapDestroyedOwnerAsync(connection, transaction, structureId, solarSystemId, ownerId, corporation, token);

    private const int QuantumCoreTypeAttributeId = 3101;
    private const int StructureDeedFlag = 180;
    private const long NeverExpires = long.MaxValue;


    private const long CharacterOwnerMin = 140_000_000;
    private const long CharacterOwnerMaxExclusive = 200_000_000;

    public async Task<StructureUnanchorDrop?> CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StructureRecord structure,
        CancellationToken cancellationToken = default)
    {
        var structureId = structure.Snapshot.StructureId;
        if (structureId <= 0 || structure.TypeId <= 0 || structure.SolarSystemId <= 0 ||
            structure.OwnerCorporationId <= 0 ||
            !double.IsFinite(structure.X) || !double.IsFinite(structure.Y) || !double.IsFinite(structure.Z))
        {
            throw new InvalidOperationException($"Structure {structureId} has invalid destruction loot data.");
        }

        long coreTypeId = 0;
        if (structure.Snapshot.CoreInstalled)
        {
            var hullType = statics.Type(structure.TypeId)
                ?? throw new InvalidOperationException($"SDE type {structure.TypeId} for structure {structureId} is missing.");
            if (!hullType.Attributes.TryGetValue(QuantumCoreTypeAttributeId, out var value) ||
                !double.IsFinite(value) || value <= 0 || value > int.MaxValue || value != Math.Truncate(value))
            {
                throw new InvalidOperationException(
                    $"SDE type {structure.TypeId} has no valid Quantum Core type attribute ({QuantumCoreTypeAttributeId}).");
            }
            coreTypeId = (long)value;
        }


        foreach (var rig in await LockedAtStructureAsync(connection, transaction, structureId, IsRigFlag, cancellationToken))
        {
            await items.DeleteAsync(connection, transaction, rig.Id, cancellationToken);
        }

        var contents = await items.InContainerAsync(connection, transaction, structureId, cancellationToken);
        var hadCore = coreTypeId > 0 && contents.Any(item =>
            item.Flag == StructureDeedFlag && item.TypeId == coreTypeId && item.Singleton && item.Quantity == 1);

        foreach (var owner in contents.Select(item => item.OwnerId).Where(owner => owner > 0).Distinct().OrderBy(owner => owner))
        {
            var corporation = owner < CharacterOwnerMin || owner >= CharacterOwnerMaxExclusive;
            // the wrap's state does not decide the loot: whatever it left at the structure is looted below
            await wrapOwner(connection, transaction, structureId, structure.SolarSystemId, owner, corporation, cancellationToken);
        }

        var loot = await LockedAtStructureAsync(connection, transaction, structureId, _ => true, cancellationToken);
        var createCore = coreTypeId > 0 && !hadCore;
        if (loot.Count == 0 && !createCore)
        {
            return null;
        }

        await statics.EnsureTypesAsync([StructureDestructionLoot.ContainerTypeId], cancellationToken);
        var containerType = statics.Type(StructureDestructionLoot.ContainerTypeId)
            ?? throw new InvalidOperationException($"SDE type {StructureDestructionLoot.ContainerTypeId} for destruction loot is missing.");
        var name = string.IsNullOrWhiteSpace(structure.Name) ? $"Structure {structureId}" : structure.Name;
        var position = new[] { structure.X, structure.Y, structure.Z };
        var direction = new[] { 0d, 0d, 0d };
        var containerId = await items.CreateAsync(connection, transaction,
            new NewItem(StructureDestructionLoot.ContainerTypeId, structure.OwnerCorporationId, structure.SolarSystemId, 0,
                Quantity: 1, Singleton: true, CustomName: $"{name} Destruction Loot",
                CustomInfo: StructureDestructionLoot.ContainerMarker(structureId)),
            cancellationToken);
        await spaceItems.SaveAsync(connection, transaction,
            StructureUnanchorDropAuthority.InSpace(containerId, structure, containerType, position, direction), cancellationToken);

        foreach (var item in loot)
        {
            await items.MoveAsync(connection, transaction, item.Id, containerId, ItemFlags.Hangar, cancellationToken);
        }
        if (createCore)
        {
            await items.CreateAsync(connection, transaction,
                new NewItem((int)coreTypeId, structure.OwnerCorporationId, containerId, ItemFlags.Hangar,
                    Quantity: 1, Singleton: true, CustomName: statics.Type((int)coreTypeId)?.Name ?? $"Type {coreTypeId}"),
                cancellationToken);
        }
        return new StructureUnanchorDrop(0, containerId);
    }

    /// <summary>The items still at the structure that match, each locked and checked to be there.</summary>
    private async Task<List<Item>> LockedAtStructureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId,
        Func<int, bool> match, CancellationToken cancellationToken)
    {
        var found = new List<Item>();
        foreach (var candidate in (await items.InContainerAsync(connection, transaction, structureId, cancellationToken)).OrderBy(item => item.Id))
        {
            var item = await items.LockAndGetAsync(connection, transaction, candidate.Id, cancellationToken);
            if (item is not null && item.LocationId == structureId && match(item.Flag))
            {
                found.Add(item);
            }
        }
        return found;
    }

    private static bool IsRigFlag(int flag) => flag is >= 92 and <= 99;
}
