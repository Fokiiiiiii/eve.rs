using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;

namespace Eve.Services.Space.Tests;

public sealed class FitShipsHostTests
{
    private static PyDict EmptyMap() => new([]);
    private static PyValue FitInfo() => Py.KeyVal(("modulesByFlag", EmptyMap()));

    [PgFact]
    public async Task FitShips_through_the_registered_ship_service_splits_only_requested_hulls_and_names_them()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([601]);
        var character = await rig.NewCharacterAsync("Multifit tester");
        var session = await rig.SessionAsync(character);
        var boundShip = await rig.BindShipAsync(session);
        long packageId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            packageId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(601, character.CharacterId, Rig.Station, ItemFlags.Hangar, 3, Singleton: false));
            await transaction.CommitAsync();
        }

        var result = await rig.RegisteredCallAsync(session, boundShip, "FitShips",
            new PyInt(601), FitInfo(), new PyLong(Rig.Station), EmptyMap(), PyBool.True,
            new PyStr("Ibis multifit"), new PyInt(2), new PyLong(0));

        Assert.Same(PyNone.Instance, result);
        var items = await rig.Items.InLocationAsync(Rig.Station, character.CharacterId);
        var hulls = items.Where(item => item.TypeId == 601).ToArray();
        Assert.Equal(3, hulls.Length);
        var remainingStack = Assert.Single(hulls, item => item.Id == packageId);
        Assert.False(remainingStack.Singleton);
        Assert.Equal(1, remainingStack.Quantity);
        var assembled = hulls.Where(item => item.Id != packageId).ToArray();
        Assert.Equal(2, assembled.Length);
        Assert.All(assembled, item =>
        {
            Assert.True(item.Singleton);
            Assert.Equal(1, item.Quantity);
            Assert.Equal("Ibis multifit", item.CustomName);
        });
    }

    [PgFact]
    public async Task FitShips_applies_the_requested_module_to_each_assembled_hull()
    {
        await using var rig = await Rig.CreateAsync();
        const int hullTypeId = 601;
        const int moduleTypeId = 3_634;
        const int highSlotFlag = 27;
        await rig.Statics.EnsureTypesAsync([hullTypeId, moduleTypeId]);
        var character = await rig.NewCharacterAsync("Multifit module tester");
        var session = await rig.SessionAsync(character);
        var boundShip = await rig.BindShipAsync(session);
        long packageId;
        long moduleId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            packageId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(hullTypeId, character.CharacterId, Rig.Station, ItemFlags.Hangar, 1, Singleton: false));
            moduleId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(moduleTypeId, character.CharacterId, Rig.Station, ItemFlags.Hangar, 1, Singleton: true));
            await transaction.CommitAsync();
        }
        var fitInfo = Py.KeyVal(("modulesByFlag", new PyDict([
            new(new PyInt(highSlotFlag), new PyInt(moduleTypeId)),
        ])));

        Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(session, boundShip, "FitShips",
            new PyInt(hullTypeId), fitInfo, new PyLong(Rig.Station), EmptyMap(), PyBool.True,
            new PyStr("Laser ibis"), new PyInt(1), new PyLong(0)));

        var hull = (await rig.Items.GetAsync(packageId))!;
        var module = (await rig.Items.GetAsync(moduleId))!;
        Assert.True(hull.Singleton);
        Assert.Equal((character.CharacterId, Rig.Station, ItemFlags.Hangar), (hull.OwnerId, hull.LocationId, hull.Flag));
        Assert.Equal((character.CharacterId, packageId, highSlotFlag, true, 1L),
            (module.OwnerId, module.LocationId, module.Flag, module.Singleton, module.Quantity));
        Assert.Equal("Laser ibis", hull.CustomName);
    }

    [PgFact]
    public async Task FitShips_rejects_a_caller_supplied_location_that_is_not_the_docked_session_location()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync("Multifit location tester");
        var session = await rig.SessionAsync(character);
        var boundShip = await rig.BindShipAsync(session);
        long packageId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            packageId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(601, character.CharacterId, Rig.Station, ItemFlags.Hangar, 1, Singleton: false));
            await transaction.CommitAsync();
        }

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.RegisteredCallAsync(session, boundShip, "FitShips",
            new PyInt(601), FitInfo(), new PyLong(Rig.Station + 1), EmptyMap(), PyBool.True,
            new PyStr("Ibis multifit"), new PyInt(1), new PyLong(0)));

        Assert.Equal("CannotLoadFittingInSpace", error.Error.UserErrorMessage);
        var unchanged = (await rig.Items.GetAsync(packageId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar, 1L, false),
            (unchanged.LocationId, unchanged.Flag, unchanged.Quantity, unchanged.Singleton));
    }
}
