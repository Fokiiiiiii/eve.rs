namespace Eve.Services.Core;

public sealed record ContractNeedsAttentionRow(long ContractId, int State);

public sealed record ContractInProgressRow(long ContractId, long StartStationId, long EndStationId, long Expires);

public sealed record ContractAssignedToMeRow(long ContractId, long IssuerId, long IssuerCorporationId = 0, long AssigneeId = 0, short Type = 0);

public sealed record ContractLoginInfo(
    IReadOnlyList<ContractNeedsAttentionRow> NeedsAttention,
    IReadOnlyList<ContractInProgressRow> InProgress,
    IReadOnlyList<ContractAssignedToMeRow> AssignedToMe);

public sealed record ContractEscrowSummary(decimal IskEscrow, long ItemsEscrow);

public sealed record ContractReadSnapshot(
    long ContractId,
    short Type,
    short Status,
    short Availability,
    long IssuerId,
    long IssuerCorporationId,
    bool IsCorporationIssued,
    long AssigneeId,
    long AcceptorId,
    long DateIssuedFileTime,
    long DateExpiredFileTime,
    long DateAcceptedFileTime,
    long DateCompletedFileTime,
    int NumDays,
    long StartStationId,
    long EndStationId,
    decimal Price,
    decimal Reward,
    decimal Collateral,
    string Title,
    string Description,
    IReadOnlyList<ContractReadItemSnapshot> Items,
    IReadOnlyList<ContractReadBidSnapshot> Bids);

public sealed record ContractReadItemSnapshot(long ItemId, int TypeId, long Quantity, bool InCrate, bool IsPackage);

public sealed record ContractReadBidSnapshot(long BidId, long BidderId, decimal Amount, long CreatedAtFileTime, bool IsCorporation);

public sealed record ContractReadBundle(IReadOnlyList<ContractReadSnapshot> Contracts);

public sealed record ContractSearchQuery(
    long CharacterId,
    long CorporationId,
    long AllianceId,
    int? Availability,
    int ContractType,
    long LocationId,
    long EndLocationId,
    long IssuerId,
    decimal? MinPrice,
    decimal? MaxPrice,
    decimal? MinReward,
    decimal? MaxReward,
    decimal? MinCollateral,
    decimal? MaxCollateral,
    decimal? MinVolume,
    decimal? MaxVolume,
    bool ExcludeTrade,
    bool ExcludeMultiple,
    bool ExcludeNoBuyout,
    string? Description,
    IReadOnlyList<int> ItemTypeIds,
    int ItemCategoryId,
    int ItemGroupId,
    string? ItemTypeName,
    IReadOnlyList<int> SecurityClasses,
    int SearchHint,
    int SortBy,
    int? SortDirection,
    bool HasExplicitSortBy,
    int StartNum);

public sealed record ContractSearchEntry(
    ContractReadSnapshot Contract,
    long StartSolarSystemId,
    long StartConstellationId,
    long StartRegionId,
    long EndSolarSystemId,
    long EndConstellationId,
    long EndRegionId,
    string StartSolarSystemName,
    string EndSolarSystemName,
    int StartStationDivision,
    long? IssuerWalletKey,
    long? AcceptorWalletKey,
    double Volume,
    IReadOnlyList<int> ItemTypeIds,
    IReadOnlyList<int> ItemGroupIds,
    IReadOnlyList<int> ItemCategoryIds);

public sealed record ContractSearchResult(IReadOnlyList<ContractSearchEntry> Contracts, int NumFound, long SearchTimeFileTime);

public interface IContractReadModel
{
    ValueTask<ContractLoginInfo> GetLoginInfoAsync(
        long characterId,
        long corporationId,
        long allianceId,
        long nowFileTime,
        CancellationToken cancellationToken);

    ValueTask<ContractEscrowSummary> GetMyContractEscrowAsync(
        long characterId,
        long corporationId,
        CancellationToken cancellationToken);

    ValueTask<ContractReadBundle> GetMyBidsAsync(
        long characterId,
        long corporationId,
        bool forCorporation,
        long nowFileTime,
        CancellationToken cancellationToken);

    ValueTask<ContractReadBundle> GetContractListForOwnerAsync(
        long characterId,
        long corporationId,
        long ownerId,
        int status,
        int contractType,
        long issuedBy,
        int pageSize,
        long? startContractId,
        long nowFileTime,
        CancellationToken cancellationToken);

    ValueTask<ContractSearchResult> SearchContractsAsync(
        ContractSearchQuery query,
        long nowFileTime,
        CancellationToken cancellationToken);

    ValueTask<ContractReadBundle> GetMyCurrentContractListAsync(
        long characterId,
        long corporationId,
        bool isAccepted,
        bool forCorporation,
        long nowFileTime,
        CancellationToken cancellationToken);

    ValueTask<ContractReadBundle> GetMyExpiredContractListAsync(
        long characterId,
        long corporationId,
        bool forCorporation,
        long nowFileTime,
        CancellationToken cancellationToken);
}
