using System.Text.Json.Nodes;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Rig.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

internal sealed class Capture : IPacketSink
{
    public List<PyValue> Sent { get; } = [];

    public void Send(PyValue packet) => Sent.Add(packet);
}

/// <summary>The skill services over a scratch schema and a small catalog.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    public const long ClockMs = 1_700_000_000_123;

    public static readonly SkillCatalog Catalog = new(
        [new SkillType(3300, "Gunnery", 255, "Gunnery", true, 1), new SkillType(3301, "Small Hybrid Turret", 255, "Gunnery", true, 1), new SkillType(3315, "Repair Systems", 1210, "Armor", false, 4)],
        new Dictionary<int, IReadOnlyList<StartingSkill>>());

    private readonly string _schema = $"sks_{Guid.NewGuid():N}"[..20];

    private Rig(NpgsqlDataSource source, TrainingSettings? settings = null)
    {
        Source = source;
        Store = new PgSkillStore(source, _schema);
        Training = new CharacterTraining(source, Catalog, Store, new PgTrainingStore(source, _schema), settings ?? new TrainingSettings(), () => Now);
        Sessions = new CharacterSessions();
        Announcer = new TrainingAnnouncer(Training, Catalog, Sessions, null, () => Now, schedule: false);
        Announcer.Attach();
        Handler = new SkillHandlerService(Store, Catalog, Training, new Eve.Characters.PgCharacterStore(source, _schema + "c"), Announcer, () => Now);
        Door = new SkillMgr2Service();
        Hook = new SkillsLoginHook(Training, Catalog, Sessions, Announcer, () => Now);
    }

    /// <summary>The clock of the rig: it stands still until a test moves it.</summary>
    public long Now = ClockMs;

    public void Wait(TimeSpan time) => Now += (long)time.TotalMilliseconds;

    public CharacterTraining Training { get; }

    public CharacterSessions Sessions { get; }

    public TrainingAnnouncer Announcer { get; }

    public NpgsqlDataSource Source { get; }

    public PgSkillStore Store { get; }

    public SkillHandlerService Handler { get; }

    public SkillMgr2Service Door { get; }

    public SkillsLoginHook Hook { get; }

    public static async Task<Rig> CreateAsync(TrainingSettings? settings = null)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var rig = new Rig(source, settings);
        await PgSkillStore.MigrateAsync(source, rig._schema);
        await Eve.Characters.PgCharacterStore.MigrateAsync(source, rig._schema + "c");
        return rig;
    }

    public async Task GrantAsync(long characterId, params (int Type, int Level, long Points)[] skills)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Store.GrantAsync(connection, transaction, characterId, skills.Select(s => new CharacterSkill(s.Type, s.Level, s.Points)).ToList());
        await transaction.CommitAsync();
    }

    public static (ClientSession Session, Capture Sink) Session(long? characterId, long userId = 34)
    {
        var sink = new Capture();
        var session = new ClientSession(sink) { SessionId = 26_859_804_000_001, ClientId = 34_065_450, UserId = userId };
        if (characterId is { } id)
        {
            session.CharacterId = id;
        }
        return (session, sink);
    }

    public static async Task<PyValue> CallAsync(IService service, ClientSession session, string method, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    public async ValueTask DisposeAsync()
    {
        await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE; DROP SCHEMA IF EXISTS {_schema}c CASCADE");
        await command.ExecuteNonQueryAsync();
        await Source.DisposeAsync();
    }
}

public class SkillWireTests
{
    [Fact]
    public void A_skill_item_id_is_made_from_the_character_and_the_type_as_the_reference_makes_it()
    {
        Assert.Equal(14_000_003_803_300, SkillWire.ItemId(140_000_038, 3300)); // recorded
        Assert.Equal(14_000_007_403_315, SkillWire.ItemId(140_000_074, 3315)); // recorded
        Assert.NotEqual(SkillWire.ItemId(140_000_038, 3301), SkillWire.ItemId(140_000_039, 3301));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_000)]
    public void A_type_that_would_run_into_the_next_characters_ids_is_refused(int typeId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillWire.ItemId(1, typeId));

    [Fact]
    public void The_notification_carries_the_bytes_the_reference_sent_for_the_same_skill()
    {
        // OnServerSkillsChanged as recorded from the reference for character 140000038: Gunnery at level 4, 45255 points
        var recorded = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", "login_to_character.json")))!;
        var packet = recorded["afterSelect"]![1]!["$tuple"]!;
        var buffer = Convert.FromHexString((string)packet[4]!["$tuple"]![0]!["$tuple"]![1]!["$buffer"]!);

        var decoded = (PyTuple)MarshalCodec.Decode(buffer);
        var time = ((PyLong)((PyTuple)((PyTuple)decoded.Items[1]).Items[1]).Items[2]).Value;

        var entry = SkillWire.EntryDictionary(140_000_038, Rig.Catalog, [new CharacterSkill(3300, 4, 45_255)]);
        var built = ClientNotification.Build(34, MachoMessage.ProxyNodeId, "OnServerSkillsChanged", "charid", [entry, PyNone.Instance, new PyLong(time)], 0);

        var sent = (PyTuple)((PyObject)built).Args;
        var inner = (PyBuffer)((PyTuple)((PyTuple)sent.Items[4]).Items[0]).Items[1];
        Assert.Equal(buffer, inner.Value.ToArray());
    }
}

public class ClientNotificationTests
{
    [Fact]
    public void A_notification_has_the_shape_the_reference_sends_and_counts_from_zero()
    {
        var (session, sink) = Rig.Session(140_000_038);
        session.SendNotification("OnServerSkillsChanged", "charid", new PyInt(1), PyNone.Instance);
        session.SendNotification("OnServerSkillsChanged", "charid", new PyInt(2));

        var first = (PyTuple)((PyObject)sink.Sent[0]).Args;
        Assert.Equal(14, first.Items.Count);
        Assert.Equal(new PyInt(12), first.Items[0]);
        Assert.Equal(new PyTuple([new PyInt(1), new PyInt(65_450), PyNone.Instance, PyNone.Instance]), ((PyObject)first.Items[1]).Args);
        Assert.Equal(new PyTuple([new PyInt(4), new PyStr("OnServerSkillsChanged"), new PyList([]), new PyStr("charid")]), ((PyObject)first.Items[2]).Args);
        Assert.Equal(new PyInt(34), first.Items[3]);
        Assert.Equal(new PyDict([new(new PyStr("sn"), new PyInt(0))]), first.Items[5]);
        Assert.All(first.Items.Skip(6), item => Assert.Equal(PyNone.Instance, item));
        Assert.Equal(new PyDict([new(new PyStr("sn"), new PyInt(1))]), ((PyTuple)((PyObject)sink.Sent[1]).Args).Items[5]);

        // the arguments are a marshalled stream of their own: (0, (1, args))
        var inner = (PyBuffer)((PyTuple)((PyTuple)first.Items[4]).Items[0]).Items[1];
        var decoded = (PyTuple)MarshalCodec.Decode(inner.Value.ToArray());
        Assert.Equal(new PyInt(0), decoded.Items[0]);
        var args = (PyTuple)((PyTuple)decoded.Items[1]).Items[1];
        Assert.Equal(2, args.Items.Count);
        Assert.Equal(new PyInt(1), args.Items[0]);
    }

    [Fact]
    public async Task Concurrent_notifications_are_each_numbered_once()
    {
        var (session, sink) = Rig.Session(1);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => session.SendNotification("OnX", "charid"))));
        var numbers = sink.Sent.Select(p => ((PyInt)((PyDict)((PyTuple)((PyObject)p).Args).Items[5]).Entries[0].Value).Value).ToArray();
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (long)i), numbers);
    }
}

public class SkillHandlerTests
{
    [PgFact]
    public async Task The_skills_of_a_character_come_as_entries_in_type_order_with_their_item_fields()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.GrantAsync(140_000_038, (3315, 0, 0), (3300, 4, 45_255), (3301, 1, 250));
        var (session, _) = Rig.Session(140_000_038);

        var skills = (PyDict)await Rig.CallAsync(rig.Handler, session, "GetSkills");
        Assert.Equal([3300L, 3301, 3315], skills.Entries.Select(e => ((PyInt)e.Key).Value));

        var gunnery = (PyObjectEx)skills.Entries[0].Value;
        var header = (PyTuple)gunnery.Header;
        Assert.Equal(new PyToken(SkillWire.EntryClass), header.Items[0]);
        Assert.Equal(new PyTuple([new PyInt(3300), new PyInt(4), new PyInt(45_255), new PyInt(1), PyNone.Instance]), header.Items[1]);
        var state = ((PyDict)header.Items[2]).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(["itemID", "ownerID", "locationID", "flagID", "groupID", "categoryID", "groupName", "published", "inTraining"], ((PyDict)header.Items[2]).Entries.Select(e => JsSemantics.StringValue(e.Key)!));
        Assert.Equal(new PyLong(14_000_003_803_300), state["itemID"]);
        Assert.Equal((new PyInt(140_000_038), new PyInt(140_000_038), new PyInt(7), new PyInt(255), new PyInt(16)), ((PyValue)state["ownerID"], state["locationID"], state["flagID"], state["groupID"], state["categoryID"]));
        Assert.Equal((new PyStr("Gunnery"), PyBool.True, PyBool.False), ((PyValue)state["groupName"], state["published"], state["inTraining"]));

        // an unpublished skill says so, and an untrained one is there at level 0
        var repair = (PyTuple)((PyTuple)((PyObjectEx)skills.Entries[2].Value).Header).Items[1];
        Assert.Equal(new PyTuple([new PyInt(3315), new PyInt(0), new PyInt(0), new PyInt(4), PyNone.Instance]), repair);
        Assert.Equal(PyBool.False, ((PyDict)((PyTuple)((PyObjectEx)skills.Entries[2].Value).Header).Items[2]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "published").Value);
    }

    [PgFact]
    public async Task The_whole_list_is_the_same_as_the_trained_list_and_the_skill_info_holds_both_with_the_points()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.GrantAsync(7, (3300, 4, 45_255), (3301, 1, 250));
        var (session, _) = Rig.Session(7);
        var skills = await Rig.CallAsync(rig.Handler, session, "GetSkills");
        Assert.Equal(skills, await Rig.CallAsync(rig.Handler, session, "GetAllSkills"));
        Assert.Equal(new PyInt(45_505), await Rig.CallAsync(rig.Handler, session, "GetSkillPoints"));

        var info = ((PyDict)((PyObject)await Rig.CallAsync(rig.Handler, session, "GetMySkillInfo")).Args).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(skills, info["skills"]);
        Assert.Equal((new PyInt(45_505), new PyInt(0), new PyList([])), ((PyValue)info["skillPoints"], info["freeSkillPoints"], info["queue"]));
    }

    [PgFact]
    public async Task A_character_with_no_skills_has_none_and_no_points()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(9);
        Assert.Equal(new PyDict([]), await Rig.CallAsync(rig.Handler, session, "GetSkills"));
        Assert.Equal(new PyInt(0), await Rig.CallAsync(rig.Handler, session, "GetSkillPoints"));
    }

    [PgFact]
    public async Task Characters_do_not_see_each_others_skills()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.GrantAsync(1, (3300, 4, 45_255));
        await rig.GrantAsync(2, (3301, 1, 250));
        var (two, _) = Rig.Session(2);
        Assert.Equal([3301L], ((PyDict)await Rig.CallAsync(rig.Handler, two, "GetSkills")).Entries.Select(e => ((PyInt)e.Key).Value));
    }

    [PgFact]
    public async Task Without_a_character_the_skill_questions_are_answered_with_none()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(null);
        foreach (var method in new[] { "GetSkills", "GetAllSkills", "GetSkillPoints", "GetMySkillInfo" })
        {
            Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, method));
        }
    }

    [PgFact]
    public async Task The_fixed_answers_are_the_recorded_ones()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(1);
        async Task<PyValue> Ask(string method) => await Rig.CallAsync(rig.Handler, session, method);

        Assert.Equal(new PyInt(0), await Ask("GetFreeSkillPoints"));
        Assert.Equal(new PyList([]), await Ask("GetSkillQueue"));
        Assert.Equal(new PyList([]), await Ask("GetMySkillQueue"));
        Assert.Equal(new PyTuple([new PyList([]), new PyInt(0)]), await Ask("GetSkillQueueAndFreePoints"));
        Assert.Equal(new PyDict(new[] { 164, 165, 166, 167, 168 }.Select(id => new KeyValuePair<PyValue, PyValue>(new PyInt(id), new PyInt(20))).ToList()), await Ask("GetAttributes"));
        Assert.Equal(new PyList([]), await Ask("GetCharacterAttributeModifiers"));
        Assert.Equal(new PyDict([]), await Ask("GetBoosters"));
        Assert.Equal(new PyDict([]), await Ask("GetImplants"));
        Assert.Equal(new PyList([]), await Ask("GetSkillHistory"));
        Assert.Equal(new PyDict([]), await Ask("GetFreeSkillPointsAppliedToQueue"));
        Assert.Equal(new PyDict([]), await Ask("GetFreeSkillPointsAppliedToSkills"));
        var respec = ((PyDict)((PyObject)await Ask("GetRespecInfo")).Args).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal((new PyInt(3), PyNone.Instance, PyNone.Instance), ((PyValue)respec["freeRespecs"], respec["lastRespecDate"], respec["nextTimedRespec"]));
    }

    [PgFact]
    public async Task Knowing_a_skill_and_being_a_skill_are_different_questions()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.GrantAsync(1, (3300, 1, 250));
        var (session, _) = Rig.Session(1);
        Assert.Equal(PyBool.True, await Rig.CallAsync(rig.Handler, session, "IsSkillInjected", new PyInt(3300)));
        Assert.Equal(PyBool.False, await Rig.CallAsync(rig.Handler, session, "IsSkillInjected", new PyInt(3301)));
        Assert.Equal(PyBool.True, await Rig.CallAsync(rig.Handler, session, "IsSkill", new PyInt(3301)));
        Assert.Equal(PyBool.False, await Rig.CallAsync(rig.Handler, session, "IsSkill", new PyInt(587)));
        Assert.Equal(PyBool.False, await Rig.CallAsync(rig.Handler, session, "IsSkill", new PyStr("x")));
    }

    // ── reaching the handler ─────────────────────────────────────────────────

    [Fact]
    public async Task The_door_hands_out_a_moniker_for_the_character_and_falls_back_to_the_account()
    {
        var door = new SkillMgr2Service();
        var (withCharacter, _) = Rig.Session(140_000_073);
        var moniker = (PyObject)await Rig.CallAsync(door, withCharacter, "GetMySkillHandler");
        Assert.Equal("carbon.common.script.net.moniker.Moniker", JsSemantics.StringValue(moniker.Name));
        Assert.Equal(new PyTuple([new PyStr("skillHandler"), PyNone.Instance, new PyInt(140_000_073), PyNone.Instance]), moniker.Args);

        var (accountOnly, _) = Rig.Session(null, userId: 34);
        Assert.Equal(new PyInt(34), ((PyTuple)((PyObject)await Rig.CallAsync(door, accountOnly, "GetMySkillHandler")).Args).Items[2]);
    }

    [PgFact]
    public async Task Binding_answers_a_bound_object_and_the_result_of_the_nested_call()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.GrantAsync(7, (3300, 4, 45_255));
        var (session, _) = Rig.Session(7);
        Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), await Rig.CallAsync(rig.Handler, session, "MachoResolveObject", new PyInt(7), new PyInt(0)));

        var bound = (PyTuple)await Rig.CallAsync(rig.Handler, session, "MachoBindObject", new PyInt(7), new PyTuple([new PyStr("GetSkillPoints"), new PyTuple([]), PyNone.Instance]));
        var oid = (PyTuple)((PySubStream)((PySubStruct)bound.Items[0]).Value).Decoded!;
        Assert.StartsWith($"N={MachoMessage.ProxyNodeId}:", JsSemantics.StringValue(oid.Items[0]));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs)), oid.Items[1]);
        Assert.Equal(new PyInt(45_255), bound.Items[1]);

        var again = (PyTuple)await Rig.CallAsync(rig.Handler, session, "MachoBindObject", new PyInt(7), PyNone.Instance);
        Assert.Equal(PyNone.Instance, again.Items[1]); // no nested call: just the bound object
        Assert.NotEqual(
            JsSemantics.StringValue(oid.Items[0]),
            JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)again.Items[0]).Value).Decoded!).Items[0]));
    }

    [PgFact]
    public async Task A_nested_call_to_a_method_the_handler_has_not_is_answered_with_none()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(7);
        var bound = (PyTuple)await Rig.CallAsync(rig.Handler, session, "MachoBindObject", new PyInt(7), new PyTuple([new PyStr("NoSuchMethod"), new PyTuple([]), PyNone.Instance]));
        Assert.Equal(PyNone.Instance, bound.Items[1]);
    }

    // ── the notification after choosing a character ──────────────────────────

    [PgFact]
    public async Task Entering_sends_one_unchanged_skill_of_the_character()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.GrantAsync(7, (3301, 1, 250), (3300, 4, 45_255));
        var (session, sink) = Rig.Session(7);
        await rig.Hook.OnCharacterEnteredAsync(session, 7, CancellationToken.None);

        var packet = Assert.Single(sink.Sent);
        var body = (PyTuple)((PyObject)packet).Args;
        Assert.Equal(new PyInt(12), body.Items[0]);
        var inner = (PyTuple)MarshalCodec.Decode(((PyBuffer)((PyTuple)((PyTuple)body.Items[4]).Items[0]).Items[1]).Value.ToArray());
        var args = (PyTuple)((PyTuple)inner.Items[1]).Items[1];
        Assert.Equal([3300L], ((PyDict)args.Items[0]).Entries.Select(e => ((PyInt)e.Key).Value)); // the first by type, one only
        Assert.Equal(PyNone.Instance, args.Items[1]);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs)), args.Items[2]);
    }

    [PgFact]
    public async Task A_character_with_no_skills_gets_no_notification()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, sink) = Rig.Session(7);
        await rig.Hook.OnCharacterEnteredAsync(session, 7, CancellationToken.None);
        Assert.Empty(sink.Sent);
    }
}
