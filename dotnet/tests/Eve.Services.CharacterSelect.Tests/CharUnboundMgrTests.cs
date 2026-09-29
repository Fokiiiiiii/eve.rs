using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect.Tests;

public class CharUnboundMgrTests
{
    private static readonly PyValue Doll = new PyDict([new(new PyStr("recorded"), new PyInt(1))]);
    private static readonly PyValue Portrait = new PyDict([new(new PyStr("recorded"), new PyInt(2))]);

    private static async Task<long> CreateAsync(Harness h, ClientSession session, string name, int race = 1, int bloodline = 1, int gender = 1, int school = 35, int ancestry = 10)
    {
        var id = await h.CallAsync(session, "CreateCharacterWithDoll",
            new PyStr(name), new PyInt(race), new PyInt(bloodline), new PyInt(gender), new PyInt(ancestry), Doll, Portrait, new PyInt(school), PyNone.Instance, PyNone.Instance);
        return ((PyInt)id).Value;
    }

    private static async Task<MachoError> ErrorOfAsync(Func<ValueTask<PyValue>> call) =>
        (await Assert.ThrowsAsync<MachoWrappedException>(async () => await call())).Error;

    private static MachoError Info(string text)
    {
        var error = MachoErrors.UserError("CustomInfo", ("info", new PyStr(text)));
        Assert.True(error.IsUserError);
        return error;
    }

    // ── the screen before any character ─────────────────────────────────────

    [PgFact]
    public async Task A_new_account_has_no_characters_and_the_fixed_answers_are_the_references()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");

        Assert.Equal(new PyInt(0), await h.CallAsync(session, "GetNumCharacters"));
        Assert.Equal(new PyList([]), await h.CallAsync(session, "GetCharactersToSelect"));
        Assert.Equal(new PyList([]), await h.CallAsync(session, "GetCohortsForUser"));
        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "GetCharacterLockType"));
        Assert.Equal(PyBool.False, await h.CallAsync(session, "ToggleValidation", PyBool.True));

        // the recorded answers are the same
        Assert.Equal(0, Recorded.Steps("GetNumCharacters")[0]["ok"]!.GetValue<int>());
        Assert.Null(Recorded.Steps("GetCharacterLockType")[0]["ok"]);
        Assert.False(Recorded.Steps("ToggleValidation")[0]["ok"]!.GetValue<bool>());
    }

    [PgFact]
    public async Task The_selection_data_of_a_new_account_is_the_recorded_shape()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("rec-walk");
        var data = (PyTuple)await h.CallAsync(session, "GetCharacterSelectionData");

        Assert.Equal(4, data.Items.Count);
        Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance]), data.Items[1]);
        Assert.Equal(new PyList([]), data.Items[2]);
        Assert.Equal(new PyList([]), data.Items[3]);

        var account = Assert.Single(((PyList)data.Items[0]).Items);
        var recorded = Recorded.Steps("GetCharacterSelectionData")[0]["ok"]!["$tuple"]![0]![0]!;
        Assert.Equal(Recorded.RecordedKinds(recorded), Recorded.Attributes(account).Select(a => new KeyValuePair<string, string>(a.Key, Recorded.Kind(a.Value))));
        var values = Recorded.Attributes(account).ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal(new PyStr("rec-walk"), values["userName"]);
        Assert.Equal(new PyInt(3), values["characterSlots"]);
        Assert.Equal(new PyInt(3), values["maxCharacterSlots"]);
        Assert.Equal(new PyLong(157_469_184_000_000_000), values["subscriptionEndTime"]);
        // the account began now: its creation date is a FILETIME of today, not the reference's constant
        var created = ((PyLong)values["creationDate"]).Value;
        Assert.InRange(created, Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()), Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()));
    }

    // ── names ───────────────────────────────────────────────────────────────

    [PgFact]
    public async Task Every_recorded_name_check_gets_the_recorded_answer_but_one()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        var checkedNames = 0;
        foreach (var entry in Recorded.Login["names"]!.AsArray())
        {
            var name = entry!["name"]!.GetValue<string>();
            var expected = entry["ok"]!.GetValue<int>();
            var actual = ((PyInt)await h.CallAsync(session, "ValidateNameEx", new PyStr(name))).Value;
            if (name == "Elysian")
            {
                // The reference reserves the name of its own system character; nothing in the client needs that (DEVIATIONS.md).
                Assert.Equal((-102, 1), (expected, (int)actual));
                continue;
            }
            Assert.True(expected == actual, $"{name}: recorded {expected}, got {actual}");
            checkedNames++;
        }
        Assert.Equal(19, checkedNames);
    }

    [PgFact]
    public async Task A_taken_name_is_unavailable_in_every_spelling()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        await CreateAsync(h, session, "Recorded Pilot");

        foreach (var spelling in new[] { "Recorded Pilot", "recorded pilot", "RECORDED PILOT", " Recorded Pilot " })
        {
            Assert.Equal(new PyInt(-101), await h.CallAsync(session, "ValidateNameEx", new PyStr(spelling)));
        }
        Assert.Equal(new PyInt(-101), await h.CallAsync(session, "ValidateNameEx", new PyStr("Recorded Pilot")));
        Assert.Equal(new PyInt(1), await h.CallAsync(session, "ValidateNameEx", new PyStr("Recorded Pilot Two")));
        // an invalid name is reported as invalid, not as taken
        Assert.Equal(new PyInt(-1), await h.CallAsync(session, "ValidateNameEx", new PyStr("ab")));
        Assert.Equal(new PyInt(-1), await h.CallAsync(session, "ValidateNameEx", PyNone.Instance));
    }

    [PgFact]
    public async Task Random_names_are_valid_free_and_of_the_races_flavour()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        foreach (var race in new[] { 1, 2, 4, 8, 99 })
        {
            for (var i = 0; i < 20; i++)
            {
                var name = JsSemantics.StringValue(await h.CallAsync(session, "GetValidRandomName", new PyInt(race)))!;
                Assert.Equal(NameCheck.Valid, CharacterNames.Validate(name));
                Assert.Equal(new PyInt(1), await h.CallAsync(session, "ValidateNameEx", new PyStr(name)));
            }
        }
    }

    // ── creation ────────────────────────────────────────────────────────────

    [PgFact]
    public async Task Creating_a_character_answers_its_id_and_the_account_then_lists_it_in_the_recorded_shapes()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("rec-walk");
        var id = await CreateAsync(h, session, "Recorded Pilot");
        Assert.Equal(140_000_001, id);
        Assert.Equal(new PyInt(1), await h.CallAsync(session, "GetNumCharacters"));

        // the short list
        var row = Assert.Single(((PyList)await h.CallAsync(session, "GetCharactersToSelect")).Items);
        var recordedShort = Recorded.Steps("GetCharactersToSelect").Last()["ok"]![0]!;
        Assert.Equal(Recorded.RecordedKinds(recordedShort), Recorded.Attributes(row).Select(a => new KeyValuePair<string, string>(a.Key, Recorded.Kind(a.Value))));
        var shortRow = Recorded.Attributes(row).ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal(new PyInt(id), shortRow["characterID"]);
        Assert.Equal(new PyStr("Recorded Pilot"), shortRow["characterName"]);
        Assert.Equal(new PyInt(1373), shortRow["typeID"]);
        Assert.Equal(PyNone.Instance, shortRow["deletePrepareDateTime"]);

        // the full data
        var data = (PyTuple)await h.CallAsync(session, "GetCharacterSelectionData");
        var full = Assert.Single(((PyList)data.Items[2]).Items);
        var recordedFull = Recorded.Steps("GetCharacterSelectionData").Last()["ok"]!["$tuple"]![2]![0]!;
        Assert.Equal(37, Recorded.RecordedKinds(recordedFull).Count);
        Assert.Equal(Recorded.RecordedKinds(recordedFull), Recorded.Attributes(full).Select(a => new KeyValuePair<string, string>(a.Key, Recorded.Kind(a.Value))));

        var v = Recorded.Attributes(full).ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal((new PyInt(1), new PyInt(1), new PyInt(1), new PyInt(10), new PyInt(35)), ((PyValue)v["gender"], v["raceID"], v["bloodlineID"], v["ancestryID"], v["schoolID"]));
        Assert.Equal((new PyInt(1000006), new PyInt(60000610), new PyInt(30002775), new PyInt(20000406), new PyInt(10000033)), ((PyValue)v["corporationID"], v["stationID"], v["solarSystemID"], v["constellationID"], v["regionID"]));
        Assert.Equal((new PyInt(601), new PyStr("Ibis")), ((PyValue)v["shipTypeID"], v["shipName"]));
        Assert.Equal(new PyInt(384_036), v["skillPoints"]);
        Assert.Equal(PyNone.Instance, v["factionID"]);
        Assert.Equal(PyNone.Instance, v["allianceID"]);
    }

    [PgFact]
    public async Task Each_argument_of_the_creation_call_lands_in_its_own_field()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        var id = await CreateAsync(h, session, "Reaper Pilot", race: 2, bloodline: 3, gender: 2, school: 33, ancestry: 19);
        var c = (await h.Characters.GetAsync(id))!;
        Assert.Equal((2, 3, 2, 19, 33, 1375, 1000044L), (c.RaceId, c.BloodlineId, c.Gender, c.AncestryId, c.SchoolId, c.TypeId, c.CorporationId));
        Assert.Equal(588, (await h.Items.GetAsync(c.ShipId!.Value))!.TypeId);
        // the appearance the client sent is kept as sent: the paper doll first, the portrait second
        var (doll, portrait) = await h.Characters.AppearanceAsync(id);
        Assert.Equal(MarshalCodec.Encode(Doll), doll);
        Assert.Equal(MarshalCodec.Encode(Portrait), portrait);
    }

    [PgFact]
    public async Task Creation_fails_with_the_recorded_errors()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        await CreateAsync(h, session, "Recorded Pilot");

        var tooShort = await ErrorOfAsync(() => h.CallAsync(session, "CreateCharacterWithDoll",
            new PyStr("a"), new PyInt(1), new PyInt(1), new PyInt(1), new PyInt(10), Doll, Portrait, new PyInt(35), PyNone.Instance, PyNone.Instance));
        var taken = await ErrorOfAsync(() => h.CallAsync(session, "CreateCharacterWithDoll",
            new PyStr("recorded pilot"), new PyInt(1), new PyInt(1), new PyInt(1), new PyInt(10), Doll, Portrait, new PyInt(35), PyNone.Instance, PyNone.Instance));

        foreach (var error in new[] { tooShort, taken })
        {
            Assert.Equal(2, error.ErrorCode);
            Assert.True(error.IsUserError);
            Assert.Equal("CharNameInvalid", error.UserErrorMessage);
        }
        // the recorded errors are the same
        var recorded = Recorded.Steps("CreateCharacterWithDoll").Where(s => s["error"] is not null).ToList();
        Assert.Equal(2, recorded.Count);
        Assert.All(recorded, step =>
        {
            Assert.Equal(2, step["error"]!["code"]!.GetValue<int>());
            Assert.Equal("CharNameInvalid", step["error"]!["payload"]!["$objectex"]!["$tuple"]![1]!["$tuple"]![0]!.GetValue<string>());
        });
        Assert.Equal(new PyInt(1), await h.CallAsync(session, "GetNumCharacters"));
    }

    [PgFact]
    public async Task An_account_has_three_slots_and_the_fourth_is_refused_with_a_message()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        for (var i = 1; i <= 3; i++)
        {
            await CreateAsync(h, session, $"Slot Pilot {i}");
        }
        var error = await ErrorOfAsync(() => h.CallAsync(session, "CreateCharacterWithDoll",
            new PyStr("Fourth Pilot"), new PyInt(1), new PyInt(1), new PyInt(1), new PyInt(10), Doll, Portrait, new PyInt(35), PyNone.Instance, PyNone.Instance));
        Assert.Equal("CustomInfo", error.UserErrorMessage);
        Assert.Equal(new PyInt(3), await h.CallAsync(session, "GetNumCharacters"));
    }

    [PgFact]
    public async Task A_choice_that_does_not_exist_is_refused_with_a_message()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        var error = await ErrorOfAsync(() => h.CallAsync(session, "CreateCharacterWithDoll",
            new PyStr("Some Pilot"), new PyInt(16), new PyInt(1), new PyInt(1), new PyInt(10), Doll, Portrait, new PyInt(35), PyNone.Instance, PyNone.Instance));
        Assert.Equal("CustomInfo", error.UserErrorMessage);
        Assert.Equal(new PyInt(0), await h.CallAsync(session, "GetNumCharacters"));
    }

    [PgFact]
    public async Task The_accounts_see_only_their_own_characters()
    {
        await using var h = await Harness.CreateAsync();
        var (one, _) = await h.LoginAsync("one");
        var (two, _) = await h.LoginAsync("two");
        await CreateAsync(h, one, "First Pilot");
        await CreateAsync(h, two, "Second Pilot");
        await CreateAsync(h, two, "Third Pilot");
        Assert.Equal(new PyInt(1), await h.CallAsync(one, "GetNumCharacters"));
        Assert.Equal(new PyInt(2), await h.CallAsync(two, "GetNumCharacters"));
        Assert.Equal(2, ((PyList)await h.CallAsync(two, "GetCharactersToSelect")).Items.Count);
    }

    // ── choosing a character ────────────────────────────────────────────────

    [PgFact]
    public async Task Choosing_a_character_sets_the_session_and_sends_one_change_in_the_recorded_shape()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("rec-walk", role: 0x0140_0000_2024_0000);
        var id = await CreateAsync(h, session, "Recorded Pilot");
        var shipId = (await h.Characters.GetAsync(id))!.ShipId!.Value;

        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyInt(id)));

        var packet = Assert.Single(sink.Sent);
        var changes = ChangesOf(packet);
        var recorded = Recorded.Login["afterSelect"]![0]!["$tuple"]![4]!["$tuple"]![1]!["$tuple"]![1]!["$dict"]!.AsArray();
        Assert.Equal(recorded.Select(e => e![0]!.GetValue<string>()), changes.Select(c => c.Name));
        for (var i = 0; i < recorded.Count; i++)
        {
            var pair = recorded[i]![1]!["$tuple"]!;
            Assert.True(
                (Recorded.Kind(pair[0]), Recorded.Kind(pair[1])) == (Recorded.Kind(changes[i].Old), Recorded.Kind(changes[i].New)),
                $"{changes[i].Name}: recorded ({Recorded.Kind(pair[0])}, {Recorded.Kind(pair[1])}), ours ({Recorded.Kind(changes[i].Old)}, {Recorded.Kind(changes[i].New)})");
        }

        var now = changes.ToDictionary(c => c.Name, c => c.New);
        Assert.Equal(new PyInt(id), now["charid"]);
        Assert.Equal(new PyInt(1000006), now["corpid"]);
        Assert.Equal(new PyInt(500001), now["factionid"]);
        Assert.Equal((new PyInt(60000610), new PyInt(60000610), new PyInt(60000610)), ((PyValue)now["stationid"], now["stationid2"], now["locationid"]));
        Assert.Equal((new PyInt(30002775), new PyInt(20000406), new PyInt(10000033)), ((PyValue)now["solarsystemid2"], now["constellationid"], now["regionid"]));
        Assert.Equal(new PyLong(shipId), now["shipid"]);
        Assert.Equal(new PyInt(1000), now["corpAccountKey"]);
        Assert.Equal(new PyLong(0x0140_0000_2024_0000 | CharUnboundMgrService.SessionBaseRole), now["role"]);
        Assert.Equal((new PyInt(0), PyNone.Instance), (changes.Single(c => c.Name == "hqID").Old, changes.Single(c => c.Name == "hqID").New));

        // the session itself holds them
        Assert.Equal(id, session.CharacterId);
        Assert.Equal(new PyInt(60000610), session.Get("stationid"));
        // and the packet is addressed to this client, from the node
        var state = ((PyTuple)((PyObject)packet).Args).Items;
        Assert.Equal(new PyInt(16), state[0]);
        Assert.Equal(new PyInt(session.UserId!.Value), state[3]);
    }

    [PgFact]
    public async Task An_ordinary_account_gets_the_base_role_alone()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        var id = await CreateAsync(h, session, "Ann Lee");
        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));
        Assert.Equal(new PyLong(CharUnboundMgrService.SessionBaseRole), session.Get("role"));
    }

    [PgFact]
    public async Task Another_accounts_character_cannot_be_chosen()
    {
        await using var h = await Harness.CreateAsync();
        var (one, _) = await h.LoginAsync("one");
        var (two, sink) = await h.LoginAsync("two");
        var theirs = await CreateAsync(h, one, "Their Pilot");

        var error = await ErrorOfAsync(() => h.CallAsync(two, "SelectCharacterID", new PyInt(theirs)));
        Assert.Equal("CustomInfo", error.UserErrorMessage);
        // the reference's message, word for word
        var recordedMessage = Recorded.Steps("SelectCharacterID").Single(s => s["error"] is not null)["error"]!["payload"]!["$objectex"]!["$tuple"]![1]!["$tuple"]![1]!["$dict"]![0]![1]!.GetValue<string>();
        Assert.Equal(recordedMessage, InfoText(error));
        Assert.Empty(sink.Sent);
        Assert.Null(two.CharacterId);
        Assert.False(h.Online.IsOnline(theirs));
    }

    [PgFact]
    public async Task A_character_queued_for_deletion_cannot_be_chosen()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("pilot");
        var id = await CreateAsync(h, session, "Doomed Pilot");
        await h.ExecuteAsync($"UPDATE {{characters}}.characters SET delete_prepare_at = now() WHERE id = {id}");

        var error = await ErrorOfAsync(() => h.CallAsync(session, "SelectCharacterID", new PyInt(id)));
        Assert.Equal("CustomInfo", error.UserErrorMessage);
        Assert.Empty(sink.Sent);
        Assert.False(h.Online.IsOnline(id));
        // the list still shows it, with the time deletion was asked for
        var row = Recorded.Attributes(Assert.Single(((PyList)await h.CallAsync(session, "GetCharactersToSelect")).Items)).ToDictionary(a => a.Key, a => a.Value);
        Assert.IsType<PyLong>(row["deletePrepareDateTime"]);
    }

    [PgFact]
    public async Task A_character_in_the_game_cannot_be_chosen_again_until_its_session_ends()
    {
        await using var h = await Harness.CreateAsync();
        var (first, _) = await h.LoginAsync("pilot");
        var id = await CreateAsync(h, first, "Ann Lee");
        await h.CallAsync(first, "SelectCharacterID", new PyInt(id));
        Assert.True(h.Online.IsOnline(id));

        // the same account, another connection
        var (second, secondSink) = await h.LoginAsync("pilot");
        var error = await ErrorOfAsync(() => h.CallAsync(second, "SelectCharacterID", new PyInt(id)));
        Assert.Equal("CustomInfo", error.UserErrorMessage);
        Assert.Empty(secondSink.Sent);
        Assert.Null(second.CharacterId);

        first.Close();
        Assert.False(h.Online.IsOnline(id));
        await h.CallAsync(second, "SelectCharacterID", new PyInt(id));
        Assert.Equal(id, second.CharacterId);
        Assert.Single(secondSink.Sent);
    }

    [PgFact]
    public async Task Choosing_the_same_character_twice_in_one_session_announces_nothing_the_second_time()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("pilot");
        var id = await CreateAsync(h, session, "Ann Lee");
        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));
        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));
        Assert.Single(sink.Sent);
    }

    [PgFact]
    public async Task An_unknown_or_malformed_choice_answers_none_and_changes_nothing()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("pilot");
        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyInt(1)));            // recorded: ok None
        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyInt(140_009_999)));
        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyStr("x")));
        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID"));
        Assert.Empty(sink.Sent);
        Assert.Null(session.CharacterId);
        Assert.Null(Recorded.Steps("SelectCharacterID")[0]["ok"]);
    }

    [PgFact]
    public async Task The_long_form_of_an_id_is_understood()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("pilot");
        var id = await CreateAsync(h, session, "Ann Lee");
        await h.CallAsync(session, "SelectCharacterID", new PyLong(id));
        Assert.Equal(id, session.CharacterId);
    }

    /// <summary>The <c>info</c> text of a <c>CustomInfo</c> user error.</summary>
    private static string? InfoText(MachoError error)
    {
        var header = (PyTuple)((PyObjectEx)error.Payload).Header;
        var dict = (PyDict)((PyTuple)header.Items[1]).Items[1];
        return JsSemantics.StringValue(dict.Entries.Single(e => JsSemantics.StringValue(e.Key) == "info").Value);
    }

    private static List<SessionChange> ChangesOf(PyValue packet)
    {
        var payload = (PyTuple)((PyTuple)((PyObject)packet).Args).Items[4];
        var dict = (PyDict)((PyTuple)payload.Items[1]).Items[1];
        return [.. dict.Entries.Select(e => new SessionChange(JsSemantics.StringValue(e.Key)!, ((PyTuple)e.Value).Items[0], ((PyTuple)e.Value).Items[1]))];
    }
}
