using Eve.Services.Space;
using Eve.Services.Space.Entosis;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private Eve.Host.Persistence.Sovereignty.SovereigntyClaimNotifications sovClaimNotifications = default!;
    private Eve.Services.Space.Entosis.EntosisCaptureService? entosisCaptureService;

    /// <summary>


    /// </summary>
    private Task<bool> EntosisHubDestroyedAsync(Eve.Services.Space.Entosis.EntosisDestroyedHub hub, CancellationToken cancellationToken) =>
        sovWriteOperations.DestroyLostHubAsync(hub.SolarSystemId, hub.HubId, hub.DefenderAllianceId, cancellationToken);


    private async Task<int> EntosisConstellationOfAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        var systems = await mapSolarSystems.Value.WaitAsync(cancellationToken);
        var home = systems.FirstOrDefault(row => row.Key == solarSystemId);
        return home is not null && home.Doc.TryGetProperty("constellationID", out var value) && value.TryGetInt32(out var id) ? id : 0;
    }

    /// <summary>

    /// member when the static data has no constellation for it.
    /// </summary>
    private async Task<IReadOnlyList<int>> EntosisConstellationSystemsAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        var systems = await mapSolarSystems.Value.WaitAsync(cancellationToken);
        static long ConstellationOf(Eve.Sde.SdeRow row) =>
            row.Doc.TryGetProperty("constellationID", out var value) && value.TryGetInt64(out var id) ? id : 0;
        var home = systems.FirstOrDefault(row => row.Key == solarSystemId);
        if (home is null || ConstellationOf(home) == 0)
        {
            return [solarSystemId];
        }
        var constellation = ConstellationOf(home);
        return systems.Where(row => row.Key > 0 && ConstellationOf(row) == constellation).Select(row => (int)row.Key).ToList();
    }

    /// <summary>


    /// </summary>
    private async Task<EntosisHandover> EntosisHandoverAsync(EntosisCapturedHub captured, CancellationToken cancellationToken)
    {
        var fileTime = DateTime.UtcNow.ToFileTimeUtc();
        // the claim is committed for the capturing alliance's executor corporation (EVE.js commitInitialSovereigntyHubCapture: executorCorporationID); a
        // capturing corporation stands in when the alliance has none
        var executor = (await corporations.AllianceAsync(captured.AllianceId, cancellationToken))?.ActiveExecutorId ?? captured.CorporationId;
        var claimed = await sovWriteOperations.ClaimCapturedHubAsync(
            captured.SolarSystemId, captured.HubId, executor, captured.AllianceId, fileTime, cancellationToken);
        if (!claimed)
        {
            return EntosisHandover.Superseded;
        }
        if (spaceSovHubs is not null)
        {
            await spaceSovHubs.ReplaceAsync(captured.HubId, captured.SolarSystemId, cancellationToken);
        }

        var snapshot = await sovereigntyReadModel.ReadSnapshotAsync(cancellationToken);
        var claimTime = snapshot.HubsBySolarSystemId.TryGetValue(captured.SolarSystemId, out var claimed0) ? claimed0.ClaimTimeFileTime : null;
        if (claimTime is not { } claimedAt)
        {
            throw new InvalidOperationException($"The claim of hub {captured.HubId} has no claim time in the records; its notices are tried again.");
        }
        await sovClaimNotifications.WriteAsync(captured.HubId, claimedAt, executor, captured.AllianceId, cancellationToken);
        return EntosisHandover.Claimed;
    }

    /// <summary>



    /// </summary>
    private async Task<EntosisHubReading?> EntosisHubReadingAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        var snapshot = await sovereigntyReadModel.ReadSnapshotAsync(cancellationToken);
        if (SpaceSovHubs.HubOf(snapshot, solarSystemId) is not { } hub)
        {
            return null;
        }
        var multiplier = 1.0;
        var primeHour = 0;
        if (hub.AllianceId is { } allianceId and > 0)
        {
            primeHour = (await corporations.AlliancePrimeTimeAsync(allianceId, cancellationToken)).CurrentHour;
            var indices = await sovereigntyReadModel.ReadAllDevelopmentIndicesAsync(cancellationToken);
            if (indices.FirstOrDefault(index => index.SolarSystemId == solarSystemId) is { } record)
            {
                multiplier = StructureAnchorAuthority.ActivityDefenseMultiplier(record);
            }
        }
        return new EntosisHubReading(hub.HubId, solarSystemId, hub.AllianceId, multiplier, primeHour);
    }
}
