using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class ContractWriteModelTests
{
    [Theory]
    [InlineData(1440)]
    [InlineData(4320)]
    [InlineData(10080)]
    [InlineData(20160)]
    [InlineData(40320)]
    public void Item_exchange_terms_accept_each_client_expiry_option(int expiryMinutes) =>
        ContractCreationRules.ValidateItemExchangeTerms(Request(expiryMinutes: expiryMinutes));

    [Theory]
    [InlineData(2880)]
    [InlineData(1441)]
    public void Item_exchange_terms_reject_expiry_values_the_client_does_not_offer(int expiryMinutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContractCreationRules.ValidateItemExchangeTerms(Request(expiryMinutes: expiryMinutes)));

    [Fact]
    public void Item_exchange_description_uses_the_client_four_thousand_character_limit()
    {
        ContractCreationRules.ValidateItemExchangeTerms(Request(description: new string('x', 4000)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContractCreationRules.ValidateItemExchangeTerms(Request(description: new string('x', 4001))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContractCreationRules.ValidateItemExchangeTerms(Request(title: new string('x', 51))));
    }

    [Fact]
    public void Item_exchange_terms_require_a_unique_positive_item_list_and_cents()
    {
        var duplicate = Request(items: [new ContractOfferedItem(20, 1), new ContractOfferedItem(20, 2)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractCreationRules.ValidateItemExchangeTerms(duplicate));

        var fractions = Request(price: 1.001m);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractCreationRules.ValidateItemExchangeTerms(fractions));
    }

    [Fact]
    public void Item_exchange_availability_requires_an_assignee_only_for_restricted_contracts()
    {
        Assert.Throws<ArgumentException>(() =>
            ContractCreationRules.ValidateItemExchangeTerms(Request(availability: 0, assigneeId: 300)));
        Assert.Throws<ArgumentException>(() =>
            ContractCreationRules.ValidateItemExchangeTerms(Request(availability: 1, assigneeId: 0)));
        ContractCreationRules.ValidateItemExchangeTerms(Request(availability: 1, assigneeId: 300));
    }

    [Fact]
    public void Contract_creation_accepts_auction_terms_and_requires_courier_endpoints_and_duration()
    {
        ContractCreationRules.ValidateItemExchangeTerms(Request(contractType: 2));
        ContractCreationRules.ValidateItemExchangeTerms(Request(contractType: 3, endStationId: 700, numDays: 7));
        Assert.Throws<ArgumentException>(() =>
            ContractCreationRules.ValidateItemExchangeTerms(Request(contractType: 3)));
        Assert.Throws<ArgumentException>(() =>
            ContractCreationRules.ValidateItemExchangeTerms(Request(contractType: 2, endStationId: 700)));
    }

    [Fact]
    public void Outstanding_limit_must_be_within_the_client_maximum()
    {
        ContractCreationRules.ValidateItemExchangeTerms(Request(outstandingLimit: 500));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractCreationRules.ValidateItemExchangeTerms(Request(outstandingLimit: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractCreationRules.ValidateItemExchangeTerms(Request(outstandingLimit: 501)));
    }

    [Fact]
    public void Contract_limits_follow_the_client_skill_tables()
    {
        Assert.Equal(28, ContractCreationLimits.Personal(0, 0));
        Assert.Equal(46, ContractCreationLimits.Personal(5, 5));
        Assert.Equal(5, ContractCreationLimits.Corporation(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractCreationLimits.Personal(6, 0));
    }

    [Fact]
    public void Contract_creation_validation_identifies_the_rejected_field()
    {
        var request = Request() with { IssuerCorporationId = 0 };
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContractCreationRules.ValidateItemExchangeTerms(request));
        Assert.Equal(nameof(CreateItemExchangeRequest.IssuerCorporationId), error.ParamName);
    }

    private static CreateItemExchangeRequest Request(
        int expiryMinutes = 1440,
        int availability = 0,
        long assigneeId = 0,
        decimal price = 5,
        short contractType = 1,
        long endStationId = 0,
        int numDays = 0,
        IReadOnlyList<ContractOfferedItem>? items = null,
        string? title = null,
        string? description = null,
        int outstandingLimit = 500) => new(
        IssuerCharacterId: 100,
        IssuerCorporationId: 200,
        IsCorporationIssued: false,
        StartStationId: 600,
        StartStationDivision: 4,
        Availability: availability,
        AssigneeId: assigneeId,
        ExpiryMinutes: expiryMinutes,
        Price: price,
        Reward: 0,
        BrokerFee: 0,
        Deposit: 0,
        SalesTax: 0,
        WalletAccountKey: 1000,
        Title: title ?? "Exchange",
        Description: description ?? "",
        Items: items ?? [new ContractOfferedItem(10, 1)],
        OutstandingLimit: outstandingLimit,
        ContractType: contractType,
        EndStationId: endStationId,
        Collateral: 0,
        NumDays: numDays);
}
