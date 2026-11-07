using System.Text.Json.Nodes;
using Eve.Crimewatch;
using Eve.Events;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Crimewatch;
using Npgsql;

namespace Eve.Host;

internal static class CrimewatchKillRightEvents
{
    internal const string CreatedTopic = "crimewatch.kill_right.created";
    private const string NotificationSubscriber = "crimewatch.kill_right.notifications";

    internal sealed record Created(
        long KillRightId,
        long OwnerCharacterId,
        long TargetCharacterId,
        long SolarSystemId,
        long CreatedAtUnixMs,
        long ExpiresAtUnixMs);

    public static async Task<long?> CreateAndPublishAsync(
        NpgsqlDataSource source,
        PgCrimewatchStore store,
        EventBus events,
        KillRightGrantRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var killRightId = await store.CreateKillRightAsync(
            connection,
            transaction,
            request.OwnerCharacterId,
            request.TargetCharacterId,
            request.SolarSystemId,
            request.CreatedAtUnixMs,
            cancellationToken);
        if (killRightId is not { } id)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var created = new Created(
            id,
            request.OwnerCharacterId,
            request.TargetCharacterId,
            request.SolarSystemId,
            request.CreatedAtUnixMs,
            checked(request.CreatedAtUnixMs + PgCrimewatchStore.KillRightLifetimeMs));
        await events.PublishOnceInTransactionAsync(
            connection,
            transaction,
            CreatedTopic,
            created,
            $"kill-right-created:{id}",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        events.Wake();
        return id;
    }

    public static void Subscribe(
        EventBus events,
        PersistenceMailSendHostAdapter mail,
        CharacterSessions sessions) =>
        events.Subscribe(NotificationSubscriber, [CreatedTopic], async (context, cancellationToken) =>
        {
            var created = context.Event.Payload<Created>()
                ?? throw new InvalidOperationException("Kill-right creation event has no payload.");
            if (created.KillRightId <= 0 || created.OwnerCharacterId <= 0 || created.TargetCharacterId <= 0 ||
                created.OwnerCharacterId == created.TargetCharacterId || created.SolarSystemId <= 0 ||
                created.CreatedAtUnixMs <= 0 ||
                created.ExpiresAtUnixMs != created.CreatedAtUnixMs + PgCrimewatchStore.KillRightLifetimeMs)
                throw new InvalidOperationException("Kill-right creation event has invalid identities or lifetime.");

            await mail.WriteKillRightEarnedNotificationAsync(
                created.OwnerCharacterId,
                created.TargetCharacterId,
                new JsonObject { ["charID"] = created.TargetCharacterId },
                $"kill-right-earned:{created.KillRightId}",
                () => TellCreated(created, sessions),
                cancellationToken);
        });

    private static void TellCreated(Created created, CharacterSessions sessions)
    {
        foreach (var characterId in new[] { created.OwnerCharacterId, created.TargetCharacterId })
        {
            sessions.Of(characterId)?.SendNotification(
                "OnKillRightCreated",
                "clientID",
                new PyLong(created.KillRightId),
                InventoryWire.Id(created.OwnerCharacterId),
                InventoryWire.Id(created.TargetCharacterId),
                new PyLong(Py.FileTimeFromUnixMs(created.ExpiresAtUnixMs)));
        }
    }
}
