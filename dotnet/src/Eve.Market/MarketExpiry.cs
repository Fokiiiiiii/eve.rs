using Npgsql;

namespace Eve.Market;

/// <summary>

/// tries that job gets. The jobs themselves are the host's (Eve.Services.Market); the market only keeps or cancels them, in its own transaction.
/// </summary>
public static class MarketExpiry
{
    /// <summary>The grace after the expiry instant (EVE.js marketProxyService.js:249 uses one second); the job and the sweep both wait for it.</summary>
    public const long GraceMs = 1_000;

    /// <summary>How many tries a failing expiry gets before the sweep is the only one left to close the order.</summary>
    public const int MaxAttempts = 100;
}

/// <summary>The order's expiry job, kept in the caller's transaction: it exists while the order is open, and is due at its expiry plus the grace.</summary>
public interface IMarketExpiryJobs
{
    /// <summary>Makes the order's job due at <paramref name="dueMs"/>, replacing an earlier one (a modification restarts the duration).</summary>
    Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long orderId, long dueMs, CancellationToken token);

    /// <summary>Removes the order's job when the order closes in the same transaction.</summary>
    Task CancelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long orderId, CancellationToken token);
}
