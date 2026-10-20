using Eve.Characters;
using Eve.Corporations;
using Eve.Marshal;
using Eve.Inventory;
using Eve.Dogma;
using Eve.Services;
using Eve.Universe;

namespace Eve.Services.CharacterInfo;

[Service("config")]
public sealed partial class ConfigService(
    PgCharacterStore characters,
    CorporationStore corporations,
    StationCatalog stations,
    SolarSystemCatalog solarSystems,
    Func<CancellationToken, Task<IReadOnlyDictionary<long, decimal>>>? latestAverageSales = null,
    PgItemStore? items = null,
    DogmaStatic? dogma = null)
{
    private static readonly string[] OwnerColumns = ["ownerID", "ownerName", "typeID", "gender", "ownerNameID"];
    private static readonly string[] CorporationColumns =
        ["corporationID", "tickerName", "shape1", "shape2", "shape3", "color1", "color2", "color3"];
    private static readonly string[] LocationColumns =
        ["locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID"];

    private const long CorporationTypeId = 2;
    private const long AllianceTypeId = 16159;

    [Method("GetStationSolarSystemsByOwner")]
    public PyValue GetStationSolarSystemsByOwner(ServiceCall call)
    {
        var ownerId = call.Arg(0) switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => 0,
        };
        if (ownerId <= 0)
            return new PyList([]);

        var rows = stations.All
            .Where(station => station.OwnerId == ownerId && station.SolarSystemId > 0)
            .GroupBy(station => station.SolarSystemId)
            .OrderBy(group => group.Key)
            .Select(group => (PyValue)Py.KeyVal(
                ("ownerID", PyValue.Of(ownerId)),
                ("solarSystemID", PyValue.Of((long)group.Key)),
                ("stationCount", PyValue.Of((long)group.Count()))))
            .ToArray();
        return new PyList(rows);
    }

    /// <summary>The planets the client does not offer. The static data and the server have no such list, so none is blocked.</summary>
    [Method("GetBlackListedPlanets")]
    public PyValue GetBlackListedPlanets(ServiceCall call) => new PyList([]);

    /// <summary>
    /// The stations that were removed from the game. The static data has no such list. The client reads the answer only when it is
    /// not empty (eveCfg.py:750-752), so the empty list is the answer for "none".
    /// </summary>
    [Method("GetOldStationData")]
    public PyValue GetOldStationData(ServiceCall call) => new PyList([]);

    /// <summary>
    /// The average price of each type that has a recorded sale: the newest day's volume-weighted average (the market's fills,
    /// <c>MarketStore.LatestAverageSalesAsync</c>). A type with no sale has no entry, so the client's GetAveragePrice returns None
    /// </summary>
    [Method("GetAverageMarketPrices")]
    public async ValueTask<PyValue> GetAverageMarketPrices(ServiceCall call)
    {
        IReadOnlyDictionary<long, decimal> sales = latestAverageSales is null
            ? new Dictionary<long, decimal>()
            : await latestAverageSales(call.Cancellation);
        var entries = sales
            .Where(sale => sale.Value > 0)
            .OrderBy(sale => sale.Key)
            .Select(sale => new KeyValuePair<PyValue, PyValue>(
                PyValue.Of(sale.Key),
                (PyValue)Py.KeyVal(
                    ("averagePrice", PyValue.Of((double)sale.Value)),
                    ("adjustedPrice", PyValue.Of((double)sale.Value)))))
            .ToArray();
        return new PyDict(entries);
    }

    /// <summary>
    /// of these has no row. The client's Prime logs the miss, and a later Get raises RecordNotFound (cfg.py:600-602, 643-645);
    /// no placeholder name is made up for it.
    /// </summary>
    [Method("GetMultiOwnersEx")]
    public async ValueTask<PyValue> GetMultiOwnersEx(ServiceCall call)
    {
        var ids = RequestedIds(call.Arg(0));
        var corporationById = ids.Length == 0
            ? new Dictionary<long, Corporation>()
            : (await corporations.GetManyAsync(ids, call.Cancellation)).ToDictionary(c => c.Id);
        var allianceById = ids.Length == 0
            ? new Dictionary<long, Alliance>()
            : (await corporations.AlliancesByIdsAsync(ids, call.Cancellation))
                .Where(alliance => !alliance.Deleted)
                .ToDictionary(alliance => alliance.Id);
        var rows = new List<PyTuple>(ids.Length);
        foreach (var id in ids)
        {
            var character = await characters.GetAsync(id, call.Cancellation);
            if (character is not null)
            {
                rows.Add(Row(
                    PyValue.Of(character.Id),
                    PyValue.Of(character.Name),
                    PyValue.Of((long)character.TypeId),
                    PyValue.Of(Convert.ToInt64(character.Gender)),
                    PyNone.Instance));
            }
            else if (corporationById.TryGetValue(id, out var corporation) && !corporation.Deleted)
            {
                rows.Add(Row(
                    PyValue.Of(corporation.Id),
                    PyValue.Of(corporation.Name),
                    PyValue.Of(CorporationTypeId),
                    PyValue.Of(0L),
                    PyNone.Instance));
            }
            else if (allianceById.TryGetValue(id, out var alliance))
            {
                rows.Add(Row(
                    PyValue.Of(alliance.Id),
                    PyValue.Of(alliance.Name),
                    PyValue.Of(AllianceTypeId),
                    PyValue.Of(0L),
                    PyNone.Instance));
            }
        }

        return TupleSet(OwnerColumns, rows);
    }

    /// <summary>The tickers of the corporations asked for; an unknown or closed corporation has no row.</summary>
    [Method("GetMultiCorpTickerNamesEx")]
    public async ValueTask<PyValue> GetMultiCorpTickerNamesEx(ServiceCall call)
    {
        var ids = RequestedIds(call.Arg(0));
        var corporationById = ids.Length == 0
            ? new Dictionary<long, Corporation>()
            : (await corporations.GetManyAsync(ids, call.Cancellation)).ToDictionary(c => c.Id);
        var rows = new List<PyTuple>(ids.Length);
        foreach (var id in ids)
        {
            if (!corporationById.TryGetValue(id, out var corporation) || corporation.Deleted)
            {
                continue;
            }
            rows.Add(Row(
                PyValue.Of(id),
                PyValue.Of(corporation.Ticker),
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance));
        }

        return TupleSet(CorporationColumns, rows);
    }

    /// <summary>The short names of the alliances asked for; an unknown or closed alliance has no row.</summary>
    [Method("GetMultiAllianceShortNamesEx")]
    public async ValueTask<PyValue> GetMultiAllianceShortNamesEx(ServiceCall call)
    {
        var ids = RequestedIds(call.Arg(0));
        var alliances = ids.Length == 0
            ? new Dictionary<long, Alliance>()
            : (await corporations.AlliancesByIdsAsync(ids, call.Cancellation))
                .Where(alliance => !alliance.Deleted)
                .ToDictionary(alliance => alliance.Id);
        var rows = ids
            .Where(alliances.ContainsKey)
            .Select(id => Row(PyValue.Of(id), PyValue.Of(alliances[id].ShortName)))
            .ToArray();
        return TupleSet(["allianceID", "shortName"], rows);
    }

    /// <summary>
    /// The locations asked for: the NPC stations and solar systems of the static data, with the station's position. Location 0
    /// is the client's "none" row. Owned inventory items also expose their stored label or SDE type name; unknown and foreign items have no row.
    /// </summary>
    [Method("GetMultiLocationsEx")]
    public async ValueTask<PyValue> GetMultiLocationsEx(ServiceCall call)
    {
        var ids = RequestedIds(call.Arg(0), includeZero: true);
        var rows = new List<PyTuple>(ids.Length);
        foreach (var id in ids)
        {
            if (id == 0)
            {
                rows.Add(LocationRow(0, "(none)", null, nameId: "(none)"));
            }
            else if (stations.Get(id) is { } station)
            {
                rows.Add(LocationRow(station));
            }
            else if (id <= int.MaxValue && solarSystems.Get((int)id) is { } system)
            {
                rows.Add(LocationRow(id, system.Name, system.Id));
            }
            else if (items is not null && dogma is not null && call.Session.CharacterId is { } characterId
                && await items.GetAsync(id, call.Cancellation) is { } item && item.OwnerId == characterId)
            {
                await dogma.EnsureTypesAsync([item.TypeId], call.Cancellation);
                var name = item.CustomName ?? dogma.Type(item.TypeId)?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (stations.Get(item.LocationId) is { } itemStation)
                    rows.Add(LocationRow(id, name, itemStation.SolarSystemId, itemStation.X, itemStation.Y, itemStation.Z));
                else if (item.LocationId <= int.MaxValue && solarSystems.Get((int)item.LocationId) is { } itemSystem)
                    rows.Add(LocationRow(id, name, itemSystem.Id));
            }
        }

        return TupleSet(LocationColumns, rows);
    }

    private static long[] RequestedIds(PyValue value, bool includeZero = false)
    {
        var items = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => [],
        };
        return items.Select(item => item switch
            {
                PyInt number => number.Value,
                PyLong number => number.Value,
                _ => 0,
            })
            .Where(id => id > 0 || includeZero && id == 0)
            .Distinct()
            .ToArray();
    }

    private static PyTuple LocationRow(long id, string name, int? solarSystemId, double x = 0, double y = 0, double z = 0, string? nameId = null) =>
        Row(
            PyValue.Of(id),
            PyValue.Of(name),
            solarSystemId is { } system ? PyValue.Of((long)system) : PyNone.Instance,
            PyValue.Of(x),
            PyValue.Of(y),
            PyValue.Of(z),
            PyValue.Of(nameId));

    private static PyTuple LocationRow(Station station) =>
        LocationRow(station.Id, station.Name, station.SolarSystemId, station.X, station.Y, station.Z);

    private static PyTuple Row(params PyValue[] values) => PyTuple.Of(values);

    private static PyTuple TupleSet(IReadOnlyList<string> header, IEnumerable<PyTuple> rows) =>
        PyTuple.Of(
            PyTuple.Of(header.Select(column => (PyValue)new PyStr(column)).ToArray()),
            PyTuple.Of(rows.Select(row => (PyValue)row).ToArray()));
}
