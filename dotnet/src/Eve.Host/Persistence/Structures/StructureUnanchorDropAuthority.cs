using Eve.Dogma;
using Eve.Inventory;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Persistence.Structures;

public sealed class StructureUnanchorDropAuthority(
    PgItemStore items,
    PgSpaceItemStore spaceItems,
    DogmaStatic statics)
{
    private const int QuantumCoreTypeAttributeId = 3101;
    private const int StructureRadiusAttributeId = 162;
    private const int StructureDeedFlag = 180;
    private const int StructureFuelBayFlag = 172;
    private const int InSpaceFlag = 0;
    private const long NeverExpires = long.MaxValue;

    public async Task<StructureUnanchorDrop> CompleteAsync(
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
            throw new InvalidOperationException($"Structure {structureId} has invalid unanchor drop data.");
        }

        await statics.EnsureTypesAsync([structure.TypeId], cancellationToken);
        var hullType = statics.Type(structure.TypeId)
            ?? throw new InvalidOperationException($"SDE type {structure.TypeId} for structure {structureId} is missing.");
        if (statics.CategoryName(hullType.CategoryId) != "Structure")
        {
            throw new InvalidOperationException($"SDE type {structure.TypeId} for structure {structureId} is not a structure.");
        }

        var contents = await items.InContainerAsync(connection, transaction, structureId, cancellationToken);
        var attached = new List<Item>();
        var rigs = new List<Item>();
        foreach (var candidate in contents.Where(item => IsAttachedFlag(item.Flag) || IsRigFlag(item.Flag)).OrderBy(item => item.Id))
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
            else if (IsAttachedFlag(item.Flag))
            {
                attached.Add(item);
            }
        }

        var coreTypeId = 0;
        TypeDef? coreType = null;
        Item[] matchingCores = [];
        if (structure.Snapshot.CoreInstalled)
        {
            if (!hullType.Attributes.TryGetValue(QuantumCoreTypeAttributeId, out var value) ||
                !double.IsFinite(value) || value <= 0 || value > int.MaxValue || value != Math.Truncate(value))
            {
                throw new InvalidOperationException(
                    $"SDE type {structure.TypeId} has no valid Quantum Core type attribute ({QuantumCoreTypeAttributeId}).");
            }
            coreTypeId = (int)value;
            await statics.EnsureTypesAsync([coreTypeId], cancellationToken);
            coreType = statics.Type(coreTypeId)
                ?? throw new InvalidOperationException($"SDE Quantum Core type {coreTypeId} is missing.");
            matchingCores = attached
                .Where(item => item.Flag == StructureDeedFlag && item.TypeId == coreTypeId && item.Singleton && item.Quantity == 1)
                .ToArray();
            if (matchingCores.Length > 1)
            {
                throw new InvalidOperationException($"Structure {structureId} has multiple installed Quantum Core items.");
            }
        }

        var createCore = structure.Snapshot.CoreInstalled && matchingCores.Length == 0;
        var needsFittingsContainer = attached.Count != 0 || createCore;
        TypeDef? fittingsType = null;
        if (needsFittingsContainer)
        {
            await statics.EnsureTypesAsync([StructureUnanchorDrop.FittingsContainerTypeId], cancellationToken);
            fittingsType = statics.Type(StructureUnanchorDrop.FittingsContainerTypeId)
                ?? throw new InvalidOperationException(
                    $"SDE type {StructureUnanchorDrop.FittingsContainerTypeId} for unanchor fittings is missing.");
        }

        var name = string.IsNullOrWhiteSpace(structure.Name) ? $"Structure {structureId}" : structure.Name;
        var position = new[] { structure.X, structure.Y, structure.Z };
        var direction = new[] { 0d, 0d, 0d };
        var hullId = await items.CreateAsync(connection, transaction,
            new NewItem(structure.TypeId, structure.OwnerCorporationId, structure.SolarSystemId, InSpaceFlag,
                Quantity: 1, Singleton: true, CustomName: name, CustomInfo: StructureUnanchorDrop.HullMarker(structureId)),
            cancellationToken);
        await spaceItems.SaveAsync(connection, transaction,
            InSpace(hullId, structure, hullType, position, direction), cancellationToken);

        long fittingsId = 0;
        if (needsFittingsContainer)
        {
            var containerType = fittingsType!;
            fittingsId = await items.CreateAsync(connection, transaction,
                new NewItem(StructureUnanchorDrop.FittingsContainerTypeId, structure.OwnerCorporationId, structure.SolarSystemId,
                    InSpaceFlag, Quantity: 1, Singleton: true, CustomName: $"{name} Decommissioned Fittings",
                    CustomInfo: StructureUnanchorDrop.FittingsMarker(structureId)),
                cancellationToken);
            await spaceItems.SaveAsync(connection, transaction,
                InSpace(fittingsId, structure, containerType, position, direction), cancellationToken);

            foreach (var item in attached)
            {
                await items.MoveAsync(connection, transaction, item.Id, fittingsId, ItemFlags.Hangar, cancellationToken);
            }
            if (createCore)
            {
                await items.CreateAsync(connection, transaction,
                    new NewItem(coreTypeId, structure.OwnerCorporationId, fittingsId, ItemFlags.Hangar,
                        Quantity: 1, Singleton: true, CustomName: coreType!.Name),
                    cancellationToken);
            }
        }

        foreach (var rig in rigs)
        {
            await items.DeleteAsync(connection, transaction, rig.Id, cancellationToken);
        }

        var remaining = await items.InContainerAsync(connection, transaction, structureId, cancellationToken);
        if (remaining.Any(item => IsAttachedFlag(item.Flag) || IsRigFlag(item.Flag)))
        {
            throw new InvalidOperationException($"Structure {structureId} still has attached assets after its unanchor handoff.");
        }

        return new StructureUnanchorDrop(hullId, fittingsId);
    }

    private static SpaceItem InSpace(
        long itemId,
        StructureRecord structure,
        TypeDef type,
        IReadOnlyList<double> position,
        IReadOnlyList<double> direction) =>
        new(itemId, structure.SolarSystemId, position, direction, RadiusOf(type), type.GroupId,
            structure.Snapshot.StructureId, 0, structure.OwnerCorporationId, structure.AllianceId, 0, NeverExpires);

    private static double RadiusOf(TypeDef type)
    {
        if (!type.Attributes.TryGetValue(StructureRadiusAttributeId, out var radius) ||
            !double.IsFinite(radius) || radius <= 0)
        {
            throw new InvalidOperationException($"SDE type {type.TypeId} has no valid space radius.");
        }
        return radius;
    }

    private static bool IsRigFlag(int flag) => flag is >= 92 and <= 99;

    private static bool IsAttachedFlag(int flag) =>
        flag is >= 11 and <= 34 or >= 125 and <= 132 or >= 158 and <= 163 or >= 164 and <= 171 or StructureFuelBayFlag or StructureDeedFlag;
}
