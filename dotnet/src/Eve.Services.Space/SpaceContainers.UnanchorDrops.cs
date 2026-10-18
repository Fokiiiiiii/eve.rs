using Eve.Events;
using Eve.Structures;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    private const string UnanchorDropSubscriberName = "space.structure_unanchor_drops";

    public void SubscribeUnanchorDrops(EventBus events) =>
        events.Subscribe(UnanchorDropSubscriberName, [StructureTimers.StateChangedTopic], async (context, cancellationToken) =>
        {
            var change = context.Event.Payload<StructureStateChanged>()!;
            if (change.Removal == "unanchored" && change.UnanchorDrop is { } drop)
            {
                await PlaceUnanchorDropsAsync(change, drop, cancellationToken);
            }
            else if (change.Removal == "destroyed" && change.UnanchorDrop is { FittingsContainerItemId: > 0 } loot)
            {
                await PlaceUnanchorDropAsync(loot.FittingsContainerItemId, StructureDestructionLoot.ContainerMarker(change.StructureId), cancellationToken);
            }
        });

    private async Task PlaceUnanchorDropsAsync(
        StructureStateChanged change,
        StructureUnanchorDrop drop,
        CancellationToken cancellationToken)
    {
        await PlaceUnanchorDropAsync(drop.HullItemId, StructureUnanchorDrop.HullMarker(change.StructureId), cancellationToken);
        if (drop.FittingsContainerItemId > 0)
        {
            await PlaceUnanchorDropAsync(
                drop.FittingsContainerItemId,
                StructureUnanchorDrop.FittingsMarker(change.StructureId),
                cancellationToken);
        }
    }

    private async Task PlaceUnanchorDropAsync(long itemId, string marker, CancellationToken cancellationToken)
    {
        if (itemId <= 0 || !_placed.TryAdd(itemId, 0))
        {
            return;
        }

        try
        {
            var item = await _items.GetAsync(itemId, cancellationToken);
            if (item is null)
            {
                _placed.TryRemove(itemId, out _);
                return;
            }
            var spaceItem = await _spaceItems.GetAsync(itemId, cancellationToken)
                ?? throw new InvalidOperationException($"Unanchor drop {itemId} has no persisted space item.");
            if (!StringComparer.Ordinal.Equals(item.CustomInfo, marker))
            {
                throw new InvalidOperationException($"Unanchor drop {itemId} has an invalid marker.");
            }

            await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
            var type = _statics.Type(item.TypeId)
                ?? throw new InvalidOperationException($"SDE type {item.TypeId} for unanchor drop {itemId} is missing.");
            var empty = (await _items.InContainerAsync(itemId, cancellationToken)).Count == 0;
            _sim.PlaceWreck(new SimPlaceWreck(
                SimSceneKey.SolarSystem(spaceItem.SolarSystemId), itemId, spaceItem.GroupId,
                new SimWreckKind(item.TypeId, item.CustomName ?? type.Name, type.GraphicId),
                spaceItem.Position, spaceItem.Direction, spaceItem.Radius, spaceItem.LauncherId,
                new SimCredit(spaceItem.CharacterId, spaceItem.CorporationId, spaceItem.AllianceId, spaceItem.WarFactionId), empty));
        }
        catch
        {
            _placed.TryRemove(itemId, out _);
            throw;
        }
    }
}
