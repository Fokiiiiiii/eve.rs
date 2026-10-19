namespace Eve.Services.Core;

public sealed record ContractNeedsAttentionRow(long ContractId, int State);

public sealed record ContractInProgressRow(long ContractId, long StartStationId, long EndStationId, long Expires);

public sealed record ContractAssignedToMeRow(long ContractId, long IssuerId);

public sealed record ContractLoginInfo(
    IReadOnlyList<ContractNeedsAttentionRow> NeedsAttention,
    IReadOnlyList<ContractInProgressRow> InProgress,
    IReadOnlyList<ContractAssignedToMeRow> AssignedToMe);

public sealed record ContractEscrowSummary(decimal IskEscrow, long ItemsEscrow);

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
}
