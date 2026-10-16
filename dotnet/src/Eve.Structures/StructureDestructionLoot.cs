namespace Eve.Structures;

/// <summary>
/// The loot a destroyed structure leaves (EVE.js handleStructureDestroyedLoot; the wiki's Upwell "when destroyed"): its fitted modules, fuel and cargo, and its
/// Quantum Core, in one Abandoned Container (SDE type 10167) at the structure's position. Its rigs are destroyed. The container is placed in space as an unanchor drop is.
/// </summary>
public static class StructureDestructionLoot
{
    public const int ContainerTypeId = StructureUnanchorDrop.FittingsContainerTypeId;

    /// <summary>The CustomInfo of the loot container: placement accepts only this marker for the structure.</summary>
    public static string ContainerMarker(long structureId) => $"structure-destruction-loot:{structureId}";
}
