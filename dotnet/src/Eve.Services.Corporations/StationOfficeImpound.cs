using Eve.Corporations;
using Eve.Inventory;
using Npgsql;

namespace Eve.Services.Corporations;

/// <summary>
/// The impound of a station office whose rent is not paid, and the un-rent by a Director (EVE.js impoundStationOfficeForUnpaidBill, eve.rs UnrentOffice): the
/// corporation's hangar items at the station move to the Impounded location, the office folder goes, and the office ends with its open bills. The items come back by
/// paying the release fee (GetItemsFromImpound). The caller holds the station lock and the transaction.
/// </summary>
public static class StationOfficeImpound
{
    public static async Task<bool> ImpoundAsync(PgItemStore items, CorporationOfficeStore store, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CorporationOffice office, long nowMs, CancellationToken cancellationToken)
    {
        var corporationId = office.CorporationId;
        var stationId = office.StationId;
        var assets = await items.InLocationAsync(stationId, corporationId, cancellationToken);
        var hadAssets = assets.Any(item => ItemFlags.IsCorporationHangar(item.Flag));
        foreach (var item in assets.Where(item => ItemFlags.IsCorporationHangar(item.Flag)))
            await MoveIfCurrentAsync(items, connection, transaction, item, corporationId, stationId, item.Flag,
                CorporationOfficeStore.ImpoundedFlag, cancellationToken);
        var folder = await items.LockAndGetAsync(connection, transaction, office.OfficeFolderId, cancellationToken);
        if (folder is { OwnerId: var folderOwner, LocationId: var folderLocation, TypeId: CorporationOfficeStore.OfficeFolderTypeId } &&
            folderOwner == corporationId && folderLocation == stationId)
            await items.DeleteAsync(connection, transaction, folder.Id, cancellationToken);
        await store.UnrentAsync(connection, transaction, office, nowMs, cancellationToken);
        return hadAssets;
    }

    /// <summary>Moves one item to <paramref name="destinationFlag"/> if it is still the corporation's at the station with the expected flag.</summary>
    public static async Task MoveIfCurrentAsync(PgItemStore items, NpgsqlConnection connection, NpgsqlTransaction transaction,
        Eve.Inventory.Item snapshot, long corporationId, long stationId, int expectedFlag, int destinationFlag, CancellationToken token)
    {
        var current = await items.LockAndGetAsync(connection, transaction, snapshot.Id, token);
        if (current is { OwnerId: var owner, LocationId: var location, Flag: var flag } &&
            owner == corporationId && location == stationId && flag == expectedFlag)
            await items.MoveAsync(connection, transaction, current.Id, stationId, destinationFlag, token);
    }
}
