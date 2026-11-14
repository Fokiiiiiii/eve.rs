using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Skills;
using Eve.Skills;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class CharacterHostTests
{
    [PgFact]
    public async Task Registered_cerebral_accelerator_refreshes_training_attributes_without_a_running_queue()
    {
        const long startUnixMs = 1_800_000_000_000;
        const int boosterTypeId = 49_539;
        await using var rig = await CharacterHostRig.CreateAsync(startUnixMs);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new SdeReader(rig.Source));
        var skill = catalog.Skills
            .Where(candidate => candidate.Published && candidate.Rank == 1 && candidate.Needs.Count == 0)
            .OrderBy(candidate => candidate.TypeId)
            .First();

        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = characterId, UserId = 1, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        rig.Sessions.Enter(characterId, session);
        var handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var dogma = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("dogmaIM"));
        var attributesBefore = await BoundAsync(handler, session, "GetAttributes");
        var rateBefore = SkillRate(skill, attributesBefore);
        var booster = await GiveItemAsync(rig, characterId, boosterTypeId, rig.Station, ItemFlags.Hangar, singleton: true);

        Assert.IsType<PyNone>(await BoundAtAsync(dogma, session, rig.Station,
            "UseBooster", new PyLong(booster), new PyLong(rig.Station)));

        var notifications = Notifications(sink);
        Assert.Equal(new[] { "OnItemChange", "OnServerBoostersChanged" }, notifications.Select(notification => notification.Name));
        Assert.Equal(1, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE item_id = {booster} AND character_id = {characterId}"));
        var consumed = Assert.IsType<Eve.Inventory.Item>(await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(booster));
        Assert.Equal((characterId, 88), (consumed.LocationId, consumed.Flag));

        var attributesAfter = await BoundAsync(handler, session, "GetAttributes");
        var rateAfter = SkillRate(skill, attributesAfter);
        Assert.True(rateAfter > rateBefore);

        await rig.RestartAsync();
        rig.Sessions.Enter(characterId, session);
        handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var attributesAfterRestart = await BoundAsync(handler, session, "GetAttributes");
        Assert.Equal(rateAfter, SkillRate(skill, attributesAfterRestart));
        Assert.Equal(1, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.SkillsSchema}.active_skill_boosters WHERE item_id = {booster} AND character_id = {characterId}"));
    }

    [PgFact]
    public async Task Registered_queue_rejects_a_partially_trained_skill_after_its_prerequisite_was_lost()
    {
        const long startUnixMs = 1_800_000_000_000;
        await using var rig = await CharacterHostRig.CreateAsync(startUnixMs);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new SdeReader(rig.Source));
        var skill = catalog.Skills
            .Where(candidate => candidate.Published && candidate.Rank > 0 && candidate.Needs.Count > 0)
            .OrderBy(candidate => candidate.TypeId)
            .First();
        var skills = new PgSkillStore(rig.Source, rig.SkillsSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await skills.GrantAsync(connection, transaction, characterId, [new CharacterSkill(skill.TypeId, 0, 1)]);
            await transaction.CommitAsync();
        }

        Assert.Equal([new CharacterSkill(skill.TypeId, 0, 1)], await skills.ForCharacterAsync(characterId));
        var handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = characterId, UserId = 1, Sink = sink };
        session.Set("charid", new PyLong(characterId));
        rig.Sessions.Enter(characterId, session);

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(handler, session, "SaveNewQueue",
            new PyList([Py.Tuple(new PyInt(skill.TypeId), new PyInt(1))])));
        Assert.Equal("QueueCannotPlaceSkillBeforeRequirements", refusal.Error.UserErrorMessage);
        Assert.Empty(Notifications(sink));
        Assert.Equal([new CharacterSkill(skill.TypeId, 0, 1)], await skills.ForCharacterAsync(characterId));

        var queue = Assert.IsType<PyList>(Assert.IsType<PyTuple>(
            await BoundAsync(handler, session, "GetSkillQueueAndFreePoints")).Items[0]);
        Assert.Empty(queue.Items);

        await rig.RestartAsync();
        rig.Sessions.Enter(characterId, session);
        handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var restartedQueue = Assert.IsType<PyList>(Assert.IsType<PyTuple>(
            await BoundAsync(handler, session, "GetSkillQueueAndFreePoints")).Items[0]);
        Assert.Empty(restartedQueue.Items);
        Assert.Equal([new CharacterSkill(skill.TypeId, 0, 1)], await skills.ForCharacterAsync(characterId));
    }

    [PgFact]
    public async Task Registered_omega_purchase_does_not_retroactively_double_alpha_skill_training()
    {
        const long startUnixMs = 1_800_000_000_000;
        const long elapsedMs = 10 * 60_000;
        await using var rig = await CharacterHostRig.CreateAsync(startUnixMs);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var accounts = new PgAccountDirectory(rig.Source, new AccountOptions(), rig.IdentitySchema);
        accounts.SetSubscriptionEndsAt(1, DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs - 60_000));
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new SdeReader(rig.Source));
        var skill = catalog.Skills
            .Where(candidate => candidate.Published && candidate.Rank == 1 && candidate.Needs.Count == 0)
            .OrderBy(candidate => candidate.TypeId)
            .First();
        await rig.SetSkillLevelAsync(characterId, skill.TypeId, 0);

        var handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var store = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("storeManager"));
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = characterId, UserId = 1, Sink = sink };
        session.Set("charid", new PyLong(characterId));
        session.Set("stationid", new PyLong(rig.Station));
        rig.Sessions.Enter(characterId, session);
        var attributes = await BoundAsync(handler, session, "GetAttributes");
        var fullRate = SkillRate(skill, attributes);
        var levelOnePoints = SkillPoints.ForTraining(skill.Rank, 1);
        Assert.True(Training.DurationTicks(levelOnePoints, fullRate) < elapsedMs * 10_000);
        Assert.True(Training.DurationTicks(levelOnePoints, fullRate / 2) > elapsedMs * 10_000);

        await BoundAsync(handler, session, "SaveNewQueue", new PyList([Py.Tuple(new PyInt(skill.TypeId), new PyInt(1))]));
        rig.AdvanceTo(startUnixMs + elapsedMs);
        await using (var wallet = rig.Source.CreateCommand(
            $"INSERT INTO {rig.WalletSchema}.balances(owner_id, account_key, isk, plex) VALUES ($1, 1000, 0, 500) " +
            "ON CONFLICT (owner_id, account_key) DO UPDATE SET plex = EXCLUDED.plex"))
        {
            wallet.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await wallet.ExecuteNonQueryAsync();
        }

        var purchase = await store.CallAsync(new ServiceCall
        {
            Method = "buy_offer",
            Args = [new PyStr("omega_30_days"), new PyStr("PLX"), new PyInt(1)],
            Kwargs = Py.Dict(
                ("from_character_id", Py.Number(characterId)),
                ("to_character_id", Py.Number(characterId)),
                ("message", new PyStr("")),
                ("is_game_time", PyBool.True),
                ("store_id", new PyInt(4))),
            Session = session,
            CallId = new PyInt(900),
        });
        Assert.Equal(PyBool.True, Assert.IsType<PyDict>(purchase).Entries
            .Single(entry => entry.Key is PyStr { Value: "success" }).Value);
        Assert.True(accounts.SubscriptionEndsAt(1) > DateTimeOffset.FromUnixTimeMilliseconds(rig.NowUnixMs));
        var notifications = Notifications(sink);
        Assert.Equal(new[] { "OnNewSkillQueueSaved", "OnSubscriptionTimeUpdateServer", "OnSubscriptionChangedServer" },
            notifications.Select(notification => notification.Name));
        var history = accounts.SubscriptionTrainingHistory(1);
        Assert.Equal(2, history.Count);
        Assert.Equal(0, history[0].FromFileTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs - 60_000).ToFileTime(), history[0].ExpiryFileTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs + elapsedMs).ToFileTime(), history[1].FromFileTime);

        var projected = Assert.IsType<PyTuple>(await BoundAsync(handler, session, "GetSkillQueueAndFreePoints"));
        var queue = Assert.IsType<PyList>(projected.Items[0]);
        Assert.Single(queue.Items);
        Assert.Equal(new CharacterSkill(skill.TypeId, 0, 0),
            Assert.Single(await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(characterId)));
        var line = Assert.IsType<PyObject>(queue.Items[0]);
        var projectedEnd = Integer(Field(line, "trainingEndTime"));
        Assert.True(projectedEnd > DateTimeOffset.FromUnixTimeMilliseconds(rig.NowUnixMs).ToFileTime());

        await rig.RestartAsync();
        handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var restartedQueue = Assert.IsType<PyList>(Assert.IsType<PyTuple>(
            await BoundAsync(handler, session, "GetSkillQueueAndFreePoints")).Items[0]);
        Assert.Single(restartedQueue.Items);
        Assert.Equal(projectedEnd, Integer(Field(Assert.IsType<PyObject>(restartedQueue.Items[0]), "trainingEndTime")));
        Assert.Equal(new CharacterSkill(skill.TypeId, 0, 0),
            Assert.Single(await new PgSkillStore(rig.Source, rig.SkillsSchema).ForCharacterAsync(characterId)));
        Assert.Equal(history, accounts.SubscriptionTrainingHistory(1));
    }

    [PgFact]
    public async Task Registered_skill_queue_projects_each_line_across_cerebral_accelerator_expiry()
    {
        const long startUnixMs = 1_800_000_000_000;
        const int boosterTypeId = 49_539;
        const long boosterDurationMs = 4 * 60_000;
        await using var rig = await CharacterHostRig.CreateAsync(startUnixMs);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new SdeReader(rig.Source));
        var skill = catalog.Skills
            .Where(candidate => candidate.Published && candidate.Rank == 1 && candidate.Needs.Count == 0)
            .OrderBy(candidate => candidate.TypeId)
            .First();
        await rig.SetSkillLevelAsync(characterId, skill.TypeId, 0);

        var handler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var session = new ClientSession { CharacterId = characterId, UserId = 1, Sink = new RecordingPacketSink() };
        session.Set("charid", new PyLong(characterId));
        var normalAttributes = await BoundAsync(handler, session, "GetAttributes");
        var normalRate = SkillRate(skill, normalAttributes);

        var booster = await GiveItemAsync(rig, characterId, boosterTypeId, characterId, 88, singleton: true);
        var expiryUnixMs = startUnixMs + boosterDurationMs;
        var expiryFileTime = DateTimeOffset.FromUnixTimeMilliseconds(expiryUnixMs).ToFileTime();
        await InsertBoosterAsync(rig, booster, characterId, boosterTypeId, expiryFileTime);

        var queueInput = new PyList(
        [
            Py.Tuple(new PyInt(skill.TypeId), new PyInt(1)),
            Py.Tuple(new PyInt(skill.TypeId), new PyInt(2)),
        ]);
        await BoundAsync(handler, session, "SaveNewQueue", queueInput);

        var boostedAttributes = await BoundAsync(handler, session, "GetAttributes");
        var boostedRate = SkillRate(skill, boostedAttributes);
        Assert.True(boostedRate > normalRate);
        var startFileTime = DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs).ToFileTime();
        var levelOne = SkillPoints.ForTraining(skill.Rank, 1);
        var levelTwo = SkillPoints.ForTraining(skill.Rank, 2);
        var pointsAtExpiry = Training.PointsAt(0, levelOne, startFileTime, expiryFileTime, boostedRate);
        Assert.InRange(pointsAtExpiry, 1, levelOne - 1);
        var expectedLevelOneEnd = expiryFileTime + Training.DurationTicks(levelOne - pointsAtExpiry, normalRate);
        var expectedLevelTwoEnd = expectedLevelOneEnd + Training.DurationTicks(levelTwo - levelOne, normalRate);

        var response = Assert.IsType<PyTuple>(await BoundAsync(handler, session, "GetSkillQueueAndFreePoints"));
        var queue = Assert.IsType<PyList>(response.Items[0]);
        Assert.Equal(2, queue.Items.Count);
        var first = Assert.IsType<PyObject>(queue.Items[0]);
        var second = Assert.IsType<PyObject>(queue.Items[1]);
        Assert.Equal(startFileTime, Integer(Field(first, "trainingStartTime")));
        Assert.Equal(expectedLevelOneEnd, Integer(Field(first, "trainingEndTime")));
        Assert.Equal(expectedLevelOneEnd, Integer(Field(second, "trainingStartTime")));
        Assert.Equal(expectedLevelTwoEnd, Integer(Field(second, "trainingEndTime")));
    }
}
