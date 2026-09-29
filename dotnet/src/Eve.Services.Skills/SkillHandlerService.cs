using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>
/// What the client asks about its character's skills (<c>skillHandler</c>, which it reaches through the moniker
/// <c>skillMgr2.GetMySkillHandler</c>). Only reading is here: training, the queue and injectors come later.
/// </summary>
[Service("skillHandler", Aliases = ["skillMgr"])]
public sealed partial class SkillHandlerService(PgSkillStore store, SkillCatalog catalog, Func<long> nowUnixMs)
{
    /// <summary>The five attributes a character trains with (charisma, intelligence, memory, perception, willpower) all start at 20.</summary>
    private static readonly int[] AttributeIds = [164, 165, 166, 167, 168];

    private const int BaseAttribute = 20;
    private const int FreeRespecs = 3;

    private static PyValue None => PyNone.Instance;

    private static long? CharacterOf(ServiceCall call) => call.Session.CharacterId;

    private async ValueTask<IReadOnlyList<CharacterSkill>> SkillsOf(ServiceCall call, long characterId) =>
        await store.ForCharacterAsync(characterId, call.Cancellation);

    [Method("GetSkills")]
    public async ValueTask<PyValue> GetSkills(ServiceCall call) =>
        CharacterOf(call) is { } id ? SkillWire.EntryDictionary(id, catalog, await SkillsOf(call, id)) : None;

    /// <summary>The skills including lapsed ones (a character that let its subscription lapse): the same until subscriptions exist.</summary>
    [Method("GetAllSkills")]
    public ValueTask<PyValue> GetAllSkills(ServiceCall call) => GetSkills(call);

    [Method("GetSkillPoints")]
    public async ValueTask<PyValue> GetSkillPoints(ServiceCall call) =>
        CharacterOf(call) is { } id ? new PyInt((await SkillsOf(call, id)).Sum(s => s.SkillPoints)) : None;

    [Method("GetFreeSkillPoints")]
    public PyValue GetFreeSkillPoints(ServiceCall call) => new PyInt(0);

    [Method("GetSkillQueue")]
    public PyValue GetSkillQueue(ServiceCall call) => new PyList([]);

    [Method("GetMySkillQueue")]
    public PyValue GetMySkillQueue(ServiceCall call) => new PyList([]);

    [Method("GetSkillQueueAndFreePoints")]
    public PyValue GetSkillQueueAndFreePoints(ServiceCall call) => Py.Tuple(new PyList([]), new PyInt(0));

    [Method("GetAttributes")]
    public PyValue GetAttributes(ServiceCall call) => new PyDict([.. AttributeIds.Select(id => new KeyValuePair<PyValue, PyValue>(new PyInt(id), new PyInt(BaseAttribute)))]);

    [Method("GetCharacterAttributeModifiers")]
    public PyValue GetCharacterAttributeModifiers(ServiceCall call) => new PyList([]);

    [Method("GetBoosters")]
    public PyValue GetBoosters(ServiceCall call) => new PyDict([]);

    [Method("GetImplants")]
    public PyValue GetImplants(ServiceCall call) => new PyDict([]);

    [Method("GetSkillHistory")]
    public PyValue GetSkillHistory(ServiceCall call) => new PyList([]);

    [Method("GetRespecInfo")]
    public PyValue GetRespecInfo(ServiceCall call) => Py.KeyVal(("freeRespecs", new PyInt(FreeRespecs)), ("lastRespecDate", None), ("nextTimedRespec", None));

    [Method("GetFreeSkillPointsAppliedToQueue")]
    public PyValue GetFreeSkillPointsAppliedToQueue(ServiceCall call) => new PyDict([]);

    [Method("GetFreeSkillPointsAppliedToSkills")]
    public PyValue GetFreeSkillPointsAppliedToSkills(ServiceCall call) => new PyDict([]);

    /// <summary>Everything the skill window opens with, in one call.</summary>
    [Method("GetMySkillInfo")]
    public async ValueTask<PyValue> GetMySkillInfo(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return None;
        }
        var skills = await SkillsOf(call, id);
        return Py.KeyVal(
            ("skills", SkillWire.EntryDictionary(id, catalog, skills)),
            ("skillPoints", new PyInt(skills.Sum(s => s.SkillPoints))),
            ("freeSkillPoints", new PyInt(0)),
            ("queue", new PyList([])));
    }

    [Method("IsSkillInjected")]
    public async ValueTask<PyValue> IsSkillInjected(ServiceCall call) =>
        CharacterOf(call) is { } id && call.Arg(0) is PyInt type
            ? PyValue.Of((await SkillsOf(call, id)).Any(s => s.TypeId == type.Value))
            : PyBool.False;

    [Method("IsSkill")]
    public PyValue IsSkill(ServiceCall call) => PyValue.Of(call.Arg(0) is PyInt type && type.Value is >= 0 and <= int.MaxValue && catalog.Get((int)type.Value) is not null);

    /// <summary>The proxy node that serves this object: the one the client is connected to.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>
    /// Binds the caller to this handler: <c>(bindParams, (method, args, kwargs))</c> answers <c>(bound object, result of the
    /// nested call)</c>, so the client's first call and its bind are one round trip.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = Py.Tuple(new PyStr($"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}"), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())));
        PyValue result = None;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = nested.Items.Count > 1 && nested.Items[1] is PyTuple or PyList ? ItemsOf(nested.Items[1]) : [],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : None,
                Session = call.Session,
                CallId = call.CallId,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(new PySubStruct(new PySubStream(oid)), result);
    }

    private static IReadOnlyList<PyValue> ItemsOf(PyValue value) => value switch
    {
        PyTuple tuple => tuple.Items,
        PyList list => list.Items,
        _ => [],
    };
}
