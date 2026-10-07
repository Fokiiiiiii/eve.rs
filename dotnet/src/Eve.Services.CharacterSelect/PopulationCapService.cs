using System.Security.Cryptography;
using System.Text;

using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect;

[Service("populationCap")]
public sealed partial class PopulationCapService(PgCharacterStore characters)
{
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall _) => new PyInt(MachoMessage.ProxyNodeId);

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        var bound = new PySubStruct(new PySubStream(Py.Tuple(
            new PyStr(oid),
            new PyLong(Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested
            && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            var traceLogin = Environment.GetEnvironmentVariable("EVE_TRACE_LOGIN") == "1";
            var args = nested.Items.Count > 1
                ? nested.Items[1] switch { PyTuple tuple => tuple.Items, PyList list => list.Items, _ => [] }
                : [];
            if (traceLogin)
            {
                Console.Error.WriteLine($"[login-trace] populationCap nested request method={method} args={args.Count}");
            }
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = args,
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                BoundObjectParameters = call.Arg(0),
                Cancellation = call.Cancellation,
            });
            if (traceLogin)
            {
                Console.Error.WriteLine($"[login-trace] populationCap nested response method={method} result={result.GetType().Name}");
            }
        }
        return Py.Tuple(bound, result);
    }

    [Method("GetCharacterLoadSlot")]
    public async ValueTask<PyValue> GetCharacterLoadSlot(ServiceCall call)
    {
        var boundCharacterId = CharacterId(call.BoundObjectParameters);
        var requestedCharacterId = CharacterId(call.Arg(0));
        if (boundCharacterId is { } bound && requestedCharacterId is { } requested && bound != requested)
            return new PyDict([]);
        if (call.Session.UserId is not { } userId
            || (requestedCharacterId ?? boundCharacterId) is not { } characterId)
            return new PyDict([]);

        var character = await characters.GetAsync(characterId, call.Cancellation);
        if (character is null || character.AccountId != userId)
            return new PyDict([]);

        var solarSystemId = character.SolarSystemId > 0
            ? character.SolarSystemId
            : call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 30000142;
        return new PyDict([new KeyValuePair<PyValue, PyValue>(
            new PyInt(solarSystemId), new PyStr(LoadSlotKey(character.Id, solarSystemId)))]);
    }

    private static string LoadSlotKey(long characterId, long solarSystemId)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{characterId}:{solarSystemId}:populationCap")))
            .ToLowerInvariant();
        var variant = (Convert.ToByte(hash.Substring(16, 2), 16) & 0x3f) | 0x80;
        return $"{hash[..8]}-{hash.Substring(8, 4)}-4{hash.Substring(13, 3)}-{variant:x2}{hash.Substring(18, 2)}-{hash.Substring(20, 12)}";
    }

    private static long? CharacterId(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyTuple tuple when tuple.Items.Count > 0 => CharacterId(tuple.Items[0]),
        PyList list when list.Items.Count > 0 => CharacterId(list.Items[0]),
        _ => null,
    };
}
