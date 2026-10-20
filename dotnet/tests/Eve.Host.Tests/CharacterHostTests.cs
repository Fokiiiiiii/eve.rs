using Eve.Characters;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;
using Eve.Services;
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
public sealed class CharacterHostTests
{
    private const long ProxyNode = MachoMessage.ProxyNodeId;

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
        Assert.IsType<PyNone>(await CallAsync(restarted, "DeleteCharacter", session, new PyLong(character)));
        Assert.IsType<PyNone>(await CallAsync(restarted, "DeleteCharacter", session, new PyLong(character)));

        // the assets are in the corporation's deliveries (flag 62), the cargo still inside the ship, and the skills are gone
        var corporationItems = await new PgItemStore(rig.Source, rig.InventorySchema).OwnedByAsync(900_001_001);
        var movedShip = Assert.Single(corporationItems, item => item.Id == shipId);
        Assert.Equal(62, movedShip.Flag);
        Assert.Equal(rig.Station, movedShip.LocationId);
        var movedCargo = Assert.Single(corporationItems, item => item.Id == cargoId);
        Assert.Equal(shipId, movedCargo.LocationId);
        Assert.Empty(await new PgItemStore(rig.Source, rig.InventorySchema).OwnedByAsync(character));
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
    public async Task Character_contact_labels_persist_through_the_host_and_a_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character, UserId = 1, Sink = new RecordingPacketSink() };
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));

        Assert.Equal(new PyInt(1), await CallAsync(charMgr, "CreateLabel", session, new PyStr("Fleet"), new PyInt(7)));
        Assert.Equal(new PyInt(2), await CallAsync(charMgr, "CreateLabel", session, new PyStr("Friends"), new PyInt(3)));
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

    private static PyValue Field(PyValue keyVal, string key)
    {
        var entries = keyVal switch
        {
            PyObject obj => Assert.IsType<PyDict>(obj.Args).Entries,
            PyDict dict => dict.Entries,
            _ => throw new Xunit.Sdk.XunitException($"Expected a dictionary, got {keyVal.GetType().Name}"),
        };
        return entries.Single(entry => ((PyStr)entry.Key).Value == key).Value;
    }

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

    private sealed class RecordingPacketSink(Action? onSend = null) : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];

        public void Send(PyValue packet)
        {
            Sent.Add(packet);
            onSend?.Invoke();
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
            "EVE_STRUCTURES_SCHEMA", "EVE_AGENTS_SCHEMA",
        ];

        private const long CorporationId = 900_001_001;
        private readonly IReadOnlyDictionary<string, string> _schemas;
        private HostRuntime _runtime;
        private Func<Task> _shutdown;

        private CharacterHostRig(NpgsqlDataSource source, IReadOnlyDictionary<string, string> schemas, HostRuntime runtime, Func<Task> shutdown, long station)
        {
            Source = source;
            _schemas = schemas;
            _runtime = runtime;
            _shutdown = shutdown;
            Station = station;
        }

        public NpgsqlDataSource Source { get; }
        public ServiceRegistry Registry => _runtime.Registry;
        internal Eve.Host.PublicGateway.PublicGatewayRoutes Gateway => _runtime.PublicGateway;
        public CharacterSessions Sessions => Assert.IsType<CharacterSessions>(_runtime.CharacterSessions);
        public Eve.Scheduler.DurableScheduler Scheduler => _runtime.Scheduler ?? throw new InvalidOperationException("the scheduler is not composed");
        public PgCharacterStore Characters => new(Source, CharactersSchema);
        public long Station { get; }
        public string CharactersSchema => _schemas["EVE_CHARACTERS_SCHEMA"];
        public string SkillsSchema => _schemas["EVE_SKILLS_SCHEMA"];
        public string InventorySchema => _schemas["EVE_INVENTORY_SCHEMA"];
        public string IdentitySchema => _schemas["EVE_IDENTITY_SCHEMA"];
        public string WalletSchema => _schemas["EVE_WALLET_SCHEMA"];
        public string GameStoreSchema => _schemas["EVE_GAMESTORE_SCHEMA"];
        public string SchedulerSchema => _schemas["EVE_SCHEDULER_SCHEMA"];
        public string StructuresSchema => _schemas["EVE_STRUCTURES_SCHEMA"];

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

            (_runtime, _shutdown) = await BuildHostAsync(_schemas);
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

        /// <summary>A fresh account in the identity schema (Omega until 2100 by the schema's default); the first one is account 1.</summary>
        public async Task<int> CreateAccountAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {IdentitySchema}.accounts (user_name, password_hash) VALUES ('mct-test', '\\x00'::bytea) RETURNING id", connection);
            return (int)(await command.ExecuteScalarAsync())!;
        }

        public static async Task<CharacterHostRig> CreateAsync()
        {
            var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")
                ?? throw new InvalidOperationException("EVE_PG_URL is not set.");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var schemas = SchemaVariables.ToDictionary(
                variable => variable,
                variable => $"jc_{suffix}_{variable[4..^7].ToLowerInvariant()}");
            var source = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(Eve.Data.PgConnection.ForTest(pgUrl)).ConnectionString).Build();
            try
            {
                var (runtime, shutdown) = await BuildHostAsync(schemas);
                var stations = await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(source));
                var station = stations.All.OrderBy(s => s.Id).First().Id;
                return new CharacterHostRig(source, schemas, runtime, shutdown, station);
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
            (_runtime, _shutdown) = await BuildHostAsync(_schemas);
        }

        public async Task<long> CreateCharacterAsync()
        {
            var character = new NewCharacter(
                AccountId: 1,
                Name: $"ClonePilot{Guid.NewGuid():N}"[..18],
                Gender: 0, RaceId: 1, BloodlineId: 1, AncestryId: 1, SchoolId: 0, TypeId: 1373,
                CorporationId: CorporationId, FactionId: null,
                StationId: Station, SolarSystemId: 30000142, ConstellationId: 20000020, RegionId: 10000002,
                DollInfo: null, PortraitInfo: null);
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await new PgCharacterStore(Source, CharactersSchema).InsertAsync(connection, transaction, character);
            await transaction.CommitAsync();
            return id;
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

        private static async Task<(HostRuntime Runtime, Func<Task> Shutdown)> BuildHostAsync(IReadOnlyDictionary<string, string> schemas)
        {
            var previous = SchemaVariables.Concat(["EVE_MARKET_SEED", "EVE_SIM", "EVE_WORMHOLES"])
                .ToDictionary(variable => variable, variable => Environment.GetEnvironmentVariable(variable));
            try
            {
                foreach (var (variable, schema) in schemas)
                    Environment.SetEnvironmentVariable(variable, schema);
                Environment.SetEnvironmentVariable("EVE_MARKET_SEED", "skip");
                Environment.SetEnvironmentVariable("EVE_SIM", "off");
                Environment.SetEnvironmentVariable("EVE_WORMHOLES", "off");
                var host = await HostComposition.BuildAsync(HostConfiguration.Read());
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
    }
}
