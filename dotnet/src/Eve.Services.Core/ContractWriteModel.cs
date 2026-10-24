namespace Eve.Services.Core;

public sealed record ContractOfferedItem(long ItemId, long Quantity);

public sealed record CreateItemExchangeRequest(
    long IssuerCharacterId,
    long IssuerCorporationId,
    bool IsCorporationIssued,
    long StartStationId,
    int StartStationDivision,
    int Availability,
    long AssigneeId,
    int ExpiryMinutes,
    decimal Price,
    decimal Reward,
    decimal BrokerFee,
    decimal Deposit,
    decimal SalesTax,
    int WalletAccountKey,
    string Title,
    string Description,
    IReadOnlyList<ContractOfferedItem> Items,
    int OutstandingLimit,
    short ContractType = 1,
    long EndStationId = 0,
    decimal Collateral = 0,
    int NumDays = 0,
    IReadOnlyList<ContractRequestedItem>? RequestedItems = null);

/// <summary>A type the acceptor must supply (requestItemTypeList entry); stored as terms, not escrowed.</summary>
public sealed record ContractRequestedItem(int TypeId, long Quantity);

/// <summary>A client-visible contract refusal; the RPC layer maps it to a user error with the same key.</summary>
public sealed class ContractUserErrorException(string messageKey, params (string Name, Eve.Marshal.PyValue Value)[] values) : Exception(messageKey)
{
    public IReadOnlyList<(string Name, Eve.Marshal.PyValue Value)> Values { get; } = values;
}

public sealed record CreatedContract(long ContractId, IReadOnlyList<long> EscrowItemIds);

public sealed record AcceptedContract(long ContractId, short Type, short Status, long IssuerId, long IssuerCorporationId,
    bool IsCorporationIssued, long AcceptorId, long StartStationId, long EndStationId, long DateAcceptedFileTime, int NumDays);

public interface IContractWriteModel
{
    ValueTask<CreatedContract> CreateContractAsync(
        CreateItemExchangeRequest request,
        CancellationToken cancellationToken);

    ValueTask<AcceptedContract> AcceptContractAsync(long contractId, long characterId, long corporationId,
        long allianceId, bool acceptForCorporation, int acceptorWalletKey, long currentStationId,
        CancellationToken cancellationToken);

    ValueTask<AcceptedContract> AcceptItemExchangeAsync(long contractId, long characterId, long corporationId,
        long allianceId, bool acceptForCorporation, int acceptorWalletKey, CancellationToken cancellationToken);

    ValueTask<bool> CompleteContractAsync(long contractId, int targetStatus, long characterId, long corporationId,
        long currentStationId, bool hasCorpContractManager, CancellationToken cancellationToken);

    ValueTask<bool> DeleteContractAsync(long contractId, long characterId, long corporationId,
        bool hasCorpContractManager, CancellationToken cancellationToken);

    ValueTask<bool> PlaceBidAsync(long contractId, long characterId, long corporationId, long allianceId,
        bool bidForCorporation, int bidderWalletKey, decimal amount, CancellationToken cancellationToken);

    ValueTask<bool> SplitStackAsync(long itemId, long quantity, long characterId, long corporationId,
        long stationId, bool forCorporation, int flag, CancellationToken cancellationToken);

    ValueTask<CreatedContract> CreateItemExchangeAsync(
        CreateItemExchangeRequest request,
        CancellationToken cancellationToken) => CreateContractAsync(request, cancellationToken);
}

public static class ContractCreationRules
{
    private static readonly int[] ItemExchangeExpiryOptions = [1440, 4320, 10080, 20160, 40320];

    public static void ValidateItemExchangeTerms(CreateItemExchangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.IssuerCharacterId > 0, nameof(request.IssuerCharacterId));
        Require(request.IssuerCorporationId > 0, nameof(request.IssuerCorporationId));
        Require(request.StartStationId > 0, nameof(request.StartStationId));
        Require(request.ContractType is >= 1 and <= 3, nameof(request.ContractType));
        Require(request.EndStationId >= 0, nameof(request.EndStationId));
        Require(request.Collateral >= 0, nameof(request.Collateral));
        Require(request.NumDays is >= 0 and <= 28, nameof(request.NumDays));
        Require(request.Availability is >= 0 and <= 3, nameof(request.Availability));
        Require(request.AssigneeId >= 0, nameof(request.AssigneeId));
        Require(ItemExchangeExpiryOptions.Contains(request.ExpiryMinutes), nameof(request.ExpiryMinutes));
        Require(request.Price >= 0, nameof(request.Price));
        Require(request.Reward >= 0, nameof(request.Reward));
        Require(request.BrokerFee >= 0, nameof(request.BrokerFee));
        Require(request.Deposit >= 0, nameof(request.Deposit));
        Require(request.SalesTax >= 0, nameof(request.SalesTax));
        Require(request.WalletAccountKey is >= 1000 and <= 1006, nameof(request.WalletAccountKey));
        Require(!string.IsNullOrWhiteSpace(request.Title), nameof(request.Title));
        Require(request.Title.Length <= 50, nameof(request.Title));
        Require(request.Description is not null && request.Description.Length <= 4000, nameof(request.Description));
        var items = request.Items ?? throw new ArgumentOutOfRangeException(nameof(request.Items));
        Require(items.Count is > 0 and <= 500, nameof(request.Items));
        Require(items.All(item => item is not null && item.ItemId > 0 && item.Quantity > 0), nameof(request.Items));
        Require(items.Select(item => item.ItemId).Distinct().Count() == items.Count, nameof(request.Items));
        Require(new[] { request.Price, request.Reward, request.BrokerFee, request.Deposit, request.SalesTax }
            .All(amount => decimal.Round(amount, 2) == amount), nameof(request.Price));
        Require(decimal.Round(request.Collateral, 2) == request.Collateral, nameof(request.Collateral));
        Require(request.OutstandingLimit is >= 1 and <= 500, nameof(request.OutstandingLimit));
        Require(request.RequestedItems is null || request.RequestedItems.Count <= 500, nameof(request.RequestedItems));
        Require(request.RequestedItems is null ||
            request.RequestedItems.All(item => item is not null && item.TypeId > 0 && item.Quantity > 0), nameof(request.RequestedItems));
        Require(request.RequestedItems is null ||
            request.RequestedItems.Select(item => item.TypeId).Distinct().Count() == request.RequestedItems.Count, nameof(request.RequestedItems));
        if (request.ContractType == 3
            ? request.EndStationId <= 0 || request.NumDays <= 0
            : request.EndStationId != 0 || request.NumDays != 0)
            throw new ArgumentException("only courier contracts require a destination and duration", nameof(request.EndStationId));
        if (request.Availability == 0 && request.AssigneeId != 0 || request.Availability != 0 && request.AssigneeId == 0)
            throw new ArgumentException("public contracts have no assignee and restricted contracts require one", nameof(request.AssigneeId));
        if (request.IsCorporationIssued && request.StartStationDivision is < 115 or > 121 ||
            !request.IsCorporationIssued && request.StartStationDivision != 4)
            throw new ArgumentException("the selected division does not match the issuer", nameof(request.StartStationDivision));
    }

    private static void Require(bool valid, string parameterName)
    {
        if (!valid)
            throw new ArgumentOutOfRangeException(parameterName, "contract creation terms are outside supported limits");
    }
}
