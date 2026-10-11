using Eve.Events;
using Eve.Marshal;
using Eve.Services;
using Eve.Structures;

namespace Eve.Host;

/// <summary>
/// The structure notices the client listens for (EVE.js <c>space/runtime/structureLifecycleFanout.js</c>), from one Event Bus subscriber, <c>structure.notify</c>, on
/// <c>structure.state_changed</c>. The owner corporation's sessions hear <c>OnStructureStateChanged(solarSystemID, structureID, state)</c> when the state changed,
/// and <c>OnCorporationStructuresUpdated</c> when what the directory shows changed (the state, the upkeep, or the structure left). The sessions docked in the structure
/// hear <c>OnCurrentStructureStateChanged(solarSystemID, structureID, state)</c>. The bus delivers the event after its transaction committed.
/// </summary>
public static class StructureNotifications
{
    /// <summary>The subscriber's name: a name is a contract, so it is never renamed.</summary>
    public const string SubscriberName = "structure.notify";

    public static void Subscribe(EventBus events, StructureStore store, CharacterSessions sessions)
    {
        events.Subscribe(SubscriberName, [StructureTimers.StateChangedTopic], async (context, cancellation) =>
        {
            var change = context.Event.Payload<StructureStateChanged>()!;
            var owner = (await store.GetAsync(context.Connection, context.Transaction, change.StructureId, false, cancellation))?.OwnerCorporationId ?? 0;
            var stateChanged = change.State > 0 && change.PreviousState != change.State;
            var directoryChanged = stateChanged || change.PreviousUpkeep != change.Upkeep || change.Removal is not null;
            if (owner > 0 && stateChanged)
            {
                foreach (var session in sessions.OfCorporation(owner))
                {
                    session.SendNotification("OnStructureStateChanged", "clientID", new PyLong(change.SolarSystemId), new PyLong(change.StructureId), new PyInt(change.State));
                }
            }
            if (stateChanged)
            {
                foreach (var session in sessions.InStructure(change.StructureId))
                {
                    session.SendNotification("OnCurrentStructureStateChanged", "shipid", new PyLong(change.SolarSystemId), new PyLong(change.StructureId), new PyInt(change.State));
                }
            }
            if (owner > 0 && directoryChanged)
            {
                foreach (var session in sessions.OfCorporation(owner))
                {
                    session.SendNotification("OnCorporationStructuresUpdated", "*corpid&corprole");
                }
            }
        });
    }
}
