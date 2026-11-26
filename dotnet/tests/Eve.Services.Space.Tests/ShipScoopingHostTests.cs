using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;

namespace Eve.Services.Space.Tests;

public sealed class ShipScoopingHostTests
{
    [PgFact]
    public async Task Scoop_uses_the_registered_ship_route_and_moves_the_container_tree_into_cargo()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var (session, bound, canId, cargoId, systemId, characterId, shipId) =
            await MakeSpaceCanAsync(rig, withCargo: true, shipTypeId: 28606);
        Events.Of(session);

        Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(session, bound, "Scoop", new PyLong(canId)));

        var can = (await rig.Items.GetAsync(canId))!;
        Assert.Equal((characterId, shipId, CharacterShips.CargoFlag), (can.OwnerId, can.LocationId, can.Flag));
        Assert.Equal((canId, 0, 5L), ((await rig.Items.GetAsync(cargoId))!.LocationId,
            (await rig.Items.GetAsync(cargoId))!.Flag, (await rig.Items.GetAsync(cargoId))!.Quantity));
        Assert.Null(await rig.SpaceItems.GetAsync(canId));
        Assert.Contains(rig.Sim.Heard, message => message is SimRemoveWreck remove && remove.WreckId == canId);
        Assert.Equal("OnItemChange", Assert.Single(Events.Of(session)).Name);
    }

    [PgFact]
    public async Task Scoop_returns_the_client_typed_range_error_without_mutating_a_distant_target()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var (session, bound, canId, _, _, _, _) = await MakeSpaceCanAsync(rig);
        rig.Sim.MeasureAnswer = 2_501;
        Events.Of(session);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.RegisteredCallAsync(
            session, bound, "Scoop", new PyLong(canId)));

        Assert.Equal("CantUseTooDistantWithType", error.Error.UserErrorMessage);
        Assert.Equal((session.GetInt("solarsystemid")!.Value, CharacterShips.InSpaceFlag),
            ((await rig.Items.GetAsync(canId))!.LocationId, (await rig.Items.GetAsync(canId))!.Flag));
        Assert.NotNull(await rig.SpaceItems.GetAsync(canId));
        Assert.Empty(Events.Of(session));
        Assert.DoesNotContain(rig.Sim.Heard, message => message is SimRemoveWreck remove && remove.WreckId == canId);
    }

    [PgFact]
    public async Task Scoop_checks_the_saved_secure_container_password_and_allows_the_matching_credential()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var secureType = (await rig.Statics.TypesInGroupsAsync(["Secure Cargo Container"]))
            .First(type => type.GroupId is 340 or 448);
        var (session, bound, containerId, _, systemId, characterId, shipId) =
            await MakeSpaceContainerAsync(rig, secureType.TypeId,
                "{\"secureContainerState\":{\"anchored\":true,\"generalPassword\":\"secret\"}}", shipTypeId: 28606);
        Events.Of(session);

        var denied = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.RegisteredCallAsync(
            session, bound, "Scoop", new PyLong(containerId), new PyStr("wrong")));

        Assert.Equal("ShpScoopSecureCC", denied.Error.UserErrorMessage);
        Assert.Equal((systemId, CharacterShips.InSpaceFlag),
            ((await rig.Items.GetAsync(containerId))!.LocationId, (await rig.Items.GetAsync(containerId))!.Flag));
        Assert.Empty(Events.Of(session));

        Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(
            session, bound, "Scoop", new PyLong(containerId), new PyStr("secret")));

        var scooped = (await rig.Items.GetAsync(containerId))!;
        Assert.Equal((characterId, shipId, CharacterShips.CargoFlag), (scooped.OwnerId, scooped.LocationId, scooped.Flag));
        Assert.Null(await rig.SpaceItems.GetAsync(containerId));
        Assert.Equal("OnItemChange", Assert.Single(Events.Of(session)).Name);
    }

    [PgFact]
    public async Task ScoopToFleetHangar_uses_the_registered_route_and_persists_the_selected_hold()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var (session, bound, canId, _, _, characterId, shipId) =
            await MakeSpaceCanAsync(rig, shipTypeId: 28_606);
        Events.Of(session);

        Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(
            session, bound, "ScoopToFleetHangar", new PyLong(canId)));

        var can = (await rig.Items.GetAsync(canId))!;
        Assert.Equal((characterId, shipId, CharacterShips.FleetHangarFlag), (can.OwnerId, can.LocationId, can.Flag));
        Assert.Null(await rig.SpaceItems.GetAsync(canId));
        Assert.Contains(rig.Sim.Heard, message => message is SimRemoveWreck remove && remove.WreckId == canId);
        Assert.Equal("OnItemChange", Assert.Single(Events.Of(session)).Name);
    }

    [PgFact]
    public async Task ScoopToInfrastructureHold_accepts_a_mobile_depot_when_the_SDE_hull_has_that_hold()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        const int squallTypeId = 81_008;
        await rig.Statics.EnsureTypesAsync([squallTypeId]);
        var depotType = (await rig.Statics.TypesInGroupsAsync(["Mobile Depot"]))
            .First(type => type.Name == "Mobile Depot");
        var (session, bound, systemId, characterId, shipId) = await MakeSpacePilotAsync(rig, shipTypeId: squallTypeId);
        var depotId = await MakeSpaceContainerItemAsync(rig, characterId, shipId, systemId, depotType.TypeId, null);
        var character = await rig.Characters.GetAsync(characterId) ?? throw new InvalidOperationException("Test pilot is missing.");
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var depotSpace = (await rig.SpaceItems.GetAsync(connection, transaction, depotId))!;
            await rig.SpaceItems.SaveAsync(connection, transaction, depotSpace with
            {
                CharacterId = characterId,
                CorporationId = character.CorporationId,
                AllianceId = character.AllianceId ?? 0,
            });
            await rig.Deployed.SaveAsync(connection, transaction, new DeployedRecord(
                depotId, "mobile_depot", systemId, "Mobile Depot", depotType.TypeId, depotType.GroupId,
                [0, 0, 0], 50, characterId, checked((int)character.CorporationId), checked((int)(character.AllianceId ?? 0)),
                shipId, rig.Now + 86_400_000));
            await transaction.CommitAsync();
        }
        Events.Of(session);

        Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(
            session, bound, "ScoopToInfrastructureHold", new PyLong(depotId)));

        var depot = (await rig.Items.GetAsync(depotId))!;
        Assert.Equal((characterId, shipId, 185), (depot.OwnerId, depot.LocationId, depot.Flag));
        Assert.Null(await rig.SpaceItems.GetAsync(depotId));
        Assert.Null(await rig.Deployed.GetAsync(depotId));
        Assert.Contains(rig.Sim.Heard.OfType<SimRemoveDeployed>(), removal => removal.DeployableId == depotId);
        Assert.Equal("OnItemsChanged", Assert.Single(Events.Of(session)).Name);
    }

    [PgFact]
    public async Task ScoopToMobileDepotHold_ejects_contents_to_a_persisted_space_can_before_notifying_the_scooped_depot()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var depotType = (await rig.Statics.TypesInGroupsAsync(["Mobile Depot"]))
            .First(type => type.Name == "Mobile Depot");
        var (session, bound, systemId, characterId, shipId) = await MakeSpacePilotAsync(rig, shipTypeId: 77114);
        var depotId = await MakeSpaceContainerItemAsync(rig, characterId, shipId, systemId, depotType.TypeId, null);
        const int oldContentFlag = 5;
        long contentId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            contentId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(34, characterId, depotId, oldContentFlag, 3, Singleton: false));
            var depotSpace = (await rig.SpaceItems.GetAsync(connection, transaction, depotId))!;
            var character = await rig.Characters.GetAsync(characterId) ?? throw new InvalidOperationException("Test pilot is missing.");
            await rig.SpaceItems.SaveAsync(connection, transaction, depotSpace with
            {
                CharacterId = characterId,
                CorporationId = character.CorporationId,
                AllianceId = character.AllianceId ?? 0,
            });
            await rig.Deployed.SaveAsync(connection, transaction, new DeployedRecord(
                depotId, "mobile_depot", systemId, "Mobile Depot", depotType.TypeId, depotType.GroupId,
                [0, 0, 0], 50, characterId, checked((int)character.CorporationId), checked((int)(character.AllianceId ?? 0)),
                shipId, rig.Now + 86_400_000));
            await transaction.CommitAsync();
        }
        Events.Of(session);

        Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(session, bound, "ScoopToMobileDepotHold", new PyLong(depotId)));

        var ejected = Assert.Single(rig.Sim.Heard.OfType<SimPlaceWreck>(), wreck => wreck.WreckId != depotId);
        var can = (await rig.Items.GetAsync(ejected.WreckId))!;
        var content = (await rig.Items.GetAsync(contentId))!;
        var depot = (await rig.Items.GetAsync(depotId))!;
        var canSpace = (await rig.SpaceItems.GetAsync(can.Id))!;
        Assert.Equal((characterId, systemId, CharacterShips.InSpaceFlag, 1L),
            (can.OwnerId, can.LocationId, can.Flag, can.Quantity));
        Assert.Equal((can.Id, CharacterShips.ContainerFlag, characterId, 3L),
            (content.LocationId, content.Flag, content.OwnerId, content.Quantity));
        Assert.Equal((characterId, shipId, 176), (depot.OwnerId, depot.LocationId, depot.Flag));
        Assert.Equal((systemId, shipId, rig.Now + 7_200_000),
            (canSpace.SolarSystemId, canSpace.LauncherId, canSpace.ExpiresAtMs));
        Assert.Null(await rig.SpaceItems.GetAsync(depotId));
        Assert.Null(await rig.Deployed.GetAsync(depotId));

        var notifications = Events.Of(session);
        Assert.Equal(["OnItemsChanged", "OnItemsChanged", "OnItemsChanged"], notifications.Select(e => e.Name));
        Assert.Contains(rig.Sim.Heard.OfType<SimRemoveDeployed>(), removal => removal.DeployableId == depotId);
        Assert.Equal(ejected.Position, canSpace.Position);
        Assert.Equal(ejected.Direction, canSpace.Direction);
    }

    [PgFact]
    public async Task ScoopToMobileDepotHold_names_the_depot_hold_when_the_depot_does_not_fit()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var depotType = (await rig.Statics.TypesInGroupsAsync(["Mobile Depot"]))
            .First(type => type.Name == "Mobile Depot");
        var (session, bound, systemId, characterId, shipId) = await MakeSpacePilotAsync(rig, shipTypeId: 77114);
        var depotId = await MakeMobileDepotAsync(rig, characterId, shipId, systemId, depotType.TypeId, depotType.GroupId);
        var capacity = (await rig.Ships.RoomAsync(characterId, shipId, 176))!.Value.Capacity;
        await rig.Statics.EnsureTypesAsync([34]);
        var unitVolume = rig.Statics.Type(34)!.Attributes[rig.Statics.AttributeId("volume")!.Value];
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.CreateAsync(connection, transaction,
                new NewItem(34, characterId, shipId, 176, (long)Math.Floor(capacity / unitVolume), Singleton: false));
            await transaction.CommitAsync();
        }
        Events.Of(session);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.RegisteredCallAsync(
            session, bound, "ScoopToMobileDepotHold", new PyLong(depotId)));

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.Equal("There is not enough room in the Mobile Depot Hold.", NoticeOf(error));
        var depot = (await rig.Items.GetAsync(depotId))!;
        Assert.Equal((systemId, CharacterShips.InSpaceFlag), (depot.LocationId, depot.Flag));
        Assert.NotNull(await rig.SpaceItems.GetAsync(depotId));
        Assert.NotNull(await rig.Deployed.GetAsync(depotId));
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task ScoopToMobileDepotHold_names_the_refusal_when_the_pilot_is_not_in_space()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var depotType = (await rig.Statics.TypesInGroupsAsync(["Mobile Depot"]))
            .First(type => type.Name == "Mobile Depot");
        var character = await rig.NewCharacterAsync("Docked scoop pilot", shipTypeId: 77114);
        var session = await rig.SessionAsync(character);
        var bound = await rig.BindShipAsync(session);
        // A docked session has no solar system; the depot only needs a system id, and the pilot is refused as not in space.
        const int systemId = 30000142;
        var depotId = await MakeMobileDepotAsync(rig, character.CharacterId, character.ShipId, systemId,
            depotType.TypeId, depotType.GroupId);
        Events.Of(session);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.RegisteredCallAsync(
            session, bound, "ScoopToMobileDepotHold", new PyLong(depotId)));

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.Equal("Unable to scoop this item.", NoticeOf(error));
        Assert.NotNull(await rig.SpaceItems.GetAsync(depotId));
        Assert.NotNull(await rig.Deployed.GetAsync(depotId));
        Assert.Empty(Events.Of(session));
    }

    private static async Task<(ClientSession Session, string Bound, long Can, long Cargo, int System, long Character, long Ship)>
        MakeSpaceCanAsync(Rig rig, bool withCargo = false, int shipTypeId = 587)
    {
        var (session, bound, systemId, characterId, shipId) = await MakeSpacePilotAsync(rig, shipTypeId);
        var canId = await MakeSpaceContainerItemAsync(rig, characterId, shipId, systemId, 3296, null);
        long cargoId = 0;
        if (withCargo)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            cargoId = await rig.Items.CreateAsync(connection, transaction, new NewItem(34, characterId, canId, 0, 5, Singleton: false));
            await transaction.CommitAsync();
        }
        return (session, bound, canId, cargoId, systemId, characterId, shipId);
    }

    private static async Task<(ClientSession Session, string Bound, long Container, long Cargo, int System, long Character, long Ship)>
        MakeSpaceContainerAsync(Rig rig, int typeId, string? customInfo, int shipTypeId = 587)
    {
        var (session, bound, systemId, characterId, shipId) = await MakeSpacePilotAsync(rig, shipTypeId);
        var itemId = await MakeSpaceContainerItemAsync(rig, characterId, shipId, systemId, typeId, customInfo);
        return (session, bound, itemId, 0, systemId, characterId, shipId);
    }

    private static async Task<(ClientSession Session, string Bound, int System, long Character, long Ship)> MakeSpacePilotAsync(
        Rig rig, int shipTypeId = 587)
    {
        var character = await rig.NewCharacterAsync("Scoop pilot", shipTypeId: shipTypeId);
        var session = await rig.SessionAsync(character);
        var bound = await rig.BindShipAsync(session);
        Assert.Same(PyNone.Instance, await rig.ShipCallAsync(session, "Undock", bound,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False));
        await rig.BindBeyonceAsync(session);
        bound = await rig.BindShipInSpaceAsync(session);
        return (session, bound, checked((int)session.GetInt("solarsystemid")!.Value), character.CharacterId, character.ShipId);
    }

    private static async Task<long> MakeSpaceContainerItemAsync(
        Rig rig, long characterId, long shipId, int systemId, int typeId, string? customInfo)
    {
        await rig.Statics.EnsureTypesAsync([typeId]);
        var type = rig.Statics.Type(typeId)!;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = await rig.Items.CreateAsync(connection, transaction,
            new NewItem(typeId, characterId, systemId, CharacterShips.InSpaceFlag, 1, Singleton: true, CustomInfo: customInfo));
        await rig.SpaceItems.SaveAsync(connection, transaction, new SpaceItem(
            itemId, systemId, [0, 0, 0], [1, 0, 0], Math.Max(1, type.Attributes.GetValueOrDefault(162, 1)),
            type.GroupId, shipId, characterId, 0, 0, 0, rig.Now + 86_400_000));
        await transaction.CommitAsync();
        return itemId;
    }

    private static async Task<long> MakeMobileDepotAsync(
        Rig rig, long characterId, long shipId, int systemId, int typeId, int groupId)
    {
        var depotId = await MakeSpaceContainerItemAsync(rig, characterId, shipId, systemId, typeId, null);
        var character = await rig.Characters.GetAsync(characterId) ?? throw new InvalidOperationException("Test pilot is missing.");
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var depotSpace = (await rig.SpaceItems.GetAsync(connection, transaction, depotId))!;
        await rig.SpaceItems.SaveAsync(connection, transaction, depotSpace with
        {
            CharacterId = characterId,
            CorporationId = character.CorporationId,
            AllianceId = character.AllianceId ?? 0,
        });
        await rig.Deployed.SaveAsync(connection, transaction, new DeployedRecord(
            depotId, "mobile_depot", systemId, "Mobile Depot", typeId, groupId,
            [0, 0, 0], 50, characterId, checked((int)character.CorporationId), checked((int)(character.AllianceId ?? 0)),
            shipId, rig.Now + 86_400_000));
        await transaction.CommitAsync();
        return depotId;
    }

    private static string NoticeOf(MachoWrappedException error)
    {
        var header = (PyTuple)((PyObjectEx)error.Error.Payload).Header;
        var args = (PyTuple)header.Items[1];
        return Eve.Macho.JsSemantics.PlainText(Assert.Single(Assert.IsType<PyDict>(args.Items[1]).Entries).Value) ?? string.Empty;
    }

}
