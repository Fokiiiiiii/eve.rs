using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Xunit;

namespace Eve.Services.Space.Tests;

public sealed class SpaceContainersUnanchorDropsTests
{
    private const int Astrahus = 35832;
    private const int AmarrControlTower = 12235;
    private const int SolarSystemId = 30000142;
    private const long StructureId = 9_020_011;
    private const long CorporationId = 98_000_021;
    private const long AllianceId = 99_000_021;
    private const int SmallShieldBooster = 1103;
    private const int Obelisk = 20187;

    [PgFact]
    public async Task Committed_unanchor_event_places_SDE_groups_and_restart_restore_keeps_them()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([Astrahus, StructureUnanchorDrop.FittingsContainerTypeId]);
        rig.Containers.SubscribeUnanchorDrops(rig.Events);
        await rig.Events.PumpAsync();

        var drops = await CreateArtifactsAsync(rig, SolarSystemId);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Events.PublishInTransactionAsync(connection, transaction, StructureTimers.StateChangedTopic,
                new StructureStateChanged(StructureId, SolarSystemId, StructureState.Unanchored, UpkeepState.LowPower,
                    new StructureTimerInfo(0, 0, 0), false, "unanchored",
                    UnanchorDrop: new StructureUnanchorDrop(drops.HullId, drops.FittingsId)), $"structure:{StructureId}");
            await transaction.CommitAsync();
        }
        await rig.Events.PumpAsync();

        var live = rig.Sim.Heard.OfType<SimPlaceWreck>().Where(message => message.WreckId == drops.HullId || message.WreckId == drops.FittingsId)
            .ToDictionary(message => message.WreckId);
        Assert.Equal(2, live.Count);
        Assert.Equal(rig.Statics.Type(Astrahus)!.GroupId, live[drops.HullId].GroupId);
        Assert.Equal(306, live[drops.FittingsId].GroupId);
        Assert.Equal(StructureUnanchorDrop.FittingsContainerTypeId, live[drops.FittingsId].Kind.TypeId);

        rig.Sim.Heard.Clear();
        rig.Sim.Lose();
        await rig.Containers.RestoreAsync(SolarSystemId);

        var restored = rig.Sim.Heard.OfType<SimPlaceWreck>().Where(message => message.WreckId == drops.HullId || message.WreckId == drops.FittingsId)
            .ToDictionary(message => message.WreckId);
        Assert.Equal(2, restored.Count);
        Assert.Equal(rig.Statics.Type(Astrahus)!.GroupId, restored[drops.HullId].GroupId);
        Assert.Equal(306, restored[drops.FittingsId].GroupId);
    }

    [PgFact]
    public async Task Bound_ship_scoop_moves_the_fittings_artifact_and_children_then_checks_hull_hold_room()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([Astrahus, StructureUnanchorDrop.FittingsContainerTypeId, SmallShieldBooster, Obelisk]);
        var character = await rig.NewCharacterAsync("Unanchor scoop pilot", shipTypeId: Obelisk);
        var session = await rig.SessionAsync(character, 1);
        var shipOid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipOid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        shipOid = await rig.BindShipInSpaceAsync(session);
        var system = checked((int)session.GetInt("solarsystemid")!.Value);
        var shipId = session.GetInt("shipid")!.Value;
        Assert.NotEqual(CorporationId, session.GetInt("corpid"));
        rig.Sim.MeasureAnswer = 25;

        var fittingsType = rig.Statics.Type(StructureUnanchorDrop.FittingsContainerTypeId)!;
        var fittingsId = await AddDropAsync(rig, system, StructureId, StructureUnanchorDrop.FittingsContainerTypeId,
            StructureUnanchorDrop.FittingsMarker(StructureId), "Astrahus Decommissioned Fittings", 306, fittingsType);
        long contentId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            contentId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(SmallShieldBooster, CorporationId, fittingsId, ItemFlags.Hangar, Quantity: 1, Singleton: true));
            await transaction.CommitAsync();
        }
        var containerBall = BallOf(rig, system, fittingsId, StructureUnanchorDrop.FittingsContainerTypeId, 306,
            StructureUnanchorDrop.FittingsMarker(StructureId), StructureId, empty: false);
        rig.Sim.PlaceWreck(containerBall);

        Assert.Equal(fittingsId, (await rig.Containers.OpenAsync(session, fittingsId, CancellationToken.None))!.Id);
        Events.Of(session);
        await rig.ShipCallAsync(session, "Scoop", shipOid, new PyLong(fittingsId));

        var scoopedFittings = Assert.IsType<Eve.Inventory.Item>(await rig.Items.GetAsync(fittingsId));
        var scoopedContent = Assert.IsType<Eve.Inventory.Item>(await rig.Items.GetAsync(contentId));
        Assert.Equal((character.CharacterId, shipId, CharacterShips.CargoFlag),
            (scoopedFittings.OwnerId, scoopedFittings.LocationId, scoopedFittings.Flag));
        Assert.Equal((SmallShieldBooster, character.CharacterId, fittingsId, ItemFlags.Hangar, true, 1L),
            (scoopedContent.TypeId, scoopedContent.OwnerId, scoopedContent.LocationId, scoopedContent.Flag, scoopedContent.Singleton, scoopedContent.Quantity));
        Assert.Null(await rig.SpaceItems.GetAsync(fittingsId));
        Assert.Contains(rig.Sim.Heard.OfType<SimRemoveWreck>(), message => message.WreckId == fittingsId && message.Effect == 0);
        var notifications = Events.Of(session);
        Assert.Contains(notifications, notification => notification.Name == "OnItemsChanged");
        Assert.Contains(notifications, notification => notification.Name == "OnItemChange");

        var hullType = rig.Statics.Type(Astrahus)!;
        var hullId = await AddDropAsync(rig, system, StructureId, Astrahus,
            StructureUnanchorDrop.HullMarker(StructureId), "Unanchored Astrahus", hullType.GroupId, hullType, CorporationId);
        var hullBall = BallOf(rig, system, hullId, Astrahus, hullType.GroupId,
            StructureUnanchorDrop.HullMarker(StructureId), StructureId, empty: true);
        rig.Sim.PlaceWreck(hullBall);

        MachoWrappedException? scoopRefusal = null;
        try
        {
            await rig.ShipCallAsync(session, "Scoop", shipOid, new PyLong(hullId));
        }
        catch (MachoWrappedException error)
        {
            scoopRefusal = error;
        }

        var hullAfter = Assert.IsType<Eve.Inventory.Item>(await rig.Items.GetAsync(hullId));
        if (scoopRefusal is null)
        {
            Assert.Equal((character.CharacterId, shipId, CharacterShips.CargoFlag),
                (hullAfter.OwnerId, hullAfter.LocationId, hullAfter.Flag));
            Assert.Null(await rig.SpaceItems.GetAsync(hullId));
            Assert.Contains(rig.Sim.Heard.OfType<SimRemoveWreck>(), message => message.WreckId == hullId && message.Effect == 0);
        }
        else
        {
            Assert.Equal("NotEnoughCargoSpace", scoopRefusal.Error.UserErrorMessage);
            Assert.Equal((CorporationId, system, CharacterShips.InSpaceFlag),
                (hullAfter.OwnerId, hullAfter.LocationId, hullAfter.Flag));
            Assert.NotNull(await rig.SpaceItems.GetAsync(hullId));
            Assert.DoesNotContain(rig.Sim.Heard.OfType<SimRemoveWreck>(), message => message.WreckId == hullId);
        }
    }

    [PgFact]
    public async Task Bound_ship_scoop_rejects_another_corporations_control_tower()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([AmarrControlTower]);
        var character = await rig.NewCharacterAsync("TowerScoopPilot");
        var session = await rig.SessionAsync(character, 1);
        var shipOid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipOid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        shipOid = await rig.BindShipInSpaceAsync(session);
        var system = checked((int)session.GetInt("solarsystemid")!.Value);
        var shipId = session.GetInt("shipid")!.Value;
        Assert.NotEqual(CorporationId, session.GetInt("corpid"));
        rig.Sim.MeasureAnswer = 25;

        var towerType = rig.Statics.Type(AmarrControlTower)!;
        var towerId = await AddDropAsync(rig, system, StructureId, AmarrControlTower,
            StructureUnanchorDrop.HullMarker(StructureId), "Unanchored Amarr Control Tower", towerType.GroupId, towerType);
        rig.Sim.PlaceWreck(BallOf(rig, system, towerId, AmarrControlTower, towerType.GroupId,
            StructureUnanchorDrop.HullMarker(StructureId), StructureId, empty: true));

        MachoWrappedException? refusal = null;
        try
        {
            await rig.ShipCallAsync(session, "Scoop", shipOid, new PyLong(towerId));
        }
        catch (MachoWrappedException error)
        {
            refusal = error;
        }

        Assert.NotNull(refusal);
        Assert.Equal("FakeItemNotFound", refusal.Error.UserErrorMessage);
        var towerAfter = Assert.IsType<Eve.Inventory.Item>(await rig.Items.GetAsync(towerId));
        Assert.Equal((CorporationId, system, CharacterShips.InSpaceFlag),
            (towerAfter.OwnerId, towerAfter.LocationId, towerAfter.Flag));
        Assert.NotNull(await rig.SpaceItems.GetAsync(towerId));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimRemoveWreck>(), message => message.WreckId == towerId);
    }

    [PgFact]
    public async Task Scoop_transaction_rejects_an_upwell_hull_owner_that_disagrees_with_its_recorded_corporation()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([Astrahus]);
        var character = await rig.NewCharacterAsync("HullOwnerMismatch");
        var session = await rig.SessionAsync(character, 1);
        var shipOid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipOid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindShipInSpaceAsync(session);
        var system = checked((int)session.GetInt("solarsystemid")!.Value);
        var shipId = session.GetInt("shipid")!.Value;
        var pilot = (await rig.Characters.GetAsync(character.CharacterId))!;

        var hullType = rig.Statics.Type(Astrahus)!;
        var marker = StructureUnanchorDrop.HullMarker(StructureId);
        var hullId = await AddDropAsync(rig, system, StructureId, Astrahus, marker,
            "Mismatched owner Astrahus", hullType.GroupId, hullType, character.CharacterId);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Ships.ScoopSpaceArtifactAsync(character.CharacterId, pilot.CorporationId, false,
                shipId, system, hullId, marker, rig.SpaceItems));

        Assert.Contains("custody record", error.Message, StringComparison.Ordinal);
        var hullAfter = Assert.IsType<Eve.Inventory.Item>(await rig.Items.GetAsync(hullId));
        Assert.Equal((character.CharacterId, system, CharacterShips.InSpaceFlag),
            (hullAfter.OwnerId, hullAfter.LocationId, hullAfter.Flag));
        Assert.NotNull(await rig.SpaceItems.GetAsync(hullId));
    }

    private sealed record Drops(long HullId, long FittingsId);

    private static async Task<Drops> CreateArtifactsAsync(Rig rig, int system)
    {
        var hullType = rig.Statics.Type(Astrahus)!;
        var fittingsType = rig.Statics.Type(StructureUnanchorDrop.FittingsContainerTypeId)!;
        var hullId = await AddDropAsync(rig, system, StructureId, Astrahus,
            StructureUnanchorDrop.HullMarker(StructureId), "Unanchored Astrahus", hullType.GroupId, hullType);
        var fittingsId = await AddDropAsync(rig, system, StructureId, StructureUnanchorDrop.FittingsContainerTypeId,
            StructureUnanchorDrop.FittingsMarker(StructureId), "Astrahus Decommissioned Fittings", 306, fittingsType);
        return new Drops(hullId, fittingsId);
    }

    private static async Task<long> AddDropAsync(Rig rig, int system, long structureId, int typeId, string marker, string name, int groupId, TypeDef type, long? ownerId = null)
    {
        var position = new[] { 1.25, 2.5, -3.75 };
        var direction = new[] { 0d, 0d, 0d };
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = await rig.Items.CreateAsync(connection, transaction,
            new NewItem(typeId, ownerId ?? CorporationId, system, CharacterShips.InSpaceFlag, Quantity: 1, Singleton: true,
                CustomName: name, CustomInfo: marker));
        await rig.SpaceItems.SaveAsync(connection, transaction,
            new SpaceItem(itemId, system, position, direction, type.Attributes[162], groupId,
                structureId, 0, CorporationId, AllianceId, 0, long.MaxValue));
        await transaction.CommitAsync();
        return itemId;
    }

    private static SimPlaceWreck BallOf(Rig rig, int system, long itemId, int typeId, int groupId, string marker, long structureId, bool empty)
    {
        var type = rig.Statics.Type(typeId)!;
        return new SimPlaceWreck(SimSceneKey.SolarSystem(system), itemId, groupId,
            new SimWreckKind(typeId, type.Name ?? "", type.GraphicId),
            [1.25, 2.5, -3.75], [0d, 0d, 0d], type.Attributes[162], structureId,
            new SimCredit(0, CorporationId, AllianceId, 0), empty);
    }
}
