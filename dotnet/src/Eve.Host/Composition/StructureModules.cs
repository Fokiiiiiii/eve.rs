using Eve.Services.Space;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private Func<long, long, CancellationToken, Task<WeaponTargetContext>>? structureWeaponTargetContextOf;

    private Func<long, long, CancellationToken, Task<WeaponTargetContext>>? structureDamageTargetCorporationContextOf;

    private void SetStructureWeaponTargetContext(
        Func<long, long, CancellationToken, Task<WeaponTargetContext>> read)
    {
        structureWeaponTargetContextOf = read;
        if (spaceSessions is { } sessions)
            sessions.StructureWeaponTargetContextOf = read;
    }

    private void SetStructureDamageTargetCorporationContext(
        Func<long, long, CancellationToken, Task<WeaponTargetContext>> read)
    {
        structureDamageTargetCorporationContextOf = read;
        if (spaceSessions is { } sessions)
            sessions.StructureDamageTargetCorporationContextOf = read;
    }

    private void SetupStructureModules()
    {
        if (spaceSessions is not { } sessions || structureControlReadModel is not { } controlReadModel)
            return;

        sessions.StructureDogmaOf = controlReadModel.DogmaAsync;
        sessions.StructureWeaponTargetContextOf = structureWeaponTargetContextOf;
        sessions.StructureControlLeaseGenerationOf = controlReadModel.ControlLeaseGenerationAsync;
        sessions.StructureDamageTargetCorporationsOf = controlReadModel.DamageTargetCorporationsOfAsync;
        sessions.StructureDamageDroneOwnerOf = itemId => spaceDrones?.DamageOwnerOf(itemId);
        sessions.StructureDamageTargetCorporationContextOf = structureDamageTargetCorporationContextOf;
    }
}
