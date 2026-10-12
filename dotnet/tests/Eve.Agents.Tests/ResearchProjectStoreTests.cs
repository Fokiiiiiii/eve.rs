using Eve.Data;
using Npgsql;

namespace Eve.Agents.Tests;

/// <summary>Research projects in PostgreSQL: the slot limit, the one project per agent, the accrual, persistence and the cancel.</summary>
public class ResearchProjectStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const long Character = 140000031;
    private const long AgentA = 3009318;
    private const long AgentB = 3009320;
    private const long Skill = 3380;

    [PgFact]
    public async Task A_project_accrues_its_remainder_and_the_rate_and_survives_a_restart()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "rsp_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await ResearchProjectStore.MigrateAsync(source, schema);
            var store = new ResearchProjectStore(source, schema);
            await store.StartAsync(new ResearchProject(Character, AgentA, Skill, Start, 24.0, 0), maxConcurrent: 1);

            var restarted = new ResearchProjectStore(source, schema);
            var project = Assert.IsType<ResearchProject>(await restarted.ReadAsync(Character, AgentA));
            Assert.Equal((Skill, 24.0, Start), (project.SkillTypeId, project.PointsPerDay, project.StartedAt));
            Assert.Equal(48.0, ResearchProjectStore.AccruedPoints(project, Start.AddDays(2)), 9);
            Assert.Equal(0.0, ResearchProjectStore.AccruedPoints(project, Start), 9);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task A_start_over_the_slot_limit_or_with_the_same_agent_is_refused_and_writes_nothing()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "rsp_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await ResearchProjectStore.MigrateAsync(source, schema);
            var store = new ResearchProjectStore(source, schema);
            await store.StartAsync(new ResearchProject(Character, AgentA, Skill, Start, 10.0, 0), maxConcurrent: 1);

            var limit = await Assert.ThrowsAsync<ResearchRefusedException>(() =>
                store.StartAsync(new ResearchProject(Character, AgentB, Skill, Start, 10.0, 0), maxConcurrent: 1));
            Assert.Equal("too_many_projects", limit.Reason);

            var same = await Assert.ThrowsAsync<ResearchRefusedException>(() =>
                store.StartAsync(new ResearchProject(Character, AgentA, Skill, Start, 10.0, 0), maxConcurrent: 5));
            Assert.Equal("already_researching", same.Reason);

            var rows = await store.ReadAllAsync(Character);
            Assert.Equal(new[] { AgentA }, rows.Select(row => row.AgentId).ToArray());
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Cancelling_deletes_the_project_and_its_points_are_gone()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "rsp_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await ResearchProjectStore.MigrateAsync(source, schema);
            var store = new ResearchProjectStore(source, schema);
            await store.StartAsync(new ResearchProject(Character, AgentA, Skill, Start, 10.0, 0), maxConcurrent: 1);

            Assert.True(await store.CancelAsync(Character, AgentA));
            Assert.False(await store.CancelAsync(Character, AgentA));
            Assert.Null(await store.ReadAsync(Character, AgentA));
            // A new start is allowed again, from zero points.
            var again = await store.StartAsync(new ResearchProject(Character, AgentA, Skill, Start.AddDays(1), 10.0, 0), maxConcurrent: 1);
            Assert.Equal(0.0, again.RemainderPoints, 9);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
