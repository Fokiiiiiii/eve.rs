using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Services.Space;
using Eve.Space;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private AbyssalDeploymentEligibility abyssalDeploymentEligibility = default!;

    private void ValidateAbyssalActivationSystem(int systemId)
    {
        if (abyssalCatalog.AbyssalSystemsById.ContainsKey(systemId)) throw AbyssalErrorAdapter.ClientError(2);
        // Pinned abyssalManagerRuntimeGroup7 and matching eve-logic solar-system constants.
        if (systemId is >= 34_000_000 and <= 34_999_999) throw AbyssalErrorAdapter.ClientError(35);
        if (systemId == 30_100_000) throw AbyssalErrorAdapter.ClientError(37);
    }

    private async Task<AbyssalParticipantPoseResult> ReadAbyssalDeploymentEligibilityAsync(
        AbyssalRunAdmissionRequest request, CancellationToken ct)
    {
        ValidateAbyssalActivationSystem(request.SolarSystemId);
        var space = spaceSessions ?? throw new InvalidOperationException("Deployment requires SpaceSessions.");
        var session = characterSessions.Of(request.CharacterId)
            ?? throw AbyssalErrorAdapter.ClientError(21);
        if (space.ShipIdOf(session) != request.ShipId) throw AbyssalErrorAdapter.ClientError(21);
        var pose = await space.GetAbyssalDeploymentPoseAsync(request.ShipId,
            SimSceneKey.SolarSystem(request.SolarSystemId), ct).ConfigureAwait(false);
        AbyssalDeploymentEligibility.ValidateProbe(request, pose);
        AbyssalDeploymentEligibility.ValidateSessionState(pose);
        if (await dungeonReadModel.ReadCurrentDungeonAsync(request.CharacterId, request.ShipId, request.SolarSystemId,
                pose.Position[0], pose.Position[1], pose.Position[2], pose.Deployment!.Warping, ct).ConfigureAwait(false) is { } dungeon &&
            dungeon is not PyNone)
            throw AbyssalErrorAdapter.ClientError(12);
        // The current POS owner models an anchored hull, not a force-field entity/radius.
        // Do not substitute tower hull radius or an invented InsideForceField=false for that unavailable state.
        AbyssalDeploymentEligibility.ValidateShipEffects(pose);
        var combat = await crimewatchStore.StateOfAsync(request.CharacterId, ct).ConfigureAwait(false);
        if (combat.PvpUntilMs > foundations.Scheduler.Now) throw AbyssalErrorAdapter.ClientError(9);
        var filament = await items.GetAsync(request.FilamentItemId, ct).ConfigureAwait(false)
            ?? throw AbyssalErrorAdapter.ClientError(7);
        if (!abyssalCatalog.FilamentsByTypeId.TryGetValue(filament.TypeId, out var definition))
            throw AbyssalErrorAdapter.ClientError(3);
        var system = (await sde.ReadAsync("mapSolarSystems", [request.SolarSystemId], ct).ConfigureAwait(false)).Single().Doc;
        var security = system.GetProperty("securityStatus").GetDouble();
        if (!double.IsFinite(security)) throw new InvalidDataException("The deployment system security is invalid.");
        if (AbyssalDeploymentEligibility.RequiresSuspect(definition.Tier, security) &&
            await crimewatchStore.SafetyOfAsync(request.CharacterId, ct).ConfigureAwait(false) == SafetyLevel.Full)
            throw AbyssalErrorAdapter.ClientError(10);
        return pose;
    }
}
