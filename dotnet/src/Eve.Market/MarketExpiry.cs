using Eve.Scheduler;

namespace Eve.Market;

/// <summary>
/// Expiry of resting orders as durable jobs (M3; market-decisions Q7). A resting order has one job, due at its expiry plus a grace
/// (EVE.js marketProxyService.js:249 uses one second), scheduled in the transaction that places or moves the order and cancelled in the
/// one that closes it, so the job exists while the order is open. A safety sweep every minute closes what is due, and a read-triggered
/// sweep (at most once a second) does the same when a client reads the market (EVE.js marketProxyService.js:248). A restart makes sure every
/// open order has its job again, so the expiry resumes; an overdue job runs at once.
/// </summary>
public sealed class MarketExpiry
{
    public const string OrderKind = "market.order.expire";
    public const string SweepKind = "market.expiry.sweep";
    public const long GraceMs = 1_000;
    public const long SweepEveryMs = 60_000;
    public const long ReadSweepThrottleMs = 1_000;
    /// <summary>A failing expiry is tried this many times (with the scheduler's back-off) before the sweep is the only one left to close it.</summary>
    public const int MaxAttempts = 100;

    /// <summary>The payload of an order's job.</summary>
    public sealed record OrderJob(long OrderId);

    public static string OrderKey(long orderId) => $"order:{orderId}";

    private readonly DurableScheduler _scheduler;
    private readonly MarketTrading _trading;
    private readonly Func<long> _now;
    private readonly Func<MarketReceipt, CancellationToken, Task> _announce;
    private readonly SemaphoreSlim _sweeps = new(1, 1);
    private long? _lastSweepMs;

    public MarketExpiry(DurableScheduler scheduler, MarketTrading trading, Func<long> now, Func<MarketReceipt, CancellationToken, Task> announce)
    {
        _scheduler = scheduler;
        _trading = trading;
        _now = now;
        _announce = announce;
    }

    /// <summary>
    /// Registers the two job kinds, schedules the minute sweep and gives every open resting order its job (a restart resumes the expiry).
    /// Call once, when the host composes the market; the scheduler then runs the jobs.
    /// </summary>
    public async Task StartAsync(CancellationToken token = default)
    {
        _scheduler.Register(OrderKind, ExpireOrderJobAsync);
        _scheduler.Register(SweepKind, async (_, cancellation) => await SweepAsync(cancellation));
        await _scheduler.EnsureRecurringAsync(SweepKind, "sweep", TimeSpan.FromMilliseconds(SweepEveryMs), null, token);
        foreach (var (orderId, expiresMs) in await _trading.OpenRestingExpiriesAsync(token))
            await _scheduler.ScheduleAsync(new JobSpec(OrderKind, expiresMs + GraceMs)
            {
                Key = OrderKey(orderId),
                Payload = new OrderJob(orderId),
                OnDuplicate = OnDuplicate.Keep,
                MaxAttempts = MaxAttempts,
            }, token);
    }

    /// <summary>
    /// Closes every resting order whose duration has run out and announces each closure once. A read-triggered sweep does nothing when the
    /// previous sweep ran less than a second before; sweeps never overlap in this process.
    /// </summary>
    public async Task<IReadOnlyList<MarketReceipt>> SweepAsync(CancellationToken token = default, bool readTriggered = false)
    {
        await _sweeps.WaitAsync(token);
        try
        {
            var now = _now();
            if (readTriggered && _lastSweepMs is { } last && now - last < ReadSweepThrottleMs) return [];
            _lastSweepMs = now;
            var receipts = await _trading.ExpireDueAsync(token);
            foreach (var receipt in receipts) await _announce(receipt, token);
            return receipts;
        }
        finally
        {
            _sweeps.Release();
        }
    }

    private async Task ExpireOrderJobAsync(JobContext job, CancellationToken token)
    {
        var orderId = job.Payload<OrderJob>()?.OrderId ?? throw new InvalidDataException("a market expiry job names no order");
        var state = await _trading.ExpiryStateAsync(orderId, token);
        if (state is null || state.Value.State != 0) return; // cancelled, filled out or expired already: nothing is left to expire
        if (state.Value.ExpiresMs + GraceMs > _now())
        {
            job.RunAgainAt(state.Value.ExpiresMs + GraceMs); // a modification restarted the duration after this job was made
            return;
        }
        var receipt = await _trading.ExpireOrderAsync(orderId, token);
        if (receipt is not null) await _announce(receipt, token);
    }
}
