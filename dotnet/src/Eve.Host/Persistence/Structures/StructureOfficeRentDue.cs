using Eve.Corporations;
using Eve.Scheduler;
using Eve.Services.Corporations;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The rent of a corporation office at a structure falls due (ST-12). The job is kept per office and runs at the open bill's due time. A bill that is paid by auto-pay
/// renews the office; an unpaid one (auto-pay off, or short of funds) gives the office up: the corporation's items at the structure go to asset safety and the office
/// ends, as EVE.js processUnpaidOfficeRentalBill does for a structure. A station office has no creditor named by its bill and is not handled here.
/// </summary>
internal sealed class StructureOfficeRentDue(
    NpgsqlDataSource source,
    CorporationOfficeStore offices,
    WalletTransactions wallets,
    DurableScheduler scheduler,
    IStructureOffices structures)
{
    public const string JobKind = "office.rent_due";
    private const int RentalBillTypeId = 2;
    private const long RetryMs = 60L * 60 * 1000;

    private sealed record Payload(long OfficeId);

    public void Register() => scheduler.Register(JobKind, (job, token) => RunAsync(job.Payload<Payload>()!.OfficeId, token));

    /// <summary>One run for an office: waits for the open bill's due time, pays it by auto-pay, or gives the office up.</summary>
    public async Task RunAsync(long officeId, CancellationToken cancellationToken)
    {
        await using var read = await source.OpenConnectionAsync(cancellationToken);
        if (await offices.OpenBillForOfficeAsync(read, null, officeId, forUpdate: false, cancellationToken) is not { } bill || bill.CreditorId is null)
            return;
        var now = scheduler.Now;
        if (bill.DueAtMs > now)
        {
            await ScheduleAsync(officeId, bill.DueAtMs, cancellationToken);
            return;
        }
        if (await TryAutoPayAsync(bill, now, cancellationToken))
        {
            // the paid bill renewed the office: the job waits for the next bill's due time
            await RescheduleAsync(officeId, cancellationToken);
            return;
        }
        await GiveUpAsync(officeId, now, cancellationToken);
    }

    /// <summary>The corporation's auto-pay for rental bills: the bill is paid from its division when the setting is on and the funds are there.</summary>
    private async Task<bool> TryAutoPayAsync(CorporationOfficeRentBill bill, long now, CancellationToken cancellationToken)
    {
        var settings = (await offices.AutomaticPaySettingsAsync([bill.CorporationId], cancellationToken))[bill.CorporationId];
        if (!settings.EnabledBillTypes.TryGetValue(RentalBillTypeId, out var enabled) || !enabled) return false;
        try
        {
            return await offices.PayBillAsync(bill.BillId, bill.CorporationId, bill.CorporationId, now,
                (connection, transaction, open) => OfficeRentSettlement.SettleAsync(wallets, connection, transaction, open,
                    new WalletAccount(bill.CorporationId, settings.DivisionId), open.CreditorId!.Value, now, cancellationToken),
                cancellationToken);
        }
        catch (InsufficientFundsException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gives an office up for an unpaid bill: the items go to asset safety and the office ends in one transaction. A bill that was paid or given up meanwhile is
    /// left alone; a refused asset safety wrap is tried again in an hour.
    /// </summary>
    private async Task GiveUpAsync(long officeId, long now, CancellationToken cancellationToken)
    {
        await using var read = await source.OpenConnectionAsync(cancellationToken);
        if (await offices.OpenBillForOfficeAsync(read, null, officeId, forUpdate: false, cancellationToken) is not { } bill) return;
        var site = await structures.ReadAsync(bill.StationId, cancellationToken);
        if (site is null) return;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var open = await offices.OpenBillForOfficeAsync(connection, transaction, officeId, forUpdate: true, cancellationToken);
        var office = await offices.GetAsync(connection, transaction, bill.CorporationId, bill.StationId, forUpdate: true, cancellationToken);
        if (open is null || office is null || office.OfficeId != officeId) return;
        if (open.DueAtMs > now)
        {
            await structures.ScheduleRentDueAsync(connection, transaction, officeId, open.DueAtMs, cancellationToken);
        }
        else if (await structures.MoveOfficeToSafetyAsync(connection, transaction, site, bill.CorporationId, cancellationToken))
        {
            await offices.UnrentAsync(connection, transaction, office, now, cancellationToken);
        }
        else
        {
            await structures.ScheduleRentDueAsync(connection, transaction, officeId, now + RetryMs, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RescheduleAsync(long officeId, CancellationToken cancellationToken)
    {
        await using var read = await source.OpenConnectionAsync(cancellationToken);
        if (await offices.OpenBillForOfficeAsync(read, null, officeId, forUpdate: false, cancellationToken) is { } open)
            await ScheduleAsync(officeId, open.DueAtMs, cancellationToken);
    }

    private async Task ScheduleAsync(long officeId, long dueAtMs, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await structures.ScheduleRentDueAsync(connection, transaction, officeId, dueAtMs, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
