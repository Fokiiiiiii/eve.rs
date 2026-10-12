using Eve.Data;
using Npgsql;

namespace Eve.Structures;

public sealed record StructurePilotRequest(
    long StructureId,
    long CharacterId,
    long SessionId,
    long OriginalShipId,
    long? OriginalLocationId,
    bool FromSpace);

public sealed record StructurePilotRecord(
    long StructureId,
    long CharacterId,
    long SessionId,
    long OriginalShipId,
    long? OriginalLocationId,
    bool FromSpace,
    long Version);

public enum StructurePilotAcquireStatus
{
    Acquired,
    AlreadyOwned,
    TakeoverDenied,
    StructureNotFound,
    StructureDestroyed,
    IdentityInUse
}

public sealed record StructurePilotAcquireResult(
    StructurePilotAcquireStatus Status,
    StructurePilotRecord? Current,
    StructurePilotRecord? Displaced);

/// <summary>Persisted, exclusive control of an Upwell structure.</summary>
public sealed class StructurePilotStore
{
    private const string Columns =
        "structure_id, character_id, session_id, original_ship_id, original_location_id, from_space, version";

    private readonly StructureStore _structures;
    private readonly string _schema;

    public StructurePilotStore(StructureStore structures)
    {
        ArgumentNullException.ThrowIfNull(structures);
        _structures = structures;
        _schema = structures.Schema;
    }

    private string Q(string text) => Sql.In(_schema, text);

    public Task<StructurePilotRecord?> GetByStructureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long structureId,
        CancellationToken cancellationToken = default)
    {
        RequirePositive(structureId, nameof(structureId));
        return ReadAsync(connection, transaction, "WHERE structure_id = $1", cancellationToken, structureId);
    }

    public Task<StructurePilotRecord?> GetBySessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long sessionId,
        CancellationToken cancellationToken = default,
        bool forUpdate = false)
    {
        RequirePositive(sessionId, nameof(sessionId));
        if (forUpdate && transaction is null) throw new ArgumentException("A locking pilot read requires a transaction.", nameof(transaction));
        return ReadAsync(connection, transaction, "WHERE session_id = $1" + (forUpdate ? " FOR UPDATE" : ""), cancellationToken, sessionId);
    }

    /// <summary>The caller must authorize control first. A permitted takeover returns the displaced pilot.</summary>
    public async Task<StructurePilotAcquireResult> AcquireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StructurePilotRequest request,
        bool takeoverAuthorized,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var structure = await _structures.GetAsync(connection, transaction, request.StructureId, true, cancellationToken);
        if (structure is null)
            return new(StructurePilotAcquireStatus.StructureNotFound, null, null);
        if (structure.Snapshot.DestroyedMs != 0)
            return new(StructurePilotAcquireStatus.StructureDestroyed, null, null);

        var current = await ReadAsync(connection, transaction, "WHERE structure_id = $1 FOR UPDATE", cancellationToken, request.StructureId);
        if (current is not null && current.CharacterId == request.CharacterId && current.SessionId == request.SessionId)
            return new(StructurePilotAcquireStatus.AlreadyOwned, current, null);
        if (current is not null && current.SessionId == request.SessionId)
            return new(StructurePilotAcquireStatus.IdentityInUse, current, null);

        var identityOwner = await ReadAsync(
            connection,
            transaction,
            "WHERE structure_id <> $1 AND (character_id = $2 OR session_id = $3) ORDER BY structure_id LIMIT 1 FOR UPDATE",
            cancellationToken,
            request.StructureId,
            request.CharacterId,
            request.SessionId);
        if (identityOwner is not null)
            return new(StructurePilotAcquireStatus.IdentityInUse, current, null);
        if (current is not null && !takeoverAuthorized)
            return new(StructurePilotAcquireStatus.TakeoverDenied, current, null);

        if (current is not null)
        {
            var removed = await Sql.ExecuteAsync(
                connection,
                transaction,
                Q("DELETE FROM {schema}.structure_pilots WHERE structure_id = $1 AND version = $2"),
                cancellationToken,
                current.StructureId,
                current.Version);
            if (removed != 1)
                throw new InvalidOperationException("The locked structure pilot changed during takeover.");
        }

        var candidate = new StructurePilotRecord(
            request.StructureId,
            request.CharacterId,
            request.SessionId,
            request.OriginalShipId,
            request.OriginalLocationId,
            request.FromSpace,
            await NextVersionAsync(connection, transaction, cancellationToken));
        var acquired = await InsertAsync(connection, transaction, candidate, cancellationToken);
        if (acquired is null)
        {
            if (current is not null && await InsertAsync(connection, transaction, current, cancellationToken) is null)
                throw new InvalidOperationException("The displaced structure pilot could not be restored.");
            return new(StructurePilotAcquireStatus.IdentityInUse, current, null);
        }

        return new(StructurePilotAcquireStatus.Acquired, acquired, current);
    }

    public async Task<bool> ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        StructurePilotRecord expectedOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        if (!IsValid(expectedOwner))
            return false;

        return await Sql.ExecuteAsync(
            connection,
            transaction,
            Q("DELETE FROM {schema}.structure_pilots WHERE structure_id = $1 AND character_id = $2 AND session_id = $3 AND version = $4"),
            cancellationToken,
            expectedOwner.StructureId,
            expectedOwner.CharacterId,
            expectedOwner.SessionId,
            expectedOwner.Version) > 0;
    }

    /// <summary>Restores a displaced pilot only while the failed binding still owns the exact acquired version.</summary>
    public async Task<StructurePilotRecord?> RestoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StructurePilotRecord expectedOwner,
        StructurePilotRecord displaced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(displaced);
        if (!IsValid(expectedOwner) || !IsValid(displaced) || expectedOwner.StructureId != displaced.StructureId)
            return null;

        var structure = await _structures.GetAsync(connection, transaction, expectedOwner.StructureId, true, cancellationToken);
        if (structure is null || structure.Snapshot.DestroyedMs != 0)
            return null;

        var current = await ReadAsync(connection, transaction, "WHERE structure_id = $1 FOR UPDATE", cancellationToken, expectedOwner.StructureId);
        if (current != expectedOwner)
            return null;

        var identityOwner = await ReadAsync(
            connection,
            transaction,
            "WHERE structure_id <> $1 AND (character_id = $2 OR session_id = $3) ORDER BY structure_id LIMIT 1 FOR UPDATE",
            cancellationToken,
            displaced.StructureId,
            displaced.CharacterId,
            displaced.SessionId);
        if (identityOwner is not null)
            return null;

        var removed = await Sql.ExecuteAsync(
            connection,
            transaction,
            Q("DELETE FROM {schema}.structure_pilots WHERE structure_id = $1 AND character_id = $2 AND session_id = $3 AND version = $4"),
            cancellationToken,
            expectedOwner.StructureId,
            expectedOwner.CharacterId,
            expectedOwner.SessionId,
            expectedOwner.Version);
        if (removed != 1)
            return null;

        var restored = await InsertAsync(
            connection,
            transaction,
            displaced with { Version = await NextVersionAsync(connection, transaction, cancellationToken) },
            cancellationToken);
        if (restored is not null)
            return restored;

        if (await InsertAsync(connection, transaction, current, cancellationToken) is null)
            throw new InvalidOperationException("The failed structure pilot rollback could not restore its current owner.");
        return null;
    }

    public Task<int> ClearOnRestartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default) =>
        Sql.ExecuteAsync(connection, transaction, Q("DELETE FROM {schema}.structure_pilots"), cancellationToken);

    private async Task<long> NextVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = Sql.Command(connection, transaction, Q("SELECT nextval('{schema}.structure_pilot_versions')"));
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private async Task<StructurePilotRecord?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string where,
        CancellationToken cancellationToken,
        params object?[] values)
    {
        await using var command = Sql.Command(
            connection,
            transaction,
            Q($"SELECT {Columns} FROM {{schema}}.structure_pilots {where}"),
            values);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private async Task<StructurePilotRecord?> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StructurePilotRecord pilot,
        CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            connection,
            transaction,
            Q($"INSERT INTO {{schema}}.structure_pilots ({Columns}) VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING RETURNING {Columns}"),
            pilot.StructureId,
            pilot.CharacterId,
            pilot.SessionId,
            pilot.OriginalShipId,
            pilot.OriginalLocationId,
            pilot.FromSpace,
            pilot.Version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static StructurePilotRecord Read(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.GetBoolean(5),
        reader.GetInt64(6));

    private static void Validate(StructurePilotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePositive(request.StructureId, nameof(request.StructureId));
        RequirePositive(request.CharacterId, nameof(request.CharacterId));
        RequirePositive(request.SessionId, nameof(request.SessionId));
        RequirePositive(request.OriginalShipId, nameof(request.OriginalShipId));
        if (request.OriginalLocationId is long locationId)
            RequirePositive(locationId, nameof(request.OriginalLocationId));
    }

    private static bool IsValid(StructurePilotRecord pilot) =>
        pilot.StructureId > 0 &&
        pilot.CharacterId > 0 &&
        pilot.SessionId > 0 &&
        pilot.OriginalShipId > 0 &&
        (pilot.OriginalLocationId is null or > 0) &&
        pilot.Version > 0;

    private static void RequirePositive(long value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name);
    }
}
