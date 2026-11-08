using Eve.Scheduler;
using Npgsql;

namespace Eve.Events.Tests;

public sealed class EventBusOptionsTests
{
    public static IEnumerable<object[]> InvalidOptions()
    {
        var defaults = new EventBusOptions();
        yield return [defaults with { MaxAttempts = 0 }, nameof(EventBusOptions.MaxAttempts)];
        yield return [defaults with { PollMs = 4 }, nameof(EventBusOptions.PollMs)];
        yield return [defaults with { BatchPerPump = 0 }, nameof(EventBusOptions.BatchPerPump)];
        yield return [defaults with { BackoffBaseMs = 0 }, nameof(EventBusOptions.BackoffBaseMs)];
        yield return [defaults with { BackoffMaxMs = 0 }, nameof(EventBusOptions.BackoffMaxMs)];
        yield return [defaults with { BackoffBaseMs = 20, BackoffMaxMs = 10 }, nameof(EventBusOptions.BackoffMaxMs)];
        yield return [defaults with { RetentionMs = 0 }, nameof(EventBusOptions.RetentionMs)];
        yield return [defaults with { HardRetentionMs = 0 }, nameof(EventBusOptions.HardRetentionMs)];
        yield return [defaults with { RetentionMs = 2_000, HardRetentionMs = 1_000 }, nameof(EventBusOptions.HardRetentionMs)];
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task Constructor_rejects_invalid_options_before_registering_its_scheduler_handler(
        EventBusOptions options,
        string parameterName)
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Username=unused;Database=unused");
        await using var scheduler = new DurableScheduler(source, () => 1_000, schema: "sch_" + Guid.NewGuid().ToString("N")[..12]);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EventBus(source, scheduler, options, "evt_" + Guid.NewGuid().ToString("N")[..12]));

        Assert.Equal(parameterName, error.ParamName);
        Assert.Empty(scheduler.Kinds);
    }
}
