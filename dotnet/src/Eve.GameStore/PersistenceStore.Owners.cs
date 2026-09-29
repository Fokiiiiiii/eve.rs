using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Eve.GameStore;

public sealed partial class PersistenceStore
{
    [GeneratedRegex(@"\A[A-Za-z0-9_.:-]+\z")]
    private static partial Regex RolePattern();

    private enum RowLock
    {
        None,
        Share,
        Update,
    }

    private sealed record OwnerRow(string Role, long Epoch, string InstanceId, bool Active, bool Exclusive, long LeaseExpiresAt);

    private static string NormalizeRole(string? value)
    {
        var role = (value ?? "").Trim();
        if (role.Length == 0 || role.Length > 128 || !RolePattern().IsMatch(role))
        {
            throw new PersistenceException($"invalid persistence owner role: \"{value}\"", PersistenceCodes.OwnerRoleInvalid);
        }
        return role;
    }

    private static string NormalizeInstance(string? value)
    {
        var instance = (value ?? "").Trim();
        if (instance.Length == 0 || instance.Length > 320)
        {
            throw new PersistenceException(
                "persistence owner instanceId must be a non-empty string no longer than 320 characters",
                PersistenceCodes.OwnerInstanceInvalid);
        }
        return instance;
    }

    private static OwnerFence NormalizeFence(OwnerFence? fence)
    {
        if (fence is null)
        {
            throw new PersistenceException("persistence operation requires an acquired owner fence", PersistenceCodes.OwnerFenceRequired);
        }
        var role = NormalizeRole(fence.OwnerRole);
        var instance = NormalizeInstance(fence.InstanceId);
        if (fence.Epoch <= 0)
        {
            throw new PersistenceException("persistence owner epoch must be a positive integer", PersistenceCodes.OwnerEpochInvalid);
        }
        return new OwnerFence(role, fence.Epoch, instance, fence.LeaseExpiresAt);
    }

    private static long NormalizeLease(long? leaseMs)
    {
        var value = leaseMs ?? DefaultLeaseMs;
        if (value < MinLeaseMs)
        {
            throw new PersistenceException(
                $"persistence owner leaseMs must be an integer of at least {MinLeaseMs}",
                PersistenceCodes.OwnerLeaseInvalid);
        }
        return value;
    }

    private async Task<OwnerRow?> ReadOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string role, RowLock rowLock, CancellationToken ct)
    {
        var suffix = rowLock switch
        {
            RowLock.Share => " FOR SHARE",
            RowLock.Update => " FOR UPDATE",
            _ => "",
        };
        await using var command = Command(
            connection, tx,
            $"SELECT owner_role, epoch, instance_id, active, exclusive, lease_expires_at FROM {Qualified(TableNames.Owners)} WHERE owner_role = @role{suffix}",
            ("role", NpgsqlDbType.Text, role));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new OwnerRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetInt64(5))
            : null;
    }

    private static PersistenceException Stale(OwnerFence fence, long? leaseExpiresAt) => new(
        $"persistence owner {fence.OwnerRole} epoch {fence.Epoch} is stale or inactive",
        PersistenceCodes.OwnerStale,
        new Dictionary<string, object?>
        {
            ["ownerRole"] = fence.OwnerRole,
            ["ownerEpoch"] = fence.Epoch,
            ["leaseExpiresAt"] = leaseExpiresAt,
        });

    /// <summary>
    /// Checks the fence inside <paramref name="tx"/> and, for the default lock,
    /// keeps a share lock on the owner row until the transaction ends: a
    /// successor cannot advance the epoch between this check and the commit.
    /// </summary>
    private async Task<OwnerFence> AssertActiveAsync(NpgsqlConnection connection, NpgsqlTransaction tx, OwnerFence? requested, CancellationToken ct, RowLock rowLock = RowLock.Share)
    {
        var fence = NormalizeFence(requested);
        var stored = await ReadOwnerAsync(connection, tx, fence.OwnerRole, rowLock, ct);
        if (stored is null
            || !stored.Active
            || stored.Epoch != fence.Epoch
            || stored.InstanceId != fence.InstanceId
            || stored.LeaseExpiresAt <= await NowAsync(connection, tx, ct))
        {
            throw Stale(fence, stored?.LeaseExpiresAt);
        }
        return fence with { LeaseExpiresAt = stored.LeaseExpiresAt };
    }

    /// <summary>Validates a fence without doing anything else.</summary>
    public async Task<OwnerFence> AssertOwnerAsync(OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        return await AssertActiveAsync(connection, tx, fence, ct);
    }

    /// <summary>
    /// Takes (or, for the same live instance, re-takes) a role. Taking it from
    /// another instance advances the epoch, which fences that instance out.
    /// </summary>
    public async Task<OwnerFence> AcquireOwnerAsync(string ownerRole, string instanceId, AcquireOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AcquireOptions();
        var role = NormalizeRole(ownerRole);
        var instance = NormalizeInstance(instanceId);
        var exclusive = options.Exclusive || role == PersistenceRoles.Maintenance;
        var leaseMs = NormalizeLease(options.LeaseMs);

        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, "owners", ct);
        var now = await NowAsync(connection, tx, ct);
        var leaseExpiresAt = now + leaseMs;

        var existing = await ReadOwnerAsync(connection, tx, role, RowLock.Update, ct);
        var live = existing is { Active: true } && existing.LeaseExpiresAt > now;
        var sameLiveInstance = live && existing!.InstanceId == instance;
        if (live && !sameLiveInstance && !options.ForceTakeover)
        {
            throw new PersistenceException(
                $"persistence owner {role} is active in another instance",
                PersistenceCodes.OwnerActive,
                new Dictionary<string, object?>
                {
                    ["ownerRole"] = role,
                    ["ownerEpoch"] = existing!.Epoch,
                    ["ownerInstanceId"] = existing.InstanceId,
                    ["leaseExpiresAt"] = existing.LeaseExpiresAt,
                });
        }

        var conflicting = new List<OwnerRow>();
        await using (var command = Command(
            connection, tx,
            $"""
            SELECT owner_role, epoch, instance_id, active, exclusive, lease_expires_at
            FROM {Qualified(TableNames.Owners)}
            WHERE active AND lease_expires_at > @now AND owner_role <> @role {(exclusive ? "" : "AND exclusive")}
            ORDER BY owner_role
            """,
            ("now", NpgsqlDbType.Bigint, now), ("role", NpgsqlDbType.Text, role)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                conflicting.Add(new OwnerRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetInt64(5)));
            }
        }
        if (conflicting.Count > 0 && !options.ForceExclusive)
        {
            throw new PersistenceException(
                $"persistence owner {role} conflicts with active {string.Join(", ", conflicting.Select(owner => owner.Role))}",
                PersistenceCodes.OwnerConflict,
                new Dictionary<string, object?> { ["ownerRole"] = role, ["conflictingOwners"] = conflicting.Select(owner => owner.Role).ToArray() });
        }
        if (conflicting.Count > 0)
        {
            // A proven-offline maintenance transition: deactivate every conflicting
            // lease in this transaction so a stale process fails closed if it resumes.
            await ExecuteAsync(
                connection, tx,
                $"""
                UPDATE {Qualified(TableNames.Owners)}
                SET active = false, lease_expires_at = 0, released_at = now()
                WHERE active AND lease_expires_at > @now AND owner_role <> @role
                """,
                ct, ("now", NpgsqlDbType.Bigint, now), ("role", NpgsqlDbType.Text, role));
        }

        var epoch = sameLiveInstance ? existing!.Epoch : existing is null ? 1 : existing.Epoch + 1;
        await ExecuteAsync(
            connection, tx,
            $"""
            INSERT INTO {Qualified(TableNames.Owners)} (owner_role, epoch, instance_id, active, exclusive, lease_expires_at, acquired_at, released_at)
            VALUES (@role, @epoch, @instance, true, @exclusive, @lease, now(), NULL)
            ON CONFLICT (owner_role) DO UPDATE SET
              epoch = EXCLUDED.epoch,
              instance_id = EXCLUDED.instance_id,
              active = true,
              exclusive = EXCLUDED.exclusive,
              lease_expires_at = EXCLUDED.lease_expires_at,
              acquired_at = EXCLUDED.acquired_at,
              released_at = NULL
            """,
            ct,
            ("role", NpgsqlDbType.Text, role), ("epoch", NpgsqlDbType.Bigint, epoch), ("instance", NpgsqlDbType.Text, instance),
            ("exclusive", NpgsqlDbType.Boolean, exclusive), ("lease", NpgsqlDbType.Bigint, leaseExpiresAt));
        await tx.CommitAsync(ct);
        return new OwnerFence(role, epoch, instance, leaseExpiresAt);
    }

    /// <summary>Extends the lease of a fence that is still valid.</summary>
    public async Task<OwnerFence> RenewOwnerAsync(OwnerFence fence, long? leaseMs = null, CancellationToken ct = default)
    {
        var lease = NormalizeLease(leaseMs);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct, RowLock.Update);
        var leaseExpiresAt = await NowAsync(connection, tx, ct) + lease;
        var changed = await ExecuteAsync(
            connection, tx,
            $"""
            UPDATE {Qualified(TableNames.Owners)} SET lease_expires_at = @lease
            WHERE owner_role = @role AND epoch = @epoch AND instance_id = @instance AND active
            """,
            ct,
            ("lease", NpgsqlDbType.Bigint, leaseExpiresAt), ("role", NpgsqlDbType.Text, active.OwnerRole),
            ("epoch", NpgsqlDbType.Bigint, active.Epoch), ("instance", NpgsqlDbType.Text, active.InstanceId));
        if (changed != 1)
        {
            throw Stale(active, null);
        }
        await tx.CommitAsync(ct);
        return active with { LeaseExpiresAt = leaseExpiresAt };
    }

    /// <summary>Gives the role up so another instance can take it immediately.</summary>
    public async Task ReleaseOwnerAsync(OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct, RowLock.Update);
        var changed = await ExecuteAsync(
            connection, tx,
            $"""
            UPDATE {Qualified(TableNames.Owners)} SET active = false, lease_expires_at = 0, released_at = now()
            WHERE owner_role = @role AND epoch = @epoch AND instance_id = @instance AND active
            """,
            ct,
            ("role", NpgsqlDbType.Text, active.OwnerRole), ("epoch", NpgsqlDbType.Bigint, active.Epoch),
            ("instance", NpgsqlDbType.Text, active.InstanceId));
        if (changed != 1)
        {
            throw new PersistenceException($"failed to release persistence owner {active.OwnerRole}", PersistenceCodes.OwnerReleaseFailed);
        }
        await tx.CommitAsync(ct);
    }
}
