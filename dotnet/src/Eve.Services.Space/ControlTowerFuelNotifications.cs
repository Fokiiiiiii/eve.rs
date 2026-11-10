using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Space;

/// <summary>A control tower's fuel stacks changed in the same transaction that advanced its paid-through state.</summary>
public sealed record ControlTowerFuelInventoryChange(
    Item After, long PreviousLocationId, int PreviousFlag, long PreviousQuantity, long? PreviousOwnerId = null);

public sealed record ControlTowerFuelInventoryChanged(
    long CorporationId, IReadOnlyList<ControlTowerFuelInventoryChange> Changes);

/// <summary>Delivers POS fuel-bay item changes to online corporation members after the fuel transaction commits.</summary>
public sealed class ControlTowerFuelNotifications(CharacterSessions sessions, DogmaStatic statics)
{
    public const string SubscriptionName = "space.control_tower.fuel_inventory";

    public void Subscribe(EventBus events) => events.Subscribe(SubscriptionName,
        [SpaceContainers.ControlTowerFuelInventoryChangedTopic], (context, _) =>
        {
            var changed = context.Event.Payload<ControlTowerFuelInventoryChanged>();
            if (changed is null || changed.Changes.Count == 0)
                return Task.CompletedTask;

            context.AfterCommit(async cancellationToken =>
            {
                await statics.EnsureTypesAsync(changed.Changes.Select(change => change.After.TypeId).Distinct().ToArray(), cancellationToken);
                foreach (var session in sessions.OfCorporation(changed.CorporationId))
                foreach (var change in changed.Changes)
                {
                    var item = change.After;
                    var type = statics.Type(item.TypeId);
                    var row = new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag,
                        item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
                    ShipNotifications.SendItemChange(session,
                        new ItemChanged(item, change.PreviousLocationId, change.PreviousFlag, change.PreviousQuantity, change.PreviousOwnerId),
                        _ => row);
                }
            });
            return Task.CompletedTask;
        });
}
