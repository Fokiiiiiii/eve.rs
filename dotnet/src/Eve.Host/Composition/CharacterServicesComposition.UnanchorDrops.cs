using Eve.Host.Persistence.Structures;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private StructureUnanchorDropAuthority CreateStructureUnanchorDropAuthority() =>
        new(items, spaceItemStore, dogma);

    private StructureDestructionLootAuthority CreateStructureDestructionLootAuthority() =>
        new(items, spaceItemStore, dogma);
}
