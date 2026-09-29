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

    public static PyList List(params PyValue[] items) => new(items);

    public static PyTuple Tuple(params PyValue[] items) => new(items);

    /// <summary>A Windows FILETIME as the long the client expects.</summary>
    public static PyValue FileTimeLong(long fileTime) => new PyLong(fileTime);

    private const long UnixEpochAsFileTime = 116444736000000000L;
    private const long TicksPerMillisecond = 10000L;

    /// <summary>The FILETIME of a Unix time in milliseconds (<c>currentFileTime</c> for a given clock).</summary>
    public static long FileTimeFromUnixMs(long unixMs) => (unixMs * TicksPerMillisecond) + UnixEpochAsFileTime;
}
