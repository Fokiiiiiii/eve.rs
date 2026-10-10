using System.Security.Cryptography;
using System.Text;

using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect;

/// <summary>
/// The population cap of the selection screen (<c>Moniker('populationCap', (charID, groupCharacter))</c>). The client asks
/// where a character may load (<c>GetCharacterLoadSlot</c>): one entry when its system has room, which the client loads
/// without asking; several entries when the system is full, from which the pilot chooses (<c>MoveCharacterToNewSystem</c>).
/// The answer comes from <see cref="SystemPopulation"/>, the characters in the game now. A chosen system is saved on the
/// character, so the next load starts there. A docked character (a station on record) is never moved: when its system is
/// full the selection is refused.
/// </summary>
[Service("populationCap")]
public sealed partial class PopulationCapService(PgCharacterStore characters, SystemPopulation population)
{
    /// <summary>The system a character loads into when none is on record (the reference's preselection).</summary>
    private const int DefaultLoadSystemId = 30000142;

    private sealed record Decision(IReadOnlyList<int> Systems);

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

    /// <summary>
    /// <c>{solarSystemID: slotKey}</c> for the systems the character may load into. The slot key is a stable token that
    /// <c>MoveCharacterToNewSystem</c> must echo back. A single system that is not the character's own is saved to the
    /// character here, because the client loads a single entry without a further call.
    /// </summary>
    [Method("GetCharacterLoadSlot")]
    public async ValueTask<PyValue> GetCharacterLoadSlot(ServiceCall call)
    {
        if (await OwnedCharacterAsync(call) is not { } character)
            return new PyDict([]);
        var home = HomeOf(character, call.Session);
        var systems = Decide(character, home).Systems;
        if (systems.Count == 1 && systems[0] != character.SolarSystemId)
            await MoveAsync(character, systems[0], call.Cancellation);
        return Slots(character.Id, systems);
    }

    /// <summary>Saves the system the pilot chose. The slot key must be the one <c>GetCharacterLoadSlot</c> gave for it.</summary>
    [Method("MoveCharacterToNewSystem")]
    public async ValueTask<PyValue> MoveCharacterToNewSystem(ServiceCall call)
    {
        if (await OwnedCharacterAsync(call) is not { } character)
            throw Info("That character is not available on this account.");
        var target = Number(call.Arg(1)) is { } systemId and > 0 ? systemId : 0;
        var slotKey = JsSemantics.StringValue(call.Arg(2));
        var offered = Decide(character, HomeOf(character, call.Session)).Systems;
        if (target == 0 || !offered.Contains((int)target) || slotKey != LoadSlotKey(character.Id, target))
            throw Info("That solar system is not offered for this character.");
        await MoveAsync(character, (int)target, call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>The systems the character may load into now, or the refusal when no system takes it.</summary>
    private Decision Decide(Character character, long home)
    {
        if (population.HasRoom(home, character.Id))
            return new([(int)home]);
        if (character.StationId > 0)
            throw Full();
        var alternatives = population.Alternatives((int)home, character.Id);
        return alternatives.Count == 0 ? throw Full() : new(alternatives);
    }

    private async ValueTask MoveAsync(Character character, int solarSystemId, CancellationToken cancellationToken)
    {
        var system = population.Get(solarSystemId)
            ?? throw Info("That solar system is unknown.");
        await characters.SetSolarSystemAsync(character.Id, system.Id, system.ConstellationId, system.RegionId, cancellationToken);
    }

    private static MachoWrappedException Full() =>
        Info("No solar system near this one has room for another pilot. Try again later.");

    private static MachoWrappedException Info(string text) => MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(text)));

    private static PyDict Slots(long characterId, IReadOnlyList<int> systems) => new([
        .. systems.Select(system => new KeyValuePair<PyValue, PyValue>(new PyInt(system), new PyStr(LoadSlotKey(characterId, system)))),
    ]);

    private long HomeOf(Character character, ClientSession session) =>
        character.SolarSystemId > 0
            ? character.SolarSystemId
            : session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? DefaultLoadSystemId;

    /// <summary>The character the call is about, when it belongs to the calling account; null otherwise.</summary>
    private async ValueTask<Character?> OwnedCharacterAsync(ServiceCall call)
    {
        var boundCharacterId = CharacterId(call.BoundObjectParameters);
        var requestedCharacterId = CharacterId(call.Arg(0));
        if (boundCharacterId is { } bound && requestedCharacterId is { } requested && bound != requested)
            return null;
        if (call.Session.UserId is not { } userId
            || (requestedCharacterId ?? boundCharacterId) is not { } characterId)
            return null;

        var character = await characters.GetAsync(characterId, call.Cancellation);
        return character is null || character.AccountId != userId ? null : character;
    }

    private static string LoadSlotKey(long characterId, long solarSystemId)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{characterId}:{solarSystemId}:populationCap")))
            .ToLowerInvariant();
        var variant = (Convert.ToByte(hash.Substring(16, 2), 16) & 0x3f) | 0x80;
        return $"{hash[..8]}-{hash.Substring(8, 4)}-4{hash.Substring(13, 3)}-{variant:x2}{hash.Substring(18, 2)}-{hash.Substring(20, 12)}";
    }

    private static long? Number(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static long? CharacterId(PyValue? value) => value switch
    {
        PyTuple tuple when tuple.Items.Count > 0 => Number(tuple.Items[0]),
        PyList list when list.Items.Count > 0 => Number(list.Items[0]),
        _ => Number(value),
    };
}
