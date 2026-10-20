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
    short ContractType = 1,
    long EndStationId = 0,
    decimal Collateral = 0,
    int NumDays = 0);

public sealed record CreatedContract(long ContractId, IReadOnlyList<long> EscrowItemIds);

public interface IContractWriteModel
{
    ValueTask<CreatedContract> CreateContractAsync(
        CreateItemExchangeRequest request,
        CancellationToken cancellationToken);

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
        if (request.IssuerCharacterId <= 0 || request.IssuerCorporationId <= 0 || request.StartStationId <= 0 ||
            request.ContractType is < 1 or > 3 || request.EndStationId < 0 || request.Collateral < 0 || request.NumDays is < 0 or > 28 ||
            request.Availability is < 0 or > 3 || request.AssigneeId < 0 ||
            !ItemExchangeExpiryOptions.Contains(request.ExpiryMinutes) ||
            request.Price < 0 || request.Reward < 0 || request.Collateral < 0 || request.BrokerFee < 0 || request.Deposit < 0 || request.SalesTax < 0 ||
            request.WalletAccountKey is < 1000 or > 1006 || string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Length > 50 || request.Description.Length > 4000 || request.Items is null ||
            request.Items.Count is 0 or > 500 || request.Items.Any(item => item.ItemId <= 0 || item.Quantity <= 0) ||
            request.Items.Select(item => item.ItemId).Distinct().Count() != request.Items.Count ||
            new[] { request.Price, request.Reward, request.BrokerFee, request.Deposit, request.SalesTax }
            .Any(amount => decimal.Round(amount, 2) != amount) || decimal.Round(request.Collateral, 2) != request.Collateral)
            throw new ArgumentOutOfRangeException(nameof(request), "contract creation terms are outside supported limits");
        if (request.ContractType == 3 ? request.EndStationId <= 0 || request.NumDays <= 0 : request.EndStationId != 0 || request.NumDays != 0)
            throw new ArgumentException("only courier contracts require a destination and duration", nameof(request));
        if (request.Availability == 0 && request.AssigneeId != 0 || request.Availability != 0 && request.AssigneeId == 0)
            throw new ArgumentException("public contracts have no assignee and restricted contracts require one", nameof(request));
        if (request.IsCorporationIssued && request.StartStationDivision is < 115 or > 121 ||
            !request.IsCorporationIssued && request.StartStationDivision != 4)
            throw new ArgumentException("the selected division does not match the issuer", nameof(request));
    }
}
