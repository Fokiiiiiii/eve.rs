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
    /// The minimum standing each station service needs from the owner's characters: the SDE's <c>stationStandingsRestrictions</c>
    /// (<c>services</c>: service id to minimum). An owner with no row has no restriction.
    /// </summary>
    private async ValueTask<IReadOnlyDictionary<long, double>> StationStandingRulesAsync(long owner, CancellationToken cancellationToken)
    {
        var rules = new Dictionary<long, double>();
        foreach (var row in await sde.ReadAsync("stationStandingsRestrictions", [owner], cancellationToken))
        {
            if (row.Key != owner || !row.Doc.TryGetProperty("services", out var services)) continue;
            foreach (var entry in services.EnumerateObject())
                if (long.TryParse(entry.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var serviceId)
                    && entry.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    rules[serviceId] = entry.Value.GetDouble();
        }
        return rules;
    }
}
