using Eve.Events;
using Eve.Services;
using Eve.Structures;

namespace Eve.Host;

/// <summary>Invalidates the V24 structure directory cache when beacon availability or system jamming changes.</summary>
internal static class StructureCynoBeaconNotifications
{
    private const string SubscriberName = "structure-cyno-beacon-directory";

    public static void Subscribe(EventBus events, StructureStore store, CharacterSessions sessions)
    {
        events.Subscribe(SubscriberName, [StructureTimers.ServiceChangedTopic], async (context, cancellationToken) =>
        {
            var change = context.Event.Payload<StructureServiceChanged>()!;
            if (change.ServiceId is not (StructureServiceIds.CynoBeacon or StructureServiceIds.CynoJammer))
                return;

            var affected = new HashSet<ClientSession>(sessions.InSpace(change.SolarSystemId));
            if (change.ServiceId == StructureServiceIds.CynoBeacon &&
                await store.GetAsync(context.Connection, context.Transaction, change.StructureId, false, cancellationToken) is { OwnerCorporationId: > 0 } record)
            {
                foreach (var session in sessions.OfCorporation(record.OwnerCorporationId))
                    affected.Add(session);
            }
            foreach (var session in affected)
                session.SendNotification("OnCorporationStructuresUpdated", "*corpid&corprole");
        });
    }
}
