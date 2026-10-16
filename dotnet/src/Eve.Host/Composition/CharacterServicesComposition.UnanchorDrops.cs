using Eve.Host.Persistence.Structures;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private StructureUnanchorDropAuthority CreateStructureUnanchorDropAuthority() =>
        new(items, spaceItemStore, dogma);

    // the read model is created later in Structures(); the wrap reads the field when the destruction runs
    private StructureDestructionLootAuthority CreateStructureDestructionLootAuthority() =>
        new(items, spaceItemStore, dogma, StructureDestructionLootAuthority.WrapWith(() => structureAssetSafetyReadModel));
}
