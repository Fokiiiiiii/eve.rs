using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Events;
using Eve.Host.Persistence.Mail;

namespace Eve.Host.Persistence.Structures;

internal sealed record StructureReinforcementChanged(
    long CorporationId,
    int Weekday,
    int Hour,
    long ApplyAtMs,
    IReadOnlyList<StructureReinforcementChangedStructure> Structures);

internal sealed record StructureReinforcementChangedStructure(long StructureId, string Name, int TypeId);

internal sealed class ReinforcementChangedNotifications(
    PgCharacterStore characters,
    PersistenceTransactionMailbox mailbox)
{
    internal const string Topic = "structure.reinforcement_changed";
    private const int NotificationTypeId = 209;
    private const int StructuresGroupId = 7;

    public void Subscribe(EventBus events) => events.Subscribe(
        "structure.reinforcement_changed.notifications",
        [Topic],
        async (context, cancellationToken) =>
        {
            var change = context.Event.Payload<StructureReinforcementChanged>()!;
            var recipients = (await characters.ForCorporationAsync(change.CorporationId, cancellationToken))
                .Select(character => character.Id);
            var allStructureInfo = new JsonArray();
            foreach (var structure in change.Structures.OrderBy(structure => structure.StructureId))
            {
                allStructureInfo.Add(new JsonArray(
                    JsonValue.Create(structure.StructureId),
                    JsonValue.Create(structure.Name),
                    JsonValue.Create(structure.TypeId)));
            }
            var data = new JsonObject
            {
                ["numStructures"] = change.Structures.Count,
                ["allStructureInfo"] = allStructureInfo,
                ["weekday"] = change.Weekday,
                ["hour"] = change.Hour,
                ["timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(change.ApplyAtMs).UtcDateTime.ToFileTimeUtc(),
            };
            await mailbox.WriteNotificationsAsync(
                change.CorporationId,
                NotificationTypeId,
                StructuresGroupId,
                recipients,
                $"structure-reinforcement:{context.Event.Id}",
                data,
                cancellationToken,
                context);
        });
}
