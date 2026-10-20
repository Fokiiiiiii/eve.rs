using Eve.Corporations;
using Npgsql;
using Xunit;

namespace Eve.Corporations.Tests;

/// <summary>
/// </summary>
public sealed class StationBillCreditorMigrationTests
{
    private const long LegacyCorporation = 80_000_001, ChargedCorporation = 80_000_002;
    private const long LegacyStation = 60_000_001, ChargedStation = 60_000_002, Owner = 1000044, Creditor = 98_000_009;
    private static readonly CorporationOfficeStation Legacy = new(LegacyStation, 1, Owner, 30000142, 0.9);
    private static readonly CorporationOfficeStation Charged = new(ChargedStation, 1, Owner, 30000142, 0.9);

    [PgFact]
    public async Task A_station_bill_without_a_creditor_gets_the_station_owner_once_and_its_open_bill_is_returned_for_its_job()
    {
        await using var rig = await Rig.CreateAsync();
        var (legacyOffice, chargedOffice) = await rig.OfficeStore.WriteAsync(async (connection, transaction) =>
            (await RentAtAsync(rig, connection, transaction, Legacy, LegacyCorporation, creditor: null),
             await RentAtAsync(rig, connection, transaction, Charged, ChargedCorporation, creditor: Creditor)));

        Assert.Equal([LegacyStation], await rig.OfficeStore.StationsWithoutCreditorAsync());
        Assert.Null((await OpenBillAsync(rig, legacyOffice.OfficeId))!.CreditorId);

        var credited = await CreditAsync(rig, LegacyStation);

        var bill = Assert.Single(credited);
        Assert.Equal((legacyOffice.OfficeId, Owner), (bill.OfficeId, bill.CreditorId));
        Assert.Empty(await rig.OfficeStore.StationsWithoutCreditorAsync());
        Assert.Equal(Owner, (await OpenBillAsync(rig, legacyOffice.OfficeId))!.CreditorId);
        Assert.Equal(Creditor, (await OpenBillAsync(rig, chargedOffice.OfficeId))!.CreditorId);

        // a second start: nothing is credited and nothing is scheduled again
        Assert.Empty(await CreditAsync(rig, LegacyStation));
        Assert.Equal(Owner, (await OpenBillAsync(rig, legacyOffice.OfficeId))!.CreditorId);
    }

    private static async Task<CorporationOffice> RentAtAsync(Rig rig, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CorporationOfficeStation station, long corporationId, long? creditor)
    {
        await rig.OfficeStore.LockStationAsync(connection, transaction, station.Id);
        var folder = await rig.Items.CreateAsync(connection, transaction,
            new Eve.Inventory.NewItem(CorporationOfficeStore.OfficeFolderTypeId, corporationId, station.Id, Eve.Inventory.ItemFlags.Hangar, Singleton: true));
        return await rig.OfficeStore.RentAsync(connection, transaction, corporationId, folder, station, CorporationOfficeStore.MinimumRentIsk, rig.Now,
            creditorId: creditor);
    }

    private static async Task<IReadOnlyList<CorporationOfficeRentBill>> CreditAsync(Rig rig, long stationId) =>
        await rig.OfficeStore.WriteAsync(async (connection, transaction) =>
            await rig.OfficeStore.CreditStationBillsAsync(connection, transaction, stationId, Owner));

    private static async Task<CorporationOfficeRentBill?> OpenBillAsync(Rig rig, long officeId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        return await rig.OfficeStore.OpenBillForOfficeAsync(connection, null, officeId, forUpdate: false);
    }
}
