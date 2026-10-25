using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private SpaceStructureCynoBeacons? spaceStructureCynoBeacons;
    private Eve.Services.Space.SpaceSovHubs? spaceSovHubs;

    /// <summary>Called from structure composition after its space structures and EventBus are available.</summary>
    private void SetupStructureCynoBeaconRuntime()
    {
        StructureFuelBayNotifications.Subscribe(foundations.Events, characterSessions, dogma);
        StructureCynoBeaconNotifications.Subscribe(foundations.Events, structureStore, characterSessions);
        if (spaceSessions is null)
            return;

        spaceStructureCynoBeacons = new SpaceStructureCynoBeacons(
            source,
            structureStore,
            dogma,
            spaceSessions,
            message => Console.Error.WriteLine($"eve-host: structure cyno beacons: {message}"));
        spaceStructureCynoBeacons.Subscribe(foundations.Events);
    }

    /// <summary>Restores ordinary structures first, then the online Pharolux scene beacons.</summary>
    private async Task RestoreStructuresAndCynoBeaconsAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        if (spaceStructures is not null)
            await spaceStructures.RestoreAsync(solarSystemId, cancellationToken);
        if (spaceStructureCynoBeacons is not null)
            await spaceStructureCynoBeacons.RestoreAsync(solarSystemId, cancellationToken);
    }

    private Task<PyValue> JumpToStructureBeaconAsync(
        ClientSession session,
        long structureId,
        int typeId,
        int solarSystemId,
        string? boundObjectId,
        CancellationToken cancellationToken)
    {
        if (spaceSessions?.JumpDrive is not { } jumpDrive)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Your jump drive is not available.")));
        return jumpDrive.JumpToStructureBeaconAsync(session, structureId, typeId, solarSystemId, boundObjectId, cancellationToken);
    }

    private async ValueTask<PyValue> BridgeToStructureBeaconAsync(
        ServiceCall call, long structureId, Eve.Services.Space.StructureBeaconInfo beacon)
    {
        if (spaceSessions?.Bridges is not { } bridges)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Structure cyno bridging is not available.")));
        await bridges.OpenStructureAsync(
            call.Session, structureId, beacon.TypeId, checked((int)beacon.SolarSystemId), call.Cancellation);
        return PyBool.True;
    }

    private async ValueTask<PyValue> GroupJumpToStructureBeaconAsync(
        ServiceCall call, long structureId, Eve.Services.Space.StructureBeaconInfo beacon)
    {
        if (spaceSessions?.Conduits is not { } conduits)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Structure group jumps are not available.")));
        return await conduits.GroupJumpToStructureBeaconAsync(
            call.Session, structureId, beacon.TypeId, checked((int)beacon.SolarSystemId), call.BoundObjectId, call.Cancellation);
    }

}
