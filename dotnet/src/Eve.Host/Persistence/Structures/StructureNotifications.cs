using Eve.Events;
using Eve.Dogma;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Structures;

namespace Eve.Host;

/// <summary>
/// The structure notices the client listens for (EVE.js <c>space/runtime/structureLifecycleFanout.js</c>), from one Event Bus subscriber, <c>structure.notify</c>, on

/// and <c>OnCorporationStructuresUpdated</c> when what the directory shows changed (the state, the upkeep, or the structure left). The sessions docked in the structure
/// hear <c>OnCurrentStructureStateChanged(solarSystemID, structureID, state)</c>. The bus delivers the event after its transaction committed.
/// </summary>
public static class StructureNotifications
{
    /// <summary>The subscriber's name: a name is a contract, so it is never renamed.</summary>
    public const string SubscriberName = "structure.notify";

    public static void Subscribe(
        EventBus events,
        StructureStore store,
        CharacterSessions sessions,
        Func<long, CancellationToken, ValueTask<PyValue?>> getMyHangarViewStateAsync)
    {
        events.Subscribe(SubscriberName, [StructureTimers.StateChangedTopic], async (context, cancellation) =>
        {
            var change = context.Event.Payload<StructureStateChanged>()!;
            var hangarViewState = change.Removal is null &&
                sessions.InStructure(change.StructureId).Any(session => session.GetInt("shipid") != change.StructureId)
                ? await getMyHangarViewStateAsync(change.StructureId, cancellation)
                : null;
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
            if (hangarViewState is not null)
            {
                foreach (var session in sessions.InStructure(change.StructureId).Where(session => session.GetInt("shipid") != change.StructureId))
                {
                    session.SendNotification("OnHangarViewStateUpdated", "structureid", new PyLong(change.StructureId), hangarViewState);
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
        events.Subscribe("structure.service.notify", [StructureTimers.ServiceChangedTopic], (context, _) =>
        {
            var change = context.Event.Payload<StructureServiceChanged>()!;
            foreach (var session in sessions.InStructure(change.StructureId))
                session.SendNotification("OnStructureServiceChanged", "clientID", new PyLong(change.StructureId));
            return Task.CompletedTask;
        });
        events.Subscribe("structure.service_module.notify", [StructureTimers.ServiceModulesOfflinedTopic], (context, _) =>
        {
            var change = context.Event.Payload<StructureServiceModulesOfflined>()!;
            var dogmaEvents = change.ModuleIds.SelectMany(moduleId => new DogmaEvent[]
            {
                new AttributeChanged(moduleId, BuiltInEffects.IsOnline, 1, 0),
                new EffectChanged(moduleId, BuiltInEffects.Online, false),
            }).ToArray();
            var fileTime = Py.FileTimeFromUnixMs(change.OccurredAtMs);
            foreach (var session in sessions.All().Where(session => session.GetInt("shipid") == change.StructureId))
            {
                if (session.CharacterId is { } characterId)
                    ShipNotifications.SendEvents(session, characterId, change.StructureId, dogmaEvents, fileTime);
            }
            return Task.CompletedTask;
        });
    }
}
