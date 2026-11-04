using Eve.FactionWarfare;
using Eve.Services.Slash;

namespace Eve.Host;

/// <summary>
/// The game master's faction warfare changes on this host (<see cref="IFwControl"/>): the faction a token names is the standing directory's (a faction's SDE name or
/// its ID), the enlistment and the victory points are the faction warfare service's, and the times are the host's clock.
/// </summary>
internal sealed class HostFwControl(FwService fw, IStandingDirectory factions, Func<long> nowMs) : IFwControl
{
    public async Task<GmFwAnswer> EnlistAsync(long corporationId, string factionToken, CancellationToken cancellationToken)
    {
        if (await factions.FindOwnerAsync(factionToken, cancellationToken) is not { IsFaction: true } faction)
            return new GmFwAnswer($"Faction not found: {factionToken}.");
        var label = faction.Name.Length > 0 ? faction.Name : $"faction {faction.Id}";
        var outcome = await fw.GmEnlistAsync(FwStore.CorporationKind, corporationId, faction.Id, nowMs(), cancellationToken);
        return outcome switch
        {
            EnlistOutcome.Accepted => new GmFwAnswer(null, label),
            EnlistOutcome.NotAnOccupier => new GmFwAnswer($"{label} is not an occupier faction; a corporation enlists in an occupier militia."),
            EnlistOutcome.AlreadyEnlisted => new GmFwAnswer($"Corporation {corporationId} is already in a faction; leave it first."),
            _ => new GmFwAnswer($"The enlistment could not be made ({outcome})."),
        };
    }

    public async Task<GmFwAnswer> LeaveAsync(long corporationId, CancellationToken cancellationToken) =>
        await fw.GmLeaveAsync(FwStore.CorporationKind, corporationId, nowMs(), cancellationToken) switch
        {
            EnlistOutcome.Accepted => new GmFwAnswer(null),
            _ => new GmFwAnswer($"Corporation {corporationId} is in no faction to leave."),
        };

    public async Task<GmFwAnswer> AddVictoryPointsAsync(long solarSystemId, long points, CancellationToken cancellationToken) =>
        await fw.AddVictoryPointsAsync(solarSystemId, points, nowMs(), cancellationToken) is { } total
            ? new GmFwAnswer(null, Points: total)
            : new GmFwAnswer($"System {solarSystemId} is not occupied, so it takes no victory points.");
}
