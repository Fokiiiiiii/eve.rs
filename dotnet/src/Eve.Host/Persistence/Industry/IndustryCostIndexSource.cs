namespace Eve.Host;

/// <summary>
/// The system cost index of an industry activity (a fraction). A stored ESI index answers first; a system or activity with
/// none uses <see cref="Fallback"/>, the configured project rule (EVE_INDUSTRY_COST_INDEX).
/// </summary>
public interface IIndustryCostIndexSource
{
    decimal Fallback { get; }

    ValueTask<decimal> GetAsync(long solarSystemId, int activityId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyDictionary<long, decimal>> GetActivityAsync(int activityId, CancellationToken cancellationToken);
}

/// <summary>The stored ESI indices (the market schema) over the configured fallback.</summary>
public sealed class StoredIndustryCostIndexes(Eve.Market.IndustryCostIndexCache cache, decimal fallback) : IIndustryCostIndexSource
{
    public decimal Fallback { get; } = fallback;

    public async ValueTask<decimal> GetAsync(long solarSystemId, int activityId, CancellationToken cancellationToken) =>
        await cache.GetAsync(solarSystemId, activityId, cancellationToken) is { } rate ? (decimal)rate : Fallback;

    public async ValueTask<IReadOnlyDictionary<long, decimal>> GetActivityAsync(int activityId, CancellationToken cancellationToken) =>
        (await cache.GetActivityAsync(activityId, cancellationToken)).ToDictionary(row => row.Key, row => (decimal)row.Value);
}
