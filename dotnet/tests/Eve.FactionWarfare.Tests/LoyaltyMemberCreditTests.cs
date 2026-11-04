using Eve.Wallet;

namespace Eve.FactionWarfare.Tests;

public sealed class LoyaltyMemberCreditTests
{
    private const long Character = 92_000_001;
    private const long Corporation = 98_600_001;
    private const long Issuer = 1000179;

    [Theory]
    [InlineData(1000, 0.1, 5, 100)]
    [InlineData(1000, 10, 5, 100)]
    [InlineData(1000, 0, 5, 0)]
    [InlineData(7, 0.5, 5, 3)]
    [InlineData(1000, 0.1, 0, 0)]
    [InlineData(1000, -1, 5, 0)]
    public void The_tax_is_the_rate_of_the_amount_rounded_down_and_none_without_a_corporation(long amount, double rate, long corporation, long tax) =>
        Assert.Equal(tax, LoyaltyTax.TaxOf(amount, (decimal)rate, corporation));

    [PgFact]
    public async Task A_member_credit_gives_the_corporation_its_tax_and_the_member_the_rest()
    {
        await using var rig = await FwRig.CreateAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var credit = await rig.Loyalty.CreditMemberAsync(connection, transaction, Character, Corporation, 10m, Issuer, 1000, "test.credit", "member-credit:1");
            Assert.Equal(new LoyaltyMemberCredit(900, 100), credit);
            await transaction.CommitAsync();
        }
        Assert.Equal(900, await rig.BalanceAsync(Character, Issuer));
        var corporation = await rig.Loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, Corporation));
        Assert.Equal(100, corporation.FirstOrDefault(balance => balance.IssuerCorpId == Issuer)?.Amount ?? 0);
    }

    [PgFact]
    public async Task A_credit_of_a_character_without_a_corporation_keeps_the_whole_amount()
    {
        await using var rig = await FwRig.CreateAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Equal(new LoyaltyMemberCredit(1000, 0), await rig.Loyalty.CreditMemberAsync(connection, transaction, Character, 0, 10m, Issuer, 1000, "test.credit", "member-credit:2"));
            await transaction.CommitAsync();
        }
        Assert.Equal(1000, await rig.BalanceAsync(Character, Issuer));
    }
}
