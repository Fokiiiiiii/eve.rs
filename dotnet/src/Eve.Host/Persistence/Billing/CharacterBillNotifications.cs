using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Corporations;
using Eve.Events;
using Eve.Host.Persistence.Mail;

namespace Eve.Host.Persistence.Billing;

internal sealed class CharacterBillNotifications(PersistenceTransactionMailbox mailbox)
{
    private const int CharacterBillIssuedTypeId = 9;
    private const int CharacterBillPaidTypeId = 12;
    private const int BillsGroupId = 2;

    public void Subscribe(EventBus events) => events.Subscribe(
        "bill.character_notifications",
        [CharacterBillEvents.IssuedTopic, CharacterBillEvents.PaidTopic],
        async (context, cancellationToken) =>
        {
            var bill = context.Event.Payload<CharacterBill>() ??
                throw new InvalidOperationException("The character bill event did not include its bill row.");
            var typeId = context.Event.Topic == CharacterBillEvents.IssuedTopic
                ? CharacterBillIssuedTypeId
                : CharacterBillPaidTypeId;
            var data = new JsonObject
            {
                ["billID"] = bill.BillId,
                ["billTypeID"] = bill.BillTypeId,
                ["amount"] = bill.AmountIsk,
                ["interest"] = bill.Interest,
                ["debtorID"] = bill.DebtorCharacterId,
                ["creditorID"] = bill.CreditorId,
                ["externalID"] = bill.ExternalId,
                ["dueDateTime"] = FiletimeLong(bill.DueFiletime),
                ["paidDateTime"] = FiletimeLong(bill.PaidFiletime ?? 0),
                ["paidByOwnerID"] = bill.PaidByOwnerId ?? 0,
            };
            await mailbox.WriteNotificationsAsync(
                bill.CreditorId,
                typeId,
                BillsGroupId,
                [bill.DebtorCharacterId],
                $"bill:{bill.BillId}:{typeId}",
                data,
                cancellationToken,
                context);
        });

    private static JsonObject FiletimeLong(long value) => new()
    {
        ["type"] = "long",
        ["value"] = value.ToString(CultureInfo.InvariantCulture),
    };
}
