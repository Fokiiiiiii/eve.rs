namespace Eve.Services.Core;
using Eve.Marshal;
using Eve.Macho;

public sealed record LookupEntity(
    long Id,
    string? Name,
    long CorporationId = 0,
    long AllianceId = 0,
    long FactionId = 0,
    int Gender = 0,
    string? TickerName = null,
    bool IsNpc = false,
    int? WarPermit = null);

public sealed record LookupLocation(
    long Id,
    string? Name,
    int TypeId,
    int GroupId,
    long? SolarSystemId = null,
    long? ConstellationId = null,
    long? RegionId = null);

public sealed record LookupType(int Id, string Name);

public sealed record LookupSearchSources(
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Characters,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Corporations,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Alliances,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Factions,
    Func<int, CancellationToken, Task<IReadOnlyList<LookupLocation>>>? Locations = null,
    Func<CancellationToken, Task<IReadOnlyList<LookupType>>>? Types = null) : ILookupDirectory
{
    public async ValueTask<IReadOnlyList<LookupCharacter>> CharactersAsync(CancellationToken cancellationToken) =>
        (await Characters(cancellationToken).ConfigureAwait(false))
            .Select(row => new LookupCharacter(row.Id, row.Name ?? string.Empty, Gender: row.Gender, CorporationId: row.CorporationId, AllianceId: row.AllianceId))
            .ToArray();

    public async ValueTask<IReadOnlyList<LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken) =>
        (await Corporations(cancellationToken).ConfigureAwait(false))
            .Select(row => new LookupCorporation(row.Id, row.Name ?? string.Empty, row.TickerName, row.FactionId, row.IsNpc, WarPermit: row.WarPermit))
            .ToArray();

    public async ValueTask<IReadOnlyList<LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken) =>
        (await Alliances(cancellationToken).ConfigureAwait(false))
            .Select(row => new LookupAlliance(row.Id, row.Name ?? string.Empty, row.TickerName, row.IsNpc, row.CorporationId, row.WarPermit))
            .ToArray();

    public async ValueTask<IReadOnlyList<LookupFaction>> FactionsAsync(CancellationToken cancellationToken) =>
        (await Factions(cancellationToken).ConfigureAwait(false))
            .Select(row => new LookupFaction(row.Id, row.Name ?? string.Empty))
            .ToArray();

    public async ValueTask<IReadOnlyList<LookupLocation>> LocationsByGroupAsync(int groupId, CancellationToken cancellationToken) =>
        Locations is null ? [] : await Locations(groupId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<LookupType>> TypesAsync(CancellationToken cancellationToken) =>
        Types is null ? [] : await Types(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// The lookup wire contract is a Python list of ordered util.KeyVal objects.
/// The host supplies live read-only snapshots backed by its character, corporation and universe stores.
/// </summary>
/// <summary>Persisted player character data supplied from PgCharacterStore.</summary>
public sealed record LookupCharacter(long Id, string Name, int TypeId = 1373, int Gender = 0, long CorporationId = 0, long? AllianceId = null);

/// <summary>Corporation records supplied from CorporationStore and its NPC seed.</summary>
public sealed record LookupCorporation(long Id, string Name, string? TickerName = null, long? FactionId = null, bool IsNpc = false, bool? AllowWar = null, int? WarPermit = null);

/// <summary>Alliance records supplied from CorporationStore.</summary>
public sealed record LookupAlliance(long Id, string Name, string? TickerName = null, bool IsNpc = false, long ExecutorCorporationId = 0, int? WarPermit = null);

/// <summary>Static faction rows supplied from Universe/SDE data.</summary>
public sealed record LookupFaction(long Id, string Name);
