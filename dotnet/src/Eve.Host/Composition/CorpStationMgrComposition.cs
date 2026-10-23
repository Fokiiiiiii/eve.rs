using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    /// <summary>The NPC station a corporation door is bound to, or null when there is no such station.</summary>
    private Eve.Services.Corporations.CorporationStationPlace? StationPlace(long stationId) =>
        stations.Get(stationId) is { } station ? new Eve.Services.Corporations.CorporationStationPlace(station.Id, station.OwnerId, station.SolarSystemId) : null;

    /// <summary>

    /// uses; EVE's effective standing for an NPC corporation).
    /// </summary>
    private async ValueTask<double> StandingTowardAsync(long character, long owner, CancellationToken cancellationToken)
        => await foundations.Relations.StandingTowardAsync(character, owner, cancellationToken);

    /// <summary>

    /// restriction. shortcut: the pinned SDE build has no stationStandingsRestrictions table (every read failed), so these are the seven

    /// SDE import carries the table.
    /// </summary>
    private static readonly IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> EmbeddedRules =
        new Dictionary<long, IReadOnlyDictionary<long, double>>
        {
            [500026] = new Dictionary<long, double> { [5] = 3.0, [13] = -0.05, [14] = 3.0, [17] = -0.05, [22] = -0.05, [23] = 3.0, [24] = 3.0 },
        };

    private ValueTask<IReadOnlyDictionary<long, double>> StationStandingRulesAsync(long owner, CancellationToken cancellationToken) =>
        ValueTask.FromResult(EmbeddedRules.TryGetValue(owner, out var rules) ? rules : new Dictionary<long, double>());
}
