using Eve.Characters;
using Eve.Marshal;
using Eve.Universe;

namespace Eve.Services.Station;

/// <summary>
/// What the client asks about the station its character is docked at (<c>stationSvc</c>, also reached as <c>station</c>):
/// the station's record, which it keeps for the whole run, the ownership tuple its station window is built from, who else
/// is docked here, and the solar system it is in.
/// </summary>
[Service("stationSvc", Aliases = ["station"])]
public sealed partial class StationService(StationCatalog stations, PgCharacterStore characters, OnlineCharacters online, Func<long> nowUnixMs)
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

    /// <summary><c>(ownerID, stationID, operationID, stationTypeID)</c> of the session's station: the columns of the row the client builds.</summary>
    [Method("GetStationItemBits")]
    public PyValue GetStationItemBits(ServiceCall call) =>
        StationOf(call.Session) is { } s ? Py.Tuple(new PyInt(s.OwnerId), new PyInt(s.Id), new PyInt(s.OperationId), new PyInt(s.TypeId)) : None;

    /// <summary>Who is docked here: <c>[(characterID, corporationID, allianceID, warFactionID)]</c> for every character in the game at this station.</summary>
    [Method("GetGuests")]
    public async ValueTask<PyValue> GetGuests(ServiceCall call)
    {
        if (call.Session.GetInt("stationid") is not { } stationId)
        {
            return new PyList([]);
        }
        var present = await characters.GetManyAsync(online.Snapshot(), call.Cancellation);
        return new PyList([.. present
            .Where(c => c.StationId == stationId)
            .OrderBy(c => c.Id)
            .Select(c => Py.Tuple(new PyInt(c.Id), new PyInt(c.CorporationId), new PyInt(c.AllianceId ?? 0), new PyInt(0)))]);
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
    public PyValue GetAllianceSystems(ServiceCall call) => new PyList([]);

    [Method("GetSystemsForAlliance")]
    public PyValue GetSystemsForAlliance(ServiceCall call) => new PyList([]);
}

[Service("map")]
public sealed partial class MapService
{
    private const string StationInfoQuery = "map.stationInfo";
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
    }

    [Method("GetStationInfo")]
    public async ValueTask<PyValue> GetStationInfo(ServiceCall call)
    {
        var rows = await _reads.RowsAsync(StationInfoQuery, cancellationToken: call.Cancellation);
        return ObjectCaching.MethodResult(rows.ToRowset(), _nowUnixMs());
    }
}
