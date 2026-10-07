using Eve.Data;
using Npgsql;

namespace Eve.Crimewatch.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

public class SafetyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 2)]
    [InlineData(-3, 0)]
    [InlineData(1.9, 1)]
    public void A_level_is_brought_to_the_nearest_of_the_three(double requested, int expected) => Assert.Equal(expected, SafetyLevel.Normalize(requested));

    [Fact]
    public void A_value_that_is_no_number_is_full()
    {
        Assert.Equal(SafetyLevel.Full, SafetyLevel.Normalize(null));
        Assert.Equal(SafetyLevel.Full, SafetyLevel.Normalize(double.NaN));
        Assert.Equal(SafetyLevel.Full, SafetyLevel.Normalize(double.PositiveInfinity));
    }

    [PgFact]
    public async Task A_character_is_at_full_until_it_sets_a_level_and_the_level_is_kept_per_character()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "cw_" + Guid.NewGuid().ToString("N")[..12];
        await PgCrimewatchStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgCrimewatchStore(source, schema);
            Assert.Equal(SafetyLevel.Full, await store.SafetyOfAsync(1));
            Assert.Equal(SafetyLevel.Partial, await store.SetSafetyAsync(1, 1, 1000));
            Assert.Equal(SafetyLevel.Partial, await store.SafetyOfAsync(1));
            Assert.Equal(SafetyLevel.Full, await store.SafetyOfAsync(2));
            Assert.Equal(SafetyLevel.None, await store.SetSafetyAsync(1, 0, 2000));
            Assert.Equal(SafetyLevel.Full, await store.SetSafetyAsync(1, 7, 3000));   // out of range: the nearest
            Assert.Equal(SafetyLevel.Full, await store.SafetyOfAsync(1));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(Eve.Universe.SecurityClass.HighSec, false, 2, Flag.Criminal, 0, false)]
    [InlineData(Eve.Universe.SecurityClass.HighSec, false, 0, Flag.Criminal, 0, true)]
    [InlineData(Eve.Universe.SecurityClass.LowSec, false, 2, Flag.Suspect, 1, false)]
    [InlineData(Eve.Universe.SecurityClass.LowSec, false, 1, Flag.Suspect, 1, true)]
    [InlineData(Eve.Universe.SecurityClass.LowSec, true, 0, Flag.Criminal, 0, true)]
    [InlineData(Eve.Universe.SecurityClass.ZeroSec, false, 2, Flag.None, null, true)]
    public void Attack_policy_boundaries_match_crimewatch_and_safety(
        Eve.Universe.SecurityClass securityClass,
        bool targetIsCapsule,
        int safetyLevel,
        Flag expectedConsequence,
        int? expectedSafetyThreshold,
        bool expectedAllowed)
    {
        const long now = 1000;
        var facts = new PolicyFacts(
            AttackerIsPlayer: true,
            TargetIsPlayerProperty: true,
            TargetIsCapsule: targetIsCapsule);

        var decision = PolicyEngine.Evaluate(new PolicyRequest(
            PolicyAction.Attack, securityClass, facts, safetyLevel));

        Assert.Equal(expectedConsequence, decision.Consequence);
        Assert.Equal(expectedSafetyThreshold, decision.SafetyThreshold);
        Assert.Equal(expectedAllowed, decision.Allowed);
        Assert.Equal(expectedAllowed ? PolicyBlockReason.None : PolicyBlockReason.Safety, decision.BlockReason);

        var outcome = Aggression.Record(
            CombatState.Idle, CombatState.Idle, securityClass, 30000142,
            targetIsCapsule, facts, now);
        Assert.Equal(expectedConsequence, outcome.Attacker.FlagAt(now));
    }

    [Theory]
    [InlineData(Flag.Suspect, false)]
    [InlineData(Flag.Criminal, false)]
    [InlineData(Flag.None, true)]
    public void Null_security_remote_assistance_has_no_crime_consequence(Flag targetFlag, bool targetIsOutlaw)
    {
        var facts = new PolicyFacts(
            AttackerIsPlayer: true,
            TargetIsPlayerProperty: true,
            TargetFlag: targetFlag,
            TargetIsOutlaw: targetIsOutlaw);

        var decision = PolicyEngine.Evaluate(new PolicyRequest(
            PolicyAction.RemoteAssistance, Eve.Universe.SecurityClass.ZeroSec, facts));

        Assert.True(decision.Allowed);
        Assert.True(decision.Lawful);
        Assert.Equal(Flag.None, decision.Consequence);
        Assert.Null(decision.SafetyThreshold);
    }

    [Theory]
    [InlineData(PolicyAction.Dock)]
    [InlineData(PolicyAction.GateJump)]
    [InlineData(PolicyAction.CynoJump)]
    [InlineData(PolicyAction.Warp)]
    public void Criminal_timer_blocks_high_security_movement_except_for_capsules(PolicyAction action)
    {
        var facts = new PolicyFacts(HasCriminalTimer: true);

        var blocked = PolicyEngine.Evaluate(new PolicyRequest(
            action, Eve.Universe.SecurityClass.HighSec, facts));
        Assert.False(blocked.Allowed);
        Assert.Equal(PolicyBlockReason.CriminalTimer, blocked.BlockReason);

        Assert.True(PolicyEngine.Evaluate(new PolicyRequest(
            action, Eve.Universe.SecurityClass.HighSec, facts with { ActorIsCapsule = true })).Allowed);
        Assert.True(PolicyEngine.Evaluate(new PolicyRequest(
            action, Eve.Universe.SecurityClass.LowSec, facts)).Allowed);
        Assert.True(PolicyEngine.Evaluate(new PolicyRequest(
            action, Eve.Universe.SecurityClass.ZeroSec, facts)).Allowed);
    }
}
