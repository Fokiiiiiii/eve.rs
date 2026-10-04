namespace Eve.Services.Core;

/// <summary>The services ported so far, wired to their dependencies.</summary>
using Eve.Marshal;
using Eve.Macho;

public static class CoreServices
{
    /// <summary>Creates every core service. Register them with <see cref="ServiceRegistry.Register"/>.</summary>
    public static IReadOnlyList<IService> Create(SubscriptionSettings settings, IOmegaAccountStore accounts, Func<long> nowUnixMs)
    {
        var licenses = new OmegaLicenses(settings, accounts, nowUnixMs);
        return
        [
            new PingService(),
            new BrowserLockdownService(),
            new SubscriptionMgrService(settings, licenses),
            new CalendarMgrService(),
            new CalendarProxyService(),
        ];
    }

    /// <summary>Creates the read-only lookup service for explicit host registration.</summary>
    public static IService CreateLookupService(ILookupDirectory directory) => new LookupSvcService(directory);


    /// <summary>Creates the lookup service from the legacy asynchronous source delegates.</summary>
    public static IService CreateLookupService(LookupSearchSources sources) => new LookupSvcService(sources);

    /// <summary>The names of the services <see cref="Create"/> returns.</summary>
    public static IReadOnlyList<string> Names => Generated.Eve_Services_CoreServiceCatalog.Names;
}

public sealed record LookupEntity(
    long Id,
    string? Name,
    long CorporationId = 0,
    long AllianceId = 0,
    long FactionId = 0,
    int Gender = 0,
    string? TickerName = null,
    bool IsNpc = false,
    int WarPermit = 0);

public sealed record LookupSearchSources(
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Characters,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Corporations,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Alliances,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Factions) : ILookupDirectory
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

/// <summary>
/// The Host adapter exposes the owner rows from the existing character, corporation,
/// alliance, and universe stores. The core service owns EVE.js filtering and wire shape.
/// </summary>
public interface ILookupDirectory
{
    ValueTask<IReadOnlyList<LookupCharacter>> CharactersAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupFaction>> FactionsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// EVE.js v0.12.9 lookupSvc owner lookup methods, pinned to
/// 828823a75f211fdecb945b087a26c225f111491f.
/// </summary>
public sealed class LookupSvcService : IService
{
    private readonly ILookupDirectory directory;
    private const int MaxLookupResults = 500;
    private const int CharacterTypeId = 1373;
    private const int CorporationTypeId = 2;
    private const int AllianceTypeId = 16159;
    private const int FactionTypeId = 30;
    private const int CharacterGroupId = 1;
    private const int CorporationGroupId = 2;
    private const int AllianceGroupId = 32;
    private const int FactionGroupId = 19;

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "LookupCharacters",
        "LookupEvePlayerCharacters",
        "LookupPlayerCharacters",
        "LookupCorporations",
        "LookupOwners",
        "LookupPCOwners",
        "LookupNoneNPCAccountOwners",
        "LookupWarableCorporationsOrAlliances",
    };

    public string Name => "lookupSvc";

    public LookupSvcService(ILookupDirectory directory)
    {
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public bool HasMethod(string method) => Methods.Contains(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        return call.Method switch
        {
            "LookupCharacters" or "LookupEvePlayerCharacters" or "LookupPlayerCharacters" => await LookupCharactersAsync(call),
            "LookupCorporations" => await LookupCorporationsAsync(call),
            "LookupOwners" => await LookupOwnersAsync(call, includeNpcCorporations: true, includeAlliances: true, includeFactions: true),
            "LookupPCOwners" => await LookupOwnersAsync(call, includeNpcCorporations: false, includeAlliances: true, includeFactions: false),
            "LookupNoneNPCAccountOwners" => await LookupOwnersAsync(call, includeNpcCorporations: false, includeAlliances: false, includeFactions: false),
            "LookupWarableCorporationsOrAlliances" => await LookupWarableCorporationsOrAlliancesAsync(call),
            _ => PyNone.Instance,
        };
    }

    private async ValueTask<PyValue> LookupCharactersAsync(ServiceCall call)
    {
        var search = SearchString(call.Arg(0));
        var exactMode = ExactMode(call.Arg(1));
        var characters = await directory.CharactersAsync(call.Cancellation);
        var entries = characters
            .Where(character => character.Id > 0)
            .Select(character => new OwnerEntry(
                character.Id,
                NameOr(character.Name, $"Character {character.Id}"),
                character.TypeId > 0 ? character.TypeId : CharacterTypeId,
                CharacterGroupId,
                character.Gender,
                CharacterId: character.Id,
                CorporationId: Math.Max(character.CorporationId, 0),
                AllianceId: Math.Max(character.AllianceId ?? 0, 0)));
        return AsList(Filter(entries, search, exactMode).Select(BuildCharacterRow));
    }

    private async ValueTask<PyValue> LookupCorporationsAsync(ServiceCall call)
    {
        var search = SearchString(call.Arg(0));
        var exactMode = ExactMode(call.Arg(1));
        var corporations = await directory.CorporationsAsync(call.Cancellation);
        var entries = corporations.Where(corporation => corporation.Id > 0)
            .Select(corporation => new OwnerEntry(
                corporation.Id,
                NameOr(corporation.Name, $"Corporation {corporation.Id}"),
                CorporationTypeId,
                CorporationGroupId,
                0,
                CorporationId: corporation.Id,
                FactionId: Math.Max(corporation.FactionId ?? 0, 0),
                TickerName: EmptyToNull(corporation.TickerName),
                IncludeTickerName: true,
                IsNpc: corporation.IsNpc));
        return AsList(Filter(entries, search, exactMode).Select(BuildCorporationRow));
    }

    private async ValueTask<PyValue> LookupOwnersAsync(
        ServiceCall call,
        bool includeNpcCorporations,
        bool includeAlliances,
        bool includeFactions)
    {
        var search = SearchString(call.Arg(0));
        var exactMode = ExactMode(call.Arg(1));
        var characters = await directory.CharactersAsync(call.Cancellation);
        var corporations = await directory.CorporationsAsync(call.Cancellation);
        var alliances = includeAlliances
            ? await directory.AlliancesAsync(call.Cancellation)
            : [];
        var factions = includeFactions
            ? await directory.FactionsAsync(call.Cancellation)
            : [];

        var entries = new List<OwnerEntry>(
            characters.Count + corporations.Count + alliances.Count + factions.Count);
        entries.AddRange(characters.Where(character => character.Id > 0).Select(character =>
            new OwnerEntry(
                character.Id,
                NameOr(character.Name, $"Character {character.Id}"),
                character.TypeId > 0 ? character.TypeId : CharacterTypeId,
                CharacterGroupId,
                character.Gender,
                CharacterId: character.Id,
                CorporationId: Math.Max(character.CorporationId, 0),
                AllianceId: Math.Max(character.AllianceId ?? 0, 0))));
        entries.AddRange(corporations
            .Where(corporation => corporation.Id > 0 && (includeNpcCorporations || !corporation.IsNpc))
            .Select(corporation => new OwnerEntry(
                corporation.Id,
                NameOr(corporation.Name, $"Corporation {corporation.Id}"),
                CorporationTypeId,
                CorporationGroupId,
                0,
                CorporationId: corporation.Id,
                FactionId: Math.Max(corporation.FactionId ?? 0, 0),
                TickerName: EmptyToNull(corporation.TickerName),
                IncludeTickerName: true,
                IsNpc: corporation.IsNpc)));
        entries.AddRange(alliances.Where(alliance => alliance.Id > 0).Select(alliance =>
            new OwnerEntry(
                alliance.Id,
                NameOr(alliance.Name, $"Alliance {alliance.Id}"),
                AllianceTypeId,
                AllianceGroupId,
                0,
                AllianceId: alliance.Id,
                TickerName: EmptyToNull(alliance.TickerName),
                IncludeTickerName: true,
                IsNpc: alliance.IsNpc)));
        entries.AddRange(factions.Where(faction => faction.Id > 0).Select(faction =>
            new OwnerEntry(
                faction.Id,
                NameOr(faction.Name, $"Faction {faction.Id}"),
                FactionTypeId,
                FactionGroupId,
                0,
                FactionId: faction.Id)));

        return AsList(Filter(entries, search, exactMode).Select(BuildOwnerRow));
    }

    private async ValueTask<PyValue> LookupWarableCorporationsOrAlliancesAsync(ServiceCall call)
    {
        var search = (SearchString(call.Arg(0)) ?? string.Empty).ToLowerInvariant();
        var exact = JsTruthy(call.Arg(1));
        var corporations = await directory.CorporationsAsync(call.Cancellation);
        var alliances = await directory.AlliancesAsync(call.Cancellation);
        var corporationsById = corporations.ToDictionary(corporation => corporation.Id);
        var rows = new List<PyValue>();

        foreach (var corporation in corporations.OrderBy(corporation => corporation.Id))
        {
            if (corporation.Id <= 0 || !WarableNameMatches(corporation.Name, search, exact))
            {
                continue;
            }
            rows.Add(WarableRow(corporation.Id, corporation.Name, CorporationTypeId, CorporationPermit(corporation)));
        }

        foreach (var alliance in alliances.OrderBy(alliance => alliance.Id))
        {
            if (alliance.Id <= 0 || !WarableNameMatches(alliance.Name, search, exact))
            {
                continue;
            }
            var permit = alliance.WarPermit is { } alliancePermit
                ? (alliancePermit == 0 ? 0 : 1)
                : corporationsById.TryGetValue(alliance.ExecutorCorporationId, out var executor)
                    ? CorporationPermit(executor)
                    : 0;
            rows.Add(WarableRow(alliance.Id, alliance.Name, AllianceTypeId, permit));
        }

        return new PyList(rows);
    }

    private static PyValue BuildCharacterRow(OwnerEntry entry) => Py.KeyVal(
        ("characterID", new PyInt(entry.Id)),
        ("characterName", new PyStr(entry.Name)),
        ("ownerID", new PyInt(entry.Id)),
        ("ownerName", new PyStr(entry.Name)),
        ("typeID", new PyInt(entry.TypeId)),
        ("groupID", new PyInt(CharacterGroupId)),
        ("corporationID", new PyInt(entry.CorporationId ?? 0)),
        ("allianceID", new PyInt(entry.AllianceId ?? 0)));

    private static PyValue BuildCorporationRow(OwnerEntry entry) => Py.KeyVal(
        ("corporationID", new PyInt(entry.Id)),
        ("corporationName", new PyStr(entry.Name)),
        ("ownerID", new PyInt(entry.Id)),
        ("ownerName", new PyStr(entry.Name)),
        ("typeID", new PyInt(CorporationTypeId)),
        ("groupID", new PyInt(CorporationGroupId)),
        ("tickerName", entry.TickerName is { } ticker ? new PyStr(ticker) : PyNone.Instance),
        ("factionID", new PyInt(entry.FactionId ?? 0)),
        ("isNPC", PyValue.Of(entry.IsNpc ?? false)));

    private static PyValue BuildOwnerRow(OwnerEntry entry)
    {
        var fields = new List<(string Key, PyValue Value)>
        {
            ("ownerID", new PyInt(entry.Id)),
            ("ownerName", new PyStr(entry.Name)),
            ("typeID", new PyInt(entry.TypeId)),
            ("groupID", new PyInt(entry.GroupId)),
            ("gender", new PyInt(entry.Gender)),
        };
        if (entry.CharacterId is { } characterId) fields.Add(("characterID", new PyInt(characterId)));
        if (entry.CorporationId is { } corporationId) fields.Add(("corporationID", new PyInt(corporationId)));
        if (entry.AllianceId is { } allianceId) fields.Add(("allianceID", new PyInt(allianceId)));
        if (entry.FactionId is { } factionId) fields.Add(("factionID", new PyInt(factionId)));
        if (entry.IncludeTickerName)
        {
            fields.Add(("tickerName", entry.TickerName is { } ticker ? new PyStr(ticker) : PyNone.Instance));
        }
        if (entry.IsNpc is { } isNpc) fields.Add(("isNPC", PyValue.Of(isNpc)));
        return Py.KeyVal(fields.ToArray());
    }

    private static PyValue WarableRow(long id, string name, int typeId, int permit) => Py.KeyVal(
        ("ownerID", new PyInt(id)),
        ("ownerName", new PyStr(name)),
        ("typeID", new PyInt(typeId)),
        ("warPermit", new PyInt(permit)));

    private static PyList AsList(IEnumerable<PyValue> items) => new(items.ToArray());

    private static IReadOnlyList<OwnerEntry> Filter(IEnumerable<OwnerEntry> entries, string search, int exactMode)
    {
        if (Collapse(search).Length == 0)
        {
            return [];
        }

        return entries
            .Where(entry => Matches(entry.Name, search, exactMode))
            .OrderBy(entry => Rank(entry.Name, search))
            .ThenBy(entry => entry.Name, StringComparer.CurrentCulture)
            .ThenBy(entry => entry.Id)
            .Take(MaxLookupResults)
            .ToArray();
    }

    private static bool Matches(string name, string search, int exactMode)
    {
        var rawTarget = Normalize(name);
        var collapsedTarget = Collapse(rawTarget);
        var rawSearch = Normalize(search);
        var collapsedSearch = Collapse(rawSearch);
        if (collapsedSearch.Length == 0)
        {
            return false;
        }

        if (exactMode is 1 or 2 or 3)
        {
            return rawTarget == rawSearch || collapsedTarget == collapsedSearch;
        }

        var terms = rawSearch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Collapse)
            .Where(term => term.Length > 0)
            .ToArray();
        return terms.Length == 0
            ? collapsedTarget.Contains(collapsedSearch, StringComparison.Ordinal)
            : terms.All(term => collapsedTarget.Contains(term, StringComparison.Ordinal));
    }

    private static int Rank(string name, string search)
    {
        var rawName = Normalize(name);
        var collapsedName = Collapse(rawName);
        var rawSearch = Normalize(search);
        var collapsedSearch = Collapse(rawSearch);
        if (rawName == rawSearch || collapsedName == collapsedSearch) return 0;
        if (rawName.StartsWith(rawSearch, StringComparison.Ordinal) || collapsedName.StartsWith(collapsedSearch, StringComparison.Ordinal)) return 1;
        return 2;
    }

    private static int ExactMode(PyValue value)
    {
        var numeric = JsSemantics.NumberValue(value);
        if (!double.IsFinite(numeric)) return 0;
        var integral = Math.Truncate(numeric);
        return integral is >= int.MinValue and <= int.MaxValue ? (int)integral : 0;
    }

    private static string SearchString(PyValue value) => JsSemantics.StringValue(value)?.Trim() ?? string.Empty;
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string Collapse(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }

    private static string NameOr(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;
    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool JsTruthy(PyValue value) => value switch
    {
        PyNone => false,
        PyBool boolean => boolean.Value,
        PyInt number => number.Value != 0,
        PyReal number => number.Value != 0 && !double.IsNaN(number.Value),
        PyStr text => text.Value.Length > 0,
        _ => true,
    };

    private static int CorporationPermit(LookupCorporation corporation) =>
        corporation.WarPermit is { } permit ? (permit == 0 ? 0 : 1)
        : corporation.AllowWar is { } allowWar ? (allowWar ? 1 : 0)
        : corporation.IsNpc ? 0 : 1;

    private static bool WarableNameMatches(string name, string search, bool exact)
    {
        var loweredName = (name ?? string.Empty).ToLowerInvariant();
        if (exact)
        {
            return loweredName == search || Collapse(loweredName) == Collapse(search);
        }
        return loweredName.Contains(search, StringComparison.Ordinal)
            || Collapse(loweredName).Contains(Collapse(search), StringComparison.Ordinal);
    }

    private sealed record OwnerEntry(
        long Id,
        string Name,
        int TypeId,
        int GroupId,
        int Gender,
        long? CharacterId = null,
        long? CorporationId = null,
        long? AllianceId = null,
        long? FactionId = null,
        string? TickerName = null,
        bool IncludeTickerName = false,
        bool? IsNpc = null);
}
