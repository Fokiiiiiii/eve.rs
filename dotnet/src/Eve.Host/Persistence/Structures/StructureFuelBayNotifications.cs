using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Services;

namespace Eve.Host;

internal sealed record StructureFuelBayItemsChanged(long StructureId, IReadOnlyList<ItemChanged> Changes, IReadOnlyList<StructureFuelRemaining>? Remaining = null);


internal sealed record StructureFuelRemaining(int TypeId, long Quantity);

internal static class StructureFuelBayNotifications
{
    public const string Topic = "structure.fuel_bay.items_changed";

    public static void Subscribe(EventBus events, CharacterSessions sessions, DogmaStatic dogma)
    {
        events.Subscribe("structure-fuel-bay-notifications", [Topic], async (context, cancellationToken) =>
        {
            var change = context.Event.Payload<StructureFuelBayItemsChanged>()!;
            if (change.Changes.Count == 0)
            {
                return;
            }

            await dogma.EnsureTypesAsync(change.Changes.Select(item => item.After.TypeId).Distinct(), cancellationToken);
            var recipients = sessions.InStructure(change.StructureId).ToArray();
            foreach (var itemChange in change.Changes)
            {
                foreach (var session in recipients)
                {
                    ShipNotifications.SendItemChange(session, itemChange, item => RowOf(item, dogma));
                }
            }
        });
    }

    internal static Eve.Services.ItemRow RowOf(Item item, DogmaStatic dogma) =>
        new(
            item.Id,
            item.TypeId,
            item.OwnerId,
            item.LocationId,
            item.Flag,
            item.Singleton ? -1 : item.Quantity,
            dogma.Type(item.TypeId)?.GroupId ?? 0,
            dogma.Type(item.TypeId)?.CategoryId ?? 0);
}
