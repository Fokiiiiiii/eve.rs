using Eve.Scheduler;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The durable readiness of installed jobs over PostgreSQL: the job becomes ready at its end (status 3), once; it survives a
/// restart; the startup sweep readies overdue jobs whose durable entry was lost; a waiting job keeps its entry.
/// </summary>
public sealed class IndustryReadyTests
{
    private const int Blueprint = 681;
    private const int ManufacturingSeconds = 600;

    private static async Task<(IndustryRig Rig, long Owner, long Job)> InstallAsync()
    {
        var rig = await IndustryRig.CreateAsync();
        var owner = await rig.Owner();
        var blueprint = await rig.Give(owner, Blueprint, 1, blueprint: true);
        await rig.Give(owner, 38, 86);
        var job = await rig.Install(owner, blueprint, IndustryRules.Manufacturing);
        return (rig, owner, job);
    }

    [Fact]
    public async Task The_job_becomes_ready_at_its_end_and_can_then_be_delivered()
    {
        var (rig, owner, job) = await InstallAsync();
        await using var _ = rig;

        rig.Now += (ManufacturingSeconds - 100) * 1000L;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(1, (await rig.JobRow(job)).Status);

        rig.Now += 101 * 1000L;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(IndustryReadyJobs.StatusReady, (await rig.JobRow(job)).Status);

        await rig.Complete(owner, job);
        Assert.Equal(100, (await rig.JobRow(job)).Status);
    }

    [Fact]
    public async Task Readiness_is_idempotent_a_second_run_changes_nothing()
    {
        var (rig, _, job) = await InstallAsync();
        await using var _ = rig;
        rig.Now += (ManufacturingSeconds + 1) * 1000L;

        Assert.True(await rig.ReadyJobs.MarkReadyAsync(job, default));
        Assert.False(await rig.ReadyJobs.MarkReadyAsync(job, default));
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(IndustryReadyJobs.StatusReady, (await rig.JobRow(job)).Status);
    }

    [Fact]
    public async Task The_durable_entry_survives_a_restart_and_readies_the_job_after_it()
    {
        var (rig, _, job) = await InstallAsync();
        await using var _ = rig;
        rig.Now += (ManufacturingSeconds + 1) * 1000L;

        await rig.RestartAndRunDueAsync();

        Assert.Equal(IndustryReadyJobs.StatusReady, (await rig.JobRow(job)).Status);
    }

    [Fact]
    public async Task The_startup_sweep_readies_an_overdue_job_whose_durable_entry_was_lost()
    {
        var (rig, _, job) = await InstallAsync();
        await using var _ = rig;
        Assert.True(await rig.Scheduler.CancelAsync(IndustryReadyJobs.ReadyKind, IndustryReadyJobs.KeyFor(job)));
        rig.Now += (ManufacturingSeconds + 1) * 1000L;

        Assert.Equal(1, await rig.ReadyJobs.SweepAsync(default));
        Assert.Equal(IndustryReadyJobs.StatusReady, (await rig.JobRow(job)).Status);
        Assert.Equal(0, await rig.ReadyJobs.SweepAsync(default));
    }

    [Fact]
    public async Task The_sweep_keeps_a_waiting_job_scheduled_and_does_not_ready_it_early()
    {
        var (rig, _, job) = await InstallAsync();
        await using var _ = rig;
        rig.Now += 60_000L;

        Assert.Equal(0, await rig.ReadyJobs.SweepAsync(default));
        Assert.Equal(1, (await rig.JobRow(job)).Status);
        Assert.NotNull(await rig.Scheduler.FindAsync(IndustryReadyJobs.ReadyKind, IndustryReadyJobs.KeyFor(job)));
    }
}
