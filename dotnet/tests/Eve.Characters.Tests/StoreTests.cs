using Npgsql;

namespace Eve.Characters.Tests;

public class StoreTests
{
    private static NewCharacter Pilot(string name, int account = 1, byte[]? doll = null, byte[]? portrait = null) =>
        new(account, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: 1000006, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: doll, PortraitInfo: portrait);

    private static async Task<long> Insert(PgSchemas s, NewCharacter c)
    {
        await using var connection = await s.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await s.Characters.InsertAsync(connection, transaction, c);
        await transaction.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task A_character_round_trips_and_ids_start_where_the_reference_started_them()
    {
        await using var s = await PgSchemas.CreateAsync();
        var id = await Insert(s, Pilot("Ann Lee"));
        Assert.Equal(140_000_001, id);
        var c = (await s.Characters.GetAsync(id))!;
        Assert.Equal(("Ann Lee", 1, 1, 1, 10, 35, 1373), (c.Name, c.AccountId, c.RaceId, c.BloodlineId, c.AncestryId, c.SchoolId, c.TypeId));
        Assert.Equal((1000006L, (long?)500001, (long?)null), (c.CorporationId, c.FactionId, c.AllianceId));
        Assert.Equal((60000610L, 60000610L, 30002775, 20000406, 10000033), (c.StationId, c.HomeStationId, c.SolarSystemId, c.ConstellationId, c.RegionId));
        Assert.Null(c.ShipId);
        Assert.False(c.IsQueuedForDeletion);
        Assert.True((DateTimeOffset.UtcNow - c.CreatedAt).Duration() < TimeSpan.FromMinutes(1));
        Assert.Equal(140_000_002, await Insert(s, Pilot("Bob Roe")));
    }

    [PgFact]
    public async Task An_account_sees_its_own_characters_oldest_first()
    {
        await using var s = await PgSchemas.CreateAsync();
        var first = await Insert(s, Pilot("First One", account: 7));
        var other = await Insert(s, Pilot("Someone Else", account: 8));
        var second = await Insert(s, Pilot("Second One", account: 7));
        Assert.Equal([first, second], (await s.Characters.ForAccountAsync(7)).Select(c => c.Id));
        Assert.Equal([first, other, second], (await s.Characters.AllAsync()).Select(c => c.Id));
        Assert.Equal(2, await s.Characters.CountForAccountAsync(7));
        Assert.Equal(1, await s.Characters.CountForAccountAsync(8));
        Assert.Empty(await s.Characters.ForAccountAsync(9));
        Assert.Null(await s.Characters.GetAsync(999));
    }

    [PgFact]
    public async Task A_name_is_taken_in_every_spelling_that_folds_to_the_same_key()
    {
        await using var s = await PgSchemas.CreateAsync();
        await Insert(s, Pilot("Ann Lee"));
        foreach (var spelling in new[] { "ann lee", "ANN LEE", "Ann  Lee", "  Ann Lee ", "Ann Lee" })
        {
            Assert.True(await s.Characters.NameTakenAsync(spelling), spelling);
            await Assert.ThrowsAsync<NameTakenException>(() => Insert(s, Pilot(spelling, account: 2)));
        }
        Assert.False(await s.Characters.NameTakenAsync("Ann Leigh"));
        Assert.Single(await s.Characters.ForAccountAsync(1));
    }

    [PgFact]
    public async Task Two_creations_of_one_name_at_once_leave_exactly_one_character()
    {
        await using var s = await PgSchemas.CreateAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            try
            {
                return await Insert(s, Pilot("Race Winner", account: i + 1));
            }
            catch (NameTakenException)
            {
                return -1L;
            }
        }));
        Assert.Single(results, r => r > 0);
        Assert.Equal(1L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.CharactersSchema}.characters"));
    }

    [PgFact]
    public async Task What_the_client_sent_for_the_doll_and_portrait_is_kept_as_sent()
    {
        await using var s = await PgSchemas.CreateAsync();
        byte[] doll = [1, 2, 3, 0, 255];
        var id = await Insert(s, Pilot("Ann Lee", doll: doll, portrait: []));
        var (d, p) = await s.Characters.AppearanceAsync(id);
        Assert.Equal(doll, d);
        Assert.Equal(Array.Empty<byte>(), p);
        var none = await Insert(s, Pilot("Bob Roe"));
        Assert.Equal((null, null), await s.Characters.AppearanceAsync(none));
        Assert.Equal((null, null), await s.Characters.AppearanceAsync(999));
        var many = await s.Characters.AppearanceManyAsync([id, none, 999]);
        Assert.Equal(doll, many[id].Doll);
        Assert.True(many.ContainsKey(none));
        Assert.False(many.ContainsKey(999));
    }


    [PgFact]
    public async Task Appearance_updates_preserve_omitted_fields_and_clear_a_missing_doll()
    {
        await using var s = await PgSchemas.CreateAsync();
        var id = await Insert(s, Pilot("Doll Update"));
        byte[] firstDoll = [1, 2, 3];
        byte[] portrait = [4, 5, 6];
        Assert.True(await s.Characters.SetAppearanceAsync(id, firstDoll, true, portrait, true));
        byte[] secondDoll = [7, 8];
        Assert.True(await s.Characters.SetAppearanceAsync(id, secondDoll, true, null, false));
        var (updatedDoll, keptPortrait) = await s.Characters.AppearanceAsync(id);
        Assert.Equal(secondDoll, updatedDoll);
        Assert.Equal(portrait, keptPortrait);
        Assert.True(await s.Characters.SetAppearanceAsync(id, null, true, null, true));
        Assert.Equal((null, null), await s.Characters.AppearanceAsync(id));
    }

    [PgFact]
    public async Task A_rolled_back_creation_leaves_nothing_and_the_name_free()
    {
        await using var s = await PgSchemas.CreateAsync();
        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await s.Characters.InsertAsync(connection, transaction, Pilot("Ann Lee"));
            await transaction.RollbackAsync();
        }
        Assert.False(await s.Characters.NameTakenAsync("Ann Lee"));
        Assert.Equal(0, await s.Characters.CountForAccountAsync(1));
    }

    [PgFact]
    public async Task The_ship_is_set_in_the_same_transaction_as_the_character()
    {
        await using var s = await PgSchemas.CreateAsync();
        await using var connection = await s.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await s.Characters.InsertAsync(connection, transaction, Pilot("Ann Lee"));
        await s.Characters.SetShipAsync(connection, transaction, id, 1_990_000_123);
        await transaction.CommitAsync();
        Assert.Equal(1_990_000_123, (await s.Characters.GetAsync(id))!.ShipId);
    }

    [PgFact]
    public async Task The_database_refuses_a_gender_that_is_not_zero_one_or_two()
    {
        await using var s = await PgSchemas.CreateAsync();
        await Assert.ThrowsAsync<PostgresException>(() => Insert(s, Pilot("Ann Lee") with { Gender = 3 }));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.CharactersSchema}.characters"));
    }

    [Fact]
    public void A_schema_name_that_could_carry_sql_is_refused()
    {
        using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest("Host=127.0.0.1"));
        Assert.Throws<ArgumentException>(() => new PgCharacterStore(source, "characters; DROP SCHEMA x"));
        Assert.Throws<ArgumentException>(() => new Eve.Inventory.PgItemStore(source, "Inventory"));
    }


    [PgFact]
    public async Task Direct_enlistment_faction_and_cooldown_round_trip_through_character_store()
    {
        await using var s = await PgSchemas.CreateAsync();
        var id = await Insert(s, Pilot("Warfare Pilot"));
        var cooldown = 133_000_000_000_000_000L;

        Assert.True(await s.Characters.SetFactionWarfareStateAsync(id, 500001, 0));
        var enlisted = (await s.Characters.GetAsync(id))!;
        Assert.Equal((long?)500001, enlisted.DirectFactionId);
        Assert.Equal(0, enlisted.DirectEnlistmentCooldownFileTime);

        Assert.True(await s.Characters.SetFactionWarfareStateAsync(id, null, cooldown));
        var removed = (await s.Characters.GetAsync(id))!;
        Assert.Null(removed.DirectFactionId);
        Assert.Equal(cooldown, removed.DirectEnlistmentCooldownFileTime);
    }

    [PgFact]
    public async Task Home_station_cooldown_migration_preserves_existing_rows()
    {
        await using var s = await PgSchemas.CreateAsync();
        await using (var connection = await s.Source.OpenConnectionAsync())
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {s.CharactersSchema} CASCADE";
            await drop.ExecuteNonQueryAsync();
        }

        var legacyMigrations = PgCharacterStore.MigrationSet().Take(3).ToArray();
        Assert.Equal(
            new[] { "0001_characters", "0002_character_tutorial_state", "0003_character_faction_warfare" },
            await Eve.Data.Migrations.ApplyAsync(s.Source, s.CharactersSchema, legacyMigrations));
        var id = await Insert(s, Pilot("Legacy Home"));
        Assert.Equal(
            new[] { "0004_remote_home_station_cooldown", "0005_nes_intro_state", "0006_login_milestones", "0007_achievement_tracker" },
            await PgCharacterStore.MigrateAsync(s.Source, s.CharactersSchema));

        var migrated = (await s.Characters.GetAsync(id))!;
        Assert.Equal(("Legacy Home", 60000610L, 0L), (migrated.Name, migrated.HomeStationId, migrated.NextRemoteHomeStationChangeFileTime));
        Assert.Empty(await PgCharacterStore.MigrateAsync(s.Source, s.CharactersSchema));
    }

    [PgFact]
    public async Task Home_station_changes_enforce_and_persist_remote_cooldown()
    {
        await using var s = await PgSchemas.CreateAsync();
        var id = await Insert(s, Pilot("Home Pilot"));
        var nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
        var cooldown = 365L * TimeSpan.TicksPerDay;

        Assert.Equal(HomeStationChangeResult.Changed,
            await s.Characters.TryChangeHomeStationAsync(id, 60000611, false, nowFileTime));
        Assert.Equal(HomeStationChangeResult.AlreadyHome,
            await s.Characters.TryChangeHomeStationAsync(id, 60000611, false, nowFileTime));
        Assert.Equal(0L, (await s.Characters.GetAsync(id))!.NextRemoteHomeStationChangeFileTime);

        var nextChangeFileTime = nowFileTime + cooldown;
        Assert.Equal(HomeStationChangeResult.Changed,
            await s.Characters.TryChangeHomeStationAsync(id, 60000612, true, nowFileTime));
        Assert.Equal(HomeStationChangeResult.Changed,
            await s.Characters.TryChangeHomeStationAsync(id, 60000613, false, nowFileTime + 1));
        Assert.Equal(nextChangeFileTime, (await s.Characters.GetAsync(id))!.NextRemoteHomeStationChangeFileTime);
        Assert.Equal(HomeStationChangeResult.RemoteCooldown,
            await s.Characters.TryChangeHomeStationAsync(id, 60000614, true, nextChangeFileTime - 1));

        await PgCharacterStore.MigrateAsync(s.Source, s.CharactersSchema);
        var reopened = new PgCharacterStore(s.Source, s.CharactersSchema);
        var persisted = (await reopened.GetAsync(id))!;
        Assert.Equal((60000613L, nextChangeFileTime), (persisted.HomeStationId, persisted.NextRemoteHomeStationChangeFileTime));
        Assert.Equal(nextChangeFileTime, (await reopened.GetManyAsync(new[] { id }))[0].NextRemoteHomeStationChangeFileTime);
        Assert.Equal(nextChangeFileTime, (await reopened.ForAccountAsync(1))[0].NextRemoteHomeStationChangeFileTime);
        Assert.Equal(nextChangeFileTime, (await reopened.AllAsync())[0].NextRemoteHomeStationChangeFileTime);

        Assert.Equal(HomeStationChangeResult.Changed,
            await reopened.TryChangeHomeStationAsync(id, 60000614, true, nextChangeFileTime));
        Assert.Equal(nextChangeFileTime + cooldown, (await reopened.GetAsync(id))!.NextRemoteHomeStationChangeFileTime);
        Assert.True(await reopened.ResetRemoteHomeStationChangeTimeAsync(id));
        Assert.Equal(0L, (await reopened.GetAsync(id))!.NextRemoteHomeStationChangeFileTime);
        Assert.False(await reopened.ResetRemoteHomeStationChangeTimeAsync(id + 1));
        Assert.Equal(HomeStationChangeResult.CharacterMissing,
            await reopened.TryChangeHomeStationAsync(id + 1, 60000615, true, nowFileTime));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reopened.TryChangeHomeStationAsync(id, 0, false, nowFileTime));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reopened.TryChangeHomeStationAsync(id, 60000615, false, long.MaxValue));
    }

    [PgFact]
    public async Task Concurrent_remote_home_changes_allow_only_one_change()
    {
        await using var s = await PgSchemas.CreateAsync();
        var id = await Insert(s, Pilot("Concurrent Home"));
        var nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
        var results = await Task.WhenAll(
            s.Characters.TryChangeHomeStationAsync(id, 60000611, true, nowFileTime),
            s.Characters.TryChangeHomeStationAsync(id, 60000612, true, nowFileTime));

        Assert.Equal(1, results.Count(result => result == HomeStationChangeResult.Changed));
        Assert.Equal(1, results.Count(result => result == HomeStationChangeResult.RemoteCooldown));
        var persisted = (await s.Characters.GetAsync(id))!;
        Assert.Contains(persisted.HomeStationId, new[] { 60000611L, 60000612L });
        Assert.Equal(nowFileTime + 365L * TimeSpan.TicksPerDay, persisted.NextRemoteHomeStationChangeFileTime);
    }
}
