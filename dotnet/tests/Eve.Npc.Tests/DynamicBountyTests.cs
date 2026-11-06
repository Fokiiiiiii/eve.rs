using Eve.Npc;
using Npgsql;
using System.Text;

namespace Eve.Npc.Tests;

public sealed class DynamicBountyTests
{
    private const int SystemId = 30_000_142;
    private const long Now = 1_700_000_000_000;
    private static readonly DynamicBountySystem[] Systems =
    [
        new(SystemId, 10_000_001, 0.0),
        new(SystemId + 1, 10_000_070, 0.0),
        new(SystemId + 2, 10_000_001, 0.45),
        new(DynamicBountyRules.FirstSystemId - 1, 10_000_001, 0.0),
        new(DynamicBountyRules.EndSystemId, 10_000_001, 0.0),
    ];

    [Fact]
    public void Eligibility_and_equilibrium_rounding_match_the_reference_policy()
    {
        Assert.True(DynamicBountyRules.IsEligible(Systems[0]));
        Assert.False(DynamicBountyRules.IsEligible(Systems[1]));
        Assert.False(DynamicBountyRules.IsEligible(Systems[2]));
        Assert.False(DynamicBountyRules.IsEligible(Systems[3]));
        Assert.False(DynamicBountyRules.IsEligible(Systems[4]));

        // EveJS 261028.1 resolves a 1,000,000 ISK NPC bounty at equilibrium output 1.5 before its ESS split.
        Assert.Equal(1_500_000m, NpcBounty.AmountOf(1_000_000m, 1m, 1.5m));
        Assert.Equal(2.27m, NpcBounty.AmountOf(1.005m, 1.5m, 1.5m));
        Assert.Equal(0.01m, NpcBounty.AmountOf(0.005m));
        Assert.Null(NpcBounty.AmountOf(1m, 0.004m, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => NpcBounty.AmountOf(1m, -1m, 1m));
    }

    [Fact]
    public void NPC_profiles_load_an_authored_bounty_multiplier_and_default_to_one()
    {
        const string json = """
            {"profiles":[{"id":"authored","bountyPayoutMultiplier":1.25},{"id":"default"}],"loadouts":[],"pools":[]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var authority = NpcAuthority.FromJson(stream);

        Assert.Equal(1.25m, authority.Profile("authored")!.BountyPayoutMultiplier);
        Assert.Equal(1m, authority.Profile("default")!.BountyPayoutMultiplier);
    }

    [Fact]
    public void Paid_activity_lowers_output_and_loss_activity_raises_it_with_daily_bounds()
    {
        var paid = DynamicBountyRules.Step(1.5m, 20_000_000_000L, 0);
        var loss = DynamicBountyRules.Step(1.5m, 0, 50_000_000_000L);

        Assert.Equal(1.476666666667m, decimal.Round(paid, 12, MidpointRounding.AwayFromZero));
        Assert.Equal(1.61m, loss);
        Assert.Equal(2m, DynamicBountyRules.Step(2m, 0, long.MaxValue / 2));
        Assert.Equal(1m, DynamicBountyRules.Step(1m, long.MaxValue / 2, 0));
        Assert.Equal(1.59m, DynamicBountyRules.Idle(1.6m, 1));
        Assert.Equal(1.5m, DynamicBountyRules.Idle(1m, 100));
    }

    [PgFact]
    public async Task Eligible_and_ineligible_reads_and_map_rows_use_the_right_defaults()
    {
        await using var rig = await BountyRig.CreateAsync();
        var store = new PgDynamicBountyStore(rig.Source, Systems, rig.NpcSchema);

        Assert.Equal(new DynamicBountyValue(1.5, true), await store.ReadAsync(SystemId, Now));
        Assert.Equal(new DynamicBountyValue(1, false), await store.ReadAsync(SystemId + 1, Now));
        Assert.Equal(new DynamicBountyValue(1, false), await store.ReadAsync(SystemId + 2, Now));

        var map = await store.MapAsync(Now);
        Assert.Equal(new DynamicBountyMapEntry(SystemId, 1.5), Assert.Single(map));
    }

    [PgFact]
    public async Task Paid_inputs_lower_output_and_survive_store_restart_and_period_advance()
    {
        await using var rig = await BountyRig.CreateAsync();
        var store = new PgDynamicBountyStore(rig.Source, Systems, rig.NpcSchema);
        await RecordPaidAsync(rig, store, 200_000_000m);

        var afterRestart = new PgDynamicBountyStore(rig.Source, Systems, rig.NpcSchema);
        var projected = await afterRestart.ReadAsync(SystemId, Now + DynamicBountyRules.TimeIntervalMs);
        Assert.Equal(1.476666666667, projected.Output);
        Assert.True(projected.Active);

        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var update = await afterRestart.AdvanceAsync(connection, transaction, SystemId, Now + DynamicBountyRules.TimeIntervalMs, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(1.476666666667, update!.Output);
        Assert.Equal(update.Output, (await afterRestart.ReadAsync(SystemId, Now + DynamicBountyRules.TimeIntervalMs)).Output);
    }

    [PgFact]
    public async Task Duplicate_and_concurrent_loss_inputs_are_counted_once_and_survive_restart()
    {
        await using var rig = await BountyRig.CreateAsync();
        var store = new PgDynamicBountyStore(rig.Source, Systems, rig.NpcSchema);
        var duplicate = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            RecordLossAsync(rig, store, 4_100_000_000_000_001, 500_000_000m)));
        var unique = await Task.WhenAll(Enumerable.Range(2, 4).Select(id =>
            RecordLossAsync(rig, store, 4_100_000_000_000_000 + id, 100_000_000m)));

        Assert.Equal(1, duplicate.Count(write => write?.Recorded == true));
        Assert.All(unique, write => Assert.True(write?.Recorded));

        var restarted = new PgDynamicBountyStore(rig.Source, Systems, rig.NpcSchema);
        var output = await restarted.ReadAsync(SystemId, Now + DynamicBountyRules.TimeIntervalMs);
        Assert.Equal((double)DynamicBountyRules.Step(1.5m, 0, 90_000_000_000L), output.Output);
        Assert.True(output.Active);
    }

    [PgFact]
    public async Task Invalid_or_ineligible_losses_do_not_activate_a_system()
    {
        await using var rig = await BountyRig.CreateAsync();
        var store = new PgDynamicBountyStore(rig.Source, Systems, rig.NpcSchema);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        Assert.Null(await store.RecordLossAsync(connection, transaction, SystemId + 1, 10, 500_000m, Now, CancellationToken.None));
        Assert.Null(await store.RecordLossAsync(connection, transaction, SystemId, 0, 500_000m, Now, CancellationToken.None));
        Assert.Null(await store.RecordLossAsync(connection, transaction, SystemId, 11, 0, Now, CancellationToken.None));
        await transaction.CommitAsync();

        Assert.Equal(new DynamicBountyValue(1.5, true), await store.ReadAsync(SystemId, Now));
    }

    private static async Task RecordPaidAsync(BountyRig rig, PgDynamicBountyStore store, decimal amount)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await store.RecordPaidAsync(connection, transaction, SystemId, amount, Now, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task<DynamicBountyWrite?> RecordLossAsync(BountyRig rig, PgDynamicBountyStore store, long lossId, decimal amount)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await store.RecordLossAsync(connection, transaction, SystemId, lossId, amount, Now, CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }
}
