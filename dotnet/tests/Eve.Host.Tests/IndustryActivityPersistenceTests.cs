using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// Copying, invention and reactions over PostgreSQL and the pinned SDE: the install quote, the outputs at delivery, and the
/// facility rule for reactions. Blueprint 681 copies at 480 s per licensed run; 683 (a copy with 10 runs) invents 39581 from
/// Talocan skills (11442, 11454, 21790 at level 1) with the Esoteric Accelerant (21574: probability x1.2, +1 run, ME +2, TE +10);
/// 45732 is a reaction (360 s, Reactions 1) that makes 20 of 16672 from 100 of 16657 and 100 of 16661.
/// </summary>
public sealed class IndustryActivityPersistenceTests
{
    private const int Blueprint = 681;
    private const int CopyTime = 480;
    private const int InventionBlueprint = 683;
    private const int InventionProduct = 39581;
    private const int Accelerant = 21574;
    private const int ReactionBlueprint = 45732;
    private const int ReactionProduct = 16672;

    [Fact]
    public async Task Copying_charges_the_licensed_runs_and_delivers_one_copy_per_run_with_the_source_efficiencies()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var original = await rig.Give(owner, Blueprint, 1, blueprint: true, me: 4, te: 6);
        var balanceBefore = await rig.Balance(owner);

        var jobId = await rig.Install(owner, original, IndustryRules.Copying, runs: 3, licensedRuns: 10);

        var high = await rig.HighSecurity();
        var expected = IndustryRules.Total(IndustryRules.CostParts(860 * IndustryRules.CostPercentage * 10 * 3, 0, 0.10, high, IndustryRules.Copying));
        var (status, cost, seconds) = await rig.JobRow(jobId);
        Assert.Equal(1, status);
        Assert.Equal(expected, cost);
        Assert.Equal(CopyTime * 10 * 3, seconds);
        Assert.Equal(balanceBefore - expected, await rig.Balance(owner));

        rig.Now += (CopyTime * 10 * 3 + 1) * 1000L;
        await rig.Complete(owner, jobId);

        var copies = (await rig.Items(owner, Blueprint)).Where(item => item.BlueprintCopy).ToList();
        Assert.Equal(3, copies.Count);
        Assert.All(copies, copy =>
        {
            Assert.Equal(10, copy.BlueprintRuns);
            Assert.Equal(4, copy.MaterialEfficiency);
            Assert.Equal(6, copy.TimeEfficiency);
        });
        Assert.False((await rig.Blueprint(original)).BlueprintLocked);
    }

    [Fact]
    public async Task Copying_refuses_more_licensed_runs_than_the_blueprint_allows()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var original = await rig.Give(owner, Blueprint, 1, blueprint: true);

        await Assert.ThrowsAsync<IndustryJobUserErrorException>(() => rig.Install(owner, original, IndustryRules.Copying, licensedRuns: 301));
        Assert.Equal(0, await rig.JobCount());
    }

    [Fact]
    public async Task Invention_uses_the_decryptor_and_skills_and_delivers_the_outcome_of_the_seeded_rolls()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var copy = await rig.Give(owner, InventionBlueprint, 1, blueprint: true, copy: true, runs: 10);
        await rig.Give(owner, 20416, 8);
        await rig.Give(owner, 25887, 8);
        await rig.Give(owner, Accelerant, 1);
        await rig.Grant(owner, (11442, 1), (11454, 1), (21790, 1));

        var jobId = await rig.Install(owner, copy, IndustryRules.Invention, blueprintType: InventionBlueprint, copy: true,
            runs: 4, licensedRuns: 1, productTypeId: InventionProduct, optionalTypeId: Accelerant);

        // mean product probability 0.3 x (1 + 2 skills x 1/30 + Caldari Encryption Methods at 1/40, on the lower list) x decryptor 1.2 = 0.393
        var probability = await rig.Probability(jobId);
        Assert.Equal(0.393, probability, 9);
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(await rig.SnapshotJson(jobId))!;
        Assert.Equal(2, (int)snapshot["invention"]!["runs"]!);
        Assert.Equal(4, (int)snapshot["invention"]!["me"]!);
        Assert.Equal(14, (int)snapshot["invention"]!["te"]!);

        rig.Now += (63900L * 4 + 1) * 1000L;
        await rig.Complete(owner, jobId);

        var seed = BitConverter.ToInt32(Convert.FromBase64String((string)snapshot["rollSeed"]!), 0);
        var successes = IndustryRules.SuccessfulAttempts(4, probability, seed);
        var outputs = (await rig.Items(owner, InventionProduct)).Where(item => item.BlueprintCopy).ToList();
        Assert.Equal(successes, outputs.Count);
        Assert.All(outputs, output =>
        {
            Assert.Equal(2, output.BlueprintRuns);
            Assert.Equal(4, output.MaterialEfficiency);
            Assert.Equal(14, output.TimeEfficiency);
        });
        var remaining = (await rig.Items(owner, InventionBlueprint)).Single(item => item.BlueprintCopy);
        Assert.Equal(6, remaining.BlueprintRuns);
        Assert.Equal(0, await rig.Quantity(owner, Accelerant));
    }

    [Fact]
    public async Task Time_research_takes_the_research_skill_bonus_per_level()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var original = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Grant(owner, (3403, 2));

        var jobId = await rig.Install(owner, original, IndustryRules.ResearchTime);

        // the time-efficiency research base is 210 s; the Research skill takes 5 percent per level: 210 x 0.9 = 189
        var (_, _, seconds) = await rig.JobRow(jobId);
        Assert.Equal(189, seconds);
    }

    [Fact]
    public async Task A_facility_beyond_the_distance_skill_is_refused_and_the_skill_extends_the_range()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var original = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Give(owner, 38, 86);
        rig.InstallerSystem = await rig.NeighbourSystem();

        await Assert.ThrowsAsync<IndustryJobUserErrorException>(() => rig.Install(owner, original, IndustryRules.Manufacturing));
        Assert.Equal(0, await rig.JobCount());

        await rig.Grant(owner, (24268, 1));
        var jobId = await rig.Install(owner, original, IndustryRules.Manufacturing);
        Assert.Equal(1, (await rig.JobRow(jobId)).Status);
    }

    [Fact]
    public async Task Invention_refuses_an_original_blueprint()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var original = await rig.Give(owner, InventionBlueprint, 1, blueprint: true);
        await rig.Grant(owner, (11442, 1), (11454, 1), (21790, 1));

        await Assert.ThrowsAsync<IndustryJobUserErrorException>(() => rig.Install(owner, original, IndustryRules.Invention,
            blueprintType: InventionBlueprint, copy: false, productTypeId: InventionProduct));
    }

    [Fact]
    public async Task A_reaction_is_refused_at_an_npc_station_and_runs_in_a_structure_for_the_formula_quote()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var formula = await rig.Give(owner, ReactionBlueprint, 1, blueprint: true);
        await rig.Give(owner, 16657, 100);
        await rig.Give(owner, 16661, 100);
        await rig.Grant(owner, (45746, 1));

        await Assert.ThrowsAsync<IndustryJobUserErrorException>(() => rig.Install(owner, formula, IndustryRules.Reaction, blueprintType: ReactionBlueprint));
        Assert.Equal(0, await rig.JobCount());

        rig.Structure = true;
        var jobId = await rig.Install(owner, formula, IndustryRules.Reaction, blueprintType: ReactionBlueprint);

        // item value 100 x 2 + 100 x 3 = 500 per run; the Reactions skill takes 4 percent of 360 s per level: 345.6, rounded to 346
        var high = await rig.HighSecurity();
        var expected = IndustryRules.Total(IndustryRules.CostParts(500, 0, 0.10, high, IndustryRules.Reaction));
        var (status, cost, seconds) = await rig.JobRow(jobId);
        Assert.Equal(1, status);
        Assert.Equal(expected, cost);
        Assert.Equal(346, seconds);
        Assert.Equal(0, await rig.Quantity(owner, 16657));
        Assert.Equal(0, await rig.Quantity(owner, 16661));

        rig.Now += 347_000L;
        await rig.Complete(owner, jobId);

        Assert.Equal(20, await rig.Quantity(owner, ReactionProduct));
        Assert.False((await rig.Blueprint(formula)).BlueprintLocked);
    }
}
