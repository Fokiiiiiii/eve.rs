using Eve.Services.Space;
using Eve.Services.Space.Entosis;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private Eve.Host.Persistence.Sovereignty.SovereigntyClaimNotifications sovClaimNotifications = default!;

    /// <summary>
    /// ST-9 d: the handover of a captured neutral hub. The sovereignty records give the system to the capturer (the sovereignty write path, verified in the
    /// persisted table), and the hub's ball in space is put back with its new owner. A claim the records refuse (another alliance has the system) is superseded.
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
        // ST-9 e: keyed by the claim time the records hold (a retry keeps the first claim's time); a failure is retried by the handover job
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
    /// ST-9 c, e: a system's Sovereignty Hub as the sovereignty records have it at a cycle. Its owner's alliance is the records' (none when neutral). The activity
    /// multiplier is the owner's system's (<see cref="StructureAnchorAuthority.ActivityDefenseMultiplier"/>); a neutral hub has none and its capture time is the
    /// base time (EVE.js's neutral capture is not scaled), so it is 1. The owner's prime hour centres its daily vulnerability window (<see cref="EntosisWindow"/>).
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
