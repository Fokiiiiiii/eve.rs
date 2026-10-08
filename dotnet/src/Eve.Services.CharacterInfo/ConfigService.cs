using Eve.Characters;
using Eve.Corporations;
using Eve.Marshal;
using Eve.Services;
using Eve.Universe;

namespace Eve.Services.CharacterInfo;

[Service("config")]
public sealed partial class ConfigService(
    PgCharacterStore characters,
    CorporationStore corporations,
    StationCatalog stations,
    SolarSystemCatalog solarSystems)
{
    private static readonly string[] OwnerColumns = ["ownerID", "ownerName", "typeID", "gender", "ownerNameID"];
    private static readonly string[] CorporationColumns =
        ["corporationID", "tickerName", "shape1", "shape2", "shape3", "color1", "color2", "color3"];
    private static readonly string[] LocationColumns =
        ["locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID"];

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

    [Method("GetMultiOwnersEx")]
    public async ValueTask<PyValue> GetMultiOwnersEx(ServiceCall call)
    {
        var ids = RequestedIds(call.Arg(0));
        var corporationById = ids.Length == 0
            ? new Dictionary<long, Corporation>()
            : (await corporations.GetManyAsync(ids, call.Cancellation)).ToDictionary(c => c.Id);
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
                    PyValue.Of(2L),
                    PyValue.Of(0L),
                    PyNone.Instance));
            }
            else
            {
                rows.Add(Row(
                    PyValue.Of(id),
                    PyValue.Of($"Item {id}"),
                    PyValue.Of(0L),
                    PyValue.Of(0L),
                    PyNone.Instance));
            }
        }

        return TupleSet(OwnerColumns, rows);
    }

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
            var ticker = corporationById.TryGetValue(id, out var corporation) && !corporation.Deleted
                ? corporation.Ticker
                : "CORP";
            rows.Add(Row(
                PyValue.Of(id),
                PyValue.Of(ticker),
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance));
        }

        return TupleSet(CorporationColumns, rows);
    }

    [Method("GetMultiLocationsEx")]
    public ValueTask<PyValue> GetMultiLocationsEx(ServiceCall call)
    {
        var ids = RequestedIds(call.Arg(0), includeZero: true);
        var currentSystemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
        var rows = new List<PyTuple>(ids.Length);
        foreach (var id in ids)
        {
            if (id == 0)
            {
                rows.Add(LocationRow(0, "(none)", null, "(none)"));
            }
            else if (stations.Get(id) is { } station)
            {
                rows.Add(LocationRow(id, station.Name, station.SolarSystemId));
            }
            else if (id <= int.MaxValue && solarSystems.Get((int)id) is { } system)
            {
                rows.Add(LocationRow(id, system.Name, system.Id));
            }
            else if (id is >= 60_000_000 and < 64_000_000)
            {
                rows.Add(LocationRow(id, $"Station {id}", currentSystemId > 0 ? (int?)currentSystemId : null));
            }
            else if (id is >= 30_000_000 and < 40_000_000)
            {
                rows.Add(LocationRow(id, $"System {id}", (int)id));
            }
            else
            {
                rows.Add(LocationRow(id, $"Location {id}", currentSystemId > 0 ? (int?)currentSystemId : null));
            }
        }

        return ValueTask.FromResult<PyValue>(TupleSet(LocationColumns, rows));
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

    private static PyTuple LocationRow(long id, string name, int? solarSystemId, string? nameId = null) =>
        Row(
            PyValue.Of(id),
            PyValue.Of(name),
            solarSystemId is { } system ? PyValue.Of((long)system) : PyNone.Instance,
            PyValue.Of(0d),
            PyValue.Of(0d),
            PyValue.Of(0d),
            PyValue.Of(nameId));

    private static PyTuple Row(params PyValue[] values) => PyTuple.Of(values);

    private static PyTuple TupleSet(IReadOnlyList<string> header, IEnumerable<PyTuple> rows) =>
        PyTuple.Of(
            PyTuple.Of(header.Select(column => (PyValue)new PyStr(column)).ToArray()),
            PyTuple.Of(rows.Select(row => (PyValue)row).ToArray()));
}
