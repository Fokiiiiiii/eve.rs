using System.Text.Json.Nodes;
using Eve.Data;
using Eve.Sde;
using Npgsql;

namespace Eve.Universe.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }

    public static string? Url => Environment.GetEnvironmentVariable("EVE_PG_URL");
}

public class StationNameTests
{
    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(5, "V")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(19, "XIX")]
    [InlineData(40, "XL")]
    [InlineData(99, "XCIX")]
    public void Planet_numbers_are_written_in_roman_numerals(int number, string expected) => Assert.Equal(expected, StationCatalog.Roman(number));

    [Fact]
    public void A_station_at_a_moon_names_the_moon_and_one_at_a_planet_does_not()
    {
        Assert.Equal("Jita IV - Moon 4 - Caldari Navy Assembly Plant", StationCatalog.NameOf("Jita", 4, 4, "Caldari Navy", "Assembly Plant"));
        Assert.Equal("Manifest V - AIR Laboratories Trade Center", StationCatalog.NameOf("Manifest", 5, null, "AIR Laboratories", "Trade Center"));
        Assert.Equal("Paara I - Deep Core Mining Inc.", StationCatalog.NameOf("Paara", 1, null, "Deep Core Mining Inc.", null));
        Assert.Equal("Paara I - Deep Core Mining Inc.", StationCatalog.NameOf("Paara", 1, null, "Deep Core Mining Inc.", ""));
    }
}

public class StationCatalogTests
{
    private static readonly JsonObject Recorded = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", "stations.json")))!;

    private static async Task<StationCatalog> LoadAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var reader = new SdeReader(source);
        Assert.True(await reader.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        return await StationCatalog.LoadAsync(reader);
    }

    /// <summary>The fields of a recorded <c>GetStation</c> answer (it names <c>stationTypeID</c> twice; the last one counts).</summary>
    private static Dictionary<string, JsonNode?> Fields(JsonNode call)
    {
        var fields = new Dictionary<string, JsonNode?>();
        foreach (var pair in call["ok"]!["args"]!["$tuple"]![1]!["$substream"]!["args"]!["$dict"]!.AsArray())
        {
            fields[(string)pair![0]!] = pair[1];
        }
        return fields;
    }

    [PgFact]
    public async Task Nearly_every_station_of_the_data_is_in_the_catalog()
    {
        var catalog = await LoadAsync();
        Assert.InRange(catalog.Count, 5000, 5300);
    }

    [PgFact]
    public async Task Every_recorded_station_has_the_name_and_place_the_reference_gave_it()
    {
        var catalog = await LoadAsync();
        var checkedStations = 0;
        foreach (var node in (JsonArray)Recorded["stations"]!)
        {
            var id = (long)node!["id"]!;
            var f = Fields(node["call"]!);
            var station = catalog.Get(id);
            Assert.NotNull(station);
            Assert.Equal((string)f["stationName"]!, station.Name);
            Assert.Equal(((int)f["stationTypeID"]!, (long)f["ownerID"]!, (int)f["operationID"]!), (station.TypeId, station.OwnerId, station.OperationId));
            Assert.Equal(((int)f["solarSystemID"]!, (int)f["constellationID"]!, (int)f["regionID"]!), (station.SolarSystemId, station.ConstellationId, station.RegionId));
            Assert.Equal((double)f["security"]!, Math.Round(station.Security, 6), 6);
            Assert.Equal(((double)f["reprocessingStationsTake"]!, (int)f["reprocessingHangarFlag"]!), (station.ReprocessingStationsTake, station.ReprocessingHangarFlag));
            checkedStations++;
        }
        Assert.InRange(checkedStations, 60, 70);
    }

    [PgFact]
    public async Task The_names_of_the_place_the_owner_and_the_faction_come_with_the_station()
    {
        var catalog = await LoadAsync();
        var manifest = catalog.Get(60015249)!;
        Assert.Equal(("Manifest", "Manifest District", "Exordium", "AIR Laboratories", "Association for Interdisciplinary Research"),
            (manifest.SolarSystemName, manifest.ConstellationName, manifest.RegionName, manifest.OwnerName, manifest.FactionName));
        Assert.Equal((500028L, 40491241L), (manifest.FactionId, manifest.OrbitId)); // recorded from the reference's GetSolarSystem
    }

    [PgFact]
    public async Task An_unknown_station_is_not_invented()
    {
        var catalog = await LoadAsync();
        Assert.Null(catalog.Get(1)); // the reference answers a made-up id with Jita's record
        Assert.Null(catalog.Get(0));
    }
}

public class SolarSystemCatalogTests
{
    [PgFact]
    public async Task The_systems_of_the_static_data_say_where_they_are_in_the_universe()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var reader = new SdeReader(source);
        Assert.True(await reader.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var systems = await SolarSystemCatalog.LoadAsync(reader);

        Assert.True(systems.Count > 8000, $"{systems.Count} systems");
        // the two systems of the recorded jump (jump.json): the second is in the next constellation of the same region
        var from = systems.Get(30100032)!;
        var to = systems.Get(30100034)!;
        Assert.Equal((20010001, 20010002), (from.ConstellationId, to.ConstellationId));
        Assert.Equal(from.RegionId, to.RegionId);
        Assert.Equal(30000142, systems.Get(30000142)!.Id);
        Assert.Equal("Jita", systems.Get(30000142)!.Name);
        // the faction a system belongs to is its own (Jita's is CONCORD's), else its constellation's or its region's (Perimeter's is the Caldari State's)
        Assert.Equal(500006, systems.Get(30000142)!.FactionId);
        Assert.Equal(500001, systems.Get(30000144)!.FactionId);
        Assert.Null(systems.Get(42));
    }
}
