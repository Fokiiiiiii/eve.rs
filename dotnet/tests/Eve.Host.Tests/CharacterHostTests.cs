using Eve.Characters;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Universe;
using Npgsql;
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
    public async Task Jump_clones_are_read_changed_and_kept_through_the_host_registry_and_a_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = character, Sink = sink };
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

        // destroy removes the clone; the last jump reset is persisted as zero
        Assert.IsType<PyNone>(await BoundAsync(restarted, session, "DestroyInstalledClone", new PyLong(other)));
        Assert.Equal(new[] { first }, Rows(Field(await BoundAsync(restarted, session, "GetCloneState"), "clones"))
            .Select(row => ((PyLong)row.Items[0]).Value).ToArray());
        Assert.IsType<PyNone>(await BoundAsync(restarted, session, "ResetLastCloneJumpTime"));
        Assert.Equal(new PyLong(0), Field(await BoundAsync(restarted, session, "GetCloneState"), "timeLastJump"));
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
        // the graveyard corporation (EveJS DOOMHEIM_CORPORATION_ID)
        Assert.Equal(1000001L, (await rig.Characters.GetAsync(character))!.CorporationId);

        // the deleted character is hidden from its account and refused at selection
        var deleted = Assert.IsType<Character>(await rig.Characters.GetAsync(character));
        Assert.True(deleted.IsDeleted);
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
        var entries = Assert.IsType<PyDict>(Assert.IsType<PyObject>(keyVal).Args).Entries;
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

    private sealed class RecordingPacketSink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(packet);
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
        public CharacterSessions Sessions => Assert.IsType<CharacterSessions>(_runtime.CharacterSessions);
        public Eve.Scheduler.DurableScheduler Scheduler => _runtime.Scheduler ?? throw new InvalidOperationException("the scheduler is not composed");
        public PgCharacterStore Characters => new(Source, CharactersSchema);
        public long Station { get; }
        public string CharactersSchema => _schemas["EVE_CHARACTERS_SCHEMA"];
        public string SkillsSchema => _schemas["EVE_SKILLS_SCHEMA"];
        public string InventorySchema => _schemas["EVE_INVENTORY_SCHEMA"];
        public string IdentitySchema => _schemas["EVE_IDENTITY_SCHEMA"];
        public string WalletSchema => _schemas["EVE_WALLET_SCHEMA"];

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
