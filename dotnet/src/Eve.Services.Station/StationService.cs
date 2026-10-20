using Eve.Characters;
using Eve.Marshal;
using Eve.Universe;

namespace Eve.Services.Station;

[Service("stationSvc", Aliases = ["station"])]
public sealed partial class StationService(StationCatalog stations, PgCharacterStore characters, OnlineCharacters online, Func<long> nowUnixMs, Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? sovereigntySystemsForAlliance = null, Eve.Services.Corporations.OfficeManagerService? officeManager = null, SeedObserverGuestLedgerCallback? seedObserverGuestLedger = null, CharacterSessions? characterSessions = null)
{
    /// <summary>The station services (repair, reprocessing, market, ...) every NPC station offers, as the client's bit mask.</summary>
    public const long AllServicesMask = 1_143_312;

    private const long DockingCostPerVolume = 0;
    private const long MaxShipVolumeDockable = 50_000_000;
    private const long OfficeRentalCost = 10_000;

    /// <summary>The reference reports this for every station, whatever the station's own value.</summary>
    private const double ReprocessingEfficiency = 0.5;

    private static PyValue None => PyNone.Instance;

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    /// <summary>The station the session is docked at.</summary>
    private Universe.Station? StationOf(ClientSession session) =>
        session.GetInt("stationid") is { } id ? stations.Get(id) : null;

    /// <summary>The record of a station as the client's station cache holds it.</summary>
    internal static PyValue Record(Universe.Station s) => Py.KeyVal(
        ("stationID", new PyInt(s.Id)),
        ("stationName", new PyStr(s.Name)),
        ("stationTypeID", new PyInt(s.TypeId)),
        ("solarSystemID", new PyInt(s.SolarSystemId)),
        ("constellationID", new PyInt(s.ConstellationId)),
        ("regionID", new PyInt(s.RegionId)),
        ("ownerID", new PyInt(s.OwnerId)),
        ("corporationID", new PyInt(s.OwnerId)),
        ("dockingCostPerVolume", new PyInt(DockingCostPerVolume)),
        ("maxShipVolumeDockable", new PyInt(MaxShipVolumeDockable)),
        ("officeRentalCost", new PyInt(OfficeRentalCost)),
        ("operationID", new PyInt(s.OperationId)),
        ("stationTypeID", new PyInt(s.TypeId)), // the reference names it twice
        ("security", Py.Number(Math.Round(s.Security, 6))),
        ("x", new PyInt(0)),
        ("y", new PyInt(0)),
        ("z", new PyInt(0)),
        ("reprocessingEfficiency", Py.Number(ReprocessingEfficiency)),
        ("reprocessingStationsTake", Py.Number(s.ReprocessingStationsTake)),
        ("reprocessingHangarFlag", new PyInt(s.ReprocessingHangarFlag)),
        ("serviceMask", new PyInt(AllServicesMask)));

    /// <summary><c>GetStation(stationID)</c>: the record, to be kept by the client. A station that does not exist is answered with None.</summary>
    [Method("GetStation")]
    public PyValue GetStation(ServiceCall call) =>
        IdOf(call.Arg(0)) is { } id && stations.Get(id) is { } station
            ? ObjectCaching.MethodResult(Record(station), nowUnixMs())
            : None;

    [Method("GetStationsForOwner")]
    public PyValue GetStationsForOwner(ServiceCall call)
    {
        var ownerId = IdOf(call.Arg(0)) ?? 0;
        if (ownerId <= 0)
        {
            return new PyList([]);
        }

        var rows = stations.All
            .Where(station => station.OwnerId == ownerId)
            .OrderBy(station => station.Id)
            .Select(station => Py.KeyVal(
                ("stationID", new PyInt(station.Id)),
                ("stationName", new PyStr(station.Name)),
                ("solarSystemID", new PyInt(station.SolarSystemId)),
                ("constellationID", new PyInt(station.ConstellationId)),
                ("regionID", new PyInt(station.RegionId)),
                ("stationTypeID", new PyInt(station.TypeId)),
                ("ownerID", new PyInt(station.OwnerId)),
                ("corporationID", new PyInt(station.OwnerId)),
                ("security", Py.Number(Math.Round(station.Security, 3, MidpointRounding.AwayFromZero)))));
        return new PyList(rows.ToArray());
    }

    [Method("GetStationItemBits")]
    public PyValue GetStationItemBits(ServiceCall call) =>
        StationOf(call.Session) is { } s
            ? Py.Tuple(new PyInt(s.OwnerId), new PyInt(s.Id), new PyInt(s.OperationId), new PyInt(s.TypeId))
            : None;

    /// <summary>Who is docked here: <c>[(characterID, corporationID, allianceID, warFactionID)]</c> for every character in the game at this station.</summary>
    [Method("GetGuests")]
    public async ValueTask<PyValue> GetGuests(ServiceCall call)
    {
        var stationId = call.Session.GetInt("stationid") ?? call.Session.GetInt("stationID") ?? 0;
        if (stationId <= 0)
            return new PyList([]);

        var guestSessions = characterSessions?.InStation(stationId) ?? [];
        var sessionsByCharacter = guestSessions
            .Where(session => session.CharacterId is { } id && id > 0)
            .GroupBy(session => session.CharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var characterIds = characterSessions is null
            ? online.Snapshot()
            : sessionsByCharacter.Keys.ToArray();
        var present = await characters.GetManyAsync(characterIds, call.Cancellation);
        var guests = present
            .Where(character => character.StationId == stationId)
            .OrderBy(character => character.Id)
            .ToArray();
        seedObserverGuestLedger?.Invoke(
            call.Session,
            stationId,
            guests.Select(character => character.Id).Where(characterId => characterId > 0).ToHashSet());
        return new PyList([.. guests
            .Select(character => Py.Tuple(
                new PyInt(character.Id),
                new PyInt(character.CorporationId),
                new PyInt(character.AllianceId ?? 0),
                new PyInt(Math.Max(0, sessionsByCharacter.GetValueOrDefault(character.Id)?.GetInt("warfactionid") ?? 0))))]);
    }

    /// <summary>
    /// The solar system of the session's station. The reference answers with the station it is docked at whichever system is asked
    /// for, and so does this.
    /// </summary>
    [Method("GetSolarSystem")]
    public PyValue GetSolarSystem(ServiceCall call) => StationOf(call.Session) is { } s
        ? Py.KeyVal(
            ("solarSystemID", new PyInt(s.SolarSystemId)),
            ("solarSystemName", new PyStr(s.SolarSystemName)),
            ("constellationID", new PyInt(s.ConstellationId)),
            ("constellationName", new PyStr(s.ConstellationName)),
            ("regionID", new PyInt(s.RegionId)),
            ("regionName", new PyStr(s.RegionName)),
            ("security", Py.Number(s.Security)),
            ("factionID", s.FactionId is { } faction ? new PyInt(faction) : None),
            ("factionName", new PyStr(s.FactionName)),
            ("stationID", new PyInt(s.Id)),
            ("stationName", new PyStr(s.Name)),
            ("ownerID", new PyInt(s.OwnerId)),
            ("ownerName", new PyStr(s.OwnerName)),
            ("corporationID", new PyInt(s.OwnerId)),
            ("corporationName", new PyStr(s.OwnerName)),
            ("orbitID", new PyInt(s.OrbitId)),
            ("stationTypeID", new PyInt(s.TypeId)))
        : None;

    /// <summary>No alliance holds a system until sovereignty exists.</summary>
    [Method("GetAllianceSystems")]
    public async ValueTask<PyValue> GetAllianceSystems(ServiceCall call)
    {
        var allianceId = call.Session.GetInt("allianceid") ?? 0;
        if (allianceId <= 0 || sovereigntySystemsForAlliance is null)
            return new PyList([]);
        var systems = await sovereigntySystemsForAlliance(allianceId, call.Cancellation);
        return new PyList([.. systems.Select(systemId => (PyValue)new PyInt(systemId))]);
    }

    [Method("GetSystemsForAlliance")]
    public async ValueTask<PyValue> GetSystemsForAlliance(ServiceCall call)
    {
        var allianceId = IdOf(call.Arg(0)) ?? 0;
        if (allianceId <= 0 || sovereigntySystemsForAlliance is null)
            return new PyList([]);
        var systems = await sovereigntySystemsForAlliance(allianceId, call.Cancellation);
        return new PyList([.. systems.Select(systemId => (PyValue)new PyInt(systemId))]);
    }

    private Eve.Services.Corporations.OfficeManagerService RequireOfficeManager() =>
        officeManager ?? throw new InvalidOperationException(
            "StationService requires the shared OfficeManagerService for office station-moniker calls.");

    [Method("GetCorporationsWithOffices")]
    public ValueTask<PyValue> GetCorporationsWithOffices(ServiceCall call) =>
        RequireOfficeManager().GetCorporationsWithOffices(call);

    [Method("GetEmptyOfficeCount")]
    public ValueTask<PyValue> GetEmptyOfficeCount(ServiceCall call) =>
        RequireOfficeManager().GetEmptyOfficeCount(call);

    [Method("HasCorpImpoundedItems")]
    public ValueTask<PyValue> HasCorpImpoundedItems(ServiceCall call) =>
        RequireOfficeManager().HasCorpImpoundedItems(call);

    [Method("GetImpoundReleasePrice")]
    public ValueTask<PyValue> GetImpoundReleasePrice(ServiceCall call) =>
        RequireOfficeManager().GetImpoundReleasePrice(call);

    [Method("TrashImpoundedOffice")]
    public ValueTask<PyValue> TrashImpoundedOffice(ServiceCall call) =>
        RequireOfficeManager().TrashImpoundedOffice(call);

    [Method("PrimeOfficeItem")]
    public ValueTask<PyValue> PrimeOfficeItem(ServiceCall call) =>
        RequireOfficeManager().PrimeOfficeItem(call);

    [Method("UnrentOffice")]
    public ValueTask<PyValue> UnrentOffice(ServiceCall call) =>
        RequireOfficeManager().UnrentOffice(call);
}

[Service("map")]
public sealed partial class MapService
{
    private const string StationInfoQuery = "map.stationInfo";
    // The pinned EVE.js handler returns no modified systems; do not infer overrides from base SDE security.
    private const string SecurityModifiedSystemsQuery = "map.securityModifiedSystems";
    private const string StationCountQuery = "map.stationCount";
    private readonly Eve.ReadSide.ReadSide _reads;
    private readonly Func<long> _nowUnixMs;

    public MapService(Eve.ReadSide.ReadSide reads, Func<long> nowUnixMs)
    {
        _reads = reads;
        _nowUnixMs = nowUnixMs;
        reads.Register(new Eve.ReadSide.RowsetQuery(
            StationInfoQuery,
            """
            {sde}."npcStations" n
            JOIN {sde}."mapSolarSystems" s
              ON n.doc->>'solarSystemID' = s.key
            """,
            [
                Eve.ReadSide.Column.Of("stationID", Eve.ReadSide.ColumnType.Int, "n.key"),
                Eve.ReadSide.Column.Of("solarSystemID", Eve.ReadSide.ColumnType.Int, "n.doc #>> '{solarSystemID}'"),
                Eve.ReadSide.Column.Of("operationID", Eve.ReadSide.ColumnType.Int, "n.doc #>> '{operationID}'"),
                Eve.ReadSide.Column.Of("stationTypeID", Eve.ReadSide.ColumnType.Int, "n.doc #>> '{typeID}'"),
                Eve.ReadSide.Column.Of("ownerID", Eve.ReadSide.ColumnType.Int,
                    "COALESCE(NULLIF(n.doc #>> '{corporationID}', '0'), NULLIF(n.doc #>> '{ownerID}', '0'))"),
                Eve.ReadSide.Column.Of("serviceMask", Eve.ReadSide.ColumnType.Int,
                    StationService.AllServicesMask.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Eve.ReadSide.Column.Of("constellationID", Eve.ReadSide.ColumnType.Int, "s.doc #>> '{constellationID}'"),
                Eve.ReadSide.Column.Of("regionID", Eve.ReadSide.ColumnType.Int, "s.doc #>> '{regionID}'"),
            ])
        {
            OrderBy = "stationID",
            Cache = true,
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            SecurityModifiedSystemsQuery,
            "{sde}.\"mapSolarSystems\"",
            [
                Eve.ReadSide.Column.Of("solarSystemID", Eve.ReadSide.ColumnType.Int, "key::integer"),
            ])
        {
            Where = "false",
            OrderBy = "solarSystemID",
            Cache = true,
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            StationCountQuery,
            """
            (
                SELECT s.key::integer AS solarSystemID, COUNT(n.key)::integer AS stationCount
                FROM {sde}."mapSolarSystems" s
                LEFT JOIN {sde}."npcStations" n
                  ON n.doc->>'solarSystemID' = s.key
                GROUP BY s.key
            ) mapStationCounts
            """,
            [
                Eve.ReadSide.Column.Of("solarSystemID", Eve.ReadSide.ColumnType.Int, "mapStationCounts.solarSystemID"),
                Eve.ReadSide.Column.Of("stationCount", Eve.ReadSide.ColumnType.Int, "mapStationCounts.stationCount"),
            ])
        {
            OrderBy = "solarSystemID",
            Cache = true,
        });
    }

    [Method("GetStationInfo")]
    public async ValueTask<PyValue> GetStationInfo(ServiceCall call)
    {
        var rows = await _reads.RowsAsync(StationInfoQuery, cancellationToken: call.Cancellation);
        return ObjectCaching.MethodResult(rows.ToRowset(), _nowUnixMs());
    }

    [Method("GetSecurityModifiedSystems")]
    public async ValueTask<PyValue> GetSecurityModifiedSystems(ServiceCall call)
    {
        var rows = await _reads.RowsAsync(SecurityModifiedSystemsQuery, cancellationToken: call.Cancellation);
        return rows.ToRowset();
    }

    public Func<ServiceCall, ValueTask<PyValue?>>? ReadDataHook { get; init; }

    [Method("GetBeaconCount")]
    public async ValueTask<PyValue> GetBeaconCount(ServiceCall call) =>
        await ReadOrDefaultAsync(call, new PyDict([]));

    [Method("GetConstellationLPData")]
    public async ValueTask<PyValue> GetConstellationLPData(ServiceCall call) =>
        await ReadOrDefaultAsync(call, EmptyMapRowset("solarSystemID", "loyaltyPoints"));

    [Method("GetCurrentSovData")]
    public async ValueTask<PyValue> GetCurrentSovData(ServiceCall call) =>
        await ReadOrDefaultAsync(call, EmptyMapRowset(
            "locationID", "solarSystemID", "constellationID", "regionID", "ownerID",
            "allianceID", "corporationID", "claimStructureID", "infrastructureHubID",
            "stationID", "claimTime"));

    [Method("GetDeadspaceAgentsMap")]
    public async ValueTask<PyValue> GetDeadspaceAgentsMap(ServiceCall call) =>
        await ReadOrDefaultAsync(call, PyNone.Instance);

    [Method("GetDeadspaceComplexMap")]
    public async ValueTask<PyValue> GetDeadspaceComplexMap(ServiceCall call) =>
        await ReadOrDefaultAsync(call, PyNone.Instance);

    [Method("GetEdencomFortressSystems")]
    public async ValueTask<PyValue> GetEdencomFortressSystems(ServiceCall call) =>
        await ReadOrDefaultAsync(call, SystemIds(EdencomFortressSystems));

    [Method("GetEdencomMinorVictorySystems")]
    public async ValueTask<PyValue> GetEdencomMinorVictorySystems(ServiceCall call) =>
        await ReadOrDefaultAsync(call, SystemIds(EdencomMinorVictorySystems));

    [Method("GetFacWarZoneInfo")]
    public async ValueTask<PyValue> GetFacWarZoneInfo(ServiceCall call)
    {
        var factionId = PositiveMapId(call.Arg(0));
        var fallback = Py.KeyVal(
            ("factionID", factionId > 0 ? new PyInt(factionId) : PyNone.Instance),
            ("systemUpgradeLevel", new PyDict([])));
        return await ReadOrDefaultAsync(call, fallback);
    }

    [Method("GetHistory")]
    public async ValueTask<PyValue> GetHistory(ServiceCall call)
    {
        var history = await ReadOrDefaultAsync(call,
            EmptyMapRowset("solarSystemID", "value1", "value2", "value3"));
        return ObjectCaching.MethodResult(history, _nowUnixMs(), "1 minute");
    }

    [Method("GetIndustryCostModifier")]
    public async ValueTask<PyValue> GetIndustryCostModifier(ServiceCall call) =>
        await ReadOrDefaultAsync(call, new PyDict([]));

    [Method("GetIndustryJobsOverLast24Hours")]
    public async ValueTask<PyValue> GetIndustryJobsOverLast24Hours(ServiceCall call) =>
        await ReadOrDefaultAsync(call, EmptyMapRowset("solarSystemID", "noOfJobs"));

    [Method("GetMyExtraMapInfo")]
    public async ValueTask<PyValue> GetMyExtraMapInfo(ServiceCall call) =>
        await ReadOrDefaultAsync(call, EmptyMapRowset("characterID", "locationID"));

    [Method("GetMyExtraMapInfoAgents")]
    public async ValueTask<PyValue> GetMyExtraMapInfoAgents(ServiceCall call) =>
        await ReadOrDefaultAsync(call, EmptyMapRowset("fromID", "rank"));

    [Method("GetRecentSovActivity")]
    public async ValueTask<PyValue> GetRecentSovActivity(ServiceCall call) =>
        await ReadOrDefaultAsync(call,
            EmptyMapRowset("solarSystemID", "ownerID", "oldOwnerID", "stationID", "changeTime"));

    [Method("GetSolarSystemVisits")]
    public async ValueTask<PyValue> GetSolarSystemVisits(ServiceCall call) =>
        await ReadOrDefaultAsync(call, EmptyMapRowset("lastDateTime", "solarSystemID", "visits"));

    [Method("GetTriglavianMinorVictorySystems")]
    public async ValueTask<PyValue> GetTriglavianMinorVictorySystems(ServiceCall call) =>
        await ReadOrDefaultAsync(call, SystemIds(TriglavianMinorVictorySystems));

    [Method("GetAllRoamingWeatherSystems")]
    public async ValueTask<PyValue> GetAllRoamingWeatherSystems(ServiceCall call) =>
        await ReadOrDefaultAsync(call, EmptyMapRowset("locationID", "sceneType"));

    private async ValueTask<PyValue> ReadOrDefaultAsync(ServiceCall call, PyValue fallback) =>
        ReadDataHook is null ? fallback : await ReadDataHook(call) ?? fallback;

    private static PyValue EmptyMapRowset(params string[] columns) => Py.Rowset(columns, []);

    private static PyValue SystemIds(IEnumerable<long> ids) =>
        new PyList([.. ids.Select(id => (PyValue)new PyInt(id))]);

    private static long PositiveMapId(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value > 0 &&
            number.Value <= long.MaxValue && Math.Truncate(number.Value) == number.Value => (long)number.Value,
        PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 => number,
        _ => 0,
    };

    private static readonly long[] TriglavianMinorVictorySystems =
    [
        30003073, 30003076, 30003464, 30002575, 30003856, 30045331, 30004244,
        30001685, 30045338, 30045345, 30000163, 30001447, 30045354, 30000182,
        30002760, 30000205, 30001358, 30005330, 30002771, 30002645, 30001383,
        30002795, 30001390, 30001391, 30004981, 30001400, 30001401, 30002557,
    ];

    private static readonly long[] EdencomFortressSystems =
    [
        30004992, 30005251, 30000004, 30000005, 30004103, 30045322, 30002700,
        30005058, 30004141, 30002704, 30004248, 30004100, 30004250, 30003490,
        30002986, 30003883, 30003885, 30004150, 30003514, 30003515, 30000188,
        30005252, 30003392, 30002242, 30002243, 30003397, 30003398, 30005052,
        30005260, 30002251, 30002253, 30002385, 30002386, 30003539, 30003541,
        30002266, 30002651, 30003548, 30003553, 30002530, 30003556, 30003574,
        30002662, 30004305, 30000105, 30003050, 30004973, 30000113, 30004084,
        30003573, 30000118, 30002665, 30004090,
    ];

    private static readonly long[] EdencomMinorVictorySystems =
    [
        30002048, 30003074, 30002051, 30003460, 30003078, 30003463, 30000012,
        30003854, 30005263, 30003088, 30003090, 30003587, 30003478, 30003480,
        30003481, 30003482, 30004254, 30000160, 30004257, 30003931, 30002724,
        30004263, 30005034, 30004231, 30004268, 30003570, 30004284, 30000048,
        30005308, 30004999, 30001718, 30002999, 30005222, 30000060, 30005066,
        30000062, 30002239, 30003904, 30001696, 30005074, 30002755, 30003908,
        30003894, 30004289, 30004295, 30005213, 30004108, 30002506, 30003788,
        30004301, 30003918, 30003829, 30002513, 30003794, 30002644, 30004302,
        30005334, 30003927, 30005255, 30005209, 30003919, 30003932, 30002397,
        30005086, 30001376, 30003809, 30005219, 30003558, 30000102, 30004256,
        30003900, 30000109, 30002241, 30003823, 30003824, 30003058, 30004978,
        30005236, 30003061, 30005267, 30002772, 30004287, 30001660, 30005284,
    ];

    [Method("GetStationCount")]
    public async ValueTask<PyValue> GetStationCount(ServiceCall call)
    {
        var rows = await _reads.RowsAsync(StationCountQuery, cancellationToken: call.Cancellation);
        return new PyList([.. rows.Rows.Select(row => Py.Tuple(
            new PyInt(Convert.ToInt64(row[0])),
            new PyInt(Convert.ToInt64(row[1]))))]);
    }
}
