using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>
/// What the client asks about its character's skills (<c>skillHandler</c>, which it reaches through the moniker
/// <c>skillMgr2.GetMySkillHandler</c>). Only reading is here: training, the queue and injectors come later.
/// </summary>
[Service("skillHandler", Aliases = ["skillMgr"])]
public sealed partial class SkillHandlerService(
    PgSkillStore store, SkillCatalog catalog, CharacterTraining training, PgCharacterStore characters, TrainingAnnouncer announcer, Func<long> nowUnixMs)
{
    /// <summary>The five attributes a character trains with (charisma, intelligence, memory, perception, willpower).</summary>
    private static readonly int[] AttributeIds = [164, 165, 166, 167, 168];

    private const int FreeRespecs = 3;

    private static PyValue None => PyNone.Instance;

    private static long? CharacterOf(ServiceCall call) => call.Session.CharacterId;

    private static long AccountOf(ServiceCall call) => call.Session.UserId ?? 0;

    /// <summary>The character's skills and queue as they are now (what has finished is settled first).</summary>
    private async ValueTask<TrainingView?> ViewOf(ServiceCall call) =>
        CharacterOf(call) is { } id ? await training.ViewAsync(id, AccountOf(call), call.Cancellation) : null;

    private async ValueTask<IReadOnlyList<long>> AccountCharacters(ServiceCall call) =>
        call.Session.UserId is { } account ? [.. (await characters.ForAccountAsync((int)account, call.Cancellation)).Select(c => c.Id)] : [];

    [Method("GetSkills")]
    public async ValueTask<PyValue> GetSkills(ServiceCall call) =>
        await ViewOf(call) is { } view ? SkillWire.EntryDictionary(CharacterOf(call)!.Value, catalog, view.Skills, view.InTraining) : None;

    /// <summary>The skills including lapsed ones (a character that let its subscription lapse): the same until subscriptions exist.</summary>
    [Method("GetAllSkills")]
    public ValueTask<PyValue> GetAllSkills(ServiceCall call) => GetSkills(call);

    [Method("GetSkillPoints")]
    public async ValueTask<PyValue> GetSkillPoints(ServiceCall call) => await ViewOf(call) is { } view ? new PyInt(view.TotalPoints) : None;

    [Method("GetFreeSkillPoints")]
    public PyValue GetFreeSkillPoints(ServiceCall call) => new PyInt(0);

    [Method("GetSkillQueue")]
    public async ValueTask<PyValue> GetSkillQueue(ServiceCall call) => SkillNotifications.Queue((await ViewOf(call))?.Queue ?? []);

    [Method("GetMySkillQueue")]
    public ValueTask<PyValue> GetMySkillQueue(ServiceCall call) => GetSkillQueue(call);

    [Method("GetSkillQueueAndFreePoints")]
    public async ValueTask<PyValue> GetSkillQueueAndFreePoints(ServiceCall call) =>
        Py.Tuple(SkillNotifications.Queue((await ViewOf(call))?.Queue ?? []), new PyInt(0));

    /// <summary>
    /// The learning attributes. The client works its training-time estimates out from them, so they carry the server's training speed (the
    /// reference does the same; at the game's own speed they are what they are).
    /// </summary>
    [Method("GetAttributes")]
    public PyValue GetAttributes(ServiceCall call)
    {
        var character = CharacterOf(call) ?? 0;
        return new PyDict([.. AttributeIds.Select(id => new KeyValuePair<PyValue, PyValue>(new PyInt(id), Py.Number(training.Settings.Attribute(character, id) * training.Settings.Speed)))]);
    }

    [Method("GetCharacterAttributeModifiers")]
    public PyValue GetCharacterAttributeModifiers(ServiceCall call) => new PyList([]);

    [Method("GetBoosters")]
    public PyValue GetBoosters(ServiceCall call) => new PyDict([]);

    [Method("GetImplants")]
    public PyValue GetImplants(ServiceCall call) => new PyDict([]);

    [Method("GetSkillHistory")]
    public async ValueTask<PyValue> GetSkillHistory(ServiceCall call) =>
        CharacterOf(call) is { } id ? SkillNotifications.History(await training.HistoryAsync(id, call.Cancellation)) : new PyList([]);

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
        if (await ViewOf(call) is not { } view)
        {
            return None;
        }
        return Py.KeyVal(
            ("skills", SkillWire.EntryDictionary(CharacterOf(call)!.Value, catalog, view.Skills, view.InTraining)),
            ("skillPoints", new PyInt(view.TotalPoints)),
            ("freeSkillPoints", new PyInt(0)),
            ("queue", SkillNotifications.Queue(view.Queue)));
    }

    [Method("IsSkillInjected")]
    public async ValueTask<PyValue> IsSkillInjected(ServiceCall call) =>
        CharacterOf(call) is { } id && call.Arg(0) is PyInt type
            ? PyValue.Of((await store.ForCharacterAsync(id, call.Cancellation)).Any(s => s.TypeId == type.Value))
            : PyBool.False;

    [Method("IsSkill")]
    public PyValue IsSkill(ServiceCall call) => PyValue.Of(call.Arg(0) is PyInt type && type.Value is >= 0 and <= int.MaxValue && catalog.Get((int)type.Value) is not null);

    /// <summary>
    /// <c>SaveNewQueue(queue, activate=True)</c>: replaces the queue. It is a dict of position to <c>(skill, level)</c>. The client is told
    /// the queue as it was kept; a queue the rules refuse is answered with the message the client shows.
    /// </summary>
    [Method("SaveNewQueue")]
    public async ValueTask<PyValue> SaveNewQueue(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return None;
        }
        var activate = call.Kwarg("activate") is { } given ? JsSemantics.Truthy(given) : true;
        var outcome = await training.SaveQueueAsync(id, AccountOf(call), QueueInput(call.Arg(0)), activate, await AccountCharacters(call), call.Cancellation);
        return await Answer(call, outcome);
    }

    /// <summary><c>CharStartTrainingSkillByTypeID(typeID, toLevel=None)</c>: puts a skill first in the queue and runs it.</summary>
    [Method("CharStartTrainingSkillByTypeID")]
    public async ValueTask<PyValue> CharStartTrainingSkillByTypeID(ServiceCall call) => await StartAsync(call, call.Arg(0), call.Arg(1));

    /// <summary>
    /// <c>CharStartTrainingSkill(itemID, toLevel=None)</c>: the same for a skill named by its item id (the character's id and the type, see
    /// <see cref="SkillWire.ItemId"/>). An item that is not one of the character's skills starts nothing.
    /// </summary>
    [Method("CharStartTrainingSkill")]
    public async ValueTask<PyValue> CharStartTrainingSkill(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id || IdOf(call.Arg(0)) is not { } item || item / 100_000 != id)
        {
            return None;
        }
        return await StartAsync(call, new PyInt(item % 100_000), call.Arg(1));
    }

    private async ValueTask<PyValue> StartAsync(ServiceCall call, PyValue type, PyValue level)
    {
        if (CharacterOf(call) is not { } id || IdOf(type) is not { } typeId || typeId > int.MaxValue)
        {
            return None;
        }
        var toLevel = IdOf(level) is { } asked and > 0 and <= int.MaxValue ? (int)asked : 0;
        var outcome = await training.StartAsync(id, AccountOf(call), (int)typeId, toLevel, await AccountCharacters(call), call.Cancellation);
        return outcome is null ? None : await Answer(call, outcome);
    }

    private async ValueTask<PyValue> Answer(ServiceCall call, SaveOutcome outcome)
    {
        if (outcome is not QueueSaved saved)
        {
            throw SkillErrors.Of(outcome);
        }
        await announcer.AnnounceSavedAsync(call.Session, saved);
        return None;
    }

    /// <summary><c>CharStopTrainingSkill()</c>: the same as <c>AbortTraining()</c>.</summary>
    [Method("CharStopTrainingSkill")]
    public ValueTask<PyValue> CharStopTrainingSkill(ServiceCall call) => AbortTraining(call);

    /// <summary><c>AbortTraining()</c>: stops the running skill; the queue stays and can be started again.</summary>
    [Method("AbortTraining")]
    public async ValueTask<PyValue> AbortTraining(ServiceCall call)
    {
        if (CharacterOf(call) is { } id && await training.AbortAsync(id, AccountOf(call), call.Cancellation) is { } update)
        {
            await announcer.AnnounceStoppedAsync(call.Session, update);
        }
        return None;
    }

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal real when real.Value == Math.Floor(real.Value) => (long)real.Value,
        _ => null,
    };

    /// <summary>
    /// The lines the client sent, in queue order: a dict of position to <c>(skill, level)</c> (a list is taken as positions from 0). A line
    /// that names no skill or no level, or a negative position, is left out.
    /// </summary>
    private static List<QueueEntry> QueueInput(PyValue queue)
    {
        var lines = new List<(long Position, QueueEntry Entry)>();
        void Add(long position, PyValue value)
        {
            var (type, level) = value switch
            {
                PyTuple { Items.Count: >= 2 } pair => (IdOf(pair.Items[0]), IdOf(pair.Items[1])),
                PyList { Items.Count: >= 2 } pair => (IdOf(pair.Items[0]), IdOf(pair.Items[1])),
                PyDict dict => (Named(dict, "trainingTypeID", "typeID"), Named(dict, "trainingToLevel", "toLevel")),
                _ => (null, null),
            };
            if (position >= 0 && type is > 0 and <= int.MaxValue && level is > 0 and <= int.MaxValue)
            {
                lines.Add((position, new QueueEntry((int)type, (int)level)));
            }
        }
        switch (queue)
        {
            case PyDict dict:
                foreach (var (key, value) in dict.Entries)
                {
                    Add(IdOf(key) ?? -1, value);
                }
                break;
            case PyList list:
                for (var i = 0; i < list.Items.Count; i++)
                {
                    Add(i, list.Items[i]);
                }
                break;
            case PyTuple tuple:
                for (var i = 0; i < tuple.Items.Count; i++)
                {
                    Add(i, tuple.Items[i]);
                }
                break;
        }
        return [.. lines.OrderBy(l => l.Position).Select(l => l.Entry)];
    }

    private static long? Named(PyDict dict, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var (key, value) in dict.Entries)
            {
                if (JsSemantics.StringValue(key) == name)
                {
                    return IdOf(value);
                }
            }
        }
        return null;
    }

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
