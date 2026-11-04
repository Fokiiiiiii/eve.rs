using Eve.Market;
using Eve.Scheduler;

namespace Eve.Services.Market;

public sealed class MarketExpiryWorker
{
    public const string OrderKind = "market.order.expire";
    public const string SweepKind = "market.expiry.sweep";
    public const long SweepEveryMs = 60_000;
    public const long ReadSweepThrottleMs = 1_000;
    /// <summary>How often the minute sweep also runs the market's reconciliation (M8); a read-triggered sweep never does.</summary>
    public const long ReconcileEveryMs = 600_000;

    /// <summary>The payload of an order's job.</summary>
    public sealed record OrderJob(long OrderId);

    public static string OrderKey(long orderId) => $"order:{orderId}";

    private readonly DurableScheduler _scheduler;
    private readonly MarketTrading _trading;
    private readonly Func<long> _now;
    private readonly Func<MarketReceipt, CancellationToken, Task> _announce;
    private readonly SemaphoreSlim _sweeps = new(1, 1);
    private readonly MarketReconciliation? _reconciliation;
    private readonly Action<string>? _onMismatch;
    private long? _lastSweepMs;
    private long? _lastReconcileMs;

    /// <param name="reconciliation">The invariant queries the minute sweep runs (M8); null runs none.</param>
    /// <param name="onMismatch">Receives one line per broken invariant (the host writes them to its log).</param>
    public MarketExpiryWorker(DurableScheduler scheduler, MarketTrading trading, Func<long> now, Func<MarketReceipt, CancellationToken, Task> announce,
        MarketReconciliation? reconciliation = null, Action<string>? onMismatch = null)
    {
        _scheduler = scheduler;
        _trading = trading;
        _now = now;
        _announce = announce;
        _reconciliation = reconciliation;
        _onMismatch = onMismatch;
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
            await _scheduler.ScheduleAsync(new JobSpec(OrderKind, expiresMs + MarketExpiry.GraceMs)
            {
                Key = OrderKey(orderId),
                Payload = new OrderJob(orderId),
                OnDuplicate = OnDuplicate.Keep,
                MaxAttempts = MarketExpiry.MaxAttempts,
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
            if (!readTriggered) await ReconcileAsync(now, token);
            return receipts;
        }
        finally
        {
            _sweeps.Release();
        }
    }

    /// <summary>Runs the invariants at most every <see cref="ReconcileEveryMs"/> and logs each broken one (M8).</summary>
    private async Task ReconcileAsync(long now, CancellationToken token)
    {
        if (_reconciliation is null || _lastReconcileMs is { } last && now - last < ReconcileEveryMs) return;
        _lastReconcileMs = now;
        foreach (var problem in await _reconciliation.RunAsync(token))
            _onMismatch?.Invoke($"market reconciliation: {problem}");
    }

    private async Task ExpireOrderJobAsync(JobContext job, CancellationToken token)
    {
        var orderId = job.Payload<OrderJob>()?.OrderId ?? throw new InvalidDataException("a market expiry job names no order");
        var state = await _trading.ExpiryStateAsync(orderId, token);
        if (state is null || state.Value.State != 0) return; // cancelled, filled out or expired already: nothing is left to expire
        if (state.Value.ExpiresMs + MarketExpiry.GraceMs > _now())
        {
            job.RunAgainAt(state.Value.ExpiresMs + MarketExpiry.GraceMs); // a modification restarted the duration after this job was made
            return;
        }
        var receipt = await _trading.ExpireOrderAsync(orderId, token);
        if (receipt is not null) await _announce(receipt, token);
    }
}
