using Eve.Characters;
using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;
using Eve.Ships;
using Eve.Services;
using Eve.Services.Core;
using Eve.GameStore;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The jump clone service through the Host's registry: the state the client reads (eve-logic
/// <c>clonejumpsvc.py</c>, <c>GetCloneState</c>, <c>GetStationCloneState</c>, <c>ValidateInstallJumpClone</c>), the bound
/// calls, the cache notifications, and the rows surviving a Host restart.
/// </summary>
[Collection("host-composition")]
public sealed partial class CharacterHostTests
{
    private const long ProxyNode = MachoMessage.ProxyNodeId;

    [PgFact]
    public async Task Air_npe_skip_clears_the_client_tutorial_and_persists_through_host_restart()
    {
        var previousMode = Environment.GetEnvironmentVariable("EVE_NEW_CHARACTER_EXPERIENCE_MODE");
        Environment.SetEnvironmentVariable("EVE_NEW_CHARACTER_EXPERIENCE_MODE", "tutorial");
        try
        {
            await using var rig = await CharacterHostRig.CreateAsync();
            var character = await rig.CreateCharacterAsync("tutorial", 1);
            var sink = new RecordingPacketSink();
            var session = new ClientSession { CharacterId = character, Sink = sink };
            session.Set("charid", new PyLong(character));
            var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("air_npe"));

            Assert.IsType<PyNone>(await CallAsync(service, "skip_air_npe", session));
            var notifications = Notifications(sink);
            Assert.Equal(new[] { "OnTutorialSkipped", "OnAirNpeStateChanged" },
                notifications.Select(notification => notification.Name));
            Assert.Empty(notifications[0].Args);
            Assert.Equal(new PyValue[] { new PyInt(character), new PyInt(3) }, notifications[1].Args);

            var skipped = await rig.Characters.GetAsync(character);
            Assert.Equal(3, skipped!.AirNpeState);
            Assert.Equal("none", skipped.TutorialFirstLoginHandoff);

            await rig.RestartAsync();
            Assert.Equal(3, (await rig.Characters.GetAsync(character))!.AirNpeState);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EVE_NEW_CHARACTER_EXPERIENCE_MODE", previousMode);
        }
    }

    [PgFact]
    public async Task Host_character_selection_sends_the_subscription_time_before_the_session_change()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        await rig.SetFactionWarfareEnlistmentAsync(Eve.FactionWarfare.FwStore.CorporationKind, 900_001_001, 500_001);
        var sink = new RecordingPacketSink();
        var session = new ClientSession(sink) { SessionId = 55_001, ClientId = 55_002, UserId = 1 };
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));

        Assert.IsType<PyNone>(await CallAsync(service, "SelectCharacterID", session, new PyLong(character)));

        var notifications = Notifications(sink);
        Assert.Equal(new[] { "OnSubscriptionTimeUpdateServer" },
            notifications.Select(notification => notification.Name));
        Assert.Equal(new PyLong(OmegaLicenses.DefaultOmegaExpiryFileTime), Assert.Single(notifications[0].Args));
        Assert.Equal(500_001, session.GetInt("warfactionid"));
        Assert.Equal(
            new long[] { (long)MachoMessage.Notification, (long)MachoMessage.SessionChangeNotification },
            sink.Sent.Select(packet => Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items[0]).Value));
    }

    [PgFact]
    public async Task Host_character_selection_tells_alpha_and_expired_accounts_none_even_when_selection_fails()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        int expiredAccount;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"INSERT INTO {rig.IdentitySchema}.accounts (user_name, password_hash) VALUES ('expired-subscription-test', '\\x00'::bytea) RETURNING id",
            connection))
            expiredAccount = (int)(await command.ExecuteScalarAsync())!;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE {rig.IdentitySchema}.accounts SET subscription_end_at = $2 WHERE id = $1", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = expiredAccount });
            command.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow.AddDays(-1) });
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        async Task AssertFailedSelectionAnnouncesNone(long userId, long sessionId)
        {
            var sink = new RecordingPacketSink();
            var session = new ClientSession(sink) { SessionId = sessionId, ClientId = sessionId + 1, UserId = userId };
            var error = await Assert.ThrowsAsync<MachoWrappedException>(
                () => CallAsync(service, "SelectCharacterID", session, new PyLong(character)));
            Assert.True(error.Error.IsUserError);
            var notification = Assert.Single(Notifications(sink));
            Assert.Equal("OnSubscriptionTimeUpdateServer", notification.Name);
            Assert.Same(PyNone.Instance, Assert.Single(notification.Args));
        }

        await AssertFailedSelectionAnnouncesNone(userId: 99, sessionId: 55_101);
        await AssertFailedSelectionAnnouncesNone(expiredAccount, sessionId: 55_201);
    }

    [PgFact]
    public async Task Public_record_in_a_list_reaches_the_client_with_membership_and_authoritative_militia_values()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        const long allianceId = 900_002_001;
        var created = DateTimeOffset.FromUnixTimeMilliseconds(rig.NowUnixMs).AddYears(-5);
        var joinedMs = rig.NowUnixMs - (long)TimeSpan.FromDays(14).TotalMilliseconds;
        await rig.SeedPublicProfileCorporationMemberAsync(character, allianceId, created, joinedMs);
        await rig.SetFactionWarfareEnlistmentAsync(Eve.FactionWarfare.FwStore.CorporationKind, 900_001_001, 500004);
        await rig.SetFactionWarfareEnlistmentAsync(Eve.FactionWarfare.FwStore.AllianceKind, allianceId, 500002);
        var session = new ClientSession { CharacterId = character };
        session.Set("charid", new PyLong(character));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));

        async Task<Dictionary<string, PyValue>> ReadFieldsAsync()
        {
            var list = Assert.IsType<PyList>(await CallAsync(service, "GetPublicInfo3", session, new PyInt(character)));
            var record = Assert.IsType<PyObject>(Assert.Single(list.Items));
            return ((PyDict)record.Args).Entries.ToDictionary(entry => Eve.Macho.JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        }

        var fields = await ReadFieldsAsync();
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(created.ToUnixTimeMilliseconds())), fields["createDateTime"]);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(joinedMs)), fields["startDateTime"]);
        Assert.NotEqual(fields["createDateTime"], fields["startDateTime"]);
        Assert.Equal(new PyInt(500004), fields["militiaFactionID"]);

        await rig.SetFactionWarfareEnlistmentAsync(Eve.FactionWarfare.FwStore.CorporationKind, 900_001_001, 0);
        Assert.Equal(new PyInt(500002), (await ReadFieldsAsync())["militiaFactionID"]);

        Assert.True(await rig.Characters.SetFactionWarfareStateAsync(character, 500001L, 0));
        Assert.Equal(new PyInt(500001), (await ReadFieldsAsync())["militiaFactionID"]);

        Assert.True(await rig.Characters.SetFactionWarfareStateAsync(character, null, 0));
        await rig.SetFactionWarfareEnlistmentAsync(Eve.FactionWarfare.FwStore.AllianceKind, allianceId, 0);
        Assert.Equal(PyNone.Instance, (await ReadFieldsAsync())["militiaFactionID"]);
    }

    [PgFact]
    public async Task Achievement_tracker_updates_are_validated_before_persisting_and_survive_host_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character };
        session.Set("charid", new PyLong(character));
        var tracker = new Eve.Characters.AchievementTrackerStore(rig.Source, rig.CharactersSchema);
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("achievementTrackerMgr"));

        var invalid = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service,
            "UpdateClientAchievmentsAndCounters", session, new PyDict([new(new PyStr("9"), new PyLong(100))]), new PyDict([])));
        Assert.Equal("ACHIEVEMENT_STATE_INVALID", invalid.Error.UserErrorMessage);
        Assert.Null(await tracker.ReadAsync(character));

        var completed = new PyDict([new(new PyInt(9), new PyLong(134273341290000000))]);
        var eventCounts = new PyDict([new(new PyStr("client_openMap"), new PyInt(3))]);
        Assert.IsType<PyNone>(await CallAsync(service, "UpdateClientAchievmentsAndCounters", session, completed, eventCounts));
        var persisted = Assert.IsType<Eve.Characters.AchievementTrackerRecord>(await tracker.ReadAsync(character));
        Assert.Equal([(9, 134273341290000000L)], persisted.Completed);
        Assert.Equal([("client_openMap", 3L)], persisted.EventCounts);

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("achievementTrackerMgr"));
        var state = Assert.IsType<PyDict>(await CallAsync(service, "GetCompletedAchievementsAndClientEventCount", session));
        Assert.Equal(completed, Field(state, "completedDict"));
        Assert.Equal(new PyDict([new(new PyStr("client_openMap"), new PyLong(3))]), Field(state, "eventDict"));
    }

    [PgFact]
    public async Task Skill_injector_applies_sequential_awards_and_persists_points_and_inventory_through_host()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        rig.Sessions.Enter(character, session);
        var skills = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var sde = new Eve.Sde.SdeReader(rig.Source);
        var catalog = await Eve.Skills.SkillCatalog.LoadAsync(sde);
        var large = catalog.ItemTypeId("Large Skill Injector")!.Value;
        var itemStore = new PgItemStore(rig.Source, rig.InventorySchema);
        var itemId = await CreateHostItemAsync(rig, itemStore,
            new NewItem(large, character, rig.Station, 4, 2, Singleton: false));
        var store = new PgSkillStore(rig.Source, rig.SkillsSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.GrantFreeSkillPointsAsync(connection, transaction, character, 4_501_000);
            await transaction.CommitAsync();
        }

        Assert.Equal(new PyInt(900_000), await CallAsync(skills, "InjectSkillpoints", session,
            new PyLong(itemId), new PyInt(2)));
        Assert.Equal(5_401_000, await store.FreeSkillPointsAsync(character));
        Assert.Null(await itemStore.GetAsync(itemId));
        Assert.Equal(new[] { "OnItemChange", "OnFreeSkillPointsChanged" }, Notifications(sink).Select(n => n.Name));

        await rig.RestartAsync();
        Assert.Equal(5_401_000, await new PgSkillStore(rig.Source, rig.SkillsSchema).FreeSkillPointsAsync(character));
        Assert.Null(await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(itemId));
    }

    [PgFact]
    public async Task Daily_alpha_injector_preview_and_use_return_the_client_error_after_downtime_use()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, UserId = 1 };
        session.Set("stationid", new PyInt(rig.Station));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var catalog = await Eve.Skills.SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var dailyAlpha = catalog.ItemTypeId("Daily Alpha Injector");
        Assert.NotNull(dailyAlpha);
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var first = await CreateHostItemAsync(rig, items,
            new NewItem(dailyAlpha.Value, character, rig.Station, 4, 1, Singleton: true));
        var second = await CreateHostItemAsync(rig, items,
            new NewItem(dailyAlpha.Value, character, rig.Station, 4, 1, Singleton: true));

        Assert.Equal(new PyInt(50_000), await CallAsync(service, "InjectSkillpoints", session,
            new PyLong(first), new PyInt(1)));
        var previewError = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service,
            "CheckInjectionConstraints", session, new PyLong(second), new PyInt(1)));
        Assert.Equal("AlreadyInjectedToday", previewError.Error.UserErrorMessage);
        var useError = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service,
            "InjectSkillpoints", session, new PyLong(second), new PyInt(1)));
        Assert.Equal("AlreadyInjectedToday", useError.Error.UserErrorMessage);
        Assert.NotNull(await items.GetAsync(second));
    }

    private static async Task<long> CreateHostItemAsync(CharacterHostRig rig, PgItemStore store, NewItem item)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await store.CreateAsync(connection, transaction, item);
        await transaction.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task Jump_clones_are_read_changed_and_kept_through_the_host_registry_and_a_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("charid", new PyLong(character));
        session.Set("stationid", new PyInt(rig.Station));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        foreach (var method in new[]
        {
            "GetCloneState", "GetStationCloneState", "GetShipCloneState", "GetNumClonesInPilotsStructure",
            "ValidateInstallJumpClone", "SetJumpCloneName", "DestroyInstalledClone", "ResetLastCloneJumpTime",
        })
        {
            Assert.True(service.HasMethod(method), method);
        }

        Assert.Equal(new PyInt(ProxyNode), await CallAsync(service, "MachoResolveObject", session));

        // a new character: no clones, no last jump, and the install validation lists the missing skill
        var state = await BoundAsync(service, session, "GetCloneState");
        Assert.Empty(Rows(Field(state, "clones")));
        Assert.Empty(Rows(Field(state, "implants")));
        Assert.Equal(new PyLong(0), Field(state, "timeLastJump"));
        Assert.Empty(Rows(await BoundAsync(service, session, "GetStationCloneState")));
        Assert.Empty(Rows(await BoundAsync(service, session, "GetShipCloneState")));
        Assert.Equal(new PyInt(0), await BoundAsync(service, session, "GetNumClonesInPilotsStructure"));
        // with no limit, the usage label also shows (0 of 0), as the reference's validation does
        var errors = Assert.IsType<PyList>(await BoundAsync(service, session, "ValidateInstallJumpClone")).Items;
        Assert.Equal("UI/Medical/JumpCloneSkillReqNotMet", Assert.IsType<PyStr>(errors[0]).Value);
        Assert.Equal("UI/Medical/JumpCloneUsageAndCapacity", Assert.IsType<PyStr>(Assert.IsType<PyTuple>(errors[1]).Items[0]).Value);

        // the skills that set the limit: two Infomorph Psychology levels give room for two clones
        await rig.SetSkillLevelAsync(character, 24242, 1);
        await rig.SetSkillLevelAsync(character, 33407, 1);
        Assert.Empty(Assert.IsType<PyList>(await BoundAsync(service, session, "ValidateInstallJumpClone")).Items);

        var first = await rig.AddCloneAsync(character, rig.Station, "station", "Home body", [24242, 33399]);
        var other = await rig.AddCloneAsync(character, 1_000_000_000_051, "structure", "Far body", []);
        state = await BoundAsync(service, session, "GetCloneState");
        Assert.Equal(new[] { first, other }, Rows(Field(state, "clones")).Select(row => ((PyLong)row.Items[0]).Value).ToArray());
        Assert.Equal(new[] { 24242, 33399 }, Rows(Field(state, "implants")).Where(row => ((PyLong)row.Items[0]).Value == first)
            .Select(row => (int)((PyInt)row.Items[1]).Value).ToArray());
        var stationRows = Rows(await BoundAsync(service, session, "GetStationCloneState"));
        Assert.Equal(new[] { first }, stationRows.Select(row => ((PyLong)row.Items[0]).Value).ToArray());
        // docked in a station, not in a structure or ship: the count of the pilot's structure is none
        Assert.Equal(new PyInt(0), await BoundAsync(service, session, "GetNumClonesInPilotsStructure"));

        // rename: the client's name is kept, and the cache notifications follow in the reference's order
        Assert.IsType<PyNone>(await BoundAsync(service, session, "SetJumpCloneName", new PyLong(first), new PyStr("  Renamed  ")));
        Assert.Equal(new[] { "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated" },
            Notifications(sink).Select(n => n.Name).ToArray());
        Assert.Equal("Renamed", Rows(Field(await BoundAsync(service, session, "GetCloneState"), "clones"))
            .Select(row => ((PyWString)row.Items[2]).Value).First());
        await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(service, session, "SetJumpCloneName", new PyLong(other + 999), new PyStr("x")));
        Assert.Equal(3, Notifications(sink).Count);

        // a restart: the same clones and name are read from PostgreSQL by a new Host
        await rig.RestartAsync();
        var restarted = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        var afterRestart = await BoundAsync(restarted, session, "GetCloneState");
        Assert.Equal(new[] { first, other }, Rows(Field(afterRestart, "clones")).Select(row => ((PyLong)row.Items[0]).Value).ToArray());
        Assert.Equal("Renamed", ((PyWString)Rows(Field(afterRestart, "clones"))[0].Items[2]).Value);

        // destroy removes the clone and persists the same notification row returned by notificationMgr
        Assert.IsType<PyNone>(await BoundAsync(restarted, session, "DestroyInstalledClone", new PyLong(other)));
        Assert.Equal(new[] { first }, Rows(Field(await BoundAsync(restarted, session, "GetCloneState"), "clones"))
            .Select(row => ((PyLong)row.Items[0]).Value).ToArray());
        var notifications = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("notificationMgr"));
        var deleted = Assert.Single(Assert.IsType<PyList>(await CallAsync(notifications, "GetAllNotifications", session)).Items);
        Assert.Equal(new PyInt(56), Field(deleted, "typeID"));
        var deletionData = Field(deleted, "data");
        Assert.Equal(new PyInt(character), Field(deletionData, "locationOwnerID"));
        Assert.Equal(new PyInt(character), Field(deletionData, "destroyerID"));
        Assert.Empty(Assert.IsType<PyList>(Field(deletionData, "typeIDs")).Items);

        await rig.RestartAsync();
        var notificationsAfterRestart = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("notificationMgr"));
        var persistedDeletion = Assert.Single(Assert.IsType<PyList>(await CallAsync(notificationsAfterRestart, "GetAllNotifications", session)).Items);
        Assert.Equal(new PyInt(56), Field(persistedDeletion, "typeID"));

        var restartedClones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        Assert.IsType<PyNone>(await BoundAsync(restartedClones, session, "ResetLastCloneJumpTime"));
        Assert.Equal(new PyLong(0), Field(await BoundAsync(restartedClones, session, "GetCloneState"), "timeLastJump"));
    }

    [PgFact]
    public async Task Same_station_clone_switch_keeps_the_existing_time_last_jump_in_client_state()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, Sink = new RecordingPacketSink() };
        session.Set("stationid", new PyInt(rig.Station));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        var capsule = await GiveItemAsync(rig, character, 670, rig.Station, 4, singleton: true);
        await SetShipAsync(rig, character, capsule);
        await FundAsync(rig, character, 1_000_000m);
        var local = await rig.AddCloneAsync(character, rig.Station, "station", "Local body", []);
        var remote = await rig.AddCloneAsync(character, 60000611, "station", "Remote body", []);
        var previousJump = DateTimeOffset.UtcNow.AddHours(-1).ToFileTime();
        await using (var set = rig.Source.CreateCommand(
            $"UPDATE {rig.CharactersSchema}.characters SET time_last_clone_jump_filetime = $1 WHERE id = $2"))
        {
            set.Parameters.Add(new NpgsqlParameter { Value = previousJump });
            set.Parameters.Add(new NpgsqlParameter { Value = character });
            await set.ExecuteNonQueryAsync();
        }

        var before = await BoundAsync(service, session, "GetCloneState");
        Assert.Equal(new PyLong(previousJump), Field(before, "timeLastJump"));
        Assert.IsType<PyNone>(await BoundAsync(service, session, "CloneJump", new PyLong(rig.Station), new PyLong(local), new PyReal(900000.0), PyBool.False));
        var after = await BoundAsync(service, session, "GetCloneState");
        Assert.Equal(new PyLong(previousJump), Field(after, "timeLastJump"));

        await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(service, session, "CloneJump", new PyLong(60000611),
            new PyLong(remote), new PyReal(900000.0), PyBool.False));
        Assert.Equal(new PyLong(previousJump), Field(await BoundAsync(service, session, "GetCloneState"), "timeLastJump"));
    }

    [PgFact]
    public async Task Clone_jump_moves_the_body_charges_the_fee_starts_the_cooldown_and_keeps_all_of_it_through_a_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        var stations = await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var away = stations.All.OrderBy(s => s.Id).Where(s => s.Id != rig.Station).First();

        // the pilot is in its capsule, with one implant plugged in, and 1,000,000 ISK
        var capsule = await GiveItemAsync(rig, character, 670, rig.Station, 4, singleton: true);
        await SetShipAsync(rig, character, capsule);
        await GiveItemAsync(rig, character, 9941, character, 89, singleton: true);
        await FundAsync(rig, character, 1_000_000m);

        // a clone at the station it is docked in (replaced by the jump) and a clone elsewhere (the target)
        var target = await rig.AddCloneAsync(character, away.Id, "station", "Away body", [9941]);
        Assert.Equal(new[] { target }, Rows(Field(await BoundAsync(service, session, "GetCloneState"), "clones"))
            .Select(row => ((PyLong)row.Items[0]).Value).ToArray());

        // the jump: the clone is at another station, so the cooldown is not checked; the body leaves by the origin
        Assert.IsType<PyNone>(await BoundAsync(service, session, "CloneJump", new PyLong(away.Id), new PyLong(target), new PyReal(900000.0), PyBool.False));
        Assert.Equal(new PyInt(away.Id), session.Get("stationid"));
        Assert.Equal(new PyInt(away.SolarSystemId), session.Get("solarsystemid2"));
        Assert.Equal(new[]
        {
            "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
            "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
            "OnServerImplantsChanged", "OnJumpCloneTransitionCompleted",
        }, Notifications(sink).Select(n => n.Name).ToArray());
        var state = await BoundAsync(service, session, "GetCloneState");
        var clones = Rows(Field(state, "clones"));
        var left = Assert.Single(clones);
        Assert.Equal(new PyLong(rig.Station), left.Items[1]);
        Assert.NotEqual(new PyLong(0), Field(state, "timeLastJump"));
        Assert.Equal(new[] { 9941 }, Rows(Field(state, "implants")).Select(row => (int)((PyInt)row.Items[1]).Value).ToArray());
        Assert.Equal(new[] { 9941 }, await BodyImplantTypesAsync(rig, character));
        Assert.Equal(900_000m, 1_000_000m - await BalanceAsync(rig, character));
        Assert.Equal(away.Id, (await rig.Characters.GetAsync(character))!.StationId);

        // the cooldown: a second jump within 24 hours is refused, and nothing moves
        var replacement = left.Items[0] is PyLong replacementId ? replacementId.Value : 0;
        await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(service, session, "CloneJump", new PyLong(rig.Station), new PyLong(replacement), new PyReal(900000.0), PyBool.False));
        Assert.Equal(900_000m, 1_000_000m - await BalanceAsync(rig, character));
        Assert.Equal(away.Id, (await rig.Characters.GetAsync(character))!.StationId);

        // a restart keeps the clones, the place and the charge; the cooldown is still running
        await rig.RestartAsync();
        var restarted = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        Assert.Equal(new[] { replacement }, Rows(Field(await BoundAsync(restarted, session, "GetCloneState"), "clones"))
            .Select(row => ((PyLong)row.Items[0]).Value).ToArray());
        Assert.Equal(away.Id, (await rig.Characters.GetAsync(character))!.StationId);
        await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(restarted, session, "CloneJump", new PyLong(rig.Station), new PyLong(replacement), new PyReal(900000.0), PyBool.False));

        // a reset of the cooldown lets it jump back; with no ISK left the jump is refused with NotEnoughMoney and nothing moves
        await BoundAsync(restarted, session, "ResetLastCloneJumpTime");
        await FundAsync(rig, character, -(await BalanceAsync(rig, character)));
        await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(restarted, session, "CloneJump", new PyLong(rig.Station), new PyLong(replacement), new PyReal(900000.0), PyBool.False));
        Assert.Equal(away.Id, (await rig.Characters.GetAsync(character))!.StationId);
        Assert.Equal(new[] { replacement }, Rows(Field(await BoundAsync(restarted, session, "GetCloneState"), "clones"))
            .Select(row => ((PyLong)row.Items[0]).Value).ToArray());
        Assert.Equal(0m, await BalanceAsync(rig, character));
    }

    [PgFact]
    public async Task Clone_jump_sends_transition_notice_once_after_success_and_not_after_refusal()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        var stations = await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var away = stations.All.OrderBy(station => station.Id).First(station => station.Id != rig.Station);

        var capsule = await GiveItemAsync(rig, character, CharacterShips.CapsuleTypeId, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, capsule);
        await GiveItemAsync(rig, character, 9941, character, 89, singleton: true);
        await FundAsync(rig, character, 1_000_000m);
        var target = await rig.AddCloneAsync(character, away.Id, "station", "Away body", [9942]);

        await BoundAsync(service, session, "CloneJump", new PyLong(away.Id), new PyLong(target), new PyReal(900_000.0), PyBool.False);

        var successNotifications = Notifications(sink);
        var transition = Assert.Single(successNotifications, notification => notification.Name == "OnJumpCloneTransitionCompleted");
        Assert.Empty(transition.Args);
        Assert.Equal(new[]
        {
            "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
            "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
            "OnServerImplantsChanged", "OnJumpCloneTransitionCompleted",
        }, successNotifications.Select(notification => notification.Name).ToArray());

        sink.Sent.Clear();
        var replacement = Assert.Single(Rows(Field(await BoundAsync(service, session, "GetCloneState"), "clones")));
        var replacementId = Assert.IsType<PyLong>(replacement.Items[0]).Value;
        await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(service, session, "CloneJump", new PyLong(rig.Station),
            new PyLong(replacementId), new PyReal(900_000.0), PyBool.False));
        Assert.DoesNotContain("OnJumpCloneTransitionCompleted", Notifications(sink).Select(notification => notification.Name));
    }

    [PgFact]
    public async Task Clone_jump_keeps_SDE_marked_implants_that_follow_the_pilot_and_refreshes_the_client_cache()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        var stations = await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var away = stations.All.OrderBy(s => s.Id).First(s => s.Id != rig.Station);

        var capsule = await GiveItemAsync(rig, character, CharacterShips.CapsuleTypeId, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, capsule);
        await FundAsync(rig, character, 1_000_000m);
        await GiveItemAsync(rig, character, 9941, character, 89, singleton: true);
        await GiveItemAsync(rig, character, 33329, character, 89, singleton: true);
        var target = await rig.AddCloneAsync(character, away.Id, "station", "Away body", [9942]);

        await BoundAsync(service, session, "CloneJump", new PyLong(away.Id), new PyLong(target), new PyReal(900_000.0), PyBool.False);

        var statics = await Eve.Dogma.DogmaStatic.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        await statics.EnsureTypesAsync([9942, 33329]);
        var expectedBody = new[] { 9942, 33329 }
            .Select(typeId => (Slot: (int)statics.Type(typeId)!.Attributes[Eve.Ships.PilotAugmentations.ImplantSlotAttributeId], TypeId: typeId))
            .OrderBy(implant => implant.Slot).ThenBy(implant => implant.TypeId);
        var actualBody = (await BodyImplantTypesAsync(rig, character))
            .Select(typeId => (Slot: (int)statics.Type(typeId)!.Attributes[Eve.Ships.PilotAugmentations.ImplantSlotAttributeId], TypeId: typeId))
            .OrderBy(implant => implant.Slot).ThenBy(implant => implant.TypeId);

        var state = await BoundAsync(service, session, "GetCloneState");
        var replacement = Assert.Single(Rows(Field(state, "clones")));
        Assert.Equal(new[] { 9941 }, Rows(Field(state, "implants"))
            .Where(row => ((PyLong)row.Items[0]).Value == ((PyLong)replacement.Items[0]).Value)
            .Select(row => (int)((PyInt)row.Items[1]).Value).ToArray());
        Assert.Equal(expectedBody, actualBody);
        Assert.Equal(new[]
        {
            "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
            "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
            "OnServerImplantsChanged", "OnJumpCloneTransitionCompleted",
        }, Notifications(sink).Select(n => n.Name).ToArray());
    }

    [PgFact]
    public async Task Clone_jump_rebases_a_running_queue_when_implants_change_its_learning_rate()
    {
        const long startUnixMs = 1_800_000_000_000;
        await using var rig = await CharacterHostRig.CreateAsync(startUnixMs);
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        rig.Sessions.Enter(character, session);
        var cloneService = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var stations = await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var away = stations.All.OrderBy(station => station.Id).First(station => station.Id != rig.Station);
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var owned = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character))
            .Select(skill => skill.TypeId).ToHashSet();
        var skill = catalog.Skills.First(candidate => candidate.Published && candidate.Rank == 1
            && candidate.PrimaryAttribute == 166 && candidate.Needs.Count == 0 && !owned.Contains(candidate.TypeId));
        await rig.SetSkillLevelAsync(character, skill.TypeId, 0);

        var capsule = await GiveItemAsync(rig, character, CharacterShips.CapsuleTypeId, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, capsule);
        await FundAsync(rig, character, 1_000_000m);
        await GiveItemAsync(rig, character, 9941, character, 89, singleton: true);
        await GiveItemAsync(rig, character, 33329, character, 89, singleton: true);
        var target = await rig.AddCloneAsync(character, away.Id, "station", "Willpower body", [9942]);
        await BoundAsync(skillHandler, session, "SaveNewQueue", new PyList([Py.Tuple(new PyInt(skill.TypeId), new PyInt(1))]));
        var beforeQueue = Assert.IsType<PyList>(Assert.IsType<PyTuple>(await BoundAsync(skillHandler, session, "GetSkillQueueAndFreePoints")).Items[0]);
        var beforeLine = Assert.IsType<PyObject>(Assert.Single(beforeQueue.Items));
        var beforeEnd = Integer(Field(beforeLine, "trainingEndTime"));
        sink.Sent.Clear();

        await BoundAsync(cloneService, session, "CloneJump", new PyLong(away.Id), new PyLong(target), new PyReal(900_000.0), PyBool.False);

        var afterQueue = Assert.IsType<PyList>(Assert.IsType<PyTuple>(await BoundAsync(skillHandler, session, "GetSkillQueueAndFreePoints")).Items[0]);
        var afterLine = Assert.IsType<PyObject>(Assert.Single(afterQueue.Items));
        Assert.Equal(Integer(Field(beforeLine, "trainingStartSP")), Integer(Field(afterLine, "trainingStartSP")));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs).ToFileTime(), Integer(Field(afterLine, "trainingStartTime")));
        Assert.True(Integer(Field(afterLine, "trainingEndTime")) > beforeEnd);
        var notificationNames = Notifications(sink).Select(notification => notification.Name).ToArray();
        var expectedNotificationNames = new[]
        {
            "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
            "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
            "OnServerImplantsChanged", "OnModuleAttributeChanges", "OnModuleAttributeChanges",
            "OnServerBrainUpdated", "OnNewSkillQueueSaved", "OnMultipleCharactersTrainingUpdated", "OnJumpCloneTransitionCompleted",
        };
        Assert.True(expectedNotificationNames.SequenceEqual(notificationNames), string.Join(", ", notificationNames));
    }

    [PgFact]
    public async Task Concurrent_clone_jumps_commit_only_one_activation_and_refuse_the_other_with_the_cooldown_error()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, Sink = new RecordingPacketSink() };
        session.Set("stationid", new PyInt(rig.Station));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        var stations = await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var destinations = stations.All.Where(station => station.Id != rig.Station).OrderBy(station => station.Id).Take(2).ToArray();
        Assert.Equal(2, destinations.Length);
        var capsule = await GiveItemAsync(rig, character, 670, rig.Station, 4, singleton: true);
        await SetShipAsync(rig, character, capsule);
        await FundAsync(rig, character, 2_000_000m);
        var clones = new long[2];
        for (var index = 0; index < destinations.Length; index++)
            clones[index] = await rig.AddCloneAsync(character, destinations[index].Id, "station", $"Concurrent {index}", []);

        async Task<(PyValue? Value, MachoWrappedException? Error)> Jump(int index)
        {
            try
            {
                return (await BoundAsync(service, session, "CloneJump", new PyLong(destinations[index].Id),
                    new PyLong(clones[index]), new PyReal(900_000.0), PyBool.False), null);
            }
            catch (MachoWrappedException error)
            {
                return (null, error);
            }
        }

        var outcomes = await Task.WhenAll(Jump(0), Jump(1));

        Assert.Single(outcomes, outcome => outcome.Value is not null);
        var refused = Assert.Single(outcomes, outcome => outcome.Error is not null).Error!;
        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        var notifyText = refused.Error.Payload is PyObjectEx
        {
            Header: PyTuple { Items: [_, PyTuple { Items: [_, PyDict arguments] }, ..] },
        } ? arguments.Entries.FirstOrDefault(entry => entry.Key is PyStr { Value: "notify" }).Value as PyStr : null;
        Assert.Equal("UI/CharacterSheet/CharacterSheetWindow/JumpCloneScroll/JumpCooldownError", notifyText?.Value);
        Assert.Equal(1_100_000m, await BalanceAsync(rig, character));
        Assert.Contains((await rig.Characters.GetAsync(character))!.StationId, destinations.Select(destination => destination.Id));
        Assert.Equal(2, (await new PgJumpCloneStore(rig.Source, rig.CharactersSchema).ReadAsync(character)).Clones.Count);
    }

    [PgFact]
    public async Task A_clone_jump_ends_the_normal_boosters_and_the_cerebral_accelerator_goes_on_with_the_pilot()
    {
        // the wiki: a jump ends a normal booster (its row and item go, the pilot is told) and keeps a cerebral accelerator
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        rig.Sessions.Enter(character, session);
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        var stations = await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var away = stations.All.OrderBy(s => s.Id).Where(s => s.Id != rig.Station).First();
        const int improvedCrashBooster = 10151;
        const int standardCerebralAccelerator = 49539;
        const int boosterFlag = 88;
        var expiry = DateTime.UtcNow.AddHours(1).ToFileTimeUtc();

        var capsule = await GiveItemAsync(rig, character, 670, rig.Station, 4, singleton: true);
        await SetShipAsync(rig, character, capsule);
        await FundAsync(rig, character, 1_000_000m);
        var target = await rig.AddCloneAsync(character, away.Id, "station", "Away body", []);
        var normal = await GiveItemAsync(rig, character, improvedCrashBooster, character, boosterFlag, singleton: true);
        var cerebral = await GiveItemAsync(rig, character, standardCerebralAccelerator, character, boosterFlag, singleton: true);
        await InsertBoosterAsync(rig, normal, character, improvedCrashBooster, expiry);
        await InsertBoosterAsync(rig, cerebral, character, standardCerebralAccelerator, expiry);
        Assert.Equal(2, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE character_id = {character}"));

        await BoundAsync(service, session, "CloneJump", new PyLong(away.Id), new PyLong(target), new PyReal(900000.0), PyBool.False);

        Assert.Equal(0, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.InventorySchema}.items WHERE id = {normal}"));
        Assert.Equal(0, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE item_id = {normal}"));
        Assert.Equal(1, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE item_id = {cerebral} AND booster_type_id = {standardCerebralAccelerator}"));
        Assert.Equal(1, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.InventorySchema}.items WHERE id = {cerebral}"));
        Assert.Contains("OnServerBoostersChanged", Notifications(sink).Select(n => n.Name));

        // the cerebral accelerator is still active after a restart; the normal booster is not coming back
        await rig.RestartAsync();
        Assert.Equal(1, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE character_id = {character}"));
    }

    [PgFact]
    public async Task An_implant_and_an_expert_system_are_kept_through_a_host_restart_and_the_expert_grant_still_applies()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        const int memoryAugmentation = 9941;
        const int coreShipOperations = 57208; // grants Hull Upgrades at level 5 for seven days
        await GiveItemAsync(rig, character, memoryAugmentation, character, 89, singleton: true);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = new PgExpertSystemStore(rig.Source, rig.SkillsSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.UpsertAsync(connection, transaction, new ExpertSystemEntry(character, coreShipOperations, now, now + 7 * ExpertSystemRules.Day, null, "item", now));
            await transaction.CommitAsync();
        }

        await rig.RestartAsync();

        Assert.Equal(new[] { memoryAugmentation }, await BodyImplantTypesAsync(rig, character));
        var entries = await new PgExpertSystemStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character);
        Assert.Equal(coreShipOperations, Assert.Single(entries).TypeId);
        Assert.Equal(5, ExpertSystemRules.GrantsOf(ExpertSystemCatalog.LoadEmbedded(), entries, now)[3394]);
        // the restarted host answers the pilot's list through its registry
        var manager = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("expertSystemMgr"));
        Assert.IsType<PyDict>(await CallAsync(manager, "GetMyExpertSystems", new ClientSession { CharacterId = character, Sink = new RecordingPacketSink() }));
    }

    [PgFact]
    public async Task Alpha_cannot_inject_an_Omega_implant_but_can_retry_after_an_account_upgrade()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var accountId = await rig.CreateAccountAsync();
        Assert.Equal(1, accountId);
        var character = await rig.CreateCharacterAsync();
        await rig.SetSkillLevelAsync(character, 3411, 4);
        var accounts = new PgAccountDirectory(rig.Source, new AccountOptions(), rig.IdentitySchema);
        accounts.SetSubscriptionEndsAt((uint)accountId, DateTimeOffset.UtcNow.AddDays(-1));
        var implant = await GiveItemAsync(rig, character, 10208, rig.Station, 4, singleton: true);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(dogma, "InjectImplant", session, new PyLong(implant)));
        Assert.Equal("This implant requires an Omega clone.", NotifyText(refused));
        Assert.Empty(Notifications(sink));
        Assert.Empty(await BodyImplantTypesAsync(rig, character));
        var stored = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(implant);
        Assert.Equal((rig.Station, 4), (stored!.LocationId, stored.Flag));

        accounts.SetSubscriptionEndsAt((uint)accountId, DateTimeOffset.UtcNow.AddDays(1));
        Assert.IsType<PyNone>(await CallAsync(dogma, "InjectImplant", session, new PyLong(implant)));
        Assert.Equal(["OnItemChange", "OnServerImplantsChanged", "OnJumpCloneCacheInvalidated"], Notifications(sink).Select(notification => notification.Name));
        Assert.Equal([10208], await BodyImplantTypesAsync(rig, character));

        var retried = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(dogma, "InjectImplant", session, new PyLong(implant)));
        Assert.Equal("That implant is no longer available.", NotifyText(retried));
        Assert.Equal(3, Notifications(sink).Count);
        Assert.Equal([10208], await BodyImplantTypesAsync(rig, character));
    }

    [PgFact]
    public async Task Alpha_can_inject_an_unrestricted_implant_after_the_existing_skill_gate_is_met()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var accountId = await rig.CreateAccountAsync();
        Assert.Equal(1, accountId);
        var character = await rig.CreateCharacterAsync();
        var accounts = new PgAccountDirectory(rig.Source, new AccountOptions(), rig.IdentitySchema);
        accounts.SetSubscriptionEndsAt((uint)accountId, DateTimeOffset.UtcNow.AddDays(-1));
        var implant = await GiveItemAsync(rig, character, 9941, rig.Station, 4, singleton: true);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        var missingSkill = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(dogma, "InjectImplant", session, new PyLong(implant)));
        Assert.Equal("You do not have the required skills to plug in that implant.", NotifyText(missingSkill));
        Assert.Empty(Notifications(sink));
        Assert.Empty(await BodyImplantTypesAsync(rig, character));

        await rig.SetSkillLevelAsync(character, 3411, 1);
        Assert.IsType<PyNone>(await CallAsync(dogma, "InjectImplant", session, new PyLong(implant)));
        Assert.Equal(["OnItemChange", "OnServerImplantsChanged", "OnJumpCloneCacheInvalidated"], Notifications(sink).Select(notification => notification.Name));
        Assert.Equal([9941], await BodyImplantTypesAsync(rig, character));
    }

    [PgFact]
    public async Task An_Omega_implant_stays_worn_and_active_after_the_account_lapses()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var accountId = await rig.CreateAccountAsync();
        Assert.Equal(1, accountId);
        var character = await rig.CreateCharacterAsync();
        await rig.SetSkillLevelAsync(character, 3411, 4);
        var accounts = new PgAccountDirectory(rig.Source, new AccountOptions(), rig.IdentitySchema);
        accounts.SetSubscriptionEndsAt((uint)accountId, DateTimeOffset.UtcNow.AddDays(1));
        var capsule = await GiveItemAsync(rig, character, 670, rig.Station, 4, singleton: true);
        await SetShipAsync(rig, character, capsule);
        var implant = await GiveItemAsync(rig, character, 10208, rig.Station, 4, singleton: true);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        Assert.IsType<PyNone>(await CallAsync(dogma, "InjectImplant", session, new PyLong(implant)));
        Assert.Equal(["OnItemChange", "OnServerImplantsChanged", "OnJumpCloneCacheInvalidated"], Notifications(sink).Select(notification => notification.Name));
        accounts.SetSubscriptionEndsAt((uint)accountId, DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal([10208], await BodyImplantTypesAsync(rig, character));
        var nested = Py.Tuple(new PyStr("GetAllInfo"), Py.Tuple(PyBool.True, PyBool.True, PyNone.Instance), PyNone.Instance);
        var response = Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", session,
            Py.Tuple(new PyInt(rig.Station), new PyInt(3)), nested));
        var characters = Assert.IsType<PyDict>(((PyTuple)Field(response.Items[1], "charInfo")).Items[0]);
        var characterInfo = Assert.Single(characters.Entries).Value;
        var attributes = Assert.IsType<PyDict>(Field(characterInfo, "attributes"));
        var memory = Assert.Single(attributes.Entries, entry => ((PyInt)entry.Key).Value == 166).Value;
        Assert.Equal(new PyInt(24), memory);
    }

    [PgFact]
    public async Task Concurrent_Omega_implant_injections_for_the_same_slot_only_install_one()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var accountId = await rig.CreateAccountAsync();
        Assert.Equal(1, accountId);
        var character = await rig.CreateCharacterAsync();
        await rig.SetSkillLevelAsync(character, 3411, 5);
        var accounts = new PgAccountDirectory(rig.Source, new AccountOptions(), rig.IdentitySchema);
        accounts.SetSubscriptionEndsAt((uint)accountId, DateTimeOffset.UtcNow.AddDays(1));
        var first = await GiveItemAsync(rig, character, 10208, rig.Station, 4, singleton: true);
        var second = await GiveItemAsync(rig, character, 10209, rig.Station, 4, singleton: true);
        var firstSink = new RecordingPacketSink();
        var secondSink = new RecordingPacketSink();
        var firstSession = new ClientSession { CharacterId = character, Sink = firstSink };
        var secondSession = new ClientSession { CharacterId = character, Sink = secondSink };
        firstSession.Set("stationid", new PyInt(rig.Station));
        secondSession.Set("stationid", new PyInt(rig.Station));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        async Task<MachoWrappedException?> TryInjectAsync(long itemId, ClientSession session)
        {
            try
            {
                await CallAsync(dogma, "InjectImplant", session, new PyLong(itemId));
                return null;
            }
            catch (MachoWrappedException error)
            {
                return error;
            }
        }

        var results = await Task.WhenAll(TryInjectAsync(first, firstSession), TryInjectAsync(second, secondSession));
        var refused = Assert.Single(results, error => error is not null);
        Assert.Equal("You already have an implant in that slot.", NotifyText(refused!));
        Assert.Single(results, error => error is null);
        var wornImplants = await BodyImplantTypesAsync(rig, character);
        Assert.Contains(Assert.Single(wornImplants), new[] { 10208, 10209 });
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var firstItem = (await items.GetAsync(first))!;
        var secondItem = (await items.GetAsync(second))!;
        Assert.Equal(1, new[] { firstItem, secondItem }.Count(item => item.Flag == 89));
        Assert.Equal(1, new[] { firstItem, secondItem }.Count(item => item.LocationId == rig.Station && item.Flag == 4));
        var successSink = results[0] is null ? firstSink : secondSink;
        var failedSink = results[0] is null ? secondSink : firstSink;
        Assert.Equal(["OnItemChange", "OnServerImplantsChanged", "OnJumpCloneCacheInvalidated"], Notifications(successSink).Select(notification => notification.Name));
        Assert.Empty(Notifications(failedSink));
    }

    [PgFact]
    public async Task A_skill_booster_lapses_at_its_expiry_through_the_durable_job_and_an_online_pilot_is_told()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        rig.Sessions.Enter(character, new ClientSession { CharacterId = character, Sink = sink });
        var booster = await GiveItemAsync(rig, character, 49539, character, 88, singleton: true);
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await InsertBoosterAsync(rig, booster, character, 49539, expiredAt.UtcDateTime.ToFileTimeUtc());
        Assert.Equal(1, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE item_id = {booster}"));

        // the job of the activation runs at the expiry: the row and the item are gone, and the pilot is told
        await rig.Scheduler.ScheduleAsync(new Eve.Scheduler.JobSpec(Eve.Services.Skills.SkillBoosterLapse.JobKind, expiredAt.ToUnixTimeMilliseconds())
        {
            Key = $"{character}:{booster}",
            Payload = new Eve.Services.Skills.SkillBoosterLapse.BoosterJob(character, booster),
        });
        // the host's runner (Foundations starts it) claims the job when it is scheduled, so this test does not run it: wait, bounded, for the
        // runner to finish it (a finished job is removed by the scheduler)
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Eve.Scheduler.JobInfo? job;
        while ((job = await rig.Scheduler.FindAsync(Eve.Services.Skills.SkillBoosterLapse.JobKind, $"{character}:{booster}")) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(job is null, $"job state {job?.State}: {job?.LastError}");
        Assert.Equal(0, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE item_id = {booster}"));
        Assert.Equal(0, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.InventorySchema}.items WHERE id = {booster}"));
        Assert.Contains("OnServerBoostersChanged", Notifications(sink).Select(n => n.Name));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task Cerebral_accelerator_expiry_settles_partial_and_completed_lines_across_both_rates_after_restart_and_retry(long afterExpiryMinutes)
    {
        const long startUnixMs = 1_800_000_000_000;
        const long beforeExpiryMs = 4 * 60_000;
        const int boosterTypeId = 49539;
        await using var rig = await CharacterHostRig.CreateAsync(startUnixMs);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var skills = new PgSkillStore(rig.Source, rig.SkillsSchema);
        var owned = (await skills.ForCharacterAsync(characterId)).Select(skill => skill.TypeId).ToHashSet();
        var type = catalog.Skills
            .Where(skill => skill.Published && skill.Rank == 1 && skill.Needs.Count == 0 && !owned.Contains(skill.TypeId))
            .OrderBy(skill => skill.TypeId)
            .First();
        await rig.SetSkillLevelAsync(characterId, type.TypeId, 0);

        var expiryUnixMs = startUnixMs + beforeExpiryMs;
        var expiryFileTime = DateTimeOffset.FromUnixTimeMilliseconds(expiryUnixMs).ToFileTime();
        var nowUnixMs = expiryUnixMs + afterExpiryMinutes * 60_000;
        var nowFileTime = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs).ToFileTime();
        var handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        const int targetLevel = 2;
        var booster = await GiveItemAsync(rig, characterId, boosterTypeId, characterId, 88, singleton: true);
        await InsertBoosterAsync(rig, booster, characterId, boosterTypeId, expiryFileTime);
        var snapshots = new List<DurableTrainingSnapshot>();
        var sink = new RecordingPacketSink(packet =>
        {
            if (NotificationName(packet) != "OnServerBoostersChanged")
                return;
            using var connection = rig.Source.OpenConnection();
            using var query = new NpgsqlCommand(
                $"SELECT COALESCE((SELECT skill_points FROM {rig.SkillsSchema}.character_skills WHERE character_id = $1 AND type_id = $2), 0), " +
                $"(SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE character_id = $1)", connection);
            query.Parameters.Add(new NpgsqlParameter { Value = characterId });
            query.Parameters.Add(new NpgsqlParameter { Value = type.TypeId });
            using var reader = query.ExecuteReader();
            Assert.True(reader.Read());
            snapshots.Add(new DurableTrainingSnapshot(reader.GetInt64(0), reader.GetInt64(1)));
        });
        var session = new ClientSession { CharacterId = characterId, UserId = 1, Sink = sink };
        rig.Sessions.Enter(characterId, session);
        await BoundAsync(handler, session, "CharStartTrainingSkill",
            new PyLong(Eve.Services.Skills.SkillWire.ItemId(characterId, type.TypeId)), new PyInt(1));
        await BoundAsync(handler, session, "SaveNewQueue", new PyList(
        [
            Py.Tuple(new PyInt(type.TypeId), new PyInt(1)),
            Py.Tuple(new PyInt(type.TypeId), new PyInt(2)),
        ]));

        var attributesBeforeExpiry = await BoundAsync(handler, session, "GetAttributes");
        var boostedRate = SkillRate(type, attributesBeforeExpiry);
        var queueBeforeExpiry = Assert.IsType<PyTuple>(await BoundAsync(handler, session, "GetSkillQueueAndFreePoints"));
        var firstLine = Assert.IsType<PyObject>(Assert.IsType<PyList>(queueBeforeExpiry.Items[0]).Items[0]);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs).ToFileTime(), Integer(Field(firstLine, "trainingStartTime")));
        var key = $"{characterId}:{booster}";
        await rig.Scheduler.ScheduleAsync(new Eve.Scheduler.JobSpec(Eve.Services.Skills.SkillBoosterLapse.JobKind, expiryUnixMs)
        {
            Key = key,
            Payload = new Eve.Services.Skills.SkillBoosterLapse.BoosterJob(characterId, booster),
        });

        rig.AdvanceTo(nowUnixMs);
        var trainingStore = new PgTrainingStore(rig.Source, rig.SkillsSchema);
        await RunBoosterJobAsync(rig, key);
        Assert.Equal(1, Notifications(sink).Count(notification => notification.Name == "OnServerBoostersChanged"));
        var durable = Assert.Single(snapshots);
        Assert.Equal(0, durable.ActiveBoosters);

        // V24 refreshes attributes before it redraws the queue after this notification.
        var attributesAfterExpiry = await BoundAsync(handler, session, "GetAttributes");
        var normalRate = SkillRate(type, attributesAfterExpiry);
        Assert.True(boostedRate > normalRate);
        var firstDestination = SkillPoints.ForTraining(type.Rank, 1);
        var destination = SkillPoints.ForTraining(type.Rank, targetLevel);
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs).ToFileTime();
        var boostedPoints = Training.PointsAt(0, firstDestination, startedAt, expiryFileTime, boostedRate);
        Assert.True(boostedPoints < firstDestination);
        var completedAt = expiryFileTime + Training.DurationTicks(firstDestination - boostedPoints, normalRate);
        var completesAcrossExpiry = completedAt <= nowFileTime;
        var targetLevelAfterExpiry = completesAcrossExpiry ? targetLevel : 1;
        var expectedDestination = SkillPoints.ForTraining(type.Rank, targetLevelAfterExpiry);
        var expectedPoints = completesAcrossExpiry
            ? Training.PointsAt(firstDestination, destination, completedAt, nowFileTime, normalRate)
            : Training.PointsAt(boostedPoints, firstDestination, expiryFileTime, nowFileTime, normalRate);
        Assert.True(expectedPoints < expectedDestination);
        Assert.Equal(expectedPoints, durable.SkillPoints);

        var persisted = Assert.Single(await skills.ForCharacterAsync(characterId), skill => skill.TypeId == type.TypeId);
        Assert.Equal(expectedPoints, persisted.SkillPoints);
        var queue = Assert.IsType<PyList>(Assert.IsType<PyTuple>(await BoundAsync(handler, session, "GetSkillQueueAndFreePoints")).Items[0]);
        Assert.Equal(completesAcrossExpiry ? 1 : 2, queue.Items.Count);
        var line = Assert.IsType<PyObject>(queue.Items[0]);
        Assert.Equal(targetLevelAfterExpiry, Integer(Field(line, "trainingToLevel")));
        Assert.Equal(expectedPoints, Integer(Field(line, "trainingStartSP")));
        var expectedQueueEnd = nowFileTime + Training.DurationTicks(expectedDestination - expectedPoints, normalRate);
        Assert.Equal(expectedQueueEnd, Integer(Field(line, "trainingEndTime")));
        var history = await trainingStore.HistoryAsync(characterId, 20);
        var completedEvents = history.Where(entry => entry.EventType == SkillEvents.QueueTrainingCompleted).ToArray();
        if (completesAcrossExpiry)
        {
            var completed = Assert.Single(completedEvents);
            Assert.Equal(completedAt, completed.LogDate);
            Assert.Equal(firstDestination, completed.Points);
            Assert.Equal(1, completed.Level);
        }
        else
        {
            Assert.Empty(completedEvents);
        }
        Assert.DoesNotContain(history, entry => entry.EventType == SkillEvents.TrainingComplete);

        var notificationsAtLapse = Notifications(sink);
        var boosterChangedIndex = notificationsAtLapse.FindIndex(notification => notification.Name == "OnServerBoostersChanged");
        var queueSavedIndex = notificationsAtLapse.FindLastIndex(notification => notification.Name == "OnNewSkillQueueSaved");
        Assert.True(boosterChangedIndex >= 0);
        Assert.True(queueSavedIndex > boosterChangedIndex);
        var savedQueue = Assert.IsType<PyList>(Assert.Single(notificationsAtLapse[queueSavedIndex].Args));
        var savedLine = Assert.IsType<PyObject>(savedQueue.Items[0]);
        Assert.Equal(expectedQueueEnd, Integer(Field(savedLine, "trainingEndTime")));
        Assert.Equal(targetLevelAfterExpiry, Integer(Field(savedLine, "trainingToLevel")));

        await rig.RestartAsync();
        handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        rig.Sessions.Enter(characterId, session);
        Assert.Equal(expectedPoints, Assert.Single(await skills.ForCharacterAsync(characterId), skill => skill.TypeId == type.TypeId).SkillPoints);
        var restartedQueue = Assert.IsType<PyList>(Assert.IsType<PyTuple>(await BoundAsync(handler, session, "GetSkillQueueAndFreePoints")).Items[0]);
        Assert.Equal(completesAcrossExpiry ? 1 : 2, restartedQueue.Items.Count);
        var restartedLine = Assert.IsType<PyObject>(restartedQueue.Items[0]);
        Assert.Equal(expectedQueueEnd, Integer(Field(restartedLine, "trainingEndTime")));
        Assert.Equal(history, await trainingStore.HistoryAsync(characterId, 20));

        var notificationNamesBeforeRetry = Notifications(sink).Select(notification => notification.Name).ToArray();
        await rig.Scheduler.ScheduleAsync(new Eve.Scheduler.JobSpec(Eve.Services.Skills.SkillBoosterLapse.JobKind, expiryUnixMs)
        {
            Key = key,
            Payload = new Eve.Services.Skills.SkillBoosterLapse.BoosterJob(characterId, booster),
        });
        await RunBoosterJobAsync(rig, key);
        Assert.Equal(1, Notifications(sink).Count(notification => notification.Name == "OnServerBoostersChanged"));
        Assert.Equal(notificationNamesBeforeRetry, Notifications(sink).Select(notification => notification.Name));
        Assert.Single(snapshots);
        Assert.Equal(0, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE item_id = {booster}"));
        Assert.Equal(expectedPoints, Assert.Single(await skills.ForCharacterAsync(characterId), skill => skill.TypeId == type.TypeId).SkillPoints);
        Assert.Equal(history, await trainingStore.HistoryAsync(characterId, 20));
    }

    private static async Task RunBoosterJobAsync(CharacterHostRig rig, string key)
    {
        var kind = Eve.Services.Skills.SkillBoosterLapse.JobKind;
        await rig.Scheduler.RunNowAsync(kind, key);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Eve.Scheduler.JobInfo? job;
        while ((job = await rig.Scheduler.FindAsync(kind, key)) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Null(job);
    }

    private sealed record DurableTrainingSnapshot(long SkillPoints, long ActiveBoosters);

    private static long Integer(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an integer, got {value.GetType().Name}"),
    };

    private static double SkillRate(SkillType skill, PyValue attributes) =>
        Attribute(attributes, skill.PrimaryAttribute) + Attribute(attributes, skill.SecondaryAttribute) / 2d;

    private static double Attribute(PyValue values, int attributeId)
    {
        var value = Assert.IsType<PyDict>(values).Entries.Single(entry => Integer(entry.Key) == attributeId).Value;
        return value switch
        {
            PyReal number => number.Value,
            PyInt number => number.Value,
            PyLong number => number.Value,
            _ => throw new Xunit.Sdk.XunitException($"Expected an attribute number, got {value.GetType().Name}"),
        };
    }

    private static string? NotificationName(PyValue packet)
    {
        if (packet is not PyObject { Args: PyTuple state } || state.Items.Count < 3 || state.Items[0] is not PyInt { Value: MachoMessage.Notification })
            return null;
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(state.Items[2]).Args);
        return Assert.IsType<PyStr>(address.Items[1]).Value;
    }

    [PgFact]
    public async Task An_expert_system_that_lapses_while_the_pilot_is_offline_leaves_the_persistent_notice_once()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        const int freeAirExplorer = 63826;
        var expiredMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {rig.SkillsSchema}.character_expert_systems(character_id, type_id, installed_at_ms, expires_at_ms, source_item_id, grant_reason, updated_at_ms) " +
            "VALUES ($1, $2, $3, $4, 1, 'item', $3)", connection))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = character });
            insert.Parameters.Add(new NpgsqlParameter { Value = freeAirExplorer });
            insert.Parameters.Add(new NpgsqlParameter { Value = expiredMs - 3_600_000 });
            insert.Parameters.Add(new NpgsqlParameter { Value = expiredMs });
            await insert.ExecuteNonQueryAsync();
        }

        // no session: the pilot is offline when the expert system runs out; the periodic job is made due now
        await ScalarAsync(rig, $"UPDATE {rig.SchedulerSchema}.jobs SET run_at_ms = 0 WHERE kind = 'skills.expert-systems.expire'");
        await rig.Scheduler.RunNowAsync("skills.expert-systems.expire", "expire");
        Assert.Equal(0, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.character_expert_systems WHERE character_id = {character}"));
        var notices = await NoticesAsync(rig, character);
        var notice = Assert.Single(notices, record => record["typeID"]!.GetValue<int>() == 253);
        Assert.Equal(4, notice["groupID"]!.GetValue<int>());
        Assert.Equal(freeAirExplorer, notice["data"]!["typeID"]!.GetValue<int>());

        // the job runs again: nothing lapses twice, so no second notice
        await ScalarAsync(rig, $"UPDATE {rig.SchedulerSchema}.jobs SET run_at_ms = 0 WHERE kind = 'skills.expert-systems.expire'");
        await rig.Scheduler.RunNowAsync("skills.expert-systems.expire", "expire");
        Assert.Single(await NoticesAsync(rig, character), record => record["typeID"]!.GetValue<int>() == 253);
    }

    private static async Task InsertBoosterAsync(CharacterHostRig rig, long itemId, long characterId, int typeId, long expiryFileTime)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {rig.SkillsSchema}.active_skill_boosters(item_id, character_id, booster_type_id, expiry_filetime, side_effect_ids) " +
            "VALUES ($1, $2, $3, $4, '{}')", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = itemId });
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = typeId });
        command.Parameters.Add(new NpgsqlParameter { Value = expiryFileTime });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(CharacterHostRig rig, string sql)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task WaitForPgSleepAsync(CharacterHostRig rig, string queryFragment)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid <> pg_backend_pid() AND datname = current_database() AND wait_event = 'PgSleep' AND query LIKE '%' || $1 || '%')",
                connection);
            command.Parameters.Add(new NpgsqlParameter { Value = queryFragment });
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"PostgreSQL did not enter pg_sleep for {queryFragment}.");
    }

    private static async Task WaitForSkillOperationWaitAsync(CharacterHostRig rig, long characterId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (" +
                "SELECT 1 FROM pg_stat_activity a WHERE a.pid <> pg_backend_pid() AND a.datname = current_database() AND a.wait_event_type = 'Lock' AND (" +
                "a.query LIKE '%' || $2 || '.items%' OR EXISTS (" +
                "SELECT 1 FROM pg_locks l WHERE l.pid = a.pid AND l.locktype = 'advisory' AND NOT l.granted " +
                "AND l.classid = (((hashtextextended('skills:' || $1::text, 0) >> 32) & 4294967295)::oid) " +
                "AND l.objid = ((hashtextextended('skills:' || $1::text, 0) & 4294967295)::oid) AND l.objsubid = 1)))",
                connection);
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
            command.Parameters.Add(new NpgsqlParameter { Value = rig.InventorySchema });
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("The extraction RPC did not reach the contested character or extractor lock.");
    }

    /// <summary>The persistent notices of a character's box in the game store (the notifications table, as the mailbox writes it).</summary>
    private static async Task<JsonObject[]> NoticesAsync(CharacterHostRig rig, long characterId)
    {
        var store = new PersistenceStore(rig.Source, rig.GameStoreSchema);
        var recipientKey = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rowKey = RowCodec.Explode("notifications", new JsonObject
        {
            ["boxes"] = new JsonObject { [recipientKey] = new JsonObject() },
        }).Keys.Single(key => key != "boxes");
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var json = await store.ReadRowAsync(connection, transaction, "notifications", rowKey);
        await transaction.CommitAsync();
        var box = json is null ? null : JsonNode.Parse(json) as JsonObject;
        return box?["byID"] is JsonObject records ? records.Select(pair => (JsonObject)pair.Value!).ToArray() : [];
    }

    private static async Task<long> GiveItemAsync(CharacterHostRig rig, long characterId, int typeId, long locationId, int flag, bool singleton)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await new PgItemStore(rig.Source, rig.InventorySchema).CreateAsync(connection, transaction,
            new NewItem(typeId, characterId, locationId, flag, 1, Singleton: singleton));
        await transaction.CommitAsync();
        return id;
    }

    private static async Task<long> PutItemAsync(CharacterHostRig rig, long ownerId, int typeId, long locationId, int flag, long quantity, bool singleton)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await new PgItemStore(rig.Source, rig.InventorySchema).CreateAsync(connection, transaction,
            new NewItem(typeId, ownerId, locationId, flag, quantity, Singleton: singleton));
        await transaction.CommitAsync();
        return id;
    }

    private static long InventoryId(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an inventory ID, got {value.GetType().Name}"),
    };

    private static async Task SetShipAsync(CharacterHostRig rig, long characterId, long shipId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Characters.SetShipAsync(connection, transaction, characterId, shipId);
        await transaction.CommitAsync();
    }

    private static async Task FundAsync(CharacterHostRig rig, long characterId, decimal amount)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var wallet = new WalletTransactions(rig.Source, rig.WalletSchema);
        await wallet.EnsureAsync(connection, transaction, characterId);
        await wallet.AdjustAsync(connection, transaction, characterId, amount, 0, 10, "test funds", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), $"test-funds:{Guid.NewGuid():N}");
        await transaction.CommitAsync();
    }

    private static async Task<List<int>> BodyImplantTypesAsync(CharacterHostRig rig, long characterId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT type_id FROM {rig.InventorySchema}.items WHERE owner_id = $1 AND location_id = $1 AND flag = 89 ORDER BY id", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await using var reader = await command.ExecuteReaderAsync();
        var types = new List<int>();
        while (await reader.ReadAsync())
            types.Add(reader.GetInt32(0));
        return types;
    }

    [PgFact]
    public async Task Player_corporation_biomass_without_an_office_destroys_assets_transfers_isk_and_notifies_members_once()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var recipient = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        await rig.AddCorporationMemberAsync(recipient);
        await rig.FundAsync(character, 98_765.43m);
        var assets = new PgItemStore(rig.Source, rig.InventorySchema);
        long shipId;
        long cargoId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            shipId = await assets.CreateAsync(connection, transaction,
                new NewItem(TypeId: 587, OwnerId: character, LocationId: rig.Station, Flag: 4));
            cargoId = await assets.CreateAsync(connection, transaction,
                new NewItem(TypeId: 34, OwnerId: character, LocationId: shipId, Flag: 4, Quantity: 12));
            await transaction.CommitAsync();
        }

        var sink = new RecordingPacketSink();
        rig.Sessions.Enter(recipient, new ClientSession { CharacterId = recipient, Sink = sink });
        await DeleteCharacterNowAsync(rig, character);

        Assert.Null(await assets.GetAsync(shipId));
        Assert.Null(await assets.GetAsync(cargoId));
        Assert.Equal(0m, await BalanceAsync(rig, character));
        Assert.Equal(98_765.43m, await BalanceAsync(rig, CharacterHostRigCorporation));
        Assert.Equal(1L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE owner_id = {CharacterHostRigCorporation} AND counterparty_id = {character} AND ref_type_id = 9 AND amount = 98765.43"));
        Assert.Equal(1L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE owner_id = {character} AND counterparty_id = {CharacterHostRigCorporation} AND ref_type_id = 9 AND amount = -98765.43"));

        var (message, notification) = await WaitForBiomassMailAsync(rig, character, recipient);
        Assert.Equal(CharacterHostRigCorporation.ToString(), message["senderID"]?.ToString());
        Assert.Contains(recipient.ToString(), message["toCharacterIDs"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(CharacterHostRigCorporation.ToString(), message["toCorpOrAllianceID"]?.ToString());
        var mailboxes = Assert.IsType<JsonObject>((await ReadGameStoreTableAsync(rig, "mail"))["mailboxes"]);
        var recipientMailbox = Assert.IsType<JsonObject>(mailboxes[recipient.ToString()]);
        var statuses = Assert.IsType<JsonObject>(recipientMailbox["statuses"]);
        var messageStatus = Assert.IsType<JsonObject>(statuses[message["messageID"]!.ToString()]);
        Assert.Equal("5", messageStatus["labelMask"]?.ToString());
        Assert.Equal("1004", notification["typeID"]?.ToString());
        Assert.Equal(CharacterHostRigCorporation.ToString(), notification["senderID"]?.ToString());
        var notices = Notifications(sink);
        var noticeDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!notices.Any(row => row.Name == "OnNotificationReceived") && DateTime.UtcNow < noticeDeadline)
        {
            await Task.Delay(10);
            notices = Notifications(sink);
        }
        var mailNotice = Assert.Single(notices, row => row.Name == "OnNotificationReceived");
        Assert.Equal(new PyInt(1004), mailNotice.Args[1]);
        var noticeData = mailNotice.Args[4];
        Assert.False(string.IsNullOrWhiteSpace(Eve.Macho.JsSemantics.StringValue(Field(noticeData, "senderName"))));
        var noticeMessage = Field(noticeData, "msg");
        Assert.Equal(long.Parse(message["messageID"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture),
            Integer(Field(noticeMessage, "messageID")));
        Assert.Contains(new PyLong(CharacterHostRigCorporation), Assert.IsType<PyList>(Field(noticeMessage, "toCorpOrAllianceID")).Items);

        // A host restart and a second DeleteCharacter call reuse the original corporation, wallet receipt, mail, and notification.
        await rig.RestartAsync();
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        await CallAsync(service, "DeleteCharacter", new ClientSession { CharacterId = character, UserId = 1 }, new PyLong(character));
        Assert.Equal(CharacterHostRigCorporation, (await rig.Characters.GetAsync(character))!.DeletionCorporationId);
        Assert.Equal(2L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE ref_type_id = 9 AND " +
            $"((owner_id = {CharacterHostRigCorporation} AND counterparty_id = {character} AND amount = 98765.43) OR " +
            $"(owner_id = {character} AND counterparty_id = {CharacterHostRigCorporation} AND amount = -98765.43))"));
        await WaitForBiomassMailAsync(rig, character, recipient);
        var persistedMessages = (await ReadGameStoreTableAsync(rig, "mail"))["messages"] as JsonObject;
        Assert.Single(persistedMessages!.Select(pair => pair.Value).OfType<JsonObject>(), row =>
            string.Equals(row["biomassOperationKey"]?.ToString(), $"character-biomass:{character}", StringComparison.Ordinal));
        var persistedNotifications = ((await ReadGameStoreTableAsync(rig, "notifications"))["boxes"] as JsonObject)?[recipient.ToString()] as JsonObject;
        Assert.Single(Assert.IsType<JsonObject>(persistedNotifications?["byID"]).Select(pair => pair.Value).OfType<JsonObject>(), row =>
            string.Equals(row["idempotencyKey"]?.ToString(), $"character-biomass:{character}:recipient:{recipient}", StringComparison.Ordinal));
    }

    [PgFact]
    public async Task Npc_corporation_biomass_destroys_isk_and_assets_without_a_wallet_journal()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var npcCorporationId = await rig.FirstNpcCorporationIdAsync();
        await rig.Characters.SetCorporationAsync(character, npcCorporationId);
        await rig.FundAsync(character, 45_678.90m);
        var itemId = await GiveItemAsync(rig, character, 587, rig.Station, 4, singleton: true);

        await DeleteCharacterNowAsync(rig, character);

        Assert.Null(await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(itemId));
        Assert.Equal(0m, await BalanceAsync(rig, character));
        Assert.Equal(0L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE owner_id = {character}"));
        Assert.Equal(npcCorporationId, (await rig.Characters.GetAsync(character))!.DeletionCorporationId);
    }

    [PgFact]
    public async Task Corporation_ceos_still_cannot_prepare_character_deletion()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var member = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        await rig.AddCorporationMemberAsync(member);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var makeCeo = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.corporations SET ceo_id = $1 WHERE id = {CharacterHostRigCorporation}", connection))
        {
            makeCeo.Parameters.Add(new NpgsqlParameter { Value = character });
            Assert.Equal(1, await makeCeo.ExecuteNonQueryAsync());
        }

        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service,
            "PrepareCharacterForDelete", new ClientSession { CharacterId = character, UserId = 1 }, new PyLong(character)));
        Assert.Equal("You must resign as CEO or close the corporation before deleting this character.", InfoText(error));
        Assert.Null((await rig.Characters.GetAsync(character))!.DeletePrepareAt);
    }

    [PgFact]
    public async Task Character_deletion_queues_waits_cancels_and_deletes_and_the_deleted_character_is_refused()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        foreach (var method in new[] { "PrepareCharacterForDelete", "DeleteCharacter", "CancelCharacterDeletePrepare" })
        {
            Assert.True(service.HasMethod(method), method);
        }

        // delete before the queue: refused with the reference's text
        var notQueued = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service, "DeleteCharacter", session, new PyLong(character)));
        Assert.Equal("That character is not in the biomass queue.", InfoText(notQueued));

        // prepare: the answer is the FILETIME the deletion becomes possible, in the future; a second prepare returns the same time
        var readyAt = Assert.IsType<PyLong>(await CallAsync(service, "PrepareCharacterForDelete", session, new PyLong(character))).Value;
        Assert.True(readyAt > DateTime.UtcNow.ToFileTimeUtc());
        Assert.Equal(new PyLong(readyAt), await CallAsync(service, "PrepareCharacterForDelete", session, new PyLong(character)));
        var queued = Assert.IsType<Character>(await rig.Characters.GetAsync(character));
        Assert.True(queued.IsQueuedForDeletion);

        // a delete before the waiting time is refused
        var early = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service, "DeleteCharacter", session, new PyLong(character)));
        Assert.Equal("That character is still waiting for the biomass timer to expire.", InfoText(early));

        // a restart keeps the queue
        await rig.RestartAsync();
        var restarted = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        Assert.True((await rig.Characters.GetAsync(character))!.IsQueuedForDeletion);

        // cancel clears the queue; a delete after that is again refused as not queued
        Assert.IsType<PyNone>(await CallAsync(restarted, "CancelCharacterDeletePrepare", session, new PyLong(character)));
        Assert.False((await rig.Characters.GetAsync(character))!.IsQueuedForDeletion);
        await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(restarted, "DeleteCharacter", session, new PyLong(character)));

        // the character holds a ship with cargo in the station and a skill: the deletion moves the ship tree to the corporation's
        // deliveries (the corporation is not an NPC corporation here) and clears the skill
        long shipId;
        long cargoId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var itemStore = new PgItemStore(rig.Source, rig.InventorySchema);
            shipId = await itemStore.CreateAsync(connection, transaction, new NewItem(TypeId: 587, OwnerId: character, LocationId: rig.Station, Flag: 4));
            cargoId = await itemStore.CreateAsync(connection, transaction, new NewItem(TypeId: 34, OwnerId: character, LocationId: shipId, Flag: 4, Quantity: 500));
            await transaction.CommitAsync();
        }
        await rig.SetSkillLevelAsync(character, 3300, 3);
        var skillPlans = new Eve.Skills.PgSkillPlanStore(rig.Source, rig.SkillsSchema);
        var skillPlanId = await skillPlans.CreateAsync(character, "Private plan", "", []);
        var otherCharacter = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        await rig.AddCorporationMemberAsync(otherCharacter);
        var officeFolderId = await rig.AddCorporationOfficeAsync(CharacterHostRigCorporation);
        await rig.FundAsync(character, 12_345.67m);
        const int expertSystemTypeId = 63826;
        var expertSystemInstalledAtMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        var expertSystemExpiresAtMs = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        await using (var expertSystemConnection = await rig.Source.OpenConnectionAsync())
        await using (var expertSystemInsert = new NpgsqlCommand(
            $"INSERT INTO {rig.SkillsSchema}.character_expert_systems(character_id, type_id, installed_at_ms, expires_at_ms, source_item_id, grant_reason, updated_at_ms) " +
            "VALUES ($1, $3, $4, $5, NULL, 'item', $4), ($2, $3, $4, $5, NULL, 'item', $4)", expertSystemConnection))
        {
            expertSystemInsert.Parameters.Add(new NpgsqlParameter { Value = character });
            expertSystemInsert.Parameters.Add(new NpgsqlParameter { Value = otherCharacter });
            expertSystemInsert.Parameters.Add(new NpgsqlParameter { Value = expertSystemTypeId });
            expertSystemInsert.Parameters.Add(new NpgsqlParameter { Value = expertSystemInstalledAtMs });
            expertSystemInsert.Parameters.Add(new NpgsqlParameter { Value = expertSystemExpiresAtMs });
            await expertSystemInsert.ExecuteNonQueryAsync();
        }
        var titleId = Guid.NewGuid();
        foreach (var ownerId in new[] { character, otherCharacter })
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var statement in new[]
            {
                $"INSERT INTO {rig.CharactersSchema}.character_titles(character_id, title_id, granted_ms) VALUES ($1, $2, 1)",
                $"INSERT INTO {rig.CharactersSchema}.character_equipped_titles(character_id, title_id) VALUES ($1, $2)",
                $"INSERT INTO {rig.CharactersSchema}.achievement_trackers(character_id, updated_ms) VALUES ($1, 1)",
                $"INSERT INTO {rig.CharactersSchema}.achievement_completions(character_id, achievement_id, completed_filetime) VALUES ($1, 1, 1)",
                $"INSERT INTO {rig.CharactersSchema}.achievement_event_counts(character_id, event_name, event_count) VALUES ($1, 'login', 1)",
                $"UPDATE {rig.CharactersSchema}.characters SET time_last_clone_jump_filetime = 123 WHERE id = $1",
            })
            {
                await using var command = new NpgsqlCommand(statement, connection, transaction);
                command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
                if (statement.Contains("title_id", StringComparison.Ordinal))
                    command.Parameters.Add(new NpgsqlParameter { Value = titleId });
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            await rig.AddCloneAsync(ownerId, rig.Station, "station", "Private clone", [10]);
            await using var characterStateConnection = await rig.Source.OpenConnectionAsync();
            foreach (var statement in new[]
            {
                $"INSERT INTO {rig.CharactersSchema}.nes_intro_states(character_id, state, updated_ms) VALUES ($1, 2, 1)",
                $"INSERT INTO {rig.CharactersSchema}.project_discovery_tutorials(character_id, finished, tutorial_level, updated_ms) VALUES ($1, true, 1, 1)",
            })
            {
                await using var state = new NpgsqlCommand(statement, characterStateConnection);
                state.Parameters.Add(new NpgsqlParameter { Value = ownerId });
                await state.ExecuteNonQueryAsync();
            }
            await using (var settlement = new NpgsqlCommand(
                $"INSERT INTO {rig.CharactersSchema}.character_jump_clone_settlements(operation_id, character_id, jump_clone_id, location_id, location_kind, amount, offering_character_id) VALUES ($1, $2, $2, $3, 'station', 1, $4)", characterStateConnection))
            {
                settlement.Parameters.Add(new NpgsqlParameter { Value = $"delete-{ownerId}" });
                settlement.Parameters.Add(new NpgsqlParameter { Value = ownerId });
                settlement.Parameters.Add(new NpgsqlParameter { Value = rig.Station });
                settlement.Parameters.Add(new NpgsqlParameter { Value = ownerId });
                await settlement.ExecuteNonQueryAsync();
            }
        }
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var settlement = new NpgsqlCommand(
            $"INSERT INTO {rig.CharactersSchema}.character_jump_clone_settlements(operation_id, character_id, jump_clone_id, location_id, location_kind, amount, offering_character_id) VALUES ('offered-by-deleted', $1, $1, $2, 'station', 1, $3)", connection))
        {
            settlement.Parameters.Add(new NpgsqlParameter { Value = otherCharacter });
            settlement.Parameters.Add(new NpgsqlParameter { Value = rig.Station });
            settlement.Parameters.Add(new NpgsqlParameter { Value = character });
            await settlement.ExecuteNonQueryAsync();
        }
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var queue = new NpgsqlCommand(
            $"INSERT INTO {rig.SkillsSchema}.skill_queue (character_id, position, type_id, to_level) VALUES ($1, 0, 3300, 4)", connection))
        {
            queue.Parameters.Add(new NpgsqlParameter { Value = character });
            await queue.ExecuteNonQueryAsync();
        }

        await rig.Characters.SetAppearanceAsync(character, [1, 2], true, [3, 4], true);

        // prepare again, move the waiting time into the past (the test's clock), then delete
        await CallAsync(restarted, "PrepareCharacterForDelete", session, new PyLong(character));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE {rig.CharactersSchema}.characters SET delete_prepare_at = now() - interval '1 minute' WHERE id = $1", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = character });
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(2L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.SkillsSchema}.character_expert_systems WHERE expires_at_ms > {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
        Assert.IsType<PyNone>(await CallAsync(restarted, "DeleteCharacter", session, new PyLong(character)));
        Assert.IsType<PyNone>(await CallAsync(restarted, "DeleteCharacter", session, new PyLong(character)));

        // the assets are in the first office's first hangar division, cargo remains in the ship, and skills are gone
        var corporationItems = await new PgItemStore(rig.Source, rig.InventorySchema).OwnedByAsync(900_001_001);
        var movedShip = Assert.Single(corporationItems, item => item.Id == shipId);
        Assert.Equal((115, officeFolderId), (movedShip.Flag, movedShip.LocationId));
        var movedCargo = Assert.Single(corporationItems, item => item.Id == cargoId);
        Assert.Equal(shipId, movedCargo.LocationId);
        Assert.Empty(await new PgItemStore(rig.Source, rig.InventorySchema).OwnedByAsync(character));
        Assert.Equal(0m, await BalanceAsync(rig, character));
        Assert.Equal(12_345.67m, await BalanceAsync(rig, CharacterHostRigCorporation));
        Assert.Equal(1L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE owner_id = {CharacterHostRigCorporation} AND counterparty_id = {character} AND ref_type_id = 9 AND amount = 12345.67"));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {rig.SkillsSchema}.character_skills WHERE character_id = $1", connection))
        {
            count.Parameters.Add(new NpgsqlParameter { Value = character });
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
        Assert.Empty(await skillPlans.ListAsync(character));
        Assert.Null(await skillPlans.GetAsync(character, skillPlanId));
        await using (var purgeConnection = await rig.Source.OpenConnectionAsync())
        {
            foreach (var table in new[]
            {
                "character_jump_clones", "character_jump_clone_settlements", "character_titles", "character_equipped_titles",
                "achievement_trackers", "achievement_completions", "achievement_event_counts", "nes_intro_states",
                "project_discovery_tutorials",
            })
            {
                await using var deletedCount = new NpgsqlCommand(
                    $"SELECT count(*) FROM {rig.CharactersSchema}.{table} WHERE character_id = $1", purgeConnection);
                deletedCount.Parameters.Add(new NpgsqlParameter { Value = character });
                Assert.Equal(0L, (long)(await deletedCount.ExecuteScalarAsync())!);
                await using var retainedCount = new NpgsqlCommand(
                    $"SELECT count(*) FROM {rig.CharactersSchema}.{table} WHERE character_id = $1", purgeConnection);
                retainedCount.Parameters.Add(new NpgsqlParameter { Value = otherCharacter });
                Assert.Equal(1L, (long)(await retainedCount.ExecuteScalarAsync())!);
            }
            await using var lastJump = new NpgsqlCommand(
                $"SELECT time_last_clone_jump_filetime FROM {rig.CharactersSchema}.characters WHERE id = $1", purgeConnection);
            lastJump.Parameters.Add(new NpgsqlParameter { Value = character });
            Assert.Equal(0L, (long)(await lastJump.ExecuteScalarAsync())!);
        }
        await using (var queueConnection = await rig.Source.OpenConnectionAsync())
        await using (var queueCount = new NpgsqlCommand(
            $"SELECT count(*) FROM {rig.SkillsSchema}.skill_queue WHERE character_id = $1", queueConnection))
        {
            queueCount.Parameters.Add(new NpgsqlParameter { Value = character });
            Assert.Equal(0L, (long)(await queueCount.ExecuteScalarAsync())!);
        }
        Assert.Equal(0L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.SkillsSchema}.character_expert_systems WHERE character_id = {character}"));
        Assert.Equal(1L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.SkillsSchema}.character_expert_systems WHERE character_id = {otherCharacter}"));
        // the graveyard corporation (EveJS DOOMHEIM_CORPORATION_ID)
        Assert.Equal(1000001L, (await rig.Characters.GetAsync(character))!.CorporationId);

        // the deleted character is hidden from its account and refused at selection
        var deleted = Assert.IsType<Character>(await rig.Characters.GetAsync(character));
        Assert.True(deleted.IsDeleted);
        Assert.Equal((null, null), await rig.Characters.AppearanceAsync(character));
        Assert.DoesNotContain(await rig.Characters.ForAccountAsync(1), c => c.Id == character);
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            CallAsync(restarted, "SelectCharacterID", session, new PyLong(character), PyNone.Instance, PyBool.False));
        Assert.Equal("That character is not available on this account.", InfoText(refused));
    }

    [PgFact]
    public async Task Character_deletion_rolls_back_expert_system_purge_when_a_later_cleanup_step_fails()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var recipient = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        await rig.AddCorporationMemberAsync(recipient);
        await rig.FundAsync(character, 12_345.67m);
        var assetId = await GiveItemAsync(rig, character, 587, rig.Station, 4, singleton: true);
        var assets = new PgItemStore(rig.Source, rig.InventorySchema);
        var session = new ClientSession { CharacterId = character, UserId = 1 };
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        const int expertSystemTypeId = 63826;
        var installedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAtMs = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        Assert.True(expiresAtMs > installedAtMs);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using (var insertExpertSystem = new NpgsqlCommand(
                $"INSERT INTO {rig.SkillsSchema}.character_expert_systems(character_id, type_id, installed_at_ms, expires_at_ms, source_item_id, grant_reason, updated_at_ms) " +
                "VALUES ($1, $2, $3, $4, NULL, 'item', $3)", connection))
            {
                insertExpertSystem.Parameters.Add(new NpgsqlParameter { Value = character });
                insertExpertSystem.Parameters.Add(new NpgsqlParameter { Value = expertSystemTypeId });
                insertExpertSystem.Parameters.Add(new NpgsqlParameter { Value = installedAtMs });
                insertExpertSystem.Parameters.Add(new NpgsqlParameter { Value = expiresAtMs });
                await insertExpertSystem.ExecuteNonQueryAsync();
            }

            await using (var insertRemap = new NpgsqlCommand(
                $"INSERT INTO {rig.SkillsSchema}.character_neural_remaps(character_id, charisma, intelligence, memory, perception, willpower) " +
                "VALUES ($1, 20, 20, 20, 20, 20)", connection))
            {
                insertRemap.Parameters.Add(new NpgsqlParameter { Value = character });
                await insertRemap.ExecuteNonQueryAsync();
            }

            // Neural remaps are purged after expert systems, so this failure must roll back the earlier deletion.
            await using (var function = new NpgsqlCommand(
                $"CREATE FUNCTION {rig.SkillsSchema}.fail_character_neural_remap_delete() RETURNS trigger " +
                "LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'forced later purge failure'; END; $$", connection))
                await function.ExecuteNonQueryAsync();
            await using (var trigger = new NpgsqlCommand(
                $"CREATE TRIGGER fail_character_neural_remap_delete BEFORE DELETE ON {rig.SkillsSchema}.character_neural_remaps " +
                $"FOR EACH ROW EXECUTE FUNCTION {rig.SkillsSchema}.fail_character_neural_remap_delete()", connection))
                await trigger.ExecuteNonQueryAsync();
        }

        await CallAsync(service, "PrepareCharacterForDelete", session, new PyLong(character));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var makeDeletionDue = new NpgsqlCommand(
            $"UPDATE {rig.CharactersSchema}.characters SET delete_prepare_at = now() - interval '1 minute' WHERE id = $1", connection))
        {
            makeDeletionDue.Parameters.Add(new NpgsqlParameter { Value = character });
            await makeDeletionDue.ExecuteNonQueryAsync();
        }

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            CallAsync(service, "DeleteCharacter", session, new PyLong(character)));
        Assert.Equal("forced later purge failure", failure.MessageText);
        Assert.Equal(1L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.SkillsSchema}.character_expert_systems WHERE character_id = {character}"));
        Assert.NotNull(await assets.GetAsync(assetId));
        Assert.Equal(12_345.67m, await BalanceAsync(rig, character));
        Assert.Equal(0L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE ref_type_id = 9 AND " +
            $"((owner_id = {CharacterHostRigCorporation} AND counterparty_id = {character} AND amount = 12345.67) OR " +
            $"(owner_id = {character} AND counterparty_id = {CharacterHostRigCorporation} AND amount = -12345.67))"));
        Assert.Equal(CharacterHostRigCorporation, (await rig.Characters.GetAsync(character))!.DeletionCorporationId);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var dropTrigger = new NpgsqlCommand(
                $"DROP TRIGGER fail_character_neural_remap_delete ON {rig.SkillsSchema}.character_neural_remaps", connection);
            await dropTrigger.ExecuteNonQueryAsync();
            await using var dropFunction = new NpgsqlCommand(
                $"DROP FUNCTION {rig.SkillsSchema}.fail_character_neural_remap_delete()", connection);
            await dropFunction.ExecuteNonQueryAsync();
        }

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        Assert.IsType<PyNone>(await CallAsync(service, "DeleteCharacter", session, new PyLong(character)));
        Assert.Null(await assets.GetAsync(assetId));
        Assert.Equal(0m, await BalanceAsync(rig, character));
        Assert.Equal(12_345.67m, await BalanceAsync(rig, CharacterHostRigCorporation));
        Assert.Equal(2L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE ref_type_id = 9 AND " +
            $"((owner_id = {CharacterHostRigCorporation} AND counterparty_id = {character} AND amount = 12345.67) OR " +
            $"(owner_id = {character} AND counterparty_id = {CharacterHostRigCorporation} AND amount = -12345.67))"));
        Assert.Equal(CharacterHostRigCorporation, (await rig.Characters.GetAsync(character))!.DeletionCorporationId);
        await WaitForBiomassMailAsync(rig, character, recipient);
    }

    [PgFact]
    public async Task Character_deletion_retry_after_a_postcommit_wallet_notice_failure_keeps_each_effect_once()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var recipient = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        await rig.AddCorporationMemberAsync(recipient);
        await rig.FundAsync(character, 54_321.09m);
        var assetId = await GiveItemAsync(rig, character, 587, rig.Station, 4, singleton: true);
        var assets = new PgItemStore(rig.Source, rig.InventorySchema);
        var failingSink = new RecordingPacketSink(() => throw new InvalidOperationException("forced post-commit wallet notice failure"));
        var memberSession = new ClientSession { CharacterId = recipient, UserId = 1, Sink = failingSink };
        memberSession.Set("corpid", new PyInt(CharacterHostRigCorporation));
        memberSession.Set("rolesAtAll", new PyLong(long.MaxValue));
        rig.Sessions.Enter(recipient, memberSession);

        var session = new ClientSession { CharacterId = character, UserId = 1 };
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        await CallAsync(service, "PrepareCharacterForDelete", session, new PyLong(character));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var makeDeletionDue = new NpgsqlCommand(
            $"UPDATE {rig.CharactersSchema}.characters SET delete_prepare_at = now() - interval '1 minute' WHERE id = $1", connection))
        {
            makeDeletionDue.Parameters.Add(new NpgsqlParameter { Value = character });
            await makeDeletionDue.ExecuteNonQueryAsync();
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CallAsync(service, "DeleteCharacter", session, new PyLong(character)));
        Assert.Equal("forced post-commit wallet notice failure", failure.Message);
        rig.Sessions.Leave(recipient, memberSession);
        Assert.Null(await assets.GetAsync(assetId));
        Assert.Equal(0m, await BalanceAsync(rig, character));
        Assert.Equal(54_321.09m, await BalanceAsync(rig, CharacterHostRigCorporation));
        Assert.Equal(2L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE ref_type_id = 9 AND " +
            $"((owner_id = {CharacterHostRigCorporation} AND counterparty_id = {character} AND amount = 54321.09) OR " +
            $"(owner_id = {character} AND counterparty_id = {CharacterHostRigCorporation} AND amount = -54321.09))"));
        Assert.Equal(CharacterHostRigCorporation, (await rig.Characters.GetAsync(character))!.DeletionCorporationId);

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        Assert.IsType<PyNone>(await CallAsync(service, "DeleteCharacter", session, new PyLong(character)));
        Assert.Equal(1000001L, (await rig.Characters.GetAsync(character))!.CorporationId);
        Assert.Equal(CharacterHostRigCorporation, (await rig.Characters.GetAsync(character))!.DeletionCorporationId);
        Assert.Equal(2L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.WalletSchema}.journal WHERE ref_type_id = 9 AND " +
            $"((owner_id = {CharacterHostRigCorporation} AND counterparty_id = {character} AND amount = 54321.09) OR " +
            $"(owner_id = {character} AND counterparty_id = {CharacterHostRigCorporation} AND amount = -54321.09))"));
        var (message, notification) = await WaitForBiomassMailAsync(rig, character, recipient);
        Assert.Equal("1004", notification["typeID"]?.ToString());
        Assert.Single((await ReadGameStoreTableAsync(rig, "mail"))["messages"]!.AsObject().Select(pair => pair.Value).OfType<JsonObject>(),
            row => string.Equals(row["biomassOperationKey"]?.ToString(), $"character-biomass:{character}", StringComparison.Ordinal));
        Assert.Single(((await ReadGameStoreTableAsync(rig, "notifications"))["boxes"]!.AsObject()[recipient.ToString()]!["byID"]!.AsObject())
            .Select(pair => pair.Value).OfType<JsonObject>(),
            row => string.Equals(row["idempotencyKey"]?.ToString(), $"character-biomass:{character}:recipient:{recipient}", StringComparison.Ordinal));
        Assert.Equal(message["messageID"]?.ToString(), notification["data"]?["msg"]?["messageID"]?.ToString());
    }

    /// <summary>The <c>info</c> text of a CustomInfo refusal, as the client's UserError reads it.</summary>
    private static string InfoText(MachoWrappedException error)
    {
        Assert.Equal("CustomInfo", error.Error.UserErrorMessage);
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var args = Assert.IsType<PyDict>(Assert.IsType<PyTuple>(header.Items[1]).Items[1]);
        return Assert.IsType<PyStr>(args.Entries.Single(entry => ((PyStr)entry.Key).Value == "info").Value).Value;
    }

    [PgFact]
    public async Task Skill_check_notifications_answers_an_empty_list_and_sends_nothing_as_the_reference_does()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        var skills = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        Assert.True(skills.HasMethod("CheckAndSendNotifications"));

        var answer = Assert.IsType<PyList>(await CallAsync(skills, "CheckAndSendNotifications", session));

        Assert.Empty(answer.Items);
        Assert.Empty(Notifications(sink));
    }


    [PgFact]
    public async Task Assigning_personal_labels_does_not_create_a_missing_address_book_contact()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, UserId = 1 };
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        const long contactId = 77_001;

        Assert.Equal(new PyInt(1), await CallAsync(charMgr, "CreateLabel", session, new PyStr("Fleet"), new PyInt(7)));
        Assert.IsType<PyNone>(await CallAsync(charMgr, "AssignLabels", session,
            new PyList([new PyLong(contactId)]), new PyLong(1)));

        var expectedEmptyContacts = Py.KeyVal(
            ("addresses", Py.Rowset(["contactID", "inWatchlist", "relationshipID", "labelMask"], [])),
            ("blocked", Py.Rowset(["senderID"], [])));
        Assert.Equal(expectedEmptyContacts, await CallAsync(charMgr, "GetContactList", session));

        Assert.IsType<PyNone>(await CallAsync(charMgr, "AddContact", session,
            new PyLong(contactId), new PyLong(-10), PyBool.False));
        Assert.IsType<PyNone>(await CallAsync(charMgr, "AssignLabels", session,
            new PyList([new PyLong(contactId)]), new PyLong(1)));
        var expectedContact = Py.KeyVal(
            ("addresses", Py.Rowset(["contactID", "inWatchlist", "relationshipID", "labelMask"],
                [new PyList([new PyInt(contactId), new PyInt(0), new PyInt(-10), new PyInt(1)])])),
            ("blocked", Py.Rowset(["senderID"], [])));
        Assert.Equal(expectedContact, await CallAsync(charMgr, "GetContactList", session));

        await rig.RestartAsync();
        charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        Assert.Equal(expectedContact, await CallAsync(charMgr, "GetContactList", session));
    }

    [PgFact]
    public async Task Character_contact_labels_persist_through_the_host_and_a_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));

        Assert.Equal(new PyInt(1), await CallAsync(charMgr, "CreateLabel", session, new PyStr("Fleet"), new PyInt(7)));
        Assert.Equal(new PyInt(2), await CallAsync(charMgr, "CreateLabel", session, new PyStr("Friends"), new PyInt(3)));
        await CallAsync(charMgr, "AssignLabels", session, new PyList([new PyLong(77_001)]), new PyLong(3));
        var noContacts = Py.KeyVal(
            ("addresses", Py.Rowset(["contactID", "inWatchlist", "relationshipID", "labelMask"], [])),
            ("blocked", Py.Rowset(["senderID"], [])));
        Assert.Equal(noContacts, await CallAsync(charMgr, "GetContactList", session));
        await CallAsync(charMgr, "AddContact", session, new PyLong(77_001), new PyInt(0),
            PyBool.False, PyBool.False, new PyStr(""));
        await CallAsync(charMgr, "AssignLabels", session, new PyList([new PyLong(77_001)]), new PyLong(3));
        Assert.IsType<PyNone>(await CallAsync(charMgr, "DeleteLabel", session, new PyLong(2)));

        var expectedLabels = new PyDict([new KeyValuePair<PyValue, PyValue>(
            new PyInt(1), Py.KeyVal(("labelID", new PyInt(1)), ("name", new PyStr("Fleet")), ("color", new PyInt(7))))]);
        var expectedContacts = Py.KeyVal(
            ("addresses", Py.Rowset(["contactID", "inWatchlist", "relationshipID", "labelMask"],
                [new PyList([new PyInt(77_001), new PyInt(0), new PyInt(0), new PyInt(1)])])),
            ("blocked", Py.Rowset(["senderID"], [])));
        Assert.Equal(expectedLabels, await CallAsync(charMgr, "GetLabels", session));
        Assert.Equal(expectedContacts, await CallAsync(charMgr, "GetContactList", session));

        await rig.RestartAsync();
        charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        Assert.Equal(expectedLabels, await CallAsync(charMgr, "GetLabels", session));
        Assert.Equal(expectedContacts, await CallAsync(charMgr, "GetContactList", session));
        Assert.Equal(new PyInt(4), await CallAsync(charMgr, "CreateLabel", session, new PyStr("Next"), new PyInt(5)));
    }

    [PgFact]
    public async Task Online_status_rpc_denies_presence_before_character_selection()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var targetId = await rig.CreateCharacterAsync();
        rig.Sessions.Enter(targetId, new ClientSession { CharacterId = targetId, UserId = 1 });

        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("onlineStatus"));
        var unselected = new ClientSession { UserId = 1 };

        Assert.Equal(PyBool.False, await CallAsync(service, "GetOnlineStatus", unselected, new PyLong(targetId)));
    }

    [PgFact]
    public async Task Online_status_notifications_follow_reciprocal_watchlists_and_session_lifecycle()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var targetId = await rig.CreateCharacterAsync();
        var reciprocalId = await rig.CreateCharacterAsync();
        var oneWayId = await rig.CreateCharacterAsync();
        var unwatchedId = await rig.CreateCharacterAsync();
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        var userSvc = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("userSvc"));

        foreach (var (ownerId, contactId) in new[]
        {
            (reciprocalId, targetId),
            (targetId, reciprocalId),
            (oneWayId, targetId),
        })
        {
            Assert.IsType<PyNone>(await CallAsync(charMgr, "AddContact",
                new ClientSession { CharacterId = ownerId, UserId = 1 },
                new PyLong(contactId), new PyInt(-10), PyBool.True, PyBool.False, new PyStr("")));
        }

        var targetSession = new ClientSession { CharacterId = targetId, UserId = 1 };
        var deliveredPresence = new List<ClientSession?>();
        var reciprocalSink = new RecordingPacketSink(() => deliveredPresence.Add(rig.Sessions.Of(targetId)));
        var oneWaySink = new RecordingPacketSink();
        var unwatchedSink = new RecordingPacketSink();
        rig.Sessions.Enter(reciprocalId, new ClientSession { CharacterId = reciprocalId, UserId = 1, Sink = reciprocalSink });
        rig.Sessions.Enter(oneWayId, new ClientSession { CharacterId = oneWayId, UserId = 1, Sink = oneWaySink });
        rig.Sessions.Enter(unwatchedId, new ClientSession { CharacterId = unwatchedId, UserId = 1, Sink = unwatchedSink });
        await rig.OnlineStatusNotifications.FlushAsync();

        rig.Sessions.Enter(targetId, targetSession);
        var onlineStatus = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("onlineStatus"));
        var reciprocalSession = Assert.IsType<ClientSession>(rig.Sessions.Of(reciprocalId));
        Assert.Equal(PyBool.True, await CallAsync(onlineStatus, "GetOnlineStatus", reciprocalSession, new PyLong(targetId)));
        await rig.OnlineStatusNotifications.FlushAsync();
        Assert.Equal(new[] { "OnContactLoggedOn" }, Notifications(reciprocalSink).Select(notification => notification.Name));
        Assert.Empty(Notifications(oneWaySink));
        Assert.Empty(Notifications(unwatchedSink));
        Assert.Same(targetSession, Assert.Single(deliveredPresence));

        var replacement = new ClientSession { CharacterId = targetId, UserId = 1 };
        rig.Sessions.Enter(targetId, replacement);
        rig.Sessions.Enter(targetId, replacement);
        targetSession.Close();
        await rig.OnlineStatusNotifications.FlushAsync();
        Assert.Same(replacement, rig.Sessions.Of(targetId));
        Assert.Single(Notifications(reciprocalSink));

        Assert.Equal(PyBool.True, await CallAsync(userSvc, "UserLogOffCharacter", replacement));
        replacement.Close();
        replacement.Close();
        await rig.OnlineStatusNotifications.FlushAsync();

        Assert.Null(replacement.CharacterId);
        Assert.Null(rig.Sessions.Of(targetId));
        Assert.Equal(new[] { "OnContactLoggedOn", "OnContactLoggedOff" },
            Notifications(reciprocalSink).Select(notification => notification.Name));

        var reentered = new ClientSession { CharacterId = targetId, UserId = 1 };
        rig.Sessions.Enter(targetId, reentered);
        await rig.OnlineStatusNotifications.FlushAsync();
        reentered.Close();
        reentered.Close();
        await rig.OnlineStatusNotifications.FlushAsync();

        Assert.Equal(new[] { "OnContactLoggedOn", "OnContactLoggedOff", "OnContactLoggedOn", "OnContactLoggedOff" },
            Notifications(reciprocalSink).Select(notification => notification.Name));
        Assert.Equal(new ClientSession?[] { targetSession, null, reentered, null }, deliveredPresence);
        Assert.Empty(Notifications(oneWaySink));
        Assert.Empty(Notifications(unwatchedSink));
        var packets = reciprocalSink.Sent.OfType<PyObject>().ToArray();
        Assert.Equal(4, packets.Length);
        var expectedNames = new[] { "OnContactLoggedOn", "OnContactLoggedOff", "OnContactLoggedOn", "OnContactLoggedOff" };
        for (var i = 0; i < packets.Length; i++)
        {
            var address = MachoAddress.Decode(((PyTuple)packets[i].Args).Items[2]);
            Assert.Equal(expectedNames[i], address.BroadcastId);
            Assert.Equal("clientID", address.IdType);
            Assert.Equal(new PyValue[] { new PyLong(targetId) }, Notifications(reciprocalSink)[i].Args);
        }
    }

    [PgFact]
    public async Task Contact_add_persists_and_routes_the_recipient_notification()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var senderId = await rig.CreateCharacterAsync();
        var recipientId = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var recipientSession = new ClientSession { CharacterId = recipientId, UserId = 1, Sink = sink };
        rig.Sessions.Enter(recipientId, recipientSession);
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));

        Assert.IsType<PyNone>(await CallAsync(charMgr, "AddContact",
            new ClientSession { CharacterId = senderId, UserId = 1 },
            new PyLong(recipientId), new PyInt(-10), PyBool.True, PyBool.True, new PyStr("Hello")));

        var (name, args) = Assert.Single(Notifications(sink));
        Assert.Equal("OnNotificationReceived", name);
        var packet = Assert.Single(sink.Sent.OfType<PyObject>());
        var address = MachoAddress.Decode(((PyTuple)packet.Args).Items[2]);
        Assert.Equal("OnNotificationReceived", address.BroadcastId);
        Assert.Equal("clientID", address.IdType);
        Assert.Equal(5, args.Length);
        Assert.True(((PyLong)args[0]).Value > 0);
        Assert.Equal(new PyInt(89), args[1]);
        Assert.Equal(new PyLong(senderId), args[2]);
        var data = Assert.IsType<PyDict>(args[4]);
        Assert.Equal(new PyLong(-10), NotificationValue(data, "level"));
        Assert.Equal("Hello", Eve.Macho.JsSemantics.StringValue(NotificationValue(data, "message")));

        var notificationMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("notificationMgr"));
        var notifications = Assert.IsType<PyList>(await CallAsync(notificationMgr, "GetAllNotifications", recipientSession));
        var row = Assert.Single(notifications.Items);
        Assert.Equal(new PyInt(89), Field(row, "typeID"));
        Assert.Equal(new PyInt(senderId), Field(row, "senderID"));
        Assert.Equal(new PyInt(recipientId), Field(row, "receiverID"));

        await rig.RestartAsync();
        notificationMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("notificationMgr"));
        notifications = Assert.IsType<PyList>(await CallAsync(notificationMgr, "GetAllNotifications", recipientSession));
        row = Assert.Single(notifications.Items);
        Assert.Equal(new PyInt(89), Field(row, "typeID"));
        var persistedData = Assert.IsType<PyDict>(Field(row, "data"));
        Assert.Equal(new PyInt(-10), Field(persistedData, "level"));
        Assert.Equal(new PyStr("Hello"), Field(persistedData, "message"));
    }

    [PgFact]
public async Task Notification_manager_mutations_persist_and_notify_after_the_commit()
{
    var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
    Assert.False(string.IsNullOrWhiteSpace(pgUrl));
    await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(pgUrl!));
    var schema = "notification_mgr_" + Guid.NewGuid().ToString("N");
    var store = new Eve.GameStore.PersistenceStore(source, schema);
    try
    {
        await store.EnsureSchemaAsync();
        await using (var owner = await Eve.GameStore.OwnerSession.StartAsync(
            store, Eve.GameStore.PersistenceRoles.World, $"notification-mgr-{Guid.NewGuid():N}"))
        await using (var cache = new Eve.GameStore.GameStoreCache(store, owner, new Eve.GameStore.GameStoreCacheOptions
        {
            ProcessRole = Eve.GameStore.PersistenceRoles.World,
            FlushDelay = TimeSpan.FromHours(1),
        }))
        {
            await cache.RecoverAsync();
            await cache.LoadAsync("notifications");
            const long character = 90000091;
            var box = new System.Text.Json.Nodes.JsonObject
            {
                ["order"] = new System.Text.Json.Nodes.JsonArray(100, 101, 102, 103),
                ["byID"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["100"] = Notification(100, character),
                    ["101"] = Notification(101, character),
                    ["102"] = Notification(102, character, 3),
                    ["103"] = Notification(103, character, 3),
                },
            };
            Assert.True(cache.Write("notifications", $"/boxes/{character}", box).Success);

            async Task<System.Text.Json.Nodes.JsonObject> ReadTable(string table, CancellationToken cancellationToken)
            {
                await cache.LoadAsync(table, cancellationToken);
                return cache.Read(table, "/").Data as System.Text.Json.Nodes.JsonObject ?? new();
            }

            async Task<bool> DeletedRowIsCommitted(CancellationToken cancellationToken)
            {
                await using var persistedCache = new Eve.GameStore.GameStoreCache(
                    store, null, new Eve.GameStore.GameStoreCacheOptions
                    {
                        ProcessRole = Eve.GameStore.PersistenceRoles.Reader,
                    });
                await persistedCache.LoadAsync("notifications", cancellationToken);
                var persistedBox = persistedCache.Read("notifications", $"/boxes/{character}").Data
                    as System.Text.Json.Nodes.JsonObject;
                return persistedBox?["byID"]?["102"] is null;
            }

            var notificationWasCommittedAtSend = false;
            var sessions = new Eve.Services.CharacterSessions();
            var requestingSession = new ClientSession { CharacterId = character, UserId = 1 };
            var sink = new RecordingPacketSink(() =>
                notificationWasCommittedAtSend = DeletedRowIsCommitted(CancellationToken.None).GetAwaiter().GetResult());
            sessions.Enter(character, new ClientSession { CharacterId = character, UserId = 1, Sink = sink });

            var adapter = new NotificationMgrHostAdapter(cache, ReadTable, sessions);
            var manager = new Eve.Services.Core.NotificationMgrService(
                adapter.ReadAsync, () => 1_700_000_000_000, mutate: adapter.MutateAsync);
            Assert.True(manager.HasMethod("MarkAllAsProcessed"));
            Assert.True(manager.HasMethod("MarkAsProcessed"));
            Assert.True(manager.HasMethod("DeleteNotifications"));
            Assert.True(manager.HasMethod("MarkGroupAsProcessed"));
            Assert.True(manager.HasMethod("DeleteGroupNotifications"));
            Assert.True(manager.HasMethod("DeleteAllNotifications"));

            await CallAsync(manager, "MarkAsProcessed", requestingSession, new PyList([new PyInt(100)]));
            Assert.Equal(3, Assert.IsType<PyList>(
                await CallAsync(manager, "GetUnprocessed", requestingSession)).Items.Count);

            await CallAsync(manager, "MarkGroupAsProcessed", requestingSession, new PyInt(2));
            Assert.Equal(2, Assert.IsType<PyList>(
                await CallAsync(manager, "GetUnprocessed", requestingSession)).Items.Count);
            await CallAsync(manager, "MarkAllAsProcessed", requestingSession);
            await CallAsync(manager, "DeleteNotifications", requestingSession, new PyList([new PyInt(102)]));
            await CallAsync(manager, "DeleteGroupNotifications", requestingSession, new PyInt(3));

            Assert.Equal(2, Assert.IsType<PyList>(
                await CallAsync(manager, "GetAllNotifications", requestingSession)).Items.Count);
            await CallAsync(manager, "DeleteAllNotifications", requestingSession);
            Assert.Empty(Assert.IsType<PyList>(
                await CallAsync(manager, "GetAllNotifications", requestingSession)).Items);
            Assert.Empty(Assert.IsType<PyList>(
                await CallAsync(manager, "GetUnprocessed", requestingSession)).Items);
            Assert.True(notificationWasCommittedAtSend);
            var deleted = Notifications(sink).Where(item => item.Name == "OnNotificationDeleted").ToArray();
            Assert.Equal(3, deleted.Length);
            Assert.All(deleted, item => Assert.Single(item.Args));
            await cache.FlushTableAsync("notifications");
        }

        await using (var persistedCache = new Eve.GameStore.GameStoreCache(
            store, null, new Eve.GameStore.GameStoreCacheOptions
            {
                ProcessRole = Eve.GameStore.PersistenceRoles.Reader,
            }))
        {
            await persistedCache.LoadAsync("notifications");
            async Task<System.Text.Json.Nodes.JsonObject> ReadTable(string table, CancellationToken cancellationToken)
            {
                await persistedCache.LoadAsync(table, cancellationToken);
                return persistedCache.Read(table, "/").Data as System.Text.Json.Nodes.JsonObject ?? new();
            }

            var adapter = new NotificationMgrHostAdapter(
                persistedCache, ReadTable, new Eve.Services.CharacterSessions());
            var manager = new Eve.Services.Core.NotificationMgrService(adapter.ReadAsync, () => 1_700_000_000_000);
            var requestingSession = new ClientSession { CharacterId = 90000091, UserId = 1 };
            Assert.Empty(Assert.IsType<PyList>(
                await CallAsync(manager, "GetAllNotifications", requestingSession)).Items);
            Assert.Empty(Assert.IsType<PyList>(
                await CallAsync(manager, "GetUnprocessed", requestingSession)).Items);
        }
    }
    finally
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    static System.Text.Json.Nodes.JsonObject Notification(long id, long receiverId, long groupId = 2) => new()
    {
        ["notificationID"] = id,
        ["typeID"] = 1,
        ["senderID"] = 10,
        ["receiverID"] = receiverId,
        ["groupID"] = groupId,
        ["processed"] = false,
        ["created"] = "133000000000000000",
        ["data"] = new System.Text.Json.Nodes.JsonObject(),
    };
}

    [PgFact]
    public async Task Skill_bundle_info_answers_the_client_empty_shape_through_the_registry()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        var skills = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        Assert.True(skills.HasMethod("GetSkillBundleInfo"));

        // the client unpacks name, icon (eve-logic crate/lootItem.py 87); no bundle data exists, so the label is empty and the icon None
        var answer = Assert.IsType<PyTuple>(await CallAsync(skills, "GetSkillBundleInfo", session, new PyInt(1)));
        Assert.Equal(2, answer.Items.Count);
        Assert.Equal(string.Empty, Assert.IsType<PyWString>(answer.Items[0]).Value);
        Assert.IsType<PyNone>(answer.Items[1]);
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task DogmaIM_skill_injection_consumes_the_skillbook_and_persists_the_skill_through_the_host()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var skill = catalog.Skills.First(candidate => candidate.Published && candidate.Rank == 1 && !candidate.Needs.Any());
        var skillbook = await GiveItemAsync(rig, character, skill.TypeId, rig.Station, 4, singleton: true);
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        var result = await CallAsync(dogma, "InjectSkillIntoBrain", session, new PyList([new PyLong(skillbook)]));

        Assert.Equal(new PyList([new PyInt(skill.TypeId)]), result);
        Assert.Null(await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(skillbook));
        Assert.Contains(await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character),
            saved => saved.TypeId == skill.TypeId && saved.Level == 0 && saved.SkillPoints == 0);
        var notifications = Notifications(sink);
        Assert.Equal(["OnServerSkillsChanged", "OnSkillsChanged"], notifications.Select(notification => notification.Name).Take(2));
        Assert.Equal(new PyInt(skill.TypeId), Assert.Single(Assert.IsType<PyDict>(notifications[0].Args[0]).Entries).Key);
        Assert.Equal(new PyInt(skill.TypeId), Assert.Single(Assert.IsType<PyDict>(notifications[1].Args[0]).Entries).Key);
        Assert.Equal("OnItemChange", notifications.Last().Name);
        var itemChange = notifications.Last().Args;
        Assert.Equal(3, itemChange.Length);
        var changedItem = Assert.IsType<PyPackedRow>(itemChange[0]);
        Assert.Equal(InventoryWire.Id(skillbook), changedItem.Values[0]);
        Assert.Equal(InventoryWire.Id(6), changedItem.Values[3]);
        Assert.Equal(new PyInt(skill.GroupId), changedItem.Values[6]);
        Assert.Equal(new PyInt(SkillCatalog.SkillCategoryId), changedItem.Values[7]);
        var itemDiff = Assert.IsType<PyDict>(itemChange[1]);
        Assert.Equal(new PyInt(3), Assert.Single(itemDiff.Entries).Key);
        Assert.Equal(InventoryWire.Id(rig.Station), itemDiff.Entries[0].Value);

        await rig.RestartAsync();
        Assert.Contains(await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character),
            saved => saved.TypeId == skill.TypeId && saved.Level == 0 && saved.SkillPoints == 0);
    }

    [PgFact]
    public async Task Concurrent_direct_purchase_and_skillbook_injection_for_the_same_skill_commit_only_one_grant()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sessionPurchase = new ClientSession { CharacterId = character, UserId = 1, SessionId = 71_001, Sink = new RecordingPacketSink() };
        var sessionInject = new ClientSession { CharacterId = character, UserId = 1, SessionId = 71_002, Sink = new RecordingPacketSink() };
        sessionInject.Set("stationid", new PyInt(rig.Station));
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var owned = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character)).Select(skill => skill.TypeId).ToHashSet();
        var skill = catalog.Skills.First(candidate => candidate.Published && candidate.AvailableForPurchase && candidate.BasePrice > 0 && !owned.Contains(candidate.TypeId));
        var book = await GiveItemAsync(rig, character, skill.TypeId, rig.Station, 4, singleton: true);
        await rig.FundAsync(character, 100_000_000m);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var createFunction = new NpgsqlCommand(
                $"CREATE FUNCTION {rig.SkillsSchema}.pause_skill_grant() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(0.6); RETURN NEW; END; $$", connection);
            await createFunction.ExecuteNonQueryAsync();
            await using var createTrigger = new NpgsqlCommand(
                $"CREATE TRIGGER pause_skill_grant BEFORE INSERT ON {rig.SkillsSchema}.character_skills FOR EACH ROW EXECUTE FUNCTION {rig.SkillsSchema}.pause_skill_grant()", connection);
            await createTrigger.ExecuteNonQueryAsync();
        }

        var purchase = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        async Task<bool> TryPurchaseAsync()
        {
            try
            {
                var result = Assert.IsType<PyTuple>(await CallAsync(purchase, "PurchaseSkills", sessionPurchase, new PyList([new PyInt(skill.TypeId)])));
                Assert.Equal(new PyInt(skill.TypeId), Assert.Single(result.Items));
                return true;
            }
            catch (MachoWrappedException error) when (error.Error.IsUserError)
            {
                return false;
            }
        }
        async Task<bool> TryInjectAsync()
        {
            try
            {
                var result = Assert.IsType<PyList>(await CallAsync(dogma, "InjectSkillIntoBrain", sessionInject, new PyList([new PyLong(book)])));
                Assert.Equal(new PyInt(skill.TypeId), Assert.Single(result.Items));
                return true;
            }
            catch (MachoWrappedException error) when (error.Error.IsUserError)
            {
                return false;
            }
        }

        var outcomes = await Task.WhenAll(TryPurchaseAsync(), TryInjectAsync());

        Assert.Single(outcomes, succeeded => succeeded);
        Assert.Single(await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character), saved => saved.TypeId == skill.TypeId);
        var itemRemains = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(book) is not null;
        var balance = await BalanceAsync(rig, character);
        var bookConsumed = !itemRemains;
        var purchaseCharged = balance < 100_000_000m;
        Assert.True(bookConsumed ^ purchaseCharged, "exactly one of the skillbook and purchase payment must be consumed");
    }

    [PgFact]
    public async Task Disabled_expert_systems_do_not_project_existing_grants_or_allow_item_activation()
    {
        await using var rig = await CharacterHostRig.CreateAsync(clusterOptions: new ClusterOptions { ExpertSystemsEnabled = false });
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyInt(rig.Station));
        var catalog = ExpertSystemCatalog.LoadEmbedded();
        var expertSystem = Assert.IsType<ExpertSystemType>(catalog.Get(63826));
        var existingSkills = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character)).Select(skill => skill.TypeId).ToHashSet();
        var grant = expertSystem.SkillsGranted.First(skill => !existingSkills.Contains(skill.TypeId));
        var now = rig.NowUnixMs;
        var expertStore = new PgExpertSystemStore(rig.Source, rig.SkillsSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await expertStore.UpsertAsync(connection, transaction,
                new ExpertSystemEntry(character, expertSystem.TypeId, now, now + ExpertSystemRules.Day, null, "item", now));
            await transaction.CommitAsync();
        }
        var itemId = await GiveItemAsync(rig, character, expertSystem.TypeId, rig.Station, 4, singleton: true);
        var skills = Assert.IsType<PyDict>(await CallAsync(Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler")), "GetSkills", session));

        Assert.DoesNotContain(skills.Entries, entry => ((PyInt)entry.Key).Value == grant.TypeId);
        var flags = Assert.IsType<PyDict>(await CallAsync(Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("machoNet")), "GetGlobalConfig", session));
        Assert.Equal(new PyInt(0), flags.Entries.Single(entry => entry.Key is PyStr { Value: "expert_system_feature_enabled" }).Value);
        var manager = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("expertSystemMgr"));
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(manager, "ConsumeExpertSystem", session, new PyLong(itemId)));
        Assert.Equal("CustomNotify", refusal.Error.UserErrorMessage);
        var refusalHeader = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(refusal.Error.Payload).Header);
        var refusalArguments = Assert.IsType<PyTuple>(refusalHeader.Items[1]);
        var refusalValues = Assert.IsType<PyDict>(refusalArguments.Items[1]);
        var refusalMessage = Assert.IsType<PyStr>(refusalValues.Entries.Single(entry => entry.Key is PyStr { Value: "notify" }).Value);
        Assert.Equal("Expert Systems are disabled in server config.", refusalMessage.Value);
        Assert.NotNull(await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(itemId));
        Assert.Single(await expertStore.ForCharacterAsync(character));
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task Disabled_direct_skill_purchase_refuses_the_rpc_without_charging_or_granting()
    {
        await using var rig = await CharacterHostRig.CreateAsync(clusterOptions: new ClusterOptions { SkillPurchaseEnabled = false });
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var owned = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character)).Select(skill => skill.TypeId).ToHashSet();
        var skill = catalog.Skills.First(candidate => candidate.Published && candidate.AvailableForPurchase && candidate.BasePrice > 0 && !owned.Contains(candidate.TypeId));
        var startingBalance = Math.Max(10_000_000m, decimal.Round(skill.BasePrice * 1.3m, 2, MidpointRounding.AwayFromZero) + 1m);
        await rig.FundAsync(character, startingBalance);
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var flags = Assert.IsType<PyDict>(await CallAsync(Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("machoNet")), "GetGlobalConfig", session));

        Assert.Equal(new PyInt(0), flags.Entries.Single(entry => entry.Key is PyStr { Value: "SkillPurchaseEnabled" }).Value);
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(
            () => CallAsync(service, "PurchaseSkills", session, new PyList([new PyInt(skill.TypeId)])));

        Assert.Equal("SkillPurchaseDisabled", refusal.Error.UserErrorMessage);
        Assert.Equal(startingBalance, await BalanceAsync(rig, character));
        Assert.DoesNotContain(await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character), saved => saved.TypeId == skill.TypeId);
        Assert.Empty(await new WalletTransactions(rig.Source, rig.WalletSchema).JournalAsync(character));
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task Skill_injectors_require_the_docked_hangar_or_active_ship_cargo_in_preview_and_mutation()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var injectorType = catalog.ItemTypeId("Large Skill Injector")!.Value;
        var remoteStation = (await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source))).All.First(station => station.Id != rig.Station).Id;
        var remote = await CreateHostItemAsync(rig, new PgItemStore(rig.Source, rig.InventorySchema),
            new NewItem(injectorType, character, remoteStation, 4, 1, Singleton: true));
        var wrongFlag = await CreateHostItemAsync(rig, new PgItemStore(rig.Source, rig.InventorySchema),
            new NewItem(injectorType, character, rig.Station, 5, 1, Singleton: true));
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = new RecordingPacketSink() };
        session.Set("stationid", new PyInt(rig.Station));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var items = new PgItemStore(rig.Source, rig.InventorySchema);

        foreach (var itemId in new[] { remote, wrongFlag })
        {
            var preview = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service, "CheckInjectionConstraints", session, new PyLong(itemId), new PyInt(1)));
            Assert.Equal("CustomNotify", preview.Error.UserErrorMessage);
            var mutation = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service, "InjectSkillpoints", session, new PyLong(itemId), new PyInt(1)));
            Assert.Equal("CustomNotify", mutation.Error.UserErrorMessage);
            Assert.NotNull(await items.GetAsync(itemId));
        }
        Assert.Equal(0, await new PgSkillStore(rig.Source, rig.SkillsSchema).FreeSkillPointsAsync(character));

        var capsuleType = catalog.ItemTypeId("Capsule")!.Value;
        var shipId = await CreateHostItemAsync(rig, items, new NewItem(capsuleType, character, rig.Station, 4, 1, Singleton: true));
        var cargoItem = await CreateHostItemAsync(rig, items, new NewItem(injectorType, character, shipId, 5, 1, Singleton: true));
        var inSpace = new ClientSession { CharacterId = character, UserId = 1, Sink = new RecordingPacketSink() };
        inSpace.Set("shipid", new PyLong(shipId));
        Assert.IsType<PyNone>(await CallAsync(service, "CheckInjectionConstraints", inSpace, new PyLong(cargoItem), new PyInt(1)));
        Assert.IsType<PyInt>(await CallAsync(service, "InjectSkillpoints", inSpace, new PyLong(cargoItem), new PyInt(1)));
        Assert.Null(await items.GetAsync(cargoItem));
    }

    [PgFact]
    public async Task Skill_extraction_rechecks_the_queue_after_a_concurrent_queue_save()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var store = new PgSkillStore(rig.Source, rig.SkillsSchema);
        var existing = (await store.ForCharacterAsync(character)).Select(skill => skill.TypeId).ToHashSet();
        var all = catalog.Skills.ToArray();
        var selected = all.First(skill => skill.Published && skill.Rank >= 2 && skill.Needs.Count == 0 && !existing.Contains(skill.TypeId)
            && !all.Any(dependent => dependent.Needs.Any(requirement => requirement.TypeId == skill.TypeId)));
        var allocations = new Dictionary<int, CharacterSkill>();
        var selectedPoints = SkillPoints.ForTraining(selected.Rank, 4);
        allocations[selected.TypeId] = new CharacterSkill(selected.TypeId, SkillPoints.LevelAt(selected.Rank, selectedPoints), selectedPoints);
        foreach (var requirement in selected.Needs)
        {
            var required = catalog.Get(requirement.TypeId)!;
            var points = SkillPoints.ForTraining(required.Rank, requirement.Level);
            allocations[required.TypeId] = new CharacterSkill(required.TypeId, requirement.Level, points);
        }
        var total = allocations.Values.Sum(skill => skill.SkillPoints);
        foreach (var candidate in all.Where(skill => skill.TypeId != selected.TypeId && !existing.Contains(skill.TypeId)
                     && !allocations.ContainsKey(skill.TypeId)
                     && !all.Any(dependent => dependent.TypeId == skill.TypeId && dependent.Needs.Any(requirement => requirement.TypeId == selected.TypeId))))
        {
            if (total >= 5_500_000) break;
            var points = Math.Min(SkillPoints.ForTraining(candidate.Rank, 5), 5_500_000 - total);
            allocations[candidate.TypeId] = new CharacterSkill(candidate.TypeId, SkillPoints.LevelAt(candidate.Rank, points), points);
            total += points;
        }
        Assert.True(total >= 5_500_000, $"The pinned SDE did not provide enough eligible test skills: {total} SP.");
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.GrantAsync(connection, transaction, character, allocations.Values.ToArray());
            await transaction.CommitAsync();
        }

        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var capsuleType = catalog.ItemTypeId("Capsule")!.Value;
        var extractorType = catalog.ItemTypeId("Skill Extractor")!.Value;
        var capsule = await CreateHostItemAsync(rig, items, new NewItem(capsuleType, character, rig.Station, 4, 1, Singleton: true));
        var extractor = await CreateHostItemAsync(rig, items, new NewItem(extractorType, character, rig.Station, 4, 1, Singleton: true));
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = new RecordingPacketSink() };
        session.Set("stationid", new PyInt(rig.Station));
        session.Set("shipid", new PyLong(capsule));

        await using var heldConnection = await rig.Source.OpenConnectionAsync();
        await using var heldTransaction = await heldConnection.BeginTransactionAsync();
        Assert.NotNull(await items.LockAndGetAsync(heldConnection, heldTransaction, extractor, CancellationToken.None));

        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var createFunction = new NpgsqlCommand(
                $"CREATE FUNCTION {rig.SkillsSchema}.pause_skill_queue_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(1.2); RETURN NEW; END; $$", connection);
            await createFunction.ExecuteNonQueryAsync();
            await using var createTrigger = new NpgsqlCommand(
                $"CREATE TRIGGER pause_skill_queue_insert BEFORE INSERT ON {rig.SkillsSchema}.skill_queue FOR EACH ROW WHEN (NEW.character_id = {character}) EXECUTE FUNCTION {rig.SkillsSchema}.pause_skill_queue_insert()", connection);
            await createTrigger.ExecuteNonQueryAsync();
        }

        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var queueSave = CallAsync(service, "SaveNewQueue", session,
            new PyList([Py.Tuple(new PyInt(selected.TypeId), new PyInt(5))]));
        try
        {
            await WaitForPgSleepAsync(rig, $"INSERT INTO {rig.SkillsSchema}.skill_queue");
        }
        catch
        {
            await queueSave;
            throw;
        }
        var extraction = CallAsync(service, "ExtractSkills", session, new PyLong(extractor),
            ExtractionRequest());
        await WaitForSkillOperationWaitAsync(rig, character);
        await queueSave;
        await heldTransaction.RollbackAsync();

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => extraction);
        Assert.Equal("CustomNotify", refusal.Error.UserErrorMessage);
        var errorHeader = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(refusal.Error.Payload).Header);
        var errorArguments = Assert.IsType<PyTuple>(errorHeader.Items[1]);
        var errorValues = Assert.IsType<PyDict>(errorArguments.Items[1]);
        var refusalMessage = Assert.IsType<PyStr>(errorValues.Entries.Single(entry => entry.Key is PyStr { Value: "notify" }).Value);
        Assert.Contains("training queue", refusalMessage.Value, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await items.GetAsync(extractor));
        Assert.Equal(selectedPoints, (await store.ForCharacterAsync(character)).Single(skill => skill.TypeId == selected.TypeId).SkillPoints);
        Assert.Equal(selected.TypeId, Assert.Single((await new PgTrainingStore(rig.Source, rig.SkillsSchema).LoadAsync(character)).Entries).TypeId);

        PyDict ExtractionRequest()
        {
            var eligible = allocations.Values
                .Where(candidate => !all.Any(dependent => allocations.TryGetValue(dependent.TypeId, out var trained)
                    && trained.SkillPoints > 0 && dependent.Needs.Any(requirement => requirement.TypeId == candidate.TypeId)))
                .ToDictionary(candidate => candidate.TypeId);
            var requested = new List<CharacterSkill> { eligible[selected.TypeId] };
            var points = requested[0].SkillPoints;
            foreach (var candidate in eligible.Values.Where(candidate => candidate.TypeId != selected.TypeId).OrderBy(candidate => candidate.TypeId))
            {
                if (points >= 500_000) break;
                requested.Add(candidate);
                points += candidate.SkillPoints;
            }
            Assert.True(points >= 500_000, $"Only {points} extraction-eligible SP were prepared.");
            return new PyDict([.. requested.Select(candidate => new KeyValuePair<PyValue, PyValue>(new PyInt(candidate.TypeId), new PyLong(candidate.SkillPoints)))]);
        }
    }

    [PgFact]
    public async Task Expert_system_expiry_retries_a_durable_lapse_notice_when_the_delete_transaction_cannot_commit()
    {
        var initialTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var rig = await CharacterHostRig.CreateAsync(nowUnixMs: initialTime);
        var character = await rig.CreateCharacterAsync();
        const int expertSystemTypeId = 63826;
        var expiredAt = initialTime - 1;
        var installedAt = expiredAt - ExpertSystemRules.Day;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {rig.SkillsSchema}.character_expert_systems(character_id, type_id, installed_at_ms, expires_at_ms, source_item_id, grant_reason, updated_at_ms) " +
            "VALUES ($1, $2, $3, $4, NULL, 'item', $3)", connection))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = character });
            insert.Parameters.Add(new NpgsqlParameter { Value = expertSystemTypeId });
            insert.Parameters.Add(new NpgsqlParameter { Value = installedAt });
            insert.Parameters.Add(new NpgsqlParameter { Value = expiredAt });
            await insert.ExecuteNonQueryAsync();
        }

        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var createFunction = new NpgsqlCommand(
                $"CREATE FUNCTION {rig.SkillsSchema}.fail_expired_expert_system_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'forced expert system expiry commit failure'; END; $$", connection);
            await createFunction.ExecuteNonQueryAsync();
            await using var createTrigger = new NpgsqlCommand(
                $"CREATE CONSTRAINT TRIGGER fail_expired_expert_system_commit AFTER DELETE ON {rig.SkillsSchema}.character_expert_systems DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.character_id = {character}) EXECUTE FUNCTION {rig.SkillsSchema}.fail_expired_expert_system_commit()", connection);
            await createTrigger.ExecuteNonQueryAsync();
            await using var due = new NpgsqlCommand(
                $"UPDATE {rig.SchedulerSchema}.jobs SET run_at_ms = 0 WHERE kind = 'skills.expert-systems.expire' AND key = 'expire'", connection);
            await due.ExecuteNonQueryAsync();
        }

        Assert.True(await rig.Scheduler.RunNowAsync("skills.expert-systems.expire", "expire"));
        Assert.Equal(1, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.character_expert_systems WHERE character_id = {character}"));
        var firstNotices = await NoticesAsync(rig, character);
        var firstNotice = Assert.Single(firstNotices, record => record["typeID"]!.GetValue<int>() == 253);
        Assert.Equal(expertSystemTypeId, firstNotice["data"]!["typeID"]!.GetValue<int>());

        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var dropTrigger = new NpgsqlCommand($"DROP TRIGGER fail_expired_expert_system_commit ON {rig.SkillsSchema}.character_expert_systems", connection);
            await dropTrigger.ExecuteNonQueryAsync();
            await using var dropFunction = new NpgsqlCommand($"DROP FUNCTION {rig.SkillsSchema}.fail_expired_expert_system_commit()", connection);
            await dropFunction.ExecuteNonQueryAsync();
        }
        rig.AdvanceTo(initialTime + 60_000);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var due = new NpgsqlCommand(
            $"UPDATE {rig.SchedulerSchema}.jobs SET run_at_ms = 0 WHERE kind = 'skills.expert-systems.expire' AND key = 'expire'", connection))
            await due.ExecuteNonQueryAsync();
        Assert.True(await rig.Scheduler.RunNowAsync("skills.expert-systems.expire", "expire"));

        Assert.Equal(0, await ScalarAsync(rig, $"SELECT count(*) FROM {rig.SkillsSchema}.character_expert_systems WHERE character_id = {character}"));
        Assert.Single(await NoticesAsync(rig, character), record => record["typeID"]!.GetValue<int>() == 253);
    }

    [PgFact]
    public async Task DogmaIM_drone_settings_merge_partial_changes_and_survive_a_host_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, UserId = 1 };
        session.Set("stationid", new PyInt(rig.Station));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        Assert.True(dogma.HasMethod("ChangeDroneSettings"));

        async Task<PyValue> ChangeAsync(PyValue changes)
        {
            var nested = Py.Tuple(new PyStr("ChangeDroneSettings"), Py.Tuple(changes), PyNone.Instance);
            var bound = Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", session,
                Py.Tuple(new PyInt(rig.Station), new PyInt(3)), nested));
            return bound.Items[1];
        }

        async Task<Dictionary<int, bool>> ReadSettingsAsync()
        {
            var saved = new Dictionary<int, bool>();
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT attribute_id, value FROM {rig.CharactersSchema}.character_drone_settings WHERE character_id = $1 ORDER BY attribute_id", connection);
            command.Parameters.AddWithValue(character);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                saved.Add(reader.GetInt32(0), reader.GetBoolean(1));
            return saved;
        }

        Assert.Equal(PyBool.True, await ChangeAsync(new PyDict([new(new PyInt(1275), PyBool.True), new(new PyInt(1297), PyBool.False)])));
        var first = await ReadSettingsAsync();
        Assert.Equal(new Dictionary<int, bool> { [1275] = true, [1297] = false }, first);

        await rig.RestartAsync();
        dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        Assert.Equal(PyBool.True, await ChangeAsync(new PyDict([new(new PyInt(1275), PyBool.False), new(new PyInt(9999), PyBool.True)])));
        Assert.Equal(new Dictionary<int, bool> { [1275] = false, [1297] = false }, await ReadSettingsAsync());
    }

    [PgFact]
    public async Task DogmaIM_create_newbie_ship_boards_the_race_ship_and_rejects_a_second_request()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var frigate = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, frigate);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("charid", new PyLong(character));
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(frigate));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        async Task<PyValue> CreateNewbieShipAsync(long currentShipId)
        {
            var nested = Py.Tuple(new PyStr("CreateNewbieShip"),
                Py.Tuple(new PyLong(currentShipId), new PyLong(rig.Station)), PyNone.Instance);
            var bound = Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", session,
                Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));
            return bound.Items[1];
        }

        Assert.IsType<PyNone>(await CreateNewbieShipAsync(frigate));

        var active = (await rig.Characters.GetAsync(character))!.ShipId!.Value;
        Assert.NotEqual(frigate, active);
        Assert.Equal(active, session.GetInt("shipid"));
        var creation = await Eve.Characters.CreationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source), rig.Station);
        Assert.Equal(creation.Race((await rig.Characters.GetAsync(character))!.RaceId)!.ShipTypeId,
            (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(active))!.TypeId);
        var activeItem = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(active);
        Assert.Equal((character, rig.Station, ItemFlags.Hangar, true),
            (activeItem!.OwnerId, activeItem.LocationId, activeItem.Flag, activeItem.Singleton));
        var starterModules = await new PgItemStore(rig.Source, rig.InventorySchema).InLocationAsync(active, character);
        Assert.Equal(3, starterModules.Count);
        Assert.Equal(new[] { "OnItemChange", "OnItemChange", "OnItemChange", "OnItemChange" },
            Notifications(sink).Select(notification => notification.Name));

        var alreadyRookie = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            CreateNewbieShipAsync(active));
        Assert.Equal("AlreadyInNewbieShip", alreadyRookie.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task DogmaIM_unload_ammo_to_a_personal_container_merges_partial_and_full_stacks()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var charge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 40, singleton: false);
        var container = await GiveItemAsync(rig, character, 17365, rig.Station, ItemFlags.Hangar, singleton: true);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("structureid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        async Task<PyValue> UnloadAsync(PyValue destination, PyValue quantity)
        {
            var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
                new PyLong(ship), new PyList([new PyLong(gun)]), destination, quantity), PyNone.Instance);
            var bound = Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", session,
                Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));
            return bound.Items[1];
        }

        var first = Assert.IsType<PyList>(await UnloadAsync(
            Py.Tuple(new PyLong(container), new PyLong(character), new PyInt(CharacterShips.ContainerFlag)), new PyInt(15)));
        var firstId = InventoryId(Assert.Single(first.Items));
        Assert.Equal(25, (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(charge))!.Quantity);
        var inContainer = await new PgItemStore(rig.Source, rig.InventorySchema).InLocationAsync(container, character);
        Assert.Equal((firstId, 15L, CharacterShips.ContainerFlag),
            (Assert.Single(inContainer).Id, Assert.Single(inContainer).Quantity, Assert.Single(inContainer).Flag));

        var merged = Assert.IsType<PyList>(await UnloadAsync(new PyLong(container), PyNone.Instance));
        Assert.Equal(firstId, InventoryId(Assert.Single(merged.Items)));
        await rig.RestartAsync();
        var persisted = await new PgItemStore(rig.Source, rig.InventorySchema).InLocationAsync(container, character);
        Assert.Equal((firstId, 40L), (Assert.Single(persisted).Id, Assert.Single(persisted).Quantity));
        Assert.Equal(0, (await new PgItemStore(rig.Source, rig.InventorySchema).InLocationAsync(ship, character)).Count(item => item.Id == charge));
        var notificationNames = Notifications(sink).Select(notification => notification.Name).ToArray();
        Assert.DoesNotContain("OnItemsChanged", notificationNames);
        Assert.Equal("OnItemChange", notificationNames[0]);
        Assert.Equal("OnItemChange", notificationNames[^1]);
        Assert.Contains("OnModuleAttributeChanges", notificationNames);
    }

    [PgFact]
    public async Task DogmaIM_unload_ammo_refuses_a_full_container_without_changing_items_or_notifying()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var charge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 40, singleton: false);
        var container = await GiveItemAsync(rig, character, 17363, rig.Station, ItemFlags.Hangar, singleton: true);
        var contentsBefore = new List<long>();
        for (var i = 0; i < 24; i++)
            contentsBefore.Add(await PutItemAsync(rig, character, 10678, container, CharacterShips.ContainerFlag, 1, singleton: true));

        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
            new PyLong(ship), new PyList([new PyLong(gun)]),
            Py.Tuple(new PyLong(container), new PyLong(character), new PyInt(CharacterShips.ContainerFlag)), PyNone.Instance), PyNone.Instance);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(dogma, "MachoBindObject", session,
            Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));

        Assert.Equal("NoSpaceForThat", error.Error.UserErrorMessage);
        Assert.Equal(40, (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(charge))!.Quantity);
        var contentsAfter = await new PgItemStore(rig.Source, rig.InventorySchema).InLocationAsync(container, character);
        Assert.Equal(contentsBefore.Order(), contentsAfter.Select(item => item.Id).Order());
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task DogmaIM_unload_ammo_rejects_a_fitting_destination_without_changing_items_or_notifying()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var otherGun = await GiveItemAsync(rig, character, 10678, ship, 29, singleton: true);
        var charge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 40, singleton: false);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
            new PyLong(ship), new PyList([new PyLong(gun)]),
            Py.Tuple(new PyLong(ship), new PyLong(character), new PyInt(29)), PyNone.Instance), PyNone.Instance);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(dogma, "MachoBindObject", session,
            Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));

        Assert.Equal("CantMoveChargesBetweenModules", error.Error.UserErrorMessage);
        Assert.Equal(MachoErrors.UserErrorPayload("CantMoveChargesBetweenModules"), error.Error.Payload);
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        Assert.Equal((ship, 28, 40L), ((await items.GetAsync(charge))!.LocationId, (await items.GetAsync(charge))!.Flag,
            (await items.GetAsync(charge))!.Quantity));
        Assert.Equal(otherGun, Assert.Single(await items.InLocationAsync(ship, character), item => item.Flag == 29).Id);
        Assert.Equal((222, 40L), ((await items.GetAsync(charge))!.TypeId, (await items.GetAsync(charge))!.Quantity));
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task DogmaIM_load_ammo_from_a_personal_container_moves_the_selected_stack_into_the_fitted_module()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var container = await GiveItemAsync(rig, character, 17365, rig.Station, ItemFlags.Hangar, singleton: true);
        var stack = await PutItemAsync(rig, character, 222, container, CharacterShips.ContainerFlag, quantity: 40, singleton: false);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var nested = Py.Tuple(new PyStr("LoadAmmo"), Py.Tuple(
            new PyLong(ship), new PyList([new PyLong(gun)]), new PyList([new PyLong(stack)]), new PyLong(container)), PyNone.Instance);

        var bound = Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", session,
            Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));

        Assert.IsType<PyNone>(bound.Items[1]);
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var loaded = Assert.IsType<Eve.Inventory.Item>(await items.GetAsync(stack));
        Assert.Equal((character, ship, 28, 40L), (loaded.OwnerId, loaded.LocationId, loaded.Flag, loaded.Quantity));
        Assert.Empty(await items.InLocationAsync(container, character));
        Assert.Contains("OnChargeBeingLoadedToModule", Notifications(sink).Select(notification => notification.Name));
        await rig.RestartAsync();
        var persisted = Assert.IsType<Eve.Inventory.Item>(await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack));
        Assert.Equal((character, ship, 28, 40L), (persisted.OwnerId, persisted.LocationId, persisted.Flag, persisted.Quantity));
    }

    [PgFact]
    public async Task DogmaIM_unload_ammo_fits_the_exact_remaining_cargo_volume_and_merges_the_stack()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var charge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 40, singleton: false);
        var dogma = await Eve.Dogma.DogmaStatic.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        await dogma.EnsureTypesAsync([587, 222]);
        var capacity = dogma.Type(587)!.Attributes[dogma.AttributeId("capacity")!.Value];
        var unitVolume = dogma.Type(222)!.Attributes[dogma.AttributeId("volume")!.Value];
        var cargoQuantity = checked((long)Math.Floor((capacity - 40 * unitVolume) / unitVolume));
        var cargoStack = await PutItemAsync(rig, character, 222, ship, CharacterShips.CargoFlag, cargoQuantity, singleton: false);
        var session = new ClientSession { CharacterId = character, UserId = 1 };
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
            new PyLong(ship), new PyList([new PyLong(gun)]), new PyLong(ship), PyNone.Instance), PyNone.Instance);

        var bound = Assert.IsType<PyTuple>(await CallAsync(service, "MachoBindObject", session,
            Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));

        Assert.IsType<PyList>(bound.Items[1]);
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        Assert.Equal(cargoQuantity + 40, (await items.GetAsync(cargoStack))!.Quantity);
        Assert.Null(await items.GetAsync(charge));
        Assert.True((cargoQuantity + 40) * unitVolume <= capacity + 1e-7);
    }

    [PgFact]
    public async Task DogmaIM_unload_ammo_preflights_all_modules_when_ship_cargo_has_room_for_only_one()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var firstGun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var secondGun = await GiveItemAsync(rig, character, 10678, ship, 29, singleton: true);
        var firstCharge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 40, singleton: false);
        var secondCharge = await PutItemAsync(rig, character, 222, ship, 29, quantity: 40, singleton: false);
        var dogma = await Eve.Dogma.DogmaStatic.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        await dogma.EnsureTypesAsync([587, 222]);
        var capacity = dogma.Type(587)!.Attributes[dogma.AttributeId("capacity")!.Value];
        var unitVolume = dogma.Type(222)!.Attributes[dogma.AttributeId("volume")!.Value];
        var cargoQuantity = checked((long)Math.Floor((capacity - 40 * unitVolume) / unitVolume));
        var cargoStack = await PutItemAsync(rig, character, 222, ship, CharacterShips.CargoFlag, cargoQuantity, singleton: false);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
            new PyLong(ship), new PyList([new PyLong(firstGun), new PyLong(secondGun)]), new PyLong(ship), PyNone.Instance), PyNone.Instance);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service, "MachoBindObject", session,
            Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));

        Assert.Equal("NotEnoughCargoSpace", error.Error.UserErrorMessage);
        Assert.Equal(MachoErrors.UserErrorPayload("NotEnoughCargoSpace",
        [
            new(new PyStr("available"), Py.Number(Math.Round(Math.Max(0, capacity - cargoQuantity * unitVolume), 6))),
            new(new PyStr("volume"), Py.Number(Math.Round(80 * unitVolume, 6))),
        ]), error.Error.Payload);
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        Assert.Equal(80, (await items.GetAsync(firstCharge))!.Quantity + (await items.GetAsync(secondCharge))!.Quantity);
        Assert.Equal(cargoQuantity, (await items.GetAsync(cargoStack))!.Quantity);
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task DogmaIM_unload_ammo_in_space_returns_the_cargo_capacity_user_error_without_mutating_items()
    {
        await using var rig = await CharacterHostRig.CreateAsync(simSocket: "tcp:127.0.0.1:1");
        var character = await rig.CreateCharacterAsync();
        const int systemId = 30000142;
        var ship = await GiveItemAsync(rig, character, 587, systemId, CharacterShips.InSpaceFlag, singleton: true);
        await SetShipAsync(rig, character, ship);
        var firstGun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var secondGun = await GiveItemAsync(rig, character, 10678, ship, 29, singleton: true);
        var firstCharge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 40, singleton: false);
        var secondCharge = await PutItemAsync(rig, character, 222, ship, 29, quantity: 40, singleton: false);
        var dogma = await Eve.Dogma.DogmaStatic.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        await dogma.EnsureTypesAsync([587, 222]);
        var capacity = dogma.Type(587)!.Attributes[dogma.AttributeId("capacity")!.Value];
        var unitVolume = dogma.Type(222)!.Attributes[dogma.AttributeId("volume")!.Value];
        var cargoQuantity = checked((long)Math.Floor((capacity - 42 * unitVolume) / unitVolume));
        var cargoStack = await PutItemAsync(rig, character, 222, ship, CharacterShips.CargoFlag, cargoQuantity, singleton: false);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("solarsystemid", new PyLong(systemId));
        session.Set("shipid", new PyLong(ship));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
            new PyLong(ship), new PyList([new PyLong(firstGun), new PyLong(secondGun)]), new PyLong(ship), PyNone.Instance), PyNone.Instance);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service, "MachoBindObject", session,
            Py.Tuple(new PyLong(systemId), new PyInt(3)), nested));

        Assert.Equal("NotEnoughCargoSpace", error.Error.UserErrorMessage);
        Assert.Equal(MachoErrors.UserErrorPayload("NotEnoughCargoSpace",
        [
            new(new PyStr("available"), Py.Number(Math.Round(Math.Max(0, capacity - cargoQuantity * unitVolume), 6))),
            new(new PyStr("volume"), Py.Number(Math.Round(80 * unitVolume, 6))),
        ]), error.Error.Payload);
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        Assert.Equal((ship, 28, 40L), ((await items.GetAsync(firstCharge))!.LocationId, (await items.GetAsync(firstCharge))!.Flag,
            (await items.GetAsync(firstCharge))!.Quantity));
        Assert.Equal((ship, 29, 40L), ((await items.GetAsync(secondCharge))!.LocationId, (await items.GetAsync(secondCharge))!.Flag,
            (await items.GetAsync(secondCharge))!.Quantity));
        Assert.Equal(cargoQuantity, (await items.GetAsync(cargoStack))!.Quantity);
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task DogmaIM_unload_ammo_to_a_personal_container_returns_the_landed_item_and_persists_both_sides()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var charge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 40, singleton: false);
        var container = await GiveItemAsync(rig, character, 17365, rig.Station, ItemFlags.Hangar, singleton: true);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
            new PyLong(ship), new PyList([new PyLong(gun)]),
            Py.Tuple(new PyLong(container), new PyLong(character), new PyInt(CharacterShips.ContainerFlag)), new PyLong(20)), PyNone.Instance);

        var bound = Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", session,
            Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));

        var movedId = InventoryId(Assert.Single(Assert.IsType<PyList>(bound.Items[1]).Items));
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        Assert.Equal((character, container, CharacterShips.ContainerFlag, 20L),
            ((await items.GetAsync(movedId))!.OwnerId, (await items.GetAsync(movedId))!.LocationId,
                (await items.GetAsync(movedId))!.Flag, (await items.GetAsync(movedId))!.Quantity));
        Assert.Equal((ship, 28, 20L), ((await items.GetAsync(charge))!.LocationId, (await items.GetAsync(charge))!.Flag,
            (await items.GetAsync(charge))!.Quantity));
        var notifications = Notifications(sink).Select(notification => notification.Name).ToArray();
        Assert.Contains("OnItemChange", notifications);
        Assert.Contains("OnModuleAttributeChanges", notifications);
        Assert.True(Array.IndexOf(notifications, "OnItemChange") < Array.IndexOf(notifications, "OnModuleAttributeChanges"));
        await rig.RestartAsync();
        var persisted = new PgItemStore(rig.Source, rig.InventorySchema);
        Assert.Equal(20, (await persisted.GetAsync(movedId))!.Quantity);
        Assert.Equal(20, (await persisted.GetAsync(charge))!.Quantity);
    }

    [PgFact]
    public async Task DogmaIM_load_ammo_from_a_corporation_container_requires_take_access_and_changes_item_owner()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        var corporation = (await rig.Characters.GetAsync(character))!.CorporationId;
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var container = await GiveItemAsync(rig, corporation, 17365, rig.Station, ItemFlags.CorpSAG1, singleton: true);
        var stack = await PutItemAsync(rig, corporation, 222, container, CharacterShips.ContainerFlag, quantity: 40, singleton: false);
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        async Task<PyValue> LoadAsync(RecordingPacketSink sink)
        {
            var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
            session.Set("stationid", new PyLong(rig.Station));
            session.Set("shipid", new PyLong(ship));
            session.Set("corpid", new PyLong(corporation));
            var nested = Py.Tuple(new PyStr("LoadAmmo"), Py.Tuple(
                new PyLong(ship), new PyList([new PyLong(gun)]), new PyList([new PyLong(stack)]), new PyLong(container)), PyNone.Instance);
            return Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", session,
                Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested)).Items[1];
        }

        var deniedSink = new RecordingPacketSink();
        var denied = await Assert.ThrowsAsync<MachoWrappedException>(() => LoadAsync(deniedSink));
        Assert.Equal("CrpAccessDenied", denied.Error.UserErrorMessage);
        Assert.Equal(MachoErrors.UserErrorPayload("CrpAccessDenied",
            [new(new PyStr("reason"), Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles")))]),
            denied.Error.Payload);
        Assert.Equal((corporation, container, CharacterShips.ContainerFlag, 40L),
            ((await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack))!.OwnerId,
                (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack))!.LocationId,
                (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack))!.Flag,
                (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack))!.Quantity));
        Assert.Empty(Notifications(deniedSink));

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var roles = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.members SET roles_hq=$2 WHERE character_id=$1", connection))
        {
            roles.Parameters.Add(new NpgsqlParameter { Value = character });
            roles.Parameters.Add(new NpgsqlParameter { Value = (1L << 13) | (1L << 20) });
            Assert.Equal(1, await roles.ExecuteNonQueryAsync());
        }

        var allowedSink = new RecordingPacketSink();
        Assert.IsType<PyNone>(await LoadAsync(allowedSink));
        var loaded = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack);
        Assert.Equal((character, ship, 28, 40L), (loaded!.OwnerId, loaded.LocationId, loaded.Flag, loaded.Quantity));
        Assert.Contains("OnChargeBeingLoadedToModule", Notifications(allowedSink).Select(notification => notification.Name));
        Assert.Contains("OnItemChange", Notifications(allowedSink).Select(notification => notification.Name));
        await rig.RestartAsync();
        var persisted = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack);
        Assert.Equal((character, ship, 28, 40L), (persisted!.OwnerId, persisted.LocationId, persisted.Flag, persisted.Quantity));
    }

    [PgFact]
    public async Task DogmaIM_load_ammo_from_an_out_of_range_container_refuses_without_mutating_or_notifying()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var container = await GiveItemAsync(rig, character, 17365, rig.Station + 1, ItemFlags.Hangar, singleton: true);
        var stack = await PutItemAsync(rig, character, 222, container, CharacterShips.ContainerFlag, quantity: 40, singleton: false);
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var nested = Py.Tuple(new PyStr("LoadAmmo"), Py.Tuple(
            new PyLong(ship), new PyList([new PyLong(gun)]), new PyList([new PyLong(stack)]), new PyLong(container)), PyNone.Instance);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(dogma, "MachoBindObject", session,
            Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));

        Assert.Equal("GenericTooFarAway", error.Error.UserErrorMessage);
        Assert.Equal(MachoErrors.UserErrorPayload("GenericTooFarAway"), error.Error.Payload);
        Assert.Equal((character, container, CharacterShips.ContainerFlag, 40L),
            ((await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack))!.OwnerId,
                (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack))!.LocationId,
                (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack))!.Flag,
                (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(stack))!.Quantity));
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task DogmaIM_concurrent_ammo_unloads_serialize_capacity_checks_for_a_corporation_container()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var firstCharacter = await rig.CreateCharacterAsync();
        var secondCharacter = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(firstCharacter);
        await rig.AddCorporationMemberAsync(secondCharacter);
        var corporation = (await rig.Characters.GetAsync(firstCharacter))!.CorporationId;
        var container = await GiveItemAsync(rig, corporation, 17363, rig.Station, ItemFlags.CorpSAG1, singleton: true);
        var initialStack = await PutItemAsync(rig, corporation, 222, container, CharacterShips.ContainerFlag, 47_940, singleton: false);

        async Task<(long Ship, long Gun, long Charge)> MakeLoadedShipAsync(long character)
        {
            var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
            await SetShipAsync(rig, character, ship);
            var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
            var charge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 40, singleton: false);
            return (ship, gun, charge);
        }

        var first = await MakeLoadedShipAsync(firstCharacter);
        var second = await MakeLoadedShipAsync(secondCharacter);
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        async Task<(PyValue? Result, MachoWrappedException? Error)> UnloadAsync(long character, long ship, long gun)
        {
            var session = new ClientSession { CharacterId = character, UserId = character };
            session.Set("stationid", new PyLong(rig.Station));
            session.Set("shipid", new PyLong(ship));
            session.Set("corpid", new PyLong(corporation));
            var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
                new PyLong(ship), new PyList([new PyLong(gun)]),
                Py.Tuple(new PyLong(container), new PyLong(corporation), new PyInt(CharacterShips.ContainerFlag)), PyNone.Instance), PyNone.Instance);
            try
            {
                var bound = Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", session,
                    Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));
                return (bound.Items[1], null);
            }
            catch (MachoWrappedException error)
            {
                return (null, error);
            }
        }

        var results = await Task.WhenAll(
            UnloadAsync(firstCharacter, first.Ship, first.Gun),
            UnloadAsync(secondCharacter, second.Ship, second.Gun));

        Assert.IsType<PyList>(Assert.Single(results, result => result.Result is PyList).Result);
        Assert.Equal("NoSpaceForThat", Assert.Single(results, result => result.Error is not null).Error!.Error.UserErrorMessage);
        Assert.Equal(47_980, (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(initialStack))!.Quantity);
        var remainingCharges = new[] { first.Charge, second.Charge }
            .Select(async id => await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(id))
            .ToArray();
        var remaining = await Task.WhenAll(remainingCharges);
        Assert.Equal(1, remaining.Count(item => item is not null && item.Quantity == 40));
        Assert.Equal(1, remaining.Count(item => item is null));
    }

    [PgFact]
    public async Task DogmaIM_unload_ammo_to_a_corporation_container_uses_its_owner_and_checks_deposit_access()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        var corporation = (await rig.Characters.GetAsync(character))!.CorporationId;
        var ship = await GiveItemAsync(rig, character, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, ship);
        var gun = await GiveItemAsync(rig, character, 10678, ship, 28, singleton: true);
        var charge = await PutItemAsync(rig, character, 222, ship, 28, quantity: 24, singleton: false);
        var container = await GiveItemAsync(rig, corporation, 17365, rig.Station, ItemFlags.CorpSAG1, singleton: true);
        var session = new ClientSession { CharacterId = character, UserId = 1 };
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("shipid", new PyLong(ship));
        session.Set("corpid", new PyLong(corporation));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));

        async Task<PyValue> UnloadAsync(long actor, long activeShip, long module, long destination, PyValue owner)
        {
            var activeSession = session;
            if (actor != character)
            {
                activeSession = new ClientSession { CharacterId = actor, UserId = 2 };
                activeSession.Set("stationid", new PyLong(rig.Station));
                activeSession.Set("shipid", new PyLong(activeShip));
                activeSession.Set("corpid", new PyLong(corporation));
            }
            var nested = Py.Tuple(new PyStr("UnloadAmmo"), Py.Tuple(
                new PyLong(activeShip), new PyList([new PyLong(module)]),
                Py.Tuple(new PyLong(destination), owner, new PyInt(CharacterShips.ContainerFlag)), PyNone.Instance), PyNone.Instance);
            var bound = Assert.IsType<PyTuple>(await CallAsync(dogma, "MachoBindObject", activeSession,
                Py.Tuple(new PyLong(rig.Station), new PyInt(3)), nested));
            return bound.Items[1];
        }

        var moved = Assert.IsType<PyList>(await UnloadAsync(character, ship, gun, container, new PyLong(corporation)));
        var movedId = InventoryId(Assert.Single(moved.Items));
        var stored = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(movedId);
        Assert.Equal((corporation, container, CharacterShips.ContainerFlag, 24L),
            (stored!.OwnerId, stored.LocationId, stored.Flag, stored.Quantity));
        Assert.Null(await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(charge));

        var stranger = await rig.CreateCharacterAsync();
        var strangerShip = await GiveItemAsync(rig, stranger, 587, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, stranger, strangerShip);
        var strangerGun = await GiveItemAsync(rig, stranger, 10678, strangerShip, 28, singleton: true);
        var strangerCharge = await PutItemAsync(rig, stranger, 222, strangerShip, 28, quantity: 8, singleton: false);
        var denied = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            UnloadAsync(stranger, strangerShip, strangerGun, container, new PyLong(corporation)));
        Assert.Equal("CrpAccessDenied", denied.Error.UserErrorMessage);
        Assert.Equal(8, (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(strangerCharge))!.Quantity);
    }

    [PgFact]
    public async Task CharMgr_global_assets_moniker_resolves_and_lists_through_the_host_wrapper()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, UserId = 1 };
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));

        var node = await CallAsync(charMgr, "MachoResolveObject", session,
            new PyTuple([new PyLong(character), new PyInt(10002)]));
        Assert.Equal(new PyInt(ProxyNode), node);
        var foreignNode = await CallAsync(charMgr, "MachoResolveObject", session,
            new PyTuple([new PyLong(character + 1), new PyInt(10002)]));
        Assert.IsType<PyNone>(foreignNode);

        var binding = Assert.IsType<PyTuple>(await CallAsync(charMgr, "MachoBindObject", session,
            new PyTuple([new PyLong(character), new PyInt(10002)]),
            new PyTuple([new PyStr("ListStations"), new PyTuple([]), PyNone.Instance])));
        Assert.IsType<PySubStruct>(binding.Items[0]);
        Assert.IsType<PyObject>(binding.Items[1]);
    }

    [PgFact]
    public async Task A_multiple_character_training_certificate_is_consumed_for_thirty_days_and_other_items_are_refused()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        rig.Sessions.Enter(character, session);
        var userSvc = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("userSvc"));
        var items = new PgItemStore(rig.Source, rig.InventorySchema);

        long certificate;
        long rifter;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            certificate = await items.CreateAsync(connection, transaction, new NewItem(TypeId: 34133, OwnerId: character, LocationId: rig.Station, Flag: 4));
            rifter = await items.CreateAsync(connection, transaction, new NewItem(TypeId: 587, OwnerId: character, LocationId: rig.Station, Flag: 4));
            await transaction.CommitAsync();
        }

        // an item that is not a certificate is refused, and stays
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(userSvc, "ActivateMultiTraining", session, new PyLong(rifter)));
        Assert.Equal("That item is not a Multiple Character Training certificate.", InfoText(refused));
        Assert.Contains(await items.OwnedByAsync(character), item => item.Id == rifter);

        // the certificate is consumed, a queue runs for 30 days from now, and the client is told
        var slots = Assert.IsType<PyList>(await CallAsync(userSvc, "ActivateMultiTraining", session, new PyLong(certificate)));
        var slot = Assert.IsType<PyTuple>(Assert.Single(slots.Items));
        Assert.Equal(new PyInt(2), slot.Items[0]);
        var endsAt = DateTime.FromFileTimeUtc(((PyLong)slot.Items[1]).Value);
        Assert.InRange(endsAt, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));
        Assert.DoesNotContain(await items.OwnedByAsync(character), item => item.Id == certificate);
        // the account's registered session is told once, after the commit
        Assert.Equal(["OnMultipleCharactersTrainingUpdated"], Notifications(sink).Select(n => n.Name));

        // a restart keeps the queue
        await rig.RestartAsync();
        var restarted = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("userSvc"));
        var after = Assert.IsType<PyList>(await CallAsync(restarted, "GetUpdatedMultiCharacterTraining", session));
        Assert.Equal(slot.Items[1], Assert.IsType<PyTuple>(Assert.Single(after.Items)).Items[1]);
    }

    [PgFact]
    public async Task Deleting_a_character_of_an_npc_corporation_destroys_its_assets_instead_of_moving_them()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = new RecordingPacketSink() };
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        const long npcCorporation = 1000002;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"UPDATE {rig.CharactersSchema}.characters SET corporation_id = $2 WHERE id = $1", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = character });
            command.Parameters.Add(new NpgsqlParameter { Value = npcCorporation });
            await command.ExecuteNonQueryAsync();
        }
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        long shipId;
        long cargoId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            shipId = await items.CreateAsync(connection, transaction, new NewItem(TypeId: 587, OwnerId: character, LocationId: rig.Station, Flag: 4));
            cargoId = await items.CreateAsync(connection, transaction, new NewItem(TypeId: 34, OwnerId: character, LocationId: shipId, Flag: 4, Quantity: 500));
            await transaction.CommitAsync();
        }

        await CallAsync(service, "PrepareCharacterForDelete", session, new PyLong(character));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE {rig.CharactersSchema}.characters SET delete_prepare_at = now() - interval '1 minute' WHERE id = $1", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = character });
            await command.ExecuteNonQueryAsync();
        }
        Assert.IsType<PyNone>(await CallAsync(service, "DeleteCharacter", session, new PyLong(character)));

        // the NPC corporation's assets are destroyed with the character (EveJS destroyCharacterAssets), not delivered to a corporation
        Assert.Null(await items.GetAsync(shipId));
        Assert.Null(await items.GetAsync(cargoId));
        Assert.Empty(await items.OwnedByAsync(character));
        var npcItems = await items.OwnedByAsync(npcCorporation);
        Assert.DoesNotContain(npcItems, item => item.Id == shipId || item.Id == cargoId);
        Assert.Equal(1000001L, (await rig.Characters.GetAsync(character))!.CorporationId);
    }

    [PgFact]
    public async Task A_granted_training_slot_schedules_its_expiry_and_the_scheduler_runs_it_through_the_host()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        rig.Sessions.Enter(character, session);
        var userSvc = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("userSvc"));
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        long certificate;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            certificate = await items.CreateAsync(connection, transaction, new NewItem(TypeId: 34133, OwnerId: character, LocationId: rig.Station, Flag: 4));
            await transaction.CommitAsync();
        }
        var kind = Eve.Services.Skills.MultipleTrainingSlotExpiry.JobKind;
        Assert.Contains(kind, rig.Scheduler.Kinds);

        var slot = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(await CallAsync(userSvc, "ActivateMultiTraining", session, new PyLong(certificate))).Items));
        var endsAt = new DateTimeOffset(DateTime.FromFileTimeUtc(((PyLong)slot.Items[1]).Value), TimeSpan.Zero);
        var job = Assert.Single(await rig.Scheduler.ListAsync(kind));
        Assert.InRange(job.RunAtMs, endsAt.ToUnixTimeMilliseconds() - 1, endsAt.ToUnixTimeMilliseconds() + 1);

        // a restart keeps the job; the scheduler then dispatches it to the registered handler of the restarted host
        await rig.RestartAsync();
        rig.Sessions.Enter(character, session);
        Assert.Single(await rig.Scheduler.ListAsync(kind));
        var before = Notifications(sink).Count(n => n.Name == "OnMultipleCharactersTrainingUpdated");
        // the job is due at the slot's end, days away: the test moves it to now (same key, replaces it). The host's scheduler loop runs
        // it (Foundations starts it), so the test waits, bounded, for the job to be gone; it ran once, so the account hears once.
        await rig.Scheduler.ScheduleAsync(new Eve.Scheduler.JobSpec(kind, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            { Key = job.Key, Payload = new Eve.Services.Skills.MultipleTrainingSlotExpiry.AccountJob(1) });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await rig.Scheduler.FindAsync(kind, job.Key!) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Null(await rig.Scheduler.FindAsync(kind, job.Key!)); // a finished job is removed by the scheduler
        Assert.Equal(before + 1, Notifications(sink).Count(n => n.Name == "OnMultipleCharactersTrainingUpdated"));
    }

    [PgFact]
    public async Task A_late_MCT_expiry_pauses_the_lowest_SP_queue_at_the_slot_boundary()
    {
        const long startUnixMs = 1_800_000_000_000;
        const long expiryUnixMs = startUnixMs + 60_000;
        const long lateUnixMs = expiryUnixMs + 10 * 60_000;
        await using var rig = await CharacterHostRig.CreateAsync(startUnixMs);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var lowerSp = await rig.CreateCharacterAsync();
        var higherSp = await rig.CreateCharacterAsync();
        var lowerSink = new RecordingPacketSink();
        var higherSink = new RecordingPacketSink();
        var lowerSession = new ClientSession { CharacterId = lowerSp, UserId = 1, Sink = lowerSink };
        var higherSession = new ClientSession { CharacterId = higherSp, UserId = 1, Sink = higherSink };
        rig.Sessions.Enter(lowerSp, lowerSession);
        rig.Sessions.Enter(higherSp, higherSession);

        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var lowerSkills = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(lowerSp)).Select(skill => skill.TypeId).ToHashSet();
        var higherSkills = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(higherSp)).Select(skill => skill.TypeId).ToHashSet();
        var candidates = catalog.Skills.Where(skill => skill.Published && skill.Rank == 1 && skill.Needs.Count == 0
                && !lowerSkills.Contains(skill.TypeId) && !higherSkills.Contains(skill.TypeId))
            .OrderBy(skill => skill.TypeId).Take(2).ToArray();
        Assert.Equal(2, candidates.Length);
        var trainingSkill = candidates[0];
        await rig.SetSkillLevelAsync(lowerSp, trainingSkill.TypeId, 0);
        await rig.SetSkillLevelAsync(higherSp, trainingSkill.TypeId, 0);
        await rig.SetSkillLevelAsync(higherSp, candidates[1].TypeId, 5);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var slot = new NpgsqlCommand(
            $"INSERT INTO {rig.IdentitySchema}.account_training_slots(account_id, slot, ends_at) VALUES (1, 2, $1)", connection))
        {
            slot.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.FromUnixTimeMilliseconds(expiryUnixMs) });
            await slot.ExecuteNonQueryAsync();
        }

        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var queue = new PyList([.. Enumerable.Range(1, 5)
            .Select(level => (PyValue)Py.Tuple(new PyInt(trainingSkill.TypeId), new PyInt(level)))]);
        await BoundAsync(skillHandler, lowerSession, "SaveNewQueue", queue);
        await BoundAsync(skillHandler, higherSession, "SaveNewQueue", queue);
        lowerSink.Sent.Clear();
        higherSink.Sent.Clear();

        var jobKind = Eve.Services.Skills.MultipleTrainingSlotExpiry.JobKind;
        var jobKey = $"1:{expiryUnixMs}";
        await rig.Scheduler.ScheduleAsync(new Eve.Scheduler.JobSpec(jobKind, expiryUnixMs)
        {
            Key = jobKey,
            Payload = new Eve.Services.Skills.MultipleTrainingSlotExpiry.AccountJob(
                1, DateTimeOffset.FromUnixTimeMilliseconds(expiryUnixMs).ToFileTime()),
        });

        rig.AdvanceTo(expiryUnixMs);
        var boundaryPoints = Integer(await BoundAsync(skillHandler, lowerSession, "GetSkillPoints"));
        rig.AdvanceTo(lateUnixMs);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await rig.Scheduler.FindAsync(jobKind, jobKey) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Null(await rig.Scheduler.FindAsync(jobKind, jobKey));

        Assert.Equal(boundaryPoints, Integer(await BoundAsync(skillHandler, lowerSession, "GetSkillPoints")));
        Assert.False((await new PgTrainingStore(rig.Source, rig.SkillsSchema).LoadAsync(lowerSp)).Running);
        Assert.True((await new PgTrainingStore(rig.Source, rig.SkillsSchema).LoadAsync(higherSp)).Running);
        var lowerNotices = Notifications(lowerSink);
        Assert.Equal(["OnServerSkillsChanged", "OnMultipleCharactersTrainingUpdated"], lowerNotices.Select(notification => notification.Name));
        Assert.Equal("OnSkillQueuePausedServer", Eve.Macho.JsSemantics.StringValue(lowerNotices[0].Args[1]));
        Assert.Equal(["OnMultipleCharactersTrainingUpdated"], Notifications(higherSink).Select(notification => notification.Name));
    }

    [PgFact]
    public async Task Host_startup_applies_a_missed_MCT_expiry_at_its_saved_boundary()
    {
        const long startUnixMs = 1_800_000_000_000;
        const long expiryUnixMs = startUnixMs + 60_000;
        const long restartUnixMs = expiryUnixMs + 10 * 60_000;
        await using var rig = await CharacterHostRig.CreateAsync(startUnixMs);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var lowerSp = await rig.CreateCharacterAsync();
        var higherSp = await rig.CreateCharacterAsync();
        var lowerSession = new ClientSession { CharacterId = lowerSp, UserId = 1, Sink = new RecordingPacketSink() };
        var higherSession = new ClientSession { CharacterId = higherSp, UserId = 1, Sink = new RecordingPacketSink() };
        rig.Sessions.Enter(lowerSp, lowerSession);
        rig.Sessions.Enter(higherSp, higherSession);

        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var lowerSkills = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(lowerSp)).Select(skill => skill.TypeId).ToHashSet();
        var higherSkills = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(higherSp)).Select(skill => skill.TypeId).ToHashSet();
        var candidates = catalog.Skills.Where(skill => skill.Published && skill.Rank == 1 && skill.Needs.Count == 0
                && !lowerSkills.Contains(skill.TypeId) && !higherSkills.Contains(skill.TypeId))
            .OrderBy(skill => skill.TypeId).Take(2).ToArray();
        Assert.Equal(2, candidates.Length);
        await rig.SetSkillLevelAsync(lowerSp, candidates[0].TypeId, 0);
        await rig.SetSkillLevelAsync(higherSp, candidates[0].TypeId, 0);
        await rig.SetSkillLevelAsync(higherSp, candidates[1].TypeId, 5);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var slot = new NpgsqlCommand(
            $"INSERT INTO {rig.IdentitySchema}.account_training_slots(account_id, slot, ends_at) VALUES (1, 2, $1)", connection))
        {
            slot.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.FromUnixTimeMilliseconds(expiryUnixMs) });
            await slot.ExecuteNonQueryAsync();
        }

        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var queue = new PyList([.. Enumerable.Range(1, 5)
            .Select(level => (PyValue)Py.Tuple(new PyInt(candidates[0].TypeId), new PyInt(level)))]);
        await BoundAsync(skillHandler, lowerSession, "SaveNewQueue", queue);
        await BoundAsync(skillHandler, higherSession, "SaveNewQueue", queue);
        rig.AdvanceTo(expiryUnixMs);
        var boundaryPoints = Integer(await BoundAsync(skillHandler, lowerSession, "GetSkillPoints"));

        rig.AdvanceTo(restartUnixMs);
        await rig.RestartAsync();
        rig.Sessions.Enter(lowerSp, lowerSession);
        rig.Sessions.Enter(higherSp, higherSession);
        skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));

        Assert.Equal(boundaryPoints, Integer(await BoundAsync(skillHandler, lowerSession, "GetSkillPoints")));
        Assert.False((await new PgTrainingStore(rig.Source, rig.SkillsSchema).LoadAsync(lowerSp)).Running);
        Assert.True((await new PgTrainingStore(rig.Source, rig.SkillsSchema).LoadAsync(higherSp)).Running);
        Assert.Equal(5, Assert.IsType<PyList>(await BoundAsync(skillHandler, lowerSession, "GetSkillQueue")).Items.Count);
    }

    [PgFact]
    public async Task A_stale_MCT_expiry_job_does_not_pause_queues_after_the_slot_was_extended()
    {
        const long nowUnixMs = 1_800_000_000_000;
        const long oldExpiryUnixMs = nowUnixMs - 60_000;
        const long currentExpiryUnixMs = nowUnixMs + 24 * 60 * 60_000;
        await using var rig = await CharacterHostRig.CreateAsync(nowUnixMs);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var firstCharacter = await rig.CreateCharacterAsync();
        var secondCharacter = await rig.CreateCharacterAsync();
        var firstSink = new RecordingPacketSink();
        var secondSink = new RecordingPacketSink();
        var firstSession = new ClientSession { CharacterId = firstCharacter, UserId = 1, Sink = firstSink };
        var secondSession = new ClientSession { CharacterId = secondCharacter, UserId = 1, Sink = secondSink };
        rig.Sessions.Enter(firstCharacter, firstSession);
        rig.Sessions.Enter(secondCharacter, secondSession);

        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var firstSkills = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(firstCharacter)).Select(skill => skill.TypeId).ToHashSet();
        var secondSkills = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(secondCharacter)).Select(skill => skill.TypeId).ToHashSet();
        var typeId = catalog.Skills.First(skill => skill.Published && skill.Rank == 1 && skill.Needs.Count == 0
            && !firstSkills.Contains(skill.TypeId) && !secondSkills.Contains(skill.TypeId)).TypeId;
        await rig.SetSkillLevelAsync(firstCharacter, typeId, 0);
        await rig.SetSkillLevelAsync(secondCharacter, typeId, 0);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var slot = new NpgsqlCommand(
            $"INSERT INTO {rig.IdentitySchema}.account_training_slots(account_id, slot, ends_at) VALUES (1, 2, $1)", connection))
        {
            slot.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.FromUnixTimeMilliseconds(currentExpiryUnixMs) });
            await slot.ExecuteNonQueryAsync();
        }

        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var queue = new PyList([.. Enumerable.Range(1, 5)
            .Select(level => (PyValue)Py.Tuple(new PyInt(typeId), new PyInt(level)))]);
        await BoundAsync(skillHandler, firstSession, "SaveNewQueue", queue);
        await BoundAsync(skillHandler, secondSession, "SaveNewQueue", queue);
        firstSink.Sent.Clear();
        secondSink.Sent.Clear();

        var kind = Eve.Services.Skills.MultipleTrainingSlotExpiry.JobKind;
        var key = $"1:{oldExpiryUnixMs}";
        await rig.Scheduler.ScheduleAsync(new Eve.Scheduler.JobSpec(kind, currentExpiryUnixMs)
        {
            Key = $"1:{currentExpiryUnixMs}",
            Payload = new Eve.Services.Skills.MultipleTrainingSlotExpiry.AccountJob(
                1, DateTimeOffset.FromUnixTimeMilliseconds(currentExpiryUnixMs).ToFileTime()),
        });
        await rig.Scheduler.ScheduleAsync(new Eve.Scheduler.JobSpec(kind, nowUnixMs)
        {
            Key = key,
            Payload = new Eve.Services.Skills.MultipleTrainingSlotExpiry.AccountJob(
                1, DateTimeOffset.FromUnixTimeMilliseconds(oldExpiryUnixMs).ToFileTime()),
        });

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await rig.Scheduler.FindAsync(kind, key) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Null(await rig.Scheduler.FindAsync(kind, key));
        Assert.True((await new PgTrainingStore(rig.Source, rig.SkillsSchema).LoadAsync(firstCharacter)).Running);
        Assert.True((await new PgTrainingStore(rig.Source, rig.SkillsSchema).LoadAsync(secondCharacter)).Running);
        Assert.Empty(Notifications(firstSink));
        Assert.Empty(Notifications(secondSink));
        Assert.NotNull(await rig.Scheduler.FindAsync(kind, $"1:{currentExpiryUnixMs}"));
    }

    [PgFact]
    public async Task Saving_a_skill_queue_does_not_invalidate_the_account_MCT_cache()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        rig.Sessions.Enter(character, session);
        var catalog = await SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var owned = (await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(character)).Select(skill => skill.TypeId).ToHashSet();
        var type = catalog.Skills.First(skill => skill.Published && skill.Rank == 1 && skill.Needs.Count == 0 && !owned.Contains(skill.TypeId));
        await rig.SetSkillLevelAsync(character, type.TypeId, 0);
        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));

        await BoundAsync(skillHandler, session, "SaveNewQueue",
            new PyList([Py.Tuple(new PyInt(type.TypeId), new PyInt(1))]));

        Assert.Equal(["OnNewSkillQueueSaved"], Notifications(sink).Select(notification => notification.Name));
    }

    [PgFact]
    public async Task Host_startup_recreates_expiry_jobs_for_existing_training_slots()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var endsAt = DateTimeOffset.UtcNow.AddDays(30);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"INSERT INTO {rig.IdentitySchema}.account_training_slots(account_id, slot, ends_at) VALUES (1, 2, $1)", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = endsAt });
            await command.ExecuteNonQueryAsync();
        }

        await rig.RestartAsync();

        var key = $"1:{endsAt.ToUnixTimeMilliseconds()}";
        var job = await rig.Scheduler.FindAsync(Eve.Services.Skills.MultipleTrainingSlotExpiry.JobKind, key);
        Assert.NotNull(job);
        Assert.Equal(endsAt.ToUnixTimeMilliseconds(), job.RunAtMs);
    }

    [PgFact]
    public async Task A_stack_of_multiple_character_training_certificates_gives_up_one_certificate_per_activation()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = sink };
        rig.Sessions.Enter(character, session);
        var userSvc = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("userSvc"));
        var items = new PgItemStore(rig.Source, rig.InventorySchema);

        long stack;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            stack = await items.CreateAsync(connection, transaction, new NewItem(TypeId: 34133, OwnerId: character, LocationId: rig.Station, Flag: 4, Quantity: 2));
            await transaction.CommitAsync();
        }

        Assert.Single(Assert.IsType<PyList>(await CallAsync(userSvc, "ActivateMultiTraining", session, new PyLong(stack))).Items);
        Assert.Equal(1, (await items.GetAsync(stack))!.Quantity);
        Assert.Equal(["OnMultipleCharactersTrainingUpdated"], Notifications(sink).Select(n => n.Name));

        Assert.Equal(2, Assert.IsType<PyList>(await CallAsync(userSvc, "ActivateMultiTraining", session, new PyLong(stack))).Items.Count);
        Assert.Null(await items.GetAsync(stack));
        Assert.Equal(2, Notifications(sink).Count);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(userSvc, "ActivateMultiTraining", session, new PyLong(stack)));
        Assert.Equal("That item does not exist.", InfoText(refused));
    }

    [PgFact]
    public async Task Jumping_to_a_ship_clone_requests_the_clients_confirmation_before_settlement()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        var capsule = await GiveItemAsync(rig, character, CharacterShips.CapsuleTypeId, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, capsule);
        await FundAsync(rig, character, 1_000_000m);
        const long targetShip = 1_000_000_000_091;
        var cloneId = await rig.AddCloneAsync(character, targetShip, "ship", "Vat clone", []);
        var clones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));

        var confirmation = await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(clones, session, "CloneJump",
            new PyLong(targetShip), new PyLong(cloneId), new PyReal(900_000), PyBool.False));

        Assert.Equal("JumpCheckIntoShip", confirmation.Error.UserErrorMessage);
        Assert.Equal(new PyLong(targetShip), NamedArgument(confirmation, "shipID"));
        Assert.Equal(new PyLong(rig.Station), session.Get("stationid"));
        Assert.Single(Rows(Field(await BoundAsync(clones, session, "GetCloneState"), "clones")));
        Assert.Equal(1_000_000m, await BalanceAsync(rig, character));
        Assert.Empty(Notifications(sink));
    }

    [PgFact]
    public async Task Confirmed_ship_clone_jump_is_refused_without_mutations_when_the_destination_ship_is_not_in_space()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { SessionId = 10_003, CharacterId = character, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        var capsule = await GiveItemAsync(rig, character, CharacterShips.CapsuleTypeId, rig.Station, ItemFlags.Hangar, singleton: true);
        await SetShipAsync(rig, character, capsule);
        session.Set("shipid", new PyLong(capsule));
        await FundAsync(rig, character, 1_000_000m);
        var ship = await GiveItemAsync(rig, character, 587, 30000142, CharacterShips.InSpaceFlag, singleton: true);
        var cloneId = await rig.AddCloneAsync(character, ship, "ship", "Unavailable", []);
        var clones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));

        var confirmation = await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(clones, session, "CloneJump",
            new PyLong(ship), new PyLong(cloneId), new PyReal(900_000), PyBool.False));
        Assert.Equal("JumpCheckIntoShip", confirmation.Error.UserErrorMessage);

        var unavailable = await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(clones, session, "CloneJump",
            new PyLong(ship), new PyLong(cloneId), new PyReal(900_000), PyBool.True));
        Assert.Equal("Jump clone not found", Assert.IsType<PyStr>(NamedArgument(unavailable, "notify")).Value);
        Assert.Equal(new PyLong(rig.Station), session.Get("stationid"));
        Assert.Equal(new PyLong(capsule), session.Get("shipid"));
        Assert.Equal(1_000_000m, await BalanceAsync(rig, character));
        Assert.Equal((rig.Station, ItemFlags.Hangar), (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(capsule)) is { } item
            ? (item.LocationId, item.Flag)
            : throw new Xunit.Sdk.XunitException("The docked capsule disappeared."));
        Assert.Empty(await SettlementsAsync(rig, character));
        Assert.Single(Rows(Field(await BoundAsync(clones, session, "GetCloneState"), "clones")));
        Assert.Empty(Notifications(sink));
    }

    [RealAbyssalSimPgFact]
    public async Task Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        try
        {
            string? endpoint = null;
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal))
                {
                    endpoint = line["eve-sim: listening on ".Length..];
                    break;
                }
            Assert.NotNull(endpoint);
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            await using var rig = await CharacterHostRig.CreateAsync(simSocket: $"tcp:{endpoint}");
            await rig.WaitForSimulationConnectionAsync();
            var targetStation = (await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source)))
                .InSystem(30000143).OrderBy(station => station.Id).First();
            var targetCharacter = await rig.CreateCharacterAsync();
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await rig.Characters.SetStationAsync(connection, transaction, targetCharacter, targetStation.Id);
                await rig.Characters.SetLocationAsync(connection, transaction, targetCharacter, targetStation.SolarSystemId,
                    targetStation.ConstellationId, targetStation.RegionId);
                await transaction.CommitAsync();
            }
            var targetShip = await GiveItemAsync(rig, targetCharacter, 587, targetStation.Id, ItemFlags.Hangar, singleton: true);
            await SetShipAsync(rig, targetCharacter, targetShip);
            var targetSink = new RecordingPacketSink();
            var targetSession = new ClientSession { SessionId = 10_001, CharacterId = targetCharacter, UserId = 1, Sink = targetSink };
            targetSession.Set("stationid", new PyLong(targetStation.Id));
            rig.Sessions.Enter(targetCharacter, targetSession);
            var shipService = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("ship"));
            await BoundAtAsync(shipService, targetSession, targetStation.Id, "Undock", new PyLong(targetShip), PyBool.True);
            Assert.NotNull(targetSession.GetInt("solarsystemid"));

            var character = await rig.CreateCharacterAsync();
            var sink = new RecordingPacketSink();
            var session = new ClientSession { SessionId = 10_002, CharacterId = character, UserId = 1, Sink = sink };
            session.Set("stationid", new PyLong(rig.Station));
            rig.Sessions.Enter(character, session);
            var capsule = await GiveItemAsync(rig, character, CharacterShips.CapsuleTypeId, rig.Station, ItemFlags.Hangar, singleton: true);
            await SetShipAsync(rig, character, capsule);
            session.Set("shipid", new PyLong(capsule));
            await FundAsync(rig, character, 1_000_000m);
            await GiveItemAsync(rig, character, 9942, character, 89, singleton: true);
            var cloneId = await rig.AddCloneAsync(character, targetShip, "ship", "Vat clone", [9941]);
            var clones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));

            var confirmation = await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(clones, session, "CloneJump",
                new PyLong(targetShip), new PyLong(cloneId), new PyReal(900_000), PyBool.False));
            Assert.Equal("JumpCheckIntoShip", confirmation.Error.UserErrorMessage);
            Assert.Equal(1_000_000m, await BalanceAsync(rig, character));
            Assert.Equal(new PyLong(rig.Station), session.Get("stationid"));

            PyValue jumpResult;
            try
            {
                jumpResult = await BoundAsync(clones, session, "CloneJump",
                    new PyLong(targetShip), new PyLong(cloneId), new PyReal(900_000), PyBool.True);
            }
            catch (MachoWrappedException error)
            {
                var reason = error.Error.UserErrorMessage == "CustomNotify"
                    ? Assert.IsType<PyStr>(NamedArgument(error, "notify")).Value
                    : error.Error.UserErrorMessage;
                Assert.Fail($"Confirmed ship clone jump was rejected: {reason}");
                throw;
            }
            Assert.IsType<PyNone>(jumpResult);

            Assert.Null(session.GetInt("stationid"));
            Assert.Equal(targetStation.SolarSystemId, session.GetInt("solarsystemid2"));
            Assert.Equal(capsule, (long?)session.GetInt("shipid"));
            var capsuleState = await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(capsule);
            Assert.NotNull(capsuleState);
            Assert.Equal(targetStation.SolarSystemId, capsuleState.LocationId);
            Assert.Equal(CharacterShips.InSpaceFlag, capsuleState.Flag);
            Assert.Equal(targetStation.SolarSystemId, (await rig.Characters.GetAsync(character))!.SolarSystemId);
            Assert.Null((await rig.Characters.GetAsync(character))!.StructureId);
            Assert.Equal([9941], await BodyImplantTypesAsync(rig, character));
            Assert.Equal(100_000m, await BalanceAsync(rig, character));
            var persisted = await new PgJumpCloneStore(rig.Source, rig.CharactersSchema).ReadAsync(character);
            Assert.NotEqual(0, persisted.TimeLastJumpFileTime);
            var atOrigin = Assert.Single(persisted.Clones);
            Assert.Equal((rig.Station, "station"), (atOrigin.LocationId, atOrigin.LocationKind));
            Assert.Equal([9942], atOrigin.ImplantTypeIds);
            Assert.Equal(
                ["OnItemsChanged", "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
                    "OnJumpCloneCacheInvalidated", "OnStationJumpCloneCacheInvalidated", "OnShipJumpCloneCacheInvalidated",
                    "OnServerImplantsChanged", "OnModuleAttributeChanges", "OnModuleAttributeChanges", "OnServerBrainUpdated",
                    "OnJumpCloneTransitionCompleted"],
                Notifications(sink).Select(notification => notification.Name));

            await rig.RestartAsync();
            rig.Sessions.Enter(character, session);
            Assert.Equal([rig.Station], Rows(Field(await BoundAsync(Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc")), session,
                "GetCloneState"), "clones")).Select(row => (long)((PyLong)row.Items[1]).Value));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [PgFact]
    public async Task Concurrent_station_clone_installs_respect_the_clone_limit_and_settle_one_fee()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, Sink = new RecordingPacketSink() };
        session.Set("stationid", new PyLong(rig.Station));
        await rig.SetSkillLevelAsync(character, 24242, 1);
        await FundAsync(rig, character, 7_200_000m);
        var clones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await BoundAsync(clones, session, "InstallCloneInStation");
                return (Succeeded: true, Error: (MachoWrappedException?)null);
            }
            catch (MachoWrappedException error)
            {
                return (Succeeded: false, Error: error);
            }
        }));

        Assert.Single(attempts, attempt => attempt.Succeeded);
        Assert.All(attempts, attempt =>
        {
            if (!attempt.Succeeded)
                Assert.Equal("CustomNotify", attempt.Error!.Error.UserErrorMessage);
        });
        Assert.Single(Rows(Field(await BoundAsync(clones, session, "GetCloneState"), "clones")));
        Assert.Single(await SettlementsAsync(rig, character));
        Assert.Equal(6_300_000m, await BalanceAsync(rig, character));
    }

    [PgFact]
    public async Task Different_characters_can_install_clones_in_the_same_station()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var characters = new[] { await rig.CreateCharacterAsync(), await rig.CreateCharacterAsync() };
        var sessions = characters.Select(character =>
        {
            var session = new ClientSession { CharacterId = character, Sink = new RecordingPacketSink() };
            session.Set("stationid", new PyLong(rig.Station));
            return session;
        }).ToArray();
        foreach (var character in characters)
        {
            await rig.SetSkillLevelAsync(character, 24242, 1);
            await FundAsync(rig, character, 1_000_000m);
        }
        var clones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));

        foreach (var session in sessions)
            await BoundAsync(clones, session, "InstallCloneInStation");
        foreach (var (character, session) in characters.Zip(sessions))
        {
            Assert.Single(Rows(Field(await BoundAsync(clones, session, "GetCloneState"), "clones")));
            Assert.Single(await SettlementsAsync(rig, character));
            Assert.Equal(100_000m, await BalanceAsync(rig, character));
        }
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {rig.CharactersSchema}.character_jump_clones WHERE location_id = $1 AND location_kind = 'station'", connection);
        command.Parameters.AddWithValue(rig.Station);
        Assert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [PgFact]
    public async Task Concurrent_ship_clone_installs_from_different_characters_respect_the_shared_bay_capacity()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var carrierOwner = await rig.CreateCharacterAsync();
        var carrier = await GiveItemAsync(rig, carrierOwner, 28352, rig.Station, ItemFlags.Hangar, singleton: true);
        var statics = await Eve.Dogma.DogmaStatic.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        await statics.EnsureTypesAsync([28352]);
        var capacity = (int)statics.Type(28352)!.Attributes[979];
        Assert.True(capacity > 1);

        var cloneRows = new PgJumpCloneStore(rig.Source, rig.CharactersSchema);
        for (var index = 0; index < capacity - 1; index++)
        {
            var existingOwner = await rig.CreateCharacterAsync();
            await rig.SetSkillLevelAsync(existingOwner, 24242, 1);
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await cloneRows.InsertAsync(connection, transaction, existingOwner, carrier, "ship");
            await transaction.CommitAsync();
        }

        var candidates = new[] { await rig.CreateCharacterAsync(), await rig.CreateCharacterAsync() };
        foreach (var character in candidates)
        {
            await rig.SetSkillLevelAsync(character, 24242, 1);
            await FundAsync(rig, character, 1_000_000m);
        }

        // This is the Host's PostgreSQL store, with unused authorities null because this scenario exercises InstallAsync only.
        var hostStore = new HostJumpCloneStore(cloneRows, rig.Source, new PgSkillStore(rig.Source, rig.SkillsSchema),
            new WalletTransactions(rig.Source, rig.WalletSchema), new PgWalletStore(rig.Source, rig.WalletSchema), rig.Characters,
            new PgItemStore(rig.Source, rig.InventorySchema), null!, null!, null!, null!, null!, null!);
        var attempts = await Task.WhenAll(candidates.Select(async character =>
        {
            try
            {
                await hostStore.InstallAsync(character,
                    new JumpCloneInstall(carrier, "ship", 900_000m, OfferingCharacterId: carrierOwner, ShipCapacity: capacity),
                    CancellationToken.None);
                return (Character: character, Refusal: (string?)null);
            }
            catch (JumpCloneInstallRefusedException refused)
            {
                return (Character: character, Refusal: refused.Reason);
            }
        }));

        Assert.Single(attempts, attempt => attempt.Refusal is null);
        Assert.Equal("SHIP_CAPACITY", Assert.Single(attempts, attempt => attempt.Refusal is not null).Refusal);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {rig.CharactersSchema}.character_jump_clones WHERE location_id = $1 AND location_kind = 'ship'", connection))
        {
            command.Parameters.AddWithValue(carrier);
            Assert.Equal(capacity, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        Assert.Equal(1, (await SettlementsAsync(rig, candidates[0])).Count + (await SettlementsAsync(rig, candidates[1])).Count);
        Assert.Equal([100_000m, 1_000_000m], (await Task.WhenAll(candidates.Select(character => BalanceAsync(rig, character))))
            .Order().ToArray());
    }

    [PgFact]
    public async Task Installing_a_clone_in_a_station_charges_the_fee_once_and_is_refused_without_the_money()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = new RecordingPacketSink() };
        session.Set("stationid", new PyInt(rig.Station));
        await rig.SetSkillLevelAsync(character, 24242, 1);
        await rig.SetSkillLevelAsync(character, 33407, 1);
        var clones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));

        // without the money: refused with the client's NotEnoughMoney, and nothing is installed
        var broke = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(clones, "InstallCloneInStation", session));
        Assert.Equal("NotEnoughMoney", broke.Error.UserErrorMessage);
        Assert.Equal(new PyReal(900000d), NamedArgument(broke, "amount"));
        Assert.Empty(Rows(Field(await BoundAsync(clones, session, "GetCloneState"), "clones")));

        // with the money: the fee is charged once and the clone is installed where the character is docked
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var fund = new NpgsqlCommand(
            $"UPDATE {rig.WalletSchema}.balances SET isk = 1000000 WHERE owner_id = $1 AND account_key = 1000", connection))
        {
            fund.Parameters.Add(new NpgsqlParameter { Value = character });
            Assert.Equal(1, await fund.ExecuteNonQueryAsync());
        }
        Assert.IsType<PyNone>(await CallAsync(clones, "InstallCloneInStation", session));
        Assert.Equal(new[] { rig.Station }, Rows(await BoundAsync(clones, session, "GetStationCloneState"))
            .Select(row => ((PyLong)row.Items[1]).Value).ToArray());
        Assert.Equal(100000m, await BalanceAsync(rig, character));

        // the install survives a restart
        await rig.RestartAsync();
        var restarted = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        Assert.Single(Rows(Field(await BoundAsync(restarted, session, "GetCloneState"), "clones")));
        // the settlement is kept with the clone: the fee, the place, and no owner (a station has none)
        Assert.Equal(new[] { ("station", 900000m, 0L, 0L) }, (await SettlementsAsync(rig, character)).ToArray());
    }

    [PgFact]
    public async Task Installing_a_clone_at_a_structure_pays_its_owner_corporation_and_needs_its_clone_service()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        Assert.Equal(1, await rig.CreateAccountAsync());
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = new RecordingPacketSink() };
        const long structure = 1_000_000_000_071;
        session.Set("structureid", new PyLong(structure));
        await rig.SetSkillLevelAsync(character, 24242, 1);
        await rig.SetSkillLevelAsync(character, 33407, 1);
        await rig.FundAsync(character, 1_000_000m);
        var clones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));

        // a structure that is not there (no clone service) refuses the install with the validation label, and installs nothing
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(clones, "InstallCloneInStructure", session));
        Assert.Equal("UI/Medical/Errors/UnknownValidationError", NotifyText(refused));
        Assert.Empty(Rows(Field(await BoundAsync(clones, session, "GetCloneState"), "clones")));

        // its Medical service online: the install goes ahead, the fee moves to the owner corporation's wallet, and the clone survives a restart
        await rig.AddStructureAsync(structure, Eve.Structures.StructureServiceIds.Medical);
        clones = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        Assert.Empty(Assert.IsType<PyList>(await BoundAsync(clones, session, "ValidateInstallJumpClone")).Items);
        Assert.IsType<PyNone>(await CallAsync(clones, "InstallCloneInStructure", session));
        Assert.Equal(new[] { structure }, Rows(await BoundAsync(clones, session, "GetStationCloneState")).Select(row => ((PyLong)row.Items[1]).Value).ToArray());
        Assert.Equal(100000m, await BalanceAsync(rig, character));
        Assert.Equal(900000m, await BalanceAsync(rig, CharacterHostRigCorporation));

        await rig.RestartAsync();
        var restarted = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("jumpCloneSvc"));
        Assert.Equal(new[] { structure }, Rows(Field(await BoundAsync(restarted, session, "GetCloneState"), "clones")).Select(row => ((PyLong)row.Items[1]).Value).ToArray());
        Assert.Equal(new[] { ("structure", 900000m, CharacterHostRigCorporation, 0L) }, (await SettlementsAsync(rig, character)).ToArray());
    }

    /// <summary>The corporation the rig's structures belong to (it is paid the structure clone bay's fee).</summary>
    private const long CharacterHostRigCorporation = 900_001_001;

    /// <summary>The settlements of a character: (location kind, amount, owner corporation, offering character).</summary>
    private static async Task<List<(string Kind, decimal Amount, long OwnerCorporation, long Offering)>> SettlementsAsync(CharacterHostRig rig, long characterId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT location_kind, amount, owner_corporation_id, offering_character_id FROM {rig.CharactersSchema}.character_jump_clone_settlements WHERE character_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        var rows = new List<(string Kind, decimal Amount, long OwnerCorporation, long Offering)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetDecimal(1), reader.GetInt64(2), reader.GetInt64(3)));
        return rows;
    }

    /// <summary>The text of a CustomNotify refusal (its <c>notify</c> argument), as the client's UserError reads it.</summary>
    private static string NotifyText(MachoWrappedException error)
    {
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var args = Assert.IsType<PyDict>(Assert.IsType<PyTuple>(header.Items[1]).Items[1]);
        return Assert.IsType<PyStr>(args.Entries.Single(entry => ((PyStr)entry.Key).Value == "notify").Value).Value;
    }

    private static async Task<decimal> BalanceAsync(CharacterHostRig rig, long characterId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT isk FROM {rig.WalletSchema}.balances WHERE owner_id = $1 AND account_key = 1000", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> DeleteCharacterNowAsync(CharacterHostRig rig, long characterId)
    {
        var session = new ClientSession { CharacterId = characterId, UserId = 1, Sink = new RecordingPacketSink() };
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        await CallAsync(service, "PrepareCharacterForDelete", session, new PyLong(characterId));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE {rig.CharactersSchema}.characters SET delete_prepare_at = now() - interval '1 minute' WHERE id = $1", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await command.ExecuteNonQueryAsync();
        }
        await CallAsync(service, "DeleteCharacter", session, new PyLong(characterId));
        return characterId;
    }

    private static async Task<JsonObject> ReadGameStoreTableAsync(CharacterHostRig rig, string table)
    {
        await using var store = new PersistenceStore(rig.Source, rig.GameStoreSchema);
        await store.EnsureSchemaAsync();
        return await store.LoadTableAsync(table, createIfMissing: false);
    }

    private static async Task<(JsonObject Message, JsonObject Notification)> WaitForBiomassMailAsync(
        CharacterHostRig rig, long characterId, long recipientId)
    {
        var operationKey = $"character-biomass:{characterId}";
        var notificationKey = $"{operationKey}:recipient:{recipientId}";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var mailState = await ReadGameStoreTableAsync(rig, "mail");
            var messages = mailState["messages"] as JsonObject;
            var message = messages?.Select(pair => pair.Value).OfType<JsonObject>().SingleOrDefault(candidate =>
                string.Equals(candidate["biomassOperationKey"]?.ToString(), operationKey, StringComparison.Ordinal));
            var notificationState = await ReadGameStoreTableAsync(rig, "notifications");
            var records = ((notificationState["boxes"] as JsonObject)?[recipientId.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                as JsonObject)?["byID"] as JsonObject;
            var notification = records?.Select(pair => pair.Value).OfType<JsonObject>().SingleOrDefault(candidate =>
                string.Equals(candidate["idempotencyKey"]?.ToString(), notificationKey, StringComparison.Ordinal));
            if (message is not null && notification is not null)
                return (message, notification);
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"Character biomass mail {operationKey} did not reach the persisted mailboxes.");
    }

    /// <summary>A named argument of a user error (the dictionary the client's UserError reads).</summary>
    private static PyValue NamedArgument(MachoWrappedException error, string name)
    {
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var args = Assert.IsType<PyDict>(Assert.IsType<PyTuple>(header.Items[1]).Items[1]);
        return args.Entries.Single(entry => ((PyStr)entry.Key).Value == name).Value;
    }

    private static async Task<PyValue> CallAsync(IService service, string method, ClientSession session, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    /// <summary>A bound call the way the client makes it: <c>MachoBindObject((location, group), (method, args, kwargs))</c>.</summary>
    private static async Task<PyValue> BoundAsync(IService service, ClientSession session, string method, params PyValue[] args)
    {
        var nested = Py.Tuple(new PyStr(method), Py.Tuple(args), PyNone.Instance);
        var bound = Assert.IsType<PyTuple>(await CallAsync(service, "MachoBindObject", session,
            Py.Tuple(new PyInt(0), new PyInt(3)), nested));
        return bound.Items[1];
    }

    private static async Task<PyValue> BoundAtAsync(IService service, ClientSession session, long locationId, string method, params PyValue[] args)
    {
        var nested = Py.Tuple(new PyStr(method), Py.Tuple(args), PyNone.Instance);
        var bound = Assert.IsType<PyTuple>(await CallAsync(service, "MachoBindObject", session,
            Py.Tuple(new PyLong(locationId), new PyInt(3)), nested));
        return bound.Items[1];
    }

    private static PyValue Field(PyValue keyVal, string key)
    {
        var entries = keyVal switch
        {
            PyObject obj => Assert.IsType<PyDict>(obj.Args).Entries,
            PyDict dict => dict.Entries,
            _ => throw new Xunit.Sdk.XunitException($"Expected a dictionary, got {keyVal.GetType().Name}"),
        };
        return entries.Single(entry => entry.Key switch
        {
            PyStr text => text.Value == key,
            PyBytes bytes => bytes.Value.Span.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(key)),
            _ => false,
        }).Value;
    }

    private static PyValue NotificationValue(PyDict data, string name) =>
        data.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;

    private static List<PyList> Rows(PyValue rowset) => Assert.IsType<PyList>(Field(rowset, "lines")).Items
        .Select(line => Assert.IsType<PyList>(line)).ToList();

    private static List<(string Name, PyValue[] Args)> Notifications(RecordingPacketSink sink)
    {
        var events = new List<(string Name, PyValue[] Args)>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
                continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var args = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items;
            events.Add((((PyStr)address[1]).Value, [.. args]));
        }
        return events;
    }

    private sealed class RecordingPacketSink : IPacketSink
    {
        private readonly Action? _onSend;
        private readonly Action<PyValue>? _observePacket;

        public RecordingPacketSink(Action? onSend = null) => _onSend = onSend;
        public RecordingPacketSink(Action<PyValue> observePacket) => _observePacket = observePacket;

        public List<PyValue> Sent { get; } = [];

        public void Send(PyValue packet)
        {
            Sent.Add(packet);
            _onSend?.Invoke();
            _observePacket?.Invoke(packet);
        }
    }

    internal sealed class CharacterHostRig : IAsyncDisposable
    {
        private static readonly string[] SchemaVariables =
        [
            "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA",
            "EVE_WALLET_SCHEMA", "EVE_FITTING_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA",
            "EVE_ASSET_SAFETY_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_CORPORATIONS_SCHEMA", "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA",
            "EVE_GAMESTORE_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA", "EVE_CONTENT_SCHEMA",
            "EVE_STRUCTURES_SCHEMA", "EVE_AGENTS_SCHEMA", "EVE_FACTIONWARFARE_SCHEMA",
            "EVE_SKINR_SCHEMA", "EVE_ESS_SCHEMA",
        ];

        private const long CorporationId = 900_001_001;
        private readonly IReadOnlyDictionary<string, string> _schemas;
        private readonly ManualClock? _clock;
        private readonly string? _simSocket;
        private readonly ClusterOptions? _clusterOptions;
        private HostRuntime _runtime;
        private Func<Task> _shutdown;

        private CharacterHostRig(NpgsqlDataSource source, IReadOnlyDictionary<string, string> schemas, HostRuntime runtime, Func<Task> shutdown,
            long station, ManualClock? clock, string? simSocket, ClusterOptions? clusterOptions)
        {
            Source = source;
            _schemas = schemas;
            _runtime = runtime;
            _shutdown = shutdown;
            Station = station;
            _clock = clock;
            _simSocket = simSocket;
            _clusterOptions = clusterOptions;
        }

        public NpgsqlDataSource Source { get; }
        public ServiceRegistry Registry => _runtime.Registry;
        public HostServer Server => _runtime.Server;
        internal Eve.Host.PublicGateway.PublicGatewayRoutes Gateway => _runtime.PublicGateway;
        internal Eve.Events.EventBus Events => _runtime.Events ?? throw new InvalidOperationException("the event bus is not composed");
        public CharacterSessions Sessions => Assert.IsType<CharacterSessions>(_runtime.CharacterSessions);
        public Eve.Space.SimClient Simulation => _runtime.Simulation ?? throw new InvalidOperationException("the simulation is not composed");
        public async Task WaitForSimulationConnectionAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (_runtime.Simulation?.IsConnected != true)
                await Task.Delay(20, timeout.Token);
        }
        public OnlineStatusNotifications OnlineStatusNotifications => Assert.IsType<OnlineStatusNotifications>(_runtime.OnlineStatusNotifications);
        public Eve.Scheduler.DurableScheduler Scheduler => _runtime.Scheduler ?? throw new InvalidOperationException("the scheduler is not composed");
        public PgCharacterStore Characters => new(Source, CharactersSchema);
        public long Station { get; }
        public string CharactersSchema => _schemas["EVE_CHARACTERS_SCHEMA"];
        public string SkillsSchema => _schemas["EVE_SKILLS_SCHEMA"];
        public string InventorySchema => _schemas["EVE_INVENTORY_SCHEMA"];
        public string IdentitySchema => _schemas["EVE_IDENTITY_SCHEMA"];
        public string WalletSchema => _schemas["EVE_WALLET_SCHEMA"];
        public string CorporationsSchema => _schemas["EVE_CORPORATIONS_SCHEMA"];
        public string FactionWarfareSchema => _schemas["EVE_FACTIONWARFARE_SCHEMA"];
        public string GameStoreSchema => _schemas["EVE_GAMESTORE_SCHEMA"];
        public string SchedulerSchema => _schemas["EVE_SCHEDULER_SCHEMA"];
        public string StructuresSchema => _schemas["EVE_STRUCTURES_SCHEMA"];
        public string SkinrSchema => _schemas["EVE_SKINR_SCHEMA"];
        public long NowUnixMs => _clock?.Read() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public void AdvanceTo(long unixMs) => (_clock ?? throw new InvalidOperationException("This rig uses the system clock.")).Set(unixMs);

        /// <summary>A structure of the rig's corporation, with the given services online (the clone bay reads them).</summary>
        public async Task AddStructureAsync(long structureId, params int[] onlineServices)
        {
            await _shutdown();
            await using (var connection = await Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var structures = new Eve.Structures.StructureStore(Source, StructuresSchema);
                Assert.True(await structures.CreateAsync(connection, transaction, new Eve.Structures.NewStructure(structureId, 35832, 30000142, 0, 0, 0, 0,
                    CorporationId, 0, "Clone bay", "")));
                foreach (var service in onlineServices)
                    await structures.SetServiceAsync(connection, transaction, structureId, service, Eve.Structures.ServiceState.Online, 1_000);
                await transaction.CommitAsync();
            }

            await using (var store = new PersistenceStore(Source, GameStoreSchema))
            {
                await store.EnsureSchemaAsync();
                await using var owner = await OwnerSession.StartAsync(
                    store, PersistenceRoles.World, "character-host-structure-" + Guid.NewGuid().ToString("N"));
                await using var cache = new GameStoreCache(store, owner,
                    new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
                await cache.RecoverAsync();
                await cache.LoadAsync("structures");
                var serviceStates = new System.Text.Json.Nodes.JsonObject();
                foreach (var service in onlineServices)
                    serviceStates[service.ToString(System.Globalization.CultureInfo.InvariantCulture)] = (int)Eve.Structures.ServiceState.Online;
                Assert.True(cache.Write("structures", "/" + structureId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["structureID"] = structureId,
                        ["typeID"] = 35832,
                        ["solarSystemID"] = 30000142,
                        ["ownerCorpID"] = CorporationId,
                        ["allianceID"] = 0,
                        ["profileID"] = 0,
                        ["serviceStates"] = serviceStates,
                    }).Success);
                await cache.FlushTableAsync("structures");
            }

            (_runtime, _shutdown) = await BuildHostAsync(_schemas, _clock is null ? null : _clock.Read, _simSocket);
        }

        /// <summary>Sets a character's ISK balance (the wallet row is opened here if the character has none yet).</summary>
        public async Task FundAsync(long characterId, decimal isk)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var fund = new NpgsqlCommand(
                $"INSERT INTO {WalletSchema}.balances(owner_id, account_key, isk, plex) VALUES ($1, 1000, $2, 0) " +
                "ON CONFLICT (owner_id, account_key) DO UPDATE SET isk = EXCLUDED.isk", connection);
            fund.Parameters.Add(new NpgsqlParameter { Value = characterId });
            fund.Parameters.Add(new NpgsqlParameter { Value = isk });
            await fund.ExecuteNonQueryAsync();
        }

        public async Task AddCorporationMemberAsync(long characterId)
        {
            await using (var setupConnection = await Source.OpenConnectionAsync())
            await using (var setupTransaction = await setupConnection.BeginTransactionAsync())
            {
                await using (var owner = new NpgsqlCommand(
                    $"INSERT INTO {CorporationsSchema}.owner_names(owner_id, name_key, ticker_key) " +
                    "VALUES ($1, 'character-host-rig-corp', 'CHRC') ON CONFLICT (owner_id) DO NOTHING",
                    setupConnection, setupTransaction))
                {
                    owner.Parameters.Add(new NpgsqlParameter { Value = CorporationId });
                    await owner.ExecuteNonQueryAsync();
                }
                await using (var corporation = new NpgsqlCommand(
                    $"INSERT INTO {CorporationsSchema}.corporations " +
                    "(id, name, ticker, ceo_id, creator_id, alliance_id, station_id, solar_system_id, tax_rate, lp_tax_rate, description, url, member_limit, created_ms) " +
                    "VALUES ($1, 'Character Host Rig Corp', 'CHRC', $2, $2, 0, $3, 30000142, 0, 0, '', '', 100, $4) " +
                    "ON CONFLICT (id) DO NOTHING",
                    setupConnection, setupTransaction))
                {
                    corporation.Parameters.Add(new NpgsqlParameter { Value = CorporationId });
                    corporation.Parameters.Add(new NpgsqlParameter { Value = 0L });
                    corporation.Parameters.Add(new NpgsqlParameter { Value = Station });
                    corporation.Parameters.Add(new NpgsqlParameter { Value = NowUnixMs });
                    await corporation.ExecuteNonQueryAsync();
                }
                await setupTransaction.CommitAsync();
            }

            await using var connection = await Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {CorporationsSchema}.members(character_id, corporation_id, started_ms) VALUES ($1, {CorporationId}, $2) " +
                "ON CONFLICT (character_id) DO UPDATE SET corporation_id = EXCLUDED.corporation_id", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
            command.Parameters.Add(new NpgsqlParameter { Value = NowUnixMs });
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> AddCorporationOfficeAsync(long corporationId)
        {
            var itemStore = new PgItemStore(Source, InventorySchema);
            long officeFolderId;
            await using (var connection = await Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                officeFolderId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(TypeId: Eve.Corporations.CorporationOfficeStore.OfficeFolderTypeId,
                        OwnerId: corporationId, LocationId: Station, Flag: 4, Singleton: true));
                await transaction.CommitAsync();
            }

            var station = (await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(Source))).All.Single(row => row.Id == Station);
            var rentedAt = NowUnixMs;
            await using (var connection = await Source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand(
                $"INSERT INTO {CorporationsSchema}.office_rentals " +
                "(office_folder_id, corporation_id, station_id, solar_system_id, rent_isk, rented_at_ms, expires_at_ms, active) " +
                "VALUES ($1, $2, $3, $4, 10000, $5, $6, true)", connection))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = officeFolderId });
                command.Parameters.Add(new NpgsqlParameter { Value = corporationId });
                command.Parameters.Add(new NpgsqlParameter { Value = Station });
                command.Parameters.Add(new NpgsqlParameter { Value = station.SolarSystemId });
                command.Parameters.Add(new NpgsqlParameter { Value = rentedAt });
                command.Parameters.Add(new NpgsqlParameter { Value = rentedAt + Eve.Corporations.CorporationOfficeStore.RentalCycleMs });
                await command.ExecuteNonQueryAsync();
            }
            return officeFolderId;
        }

        public async Task<long> FirstNpcCorporationIdAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT id FROM {CorporationsSchema}.corporations WHERE is_npc AND NOT deleted ORDER BY id LIMIT 1", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>A fresh account in the identity schema (Omega until 2100 by the schema's default); the first one is account 1.</summary>
        public async Task<int> CreateAccountAsync(string userName = "mct-test")
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {IdentitySchema}.accounts (user_name, password_hash) VALUES ($1, '\\x00'::bytea) RETURNING id", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = userName });
            return (int)(await command.ExecuteScalarAsync())!;
        }

        public static async Task<CharacterHostRig> CreateAsync(long? nowUnixMs = null, string? simSocket = null, ClusterOptions? clusterOptions = null)
        {
            var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")
                ?? throw new InvalidOperationException("EVE_PG_URL is not set.");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var schemas = SchemaVariables.ToDictionary(
                variable => variable,
                variable => $"jc_{suffix}_{variable[4..^7].ToLowerInvariant()}");
            var clock = nowUnixMs is long startUnixMs ? new ManualClock(startUnixMs) : null;
            var source = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(Eve.Data.PgConnection.ForTest(pgUrl)).ConnectionString).Build();
            try
            {
                var (runtime, shutdown) = await BuildHostAsync(schemas, clock is null ? null : clock.Read, simSocket, clusterOptions);
                var stations = await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(source));
                var station = stations.InSystem(30000142).OrderBy(s => s.Id).First().Id;
                return new CharacterHostRig(source, schemas, runtime, shutdown, station, clock, simSocket, clusterOptions);
            }
            catch
            {
                await DropSchemasAsync(source, schemas.Values);
                await source.DisposeAsync();
                throw;
            }
        }

        public async Task RestartAsync()
        {
            await _shutdown();
            (_runtime, _shutdown) = await BuildHostAsync(_schemas, _clock is null ? null : _clock.Read, _simSocket, _clusterOptions);
        }

        public async Task<long> CreateCharacterAsync(string tutorialEntryMode = "disabled", int airNpeState = 2, int accountId = 1)
        {
            var character = new NewCharacter(
                AccountId: accountId,
                Name: $"ClonePilot{Guid.NewGuid():N}"[..18],
                Gender: 0, RaceId: 1, BloodlineId: 1, AncestryId: 1, SchoolId: 0, TypeId: 1373,
                CorporationId: CorporationId, FactionId: null,
                StationId: Station, SolarSystemId: 30000142, ConstellationId: 20000020, RegionId: 10000002,
                DollInfo: null, PortraitInfo: null)
            {
                TutorialEntryMode = tutorialEntryMode,
                AirNpeState = airNpeState,
            };
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await new PgCharacterStore(Source, CharactersSchema).InsertAsync(connection, transaction, character);
            await transaction.CommitAsync();
            return id;
        }

        public async Task SeedPublicProfileCorporationMemberAsync(long characterId, long allianceId, DateTimeOffset createdAt, long startedMs)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var character = new NpgsqlCommand(
                $"UPDATE {CharactersSchema}.characters SET created_at = $1, alliance_id = $2 WHERE id = $3", connection, transaction))
            {
                character.Parameters.Add(new NpgsqlParameter { Value = createdAt.UtcDateTime });
                character.Parameters.Add(new NpgsqlParameter { Value = allianceId });
                character.Parameters.Add(new NpgsqlParameter { Value = characterId });
                Assert.Equal(1, await character.ExecuteNonQueryAsync());
            }
            await using (var owner = new NpgsqlCommand(
                $"INSERT INTO {CorporationsSchema}.owner_names (owner_id, name_key, ticker_key) VALUES ($1, 'public-profile-corp', 'PPC')", connection, transaction))
            {
                owner.Parameters.Add(new NpgsqlParameter { Value = CorporationId });
                await owner.ExecuteNonQueryAsync();
            }
            await using (var corporation = new NpgsqlCommand(
                $"INSERT INTO {CorporationsSchema}.corporations " +
                "(id, name, ticker, ceo_id, creator_id, alliance_id, station_id, solar_system_id, tax_rate, lp_tax_rate, description, url, member_limit, created_ms) " +
                "VALUES ($1, 'Public Profile Corp', 'PPC', $2, $2, $3, $4, 30000142, 0, 0, '', '', 100, $5)", connection, transaction))
            {
                corporation.Parameters.Add(new NpgsqlParameter { Value = CorporationId });
                corporation.Parameters.Add(new NpgsqlParameter { Value = characterId });
                corporation.Parameters.Add(new NpgsqlParameter { Value = allianceId });
                corporation.Parameters.Add(new NpgsqlParameter { Value = Station });
                corporation.Parameters.Add(new NpgsqlParameter { Value = NowUnixMs });
                await corporation.ExecuteNonQueryAsync();
            }
            await using (var member = new NpgsqlCommand(
                $"INSERT INTO {CorporationsSchema}.members (character_id, corporation_id, started_ms) VALUES ($1, $2, $3)", connection, transaction))
            {
                member.Parameters.Add(new NpgsqlParameter { Value = characterId });
                member.Parameters.Add(new NpgsqlParameter { Value = CorporationId });
                member.Parameters.Add(new NpgsqlParameter { Value = startedMs });
                await member.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public async Task SetFactionWarfareEnlistmentAsync(int ownerKind, long ownerId, long factionId)
        {
            var store = new Eve.FactionWarfare.FwStore(Source, FactionWarfareSchema);
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await store.SaveEnlistmentAsync(connection, transaction,
                new Eve.FactionWarfare.FwEnlistment(ownerKind, ownerId, factionId, 0, 0, NowUnixMs));
            await transaction.CommitAsync();
        }

        public async Task SetSkillLevelAsync(long characterId, int typeId, short level)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {SkillsSchema}.character_skills(character_id, type_id, level, skill_points) VALUES ($1, $2, $3, 0)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
            command.Parameters.Add(new NpgsqlParameter { Value = typeId });
            command.Parameters.Add(new NpgsqlParameter { Value = level });
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> AddCloneAsync(long characterId, long locationId, string kind, string name, int[] implants)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            long cloneId;
            await using (var insert = new NpgsqlCommand(
                $"INSERT INTO {CharactersSchema}.character_jump_clones(character_id, location_id, location_kind, clone_name) " +
                "VALUES ($1, $2, $3, $4) RETURNING jump_clone_id", connection, transaction))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = characterId });
                insert.Parameters.Add(new NpgsqlParameter { Value = locationId });
                insert.Parameters.Add(new NpgsqlParameter { Value = kind });
                insert.Parameters.Add(new NpgsqlParameter { Value = name });
                cloneId = (long)(await insert.ExecuteScalarAsync())!;
            }
            for (var slot = 0; slot < implants.Length; slot++)
            {
                await using var implant = new NpgsqlCommand(
                    $"INSERT INTO {CharactersSchema}.character_jump_clone_implants(jump_clone_id, slot, type_id) VALUES ($1, $2, $3)",
                    connection, transaction);
                implant.Parameters.Add(new NpgsqlParameter { Value = cloneId });
                implant.Parameters.Add(new NpgsqlParameter { Value = (short)slot });
                implant.Parameters.Add(new NpgsqlParameter { Value = implants[slot] });
                await implant.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            return cloneId;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _shutdown();
            }
            finally
            {
                try
                {
                    await DropSchemasAsync(Source, _schemas.Values);
                }
                finally
                {
                    await Source.DisposeAsync();
                }
            }
        }

        private static async Task<(HostRuntime Runtime, Func<Task> Shutdown)> BuildHostAsync(IReadOnlyDictionary<string, string> schemas,
            Func<long>? clockOverride = null, string? simSocket = null, ClusterOptions? clusterOptions = null)
        {
            var previous = SchemaVariables.Concat(["EVE_MARKET_SEED", "EVE_SIM", "EVE_SIM_SOCKET", "EVE_WORMHOLES", "EVE_ESS_SYSTEMS"])
                .ToDictionary(variable => variable, variable => Environment.GetEnvironmentVariable(variable));
            try
            {
                foreach (var (variable, schema) in schemas)
                    Environment.SetEnvironmentVariable(variable, schema);
                Environment.SetEnvironmentVariable("EVE_MARKET_SEED", "skip");
                Environment.SetEnvironmentVariable("EVE_SIM", simSocket is null ? "off" : "on");
                Environment.SetEnvironmentVariable("EVE_ESS_SYSTEMS", "");
                if (simSocket is not null)
                    Environment.SetEnvironmentVariable("EVE_SIM_SOCKET", simSocket);
                Environment.SetEnvironmentVariable("EVE_WORMHOLES", "off");
                var settings = HostConfiguration.Read();
                if (clusterOptions is not null)
                    settings = settings with { Options = clusterOptions };
                var host = await HostComposition.BuildAsync(settings, clockOverride);
                return (host, host.ShutdownAsync);
            }
            finally
            {
                foreach (var (variable, value) in previous)
                    Environment.SetEnvironmentVariable(variable, value);
            }
        }

        private static async Task DropSchemasAsync(NpgsqlDataSource source, IEnumerable<string> schemas)
        {
            foreach (var schema in schemas)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        private sealed class ManualClock(long initialUnixMs)
        {
            private long _unixMs = initialUnixMs;

            public long Read() => Interlocked.Read(ref _unixMs);
            public void Set(long unixMs) => Interlocked.Exchange(ref _unixMs, unixMs);
        }
    }
}
