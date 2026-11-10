using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class ScoopToSmaHostTests
{
    [PgFact]
    public async Task ScoopToSMA_moves_another_pilots_abandoned_ship_into_the_active_ship_maintenance_bay()
    {
        var scenario = await PrepareAsync();
        await using var rig = scenario.Rig;
        var sink = Assert.IsType<Sink>(scenario.CarrierSession.Sink);
        sink.Sent.Clear();

        var result = await rig.RegisteredCallAsync(scenario.CarrierSession, scenario.CarrierOid, "ScoopToSMA", new PyLong(scenario.TargetShipId));

        Assert.Same(PyNone.Instance, result);
        var stored = (await rig.Items.GetAsync(scenario.TargetShipId))!;
        Assert.Equal((scenario.CarrierCharacterId, scenario.CarrierShipId, CharacterShips.ShipMaintenanceBayFlag),
            (stored.OwnerId, stored.LocationId, stored.Flag));
        Assert.Null(await rig.SpaceItems.GetAsync(scenario.TargetShipId));

        var removal = Assert.Single(rig.Sim.Heard.OfType<SimRemoveUnpilotedShip>());
        Assert.Equal((scenario.TargetShipId, (long)scenario.SystemId), (removal.ShipId, (long)removal.Scene.LocationId));

        var notification = Assert.Single(Events.Of(scenario.CarrierSession));
        Assert.Equal(("OnItemChange", "clientID"), (notification.Name, notification.IdType));
        var row = Assert.IsType<PyPackedRow>(notification.Args[0]);
        Assert.Equal((scenario.TargetShipId, scenario.CarrierCharacterId, scenario.CarrierShipId, (long)CharacterShips.ShipMaintenanceBayFlag),
            (Events.Number(row.Values[0]), Events.Number(row.Values[2]), Events.Number(row.Values[3]), Events.Number(row.Values[4])));
        Assert.Equal(new[]
        {
            (2L, scenario.PreviousOwnerId),
            (3L, (long)scenario.SystemId),
            (4L, 0L),
        }, ((PyDict)notification.Args[1]).Entries
            .Select(entry => (Events.Number(entry.Key), Events.Number(entry.Value)))
            .OrderBy(entry => entry.Item1).ToArray());
    }

    [PgFact]
    public async Task ScoopToSMA_leaves_inventory_unchanged_when_the_authoritative_scene_refuses_removal_and_allows_a_retry()
    {
        var scenario = await PrepareAsync();
        await using var rig = scenario.Rig;
        var original = (await rig.Items.GetAsync(scenario.TargetShipId))!;
        var sink = Assert.IsType<Sink>(scenario.CarrierSession.Sink);
        sink.Sent.Clear();
        rig.Sim.RemoveUnpilotedShipAnswer = request => new(request.RequestId, request.Scene, request.ShipId, false, null);

        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.RegisteredCallAsync(
            scenario.CarrierSession, scenario.CarrierOid, "ScoopToSMA", new PyLong(scenario.TargetShipId)));

        Assert.Equal((original.OwnerId, original.LocationId, original.Flag),
            ((await rig.Items.GetAsync(scenario.TargetShipId))!.OwnerId,
                (await rig.Items.GetAsync(scenario.TargetShipId))!.LocationId,
                (await rig.Items.GetAsync(scenario.TargetShipId))!.Flag));
        Assert.Empty(Events.Of(scenario.CarrierSession));

        rig.Sim.RemoveUnpilotedShipAnswer = null;
        await rig.RegisteredCallAsync(scenario.CarrierSession, scenario.CarrierOid, "ScoopToSMA", new PyLong(scenario.TargetShipId));
        Assert.Equal((scenario.CarrierCharacterId, scenario.CarrierShipId, CharacterShips.ShipMaintenanceBayFlag),
            ((await rig.Items.GetAsync(scenario.TargetShipId))!.OwnerId,
                (await rig.Items.GetAsync(scenario.TargetShipId))!.LocationId,
                (await rig.Items.GetAsync(scenario.TargetShipId))!.Flag));
        Assert.Equal("OnItemChange", Assert.Single(Events.Of(scenario.CarrierSession)).Name);
    }

    [PgFact]
    public async Task ScoopToSMA_refuses_a_target_beyond_the_2500_meter_interaction_range_without_removing_it()
    {
        var scenario = await PrepareAsync();
        await using var rig = scenario.Rig;
        rig.Sim.MeasureAnswer = 2501;
        Assert.IsType<Sink>(scenario.CarrierSession.Sink).Sent.Clear();

        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.RegisteredCallAsync(
            scenario.CarrierSession, scenario.CarrierOid, "ScoopToSMA", new PyLong(scenario.TargetShipId)));

        var target = (await rig.Items.GetAsync(scenario.TargetShipId))!;
        Assert.Equal((scenario.PreviousOwnerId, (long)scenario.SystemId, CharacterShips.InSpaceFlag),
            (target.OwnerId, target.LocationId, target.Flag));
        Assert.DoesNotContain(rig.Sim.Heard, message => message is SimRemoveUnpilotedShip);
        Assert.Empty(Events.Of(scenario.CarrierSession));
    }

    private static async Task<Scenario> PrepareAsync()
    {
        const int orcaTypeId = 28606;
        const int rifterTypeId = 587;
        var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        Assert.Same(rig.Ship, rig.Registry.Lookup("ship"));
        await rig.Statics.EnsureTypesAsync([orcaTypeId, rifterTypeId]);
        var carrier = await rig.NewCharacterAsync("SMA Pilot", account: 1, shipTypeId: orcaTypeId);
        var abandonedPilot = await rig.NewCharacterAsync("Ejected Pilot", account: 2, shipTypeId: rifterTypeId);
        var carrierSession = await rig.SessionAsync(carrier, id: 1);
        var abandonedSession = await rig.SessionAsync(abandonedPilot, id: 2);

        var carrierOid = await rig.BindShipAsync(carrierSession);
        Assert.Same(PyNone.Instance, await rig.ShipCallAsync(carrierSession, "Undock", carrierOid,
            new PyLong(carrierSession.GetInt("shipid")!.Value), PyBool.False));
        carrierOid = await rig.BindShipInSpaceAsync(carrierSession);
        var abandonedOid = await rig.BindShipAsync(abandonedSession);
        Assert.Same(PyNone.Instance, await rig.ShipCallAsync(abandonedSession, "Undock", abandonedOid,
            new PyLong(abandonedSession.GetInt("shipid")!.Value), PyBool.False));
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        Assert.IsType<PyTuple>(await rig.Space.EjectAsync(abandonedSession, CancellationToken.None));

        var targetId = abandonedPilot.ShipId;
        var target = (await rig.Items.GetAsync(targetId))!;
        Assert.Equal((abandonedPilot.CharacterId, carrierSession.GetInt("solarsystemid"), CharacterShips.InSpaceFlag),
            (target.OwnerId, target.LocationId, target.Flag));
        var orca = rig.Statics.Type(orcaTypeId)!;
        Assert.True(orca.Attributes.GetValueOrDefault(rig.Statics.AttributeId("shipMaintenanceBayCapacity")!.Value) > 0);

        return new Scenario(rig, carrierSession, carrier.CharacterId, carrier.ShipId, carrierOid, targetId,
            target.OwnerId, checked((int)carrierSession.GetInt("solarsystemid")!.Value));
    }

    private sealed record Scenario(
        Rig Rig,
        ClientSession CarrierSession,
        long CarrierCharacterId,
        long CarrierShipId,
        string CarrierOid,
        long TargetShipId,
        long PreviousOwnerId,
        int SystemId);
}
