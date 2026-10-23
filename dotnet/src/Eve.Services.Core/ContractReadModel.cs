namespace Eve.Services.Core;

public sealed record ContractNeedsAttentionRow(long ContractId, int State);

public sealed record ContractInProgressRow(long ContractId, long StartStationId, long EndStationId, long Expires);

public sealed record ContractAssignedToMeRow(long ContractId, long IssuerId);

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
