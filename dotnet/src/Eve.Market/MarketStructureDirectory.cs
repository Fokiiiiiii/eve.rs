namespace Eve.Market;

/// <summary>The SDE-backed location and owner facts for an Upwell market.</summary>
public sealed record MarketStructure(
    long Id,
    int TypeId,
    long OwnerCorporationId,
    MarketLocation Location,
    string Name);

/// <summary>Reads persisted Upwell market eligibility, access profiles, and the caller's configured MARKET_TAX percentage.</summary>
public interface IMarketStructureDirectory
{
    ValueTask<MarketStructure?> GetAsync(long structureId, CancellationToken cancellationToken);
    ValueTask<bool> CanAccessMarketAsync(long characterId, long structureId, CancellationToken cancellationToken);
    ValueTask<double?> BrokerTaxPercentAsync(long characterId, long structureId, CancellationToken cancellationToken);
}