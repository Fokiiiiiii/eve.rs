using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

public sealed class SalvagePoolTests
{
    private static readonly (string Name, int Weight)[] CommonT1 =
    [
        ("Tripped Power Circuit", 16),
        ("Burned Logic Circuit", 16),
        ("Charred Micro Circuit", 13),
        ("Fried Interface Circuit", 13),
        ("Contaminated Nanite Compound", 10),
        ("Damaged Artificial Neural Network", 8),
        ("Malfunctioning Shield Emitter", 8),
    ];

    private static readonly IReadOnlyDictionary<string, (string Name, int Weight, bool LargeOnly)[]> FactionPools =
        new Dictionary<string, (string Name, int Weight, bool LargeOnly)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["angel"] =
            [
                ("Alloyed Tritanium Bar", 18, false),
                ("Armor Plates", 12, false),
                ("Smashed Trigger Unit", 12, false),
                ("Thruster Console", 10, false),
                ("Malfunctioning Shield Emitter", 8, false),
                ("Single-crystal Superalloy I-beam", 2, true),
            ],
            ["blood"] =
            [
                ("Armor Plates", 18, false),
                ("Melted Capacitor Console", 16, false),
                ("Current Pump", 10, false),
                ("Power Circuit", 8, false),
                ("Capacitor Console", 3, true),
                ("Intact Armor Plates", 2, true),
            ],
            ["sansha"] =
            [
                ("Armor Plates", 17, false),
                ("Melted Capacitor Console", 16, false),
                ("Current Pump", 10, false),
                ("Power Circuit", 8, false),
                ("Capacitor Console", 3, true),
                ("Intact Armor Plates", 2, true),
            ],
            ["guristas"] =
            [
                ("Malfunctioning Shield Emitter", 18, false),
                ("Scorched Telemetry Processor", 14, false),
                ("Ward Console", 8, false),
                ("Tripped Power Circuit", 8, false),
                ("Enhanced Ward Console", 2, true),
                ("Intact Shield Emitter", 2, true),
            ],
            ["serpentis"] =
            [
                ("Burned Logic Circuit", 16, false),
                ("Fried Interface Circuit", 16, false),
                ("Tripped Power Circuit", 12, false),
                ("Broken Drone Transceiver", 10, false),
                ("Damaged Artificial Neural Network", 8, false),
                ("Armor Plates", 4, true),
            ],
            ["drone"] =
            [
                ("Broken Drone Transceiver", 18, false),
                ("Damaged Artificial Neural Network", 15, false),
                ("Contaminated Nanite Compound", 14, false),
                ("Conductive Polymer", 10, false),
                ("Drone Transceiver", 3, true),
                ("Artificial Neural Network", 2, true),
            ],
            ["mercenary"] =
            [
                ("Tripped Power Circuit", 14, false),
                ("Burned Logic Circuit", 14, false),
                ("Armor Plates", 10, false),
                ("Malfunctioning Shield Emitter", 10, false),
                ("Melted Capacitor Console", 8, false),
            ],
        };

    private static readonly string[] AllSalvageNames =
    [
        "Alloyed Tritanium Bar", "Armor Plates", "Smashed Trigger Unit", "Thruster Console",
        "Single-crystal Superalloy I-beam", "Melted Capacitor Console", "Current Pump", "Power Circuit",
        "Capacitor Console", "Intact Armor Plates", "Scorched Telemetry Processor", "Ward Console",
        "Enhanced Ward Console", "Intact Shield Emitter", "Broken Drone Transceiver", "Conductive Polymer",
        "Drone Transceiver", "Artificial Neural Network", "Tripped Power Circuit", "Burned Logic Circuit",
        "Charred Micro Circuit", "Fried Interface Circuit", "Contaminated Nanite Compound",
        "Damaged Artificial Neural Network", "Malfunctioning Shield Emitter",
    ];

    private static readonly (int TypeId, string Name)[] SdeProducts =
        AllSalvageNames.Select((name, index) => (TypeId: 1001 + index, Name: name)).ToArray();

    private static readonly IReadOnlyDictionary<int, string> NamesByTypeId =
        SdeProducts.ToDictionary(product => product.TypeId, product => product.Name);

    [Theory]
    [InlineData("angel", false)]
    [InlineData("angel", true)]
    [InlineData("blood", false)]
    [InlineData("blood", true)]
    [InlineData("sansha", false)]
    [InlineData("sansha", true)]
    [InlineData("guristas", false)]
    [InlineData("guristas", true)]
    [InlineData("serpentis", false)]
    [InlineData("serpentis", true)]
    [InlineData("drone", false)]
    [InlineData("drone", true)]
    [InlineData("mercenary", false)]
    [InlineData("mercenary", true)]
    [InlineData("generic", false)]
    [InlineData("generic", true)]
    public void Build_pool_matches_faction_and_size_candidates(string faction, bool isLarge)
    {
        var factionEntries = FactionPools.GetValueOrDefault(faction) ?? [];
        var expected = factionEntries
            .Where(entry => !entry.LargeOnly || isLarge)
            .Select(entry => (Name: entry.Name, Weight: (double)entry.Weight))
            .Concat(CommonT1.Select(entry => (Name: entry.Name, Weight: (double)entry.Weight)))
            .ToArray();
        var actual = SpaceContainers.BuildSalvagePool(faction, isLarge, SdeProducts)
            .Select(candidate => (Name: NamesByTypeId[candidate.TypeId], Weight: candidate.Weight))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Angel Cartel", "angel")]
    [InlineData("Dark Blood", "blood")]
    [InlineData("True Sansha", "sansha")]
    [InlineData("Dread Guristas", "guristas")]
    [InlineData("Shadow Serpentis", "serpentis")]
    [InlineData("Infested Rogue Drone", "drone")]
    [InlineData("Mordu's Legion", "mercenary")]
    [InlineData("Amarr Empire", "generic")]
    public void Classifies_wreck_faction_from_its_identity(string identity, string expected)
    {
        Assert.Equal(expected, SpaceContainers.ClassifySalvageFaction(identity));
    }

    [Theory]
    [InlineData("Angel Frigate Wreck", false)]
    [InlineData("Rogue Drone Cruiser Wreck", false)]
    [InlineData("Angel Battlecruiser Wreck", true)]
    [InlineData("Sansha Battleship Wreck", true)]
    [InlineData("Capital Carrier Wreck", true)]
    [InlineData("Force Auxiliary Wreck", true)]
    public void Detects_large_wrecks_for_size_filtered_candidates(string identity, bool expected)
    {
        Assert.Equal(expected, SpaceContainers.IsLargeSalvageWreck(identity));
    }

    [Fact]
    public void Build_pool_uses_only_available_positive_sde_type_ids()
    {
        var products = SdeProducts
            .Where(product => product.Name != "Alloyed Tritanium Bar")
            .Append((0, "Armor Plates"))
            .ToArray();

        var pool = SpaceContainers.BuildSalvagePool("angel", false, products);

        Assert.DoesNotContain(pool, candidate => candidate.TypeId == 0);
        Assert.DoesNotContain(pool, candidate => NamesByTypeId.GetValueOrDefault(candidate.TypeId) == "Alloyed Tritanium Bar");
        Assert.NotEmpty(pool);
        Assert.Empty(SpaceContainers.BuildSalvagePool("angel", false, []));
    }

    [Fact]
    public void Weighted_selection_keeps_each_boundary_with_evejs_entry_order()
    {
        IReadOnlyList<(int TypeId, double Weight)> pool = [(101, 1), (102, 1), (103, 2)];

        Assert.Equal((int?)101, SpaceContainers.SelectWeightedSalvageProductTypeId(pool, 0));
        Assert.Equal((int?)101, SpaceContainers.SelectWeightedSalvageProductTypeId(pool, 0.25));
        Assert.Equal((int?)102, SpaceContainers.SelectWeightedSalvageProductTypeId(pool, Math.BitIncrement(0.25)));
        Assert.Equal((int?)102, SpaceContainers.SelectWeightedSalvageProductTypeId(pool, 0.5));
        Assert.Equal((int?)103, SpaceContainers.SelectWeightedSalvageProductTypeId(pool, Math.BitIncrement(0.5)));
        Assert.Equal((int?)103, SpaceContainers.SelectWeightedSalvageProductTypeId(pool, Math.BitDecrement(1d)));
    }

    [Fact]
    public void Weighted_selection_skips_invalid_candidates_and_returns_null_for_empty_pools()
    {
        IReadOnlyList<(int TypeId, double Weight)> pool =
        [
            (0, 100),
            (101, double.NaN),
            (102, 0),
            (103, -1),
            (104, double.PositiveInfinity),
            (105, 1),
            (106, 3),
        ];

        Assert.Equal((int?)105, SpaceContainers.SelectWeightedSalvageProductTypeId(pool, 0.25));
        Assert.Equal((int?)106, SpaceContainers.SelectWeightedSalvageProductTypeId(pool, Math.BitIncrement(0.25)));
        Assert.Null(SpaceContainers.SelectWeightedSalvageProductTypeId([], 0.5));
        Assert.Null(SpaceContainers.SelectWeightedSalvageProductTypeId([(101, 0), (102, -1)], 0.5));
        Assert.Null(SpaceContainers.SelectWeightedSalvageProductTypeId(pool, double.NaN));
    }
}
