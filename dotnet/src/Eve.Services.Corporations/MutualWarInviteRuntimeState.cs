using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Services;
using Npgsql;

namespace Eve.Services.Corporations;

/// <summary>Persistent authority for mutual war invitations, their block setting and the war created on acceptance.</summary>
public sealed class MutualWarInviteRuntimeState(
    NpgsqlDataSource source,
    RelationStore relations,
    CharacterSessions? sessions,
    Func<long> nowMs,
    Func<long, long, CancellationToken, Task<bool>> canManageWarAsync)
{
    private static readonly string InviteKind = RelationKinds.MutualWarInvite.Name;
    private static readonly string BlockKind = RelationKinds.MutualWarInviteBlocked.Name;
    private const long InviteLifetimeMs = 30L * 24 * 60 * 60 * 1000;
    private const long WarStartupMs = 24L * 60 * 60 * 1000;

    public async ValueTask<IReadOnlyList<MutualWarInvite>> ReadPendingInvitesAsync(
        long ownerId, CancellationToken cancellationToken)
    {
        if (ownerId <= 0) return [];
        var from = await relations.FromAsync(InviteKind, ownerId, cancellationToken: cancellationToken);
        var to = await relations.ToAsync(InviteKind, ownerId, cancellationToken: cancellationToken);
        var invites = new Dictionary<(long From, long To), MutualWarInvite>();
        foreach (var invite in from.Concat(to))
        {
            if (invite.UntilMs is { } expiry && expiry <= nowMs()) continue;
            using var attrs = JsonDocument.Parse(string.IsNullOrWhiteSpace(invite.AttrsJson) ? "{}" : invite.AttrsJson);
            if (!attrs.RootElement.TryGetProperty("sentDate", out var sentDate) ||
                !long.TryParse(sentDate.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var sentFiletime))
                continue;
            invites[(invite.Subject, invite.Object)] = new(invite.Subject, invite.Object, sentFiletime);
        }
        return [.. invites.Values];
    }

    public async ValueTask<bool> ReadInvitesBlockedAsync(long ownerId, CancellationToken cancellationToken) =>
        ownerId > 0 && await relations.HoldsAsync(BlockKind, ownerId, ownerId, cancellationToken: cancellationToken);

    public async ValueTask SendInviteByPlayerAsync(
        ServiceCall call, long fromOwnerId, long toOwnerId, CancellationToken cancellationToken)
    {
        if (fromOwnerId <= 0 || toOwnerId <= 0 || fromOwnerId == toOwnerId) return;
        await RequireWarDirectorAsync(call, fromOwnerId, cancellationToken);
        var now = nowMs();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockPairAsync(connection, transaction, fromOwnerId, toOwnerId, cancellationToken);
        if (await relations.HoldsAsync(BlockKind, toOwnerId, toOwnerId, connection, transaction, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var sentFiletime = Py.FileTimeFromUnixMs(now);
        await relations.SetAsync(connection, transaction, InviteKind, fromOwnerId, toOwnerId, 1,
            "sent", new { sentDate = sentFiletime.ToString(CultureInfo.InvariantCulture) },
            checked(now + InviteLifetimeMs), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        CorporationNotificationAdapter.MutualWarsUpdated(sessions, call, fromOwnerId, toOwnerId);
    }

    public async ValueTask WithdrawInviteByPlayerAsync(
        ServiceCall call, long fromOwnerId, long toOwnerId, CancellationToken cancellationToken)
    {
        await RequireWarDirectorAsync(call, fromOwnerId, cancellationToken);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockPairAsync(connection, transaction, fromOwnerId, toOwnerId, cancellationToken);
        var ended = await relations.EndAsync(connection, transaction, InviteKind, fromOwnerId, toOwnerId,
            "withdrawn", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (ended is not null)
            CorporationNotificationAdapter.MutualWarsUpdated(sessions, call, fromOwnerId, toOwnerId);
    }

    public async ValueTask RespondToInviteByPlayerAsync(
        ServiceCall call, long fromOwnerId, long toOwnerId, bool accepts, CancellationToken cancellationToken)
    {
        await RequireWarDirectorAsync(call, toOwnerId, cancellationToken);
        long? changedWarId = null;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await LockPairAsync(connection, transaction, fromOwnerId, toOwnerId, cancellationToken);
            var invite = await relations.GetAsync(InviteKind, fromOwnerId, toOwnerId,
                connection, transaction, cancellationToken, forUpdate: true);
            if (invite is null) return;
            if (accepts)
                changedWarId = await AcceptMutualWarAsync(call, connection, transaction,
                    fromOwnerId, toOwnerId, cancellationToken);
            await relations.EndAsync(connection, transaction, InviteKind, fromOwnerId, toOwnerId,
                accepts ? "accepted" : "declined", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (changedWarId is { } warId)
        {
            var wars = await WarsInfoMgrService.ReadWarsAsync(relations, cancellationToken);
            if (wars.FirstOrDefault(war => war.WarId == warId) is { } war)
                CorporationNotificationAdapter.WarChanged(sessions, call, war.DeclaredById, war.AgainstId,
                    WarRegistryService.Payload(war));
        }
        CorporationNotificationAdapter.MutualWarsUpdated(sessions, call, fromOwnerId, toOwnerId);
    }

    public async ValueTask SetInvitesBlockedByPlayerAsync(
        ServiceCall call, long ownerId, bool blocked, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(connection, transaction, ownerId, cancellationToken);
        RelationChange? changed = blocked
            ? await relations.SetAsync(connection, transaction, BlockKind, ownerId, ownerId, 1, "blocked",
                cancellationToken: cancellationToken)
            : await relations.EndAsync(connection, transaction, BlockKind, ownerId, ownerId,
                "unblocked", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (changed is not null)
            CorporationNotificationAdapter.MutualWarsUpdated(sessions, call, ownerId);
    }

    private async Task<long?> AcceptMutualWarAsync(
        ServiceCall call, NpgsqlConnection connection, NpgsqlTransaction transaction,
        long fromOwnerId, long toOwnerId, CancellationToken cancellationToken)
    {
        var existing = await relations.GetAsync(RelationKinds.War.Name, fromOwnerId, toOwnerId,
            connection, transaction, cancellationToken, forUpdate: true)
            ?? await relations.GetAsync(RelationKinds.War.Name, toOwnerId, fromOwnerId,
                connection, transaction, cancellationToken, forUpdate: true);
        if (existing is not null)
        {
            var attrs = JsonNode.Parse(string.IsNullOrWhiteSpace(existing.AttrsJson) ? "{}" : existing.AttrsJson) as JsonObject
                ?? new JsonObject();
            if (ReadLong(attrs, "mutual") == 1) return null;
            attrs["mutual"] = 1;
            attrs["openForAllies"] = 0;
            var change = await relations.SetAsync(connection, transaction, RelationKinds.War.Name, existing.Subject, existing.Object,
                existing.Value, "mutual", attrs, null, cancellationToken);
            if (change is null) return null;
            foreach (var ally in await relations.ToAsync(RelationKinds.WarAlly.Name, existing.Id, connection, transaction, cancellationToken))
                await relations.EndAsync(connection, transaction, RelationKinds.WarAlly.Name, ally.Subject, ally.Object,
                    "mutual war", cancellationToken);
            return existing.Id;
        }

        var now = nowMs();
        var declared = Py.FileTimeFromUnixMs(now).ToString(CultureInfo.InvariantCulture);
        var started = Py.FileTimeFromUnixMs(checked(now + WarStartupMs)).ToString(CultureInfo.InvariantCulture);
        var attrsForNewWar = new
        {
            declaredByCharacterID = call.Session.CharacterId.GetValueOrDefault(),
            timeDeclared = declared,
            timeStarted = started,
            timeFinished = (string?)null,
            retracted = (string?)null,
            retractedBy = (long?)null,
            billID = (long?)null,
            mutual = 1,
            fightingLegalNotified = 0,
            openForAllies = 0,
            createdFromWarID = (long?)null,
            warHQID = (long?)null,
            warHQ = (long?)null,
            reasonEnded = 0,
            reasonStarted = 0,
            reward = 0,
        };
        var created = await relations.SetAsync(connection, transaction, RelationKinds.War.Name, fromOwnerId, toOwnerId,
            1, "mutual invitation accepted", attrsForNewWar, null, cancellationToken);
        return created?.RelationId;
    }

    private async Task RequireWarDirectorAsync(ServiceCall call, long ownerId, CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId ?? 0;
        if (characterId <= 0 || ownerId <= 0 ||
            !await canManageWarAsync(characterId, ownerId, cancellationToken))
            throw MachoErrors.ThrowUserError("CrpAccessDenied",
                ("reason", new PyStr("UI/Corporations/AccessRestrictions/NotDirector")));
    }

    private static async Task LockPairAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long firstOwnerId, long secondOwnerId,
        CancellationToken cancellationToken)
    {
        if (firstOwnerId > secondOwnerId) (firstOwnerId, secondOwnerId) = (secondOwnerId, firstOwnerId);
        await LockOwnerAsync(connection, transaction, firstOwnerId, cancellationToken);
        if (secondOwnerId != firstOwnerId)
            await LockOwnerAsync(connection, transaction, secondOwnerId, cancellationToken);
    }

    private static Task LockOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long ownerId, CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('mutual-war-invite:' || $1::text, 0))",
            connection, transaction);
        command.Parameters.AddWithValue(ownerId);
        return ExecuteAndDisposeAsync(command, cancellationToken);
    }

    private static async Task ExecuteAndDisposeAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using (command)
            await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static long ReadLong(JsonObject attrs, string key) =>
        long.TryParse(attrs[key]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}
