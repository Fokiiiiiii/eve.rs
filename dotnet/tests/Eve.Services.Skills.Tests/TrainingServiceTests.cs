using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills.Tests;

public class TrainingServiceTests
{
    private const long Character = 140_000_038;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static PyValue Queue(params (int Position, int Skill, int Level)[] lines) =>
        new PyDict([.. lines.Select(l => new KeyValuePair<PyValue, PyValue>(new PyInt(l.Position), Py.Tuple(new PyInt(l.Skill), new PyInt(l.Level))))]);

    private static PyValue Activate(bool value) => new PyDict([new(new PyStr("activate"), PyValue.Of(value))]);

    /// <summary>A rig with a character that has Gunnery IV, Small Hybrid Turret I and Repair Systems untrained, and its session in the game.</summary>
    private static async Task<(Rig Rig, ClientSession Session, Capture Sink)> ReadyAsync(
        TrainingSettings? settings = null, Eve.Scheduler.LocalTimers? localTimers = null, bool scheduleAnnouncer = false)
    {
        var rig = await Rig.CreateAsync(settings, localTimers: localTimers, scheduleAnnouncer: scheduleAnnouncer);
        await rig.GrantAsync(Character, (3300, 4, 45255), (3301, 1, 250), (3315, 0, 0));
        var (session, sink) = Rig.Session(Character);
        rig.Sessions.Enter(Character, session);
        return (rig, session, sink);
    }

    private static Task<PyValue> Call(Rig rig, ClientSession session, string method, PyValue[] args, PyValue? kwargs = null) =>
        ((IService)rig.Handler).CallAsync(new ServiceCall { Method = method, Args = args, Kwargs = kwargs ?? PyNone.Instance, Session = session, CallId = new PyInt(1) }).AsTask();

    [PgFact]
    public async Task Saving_a_queue_tells_the_client_the_queue_and_every_client_of_the_account()
    {
        var (rig, session, sink) = await ReadyAsync();
        await using var owned = rig;
        Assert.Equal(PyNone.Instance, await Call(rig, session, "SaveNewQueue", [Queue((0, 3301, 2), (1, 3301, 3))]));

        var notes = Notes.Of(sink);
        Assert.Equal([("OnNewSkillQueueSaved", "charid"), ("OnMultipleCharactersTrainingUpdated", "userid")], notes.Select(n => (n.Name, n.IdType)));
        var lines = ((PyList)notes[0].Args[0]).Items.Select(Notes.Fields).ToArray();
        Assert.Equal([3301L, 3301], lines.Select(l => Notes.Number(l["trainingTypeID"])));
        Assert.Equal([2L, 3], lines.Select(l => Notes.Number(l["trainingToLevel"])));
        Assert.Equal([250L, 1415], lines.Select(l => Notes.Number(l["trainingStartSP"])));
        Assert.Equal(Notes.Number(lines[0]["trainingEndTime"]), Notes.Number(lines[1]["trainingStartTime"])); // the second line begins where the first ends
        Assert.Empty(notes[1].Args);
    }

    [PgFact]
    public async Task Saving_a_queue_without_running_it_says_the_queue_is_paused_and_gives_it_no_times()
    {
        var (rig, session, sink) = await ReadyAsync();
        await using var owned = rig;
        await Call(rig, session, "SaveNewQueue", [Queue((0, 3301, 2))], Activate(false));
        var notes = Notes.Of(sink);
        Assert.Equal(["OnNewSkillQueueSaved", "OnSkillQueuePausedServer", "OnMultipleCharactersTrainingUpdated"], notes.Select(n => n.Name));
        var line = Notes.Fields(((PyList)notes[0].Args[0]).Items[0]);
        Assert.Equal((PyNone.Instance, PyNone.Instance), (line["trainingStartTime"], line["trainingEndTime"]));
        Assert.Equal("clientID", notes[1].IdType);
    }

    [PgFact]
    public async Task The_queue_is_read_from_a_dict_or_a_list_and_lines_that_name_nothing_are_left_out()
    {
        var (rig, session, sink) = await ReadyAsync();
        await using var owned = rig;
        // a list is positions from 0; a line with no skill, no level or a negative position is dropped
        PyValue[] junk =
        [
            Py.Tuple(new PyInt(3301), new PyInt(2)), Py.Tuple(new PyInt(0), new PyInt(3)), Py.Tuple(new PyInt(3301), new PyInt(0)), PyNone.Instance, Py.Tuple(new PyInt(3301), new PyInt(3)),
        ];
        await Call(rig, session, "SaveNewQueue", [new PyList(junk)]);
        var lines = ((PyList)Notes.Of(sink)[0].Args[0]).Items.Select(Notes.Fields).ToArray();
        Assert.Equal([(3301L, 2L), (3301L, 3L)], lines.Select(l => (Notes.Number(l["trainingTypeID"]), Notes.Number(l["trainingToLevel"]))));
        Assert.Equal([0L, 1], lines.Select(l => Notes.Number(l["queuePosition"])));

        // positions of a dict order the lines whatever the order they arrive in, and a line may be a dict of its own
        await Call(rig, session, "SaveNewQueue", [new PyDict([
            new(new PyInt(5), Py.Tuple(new PyInt(3301), new PyInt(3))),
            new(new PyInt(2), new PyDict([new(new PyStr("trainingTypeID"), new PyInt(3301)), new(new PyStr("trainingToLevel"), new PyInt(2))])),
            new(new PyInt(-1), Py.Tuple(new PyInt(3301), new PyInt(4))),
        ])]);
        var again = ((PyList)Notes.Of(sink)[0].Args[0]).Items.Select(Notes.Fields).ToArray();
        Assert.Equal([2L, 3], again.Select(l => Notes.Number(l["trainingToLevel"])));
    }

    [PgFact]
    public async Task A_queue_the_rules_refuse_is_a_message_and_tells_nothing()
    {
        var (rig, session, sink) = await ReadyAsync();
        await using var owned = rig;
        foreach (var (queue, expected) in new (PyValue, string)[]
        {
            (Queue((0, 3301, 3)), "QueueCannotPlaceSkillLevelsOutOfOrder"),
            (Queue((0, 3300, 4)), "QueueCannotTrainPreviouslyTrainedSkills"),
            (Queue((0, 3301, 6)), "QueueCannotTrainPastMaximumLevel"),
            (Queue((0, 20315, 1)), "QueueSkillNotUploaded"),
        })
        {
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () => await Call(rig, session, "SaveNewQueue", [queue]));
            Assert.Equal(expected, refusal.Error.UserErrorMessage);
        }
        Assert.Empty(Notes.Of(sink));

        var many = new PyDict([.. Enumerable.Range(0, 151).Select(i => new KeyValuePair<PyValue, PyValue>(new PyInt(i), Py.Tuple(new PyInt(3301), new PyInt(2))))]);
        var tooMany = await Assert.ThrowsAsync<MachoWrappedException>(async () => await Call(rig, session, "SaveNewQueue", [many]));
        Assert.Equal("QueueTooManySkills", tooMany.Error.UserErrorMessage);
        Assert.Equal(new PyInt(150), Assert.Single(((PyDict)((PyTuple)((PyTuple)((PyObjectEx)tooMany.Error.Payload).Header).Items[1]).Items[1]).Entries).Value);
    }

    [PgFact]
    public async Task Starting_a_skill_by_its_type_or_by_its_item_runs_it_and_anything_else_starts_nothing()
    {
        using var shared = new Eve.Scheduler.LocalTimers(delay: (_, token) =>
            Task.Delay(Timeout.InfiniteTimeSpan, token));
        var (rig, session, sink) = await ReadyAsync(localTimers: shared, scheduleAnnouncer: true);
        await using var owned = rig;
        Assert.Equal(PyNone.Instance, await Call(rig, session, "CharStartTrainingSkillByTypeID", [new PyInt(3301)]));
        Assert.Equal(["OnNewSkillQueueSaved", "OnMultipleCharactersTrainingUpdated"], Notes.Of(sink).Select(n => n.Name));
        Assert.True(shared.IsSet($"skills.training:{Character}"));

        Assert.Equal(PyNone.Instance, await Call(rig, session, "CharStartTrainingSkillByTypeID", [new PyInt(99)]));            // no such skill
        Assert.Equal(PyNone.Instance, await Call(rig, session, "CharStartTrainingSkill", [new PyLong(SkillWire.ItemId(Character + 1, 3301))])); // another character's
        Assert.Equal(PyNone.Instance, await Call(rig, session, "CharStartTrainingSkill", [new PyInt(12345)]));                 // no item
        Assert.Empty(Notes.Of(sink));

        // by item: Gunnery IV is trained to V and goes first
        await Call(rig, session, "CharStartTrainingSkill", [new PyLong(SkillWire.ItemId(Character, 3300))]);
        var queue = ((PyList)Notes.Of(sink)[0].Args[0]).Items.Select(Notes.Fields).ToArray();
        Assert.Equal([(3300L, 5L), (3301L, 2L)], queue.Select(l => (Notes.Number(l["trainingTypeID"]), Notes.Number(l["trainingToLevel"]))));
        // a level of its own may be asked for
        await Call(rig, session, "CharStartTrainingSkillByTypeID", [new PyInt(3301), new PyInt(2)]);
        Assert.Equal(3301, Notes.Number(Notes.Fields(((PyList)Notes.Of(sink)[0].Args[0]).Items[0])["trainingTypeID"]));
    }

    [PgFact]
    public async Task The_skill_in_training_is_marked_and_has_the_points_it_has_gained()
    {
        var (rig, session, _) = await ReadyAsync();
        await using var owned = rig;
        await Call(rig, session, "SaveNewQueue", [Queue((0, 3301, 2))]);
        rig.Wait(10 * Minute);

        var info = Notes.Fields(await Call(rig, session, "GetMySkillInfo", []));
        Assert.Equal(45255 + 550, Notes.Number(info["skillPoints"]));
        var entry = (PyObjectEx)((PyDict)info["skills"]).Entries.Single(e => Notes.Number(e.Key) == 3301).Value;
        var header = ((PyTuple)entry.Header).Items;
        Assert.Equal((1L, 550L), (Notes.Number(((PyTuple)header[1]).Items[1]), Notes.Number(((PyTuple)header[1]).Items[2])));
        var state = ((PyDict)header[2]).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(PyBool.True, state["inTraining"]);
        var other = (PyObjectEx)((PyDict)info["skills"]).Entries.Single(e => Notes.Number(e.Key) == 3300).Value;
        Assert.Equal(PyBool.False, ((PyDict)((PyTuple)other.Header).Items[2]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "inTraining").Value);
        Assert.Single(((PyList)info["queue"]).Items);
        Assert.Equal(new PyInt(550 + 45255), await Call(rig, session, "GetSkillPoints", []));
    }

    [PgFact]
    public async Task Stopping_tells_the_client_the_skill_and_that_the_queue_is_paused_and_stopping_again_tells_nothing()
    {
        var (rig, session, sink) = await ReadyAsync();
        await using var owned = rig;
        await Call(rig, session, "SaveNewQueue", [Queue((0, 3301, 2))]);
        Notes.Of(sink);
        rig.Wait(10 * Minute);

        Assert.Equal(PyNone.Instance, await Call(rig, session, "AbortTraining", []));
        var notes = Notes.Of(sink);
        Assert.Equal(["OnServerSkillsChanged", "OnMultipleCharactersTrainingUpdated"], notes.Select(n => n.Name)); // no dogma here: the host adds its events
        Assert.Equal(("OnSkillQueuePausedServer", typeof(PyLong)), (JsSemantics.StringValue(notes[0].Args[1]), notes[0].Args[2].GetType()));
        var kept = (PyObjectEx)((PyDict)notes[0].Args[0]).Entries.Single().Value;
        Assert.Equal(2, ((PyTuple)kept.Header).Items.Count); // points only: no item fields
        Assert.Equal(550, Notes.Number(((PyTuple)((PyTuple)kept.Header).Items[1]).Items[2]));

        Assert.Equal(PyNone.Instance, await Call(rig, session, "CharStopTrainingSkill", []));
        Assert.Empty(Notes.Of(sink));
    }

    [PgFact]
    public async Task A_level_that_finishes_is_told_as_the_reference_tells_it_and_the_queue_pauses_when_it_is_empty()
    {
        var (rig, session, sink) = await ReadyAsync();
        await using var owned = rig;
        await Call(rig, session, "SaveNewQueue", [Queue((0, 3301, 2), (1, 3301, 3))]);
        Notes.Of(sink);
        rig.Wait(TimeSpan.FromMinutes(50)); // the first level takes 38.8 minutes

        await Call(rig, session, "GetSkills", []); // any question settles what the clock finished
        var notes = Notes.Of(sink);
        Assert.Equal(
            ["OnServerSkillsChanged", "OnSkillLevelsTrained", "OnSkillsChanged", "OnNewSkillQueueSaved"], notes.Select(n => n.Name));
        Assert.Equal(["charid", "clientID", "clientID", "charid"], notes.Select(n => n.IdType));
        Assert.Equal(PyNone.Instance, notes[0].Args[1]);
        var changed = (PyObjectEx)((PyDict)notes[0].Args[0]).Entries.Single().Value;
        Assert.Equal(3, ((PyTuple)changed.Header).Items.Count); // with its item fields
        Assert.Equal((2L, 1415L), (Notes.Number(((PyTuple)((PyTuple)changed.Header).Items[1]).Items[1]), Notes.Number(((PyTuple)((PyTuple)changed.Header).Items[1]).Items[2])));
        var pair = Assert.Single(((PyList)notes[1].Args[0]).Items.Cast<PyTuple>());
        Assert.Equal((3301L, 2L), (Notes.Number(pair.Items[0]), Notes.Number(pair.Items[1])));
        Assert.Single(((PyList)notes[3].Args[0]).Items); // the second line is left

        rig.Wait(TimeSpan.FromMinutes(500));
        await Call(rig, session, "GetSkills", []);
        var last = Notes.Of(sink);
        Assert.Equal(["OnServerSkillsChanged", "OnSkillLevelsTrained", "OnSkillsChanged", "OnNewSkillQueueSaved", "OnSkillQueuePausedServer"], last.Select(n => n.Name));
        Assert.Empty(((PyList)last[3].Args[0]).Items);
    }

    [PgFact]
    public async Task A_character_that_is_not_in_the_game_is_not_told_and_finds_its_skills_trained_when_it_comes_back()
    {
        var rig = await Rig.CreateAsync();
        await using var owned = rig;
        await rig.GrantAsync(Character, (3300, 4, 45255), (3301, 1, 250));
        var (first, firstSink) = Rig.Session(Character);
        await Call(rig, first, "SaveNewQueue", [Queue((0, 3301, 2))]); // never entered: no one is registered to hear
        Notes.Of(firstSink);
        rig.Wait(TimeSpan.FromMinutes(60));

        var (back, sink) = Rig.Session(Character);
        await rig.Hook.OnCharacterEnteredAsync(back, Character, CancellationToken.None);
        var notes = Notes.Of(sink);
        Assert.Equal(["OnServerSkillsChanged"], notes.Select(n => n.Name)); // the one that makes the client load its skills, and nothing about the level
        Assert.Equal(2, (await rig.Store.ForCharacterAsync(Character)).Single(s => s.TypeId == 3301).Level);
        Assert.Equal(back, rig.Sessions.Of(Character));
    }

    [PgFact]
    public async Task The_learning_attributes_carry_the_servers_training_speed()
    {
        var (rig, session, _) = await ReadyAsync();
        await using var owned = rig;
        var normal = (PyDict)await Call(rig, session, "GetAttributes", []);
        Assert.Equal([164L, 165, 166, 167, 168], normal.Entries.Select(e => Notes.Number(e.Key)));
        Assert.All(normal.Entries, e => Assert.Equal(new PyInt(20), e.Value));

        var (fast, fastSession, _) = await ReadyAsync(new TrainingSettings { Speed = 60_000 });
        await using var ownedFast = fast;
        Assert.All(((PyDict)await Call(fast, fastSession, "GetAttributes", [])).Entries, e => Assert.Equal(new PyInt(1_200_000), e.Value));
    }

    [PgFact]
    public async Task The_history_lists_what_happened_newest_first_with_the_recorded_fields()
    {
        var (rig, session, _) = await ReadyAsync();
        await using var owned = rig;
        await Call(rig, session, "SaveNewQueue", [Queue((0, 3301, 2))]);
        rig.Wait(TimeSpan.FromMinutes(10));
        await Call(rig, session, "AbortTraining", []);

        var history = ((PyList)await Call(rig, session, "GetSkillHistory", [])).Items.Select(Notes.Fields).ToArray();
        Assert.Equal([38L, 36], history.Select(h => Notes.Number(h["eventTypeID"])));
        Assert.Equal((3301L, 550L, 2L), (Notes.Number(history[0]["skillTypeID"]), Notes.Number(history[0]["absolutePoints"]), Notes.Number(history[0]["level"])));
        Assert.Equal((3301L, 250L, 2L), (Notes.Number(history[1]["skillTypeID"]), Notes.Number(history[1]["absolutePoints"]), Notes.Number(history[1]["level"])));
        Assert.IsType<PyLong>(history[0]["logDate"]);
        Assert.Equal("util.KeyVal", JsSemantics.StringValue(((PyObject)((PyList)await Call(rig, session, "GetSkillHistory", [])).Items[0]).Name));
    }

    [PgFact]
    public async Task Without_a_character_the_training_calls_answer_with_none_and_do_nothing()
    {
        var rig = await Rig.CreateAsync();
        await using var owned = rig;
        var (session, sink) = Rig.Session(null);
        foreach (var method in new[] { "SaveNewQueue", "CharStartTrainingSkillByTypeID", "CharStartTrainingSkill", "AbortTraining", "CharStopTrainingSkill" })
        {
            Assert.Equal(PyNone.Instance, await Call(rig, session, method, [Queue((0, 3301, 2))]));
        }
        Assert.Empty(Notes.Of(sink));
        Assert.Empty(((PyList)await Call(rig, session, "GetSkillQueue", [])).Items);
    }

    [PgFact]
    public async Task When_an_extra_slot_runs_out_the_queue_of_the_character_with_the_fewest_skill_points_is_paused_and_the_account_is_told()
    {
        var extraSlots = 1;
        var (rig, session, sink) = await ReadyAsync(new TrainingSettings { AdditionalTrainingSlots = _ => extraSlots });
        await using var owned = rig;
        const long other = 140_000_039;
        await rig.GrantAsync(other, (3301, 1, 250));
        var (otherSession, otherSink) = Rig.Session(other);
        rig.Sessions.Enter(other, otherSession);
        Assert.Equal(PyNone.Instance, await Call(rig, session, "SaveNewQueue", [Queue((0, 3301, 2))]));
        Assert.Equal(PyNone.Instance, await Call(rig, otherSession, "SaveNewQueue", [Queue((0, 3301, 2))]));
        Assert.True((await rig.Training.ViewAsync(other, 34)).State.Running);

        var expiry = new MultipleTrainingSlotExpiry(
            new Eve.Scheduler.DurableScheduler(rig.Source, () => 0, schema: rig.SchemaForTest + "s"), rig.Training, rig.Announcer, rig.Sessions,
            (_, _) => Task.FromResult<IReadOnlyList<long>>([Character, other]));
        Notes.Of(otherSink); // the saves above are read (and cleared) here
        Notes.Of(sink);

        extraSlots = 0; // the extra slot has run out
        await expiry.PauseOverLimitAsync(34, default);

        Assert.False((await rig.Training.ViewAsync(other, 34)).State.Running);
        Assert.True((await rig.Training.ViewAsync(Character, 34)).State.Running);
        Assert.Equal(["OnNewSkillQueueSaved", "OnSkillQueuePausedServer", "OnMultipleCharactersTrainingUpdated"], Notes.Of(otherSink).Select(n => n.Name));
        Assert.Equal(["OnMultipleCharactersTrainingUpdated"], Notes.Of(sink).Select(n => n.Name));

        // a second run finds nothing over the limit: the queue of the character that still runs is kept
        await expiry.PauseOverLimitAsync(34, default);
        Assert.True((await rig.Training.ViewAsync(Character, 34)).State.Running);
    }
}
