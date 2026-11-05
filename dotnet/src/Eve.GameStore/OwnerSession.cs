namespace Eve.GameStore;

/// <summary>
/// Holds a persistence role for the life of a process: acquires it, keeps the
/// lease renewed in the background, and reports loss. After the lease is lost
/// (renewal failed, or a successor took the role) <see cref="AssertHealthy"/>
/// throws, so a stale process stops mutating rather than racing its successor.
/// </summary>
public sealed class OwnerSession : IAsyncDisposable
{
    private readonly PersistenceStore _store;
    private readonly long _leaseMs;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _renewal;
    private volatile OwnerFence _fence;
    private volatile Exception? _lost;

    private OwnerSession(PersistenceStore store, OwnerFence fence, long leaseMs, TimeSpan renewEvery)
    {
        _store = store;
        _fence = fence;
        _leaseMs = leaseMs;
        _renewal = RenewLoopAsync(renewEvery);
    }

    public OwnerFence Fence => _fence;

    public bool IsLost => _lost is not null;

    /// <summary>Raised once, when the lease is lost.</summary>
    public event Action<Exception>? Lost;

    public static async Task<OwnerSession> StartAsync(
        PersistenceStore store,
        string ownerRole,
        string instanceId,
        AcquireOptions? options = null,
        TimeSpan? renewEvery = null,
        CancellationToken ct = default)
    {
        options ??= new AcquireOptions();
        var fence = await store.AcquireOwnerAsync(ownerRole, instanceId, options, ct);
        var leaseMs = options.LeaseMs ?? PersistenceStore.DefaultLeaseMs;
        // Renew at a third of the lease, so two attempts can fail before it lapses.
        return new OwnerSession(store, fence, leaseMs, renewEvery ?? TimeSpan.FromMilliseconds(Math.Max(250, leaseMs / 3)));
    }

    public void AssertHealthy()
    {
        if (_lost is { } cause)
        {
            throw new PersistenceException(
                $"persistence owner {_fence.OwnerRole} epoch {_fence.Epoch} was lost: {cause.Message}",
                PersistenceCodes.OwnerStale,
                inner: cause);
        }
    }

    private async Task RenewLoopAsync(TimeSpan every)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(every, _stop.Token);
                _fence = await _store.RenewOwnerAsync(_fence, _leaseMs, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _lost = ex;
            Lost?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _renewal;
        if (_lost is null)
        {
            try
            {
                await _store.ReleaseOwnerAsync(_fence);
            }
            catch (PersistenceException)
            {
                // Already superseded: nothing left to release.
            }
        }
        _stop.Dispose();
    }
}
