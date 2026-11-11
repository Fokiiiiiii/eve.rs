using System.Security.Cryptography;
using System.Text;
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

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public CatalogTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task<(CreationCatalog Catalog, NpgsqlDataSource Source)> LoadAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(PgSchemas.Url!));
        var reader = new SdeReader(source);
        Assert.True(await reader.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        return (await CreationCatalog.LoadAsync(reader), source);
    }

    private static string SdeRowsSha256(IReadOnlyList<SdeRow> rows) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", rows.Select(row => $"{row.Key}\t{row.Doc.GetRawText()}")))))
            .ToLowerInvariant();

    private static string TypeSummariesSha256(IReadOnlyList<SdeTypeSummary> summaries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", summaries.Select(summary =>
                $"{summary.TypeId}\t{summary.GroupId}\t{summary.Name}\t{summary.Published.ToString().ToLowerInvariant()}")))))
            .ToLowerInvariant();

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
        Assert.Equal(
            new (int School, int Race, long Corporation)[]
            {
                (31, 4, 1000165), (32, 4, 1000077),
                (33, 2, 1000171), (34, 2, 1000172),
                (35, 1, 1000045), (36, 1, 1000044),
                (37, 8, 1000115), (38, 8, 1000169),
            },
            CreationCatalog.Schools.Select(s => (s.Id, s.RaceId, s.CorporationId)));
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
        Assert.Equal(3396210L, await reader.BuildAsync());

        var rows = await reader.ReadByFieldAsync("types", "groupID", [6, 7, 8, 9]);

        Assert.Equal(
            new long[]
            {
                6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 2014, 2015, 2016, 2017, 2063, 3796, 3797, 3798, 3799, 3800,
                3801, 3802, 3803, 17774, 30889, 34331, 45030, 45031, 45032, 45033, 45034, 45035, 45036, 45037, 45038,
                45039, 45040, 45041, 45042, 45046, 45047, 52673, 56018, 56019, 56020, 56021, 56022, 56023, 56024,
                56082, 56083, 56084, 56085, 56086, 56097, 56098, 73909, 73911, 78350,
            },
            rows.Select(row => row.Key).ToArray());
        Assert.Equal("aa5747bbf7b6af43e7022ceeaa504ea121cc7f805c1a5043d8e6bceb13d81b14", SdeRowsSha256(rows));
        Assert.Equal(
            (6L, 6L, "Sun G5 (Yellow)"),
            (
                rows[0].Key,
                rows[0].Doc.GetProperty("groupID").GetInt64(),
                rows[0].Doc.GetProperty("name").GetProperty("en").GetString()!));
    }

    [PgFact]
    public async Task ReadContainingAsync_finds_type_dogma_rows_for_an_effect()
    {
        var (_, source) = await LoadAsync();
        var reader = new SdeReader(source);
        Assert.Equal(3396210L, await reader.BuildAsync());

        var rows = await reader.ReadContainingAsync("typeDogma", """{"dogmaEffects":[{"effectID":101}]}""");

        Assert.Equal(
            new long[]
            {
                499, 501, 503, 1875, 1877, 2083, 2404, 2410, 2420, 3563, 3565, 4256, 4258, 4260, 7993, 7997, 8001,
                8007, 8023, 8025, 8027, 8089, 8091, 8093, 8101, 8103, 8105, 8113, 8115, 8117, 10629, 10631, 10642,
                13320, 13919, 13920, 13921, 13922, 13923, 13924, 13925, 13926, 13927, 13929, 13931, 13933, 14516,
                14518, 14520, 14522, 14524, 14525, 14526, 14527, 14672, 14674, 14676, 14678, 14680, 14681, 14682,
                14683, 15449, 16061, 16062, 16064, 16065, 16067, 16068, 16136, 16137, 16138, 16513, 16515, 16517,
                16519, 16521, 16523, 16525, 16527, 17484, 17485, 17487, 17488, 17490, 17491, 17901, 17938, 18639,
                19660, 19739, 20138, 20539, 20593, 20595, 20597, 20599, 20601, 20603, 21542, 22564, 22565, 22566,
                22567, 22568, 22569, 22782, 25707, 25709, 25711, 25713, 25715, 25771, 26173, 27914, 28375, 28377,
                28379, 28381, 28383, 28511, 28513, 28565, 28756, 28758, 32444, 32461, 33270, 33272, 33440, 33441,
                33442, 33446, 33448, 33450, 33452, 33453, 33454, 33455, 33456, 33457, 33458, 33459, 33461, 33462,
                33463, 33464, 33465, 33466, 34290, 34292, 34294, 35921, 35922, 35923, 37288, 37292, 37293, 37294,
                37295, 41174, 41175, 41176, 41180, 41181, 41182, 41223, 41224, 44102, 44109, 47298, 47303, 47323,
                47325, 85021, 85023, 85084, 85085, 92453,
            },
            rows.Select(row => row.Key).ToArray());
        Assert.Equal("9adb44301d7db8f159dd8ea68ce671829f098e94cbb71bf162125d37b197379d", SdeRowsSha256(rows));
        Assert.Equal(499L, rows[0].Key);
        Assert.Equal(
            new[] { (12, false), (16, false), (40, false), (101, true), (3001, false) },
            rows[0].Doc.GetProperty("dogmaEffects").EnumerateArray()
                .Select(effect => (effect.GetProperty("effectID").GetInt32(), effect.GetProperty("isDefault").GetBoolean()))
                .ToArray());
    }

    [PgFact]
    public async Task ReadTypeSummariesAsync_returns_ordered_catalog_fields()
    {
        var (_, source) = await LoadAsync();
        var reader = new SdeReader(source);
        Assert.Equal(3396210L, await reader.BuildAsync());

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var summaries = await reader.ReadTypeSummariesAsync();
        timer.Stop();

        Assert.Equal(52_591, summaries.Count);
        Assert.Equal("306238ad691fee3ccc4117c2a687204d7535dee76c3936cda4e33183e7a24ffe", TypeSummariesSha256(summaries));
        Assert.Equal(
            new[]
            {
                (0, 0, "#System", false),
                (2, 2, "Corporation", false),
                (3, 3, "Region", false),
                (4, 4, "Constellation", false),
                (5, 5, "Solar System", false),
            },
            summaries.Take(5)
                .Select(summary => (summary.TypeId, summary.GroupId, summary.Name, summary.Published))
                .ToArray());

        var ibis = Assert.Single(summaries, summary => summary.TypeId == 601);
        Assert.Equal((601, 237, "Ibis", true), (ibis.TypeId, ibis.GroupId, ibis.Name, ibis.Published));
        if (Environment.GetEnvironmentVariable("EVE_SDE_BENCH") == "1")
        {
            _output.WriteLine($"SDE type summaries: {timer.Elapsed.TotalMilliseconds:F3} ms; rows={summaries.Count}");
        }
    }

    [PgFact]
    public async Task Full_table_reads_are_shared_between_readers_for_the_same_sde_build()
    {
        var (_, source) = await LoadAsync();
        var firstReader = new SdeReader(source);
        var secondReader = new SdeReader(source);
        Assert.NotNull(await firstReader.BuildAsync());
        Assert.NotNull(await secondReader.BuildAsync());

        var uncachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var first = await firstReader.ReadAsync("mapSolarSystems");
        uncachedTimer.Stop();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => secondReader.ReadAsync("mapSolarSystems", cancellationToken: cancelled.Token));

        var cachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var second = await secondReader.ReadAsync("mapSolarSystems");
        cachedTimer.Stop();

        Assert.Same(first, second);
        Assert.NotEmpty(first);

        var typeUncachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var typeRows = await firstReader.ReadAsync("types");
        typeUncachedTimer.Stop();
        var typeCachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var cachedTypeRows = await secondReader.ReadAsync("types");
        typeCachedTimer.Stop();
        Assert.Same(typeRows, cachedTypeRows);
        Assert.NotEmpty(typeRows);

        var dogmaUncachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var dogmaRows = await firstReader.ReadAsync("typeDogma");
        dogmaUncachedTimer.Stop();
        var dogmaCachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var cachedDogmaRows = await secondReader.ReadAsync("typeDogma");
        dogmaCachedTimer.Stop();
        Assert.Same(dogmaRows, cachedDogmaRows);
        Assert.NotEmpty(dogmaRows);

        var summaryUncachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var summaries = await firstReader.ReadTypeSummariesAsync();
        summaryUncachedTimer.Stop();
        using var summaryCancelled = new CancellationTokenSource();
        summaryCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => secondReader.ReadTypeSummariesAsync(summaryCancelled.Token));
        var summaryCachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var cachedSummaries = await secondReader.ReadTypeSummariesAsync();
        summaryCachedTimer.Stop();
        Assert.Same(summaries, cachedSummaries);
        Assert.NotEmpty(summaries);

        var missionReader = new SdeReader(source, "sde_mission");
        var secondMissionReader = new SdeReader(source, "sde_mission");
        Assert.NotNull(await missionReader.BuildAsync());
        Assert.NotNull(await secondMissionReader.BuildAsync());
        var missionUncachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var missions = await missionReader.ReadAsync("missions");
        missionUncachedTimer.Stop();
        var missionCachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var cachedMissions = await secondMissionReader.ReadAsync("missions");
        missionCachedTimer.Stop();
        Assert.Same(missions, cachedMissions);

        if (Environment.GetEnvironmentVariable("EVE_SDE_BENCH") == "1")
        {
            _output.WriteLine($"SDE mapSolarSystems full read: database={uncachedTimer.Elapsed.TotalMilliseconds:F3} ms; shared cache={cachedTimer.Elapsed.TotalMilliseconds:F3} ms; rows={first.Count}");
            _output.WriteLine($"SDE types full read: database={typeUncachedTimer.Elapsed.TotalMilliseconds:F3} ms; shared cache={typeCachedTimer.Elapsed.TotalMilliseconds:F3} ms; rows={typeRows.Count}");
            _output.WriteLine($"SDE typeDogma full read: database={dogmaUncachedTimer.Elapsed.TotalMilliseconds:F3} ms; shared cache={dogmaCachedTimer.Elapsed.TotalMilliseconds:F3} ms; rows={dogmaRows.Count}");
            _output.WriteLine($"SDE type summaries: database={summaryUncachedTimer.Elapsed.TotalMilliseconds:F3} ms; shared cache={summaryCachedTimer.Elapsed.TotalMilliseconds:F3} ms; rows={summaries.Count}");
            _output.WriteLine($"SDE missions full read: database={missionUncachedTimer.Elapsed.TotalMilliseconds:F3} ms; shared cache={missionCachedTimer.Elapsed.TotalMilliseconds:F3} ms; rows={missions.Count}");
        }
    }

    [Fact]
    public async Task Sde_reader_rejects_terminal_line_feeds_before_opening_a_connection()
    {
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1;Pooling=false");

        Assert.Throws<ArgumentException>(() => new SdeReader(source, "sde\n"));

        var reader = new SdeReader(source);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync("types\n"));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadByFieldAsync("types\n", "groupID", []));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadByFieldAsync("types", "groupID\n", []));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadContainingAsync("types\n", "{}"));
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
