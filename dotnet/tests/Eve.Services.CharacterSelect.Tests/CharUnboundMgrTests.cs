using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Skills;

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

    [PgFact]
    public async Task Corporation_login_attributes_are_folded_into_one_session_change_and_npc_hq_stays_none()
    {
        await using var h=await Harness.CreateAsync();
        await h.Corporations.SeedNpcAsync(new Eve.Sde.SdeReader(h.Source));
        var (session,sink)=await h.LoginAsync("pilot");
        var id=await CreateAsync(h,session,"Ann Lee");
        sink.Sent.Clear();
        await h.CallAsync(session,"SelectCharacterID",new PyInt(id));
        Assert.Equal(3,sink.Sent.Count); // selection reset, session change, then skills
        Assert.Equal("OnCharacterSelected", NotificationName(sink.Sent[0]));
        Assert.Empty(NotificationArgs(sink.Sent[0]).Items);
        Assert.Equal(PyNone.Instance, session.Get("warfactionid"));
        Assert.DoesNotContain(ChangesOf(sink.Sent[1]), change => change.Name == "warfactionid");
        session.Close();
        await using (var c=await h.Source.OpenConnectionAsync())
        await using (var tx=await c.BeginTransactionAsync())
        {
            await h.WalletTransactions.AdjustAsync(c,tx,id,2000000m,1,1,"Seed funds",Harness.ClockUnixMs,"fund");
            await tx.CommitAsync();
        }
        var corp=await h.Corporations.CreateAsync(id,new("Orbital Works","ORBIT","","",0),"found");
        var (corporateSession,corporateSink)=await h.LoginAsync("pilot");
        await h.CallAsync(corporateSession,"SelectCharacterID",new PyInt(id));
        Assert.Equal(3,corporateSink.Sent.Count);
        Assert.Equal("OnCharacterSelected", NotificationName(corporateSink.Sent[0]));
        Assert.Equal(new PyInt(corp.StationId),corporateSession.Get("hqID"));
        Assert.Equal(new PyLong(Eve.Corporations.CorporationRoles.All),corporateSession.Get("corprole"));
    }

    private static async Task<MachoError> ErrorOfAsync(Func<ValueTask<PyValue>> call) =>
        (await Assert.ThrowsAsync<MachoWrappedException>(async () => await call())).Error;

    private static MachoError Info(string text)
    {
        var error = MachoErrors.UserError("CustomInfo", ("info", new PyStr(text)));
        Assert.True(error.IsUserError);
        return error;
    }

    [Fact]
    public void Finished_skill_count_is_the_latest_nonempty_completion_batch()
    {
        HistoryEntry[] history =
        [
            new(10, SkillEvents.TrainingComplete, 3301, 1_415, 2),
            new(10, SkillEvents.QueueTrainingCompleted, 3302, 1_415, 2),
            new(20, SkillEvents.TrainingStarted, 3303, 250, 2),
            new(30, SkillEvents.TrainingComplete, 3304, 250, 1),
        ];

        Assert.Equal(1, CharUnboundMgrService.FinishedSkillsCount(history));
        Assert.Equal(0, CharUnboundMgrService.FinishedSkillsCount([new(40, SkillEvents.TrainingStarted, 3305, 250, 2)]));
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
        Assert.Equal(new PyInt(Harness.Race1Points), v["skillPoints"]); // the points its skills add up to, not a constant
        Assert.Equal((new PyReal(100_000.0), new PyInt(2_222), new PyReal(0.0)), ((PyValue)v["balance"], v["plexBalance"], v["balanceChange"])); // its wallet
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
    public async Task Failed_character_entry_releases_new_reservation_and_restores_previous_session()
    {
        var hook = new FailingEntryHook();
        await using var h = await Harness.CreateAsync(entryHooks: [hook]);
        var (session, _) = await h.LoginAsync("entry-rollback");
        var previousId = await CreateAsync(h, session, "Previous Pilot");
        var failingId = await CreateAsync(h, session, "Failing Pilot");

        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyInt(previousId)));
        hook.FailureCharacterId = failingId;
        var competingClaimSucceeded = false;
        var restoredRegistryMatches = false;
        hook.BeforeFailure = () => competingClaimSucceeded = h.Online.TryEnter(previousId, session.SessionId + 1);
        hook.OnSuccessfulEntry = restoredId => restoredRegistryMatches = h.Online.Snapshot().Contains(restoredId);

        var failure = await Record.ExceptionAsync(async () =>
            await h.CallAsync(session, "SelectCharacterID", new PyInt(failingId)));
        Assert.NotNull(failure);
        Assert.False(competingClaimSucceeded);
        Assert.True(restoredRegistryMatches);
        Assert.Equal(previousId, session.CharacterId);
        Assert.True(h.Online.TryEnter(previousId, session.SessionId));
        Assert.Contains(previousId, h.Online.Snapshot());
        Assert.DoesNotContain(failingId, h.Online.Snapshot());
    }

    [PgFact]
    public async Task Duplicate_login_is_rejected_before_preparing_session_attributes()
    {
        var hook = new FailingEntryHook();
        await using var h = await Harness.CreateAsync(entryHooks: [hook]);
        var (session, sink) = await h.LoginAsync("entry-duplicate");
        var id = await CreateAsync(h, session, "Occupied Pilot");
        Assert.True(h.Online.TryEnter(id, session.SessionId + 1));
        var packetCount = sink.Sent.Count;

        var failure = await Record.ExceptionAsync(async () =>
            await h.CallAsync(session, "SelectCharacterID", new PyInt(id)));
        Assert.NotNull(failure);
        Assert.Empty(hook.PreparedCharacterIds);
        Assert.Null(session.CharacterId);
        Assert.Equal(packetCount, sink.Sent.Count);
        Assert.False(h.Online.TryEnter(id, session.SessionId));
    }

    [PgFact]
    public async Task Failed_attribute_preparation_preserves_previous_runtime_and_allows_retry()
    {
        var hook = new FailingEntryHook();
        await using var h = await Harness.CreateAsync(entryHooks: [hook]);
        var (session, sink) = await h.LoginAsync("entry-preparation");
        var previousId = await CreateAsync(h, session, "Previous Prepare Pilot");
        var failingId = await CreateAsync(h, session, "Failing Prepare Pilot");
        await h.CallAsync(session, "SelectCharacterID", new PyInt(previousId));
        hook.PreparationFailureCharacterId = failingId;
        var packetCount = sink.Sent.Count;

        var failure = await Record.ExceptionAsync(async () =>
            await h.CallAsync(session, "SelectCharacterID", new PyInt(failingId)));
        Assert.NotNull(failure);
        Assert.Equal(previousId, session.CharacterId);
        Assert.Empty(hook.LeftCharacterIds);
        Assert.Equal(packetCount, sink.Sent.Count);
        Assert.Equal(previousId, Assert.Single(h.Online.Snapshot()));
        Assert.False(h.Online.IsOnline(failingId));

        hook.PreparationFailureCharacterId = 0;
        await h.CallAsync(session, "SelectCharacterID", new PyInt(failingId));
        Assert.Equal(failingId, session.CharacterId);
        Assert.False(h.Online.IsOnline(previousId));
    }

    [PgFact]
    public async Task Closing_during_entry_does_not_succeed_or_reenter_the_previous_runtime()
    {
        var hook = new FailingEntryHook();
        await using var h = await Harness.CreateAsync(entryHooks: [hook]);
        var (session, _) = await h.LoginAsync("entry-disconnect");
        var previousId = await CreateAsync(h, session, "Previous Disconnect Pilot");
        var nextId = await CreateAsync(h, session, "Next Disconnect Pilot");
        await h.CallAsync(session, "SelectCharacterID", new PyInt(previousId));
        var restoredPrevious = false;
        hook.OnSuccessfulEntry = enteringId =>
        {
            if (enteringId == nextId)
                session.Close();
            else
                restoredPrevious = true;
        };

        var failure = await Record.ExceptionAsync(async () =>
            await h.CallAsync(session, "SelectCharacterID", new PyInt(nextId)));
        Assert.NotNull(failure);
        Assert.False(restoredPrevious);
        Assert.Empty(h.Online.Snapshot());
    }

    [PgFact]
    public async Task Choosing_a_character_sets_the_session_and_sends_one_change_in_the_recorded_shape()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("rec-walk", role: 0x0140_0000_2024_0000);
        var id = await CreateAsync(h, session, "Recorded Pilot");
        var shipId = (await h.Characters.GetAsync(id))!.ShipId!.Value;

        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyInt(id), PyNone.Instance, PyBool.False));

        // Character selection resets the client before the single session change; skills follow it.
        Assert.Equal(3, sink.Sent.Count);
        Assert.Equal("OnCharacterSelected", NotificationName(sink.Sent[0]));
        Assert.Empty(NotificationArgs(sink.Sent[0]).Items);
        var packet = sink.Sent[1];
        Assert.Equal(new PyInt(16), ((PyTuple)((PyObject)packet).Args).Items[0]);
        Assert.Equal(new PyInt(12), ((PyTuple)((PyObject)sink.Sent[2]).Args).Items[0]);
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
    public async Task A_new_character_can_be_selected_with_the_clients_named_arguments()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("named-select");
        var id = await CreateAsync(h, session, "Named Select Pilot");

        var result = await ((Eve.Services.IService)h.Service).CallAsync(new Eve.Services.ServiceCall
        {
            Method = "SelectCharacterID",
            Args = Array.Empty<PyValue>(),
            Kwargs = new PyDict([
                new(new PyStr("charID"), new PyInt(id)),
                new(new PyStr("secondChoiceID"), PyNone.Instance),
                new(new PyStr("skipTutorial"), PyBool.False),
            ]),
            Session = session,
        });

        Assert.Equal(PyNone.Instance, result);
        Assert.Equal(id, session.CharacterId);
        Assert.Equal("OnCharacterSelected", NotificationName(sink.Sent[0]));
        Assert.Empty(NotificationArgs(sink.Sent[0]).Items);
        Assert.Equal(3, sink.Sent.Count);
    }

    [PgFact]
    public async Task Character_selection_data_includes_the_ship_name_and_live_training_projection()
    {
        await using var h = await Harness.CreateAsync(securityStatus: 1.25);
        var (session, _) = await h.LoginAsync("selection-state");
        var id = await CreateAsync(h, session, "Selection State Pilot");
        var character = await h.Characters.GetAsync(id, CancellationToken.None);
        Assert.NotNull(character);
        var shipId = character!.ShipId!.Value;
        await h.Items.SetCustomNameAsync(shipId, "Named Hull", CancellationToken.None);

        var initial = await h.Training.ViewAsync(id, character.AccountId, CancellationToken.None);
        var skill = initial.Skills.First(skill => skill.Level < 5);
        Assert.NotNull(await h.Training.StartAsync(id, character.AccountId, skill.TypeId, skill.Level + 1, [id], CancellationToken.None));
        var training = await h.Training.ViewAsync(id, character.AccountId, CancellationToken.None);
        var current = training.Current!;
        var data = (PyTuple)await h.CallAsync(session, "GetCharacterSelectionData");
        var row = Assert.Single(((PyList)data.Items[2]).Items);
        var fields = Recorded.Attributes(row).ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

        Assert.Equal(new PyStr("Named Hull"), fields["shipName"]);
        Assert.Equal(new PyReal(1.25), fields["securityRating"]);
        Assert.Equal(new PyReal(1.25), fields["securityStatus"]);
        Assert.Equal(new PyInt(training.TotalPoints), fields["skillPoints"]);
        Assert.Equal(new PyInt(current.TypeId), fields["skillTypeID"]);
        Assert.Equal(new PyInt(current.ToLevel), fields["toLevel"]);
        Assert.Equal(new PyLong(current.Start!.Value), fields["trainingStartTime"]);
        Assert.Equal(new PyLong(current.End!.Value), fields["trainingEndTime"]);
        Assert.Equal(new PyLong(training.Queue[^1].End!.Value), fields["queueEndTime"]);
        Assert.Equal(new PyInt(current.DestinationPoints), fields["finishSP"]);
        Assert.Equal(new PyInt(training.Skills.Single(skill => skill.TypeId == current.TypeId).SkillPoints), fields["trainedSP"]);
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
        Assert.Equal(3, secondSink.Sent.Count); // OnCharacterSelected, the session change and the skills notification
    }

    [PgFact]
    public async Task Choosing_the_same_character_twice_in_one_session_changes_nothing_the_second_time()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("pilot");
        var id = await CreateAsync(h, session, "Ann Lee");
        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));
        var announced = sink.Sent.Count;
        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));
        Assert.Equal(3, announced);
        // the character is the same: no session change, and it does not enter again (no second skills notification)
        Assert.Equal(announced, sink.Sent.Count);
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

    [PgFact]
    public async Task Skip_tutorial_third_argument_updates_air_npe_state_and_notifies_only_when_it_changes()
    {
        await using var h = await Harness.CreateAsync(newCharacterExperienceEnabled: true);
        var (session, sink) = await h.LoginAsync("skip-tutorial");
        var id = await CreateAsync(h, session, "Tutorial Pilot");
        var before = (await h.Characters.GetAsync(id))!;
        Assert.Equal(("tutorial", 1), (before.TutorialEntryMode, before.AirNpeState));
        sink.Sent.Clear();

        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyLong(id), new PyInt(0), PyBool.True));

        var skipped = (await h.Characters.GetAsync(id))!;
        Assert.Equal(("tutorial", "none", 3, false), (
            skipped.TutorialEntryMode, skipped.TutorialFirstLoginHandoff, skipped.AirNpeState, skipped.AirNpeRevealOnFirstLogin));
        var names = sink.Sent.Select(NotificationName).Where(name => name is not null).ToArray();
        Assert.Equal(new[] { "OnCharacterSelected", "OnServerSkillsChanged", "OnAirNpeStateChanged" }, names);
        var airNpeNotification = Assert.Single(sink.Sent, packet => NotificationName(packet) == "OnAirNpeStateChanged");
        Assert.Equal(new PyTuple([new PyInt(id), new PyInt(3)]), NotificationArgs(airNpeNotification));
        Assert.Single(sink.Sent, packet => ((PyInt)((PyTuple)((PyObject)packet).Args).Items[0]).Value == 16);

        sink.Sent.Clear();
        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyLong(id), PyNone.Instance, PyBool.True));
        Assert.DoesNotContain(sink.Sent, packet => NotificationName(packet) == "OnAirNpeStateChanged");
        Assert.Equal(3, (await h.Characters.GetAsync(id))!.AirNpeState);
    }

    [PgFact]
    public async Task First_login_air_npe_reveal_is_consumed_without_a_state_change_notification()
    {
        await using var h = await Harness.CreateAsync(newCharacterExperienceEnabled: true);
        var (session, sink) = await h.LoginAsync("npe-reveal");
        var id = await CreateAsync(h, session, "Reveal Pilot");
        await h.ExecuteAsync($"UPDATE {{characters}}.characters SET air_npe_state = 2, air_npe_reveal_on_first_login = TRUE WHERE id = {id}");
        sink.Sent.Clear();

        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "SelectCharacterID", new PyLong(id), PyNone.Instance, PyBool.False));

        var character = (await h.Characters.GetAsync(id))!;
        Assert.Equal(2, character.AirNpeState);
        Assert.False(character.AirNpeRevealOnFirstLogin);
        Assert.DoesNotContain(sink.Sent, packet => NotificationName(packet) == "OnAirNpeStateChanged");
    }

    [PgFact]
    public async Task Get_character_info_lists_owned_characters_and_returns_detail_for_an_id()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("character-info");
        var id = await CreateAsync(h, session, "Info Pilot");

        var rows = (PyList)await h.CallAsync(session, "GetCharacterInfo");
        var row = Recorded.Attributes(Assert.Single(rows.Items)).ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal(new PyInt(id), row["characterID"]);
        Assert.Equal(new PyStr("Info Pilot"), row["characterName"]);

        var detail = Recorded.Attributes(await h.CallAsync(session, "GetCharacterInfo", new PyLong(id)))
            .ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal(new PyInt(id), detail["characterID"]);
        Assert.Equal(new PyInt(1), detail["bloodlineID"]);

        var (other, _) = await h.LoginAsync("character-info-other");
        Assert.Empty(((PyList)await h.CallAsync(other, "GetCharacterInfo")).Items);
    }

    [PgFact]
    public async Task Qa_starter_system_ids_are_distinct_and_sorted_from_school_catalog_data()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("qa-starters");

        var ids = (PyList)await h.CallAsync(session, "GetQAStarterSystemIDs");
        Assert.Equal(new PyList([new PyInt(30000001), new PyInt(30002775)]), ids);
    }

    [PgFact]
    public async Task Update_character_gender_persists_the_normalized_gender_and_updates_the_matching_session()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("gender-update");
        var id = await CreateAsync(h, session, "Gender Pilot");
        session.Set("charid", new PyInt(id));

        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "UpdateCharacterGender", new PyLong(id), new PyInt(2)));

        Assert.Equal(2, (await h.Characters.GetAsync(id))!.Gender);
        Assert.Equal(new PyInt(2), session.Get("genderID"));
        Assert.Equal(new PyInt(2), session.Get("genderid"));

        await h.CallAsync(session, "UpdateCharacterGender", new PyLong(id), new PyInt(3));
        Assert.Equal(1, (await h.Characters.GetAsync(id))!.Gender);
        await h.CallAsync(session, "UpdateCharacterGender", new PyLong(id));
        Assert.Equal(1, (await h.Characters.GetAsync(id))!.Gender);
    }

    [PgFact]
    public async Task Update_character_bloodline_persists_catalog_identity_and_updates_the_matching_session()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("bloodline-update");
        var id = await CreateAsync(h, session, "Bloodline Pilot");
        session.Set("charid", new PyInt(id));

        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "UpdateCharacterBloodline", new PyLong(id), new PyInt(3)));

        var character = (await h.Characters.GetAsync(id))!;
        Assert.Equal((3, 2, 1375), (character.BloodlineId, character.RaceId, character.TypeId));
        Assert.Equal(new PyInt(3), session.Get("bloodlineID"));
        Assert.Equal(new PyInt(3), session.Get("bloodlineid"));
        Assert.Equal(new PyInt(2), session.Get("raceID"));
        Assert.Equal(new PyInt(2), session.Get("raceid"));
        Assert.Equal(new PyInt(1375), session.Get("characterTypeID"));
    }

    [PgFact]
    public async Task Cancel_character_delete_prepare_checks_ownership_and_online_state_then_clears_the_queue()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("delete-cancel");
        var id = await CreateAsync(h, session, "Delete Pilot");
        var (other, _) = await h.LoginAsync("delete-cancel-other");
        var otherId = await CreateAsync(h, other, "Other Pilot");
        await h.ExecuteAsync($"UPDATE {{characters}}.characters SET delete_prepare_at = '2025-01-01T00:00:00Z' WHERE id IN ({id}, {otherId})");

        var foreignError = await ErrorOfAsync(() => h.CallAsync(session, "CancelCharacterDeletePrepare", new PyLong(otherId)));
        Assert.Equal("That character is not available on this account.", InfoText(foreignError));
        Assert.NotNull((await h.Characters.GetAsync(otherId))!.DeletePrepareAt);

        Assert.True(h.Online.TryEnter(id, 123));
        var onlineError = await ErrorOfAsync(() => h.CallAsync(session, "CancelCharacterDeletePrepare", new PyLong(id)));
        Assert.Equal("That character is currently online and cannot be deleted.", InfoText(onlineError));
        h.Online.Leave(123);

        Assert.Equal(PyNone.Instance, await h.CallAsync(session, "CancelCharacterDeletePrepare", new PyLong(id)));
        Assert.Null((await h.Characters.GetAsync(id))!.DeletePrepareAt);
    }

    /// <summary>The <c>info</c> text of a <c>CustomInfo</c> user error.</summary>
    private static string? InfoText(MachoError error)
    {
        var header = (PyTuple)((PyObjectEx)error.Payload).Header;
        var dict = (PyDict)((PyTuple)header.Items[1]).Items[1];
        return JsSemantics.StringValue(dict.Entries.Single(e => JsSemantics.StringValue(e.Key) == "info").Value);
    }

    private sealed class FailingEntryHook : Eve.Services.ICharacterEntryHook
    {
        public long FailureCharacterId { get; set; }
        public long PreparationFailureCharacterId { get; set; }
        public List<long> PreparedCharacterIds { get; } = [];
        public List<long> LeftCharacterIds { get; } = [];
        public Action? BeforeFailure { get; set; }
        public Action<long>? OnSuccessfulEntry { get; set; }

        public ValueTask<IReadOnlyList<(string Name, PyValue Value)>> GetSessionAttributesAsync(
            ClientSession session, long characterId, CancellationToken cancellationToken)
        {
            PreparedCharacterIds.Add(characterId);
            if (characterId == PreparationFailureCharacterId)
                throw new InvalidOperationException("Injected character-preparation failure.");
            return ValueTask.FromResult<IReadOnlyList<(string Name, PyValue Value)>>([]);
        }

        public ValueTask OnCharacterLeavingAsync(
            ClientSession session, long characterId, CancellationToken cancellationToken)
        {
            LeftCharacterIds.Add(characterId);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnCharacterEnteredAsync(
            ClientSession session, long characterId, CancellationToken cancellationToken)
        {
            if (characterId == FailureCharacterId)
            {
                BeforeFailure?.Invoke();
                throw new InvalidOperationException("Injected character-entry failure.");
            }
            OnSuccessfulEntry?.Invoke(characterId);
            return ValueTask.CompletedTask;
        }
    }

    private static string? NotificationName(PyValue packet)
    {
        if (packet is not PyObject { Name: PyStr name, Args: PyTuple args } ||
            !name.Value.EndsWith("Notification", StringComparison.Ordinal))
        {
            return null;
        }
        return Eve.Macho.MachoAddress.Decode(args.Items[2]).BroadcastId;
    }

    private static PyTuple NotificationArgs(PyValue packet)
    {
        var fields = (PyTuple)((PyObject)packet).Args;
        var envelope = (PyTuple)((PyTuple)fields.Items[4]).Items[0];
        var notification = (PyTuple)MarshalCodec.Decode(((PyBuffer)envelope.Items[1]).Value);
        return (PyTuple)((PyTuple)notification.Items[1]).Items[1];
    }

    private static List<SessionChange> ChangesOf(PyValue packet)
    {
        var payload = (PyTuple)((PyTuple)((PyObject)packet).Args).Items[4];
        var dict = (PyDict)((PyTuple)payload.Items[1]).Items[1];
        return [.. dict.Entries.Select(e => new SessionChange(JsSemantics.StringValue(e.Key)!, ((PyTuple)e.Value).Items[0], ((PyTuple)e.Value).Items[1]))];
    }
}
