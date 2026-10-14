using Eve.Inventory;

namespace Eve.Services.Space.Tests;

public sealed class SiteContainerRestartTests
{
    private const long Site = 60_000_001;

    [PgFact]
    public async Task A_committed_inventory_move_clears_the_last_loot_and_survives_a_bus_restart()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Site Loot Pilot");
        Assert.Equal(Rig.Cockroach, (await rig.Items.GetAsync(pilot.ShipId))!.TypeId);
        var session = await rig.SessionAsync(pilot);
        var shipBinding = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", shipBinding,
            new Eve.Marshal.PyLong(session.GetInt("shipid")!.Value), Eve.Marshal.PyBool.False);
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);

        var schema = "site_rt_" + Guid.NewGuid().ToString("N")[..10];
        rig.KeepSchemas(schema);
        await SiteContainerStore.MigrateAsync(rig.Source, schema);
        var store = new SiteContainerStore(rig.Source, schema);
        var typeRows = await new Eve.Sde.SdeReader(rig.Source).ReadByFieldAsync("types", "groupID", new long[] { 306, 314, 1207 });
        var relicType = typeRows
            .Where(row => row.Doc.GetProperty("groupID").GetInt32() == 1207)
            .OrderBy(row => row.Key)
            .First();
        var containerTypeId = checked((int)relicType.Key);
        var groupId = relicType.Doc.GetProperty("groupID").GetInt32();
        long containerId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            containerId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(containerTypeId, 0, systemId, Eve.Ships.CharacterShips.InSpaceFlag, 1, true));
            await store.InsertAsync(connection, transaction,
            [
                new SiteContainerRecord(containerId, Site, systemId, "research_laboratory", true,
                    containerTypeId, groupId, [1_000d, 2_000d, 3_000d], 50, 4, 0, "secure", rig.Now + 86_400_000),
            ]);
            await transaction.CommitAsync();
        }

        rig.Items.Events = rig.Events;
        rig.Containers.SiteContainers = store;
        var removed = new List<long>();
        var cleared = new List<long>();

        Eve.Services.Space.SiteContainerHacks NewHacks(Eve.Events.EventBus events) =>
            new(rig.Source, new SiteContainerStore(rig.Source, schema), rig.Items, events, () => rig.Now, id =>
            {
                removed.Add(id);
                rig.Containers.RemoveSiteContainer(id);
            });

        void Subscribe(Eve.Events.EventBus events, Eve.Services.Space.SiteContainerHacks hacks)
        {
            events.Subscribe("site.container.emptied", [PgItemStore.ItemMovedTopic], async (context, token) =>
            {
                if (context.Event.Json().TryGetProperty("previousLocationId", out var previous) && previous.TryGetInt64(out var previousLocationId))
                    await hacks.OnItemLeftAsync(context.Connection, context.Transaction, previousLocationId, token);
            });
            events.Subscribe("test.site-container-cleared", [SiteContainerTopics.Cleared], (context, _) =>
            {
                cleared.Add(context.Event.Json().GetProperty("container").GetInt64());
                return Task.CompletedTask;
            });
        }

        var hacks = NewHacks(rig.Events);
        Subscribe(rig.Events, hacks);
        await rig.Events.PumpAsync();

        var target = (await hacks.FindAsync(containerId, CancellationToken.None))!;
        await hacks.EndedAsync(target, won: true, pilot.CharacterId, CancellationToken.None);
        Assert.Equal("looted", (await store.GetAsync(containerId))!.State);
        Assert.Equal(0L, (await rig.Items.GetAsync(containerId))!.OwnerId);

        // Rebuilding the scene from persisted site state restores this still-looted ball.
        rig.Containers.RemoveSiteContainer(containerId);
        await rig.Containers.RestoreSiteContainersAsync(systemId);
        Assert.Contains(rig.Sim.Heard.OfType<Eve.Space.SimDeployed>(), deployed => deployed.DeployableId == containerId);

        var loot = (await rig.Items.InLocationAsync(containerId, pilot.CharacterId)).ToList();
        Assert.NotEmpty(loot);
        if (loot.Count == 1)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Items.CreateAsync(connection, transaction, new NewItem(34, pilot.CharacterId, containerId, ItemFlags.Hangar, 1, true));
            await transaction.CommitAsync();
            loot = (await rig.Items.InLocationAsync(containerId, pilot.CharacterId)).ToList();
        }

        // A rolled-back move leaves neither a changed item nor a durable move fact.
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, loot[0].Id, Rig.Station, ItemFlags.Hangar);
            await transaction.RollbackAsync();
        }
        await DrainAsync(rig.Events);
        Assert.Equal("looted", (await store.GetAsync(containerId))!.State);
        Assert.Empty(cleared);
        Assert.Empty(removed);

        var shipInventory = await rig.BindInventoryShipInSpaceAsync(session);
        foreach (var piece in loot.Take(loot.Count - 1))
        {
            var result = await rig.InventoryCallAsync(session, "MultiAdd", shipInventory,
                [new Eve.Marshal.PyList([new Eve.Marshal.PyLong(piece.Id)]), new Eve.Marshal.PyLong(containerId)]);
            Assert.Equal(Eve.Marshal.PyBool.True, result);
        }

        // Concurrent requests for the final stack serialize at CharacterShips; one commits and one observes no source item.
        var last = loot[^1];
        var concurrent = await Task.WhenAll(
            rig.Ships.MoveAllInSpaceAsync(pilot.CharacterId, [last.Id], containerId, Eve.Ships.CharacterShips.CargoFlag),
            rig.Ships.MoveAllInSpaceAsync(pilot.CharacterId, [last.Id], containerId, Eve.Ships.CharacterShips.CargoFlag));
        Assert.Single(concurrent.OfType<Eve.Ships.ShipChanged>());
        Assert.Single(concurrent.OfType<Eve.Ships.NothingHappened>());
        Assert.Equal("looted", (await store.GetAsync(containerId))!.State);

        var restartedBus = new Eve.Events.EventBus(
            rig.Source,
            new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, null, rig.SchedulerSchema),
            new Eve.Events.EventBusOptions { MaxAttempts = 3 },
            rig.EventsSchema);
        var restartedStore = new SiteContainerStore(rig.Source, schema);
        var restartedHacks = NewHacks(restartedBus);
        rig.Containers.SiteContainers = restartedStore;
        rig.Containers.RemoveSiteContainer(containerId);
        await rig.Containers.RestoreSiteContainersAsync(systemId);
        Assert.Contains(rig.Sim.Heard.OfType<Eve.Space.SimDeployed>(), deployed => deployed.DeployableId == containerId);
        Subscribe(restartedBus, restartedHacks);
        await DrainAsync(restartedBus);

        Assert.Equal("cleared", (await restartedStore.GetAsync(containerId))!.State);
        Assert.Equal([containerId], cleared);
        Assert.Equal([containerId], removed);
        Assert.DoesNotContain(await restartedStore.InSystemAsync(systemId), record => record.ContainerId == containerId);
    }

    private static async Task DrainAsync(Eve.Events.EventBus events)
    {
        while ((await events.PumpAsync()).Delivered > 0)
        {
        }
    }
}
