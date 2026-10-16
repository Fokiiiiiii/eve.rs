using Eve.Corporations;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Corporations;

/// <summary>
/// The wallet side of paying a corporation office's rent bill. The payer is debited and the creditor (the structure's or the station's owner corporation, resolved by
/// the caller) is credited, as EVE.js creditRentalOwner does.
/// </summary>
public static class OfficeRentSettlement
{
    public const int OfficeRentReferenceType = 13;

    public static async Task SettleAsync(WalletTransactions wallets, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CorporationOfficeRentBill bill, WalletAccount payer, long creditorId, long paidAtMs, CancellationToken cancellationToken)
    {
        if (bill.AmountIsk <= 0) return;
        await wallets.AdjustAsync(connection, transaction, payer, -bill.AmountIsk, creditorId, OfficeRentReferenceType,
            "Corporation office rental", paidAtMs, $"office-rent-bill:{bill.BillId}:payment", "office_rental_fee", cancellationToken);
        await wallets.EnsureAsync(connection, transaction, creditorId, cancellationToken);
        await wallets.AdjustAsync(connection, transaction, new WalletAccount(creditorId), bill.AmountIsk, payer.OwnerId, OfficeRentReferenceType,
            "Corporation office rental", paidAtMs, $"office-rent-bill:{bill.BillId}:credit", "office_rental_fee", cancellationToken);
    }
}
