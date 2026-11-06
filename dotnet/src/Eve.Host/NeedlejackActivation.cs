using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Eve.Crimewatch;
using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Structures;
using Eve.Universe;
using Eve.Services.Crimewatch;
using Npgsql;

namespace Eve.Host;

public sealed class NeedlejackActivation(
    NpgsqlDataSource source,
    string spaceSchema,
    PgItemStore items,
    ItemTypeCatalog itemTypes,
    CharacterShips ships,
    FleetStore fleets,
    CharacterSessions characterSessions,
    SpaceSessions spaceSessions,
    SolarSystemCatalog solarSystems,
    StationCatalog stations,
    DogmaStatic dogma,
    PgCrimewatchStore crimewatchStore,
    CrimewatchRuntime crimewatchRuntime,
    StructureStore structures,
    PgDeployedStore deployed,
    PgBountyStore bounties,
    Func<long> now)
{
    private const int NeedlejackGroupId = 4041;
    private const int MaximumShipCandidatesAttributeId = 2832;
    private const int ActiveSystemJumpAttributeId = 3025;
    private const int CapitalSizeAttributeId = 1785;
    private const double FilamentProximityMeters = 1_000_000;
    private const double FleetLinkMeters = 30_000;

    private sealed record ActivationRecord(
        string SessionKey, long CallId, long ItemId, long CharacterId, int ItemTypeId, long ItemOwnerId,
        long ItemLocationId, int ItemFlag, long QuantityBefore, long QuantityAfter, int SourceSystemId,
        int DestinationSystemId, long[] PilotCharacterIds, long CreatedMs);

    private sealed record LinkedPilot(ClientSession Session, long CharacterId, long ShipId, int ShipTypeId, WarpCoordinates Position);

    private string Q(string sql) => Sql.In(spaceSchema, sql);

    public static async Task MigrateAsync(NpgsqlDataSource source, string spaceSchema, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Sql.In(spaceSchema, """
            CREATE TABLE IF NOT EXISTS {schema}.needlejack_activations (
                session_key text NOT NULL,
                call_id bigint NOT NULL,
                item_id bigint NOT NULL,
                character_id bigint NOT NULL,
                item_type_id integer,
                item_owner_id bigint,
                item_location_id bigint,
                item_flag integer,
                quantity_before bigint,
                quantity_after bigint,
                source_system_id integer,
                destination_system_id integer,
                pilot_character_ids bigint[],
                created_ms bigint NOT NULL,
                PRIMARY KEY (session_key, call_id)
            )
            """), connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<PyValue?> ReplayAsync(ServiceCall call, long itemId, CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId ?? 0;
        var callId = CallId(call.CallId);
        if (itemId <= 0 || characterId <= 0 || callId <= 0)
            return null;

        var record = await ReadActivationAsync(SessionKey(call.Session), callId, cancellationToken);
        if (record is null)
            return null;
        if (record.ItemId != itemId || record.CharacterId != characterId)
            throw ErrorList(22);
        await CompleteCommittedAsync(call.Session, record, cancellationToken);
        return PyNone.Instance;
    }

    public async ValueTask<PyValue?> ActivateAsync(ServiceCall call, SpaceRpcItem advertisedItem, CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId ?? 0;
        var callId = CallId(call.CallId);
        if (characterId <= 0 || advertisedItem.ItemId <= 0 || callId <= 0 ||
            Integer(call.Arg(0)) != advertisedItem.ItemId)
            throw ErrorList(22);

        var sessionKey = SessionKey(call.Session);
        var currentTime = now();
        ActivationRecord? committed = null;

        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var inserted = await InsertActivationAsync(connection, transaction, sessionKey, callId, advertisedItem.ItemId, characterId, currentTime, cancellationToken);
            if (!inserted)
            {
                committed = await ReadActivationAsync(connection, transaction, sessionKey, callId, cancellationToken);
                if (committed is null || committed.ItemId != advertisedItem.ItemId || committed.CharacterId != characterId)
                    throw ErrorList(22);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                var filament = await items.LockAndGetAsync(connection, transaction, advertisedItem.ItemId, cancellationToken);
                if (filament is null)
                    throw ErrorList(6);
                var filamentType = await itemTypes.GetAsync(filament.TypeId, cancellationToken);
                if (filamentType?.GroupId != NeedlejackGroupId)
                    throw ErrorList(2, new PyLong(characterId));
                await dogma.EnsureTypesAsync([filament.TypeId], cancellationToken);
                if (filament.OwnerId != characterId)
                    throw ErrorList(3, new PyLong(characterId));
                if (filament.Quantity < 1)
                    throw ErrorList(11);
                if (advertisedItem.GroupId != filamentType.GroupId || advertisedItem.OwnerId != filament.OwnerId ||
                    advertisedItem.TypeId != filament.TypeId || advertisedItem.Quantity < 1)
                    throw ErrorList(22);
                if (filament.Flag != CharacterShips.CargoFlag || filament.LocationId != spaceSessions.ShipIdOf(call.Session))
                    throw ErrorList(5, new PyInt(filament.TypeId));

                var filamentDogma = dogma.Type(filament.TypeId);
                if (filamentDogma is null ||
                    !filamentDogma.Attributes.TryGetValue(MaximumShipCandidatesAttributeId, out var maximumShipsValue) ||
                    maximumShipsValue < 1 || maximumShipsValue > int.MaxValue ||
                    !filamentDogma.Attributes.TryGetValue(ActiveSystemJumpAttributeId, out var activityMode) ||
                    activityMode is not (0 or 1))
                    throw ErrorList(22);
                var maximumShips = (int)maximumShipsValue;

                var activation = await ValidateFleetAsync(connection, transaction, call.Session, characterId, cancellationToken);
                if (activation.Errors.Count > 0)
                    throw ErrorList(activation.Errors);
                if (activation.SourceSystem is not { } sourceSystem || activation.Pilots.Count == 0)
                    throw ErrorList(22);
                if (activation.Pilots.Count > maximumShips)
                    throw ErrorList(24, new PyInt(maximumShips));

                var destination = await SelectDestinationAsync(sourceSystem.Id, activityMode == 1, cancellationToken);
                if (destination is null)
                    throw ErrorList(22);

                var destinationSystem = solarSystems.Get(destination.Value);
                if (destinationSystem is null)
                    throw ErrorList(22);

                List<PyValue> timerErrors = [];
                foreach (var participant in activation.Pilots.OrderBy(pilot => pilot.CharacterId))
                {
                    var timer = await crimewatchStore.UpdateAsync(connection, transaction, participant.CharacterId,
                        state => state.PvpUntilMs > currentTime
                            ? state
                            : state with
                            {
                                PvpUntilMs = currentTime + CombatState.PvpDurationMs,
                                SolarSystemId = destinationSystem.Id,
                            }, currentTime, cancellationToken);
                    if (timer.Before.PvpUntilMs > currentTime)
                        timerErrors.Add(Error(8, new PyLong(participant.CharacterId)));
                }
                if (timerErrors.Count > 0)
                    throw ErrorList(timerErrors);

                var moves = activation.Pilots.Select(pilot => new SpaceRelocationRequest(
                    pilot.CharacterId, pilot.ShipId, sourceSystem.Id, destinationSystem.Id,
                    destinationSystem.ConstellationId, destinationSystem.RegionId)).ToArray();
                var persistedMoves = await ships.RelocateFleetInTransactionAsync(connection, transaction, moves, cancellationToken);
                if (persistedMoves is null)
                    throw ErrorList(22);

                var participants = activation.Pilots.OrderBy(pilot => pilot.CharacterId).ToArray();

                if (filament.Quantity == 1)
                    await items.DeleteAsync(connection, transaction, filament.Id, cancellationToken);
                else
                    await items.SetQuantityAsync(connection, transaction, filament.Id, filament.Quantity - 1, cancellationToken);

                var record = new ActivationRecord(sessionKey, callId, filament.Id, characterId, filament.TypeId,
                    filament.OwnerId, filament.LocationId, filament.Flag, filament.Quantity, filament.Quantity - 1,
                    sourceSystem.Id, destinationSystem.Id, [.. participants.Select(pilot => pilot.CharacterId)], currentTime);
                await CompleteActivationRowAsync(connection, transaction, record, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                committed = record;
            }
        }

        if (committed is null)
            throw ErrorList(22);
        await CompleteCommittedAsync(call.Session, committed, cancellationToken);
        return PyNone.Instance;
    }

    private async Task<(SolarSystemInfo? SourceSystem, IReadOnlyList<LinkedPilot> Pilots, IReadOnlyList<PyValue> Errors)> ValidateFleetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ClientSession activator, long characterId, CancellationToken cancellationToken)
    {
        List<PyValue> errors = [];
        var sourceSystemId = spaceSessions.SolarSystemOf(activator);
        var ownShipId = spaceSessions.ShipIdOf(activator);
        if (sourceSystemId is not { } systemId || ownShipId is not { } activeShipId)
        {
            errors.Add(activator.DockedLocationId is null
                ? Error(21)
                : Error(16, new PyLong(characterId)));
            return (null, [], errors);
        }
        if (!spaceSessions.IsInPublicSystem(activator, systemId) || activator.GetInt("shipid") != activeShipId)
        {
            errors.Add(Error(21));
            return (null, [], errors);
        }
        if (spaceSessions.IsCloaked(activator))
            errors.Add(Error(15, new PyLong(characterId)));
        if (spaceSessions.Transitions.SessionChangeActive((ulong)activator.SessionId))
            errors.Add(Error(10, new PyLong(characterId)));
        if (spaceSessions.IsWarping(activator))
            errors.Add(Error(14, new PyLong(characterId)));
        if (spaceSessions.IsInvulnerable(activator))
            errors.Add(Error(17, new PyLong(characterId)));
        if (spaceSessions.IsTethered(activator))
            errors.Add(Error(19, new PyLong(characterId)));
        if (await spaceSessions.IsSelfDestructingAsync(activator, cancellationToken))
            errors.Add(Error(18, new PyLong(characterId)));
        if (solarSystems.Get(systemId)?.Name.Equals("Zarzakh", StringComparison.OrdinalIgnoreCase) == true)
            errors.Add(Error(35));

        var membership = await fleets.MemberForCharacterAsync(characterId, cancellationToken);
        if (membership is null || membership.FleetId <= 0)
        {
            errors.Add(Error(23));
            return (solarSystems.Get(systemId), [], errors);
        }
        var members = await fleets.LockMembersInTransactionAsync(connection, transaction, membership.FleetId, cancellationToken);
        if (!members.Any(member => member.CharacterId == characterId))
        {
            errors.Add(Error(23));
            return (solarSystems.Get(systemId), [], errors);
        }

        var ownPosition = await spaceSessions.PositionOfAsync(activator, activeShipId, cancellationToken);
        if (ownPosition is not { } anchor || anchor.SolarSystemId != systemId)
        {
            errors.Add(Error(21));
            return (solarSystems.Get(systemId), [], errors);
        }
        if (await ProximityBlockerAsync(systemId, anchor, cancellationToken) is { } blocker)
            errors.Add(Error(7, new PyInt(blocker), new PyLong(characterId)));

        List<LinkedPilot> pilots = [];
        foreach (var member in members.OrderBy(member => member.CharacterId))
        {
            var pilotSession = characterSessions.Of(member.CharacterId);
            if (pilotSession is null || !spaceSessions.IsInPublicSystem(pilotSession, systemId) ||
                spaceSessions.ShipIdOf(pilotSession) is not { } shipId)
                continue;
            var position = member.CharacterId == characterId
                ? ownPosition
                : await spaceSessions.PositionOfAsync(pilotSession, shipId, cancellationToken);
            if (position is not { } shipPosition || shipPosition.SolarSystemId != systemId ||
                Distance(anchor.X, anchor.Y, anchor.Z, shipPosition.X, shipPosition.Y, shipPosition.Z) > FleetLinkMeters)
                continue;
            if (member.CharacterId != characterId && spaceSessions.IsCloaked(pilotSession))
                errors.Add(Error(15, new PyLong(member.CharacterId)));
            var shipTypeId = spaceSessions.ShipTypeIdOf(pilotSession);
            if (shipTypeId is not { } hullTypeId)
            {
                errors.Add(Error(21));
                continue;
            }
            await dogma.EnsureTypesAsync([hullTypeId], cancellationToken);
            if (dogma.Type(hullTypeId) is not { CategoryId: 6 } hull ||
                hull.Attributes.TryGetValue(CapitalSizeAttributeId, out var capitalSize) && capitalSize > 0)
                errors.Add(Error(4, new PyLong(member.CharacterId)));
            if (spaceSessions.Transitions.SessionChangeActive((ulong)pilotSession.SessionId))
                errors.Add(Error(10, new PyLong(member.CharacterId)));
            if (spaceSessions.IsWarping(pilotSession))
                errors.Add(Error(14, new PyLong(member.CharacterId)));
            if (spaceSessions.IsInvulnerable(pilotSession))
                errors.Add(Error(17, new PyLong(member.CharacterId)));
            if (spaceSessions.IsTethered(pilotSession))
                errors.Add(Error(19, new PyLong(member.CharacterId)));
            if (await spaceSessions.IsSelfDestructingAsync(pilotSession, cancellationToken))
                errors.Add(Error(18, new PyLong(member.CharacterId)));
            if (await crimewatchStore.SafetyOfAsync(connection, transaction, member.CharacterId, cancellationToken) == SafetyLevel.Full)
                errors.Add(Error(9, new PyLong(member.CharacterId)));
            pilots.Add(new LinkedPilot(pilotSession, member.CharacterId, shipId, hullTypeId, shipPosition));
        }

        if (!pilots.Any(pilot => pilot.CharacterId == characterId))
            errors.Add(Error(25, new PyLong(characterId), new PyInt((int)FleetLinkMeters)));
        return (solarSystems.Get(systemId), pilots, errors);
    }

    private async Task<int?> ProximityBlockerAsync(int solarSystemId, WarpCoordinates ship, CancellationToken cancellationToken)
    {
        var closestTypeId = 0;
        var closestDistance = double.PositiveInfinity;
        void Check(int typeId, double x, double y, double z, double radius = 0)
        {
            var distance = Math.Max(0, Distance(ship.X, ship.Y, ship.Z, x, y, z) - Math.Max(0, radius));
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestTypeId = typeId;
            }
        }

        if (solarSystems.Get(solarSystemId) is { } system)
            foreach (var gate in system.Gates)
                Check(gate.TypeId, gate.X, gate.Y, gate.Z, gate.Radius);
        foreach (var station in stations.InSystem(solarSystemId))
            Check(station.TypeId, station.X, station.Y, station.Z);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        foreach (var structure in await structures.ListInSystemAsync(connection, null, solarSystemId, cancellationToken))
            Check(structure.TypeId, structure.X, structure.Y, structure.Z);
        foreach (var record in await deployed.InSystemAsync(solarSystemId, cancellationToken))
            if (record.Position.Length >= 3)
                Check(record.TypeId, record.Position[0], record.Position[1], record.Position[2], record.Radius);

        return closestDistance <= FilamentProximityMeters ? closestTypeId : null;
    }

    private async Task<int?> SelectDestinationAsync(int sourceSystemId, bool highActivity, CancellationToken cancellationToken)
    {
        var candidates = solarSystems.Systems
            .Where(system => system.Id != sourceSystemId && system.Security <= 0 &&
                system.RegionName != "Pochven" && system.Gates.Count > 0)
            .OrderBy(system => system.Id)
            .ToArray();
        if (candidates.Length == 0)
            return null;

        var activity = await bounties.PendingKillsBySystemAsync(cancellationToken);
        var scores = candidates.Select(system => (system.Id, Kills: activity.GetValueOrDefault(system.Id))).ToArray();
        var targetScore = highActivity ? scores.Max(row => row.Kills) : scores.Min(row => row.Kills);
        // shortcut: the source gives no activity window or tie distribution; replace this stable tie break when an authoritative rule is available.
        var selected = scores.FirstOrDefault(row => row.Kills == targetScore);
        return selected.Id > 0 ? selected.Id : null;
    }

    private async Task CompleteCommittedAsync(ClientSession activator, ActivationRecord record, CancellationToken cancellationToken)
    {
        await NotifyConsumedAsync(activator, record, cancellationToken);
        foreach (var characterId in record.PilotCharacterIds)
        {
            var session = characterSessions.Of(characterId);
            if (session is null)
                continue;
            if (!spaceSessions.IsInPublicSystem(session, record.DestinationSystemId))
            {
                if (await ships.LoadAsync(characterId, cancellationToken) is not { } snapshot ||
                    snapshot.Ship.LocationId != record.DestinationSystemId ||
                    snapshot.Ship.Flag != CharacterShips.InSpaceFlag)
                    throw ErrorList(22);
                var move = new SpaceMove(snapshot.Ship, record.SourceSystemId, CharacterShips.InSpaceFlag, snapshot.Character);
                var error = await spaceSessions.RelocatePersistedAsync(session, move,
                    MoveOperationId(record.SessionKey, record.CallId, characterId), cancellationToken);
                if (error is not null)
                    throw ErrorList(22);
            }
            await crimewatchRuntime.SyncAsync(characterId, cancellationToken);
        }
    }

    private async Task NotifyConsumedAsync(ClientSession session, ActivationRecord record, CancellationToken cancellationToken)
    {
        if (session.CharacterId != record.CharacterId)
            return;
        var type = await itemTypes.GetAsync(record.ItemTypeId, cancellationToken);
        if (type is null)
            throw ErrorList(22);
        var row = new ItemRow(record.ItemId, record.ItemTypeId, record.ItemOwnerId, record.ItemLocationId,
            record.ItemFlag, record.QuantityAfter, type.GroupId, type.CategoryId);
        var changes = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(9), InventoryWire.Id(record.QuantityBefore))]);
        session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), changes, PyNone.Instance);
    }

    private async Task<bool> InsertActivationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sessionKey, long callId,
        long itemId, long characterId, long currentTime, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Q("""
            INSERT INTO {schema}.needlejack_activations (session_key, call_id, item_id, character_id, created_ms)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (session_key, call_id) DO NOTHING
            RETURNING 1
            """), connection, transaction);
        command.Parameters.AddWithValue(sessionKey);
        command.Parameters.AddWithValue(callId);
        command.Parameters.AddWithValue(itemId);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(currentTime);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task CompleteActivationRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ActivationRecord record, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Q("""
            UPDATE {schema}.needlejack_activations
            SET item_type_id = $3, item_owner_id = $4, item_location_id = $5, item_flag = $6,
                quantity_before = $7, quantity_after = $8, source_system_id = $9, destination_system_id = $10,
                pilot_character_ids = $11
            WHERE session_key = $1 AND call_id = $2
            """), connection, transaction);
        command.Parameters.AddWithValue(record.SessionKey);
        command.Parameters.AddWithValue(record.CallId);
        command.Parameters.AddWithValue(record.ItemTypeId);
        command.Parameters.AddWithValue(record.ItemOwnerId);
        command.Parameters.AddWithValue(record.ItemLocationId);
        command.Parameters.AddWithValue(record.ItemFlag);
        command.Parameters.AddWithValue(record.QuantityBefore);
        command.Parameters.AddWithValue(record.QuantityAfter);
        command.Parameters.AddWithValue(record.SourceSystemId);
        command.Parameters.AddWithValue(record.DestinationSystemId);
        command.Parameters.AddWithValue(record.PilotCharacterIds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ActivationRecord?> ReadActivationAsync(string sessionKey, long callId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return await ReadActivationAsync(connection, null, sessionKey, callId, cancellationToken);
    }

    private async Task<ActivationRecord?> ReadActivationAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sessionKey, long callId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Q("""
            SELECT item_id, character_id, item_type_id, item_owner_id, item_location_id, item_flag,
                   quantity_before, quantity_after, source_system_id, destination_system_id, pilot_character_ids, created_ms
            FROM {schema}.needlejack_activations WHERE session_key = $1 AND call_id = $2
            """), connection, transaction);
        command.Parameters.AddWithValue(sessionKey);
        command.Parameters.AddWithValue(callId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(2) || reader.IsDBNull(9) || reader.IsDBNull(10))
            return null;
        return new ActivationRecord(sessionKey, callId, reader.GetInt64(0), reader.GetInt64(1),
            reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt32(8), reader.GetInt32(9),
            reader.GetFieldValue<long[]>(10), reader.GetInt64(11));
    }

    private static string SessionKey(ClientSession session) => session.SessionId.ToString(CultureInfo.InvariantCulture);

    private static long Integer(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0,
    };

    private static long CallId(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0,
    };

    private static string MoveOperationId(string sessionKey, long callId, long characterId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionKey}:{callId}:{characterId}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }

    private static double Distance(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        var dz = z1 - z2;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static PyValue Error(int code, params PyValue[] args) => PyList.Of(new PyInt(code), new PyList(args));

    private static MachoWrappedException ErrorList(int code, params PyValue[] args) => ErrorList([Error(code, args)]);

    private static MachoWrappedException ErrorList(IReadOnlyList<PyValue> errors) =>
        MachoErrors.ThrowUserError("RandomJumpError", ("errors", new PyList(errors.ToArray())));
}
