using Eve.Marshal;
using Eve.Market;

namespace Eve.Services.Market;

/// <summary>marketProxy's order rows and DBRowDescriptor, ported from marketProxyService.js (828823a).</summary>
public static class MarketWire
{
    private static readonly (string Name,int Type)[] Columns = [
        ("price",5),("volRemaining",3),("typeID",3),("range",3),("orderID",20),("volEntered",3),("minVolume",3),
        ("bid",3),("issueDate",64),("duration",3),("stationID",3),("regionID",3),("solarSystemID",3),("constellationID",3),("jumps",3)];
    private static readonly PyValue Header = new PyObjectEx(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"),Py.Tuple(Py.Tuple([.. Columns.Select(c => (PyValue)Py.Tuple(new PyStr(c.Name),new PyInt(c.Type)))]))),[],[]);
    public static PyValue Orders(IEnumerable<MarketOrder> orders,MarketTopology topology,MarketLocation viewer) => new PyObject(
        new PyStr("eve.common.script.sys.rowset.Rowset"),Py.Dict(
            ("header",Header),("columns",new PyList([.. Columns.Select(c => (PyValue)new PyStr(c.Name))])),
            ("RowClass",new PyToken("blue.DBRow")),
            ("lines",new PyList([.. orders.Select(o => (PyValue)Py.Tuple(
                Py.Number((double)o.Price),new PyInt(o.VolumeRemaining),new PyInt(o.TypeId),new PyInt(MarketRules.RangeForClient(o.Range)),new PyLong(o.Id),
                new PyInt(o.VolumeEntered),new PyInt(o.MinimumVolume),new PyInt(o.Bid ? 1 : 0),new PyLong(Py.FileTimeFromUnixMs(o.IssuedMs)),
                new PyInt(o.Duration),new PyInt(o.Location.StationId),new PyInt(o.Location.RegionId),new PyInt(o.Location.SolarSystemId),
                new PyInt(o.Location.ConstellationId),new PyInt(viewer.SolarSystemId>0 ? Math.Max(0,topology.OrderDistance(viewer,o.Location)) : 0)))]))));
}


internal static class MarketHistoryWire
{
    private static readonly (string Name,int Type)[] Columns = [
        ("historyDate",64),("lowPrice",5),("highPrice",5),("avgPrice",5),("volume",20),("orders",3)];
    private static readonly PyValue Header = new PyObjectEx(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"),Py.Tuple(Py.Tuple([.. Columns.Select(c => (PyValue)Py.Tuple(new PyStr(c.Name),new PyInt(c.Type)))]))),[],[]);

    public static PyValue Rowset(IEnumerable<MarketHistory> rows) => new PyObject(
        new PyStr("eve.common.script.sys.rowset.Rowset"),Py.Dict(
            ("header",Header),("columns",new PyList([.. Columns.Select(c => (PyValue)new PyStr(c.Name))])),
            ("RowClass",new PyToken("blue.DBRow")),
            ("lines",new PyList([.. rows.Select(row => (PyValue)Py.Tuple(
                new PyLong(Py.FileTimeFromUnixMs(checked(row.UnixDay*86_400_000))),Py.Number((double)row.Low),
                Py.Number((double)row.High),Py.Number((double)row.Average),new PyLong(row.Volume),new PyInt(row.Orders)))]))));

    public static (PyValue Old,PyValue New) Pair(IReadOnlyList<MarketHistory> rows)
    {
        var oldCount=Math.Max(0,rows.Count-1);
        return (Rowset(rows.Take(oldCount)),Rowset(rows.Skip(oldCount)));
    }
}
