using Eve.Corporations;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Corporations;

/// <summary>
/// The wallet side of paying a corporation office's rent bill. The payer is debited; a bill that names its owner (a structure's) credits the owner, as EVE.js
/// creditRentalOwner does. A station's bill keeps the debit-only settlement it has always had.
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
        if (bill.CreditorId is { } owner)
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(owner), bill.AmountIsk, payer.OwnerId, OfficeRentReferenceType,
                "Corporation office rental", paidAtMs, $"office-rent-bill:{bill.BillId}:credit", "office_rental_fee", cancellationToken);
    }
}
