using System.Runtime.CompilerServices;
using Eve.Content;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

/// <summary>Emits the client-visible Abyss notifications for a snapshotted recipient.</summary>
public sealed class AbyssalNotificationAdapter(CharacterSessions sessions)
{
    private const int NormalPveTimerType = 11;
    private readonly ConditionalWeakTable<ClientSession, HashSet<string>> _deliveryKeys = new();

    private bool TryReserveDelivery(ClientSession session, string key, bool forceResend)
    {
        var keys = _deliveryKeys.GetValue(session, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (keys)
        {
            if (!forceResend && !keys.Add(key))
                return false;
            keys.Add(key);
            return true;
        }
    }

    private void ReleaseDelivery(ClientSession session, string key)
    {
        if (!_deliveryKeys.TryGetValue(session, out var keys))
            return;
        lock (keys) keys.Remove(key);
    }

    public bool SendFilamentConsumed(AbyssalFilamentConsumedEvent change)
    {
        if (sessions.Of(change.CharacterId) is not { } session) return false;
        var key = $"filament:{change.ContentId}:{change.Before.Id}";
        if (!TryReserveDelivery(session, key, false)) return true;
        try
        {
            Eve.Services.Ships.ShipNotifications.SendItemChange(session,
                new Eve.Ships.ItemChanged(change.After, change.Before.LocationId, change.Before.Flag, change.Before.Quantity),
                item => new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag,
                    item.Singleton ? -1 : item.Quantity, change.GroupId, change.CategoryId));
        }
        catch { ReleaseDelivery(session, key); throw; }
        return true;
    }

    public bool SendLocationEntered(
        AbyssalLocationEnteredEvent notification,
        int difficultyTier,
        bool forceResend = false)
    {
        ArgumentNullException.ThrowIfNull(notification);
        RequireRecipient(notification.RecipientCharacterId);
        if (difficultyTier is < 0 or > 6)
            throw new ArgumentOutOfRangeException(nameof(difficultyTier));
        RequireRoomOrdinal(notification.LocationId);
        if (notification.NebulaGraphicId <= 0)
            throw new ArgumentOutOfRangeException(nameof(notification), "The Nebula graphic id must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.ContentId);

        var session = sessions.Of(notification.RecipientCharacterId);
        if (session is null)
            return false;
        var key = $"location-entered:{notification.ContentId}:{notification.LocationId}";
        if (!TryReserveDelivery(session, key, forceResend))
            return true;
        try
        {
            session.SendNotification(
                "OnAbyssalLocationEntered",
                "charid",
                Py.Dict(
                    ("difficulty_tier", new PyInt(difficultyTier)),
                    ("content_id", new PyStr(notification.ContentId)),
                    ("location_id", new PyInt(notification.LocationId)),
                    ("nebula_graphic_id", new PyInt(notification.NebulaGraphicId))));
        }
        catch
        {
            ReleaseDelivery(session, key);
            throw;
        }
        return true;
    }

    /// <summary>Publishes the client dungeon-room packet to a character on the ship channel.</summary>
    public bool SendEnteringDungeonRoom(
        long characterId,
        int dungeonId,
        int roomId,
        IReadOnlyList<double> roomPosition,
        string? contentId = null,
        bool forceResend = false)
    {
        if (characterId <= 0 || dungeonId <= 0 || roomId < 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        ArgumentNullException.ThrowIfNull(roomPosition);
        if (roomPosition.Count != 3 || roomPosition.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("Abyssal room position must contain three finite coordinates.", nameof(roomPosition));
        if (contentId is not null && !Guid.TryParse(contentId, out _))
            throw new ArgumentException("An Abyssal content id must be a UUID.", nameof(contentId));
        if (sessions.Of(characterId) is not { } session)
            return false;

        var position = new PyTuple(roomPosition.Select(value => (PyValue)new PyReal(value)).ToArray());
        var key = contentId is null ? null : $"entering-room:{contentId}:{roomId}";
        if (key is not null && !TryReserveDelivery(session, key, forceResend))
            return true;
        try
        {
            session.SendNotification(
                "OnEnteringDungeonRoom",
                "shipid",
                new PyInt(dungeonId),
                new PyInt(roomId),
                position,
                PyNone.Instance,
                PyNone.Instance);
        }
        catch
        {
            if (key is not null) ReleaseDelivery(session, key);
            throw;
        }
        return true;
    }

    /// <summary>Publishes the client dungeon-exit packet to a character on the ship channel.</summary>
    public bool SendExitingDungeon(long characterId, int dungeonId, string? contentId = null, int roomOrdinal = 0)
    {
        if (characterId <= 0 || dungeonId <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        if (sessions.Of(characterId) is not { } session)
            return false;
        var key = contentId is null ? null : $"dungeon-exited:{contentId}:{roomOrdinal}";
        if (key is not null && !TryReserveDelivery(session, key, forceResend: false))
            return true;
        try
        {
            session.SendNotification("OnExitingDungeon", "shipid", new PyInt(dungeonId));
        }
        catch
        {
            if (key is not null) ReleaseDelivery(session, key);
            throw;
        }
        return true;
    }

    public bool SendDeadlineAdded(
        AbyssalDeadlineAddedEvent notification,
        bool forceResend = false)
    {
        ArgumentNullException.ThrowIfNull(notification);
        RequireRecipient(notification.RecipientCharacterId);
        if (notification.ExpiresAtMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(notification), "The absolute deadline must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.ContentId);

        var session = sessions.Of(notification.RecipientCharacterId);
        if (session is null)
            return false;
        var key = $"deadline:{notification.ContentId}:{notification.ExpiresAtMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (!TryReserveDelivery(session, key, forceResend))
            return true;
        try
        {
            session.SendNotification(
                "OnAddExpiryDurationForAbyssalContent",
                "charid",
                Py.FileTimeLong(Py.FileTimeFromUnixMs(notification.ExpiresAtMs)));
        }
        catch
        {
            ReleaseDelivery(session, key);
            throw;
        }
        return true;
    }

    public bool SendLocationExited(AbyssalLocationExitedEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        RequireRecipient(notification.RecipientCharacterId);
        RequireRoomOrdinal(notification.LocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.ContentId);

        var session = sessions.Of(notification.RecipientCharacterId);
        if (session is null)
            return false;
        var key = $"location-exited:{notification.ContentId}:{notification.LocationId}";
        if (!TryReserveDelivery(session, key, forceResend: false))
            return true;
        try
        {
            session.SendNotification("OnAbyssalLocationExited", "charid");
        }
        catch
        {
            ReleaseDelivery(session, key);
            throw;
        }
        return true;
    }

    public bool SendContentFinished(AbyssalParticipantContentFinishedEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        RequireRecipient(notification.RecipientCharacterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.ContentId);

        var session = sessions.Of(notification.RecipientCharacterId);
        if (session is null)
            return false;
        var key = $"content-finished:{notification.ContentId}";
        if (!TryReserveDelivery(session, key, forceResend: false))
            return true;
        try
        {
            session.SendNotification(
                "OnAbyssalContentFinished",
                "charid",
                new PyStr(notification.ContentId),
                new PyInt(NormalPveTimerType));
        }
        catch
        {
            ReleaseDelivery(session, key);
            throw;
        }
        return true;
    }

    private static void RequireRecipient(long characterId)
    {
        if (characterId <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
    }

    private static void RequireRoomOrdinal(int locationId)
    {
        if (locationId is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(locationId), "Abyss client location_id is a room ordinal from 1 to 3.");
    }
}
