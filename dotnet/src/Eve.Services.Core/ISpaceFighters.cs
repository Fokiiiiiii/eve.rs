using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>
/// The server side of the <c>fighterMgr</c> calls: one answer per call, in the shape the client reads it (EVE.js <c>fighterMgrService</c>). The
/// <see cref="FighterMgrService"/> routes each call here; a call without a provider is refused by the service, never answered as done.
/// </summary>
public interface ISpaceFighters
{
    ValueTask<PyValue> GetFightersForShipAsync(ServiceCall call);

    ValueTask<PyValue> LoadFightersToTubeAsync(ServiceCall call);

    ValueTask<PyValue> UnloadTubeToFighterBayAsync(ServiceCall call);

    ValueTask<PyValue> LaunchFightersFromTubesAsync(ServiceCall call);

    ValueTask<PyValue> RecallFightersToTubesAsync(ServiceCall call);

    ValueTask<PyValue> CmdActivateAbilitySlotsAsync(ServiceCall call);

    ValueTask<PyValue> CmdDeactivateAbilitySlotsAsync(ServiceCall call);

    ValueTask<PyValue> ExecuteMovementCommandOnFightersAsync(ServiceCall call);

    ValueTask<PyValue> CmdAbandonFighterAsync(ServiceCall call);

    ValueTask<PyValue> CmdScoopAbandonedFighterFromSpaceAsync(ServiceCall call);
}
