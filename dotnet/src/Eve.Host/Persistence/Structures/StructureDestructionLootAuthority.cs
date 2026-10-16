using Eve.Dogma;
using Eve.Inventory;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Persistence.Structures;

/// <summary>
/// The destruction loot of a structure (ST-16; EVE.js handleStructureDestroyedLoot). In the destruction transaction: the rigs are destroyed; the fitted modules, fuel
/// and cargo (every item at the structure outside the corporation hangars, which the office and asset safety paths own) and the Quantum Core move into one
/// Abandoned Container at the structure's position. An installed core with no matching item is created in the container. Nothing to drop returns null.
/// </summary>
public sealed class StructureDestructionLootAuthority(
    PgItemStore items,
    PgSpaceItemStore spaceItems,
    DogmaStatic statics)
{
    private const int QuantumCoreTypeAttributeId = 3101;
    private const int StructureDeedFlag = 180;

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

        var rigs = new List<Item>();
        var loot = new List<Item>();
        foreach (var candidate in (await items.InContainerAsync(connection, transaction, structureId, cancellationToken)).OrderBy(item => item.Id))
        {
            var item = await items.LockAndGetAsync(connection, transaction, candidate.Id, cancellationToken);
            if (item is null || item.LocationId != structureId)
            {
                continue;
            }
            if (IsRigFlag(item.Flag))
            {
                rigs.Add(item);
            }
            else if (!ItemFlags.IsCorporationHangar(item.Flag))
            {
                loot.Add(item);
            }
        }

        foreach (var rig in rigs)
        {
            await items.DeleteAsync(connection, transaction, rig.Id, cancellationToken);
        }

        long coreTypeId = 0;
        var createCore = false;
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
            createCore = !loot.Any(item => item.Flag == StructureDeedFlag && item.TypeId == coreTypeId && item.Singleton && item.Quantity == 1);
        }

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

    private static bool IsRigFlag(int flag) => flag is >= 92 and <= 99;
}
