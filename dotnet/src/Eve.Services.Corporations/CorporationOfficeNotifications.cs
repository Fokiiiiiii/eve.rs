using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Corporations;

public static class CorporationOfficeNotifications
{
    // The client item row uses typeOffice; the persisted office folder has its own SDE type.
    public const int OfficeTypeId = 27;

    private const int OfficeFolderFlag = 2;
    private const int ImpoundedFlag = 6;
    private const int JunkyardLocationId = 10;

    public static void Rented(CharacterSessions sessions, long corporationId, long stationId, long officeFolderId,
        ItemType officeType)
    {
        Validate(sessions, corporationId, stationId, officeFolderId, officeType);
        SendItemsChanged(sessions, corporationId, stationId,
            new ItemRow(officeFolderId, officeType.TypeId, corporationId, stationId, OfficeFolderFlag, -1,
                officeType.GroupId, officeType.CategoryId),
            new PyDict([new(new PyInt(3), new PyInt(0))]));
        RentalChanged(sessions, corporationId, stationId, officeFolderId);
    }

    public static void Unrented(CharacterSessions sessions, long corporationId, long stationId, long officeFolderId,
        ItemType officeType, bool sendItemChange)
    {
        Validate(sessions, corporationId, stationId, officeFolderId, officeType);
        if (sendItemChange)
        {
            SendItemsChanged(sessions, corporationId, stationId,
                new ItemRow(officeFolderId, officeType.TypeId, corporationId, JunkyardLocationId, ImpoundedFlag, -1,
                    officeType.GroupId, officeType.CategoryId),
                new PyDict([
                    new(new PyInt(3), InventoryWire.Id(stationId)),
                    new(new PyInt(4), new PyInt(OfficeFolderFlag)),
                ]));
        }
        RentalChanged(sessions, corporationId, stationId, officeFolderId);
    }

    public static void RentalChanged(CharacterSessions sessions, long corporationId, long stationId, long officeFolderId)
    {
        Validate(sessions, corporationId, stationId, officeFolderId, null);
        PyValue[] args = [InventoryWire.Id(corporationId), new PyLong(officeFolderId)];
        foreach (var session in sessions.All().Where(session => session.DockedLocationId == stationId))
            Send(session, "OnOfficeRentalChange", "stationid", args);
        foreach (var session in sessions.All().Where(session => CorporationId(session) == corporationId))
            Send(session, "OnOfficeRentalChange", "corpid", args);
    }

    private static void SendItemsChanged(CharacterSessions sessions, long corporationId, long stationId,
        ItemRow item, PyValue change)
    {
        PyValue[] args =
        [
            new PyList([InventoryWire.PackedRow(item, PyNone.Instance)]),
            change,
            PyNone.Instance,
        ];
        foreach (var session in sessions.All()
                     .Where(session => session.DockedLocationId == stationId || CorporationId(session) == corporationId)
                     .ToHashSet())
            Send(session, "OnItemsChanged", "*stationid&corpid", args);
    }

    private static long? CorporationId(ClientSession session) =>
        session.GetInt("corporationID") ?? session.GetInt("corpid");

    private static void Validate(CharacterSessions sessions, long corporationId, long stationId, long officeFolderId,
        ItemType? officeType)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (corporationId <= 0) throw new ArgumentOutOfRangeException(nameof(corporationId));
        if (stationId <= 0) throw new ArgumentOutOfRangeException(nameof(stationId));
        if (officeFolderId <= 0) throw new ArgumentOutOfRangeException(nameof(officeFolderId));
        if (officeType is not null && officeType.TypeId != OfficeTypeId)
            throw new ArgumentException("Unexpected office notification type.", nameof(officeType));
    }

    private static void Send(ClientSession session, string name, string idType, params PyValue[] payload)
    {
        try { session.SendNotification(name, idType, payload); }
        catch (Exception) { }
    }
}
