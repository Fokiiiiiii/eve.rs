using Eve.Content;
using Eve.Events;
using Eve.Scheduler;
using Eve.Data;
using Eve.Sde;
using Eve.Hacking;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space.Sites;
using Eve.Ships;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>The site's containers as the hacking service sees them: the lookup and the end of an attempt go to the site container store.</summary>
internal sealed class ContainerSite(SiteContainerHacks hacks) : IHackingSite
{
    public Action? AfterSettlement { get; set; }
    public Func<Task>? BeforeReservation { get; set; }

    public async ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken)
    {
        var target = await hacks.FindAsync(targetId, cancellationToken);
        if (target is not null && BeforeReservation is { } beforeReservation)
            await beforeReservation();
        return target;
    }

    public ValueTask<string?> AnalyzerStartRefusalAsync(
        ClientSession session, HackableTarget target, double maxRange, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(null);

    public ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask<bool> TryReserveAsync(ClientSession session, HackableTarget target, Guid reservationId, CancellationToken cancellationToken) =>
        hacks.TryReserveAsync(target, reservationId, session.CharacterId ?? 0, session.SessionId, cancellationToken);

    public ValueTask ReleaseReservationAsync(Guid reservationId, CancellationToken cancellationToken) =>
        hacks.ReleaseReservationAsync(reservationId, cancellationToken);

    public async ValueTask<IReadOnlyList<HackingLootItem>> AttemptEndedAsync(
        ClientSession session, HackableTarget target, Guid reservationId, bool won, CancellationToken cancellationToken)
    {
        var contents = await hacks.EndedAsync(target, reservationId, won, session.CharacterId ?? 0, cancellationToken);
        AfterSettlement?.Invoke();
        return contents;
    }
}

/// <summary>
/// The whole chain of a data site: the site is filled in a system (its containers are placed with it, as inventory items), an analyzer opens an
/// attempt on each container through the hacking service and each attempt is won. A win leaves its loot in the container's cargo; the loot is
/// taken out, each container clears, and the last clear completes the site.
/// </summary>
public sealed class ContainerSiteEndToEndTests
{
    private const long Character = 99_001;
    private const int Jita = 30000142;
    private const int Relic = 1207, Data = 306;
    private const long Station = 60_000_001;

    /// <summary>A board where the core is next to the start: the first click reveals it and the second attacks (the board of an attempt is generated otherwise).</summary>
    private static HackingGame WinningGame(HackingAttempt attempt)
    {
        var board = HackingBoard.Blank();
        var core = board.Tiles[new Coord(0, 3)];
        core.Type = HackingWire.TypeCore;
        core.Subtype = HackingWire.SubtypeCoreLow;
        core.Coherence = core.Strength = 10;
        return new HackingGame(attempt, board);
    }

    [PgFact]
    public async Task A_data_site_is_filled_its_containers_are_hacked_through_the_service_and_the_site_completes_when_the_last_container_is_emptied()
    {
        await using var rig = await Rig.CreateAsync();
        var id = Guid.NewGuid().ToString("N")[..10];
        // the scheduler and the bus of this chain, on scratch schemas, with the clock the test owns
        var clock = 1_700_006_400_000L;
        await DurableScheduler.MigrateAsync(rig.Source, "e2e_sch_" + id);
        await EventBus.MigrateAsync(rig.Source, "e2e_evt_" + id);
        var scheduler = new DurableScheduler(rig.Source, () => clock, null, "e2e_sch_" + id);
        var bus = new EventBus(rig.Source, scheduler, null, "e2e_evt_" + id);
        await ContentRuntime.MigrateAsync(rig.Source, "e2e_content_" + id);
        var content = new ContentRuntime(rig.Source, scheduler, bus, "e2e_content_" + id);
        var sitesSchema = "e2e_sites_" + id;
        var itemsSchema = "e2e_items_" + id;
        rig.KeepSchemas("e2e_sch_" + id, "e2e_evt_" + id, "e2e_content_" + id, sitesSchema, itemsSchema);
        await SiteContainerStore.MigrateAsync(rig.Source, sitesSchema);
        await PgItemStore.MigrateAsync(rig.Source, itemsSchema);
        var store = new SiteContainerStore(rig.Source, sitesSchema);
        var items = new PgItemStore(rig.Source, itemsSchema) { Events = bus };
        var hacks = new SiteContainerHacks(rig.Source, store, items, bus, () => scheduler.Now, _ => { });
        // a container clears when its loot is taken: the move is announced, and the container's hacks hear it
        bus.Subscribe("e2e.site-container.emptied", [PgItemStore.ItemMovedTopic], async (context, token) =>
        {
            if (context.Event.Json().TryGetProperty("previousLocationId", out var previous) && previous.TryGetInt64(out var previousLocationId))
                await hacks.OnItemLeftAsync(context.Connection, context.Transaction, previousLocationId, token);
        });
        var templates = SiteTemplates.Load();
        var director = new CosmicSiteDirector(rig.Source, content, scheduler, templates, new SdeSiteAnchors(new SdeReader(rig.Source)), rig.Systems)
        {
            Containers = store,
            NewContainerAsync = (connection, transaction, typeId, system) =>
                items.CreateAsync(connection, transaction, new NewItem(typeId, 0, system, CharacterShips.InSpaceFlag, 1, true)),
            ContainerType = (name, relic) => (17, relic ? Relic : Data),
        };
        director.Attach(content, bus);
        await content.AttachAsync();
        await bus.PumpAsync();

        // the fill of the system: its sites, and the containers of the data site
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await director.FillAsync(connection, transaction, Jita);
            await transaction.CommitAsync();
        }
        var site = (await director.ActiveAsync(Jita)).Single(candidate =>
            templates.All.Single(template => template.Id == candidate.TemplateId).Family == "data");
        var containers = (await store.InSystemAsync(Jita)).Where(container => container.SiteId == site.InstanceId).ToList();
        Assert.NotEmpty(containers);
        Assert.Equal(templates.All.Single(template => template.Id == site.TemplateId).ContainerCount, containers.Count);

        // each container is hacked through the service: the analyzer opens the attempt, the core is revealed, then destroyed
        var service = new HackingMgrService(new HackingAttempts(), new ContainerSite(hacks), WinningGame);
        var session = new ClientSession(new Sink()) { CharacterId = Character };
        var analyzer = new AnalyzerValues(22329, CanHackData: true, CanHackRelic: true, MaxRange: 54_000, VirusCoherence: 40, VirusStrength: 20, VirusSlots: 3, AccessDifficultyBonus: 0);
        foreach (var container in containers)
        {
            Assert.Null(await service.AnalyzerRefusalAsync(session, Character, container.ContainerId, analyzer, CancellationToken.None));
            await service.OpenAnalyzerAttemptAsync(session, Character, container.ContainerId, analyzer, CancellationToken.None);
            await ClickTileAsync(service, session, new Coord(0, 3), 1);
            await ClickTileAsync(service, session, new Coord(0, 3), 2);
        }
        await DrainAsync(bus);
        foreach (var container in containers)
        {
            Assert.Equal("looted", (await store.GetAsync(container.ContainerId))!.State);
            Assert.NotEmpty(await items.InLocationAsync(container.ContainerId, Character));
        }
        // a won container is not yet cleared: the site is still running until its containers are emptied
        Assert.Contains(await director.ActiveAsync(Jita), active => active.InstanceId == site.InstanceId);

        // the loot is taken out of each container: the last piece taken clears it, and the site completes after the last container
        foreach (var container in containers)
        {
            foreach (var piece in await items.InLocationAsync(container.ContainerId, Character))
            {
                await using var connection = await rig.Source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await items.MoveAsync(connection, transaction, piece.Id, Station, ItemFlags.Hangar);
                await transaction.CommitAsync();
                await DrainAsync(bus);
            }
            Assert.Equal("cleared", (await store.GetAsync(container.ContainerId))!.State);
        }
        Assert.DoesNotContain(await director.ActiveAsync(Jita), active => active.InstanceId == site.InstanceId);
    }

    [PgFact]
    public async Task Concurrent_analyzer_starts_cannot_both_reserve_the_same_database_container()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        const long containerId = 7_000_000_000_000_901L;
        await rig.InsertAsync(new SiteContainerRecord(containerId, 5_300_000_000_901, SiteContainerRig.SolarSystemId,
            "research_laboratory", true, 17, Relic, [1_000, 2_000, 3_000], 50, 4, 0, "secure", rig.Now + 86_400_000));

        var bothLookedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupCount = 0;
        var site = new ContainerSite(rig.Hacks());
        site.BeforeReservation = async () =>
        {
            if (Interlocked.Increment(ref lookupCount) == 2)
                bothLookedUp.TrySetResult();
            await bothLookedUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };
        var first = new HackingMgrService(new HackingAttempts(), site, WinningGame);
        var second = new HackingMgrService(new HackingAttempts(), site, WinningGame);
        var firstSession = new ClientSession(new Sink()) { CharacterId = 99_101 };
        var secondSession = new ClientSession(new Sink()) { CharacterId = 99_102 };
        var analyzer = new AnalyzerValues(22329, CanHackData: true, CanHackRelic: true, MaxRange: 54_000,
            VirusCoherence: 40, VirusStrength: 20, VirusSlots: 3, AccessDifficultyBonus: 0);

        var results = await Task.WhenAll(
            first.AnalyzerRefusalAsync(firstSession, firstSession.CharacterId!.Value, containerId, analyzer, CancellationToken.None).AsTask(),
            second.AnalyzerRefusalAsync(secondSession, secondSession.CharacterId!.Value, containerId, analyzer, CancellationToken.None).AsTask());

        Assert.Single(results, result => result is null);
        Assert.Equal("ANALYZER_TARGET_BUSY", Assert.Single(results, result => result is not null));
    }

    [PgFact]
    public async Task A_winning_turn_is_not_sent_until_the_database_settlement_commits()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        const long containerId = 7_000_000_000_000_902L;
        await rig.InsertAsync(new SiteContainerRecord(containerId, 5_300_000_000_902, SiteContainerRig.SolarSystemId,
            "research_laboratory", true, 17, Relic, [1_000, 2_000, 3_000], 50, 4, 0, "secure", rig.Now + 86_400_000));

        var committed = false;
        var observeNotification = false;
        bool? committedWhenNotified = null;
        var sink = new Sink(_ =>
        {
            if (observeNotification)
                committedWhenNotified = committed;
        });
        var session = new ClientSession(sink) { CharacterId = Character };
        var site = new ContainerSite(rig.Hacks()) { AfterSettlement = () => committed = true };
        var service = new HackingMgrService(new HackingAttempts(), site, WinningGame);
        var analyzer = new AnalyzerValues(22329, CanHackData: true, CanHackRelic: true, MaxRange: 54_000,
            VirusCoherence: 40, VirusStrength: 20, VirusSlots: 3, AccessDifficultyBonus: 0);

        Assert.Null(await service.AnalyzerRefusalAsync(session, Character, containerId, analyzer, CancellationToken.None));
        await service.OpenAnalyzerAttemptAsync(session, Character, containerId, analyzer, CancellationToken.None);
        Events.Of(session);
        observeNotification = true;

        await ClickTileAsync(service, session, new Coord(0, 3), 1);
        var reveal = Assert.IsType<PyTuple>(Assert.Single(Events.Of(session)).Args[0]);
        Assert.Equal(HackingWire.EventTileFlipped, Number(Field(Assert.Single(reveal.Items), "eventID")));
        Assert.False(committedWhenNotified);
        await ClickTileAsync(service, session, new Coord(0, 3), 2);

        Assert.True(committedWhenNotified);
        Assert.Equal("looted", (await rig.Store.GetAsync(containerId))!.State);
        Assert.NotEmpty(await new PgItemStore(rig.Source, rig.ItemsSchema).InLocationAsync(containerId, Character));
    }

    [PgFact]
    public async Task A_winning_turn_sends_settled_core_contents_in_the_client_wire_shape_before_the_win()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        const long containerId = 7_000_000_000_000_903L;
        await rig.InsertAsync(new SiteContainerRecord(containerId, 5_300_000_000_903, SiteContainerRig.SolarSystemId,
            "research_laboratory", true, 17, Relic, [1_000, 2_000, 3_000], 50, 4, 0, "secure", rig.Now + 86_400_000));

        var session = new ClientSession(new Sink()) { CharacterId = Character };
        var service = new HackingMgrService(new HackingAttempts(), new ContainerSite(rig.Hacks()), WinningGame);
        var analyzer = new AnalyzerValues(22329, CanHackData: true, CanHackRelic: true, MaxRange: 54_000,
            VirusCoherence: 40, VirusStrength: 20, VirusSlots: 3, AccessDifficultyBonus: 0);

        Assert.Null(await service.AnalyzerRefusalAsync(session, Character, containerId, analyzer, CancellationToken.None));
        await service.OpenAnalyzerAttemptAsync(session, Character, containerId, analyzer, CancellationToken.None);
        Events.Of(session);
        await ClickTileAsync(service, session, new Coord(0, 3), 1);
        Events.Of(session);
        await ClickTileAsync(service, session, new Coord(0, 3), 2);

        var turn = Assert.IsType<PyTuple>(Assert.Single(Events.Of(session)).Args[0]);
        var eventObjects = turn.Items.Cast<PyValue>().ToArray();
        var eventIds = eventObjects.Select(item => (int)Number(Field(item, "eventID"))).ToArray();
        Assert.Contains(400, eventIds); // EVENT_CORE_CONTENTS from V24.01 hackingConstants.py
        var contentsIndex = Array.IndexOf(eventIds, 400);
        var wonIndex = Array.IndexOf(eventIds, HackingWire.EventGameWon);
        Assert.True(contentsIndex >= 0 && contentsIndex < wonIndex);

        var contentsData = Assert.IsType<PyDict>(Field(eventObjects[contentsIndex], "eventData"));
        Assert.Equal(Py.Tuple(new PyInt(0), new PyInt(3)), Field(contentsData, "coord"));
        var wireContents = Assert.IsType<PyList>(Field(contentsData, "contentsList")).Items
            .Select(value => Assert.IsType<PyTuple>(value).Items)
            .Select(values => (TypeId: checked((int)Number(values[0])), Quantity: Number(values[1])))
            .OrderBy(item => item.TypeId)
            .ToArray();
        var storedContents = (await new PgItemStore(rig.Source, rig.ItemsSchema).InLocationAsync(containerId, Character))
            .GroupBy(item => item.TypeId)
            .Select(group => (TypeId: group.Key, Quantity: group.Sum(item => item.Quantity)))
            .OrderBy(item => item.TypeId)
            .ToArray();
        Assert.Equal(storedContents, wireContents);
    }

    [PgFact]
    public async Task Hacking_turn_records_are_mappings_for_the_clients_kwargs_expansion()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        const long containerId = 7_000_000_000_000_905L;
        await rig.InsertAsync(new SiteContainerRecord(containerId, 5_300_000_000_905,
            SiteContainerRig.SolarSystemId, "containment_facility", false, 17, Data,
            [1_000, 2_000, 3_000], 50, 1, 0, "secure", rig.Now + 86_400_000));

        var board = HackingBoard.Blank();
        board.Tiles[new Coord(1, 4)].Type = HackingWire.TypeUtilityElementTile;
        board.Tiles[new Coord(1, 4)].Subtype = HackingWire.SubtypeUeSelfRepair;
        var cache = board.Tiles[new Coord(0, 3)];
        cache.Type = HackingWire.TypeDataCache;
        cache.Contents = (HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsFirewall);
        var firewall = board.Tiles[new Coord(0, 5)];
        firewall.Type = HackingWire.TypeDefenseSoftware;
        firewall.Subtype = HackingWire.SubtypeDsFirewall;
        firewall.Coherence = 60;
        firewall.Strength = 10;

        var session = new ClientSession(new Sink()) { CharacterId = Character };
        var service = new HackingMgrService(new HackingAttempts(), new ContainerSite(rig.Hacks()),
            attempt => new HackingGame(attempt, board));
        var analyzer = new AnalyzerValues(22329, CanHackData: true, CanHackRelic: false, MaxRange: 54_000,
            VirusCoherence: 40, VirusStrength: 20, VirusSlots: 3, AccessDifficultyBonus: 0);
        Assert.Null(await service.AnalyzerRefusalAsync(session, Character, containerId, analyzer, CancellationToken.None));
        await service.OpenAnalyzerAttemptAsync(session, Character, containerId, analyzer, CancellationToken.None);

        var start = Assert.IsType<PyTuple>(Assert.Single(Events.Of(session)).Args[0]);
        Assert.All(start.Items, item => Assert.IsType<PyObject>(item));
        Assert.All(start.Items, AssertEventDataIsMapping);
        var tile = start.Items.First(item => Number(Field(item, "eventID")) == HackingWire.EventTileCreated);
        Assert.IsType<PyTuple>(Field(Assert.IsType<PyDict>(Field(tile, "eventData")), "coord"));
        var virus = Assert.Single(start.Items, item => Number(Field(item, "eventID")) == HackingWire.EventVirusCreated);
        var inventory = Assert.IsType<PyList>(Field(Field(virus, "eventData"), "inventory"));
        Assert.All(inventory.Items, item => Assert.IsType<PyDict>(item));

        foreach (var coord in new[] { new Coord(1, 4), new Coord(0, 3), new Coord(0, 5) })
        {
            await service.CallAsync(new ServiceCall
            {
                Session = session,
                Method = "ClickedOnTile",
                Args = [Py.Tuple(new PyInt(coord.X), new PyInt(coord.Y))],
                CallId = new PyInt(1),
            });
            var turn = Assert.IsType<PyTuple>(Assert.Single(Events.Of(session)).Args[0]);
            Assert.All(turn.Items, AssertEventDataIsMapping);
        }

        // Pick-up and attack records contain nested mappings that the UI also expands or reads by field.
        var restart = new ClientSession(new Sink()) { CharacterId = Character + 1 };
        var utilityBoard = HackingBoard.Blank();
        utilityBoard.Tiles[new Coord(1, 4)].Type = HackingWire.TypeUtilityElementTile;
        utilityBoard.Tiles[new Coord(1, 4)].Subtype = HackingWire.SubtypeUeSelfRepair;
        utilityBoard.Tiles[new Coord(0, 3)].Type = HackingWire.TypeDefenseSoftware;
        utilityBoard.Tiles[new Coord(0, 3)].Coherence = 60;
        utilityBoard.Tiles[new Coord(0, 3)].Strength = 10;
        var secondService = new HackingMgrService(new HackingAttempts(), new ContainerSite(rig.Hacks()),
            attempt => new HackingGame(attempt, utilityBoard));
        const long secondContainerId = 7_000_000_000_000_906L;
        await rig.InsertAsync(new SiteContainerRecord(secondContainerId, 5_300_000_000_906,
            SiteContainerRig.SolarSystemId, "containment_facility", false, 17, Data,
            [1_000, 2_000, 3_000], 50, 1, 0, "secure", rig.Now + 86_400_000));
        Assert.Null(await secondService.AnalyzerRefusalAsync(restart, restart.CharacterId!.Value,
            secondContainerId, analyzer, CancellationToken.None));
        await secondService.OpenAnalyzerAttemptAsync(restart, restart.CharacterId!.Value,
            secondContainerId, analyzer, CancellationToken.None);
        Events.Of(restart);
        await ClickTileAsync(secondService, restart, new Coord(1, 4), 2);
        Events.Of(restart);
        await ClickTileAsync(secondService, restart, new Coord(1, 4), 3);
        var pickedUp = Assert.IsType<PyTuple>(Assert.Single(Events.Of(restart)).Args[0]);
        Assert.All(pickedUp.Items, AssertEventDataIsMapping);
        var pickupData = Field(Assert.Single(pickedUp.Items,
            item => Number(Field(item, "eventID")) == HackingWire.EventUePickedUp), "eventData");
        Assert.IsType<PyDict>(Field(pickupData, "replacementObject"));
        var updatedInventory = Assert.IsType<PyList>(Field(pickupData, "inventory"));
        Assert.All(updatedInventory.Items, item => Assert.IsType<PyDict>(item));

        await ClickTileAsync(secondService, restart, new Coord(0, 3), 4);
        Assert.Single(Events.Of(restart));
        await ClickTileAsync(secondService, restart, new Coord(0, 3), 5);
        var attack = Assert.IsType<PyTuple>(Assert.Single(Events.Of(restart)).Args[0]);
        Assert.All(attack.Items, AssertEventDataIsMapping);
        Assert.Contains(attack.Items, item => Number(Field(item, "eventID")) == HackingWire.EventAttack);
    }

    [PgFact]
    public async Task A_failed_database_settlement_rolls_back_loot_and_the_same_reservation_can_retry()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        const long containerId = 7_000_000_000_000_904L;
        await rig.InsertAsync(new SiteContainerRecord(containerId, 5_300_000_000_904, SiteContainerRig.SolarSystemId,
            "research_laboratory", true, 17, Relic, [1_000, 2_000, 3_000], 50, 4, 0, "secure", rig.Now + 86_400_000));

        var target = (await rig.Hacks().FindAsync(containerId, CancellationToken.None))!;
        var reservationId = Guid.NewGuid();
        Assert.True(await rig.Hacks().TryReserveAsync(target, reservationId, Character, 77, CancellationToken.None));
        var triggerSuffix = Guid.NewGuid().ToString("N")[..10];
        var function = $"reject_hack_{triggerSuffix}";
        var trigger = $"reject_hack_state_{triggerSuffix}";
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var createFunction = new NpgsqlCommand($"""
                CREATE FUNCTION {rig.SitesSchema}.{function}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.state = 'looted' THEN
                        RAISE EXCEPTION 'injected hacking settlement failure';
                    END IF;
                    RETURN NEW;
                END;
                $$
                """, connection);
            await createFunction.ExecuteNonQueryAsync();
            await using var createTrigger = new NpgsqlCommand($"""
                CREATE TRIGGER {trigger} BEFORE UPDATE ON {rig.SitesSchema}.site_containers
                FOR EACH ROW EXECUTE FUNCTION {rig.SitesSchema}.{function}()
                """, connection);
            await createTrigger.ExecuteNonQueryAsync();
        }

        var hacks = rig.Hacks();
        await Assert.ThrowsAsync<PostgresException>(() => hacks.EndedAsync(target, reservationId, won: true, Character, CancellationToken.None).AsTask());
        Assert.Equal("secure", (await rig.Store.GetAsync(containerId))!.State);
        Assert.Empty(await new PgItemStore(rig.Source, rig.ItemsSchema).InLocationAsync(containerId, Character));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var dropTrigger = new NpgsqlCommand($"DROP TRIGGER {trigger} ON {rig.SitesSchema}.site_containers", connection);
            await dropTrigger.ExecuteNonQueryAsync();
            await using var dropFunction = new NpgsqlCommand($"DROP FUNCTION {rig.SitesSchema}.{function}()", connection);
            await dropFunction.ExecuteNonQueryAsync();
        }

        var contents = await hacks.EndedAsync(target, reservationId, won: true, Character, CancellationToken.None);
        var persisted = await new PgItemStore(rig.Source, rig.ItemsSchema).InLocationAsync(containerId, Character);
        Assert.Equal("looted", (await rig.Store.GetAsync(containerId))!.State);
        Assert.Equal(contents.OrderBy(item => item.TypeId).Select(item => (item.TypeId, item.Quantity)),
            persisted.GroupBy(item => item.TypeId).OrderBy(group => group.Key).Select(group => (group.Key, group.Sum(item => item.Quantity))));
        Assert.NotEmpty(persisted);
    }

    private static PyValue Field(PyValue keyVal, string key) => keyVal switch
    {
        PyObject obj => Field(Assert.IsType<PyDict>(obj.Args), key),
        PyDict dict => dict.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == key).Value,
        _ => throw new InvalidOperationException($"expected a key/value object, found {keyVal.GetType().Name}"),
    };

    private static ValueTask<PyValue> ClickTileAsync(
        HackingMgrService service, ClientSession session, Coord coord, long callId) =>
        service.CallAsync(new ServiceCall
        {
            Session = session,
            Method = "ClickedOnTile",
            Args = [Py.Tuple(new PyInt(coord.X), new PyInt(coord.Y))],
            CallId = new PyInt(callId),
        });

    private static long Number(PyValue value) => value switch
    {
        PyInt i => i.Value,
        PyLong l => l.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };

    private static void AssertEventDataIsMapping(PyValue eventValue)
    {
        var data = Assert.IsType<PyDict>(Field(eventValue, "eventData"));
        AssertNoKeyVal(data);
    }

    private static void AssertNoKeyVal(PyValue value)
    {
        switch (value)
        {
            case PyObject:
                Assert.Fail("nested hacking event data must be a mapping, not util.KeyVal");
                break;
            case PyDict dict:
                foreach (var entry in dict.Entries)
                    AssertNoKeyVal(entry.Value);
                break;
            case PyList list:
                foreach (var item in list.Items)
                    AssertNoKeyVal(item);
                break;
            case PyTuple tuple:
                foreach (var item in tuple.Items)
                    AssertNoKeyVal(item);
                break;
        }
    }

    private static async Task DrainAsync(EventBus bus)
    {
        while ((await bus.PumpAsync()).Delivered > 0)
        {
        }
    }
}
