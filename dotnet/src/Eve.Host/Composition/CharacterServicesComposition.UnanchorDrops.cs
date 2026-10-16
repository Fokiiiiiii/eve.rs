using Eve.Host.Persistence.Structures;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private StructureUnanchorDropAuthority CreateStructureUnanchorDropAuthority() =>
        new(items, spaceItemStore, dogma);

    // the read model is created later in Structures(); the lambda reads the field when the destruction runs
    private StructureDestructionLootAuthority CreateStructureDestructionLootAuthority() =>
        new(items, spaceItemStore, dogma,
            (connection, transaction, structureId, solarSystemId, ownerId, corporation, token) =>
                structureAssetSafetyReadModel.WrapDestroyedOwnerAsync(connection, transaction, structureId, solarSystemId, ownerId, corporation, token));
}
