using Eve.Market;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Market;

/// <summary>The order's expiry job in the host's durable scheduler (one job per resting order; <see cref="MarketExpiryWorker"/> runs it).</summary>
public sealed class DurableExpiryJobs(DurableScheduler scheduler) : IMarketExpiryJobs
{
    public async Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long orderId, long dueMs, CancellationToken token)
    {
        await scheduler.ScheduleAsync(connection, transaction, new JobSpec(MarketExpiryWorker.OrderKind, dueMs)
        {
            Key = MarketExpiryWorker.OrderKey(orderId),
            Payload = new MarketExpiryWorker.OrderJob(orderId),
            MaxAttempts = MarketExpiry.MaxAttempts,
        }, token);
    }

    public async Task CancelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long orderId, CancellationToken token)
    {
        await scheduler.CancelAsync(connection, transaction, MarketExpiryWorker.OrderKind, MarketExpiryWorker.OrderKey(orderId), token);
    }
}
