using Eve.Characters;
using Npgsql;

namespace Eve.Characters.Tests;

public sealed class JumpCloneStoreTests
{
    private static NewCharacter Pilot(string name) =>
        new(1, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: 1000006, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: null, PortraitInfo: null);

    [PgFact]
    public async Task A_new_character_has_no_installed_clones_and_no_last_jump()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Clone Fresh"));
        var store = new PgJumpCloneStore(s.Source, s.CharactersSchema);

        var state = await store.ReadAsync(characterId);

        Assert.Empty(state.Clones);
        Assert.Equal(0L, state.TimeLastJumpFileTime);
    }

    [PgFact]
    public async Task Installed_clones_read_back_in_order_with_their_implants_and_survive_a_new_store()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Clone Read"));
        var first = await AddCloneAsync(s, characterId, 60000610, "station", "Home body", [24242, 33399]);
        var second = await AddCloneAsync(s, characterId, 1_000_000_000_001, "structure", "", []);

        var state = await new PgJumpCloneStore(s.Source, s.CharactersSchema).ReadAsync(characterId);

        Assert.Equal(new[] { first, second }, state.Clones.Select(c => c.JumpCloneId).ToArray());
        var clone = state.Clones[0];
        Assert.Equal(60000610L, clone.LocationId);
        Assert.Equal("station", clone.LocationKind);
        Assert.Equal("Home body", clone.Name);
        Assert.Equal(new[] { 24242, 33399 }, clone.ImplantTypeIds.ToArray());
        Assert.Equal("structure", state.Clones[1].LocationKind);
        Assert.Empty(state.Clones[1].ImplantTypeIds);
    }

    [PgFact]
    public async Task Rename_changes_only_a_clone_the_character_owns()
    {
        await using var s = await PgSchemas.CreateAsync();
        var owner = await Insert(s, Pilot("Clone Owner"));
        var other = await Insert(s, Pilot("Clone Other"));
        var clone = await AddCloneAsync(s, owner, 60000610, "station", "Old", []);
        var store = new PgJumpCloneStore(s.Source, s.CharactersSchema);

        Assert.False(await store.RenameAsync(other, clone, "Stolen"));
        Assert.True(await store.RenameAsync(owner, clone, "New name"));

        Assert.Equal("New name", (await store.ReadAsync(owner)).Clones.Single().Name);
        Assert.Empty((await store.ReadAsync(other)).Clones);
    }

    [PgFact]
    public async Task Destroy_removes_the_clone_and_its_implants_and_refuses_a_clone_the_character_does_not_own()
    {
        await using var s = await PgSchemas.CreateAsync();
        var owner = await Insert(s, Pilot("Clone Destroy"));
        var other = await Insert(s, Pilot("Clone Bystander"));
        var clone = await AddCloneAsync(s, owner, 60000610, "station", "Doomed", [24242]);
        var store = new PgJumpCloneStore(s.Source, s.CharactersSchema);

        Assert.Null(await store.DestroyAsync(other, clone));
        Assert.Single((await store.ReadAsync(owner)).Clones);

        var removed = await store.DestroyAsync(owner, clone);

        Assert.NotNull(removed);
        Assert.Equal(new[] { 24242 }, removed!.ImplantTypeIds);
        Assert.Empty((await store.ReadAsync(owner)).Clones);
        Assert.Equal(0L, await CountImplantsAsync(s, clone));
    }

    [PgFact]
    public async Task Reset_last_jump_sets_the_time_back_to_zero_and_persists()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Clone Jumper"));
        await using (var set = s.Source.CreateCommand(
            $"UPDATE {s.CharactersSchema}.characters SET time_last_clone_jump_filetime = 133000000000000000 WHERE id = $1"))
        {
            set.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await set.ExecuteNonQueryAsync();
        }
        var store = new PgJumpCloneStore(s.Source, s.CharactersSchema);
        Assert.Equal(133000000000000000L, (await store.ReadAsync(characterId)).TimeLastJumpFileTime);

        await store.ResetLastJumpAsync(characterId);

        Assert.Equal(0L, (await new PgJumpCloneStore(s.Source, s.CharactersSchema).ReadAsync(characterId)).TimeLastJumpFileTime);
    }

    [PgFact]
    public async Task Jump_rechecks_the_cooldown_inside_the_transaction_before_moving_a_second_clone()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Clone Concurrent Jumper"));
        const long origin = 60000610;
        var first = await AddCloneAsync(s, characterId, 60000611, "station", "First", []);
        var second = await AddCloneAsync(s, characterId, 60000612, "station", "Second", []);
        var store = new PgJumpCloneStore(s.Source, s.CharactersSchema);
        const long firstJump = 133000000000000000;

        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var jump = await store.JumpAsync(connection, transaction, characterId, first, origin, "station", [], 24, firstJump);
            Assert.False(jump.CooldownActive);
            Assert.Equal(first, jump.Target!.JumpCloneId);
            await transaction.CommitAsync();
        }

        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var jump = await store.JumpAsync(connection, transaction, characterId, second, origin, "station", [], 24, firstJump + 1);
            Assert.True(jump.CooldownActive);
            Assert.Null(jump.Target);
            await transaction.CommitAsync();
        }

        var state = await store.ReadAsync(characterId);
        Assert.Equal(firstJump, state.TimeLastJumpFileTime);
        Assert.Contains(state.Clones, clone => clone.JumpCloneId == second && clone.LocationId == 60000612);
    }

    [PgFact]
    public async Task Same_station_jump_preserves_an_expired_timer_and_allows_a_remote_jump()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Clone Local Station"));
        const long origin = 60000610;
        var local = await AddCloneAsync(s, characterId, origin, "station", "Local", []);
        var remote = await AddCloneAsync(s, characterId, 60000611, "station", "Remote", []);
        const long previousJump = 133000000000000000;
        const long nextJump = previousJump + TimeSpan.TicksPerDay;
        await SetLastJumpAsync(s, characterId, previousJump);
        var store = new PgJumpCloneStore(s.Source, s.CharactersSchema);

        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var result = await store.JumpAsync(connection, transaction, characterId, local, origin, "station", [], 24, nextJump - 1);
            Assert.False(result.CooldownActive);
            Assert.Equal(local, result.Target!.JumpCloneId);
            await transaction.CommitAsync();
        }

        Assert.Equal(previousJump, (await store.ReadAsync(characterId)).TimeLastJumpFileTime);

        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var result = await store.JumpAsync(connection, transaction, characterId, remote, origin, "station", [], 24, nextJump);
            Assert.False(result.CooldownActive);
            Assert.Equal(remote, result.Target!.JumpCloneId);
            await transaction.CommitAsync();
        }

        Assert.Equal(nextJump, (await store.ReadAsync(characterId)).TimeLastJumpFileTime);
    }

    [PgFact]
    public async Task Same_structure_jump_preserves_an_active_timer_and_remote_jump_remains_blocked()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Clone Local Structure"));
        const long origin = 1_000_000_000_001;
        const long remoteLocation = 1_000_000_000_002;
        var local = await AddCloneAsync(s, characterId, origin, "structure", "Local", []);
        var remote = await AddCloneAsync(s, characterId, remoteLocation, "structure", "Remote", []);
        const long previousJump = 133000000000000000;
        var now = previousJump + TimeSpan.TicksPerHour;
        await SetLastJumpAsync(s, characterId, previousJump);
        var store = new PgJumpCloneStore(s.Source, s.CharactersSchema);

        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var result = await store.JumpAsync(connection, transaction, characterId, local, origin, "structure", [], 24, now);
            Assert.False(result.CooldownActive);
            Assert.Equal(local, result.Target!.JumpCloneId);
            await transaction.CommitAsync();
        }

        Assert.Equal(previousJump, (await store.ReadAsync(characterId)).TimeLastJumpFileTime);

        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var result = await store.JumpAsync(connection, transaction, characterId, remote, origin, "structure", [], 24, now + 1);
            Assert.True(result.CooldownActive);
            Assert.Null(result.Target);
            await transaction.CommitAsync();
        }

        var state = await store.ReadAsync(characterId);
        Assert.Equal(previousJump, state.TimeLastJumpFileTime);
        Assert.Contains(state.Clones, clone => clone.JumpCloneId == remote && clone.LocationId == remoteLocation);
    }

    private static async Task<long> AddCloneAsync(PgSchemas s, long characterId, long locationId, string kind, string name, int[] implants)
    {
        await using var connection = await s.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        long cloneId;
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {s.CharactersSchema}.character_jump_clones(character_id, location_id, location_kind, clone_name) " +
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
                $"INSERT INTO {s.CharactersSchema}.character_jump_clone_implants(jump_clone_id, slot, type_id) VALUES ($1, $2, $3)",
                connection, transaction);
            implant.Parameters.Add(new NpgsqlParameter { Value = cloneId });
            implant.Parameters.Add(new NpgsqlParameter { Value = (short)slot });
            implant.Parameters.Add(new NpgsqlParameter { Value = implants[slot] });
            await implant.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return cloneId;
    }

    private static async Task<long> CountImplantsAsync(PgSchemas s, long cloneId)
    {
        await using var command = s.Source.CreateCommand(
            $"SELECT count(*) FROM {s.CharactersSchema}.character_jump_clone_implants WHERE jump_clone_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = cloneId });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetLastJumpAsync(PgSchemas s, long characterId, long fileTime)
    {
        await using var set = s.Source.CreateCommand(
            $"UPDATE {s.CharactersSchema}.characters SET time_last_clone_jump_filetime = $1 WHERE id = $2");
        set.Parameters.Add(new NpgsqlParameter { Value = fileTime });
        set.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await set.ExecuteNonQueryAsync();
    }

    private static async Task<long> Insert(PgSchemas s, NewCharacter c)
    {
        await using var connection = await s.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await s.Characters.InsertAsync(connection, transaction, c);
        await transaction.CommitAsync();
        return id;
    }
}
