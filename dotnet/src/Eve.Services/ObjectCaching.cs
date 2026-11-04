using Eve.Marshal;

namespace Eve.Services;

/// <summary>
/// The answer to a call whose result the client keeps: <c>CachedMethodCallResult</c>. The client remembers the result under the
/// call's service, method and arguments and does not ask again for as long as <c>versionCheck</c> says ("run": for the
/// life of the client), so the version it carries only has to change when the result does.
/// </summary>
public static class ObjectCaching
{
    public const string ResultClass = "carbon.common.script.net.objectCaching.CachedMethodCallResult";

    /// <summary>
    /// <c>({versionCheck[, sessionInfo]}, substream(result), [filetime, adler32(marshal(result))])</c>.
    /// </summary>
    public static PyValue MethodResult(PyValue result, long nowUnixMs, string versionCheck = "run", string? sessionInfo = null)
    {
        List<KeyValuePair<PyValue, PyValue>> details = [new(Raw("versionCheck"), Raw(versionCheck))];
        if (sessionInfo is not null)
        {
            details.Add(new(Raw("sessionInfo"), Raw(sessionInfo)));
        }
        var version = new PyList([new PyLong(Py.FileTimeFromUnixMs(nowUnixMs)), new PyInt(Checksums.SignedAdler32(MarshalCodec.Encode(result)))]);
        return new PyObject(new PyStr(ResultClass), Py.Tuple(new PyDict(details), new PySubStream(result), version));
    }

    /// <summary>A text that is written as itself, not as a string-table entry.</summary>
    private static PyValue Raw(string text) => new PyBytes(System.Text.Encoding.ASCII.GetBytes(text));
}

/// <summary>The checksums the client's caches compare.</summary>
public static class Checksums
{

    public static int SignedAdler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % Mod;
            b = (b + a) % Mod;
        }
        return unchecked((int)((b << 16) | a));
    }
}
