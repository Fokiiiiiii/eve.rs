using Eve.Skills;
using Eve.Wallet;

namespace Eve.Characters.Tests;

public class CreationTests
{
    private static readonly CreationCatalog Catalog = new(
        [new Race(1, 601, "Ibis"), new Race(2, 588, "Reaper")],
        [new Bloodline(1, 1), new Bloodline(2, 1), new Bloodline(3, 2)],
        [
            new StarterLocation(1000006, 500001, 60000610, 30002775, 20000406, 10000033),
            new StarterLocation(1000007, 500001, 60000611, 30002775, 20000406, 10000033),
            new StarterLocation(1000044, 500002, 60000001, 30000001, 20000001, 10000001),
        ]);

    private static readonly SkillCatalog Skills = new(
        [new SkillType(3300, "Gunnery", 255, "Gunnery", true, 1), new SkillType(3301, "Small Hybrid Turret", 255, "Gunnery", true, 1), new SkillType(3315, "Repair Systems", 1210, "Armor", true, 4)],
        new Dictionary<int, IReadOnlyList<StartingSkill>>
        {
            [1] = [new(3300, 4), new(3301, 1), new(3315, 0)],
            [2] = [new(3300, 3), new(9999, 1)], // 9999 is not a skill: the data disagrees with itself
        });

    private static CharacterCreation Creation(PgSchemas s) => new(s.Source, s.Characters, s.Items, s.Skills, Skills, s.Wallets, new StartingWallet(), Catalog);

    private static CreationRequest Request(string name, int account = 1, int race = 1, int bloodline = 1, int gender = 1, int school = 35) =>
        new(account, name, race, bloodline, gender, AncestryId: 10, school, [1, 2], [3]);

    [PgFact]
    public async Task A_character_is_made_with_its_rookie_ship_docked_at_the_school_station()
    {
        await using var s = await PgSchemas.CreateAsync();
        var created = Assert.IsType<Created>(await Creation(s).CreateAsync(Request("Ann Lee")));

        var c = (await s.Characters.GetAsync(created.CharacterId))!;
        Assert.Equal(("Ann Lee", 1, 1, 1, 35, 1373), (c.Name, c.AccountId, c.RaceId, c.BloodlineId, c.SchoolId, c.TypeId));
        Assert.Equal((1000006L, (long?)500001, 60000610L, 30002775, 20000406, 10000033), (c.CorporationId, c.FactionId, c.StationId, c.SolarSystemId, c.ConstellationId, c.RegionId));
        Assert.Equal(created.ShipId, c.ShipId);
        var (doll, portrait) = await s.Characters.AppearanceAsync(created.CharacterId);
        Assert.Equal(new byte[] { 1, 2 }, doll);
        Assert.Equal(new byte[] { 3 }, portrait);

        var ship = (await s.Items.GetAsync(created.ShipId))!;
        Assert.Equal((601, created.CharacterId, 60000610L, 4, true, 1L), (ship.TypeId, ship.OwnerId, ship.LocationId, ship.Flag, ship.Singleton, ship.Quantity));
    }

    [PgFact]
    public async Task The_ship_comes_with_its_starter_modules_fitted_in_their_slots()
    {
        await using var s = await PgSchemas.CreateAsync();
        var created = Assert.IsType<Created>(await Creation(s).CreateAsync(Request("Ann Lee")));

        var fitted = await s.Items.InLocationAsync(created.ShipId, created.CharacterId);
        Assert.Equal(
            [(21857, 19), (3638, 27), (3651, 28)],
            fitted.OrderBy(i => i.Id).Select(i => (i.TypeId, i.Flag)));
        Assert.All(fitted, i => Assert.True(i.Singleton && i.OwnerId == created.CharacterId && i.Quantity == 1));
        // made after the ship, in the order of the kit
        Assert.Equal(Enumerable.Range(1, 3).Select(n => created.ShipId + n), fitted.OrderBy(i => i.Id).Select(i => i.Id));
        // and nothing else is left in the hangar but the ship
        var hangar = await s.Items.InLocationAsync(60000610, created.CharacterId);
        Assert.Equal([created.ShipId], hangar.Select(i => i.Id));
    }

    [Theory]
    [InlineData(601, 3638)]
    [InlineData(606, 3638)]
    [InlineData(588, 3636)]
    [InlineData(596, 3634)]
    public void Each_rookie_ship_has_an_afterburner_the_weapon_of_its_race_and_a_miner(int ship, int weapon)
    {
        Assert.Equal(
            [new StarterModule(21857, 19), new StarterModule(weapon, 27), new StarterModule(3651, 28)],
            StarterKits.For(ship));
    }

    [Fact]
    public void A_ship_without_a_kit_starts_empty()
    {
        Assert.Empty(StarterKits.For(0));
    }

    [PgFact]
    public async Task The_character_starts_with_the_skills_of_its_race_at_their_points()
    {
        await using var s = await PgSchemas.CreateAsync();
        var created = Assert.IsType<Created>(await Creation(s).CreateAsync(Request("Ann Lee")));
        Assert.Equal(
            new CharacterSkill[] { new(3300, 4, 45255), new(3301, 1, 250), new(3315, 0, 0) },
            await s.Skills.ForCharacterAsync(created.CharacterId));
    }

    [PgFact]
    public async Task The_character_starts_with_the_starting_wallet()
    {
        await using var s = await PgSchemas.CreateAsync();
        var created = Assert.IsType<Created>(await Creation(s).CreateAsync(Request("Ann Lee")));
        Assert.Equal(new Balance(created.CharacterId, 100_000m, 2_222), await s.Wallets.GetAsync(created.CharacterId));

        var rich = new CharacterCreation(s.Source, s.Characters, s.Items, s.Skills, Skills, s.Wallets, new StartingWallet(Isk: 5m, Plex: 7), Catalog);
        var other = Assert.IsType<Created>(await rich.CreateAsync(Request("Bob Roe", account: 2)));
        Assert.Equal(new Balance(other.CharacterId, 5m, 7), await s.Wallets.GetAsync(other.CharacterId));
    }

    [PgFact]
    public async Task A_race_whose_starting_skill_is_not_in_the_catalog_makes_nothing()
    {
        await using var s = await PgSchemas.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Creation(s).CreateAsync(Request("Bad Data", race: 2, bloodline: 3, school: 33)));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.CharactersSchema}.characters"));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.InventorySchema}.items")); // the ship and its modules included
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.SkillsSchema}.character_skills"));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.WalletSchema}.balances"));
    }

    [PgFact]
    public async Task The_school_asked_for_is_used_when_it_takes_that_race_and_else_the_races_first()
    {
        await using var s = await PgSchemas.CreateAsync();
        var creation = Creation(s);
        var same = Assert.IsType<Created>(await creation.CreateAsync(Request("Pilot One", school: 36)));
        Assert.Equal(36, (await s.Characters.GetAsync(same.CharacterId))!.SchoolId);
        var other = Assert.IsType<Created>(await creation.CreateAsync(Request("Pilot Two", school: 31)));
        Assert.Equal(35, (await s.Characters.GetAsync(other.CharacterId))!.SchoolId);
    }

    [PgFact]
    public async Task Names_that_break_the_rules_or_are_taken_are_refused_and_leave_nothing_behind()
    {
        await using var s = await PgSchemas.CreateAsync();
        var creation = Creation(s);
        Assert.Equal(new CreationRefused(CreationRefusal.InvalidName), await creation.CreateAsync(Request("a")));
        Assert.Equal(new CreationRefused(CreationRefusal.InvalidName), await creation.CreateAsync(Request("Bad@Name")));
        Assert.Equal(new CreationRefused(CreationRefusal.InvalidName), await creation.CreateAsync(Request("Two  Spaces")));
        Assert.IsType<Created>(await creation.CreateAsync(Request("Ann Lee")));
        Assert.Equal(new CreationRefused(CreationRefusal.NameTaken), await creation.CreateAsync(Request("ANN LEE", account: 2)));

        Assert.Equal(1L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.CharactersSchema}.characters"));
        Assert.Equal(4L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.InventorySchema}.items")); // the one ship and its three modules
        Assert.Equal(3L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.SkillsSchema}.character_skills"));
    }

    [PgFact]
    public async Task An_account_has_three_slots_and_another_account_has_its_own()
    {
        await using var s = await PgSchemas.CreateAsync();
        var creation = Creation(s);
        for (var i = 1; i <= CharacterCreation.SlotsPerAccount; i++)
        {
            Assert.IsType<Created>(await creation.CreateAsync(Request($"Slot Pilot {i}")));
        }
        Assert.Equal(new CreationRefused(CreationRefusal.SlotsFull), await creation.CreateAsync(Request("One Too Many")));
        Assert.IsType<Created>(await creation.CreateAsync(Request("Other Account", account: 2)));
        Assert.Equal(3, await s.Characters.CountForAccountAsync(1));
    }

    [PgFact]
    public async Task Creations_racing_for_the_last_slot_cannot_both_win()
    {
        await using var s = await PgSchemas.CreateAsync();
        var creation = Creation(s);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => creation.CreateAsync(Request($"Racer {i}"))));
        Assert.Equal(CharacterCreation.SlotsPerAccount, outcomes.OfType<Created>().Count());
        Assert.Equal(12 - CharacterCreation.SlotsPerAccount, outcomes.OfType<CreationRefused>().Count(o => o.Reason == CreationRefusal.SlotsFull));
        Assert.Equal(CharacterCreation.SlotsPerAccount, await s.Characters.CountForAccountAsync(1));
    }

    [PgFact]
    public async Task Choices_a_player_cannot_make_are_refused()
    {
        await using var s = await PgSchemas.CreateAsync();
        var creation = Creation(s);
        var refused = new CreationRefused(CreationRefusal.InvalidChoice);
        Assert.Equal(refused, await creation.CreateAsync(Request("Pilot A", race: 16)));      // no such playable race
        Assert.Equal(refused, await creation.CreateAsync(Request("Pilot B", bloodline: 3)));  // bloodline 3 is race 2's
        Assert.Equal(refused, await creation.CreateAsync(Request("Pilot C", bloodline: 99)));
        Assert.Equal(refused, await creation.CreateAsync(Request("Pilot D", gender: 3)));
        Assert.Equal(refused, await creation.CreateAsync(Request("Pilot E", race: 4, bloodline: 5)));
        Assert.Equal(0, await s.Characters.CountForAccountAsync(1));
    }

    [PgFact]
    public async Task A_failure_after_the_character_is_written_rolls_the_character_back()
    {
        await using var s = await PgSchemas.CreateAsync();
        // the inventory has no table: the ship cannot be written, so the character must not stay
        await using (var drop = s.Source.CreateCommand($"DROP TABLE {s.InventorySchema}.items"))
        {
            await drop.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Creation(s).CreateAsync(Request("Ann Lee")));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.CharactersSchema}.characters"));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.SkillsSchema}.character_skills"));
    }

    [PgFact]
    public async Task A_failure_in_the_skills_rolls_back_the_character_and_the_ship()
    {
        await using var s = await PgSchemas.CreateAsync();
        await using (var drop = s.Source.CreateCommand($"DROP TABLE {s.SkillsSchema}.character_skills"))
        {
            await drop.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Creation(s).CreateAsync(Request("Ann Lee")));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.CharactersSchema}.characters"));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.InventorySchema}.items"));
    }

    [PgFact]
    public async Task A_failure_opening_the_wallet_rolls_everything_back()
    {
        await using var s = await PgSchemas.CreateAsync();
        await using (var drop = s.Source.CreateCommand($"DROP TABLE {s.WalletSchema}.balances"))
        {
            await drop.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Creation(s).CreateAsync(Request("Ann Lee")));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.CharactersSchema}.characters"));
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.InventorySchema}.items")); // the ship and its modules included
        Assert.Equal(0L, await s.ScalarAsync<long>($"SELECT count(*) FROM {s.SkillsSchema}.character_skills"));
    }
}

public class OnlineCharacterTests
{
    [Fact]
    public void A_character_is_held_by_one_session_until_it_leaves()
    {
        var online = new OnlineCharacters();
        Assert.True(online.TryEnter(100, sessionId: 1));
        Assert.True(online.TryEnter(100, sessionId: 1)); // the holder again
        Assert.False(online.TryEnter(100, sessionId: 2));
        Assert.True(online.IsOnline(100));

        online.Leave(1);
        Assert.False(online.IsOnline(100));
        Assert.True(online.TryEnter(100, sessionId: 2));
        Assert.Equal(1, online.Count);
    }

    [Fact]
    public void A_session_holds_one_character_at_a_time()
    {
        var online = new OnlineCharacters();
        Assert.True(online.TryEnter(100, sessionId: 1));
        Assert.True(online.TryEnter(200, sessionId: 1));
        Assert.False(online.IsOnline(100));
        Assert.True(online.IsOnline(200));
        Assert.True(online.TryEnter(100, sessionId: 2));
        Assert.False(online.TryEnter(200, sessionId: 2)); // 200 is held; 2 keeps 100
        Assert.True(online.IsOnline(100));
    }

    [Fact]
    public void Leaving_without_holding_anything_is_harmless()
    {
        var online = new OnlineCharacters();
        online.Leave(5);
        Assert.Equal(0, online.Count);
    }
}
