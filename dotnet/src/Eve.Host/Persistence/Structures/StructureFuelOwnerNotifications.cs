using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Dogma;
using Eve.Events;
using Eve.Host.Persistence.Mail;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Structures;

namespace Eve.Host.Persistence.Structures;

/// <summary>

/// corporation). A corporation member with a session gets OnItemChange for the burnt fuel blocks; when none is online, the burn is written to the notifications
/// mailbox in the burn transaction with the fuel left in the bay. Members already in the structure get the change from StructureFuelBayNotifications.
/// </summary>
internal sealed class StructureFuelOwnerNotifications(
    StructureStore structures,
    PgCharacterStore characters,
    CharacterSessions sessions,
    DogmaStatic dogma,
    Func<long, long[], string, JsonObject, EventContext, CancellationToken, Task> writeAlert)
{
    internal const int FuelAlertTypeId = 181;
    private const int StructuresGroupId = 7;


    public static Func<long, long[], string, JsonObject, EventContext, CancellationToken, Task> ToMailbox(PersistenceTransactionMailbox mailbox) =>
        async (corporationId, recipients, key, data, context, token) =>
        {
            await mailbox.WriteNotificationsAsync(corporationId, FuelAlertTypeId, StructuresGroupId, recipients, key, data, token, context);
        };

    public void Subscribe(EventBus events) => events.Subscribe(
        "structure-fuel-owner-notifications",
        [StructureFuelBayNotifications.Topic],
        async (context, cancellationToken) =>
        {
            var change = context.Event.Payload<StructureFuelBayItemsChanged>()!;
            if (change.Changes.Count == 0 ||
                await structures.GetAsync(context.Connection, context.Transaction, change.StructureId, false, cancellationToken) is not { } structure ||
                structure.OwnerCorporationId <= 0)
                return;

            var members = (await characters.ForCorporationAsync(structure.OwnerCorporationId, cancellationToken))
                .Select(character => character.Id)
                .Distinct()
                .ToArray();
            var online = members.Select(id => sessions.Of(id)).OfType<ClientSession>().ToArray();
            if (online.Length > 0)
            {
                var inStructure = sessions.InStructure(change.StructureId);
                var owners = online.Where(session => !inStructure.Contains(session)).ToArray();
                if (owners.Length == 0)
                    return;
                await dogma.EnsureTypesAsync(change.Changes.Select(item => item.After.TypeId).Distinct(), cancellationToken);
                foreach (var itemChange in change.Changes)
                    foreach (var session in owners)
                        ShipNotifications.SendItemChange(session, itemChange, item => StructureFuelBayNotifications.RowOf(item, dogma));
                return;
            }

            var remaining = change.Remaining ?? [];
            if (remaining.Count == 0)
                return;
            var data = new JsonObject
            {
                ["structureID"] = change.StructureId,
                ["structureShowInfoData"] = new JsonArray(
                    JsonValue.Create("showinfo"), JsonValue.Create(structure.TypeId), JsonValue.Create(change.StructureId)),
                ["solarsystemID"] = JsonValue.Create(structure.SolarSystemId),
                ["structureTypeID"] = JsonValue.Create(structure.TypeId),
                ["listOfTypesAndQty"] = new JsonArray(remaining
                    .OrderBy(entry => entry.TypeId)
                    .Select(entry => (JsonNode)new JsonArray(JsonValue.Create(entry.Quantity), JsonValue.Create(entry.TypeId)))
                    .ToArray()),
            };
            await writeAlert(structure.OwnerCorporationId, members, $"structure-fuel:{context.Event.Id}", data, context, cancellationToken);
        });
}
