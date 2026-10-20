using System.Text.Json.Nodes;
using Eve.Data;
using Eve.Inventory;
using Eve.Sde;
using Npgsql;

namespace Eve.Characters.Tests;

public class CatalogTests
{
    private static readonly JsonObject Starters = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", "starters.json")))!;

    private static async Task<(CreationCatalog Catalog, NpgsqlDataSource Source)> LoadAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(PgSchemas.Url!));
        var reader = new SdeReader(source);
        Assert.True(await reader.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        return (await CreationCatalog.LoadAsync(reader), source);
    }

    [PgFact]
    public async Task The_four_playable_races_have_their_rookie_ships()
    {
        var (catalog, _) = await LoadAsync();
        Assert.Equal([1, 2, 4, 8], catalog.Races.Select(r => r.Id).Order());
        Assert.Equal((601, "Ibis"), (catalog.Race(1)!.ShipTypeId, catalog.Race(1)!.ShipName));
        Assert.Equal((588, "Reaper"), (catalog.Race(2)!.ShipTypeId, catalog.Race(2)!.ShipName));
        Assert.Equal((596, "Impairor"), (catalog.Race(4)!.ShipTypeId, catalog.Race(4)!.ShipName));
        Assert.Equal((606, "Velator"), (catalog.Race(8)!.ShipTypeId, catalog.Race(8)!.ShipName));
        Assert.Null(catalog.Race(16)); // the Jove cannot be played
    }

    [PgFact]
    public async Task Every_rookie_ship_has_a_kit_that_fits_its_slots()
    {
        var (catalog, source) = await LoadAsync();
        var reader = new SdeReader(source);
        // effects of the static data: hiPower 12 (high slot), medPower 13 (medium), loPower 11 (low)
        int[] slotEffects = [11, 12, 13];
        foreach (var race in catalog.Races)
        {
            var kit = StarterKits.For(race.ShipTypeId);
            Assert.NotEmpty(kit);
            var dogma = (await reader.ReadAsync("typeDogma", kit.Select(m => (long)m.TypeId))).ToDictionary(r => r.Key, r => r.Doc);
            var slots = new HashSet<int>();
            foreach (var module in kit)
            {
                var effects = dogma[module.TypeId].GetProperty("dogmaEffects").EnumerateArray().Select(e => e.GetProperty("effectID").GetInt32()).Where(slotEffects.Contains).ToArray();
                var family = Assert.Single(effects);
                var first = family switch { 12 => ItemFlags.HiSlot0, 13 => ItemFlags.MedSlot0, _ => ItemFlags.LowSlot0 };
                Assert.InRange(module.Flag, first, first + 7);
                Assert.True(slots.Add(module.Flag), $"two modules in slot {module.Flag}");
            }
        }
    }

    [PgFact]
    public async Task Every_school_starts_its_pupils_in_a_real_place()
    {
        var (catalog, _) = await LoadAsync();
        foreach (var school in CreationCatalog.Schools)
        {
            var start = catalog.StarterFor(school.CorporationId);
            Assert.NotNull(start);
            Assert.True(start.StationId > 60_000_000 && start.StationId < 61_000_000, $"station {start.StationId}");
            Assert.True(start.SolarSystemId >= 30_000_000 && start.ConstellationId >= 20_000_000 && start.RegionId >= 10_000_000);
            Assert.NotNull(start.FactionId);
            Assert.NotNull(catalog.Race(school.RaceId));
        }
    }

    [PgFact]
    public async Task One_start_station_can_be_given_to_every_school_as_on_the_reference()
    {
        var (_, source) = await LoadAsync();
        var catalog = await CreationCatalog.LoadAsync(new SdeReader(source), startStationId: 60015249);
        // what the reference recorded for every new character: the station, its system, constellation and region
        var recorded = ((JsonObject)((JsonArray)((JsonObject)((JsonArray)Starters["starters"]!)[0]!["selection"]!["ok"]!)["$tuple"]!)[2]![0]!["args"]!)["$dict"]!.AsArray()
            .Where(pair => (string)pair![0]! is "stationID" or "solarSystemID" or "constellationID" or "regionID")
            .ToDictionary(pair => (string)pair![0]!, pair => (long)pair![1]!);
        Assert.Equal(4, recorded.Count);
        foreach (var school in CreationCatalog.Schools)
        {
            var start = catalog.StarterFor(school.CorporationId)!;
            Assert.Equal(
                (recorded["stationID"], recorded["solarSystemID"], recorded["constellationID"], recorded["regionID"]),
                (start.StationId, (long)start.SolarSystemId, (long)start.ConstellationId, (long)start.RegionId));
            Assert.Equal(school.CorporationId, start.CorporationId); // the corporation still comes from the school
            Assert.NotNull(start.FactionId);
        }
    }

    [PgFact]
    public async Task A_start_station_that_is_not_in_the_static_data_leaves_no_starting_place()
    {
        var (_, source) = await LoadAsync();
        var catalog = await CreationCatalog.LoadAsync(new SdeReader(source), startStationId: 1);
        Assert.All(CreationCatalog.Schools, school => Assert.Null(catalog.StarterFor(school.CorporationId)));
    }

    [PgFact]
    public async Task What_each_bloodline_is_given_matches_the_recording_of_the_reference()
    {
        var (catalog, _) = await LoadAsync();
        var checkedBloodlines = 0;
        foreach (var node in (JsonArray)Starters["starters"]!)
        {
            var recorded = (JsonObject)node!;
            var bloodlineId = (int)recorded["bloodlineID"]!;
            var row = ((JsonObject)((JsonArray)((JsonObject)recorded["selection"]!["ok"]!)["$tuple"]!)[2]![0]!["args"]!)["$dict"]!.AsArray()
                .ToDictionary(pair => (string)pair![0]!, pair => pair![1]);

            var bloodline = catalog.Bloodline(bloodlineId);
            Assert.NotNull(bloodline);
            Assert.Equal((int)recorded["raceID"]!, bloodline.RaceId);

            var school = CreationCatalog.ResolveSchool((int)recorded["schoolID"]!, bloodline.RaceId);
            Assert.NotNull(school);
            Assert.Equal((int)row["schoolID"]!, school.Id);
            Assert.Equal((long)row["corporationID"]!, school.CorporationId);

            var race = catalog.Race(bloodline.RaceId)!;
            Assert.Equal((int)row["shipTypeID"]!, race.ShipTypeId);
            Assert.Equal((string)row["shipName"]!, race.ShipName);
            Assert.Equal((int)row["typeID"]!, CreationCatalog.CharacterTypeId(bloodlineId));
            checkedBloodlines++;
        }
        Assert.Equal(12, checkedBloodlines);
    }

    [Fact]
    public void A_school_is_the_one_asked_for_when_it_takes_the_race_else_the_races_first()
    {
        Assert.Equal(36, CreationCatalog.ResolveSchool(36, 1)!.Id);
        Assert.Equal(35, CreationCatalog.ResolveSchool(11, 1)!.Id);   // not a school of that race
        Assert.Equal(35, CreationCatalog.ResolveSchool(33, 1)!.Id);   // a school of another race
        Assert.Equal(31, CreationCatalog.ResolveSchool(0, 4)!.Id);
        Assert.Equal(37, CreationCatalog.ResolveSchool(38, 8)!.Id == 38 ? 37 : 0);
        Assert.Null(CreationCatalog.ResolveSchool(35, 16));
    }

    [Fact]
    public void The_schools_are_two_per_playable_race_and_each_has_its_own_corporation()
    {
        Assert.Equal(8, CreationCatalog.Schools.Count);
        Assert.Equal(new[] { 1, 2, 4, 8 }, CreationCatalog.Schools.Select(s => s.RaceId).Distinct().Order());
        Assert.All(CreationCatalog.Schools.GroupBy(s => s.RaceId), g => Assert.Equal(2, g.Count()));
        Assert.Equal(8, CreationCatalog.Schools.Select(s => s.CorporationId).Distinct().Count());
    }

    [Theory]
    [InlineData(1, 1373)]
    [InlineData(8, 1380)]
    [InlineData(11, 1383)]
    [InlineData(14, 1386)]
    [InlineData(999, 1373)]
    public void The_character_type_follows_the_bloodline(int bloodline, int type)
    {
        Assert.Equal(type, CreationCatalog.CharacterTypeId(bloodline));
    }

    [PgFact]
    public async Task ReadByFieldAsync_returns_types_in_requested_groups()
    {
        var (_, source) = await LoadAsync();
        var reader = new SdeReader(source);

        var rows = await reader.ReadByFieldAsync("types", "groupID", [6, 7, 8, 9]);

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Contains(row.Doc.GetProperty("groupID").GetInt64(), new long[] { 6, 7, 8, 9 }));
        Assert.Equal(rows.Count, rows.Select(row => row.Key).Distinct().Count());
    }

    [PgFact]
    public async Task ReadContainingAsync_finds_type_dogma_rows_for_an_effect()
    {
        var (_, source) = await LoadAsync();
        var reader = new SdeReader(source);

        var rows = await reader.ReadContainingAsync("typeDogma", """{"dogmaEffects":[{"effectID":101}]}""");

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Contains(
            row.Doc.GetProperty("dogmaEffects").EnumerateArray(),
            effect => effect.GetProperty("effectID").GetInt32() == 101));
    }

    [PgFact]
    public async Task The_sde_reader_refuses_a_table_name_that_could_carry_sql_and_reads_a_real_one()
    {
        var (_, source) = await LoadAsync();
        var reader = new SdeReader(source);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync("races\"; DROP TABLE x; --"));
        Assert.Throws<ArgumentException>(() => new SdeReader(source, "Sde;"));
        var races = await reader.ReadAsync("races");
        Assert.Equal(11, races.Count);
        var some = await reader.ReadAsync("races", [2, 8, 999]);
        Assert.Equal([2L, 8L], some.Select(r => r.Key));
        Assert.Empty(await reader.ReadAsync("races", []));
        Assert.Equal("Caldari", SdeReader.English(races[0].Doc.GetProperty("name")));
        Assert.Equal("", SdeReader.English(races[0].Doc));
    }
}
