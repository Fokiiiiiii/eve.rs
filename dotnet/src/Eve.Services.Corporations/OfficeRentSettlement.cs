using Eve.Corporations;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Corporations;

public static class OfficeRentSettlement
{
    public const int OfficeRentReferenceType = 13;

    public static async Task<IReadOnlyList<WalletBalanceChange>> SettleAsync(WalletTransactions wallets, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CorporationOfficeRentBill bill, WalletAccount payer, long creditorId, long paidAtMs, CancellationToken cancellationToken)
    {
        if (bill.AmountIsk <= 0) return [];
        var payerBalance = (await wallets.AdjustAsync(connection, transaction, payer, -bill.AmountIsk, creditorId, OfficeRentReferenceType,
            "Corporation office rental", paidAtMs, $"office-rent-bill:{bill.BillId}:payment", "office_rental_fee", cancellationToken)).Entries[0].Balance;
        await wallets.EnsureAsync(connection, transaction, creditorId, cancellationToken);
        var creditorBalance = (await wallets.AdjustAsync(connection, transaction, new WalletAccount(creditorId), bill.AmountIsk, payer.OwnerId, OfficeRentReferenceType,
            "Corporation office rental", paidAtMs, $"office-rent-bill:{bill.BillId}:credit", "office_rental_fee", cancellationToken)).Entries[0].Balance;
        return
        [
            new WalletBalanceChange(payer.OwnerId, payer.AccountKey, payerBalance),
            new WalletBalanceChange(creditorId, 1000, creditorBalance),
        ];
    }
}
