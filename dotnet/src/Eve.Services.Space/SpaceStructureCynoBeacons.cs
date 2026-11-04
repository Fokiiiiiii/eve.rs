using Eve.Dogma;
using Eve.Events;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>Places and restores only the published Pharolux cynosural beacon for service 16.</summary>
public sealed partial class SpaceStructureCynoBeacons(
    NpgsqlDataSource source,
    StructureStore store,
    DogmaStatic statics,
    SpaceSessions sessions,
    Action<string>? log = null)
{
    private const int RadiusAttribute = 162;
    private const string ServiceSubscriberName = "space-structure-cyno-beacons-service";
    private const string RemovalSubscriberName = "space-structure-cyno-beacons-removal";

    public void Subscribe(EventBus events)
    {
        events.Subscribe(ServiceSubscriberName, [StructureTimers.ServiceChangedTopic], async (context, cancellationToken) =>
        {
            var change = context.Event.Payload<StructureServiceChanged>()!;
            if (change.ServiceId != StructureServiceIds.CynoBeacon)
                return;
            await RefreshAsync(context.Connection, context.Transaction, change.StructureId, change.SolarSystemId, cancellationToken);
        });
        events.Subscribe(RemovalSubscriberName, [StructureTimers.StateChangedTopic], (context, _) =>
        {
            var change = context.Event.Payload<StructureStateChanged>()!;
            if (change.Removal is not null || change.State == (int)StructureState.Unanchored)
                sessions.RemoveCynoBeacon(change.SolarSystemId, change.StructureId);
            return Task.CompletedTask;
        });
    }

    /// <summary>Called with the other structure restorers when a system scene becomes available.</summary>
    public async Task RestoreAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            var structures = await store.ListInSystemAsync(connection, null, solarSystemId, cancellationToken);
            await statics.EnsureTypesAsync(
                structures.Where(record => record.TypeId == StructureBeaconTravel.PharoluxCynoBeaconType)
                    .Select(record => record.TypeId),
                cancellationToken);
            foreach (var record in structures)
            {
                if (record.TypeId != StructureBeaconTravel.PharoluxCynoBeaconType ||
                    record.Snapshot.DestroyedMs > 0 ||
                    record.Snapshot.State == StructureState.Unanchored ||
                    statics.Type(record.TypeId) is not { Published: true } type)
                    continue;
                var services = await store.ServicesAsync(connection, null, record.Snapshot.StructureId, cancellationToken);
                if (services.GetValueOrDefault(StructureServiceIds.CynoBeacon) != (int)ServiceState.Online)
                    continue;
                Place(record, type);
            }
        }
        catch (Exception error)
        {
            log?.Invoke($"the cynosural beacons of system {solarSystemId} were not put back: {error.Message}");
        }
    }

    private async Task RefreshAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long structureId,
        int eventSolarSystemId,
        CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(connection, transaction, structureId, false, cancellationToken);
        if (record is null)
        {
            Remove(eventSolarSystemId, structureId);
            return;
        }
        if (record.SolarSystemId != eventSolarSystemId)
            Remove(eventSolarSystemId, structureId);
        if (record.TypeId != StructureBeaconTravel.PharoluxCynoBeaconType ||
            record.Snapshot.DestroyedMs > 0 ||
            record.Snapshot.State == StructureState.Unanchored)
        {
            Remove(record.SolarSystemId, structureId);
            return;
        }

        await statics.EnsureTypesAsync([record.TypeId], cancellationToken);
        if (statics.Type(record.TypeId) is not { Published: true } type ||
            (await store.ServicesAsync(connection, transaction, structureId, cancellationToken))
                .GetValueOrDefault(StructureServiceIds.CynoBeacon) != (int)ServiceState.Online)
        {
            Remove(record.SolarSystemId, structureId);
            return;
        }
        Place(record, type);
    }

    private void Place(StructureRecord record, TypeDef type)
    {
        var radius = type.Attributes.GetValueOrDefault(RadiusAttribute, 0d);
        if (record.OwnerCorporationId <= 0 ||
            !double.IsFinite(record.X) || !double.IsFinite(record.Y) || !double.IsFinite(record.Z) ||
            !double.IsFinite(radius) || radius <= 0)
        {
            log?.Invoke($"structure {record.Snapshot.StructureId} has no valid published Pharolux placement data");
            Remove(record.SolarSystemId, record.Snapshot.StructureId);
            return;
        }

        sessions.PlaceCynoBeacon(new SimPlaceCynoBeacon(
            record.Snapshot.StructureId,
            record.SolarSystemId,
            record.Name,
            record.TypeId,
            type.GroupId,
            [record.X, record.Y, record.Z],
            radius,
            true,
            record.OwnerCorporationId,
            (int)record.OwnerCorporationId,
            (int)record.AllianceId,
            0));
    }

    private void Remove(int solarSystemId, long structureId)
    {
        if (solarSystemId > 0 && structureId > 0)
            sessions.RemoveCynoBeacon(solarSystemId, structureId);
    }
}
