using Eve.Marshal;

namespace Eve.Services;

/// <summary>Builders for the value shapes services return (the reference's <c>serviceHelpers</c>).</summary>
public static class Py
{
    public static PyDict Dict(params (string Key, PyValue Value)[] entries) =>
        new([.. entries.Select(e => new KeyValuePair<PyValue, PyValue>(new PyStr(e.Key), e.Value))]);

    /// <summary><c>util.KeyVal</c>: an object whose attributes the client reads by name.</summary>
    public static PyValue KeyVal(params (string Key, PyValue Value)[] entries) => new PyObject(new PyStr("util.KeyVal"), Dict(entries));

    public static PyValue KeyVal(IEnumerable<KeyValuePair<PyValue, PyValue>> entries) => new PyObject(new PyStr("util.KeyVal"), new PyDict([.. entries]));

    /// <summary>A <c>Rowset</c>: named columns and their rows (the client's table type for lists of records).</summary>
    public static PyValue Rowset(IReadOnlyList<string> columns, IEnumerable<PyValue> lines) => new PyObject(
        new PyStr("eve.common.script.sys.rowset.Rowset"),
        new PyDict([
            new(new PyStr("header"), new PyList([.. columns.Select(c => (PyValue)new PyStr(c))])),
            new(new PyStr("columns"), new PyList([.. columns.Select(c => (PyValue)new PyStr(c))])),
            new(new PyStr("RowClass"), new PyToken("util.Row")),
            new(new PyStr("lines"), new PyList([.. lines])),
        ]));

    /// <summary>A number as JavaScript's marshal writes it: a whole number is an integer and anything else a real.</summary>
    public static PyValue Number(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 9_007_199_254_740_992d ? new PyInt((long)value) : new PyReal(value);

    public static PyList List(params PyValue[] items) => new(items);

    public static PyTuple Tuple(params PyValue[] items) => new(items);

    /// <summary>A Windows FILETIME as the long the client expects.</summary>
    public static PyValue FileTimeLong(long fileTime) => new PyLong(fileTime);

    private const long UnixEpochAsFileTime = 116444736000000000L;
    private const long TicksPerMillisecond = 10000L;

    /// <summary>The FILETIME of a Unix time in milliseconds (<c>currentFileTime</c> for a given clock).</summary>
    public static long FileTimeFromUnixMs(long unixMs) => (unixMs * TicksPerMillisecond) + UnixEpochAsFileTime;
}
