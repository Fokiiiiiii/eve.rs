using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Corporations;
using Eve.Events;
using Eve.Host.Persistence.Mail;
using Eve.Scheduler;
using Eve.Structures;
using Eve.Services.Corporations;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The rent of a corporation office at a structure falls due (ST-12). The job is kept per office and runs at the open bill's due time. A bill that is paid by auto-pay
/// renews the office; an unpaid one (auto-pay off, or short of funds) gives the office up: the corporation's items at the structure go to asset safety and the office
/// ends, as EVE.js processUnpaidOfficeRentalBill does for a structure. A station office (ST-16) is impounded when unpaid, as EVE.js impoundStationOfficeForUnpaidBill
/// does: its items go to the Impounded location and the office ends. The corporation hears of the due rent and of the eviction by a notification (type 10, group
/// Bills). A station bill with no creditor (a rent taken before ST-16) is not handled here.
/// </summary>
internal sealed class StructureOfficeRentDue(
    NpgsqlDataSource source,
    CorporationOfficeStore offices,
    WalletTransactions wallets,
    DurableScheduler scheduler,
    IStructureOffices structures,
    Eve.Inventory.PgItemStore items,
    PgCharacterStore characters,
    PersistenceTransactionMailbox mailbox)
{
    public const string JobKind = "office.rent_due";
    private const int RentalBillTypeId = 2;
    private const int BillNotificationTypeId = 10;
    private const int BillsNotificationGroupId = 2;
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
        // a destroyed or unanchored structure is given up at once: nothing is paid for it, and its due time does not wait
        var gone = (await structures.ReadAsync(bill.StationId, cancellationToken))?.Gone == true;
        if (!gone && bill.DueAtMs > now)
        {
            await ScheduleAsync(officeId, bill.DueAtMs, cancellationToken);
            return;
        }
        if (!gone && await TryAutoPayAsync(bill, now, cancellationToken))
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
        if (site is null)
        {
            await ImpoundStationAsync(officeId, now, cancellationToken);
            return;
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var open = await offices.OpenBillForOfficeAsync(connection, transaction, officeId, forUpdate: true, cancellationToken);
        var office = await offices.GetAsync(connection, transaction, bill.CorporationId, bill.StationId, forUpdate: true, cancellationToken);
        if (open is null || office is null || office.OfficeId != officeId) return;
        var evicted = false;
        if (!site.Gone && open.DueAtMs > now)
        {
            await structures.ScheduleRentDueAsync(connection, transaction, officeId, open.DueAtMs, cancellationToken);
        }
        else if (await structures.MoveOfficeToSafetyAsync(connection, transaction, site, bill.CorporationId, cancellationToken))
        {
            await offices.UnrentAsync(connection, transaction, office, now, cancellationToken);
            evicted = true;
        }
        else
        {
            await structures.ScheduleRentDueAsync(connection, transaction, officeId, now + RetryMs, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (evicted)
            await NotifyAsync(open, cancellationToken);
    }

    /// <summary>
    /// An unpaid station office is impounded at its due time (EVE.js impoundStationOfficeForUnpaidBill): the corporation's hangar items at the station go to the
    /// Impounded location, the folder goes and the office ends with its bills. Released by the release fee (GetItemsFromImpound).
    /// </summary>
    private async Task ImpoundStationAsync(long officeId, long now, CancellationToken cancellationToken)
    {
        await using var read = await source.OpenConnectionAsync(cancellationToken);
        if (await offices.OpenBillForOfficeAsync(read, null, officeId, forUpdate: false, cancellationToken) is not { } bill) return;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await offices.LockStationAsync(connection, transaction, bill.StationId, cancellationToken);
        var open = await offices.OpenBillForOfficeAsync(connection, transaction, officeId, forUpdate: true, cancellationToken);
        var office = await offices.GetAsync(connection, transaction, bill.CorporationId, bill.StationId, forUpdate: true, cancellationToken);
        if (open is null || office is null || office.OfficeId != officeId) return;
        if (open.DueAtMs > now)
        {
            await structures.ScheduleRentDueAsync(connection, transaction, officeId, open.DueAtMs, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await StationOfficeImpound.ImpoundAsync(items, offices, connection, transaction, office, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await NotifyAsync(open, cancellationToken);
    }

    /// <summary>
    /// The corporation hears that its rent fell due unpaid and that the office was given up (or impounded). The notice goes to the members, sent by the creditor;
    /// the key makes a repeat of the job a no-op. Written after the commit: a crash between the two loses the notice, not the eviction.
    /// </summary>
    private async Task NotifyAsync(CorporationOfficeRentBill bill, CancellationToken cancellationToken)
    {
        var recipients = (await characters.ForCorporationAsync(bill.CorporationId, cancellationToken))
            .Select(character => character.Id)
            .Distinct()
            .ToArray();
        if (recipients.Length == 0 || bill.CreditorId is not { } creditor) return;
        foreach (var (outcome, text) in new[] { ("rent-due", "unpaid"), ("evicted", "evicted") })
        {
            var data = new JsonObject
            {
                ["officeID"] = bill.OfficeId,
                ["stationID"] = bill.StationId,
                ["billID"] = bill.BillId,
                ["amount"] = (double)bill.AmountIsk,
                ["dueAtMs"] = bill.DueAtMs,
                ["outcome"] = text,
            };
            await mailbox.WriteNotificationsAsync(creditor, BillNotificationTypeId, BillsNotificationGroupId, recipients,
                $"office-rent:{bill.OfficeId}:{outcome}:{bill.BillId}", data, cancellationToken);
        }
    }

    /// <summary>
    /// A structure that is destroyed or unanchored closes its offices (ST-12): each office's rent job is due now, in the state change's transaction, and gives the
    /// office up (its corporation's items go to asset safety, its bills are cancelled). The job runs right after the commit.
    /// </summary>
    public void SubscribeClosures(EventBus events)
    {
        events.Subscribe("office.structure_gone", [StructureTimers.StateChangedTopic], async (context, cancellation) =>
        {
            var change = context.Event.Payload<StructureStateChanged>()!;
            if (change.Removal is not ("destroyed" or "unanchored"))
                return;
            foreach (var corporationId in await offices.CorporationsAtStationAsync(change.StructureId, cancellation))
            {
                if (await offices.GetAsync(context.Connection, context.Transaction, corporationId, change.StructureId, forUpdate: false, cancellation) is { } office)
                    await structures.ScheduleRentDueAsync(context.Connection, context.Transaction, office.OfficeId, scheduler.Now, cancellation);
            }
        });
    }

    /// <summary>
    /// The one-time creditor migration (ST-16): a station's rent bills taken before ST-16 have no creditor, so their due job did nothing. Each such station's bills
    /// get the station's owner (the owner the station's rent is credited to now), and the open ones get their due job. A station that is not in the catalog (a
    /// structure) is left alone. A second run changes nothing.
    /// </summary>
    public async Task CreditStationBillsAsync(Func<long, long?> stationOwnerOf, CancellationToken cancellationToken = default)
    {
        foreach (var stationId in await offices.StationsWithoutCreditorAsync(cancellationToken))
        {
            if (stationOwnerOf(stationId) is not { } owner || owner <= 0) continue;
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var bill in await offices.CreditStationBillsAsync(connection, transaction, stationId, owner, cancellationToken))
            {
                await structures.ScheduleRentDueAsync(connection, transaction, bill.OfficeId, bill.DueAtMs, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
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
