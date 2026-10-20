using System.Text;
using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Services.Space.Beacons;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The CRAB rules (<see cref="AnalysisBeaconRules"/>): the embedded owner figures load and refuse what the first cut does not build, and the beacon types, their
/// link lists and their reward types are the pinned SDE's (SDE 3396210, types 60244 and 92183, group 4137).
/// </summary>
public class AnalysisBeaconRulesTests
{
    private static AnalysisBeaconRules Parse(string json) => AnalysisBeaconRules.Load(_ => new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private const string Valid = """
        {
          "schema": "eve.rs/analysis-beacon-rules/v1",
          "beacons": [
            { "typeId": 60244, "waves": [ { "atScanProgress": 0, "spawnPool": "rogue_drones", "amount": 4 } ], "rewards": [ { "typeId": 60459, "quantity": 1441 } ] },
            { "typeId": 92183, "waves": [ { "atScanProgress": 0, "spawnPool": "rogue_drones_deadspace_pve", "amount": 6 } ], "rewards": [ { "typeId": 60473, "quantity": 1 } ] }
          ]
        }
        """;

    [Fact]
    public void The_embedded_rules_give_each_type_its_wave_and_rewards()
    {
        var rules = AnalysisBeaconRules.LoadEmbedded();

        Assert.Equal(2, rules.Types.Count);
        Assert.Equal([new BeaconWaveRule(0, "rogue_drones", 4), new BeaconWaveRule(0.5, "rogue_drones", 4)], rules.For(60244)!.Waves);
        Assert.Equal([new BeaconWaveRule(0, "rogue_drones_deadspace_pve", 6), new BeaconWaveRule(0.5, "rogue_drones_deadspace_pve", 6)], rules.For(92183)!.Waves);
        Assert.Equal([new BeaconRewardRule(60459, 1441), new BeaconRewardRule(60466, 1), new BeaconRewardRule(60463, 1), new BeaconRewardRule(60465, 1), new BeaconRewardRule(60464, 1)], rules.For(60244)!.Rewards);
        Assert.Equal([new BeaconRewardRule(60459, 2270), new BeaconRewardRule(60473, 1), new BeaconRewardRule(60472, 1), new BeaconRewardRule(60474, 1), new BeaconRewardRule(60475, 1)], rules.For(92183)!.Rewards);
    }

    [Fact]
    public void The_durations_and_the_link_lists_are_the_official_ones()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), AnalysisBeaconRules.ActivationDelay);
        Assert.Equal(TimeSpan.FromHours(1), AnalysisBeaconRules.Lifetime);
        Assert.Equal(TimeSpan.FromMinutes(4), AnalysisBeaconRules.LinkDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), AnalysisBeaconRules.ScanOf(60244));
        Assert.Equal(TimeSpan.FromMinutes(16), AnalysisBeaconRules.ScanOf(92183));
        Assert.Equal(300, AnalysisBeaconRules.LinkTypeListOf(60244));
        Assert.Equal(946, AnalysisBeaconRules.LinkTypeListOf(92183));
        Assert.True(AnalysisBeaconRules.IsBeacon(60244) && AnalysisBeaconRules.IsBeacon(92183));
        Assert.False(AnalysisBeaconRules.IsBeacon(60245));
    }

    [Fact]
    public void Character_energy_and_system_interference_regenerate_continuously_to_their_caps()
    {
        Assert.Equal(50, AnalysisBeaconRules.RegenerateResource(0, 1_000, 1_000 + 11 * 60 * 60 * 1000L,
            AnalysisBeaconRules.MaximumCharacterEnergy, AnalysisBeaconRules.CharacterEnergyFullRegeneration), 8);
        Assert.Equal(100, AnalysisBeaconRules.RegenerateResource(0, 1_000, 1_000 + 22 * 60 * 60 * 1000L,
            AnalysisBeaconRules.MaximumCharacterEnergy, AnalysisBeaconRules.CharacterEnergyFullRegeneration), 8);
        Assert.Equal(2, AnalysisBeaconRules.RegenerateResource(50, 1_000, 1_000 + 12 * 60 * 60 * 1000L,
            AnalysisBeaconRules.MaximumSignalInterference, AnalysisBeaconRules.SignalInterferenceFullDecay, decays: true), 8);
    }

    [Fact]
    public void Link_costs_preserve_the_reported_daily_use_rates()
    {
        Assert.Equal(100d / 3, AnalysisBeaconRules.CharacterEnergyCostOf(AnalysisBeaconRules.StandardType), 8);
        Assert.Equal(25, AnalysisBeaconRules.CharacterEnergyCostOf(AnalysisBeaconRules.CarrierType));
        Assert.Equal(3, AnalysisBeaconRules.SignalInterferenceCostOf(AnalysisBeaconRules.StandardType));
        Assert.Equal(2.5, AnalysisBeaconRules.SignalInterferenceCostOf(AnalysisBeaconRules.CarrierType));
    }

    [Fact]
    public void A_file_with_another_schema_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(Valid.Replace("analysis-beacon-rules/v1", "analysis-beacon-rules/v2")));

        Assert.Contains("schema", error.Message);
    }

    [Fact]
    public void A_first_wave_not_at_scan_progress_zero_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(Valid.Replace("\"atScanProgress\": 0, \"spawnPool\": \"rogue_drones\"", "\"atScanProgress\": 0.5, \"spawnPool\": \"rogue_drones\"")));

        Assert.Contains("scan progress 0", error.Message);
    }

    [Fact]
    public void A_file_without_the_carrier_beacon_is_refused()
    {
        const string standardOnly = """
            { "schema": "eve.rs/analysis-beacon-rules/v1", "beacons": [
              { "typeId": 60244, "waves": [ { "atScanProgress": 0, "spawnPool": "rogue_drones", "amount": 4 } ], "rewards": [] } ] }
            """;

        var error = Assert.Throws<InvalidDataException>(() => Parse(standardOnly));

        Assert.Contains("both CRAB types", error.Message);
    }

    [Fact]
    public void A_file_naming_a_type_that_is_not_a_beacon_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Valid.Replace("\"typeId\": 92183", "\"typeId\": 60245")));
    }

    [Fact]
    public void A_reward_without_a_quantity_or_an_unknown_field_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Valid.Replace("\"quantity\": 1441", "\"quantity\": 0")));
        Assert.Throws<System.Text.Json.JsonException>(() => Parse(Valid.Replace("\"amount\": 4", "\"amount\": 4, \"extra\": 1")));
    }

    [PgFact]
    public async Task The_beacon_types_and_their_reward_types_are_the_pinned_sde_records()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        var rules = AnalysisBeaconRules.LoadEmbedded();
        var needed = rules.Types.Values.SelectMany(rule => rule.Rewards.Select(reward => reward.TypeId))
            .Concat(rules.Types.Keys).Concat([23757, 11567]).ToList();
        await statics.EnsureTypesAsync(needed);

        foreach (var beacon in new[] { AnalysisBeaconRules.StandardType, AnalysisBeaconRules.CarrierType })
        {
            var type = statics.Type(beacon);
            Assert.NotNull(type);
            Assert.Equal(AnalysisBeaconRules.Group, type.GroupId);
            Assert.Equal(AnalysisBeaconRules.DeployableCategory, type.CategoryId);
            // the three layers: hp (9), shield capacity (263) and armor hp (265), 50,000 each; the Anchoring skill (182) at level 3 (277)
            Assert.Equal(50_000, type.Attributes[9]);
            Assert.Equal(50_000, type.Attributes[263]);
            Assert.Equal(50_000, type.Attributes[265]);
            Assert.Equal(11584, type.Attributes[182]);
            Assert.Equal(3, type.Attributes[277]);
        }
        foreach (var reward in rules.Types.Values.SelectMany(rule => rule.Rewards))
        {
            Assert.NotNull(statics.Type(reward.TypeId));
        }
        // the SDE's English name of the reward item (the group is "Rogue Drone Analysis Data")
        Assert.Equal("Rogue Drone Infestation Data", statics.Type(60459)!.Name);
        Assert.Equal("Exigent Heavy Drone Firepower Mutaplasmid", statics.Type(60464)!.Name);
        Assert.Equal("Exigent Heavy Drone Projection Mutaplasmid", statics.Type(60466)!.Name);
        Assert.Equal("Exigent Heavy Drone Navigation Mutaplasmid", statics.Type(60463)!.Name);
        Assert.Equal("Exigent Heavy Drone Durability Mutaplasmid", statics.Type(60465)!.Name);
        Assert.Equal("Exigent Medium Drone Firepower Mutaplasmid", statics.Type(60473)!.Name);
        Assert.Equal("Exigent Medium Drone Navigation Mutaplasmid", statics.Type(60472)!.Name);
        Assert.Equal("Exigent Medium Drone Durability Mutaplasmid", statics.Type(60474)!.Name);
        Assert.Equal("Exigent Medium Drone Projection Mutaplasmid", statics.Type(60475)!.Name);
    }

    [PgFact]
    public async Task Both_beacon_blueprints_match_the_pinned_sde_manufacturing_recipe()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.Equal(3396210L, await sde.BuildAsync());
        var blueprints = await sde.ReadAsync("blueprints", [60514, 92182]);

        foreach (var blueprint in blueprints)
        {
            var manufacturing = blueprint.Doc.GetProperty("activities").GetProperty("manufacturing");
            Assert.Equal(12_000, manufacturing.GetProperty("time").GetInt32());
            Assert.Equal(1, manufacturing.GetProperty("products")[0].GetProperty("quantity").GetInt32());
            var productType = manufacturing.GetProperty("products")[0].GetProperty("typeID").GetInt32();
            Assert.Contains(productType, new[] { AnalysisBeaconRules.StandardType, AnalysisBeaconRules.CarrierType });

            var materials = manufacturing.GetProperty("materials").EnumerateArray()
                .ToDictionary(row => row.GetProperty("typeID").GetInt32(), row => row.GetProperty("quantity").GetInt32());
            Assert.Equal(new Dictionary<int, int>
            {
                [34] = 1_400_000, [35] = 400_000, [37] = 6_000, [39] = 10_000, [40] = 5_000,
                [2867] = 2, [2872] = 1, [2875] = 1, [2876] = 1,
            }, materials);
        }

        Assert.Equal(2, blueprints.Count);
    }

    [PgFact]
    public async Task A_carrier_links_in_both_lists_and_a_titan_only_in_the_standard_one()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([23757, 11567]);

        Assert.True(await statics.TypeListNamesAsync(AnalysisBeaconRules.StandardLinkTypeList, 23757));
        Assert.True(await statics.TypeListNamesAsync(AnalysisBeaconRules.CarrierLinkTypeList, 23757));
        Assert.True(await statics.TypeListNamesAsync(AnalysisBeaconRules.StandardLinkTypeList, 11567));
        Assert.False(await statics.TypeListNamesAsync(AnalysisBeaconRules.CarrierLinkTypeList, 11567));
    }
}
