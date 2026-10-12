using Eve.Relations;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    /// <summary>The NPC station a corporation door is bound to, or null when there is no such station.</summary>
    private Eve.Services.Corporations.CorporationStationPlace? StationPlace(long stationId) =>
        stations.Get(stationId) is { } station ? new Eve.Services.Corporations.CorporationStationPlace(station.Id, station.OwnerId, station.SolarSystemId) : null;

    /// <summary>
    /// The standing a character has toward an owner: the higher of its own and its corporation's (the rule the reprocessing plant also
    /// uses; EVE's effective standing for an NPC corporation).
    /// </summary>
    private async ValueTask<double> StandingTowardAsync(long character, long owner, CancellationToken cancellationToken)
    {
        var personal = await foundations.Relations.ValueAsync(RelationKinds.Standing.Name, character, owner, 0, cancellationToken: cancellationToken);
        var affiliation = await foundations.Relations.AffiliationOfAsync(character, cancellationToken: cancellationToken);
        var corporate = affiliation.Corporation is { } corporation
            ? await foundations.Relations.ValueAsync(RelationKinds.Standing.Name, corporation, owner, 0, cancellationToken: cancellationToken)
            : 0;
        return Math.Max(personal, corporate);
    }

    /// <summary>
    /// The minimum standing each station service needs from the owner's characters (service id to minimum). An owner with no row has no
    /// restriction. shortcut: the pinned SDE build has no stationStandingsRestrictions table (every read failed), so these are the seven
    /// rules of owner 500026 as EveJS's static table holds them (from the client's stationstandingsrestrictions.fsdbinary); upgrade when the
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
