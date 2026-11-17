using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class BoardStoredShipHostTests
{
    [PgFact]
    public async Task BoardStoredShip_through_the_registered_ship_route_swaps_hulls_and_returns_activation_state()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var scenario = await PrepareAsync(rig, CharacterShips.CapsuleTypeId);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        Events.Of(scenario.Session);

        var reply = Assert.IsType<PyTuple>(await rig.RegisteredCallAsync(scenario.Session, scenario.Bound,
            "BoardStoredShip", new PyLong(scenario.SourceId), new PyLong(scenario.StoredShipId)));

        Assert.Equal(4, reply.Items.Count);
        var state = Assert.IsType<PyDict>(reply.Items[0]);
        Assert.Contains(state.Entries, entry => Events.Number(entry.Key) == scenario.StoredShipId);
        Assert.Contains(state.Entries, entry => Events.Number(entry.Key) == scenario.CharacterId);
        Assert.Equal(scenario.StoredShipId, scenario.Session.GetInt("shipid"));
        var stored = (await rig.Items.GetAsync(scenario.StoredShipId))!;
        Assert.Equal((scenario.CharacterId, scenario.SystemId, CharacterShips.InSpaceFlag),
            (stored.OwnerId, stored.LocationId, stored.Flag));
        var previous = (await rig.Items.GetAsync(scenario.PreviousShipId))!;
        Assert.Equal((scenario.CharacterId, scenario.SystemId, CharacterShips.InSpaceFlag),
            (previous.OwnerId, previous.LocationId, previous.Flag));
        var shipChange = Assert.Single(Sequence.LastChange(scenario.Session), change => change.Name == "shipid");
        Assert.Equal(scenario.StoredShipId, Events.Number(shipChange.New));
        Assert.Equal(1, Events.Of(scenario.Session).Count(notification => notification.Name == "OnItemChange"));
        Assert.Contains(rig.Sim.Heard.OfType<SimRespawn>(), spawn => spawn.Ship.ShipId == scenario.StoredShipId);
    }

    [PgFact]
    public async Task BoardStoredShip_stores_the_previous_non_capsule_hull_in_the_same_maintenance_bay()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var scenario = await PrepareAsync(rig, 587);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        Events.Of(scenario.Session);

        var result = await rig.RegisteredCallAsync(scenario.Session, scenario.Bound,
            "BoardStoredShip", new PyLong(scenario.SourceId), new PyLong(scenario.StoredShipId));

        Assert.IsType<PyTuple>(result);
        Assert.Equal(scenario.StoredShipId, scenario.Session.GetInt("shipid"));
        var stored = (await rig.Items.GetAsync(scenario.StoredShipId))!;
        Assert.Equal((scenario.CharacterId, scenario.SystemId, CharacterShips.InSpaceFlag),
            (stored.OwnerId, stored.LocationId, stored.Flag));
        var previous = (await rig.Items.GetAsync(scenario.PreviousShipId))!;
        Assert.Equal((scenario.CharacterId, scenario.SourceId, CharacterShips.ShipMaintenanceBayFlag),
            (previous.OwnerId, previous.LocationId, previous.Flag));
        Assert.Equal(2, Events.Of(scenario.Session).Count(notification => notification.Name == "OnItemChange"));
    }

    private static async Task<Scenario> PrepareAsync(Rig rig, int activeShipTypeId)
    {
        const int orcaTypeId = 28_606;
        const int storedShipTypeId = 603;
        await rig.Statics.EnsureTypesAsync([orcaTypeId, activeShipTypeId, storedShipTypeId]);
        var character = await rig.NewCharacterAsync("Stored ship pilot", shipTypeId: activeShipTypeId);
        var session = await rig.SessionAsync(character);
        var bound = await rig.BindShipAsync(session);
        Assert.Same(PyNone.Instance, await rig.ShipCallAsync(session, "Undock", bound,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False));
        await rig.BindBeyonceAsync(session);
        bound = await rig.BindShipInSpaceAsync(session);
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var sourcePosition = new double[] { 100, 0, 0 };
        var sourceDirection = new double[] { 1, 0, 0 };
        long sourceId;
        long storedShipId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            sourceId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(orcaTypeId, character.CharacterId, systemId, CharacterShips.InSpaceFlag, 1, Singleton: true));
            storedShipId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(storedShipTypeId, character.CharacterId, sourceId, CharacterShips.ShipMaintenanceBayFlag, 1, Singleton: true));
            var sourceType = rig.Statics.Type(orcaTypeId)!;
            await rig.SpaceItems.SaveAsync(connection, transaction, new SpaceItem(
                sourceId, systemId, sourcePosition, sourceDirection,
                sourceType.Attributes.GetValueOrDefault(162), sourceType.GroupId,
                character.ShipId, character.CharacterId, 0, 0, 0, long.MaxValue,
                Kind: SpaceItemKind.LaunchedShip));
            await transaction.CommitAsync();
        }
        rig.Sim.PositionAnswers[sourceId] = sourcePosition;
        Assert.True(await rig.Space.PlaceLaunchedShipAsync(sourceId, character.CharacterId, systemId,
            rig.Statics.Type(orcaTypeId)!, sourcePosition, sourceDirection, CancellationToken.None));
        var service = Assert.IsAssignableFrom<Eve.Services.IService>(rig.Registry.Lookup("ship"));
        Assert.True(service.HasMethod("BoardStoredShip"));
        return new Scenario(rig, session, bound, character.CharacterId, systemId,
            character.ShipId, sourceId, storedShipId);
    }

    private sealed record Scenario(
        Rig Rig,
        ClientSession Session,
        string Bound,
        long CharacterId,
        int SystemId,
        long PreviousShipId,
        long SourceId,
        long StoredShipId);
}
