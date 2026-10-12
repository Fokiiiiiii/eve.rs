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
    public ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken) =>
        hacks.FindAsync(targetId, cancellationToken);

    public ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask AttemptEndedAsync(ClientSession session, HackableTarget target, bool won, CancellationToken cancellationToken) =>
        hacks.EndedAsync(target, won, session.CharacterId ?? 0, cancellationToken);
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

    /// <summary>A board where the core is next to the start: the first click is the win (the board of an attempt is generated otherwise).</summary>
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

        // each container is hacked through the service: the analyzer opens the attempt, the window is won
        var service = new HackingMgrService(new HackingAttempts(), new ContainerSite(hacks), WinningGame);
        var session = new ClientSession(new Sink()) { CharacterId = Character };
        var analyzer = new AnalyzerValues(22329, CanHackData: true, CanHackRelic: true, MaxRange: 54_000, VirusCoherence: 40, VirusStrength: 20, VirusSlots: 3, AccessDifficultyBonus: 0);
        foreach (var container in containers)
        {
            Assert.Null(await service.AnalyzerRefusalAsync(Character, container.ContainerId, analyzer, CancellationToken.None));
            await service.OpenAnalyzerAttemptAsync(session, Character, container.ContainerId, analyzer, CancellationToken.None);
            await service.CallAsync(new ServiceCall
            {
                Session = session,
                Method = "ClickedOnTile",
                Args = [Py.Tuple(new PyInt(0), new PyInt(3))],
                CallId = new PyInt(1),
            });
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

    private static async Task DrainAsync(EventBus bus)
    {
        while ((await bus.PumpAsync()).Delivered > 0)
        {
        }
    }
}
