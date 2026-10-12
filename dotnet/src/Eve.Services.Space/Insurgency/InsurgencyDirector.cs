using System.Text.Json;
using Eve.Content;
using Eve.Dungeons;
using Eve.Dungeons.Incursions;
using Eve.Events;
using Eve.Insurgency;
using Eve.Scheduler;
using Eve.Services.Space.Waves;
using Eve.Structures;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Space.Insurgency;

/// <summary>
/// The pirate insurgencies on the space runtime (slice 2): a campaign starts in each warzone that has none open (a durable hourly job), one site of the pirate
/// faction starts in each covered system when the campaign becomes active, the first completed site of a system scores the pirates' point there, the kills of a
/// covered system are the contributions to the victory payout, and the sites end with their campaign.
/// </summary>
public sealed class InsurgencyDirector
{
    public const string StartJob = "insurgency.start";
    public const string SpawnSubscriber = "insurgency.sites.spawn";
    public const string CompletedSubscriber = "insurgency.sites.completed";
    public const string ContributorSubscriber = "insurgency.contributors";
    public const string FobExposedSubscriber = "insurgency.fob.exposed";
    public const string FobStateSubscriber = "insurgency.fob.state";

    /// <summary>How often a warzone without an open campaign is looked at again. shortcut: CCP publishes no start interval; EVE.js starts at once when none is open.</summary>
    public static readonly TimeSpan StartTick = TimeSpan.FromHours(1);

    /// <summary>
    /// The pirate sites of the owner catalog (archetype titles of the client): 72 Pirate Laundering Center and 74 Warehouse Raid. Their first wave draws the
    /// warzone's pool, so a warzone's pirate sites are its pool's templates.
    /// </summary>
    private static readonly long[] PirateArchetypes = [72, 74];

    /// <summary>
    /// The empire sites (Suppression): 73 Anti-Pirate Security Post, 75 to 78 Corporate Outpost Raid (the wiki: the Empire militia destroys the pirate NPCs and
    /// captures the point) and 79 Mining Fleet Ambush (the wiki: the Empire militia destroys the ransoming pirates). Project rule, DEVIATIONS "Insurgency sites".
    /// </summary>
    private static readonly long[] EmpireArchetypes = [73, 75, 76, 77, 78, 79];

    private readonly InsurgencyStore _store;
    private readonly InsurgencyCampaigns _campaigns;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _bus;
    private readonly ContentRuntime _content;
    private readonly DungeonWaveDirector _sites;
    private readonly SolarSystemCatalog _systems;
    private readonly StargateGraph _stargates;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<long>> _pirateTemplatesOfPool;
    private readonly IReadOnlyList<long> _empireTemplates;
    private readonly StructureTimers _structures;
    private readonly StructureStore _fobIds;

    public InsurgencyDirector(
        InsurgencyStore store, InsurgencyCampaigns campaigns, DurableScheduler scheduler, EventBus bus, ContentRuntime content, DungeonWaveDirector sites,
        SolarSystemCatalog systems, StargateGraph stargates, DungeonCatalog catalog, StructureTimers structures, StructureStore fobIds)
    {
        (_store, _campaigns, _scheduler, _bus, _content, _sites, _systems, _stargates) = (store, campaigns, scheduler, bus, content, sites, systems, stargates);
        (_structures, _fobIds) = (structures, fobIds);
        // the templates the simulation can make: the pirate ones by the pool of their first wave, the empire ones all together
        var startable = catalog.Dungeons.Where(d => d.Waves.Count > 0 && sites.StartRefusalOf(d.DungeonID) is null).ToList();
        _pirateTemplatesOfPool = startable
            .Where(d => d.ArchetypeID is { } archetype && PirateArchetypes.Contains(archetype))
            .GroupBy(d => d.Waves[0].SpawnQuery ?? "")
            .ToDictionary(g => g.Key, g => (IReadOnlyList<long>)g.Select(d => d.DungeonID).OrderBy(id => id).ToList());
        _empireTemplates = startable.Where(d => d.ArchetypeID is { } archetype && EmpireArchetypes.Contains(archetype)).Select(d => d.DungeonID).OrderBy(id => id).ToList();
    }

    /// <summary>Registers the start job and the subscribers, and schedules the start: at once, then every tick. Call it before the scheduler starts.</summary>
    public async Task AttachAsync()
    {
        _scheduler.Register(StartJob, (context, token) => StartAsync(context, token));
        _bus.Subscribe(SpawnSubscriber, [InsurgencyCampaigns.CampaignChangedTopic], (context, token) => OnCampaignChangedAsync(context, token));
        _bus.Subscribe(CompletedSubscriber, ["content." + DungeonWaveDirector.Kind + ".completed"], (context, token) => OnSiteCompletedAsync(context, token));
        _bus.Subscribe(ContributorSubscriber, [DungeonWaveDirector.NpcKilledTopic], (context, token) => OnKilledAsync(context, token));
        _bus.Subscribe(FobExposedSubscriber, [InsurgencyCampaigns.FobExposedTopic], (context, token) => OnFobExposedAsync(context, token));
        _bus.Subscribe(FobStateSubscriber, [StructureTimers.StateChangedTopic], (context, token) => OnFobStateChangedAsync(context, token));
        // the first look is now (a warzone with no campaign gets one at once); the job then repeats every tick
        await _scheduler.ScheduleAsync(new JobSpec(StartJob, _scheduler.Now)
        {
            Key = "start",
            EveryMs = (long)StartTick.TotalMilliseconds,
            OnDuplicate = OnDuplicate.Keep,
        });
    }

    /// <summary>
    /// Starts a campaign in each warzone that has none open. The hub is a low-security system of the warzone's map, chosen in turn over the warzone's campaigns;
    /// the covered systems are the hub and the nearest low-security systems of the warzone by stargate jumps, as many as the victory needs (the ambition of the pirates).
    /// A warzone with too few such systems starts nothing and is looked at again on the next tick.
    /// </summary>
    private async Task StartAsync(JobContext context, CancellationToken cancellationToken)
    {
        foreach (var warzone in InsurgencyWarzones.All)
        {
            var open = await _store.OpenAsync(context.Connection, context.Transaction, cancellationToken);
            if (open.Any(c => c.WarzoneId == warzone.WarzoneId))
                continue;
            var warzoneSystems = InsurgencyWarzones.SystemsOf(warzone.WarzoneId);
            var candidates = _systems.Systems
                .Where(s => warzoneSystems.Contains(s.Id) && IncursionRules.BandOf(s.Security) == IncursionBand.LowSec)
                .OrderBy(s => s.Id)
                .ToList();
            if (candidates.Count == 0)
                continue;
            var started = await _store.CountInWarzoneAsync(context.Connection, context.Transaction, warzone.WarzoneId, cancellationToken);
            var hub = candidates[(int)(started % candidates.Count)];
            var (pirate, _) = await _store.AmbitionAsync(context.Connection, context.Transaction, warzone.WarzoneId, cancellationToken);
            var required = InsurgencyRules.RequiredSystems(pirate);
            var jumps = _stargates.JumpsFrom(hub.Id);
            var covered = candidates
                .Where(c => jumps.ContainsKey(c.Id))
                .OrderBy(c => jumps[c.Id]).ThenBy(c => c.Id)
                .Take(required)
                .Select(c => (long)c.Id)
                .ToList();
            if (covered.Count < required)
                continue;
            // the spawn of the FOB starts the campaign: put in space at the hub, invulnerable until the empire side exposes it.
            // shortcut: the FOB sits at the system's origin; the owner data and the wiki give no place for it.
            long fob = await _fobIds.NextFobIdAsync(context.Connection, context.Transaction, cancellationToken);
            if (await _structures.DeployFobAsync(context.Connection, context.Transaction,
                    new NewStructure(fob, warzone.FobTypeId, hub.Id, 0, 0, 0, 0, warzone.PirateCorporationId, 0, "Pirate FOB", "", 0, StructureTimes.NoReinforceWeekday, 0),
                    cancellationToken) is null)
                throw new InvalidOperationException($"the FOB id {fob} of warzone {warzone.WarzoneId} is taken");
            await _campaigns.StartAsync(context.Connection, context.Transaction,
                new CampaignStart(warzone.PirateFactionId, warzone.WarzoneId, hub.Id, covered, fob), cancellationToken);
        }
    }

    /// <summary>
    /// The campaign became active: a site of the warzone's pool starts in each covered system, for the rest of the campaign. Ended campaigns (a win, or no winner)
    /// end their open sites.
    /// </summary>
    private async Task OnCampaignChangedAsync(EventContext context, CancellationToken cancellationToken)
    {
        var change = context.Event.Payload<CampaignChanged>()!;
        if (change.From == CampaignStates.Forecasting && change.To == CampaignStates.Active)
        {
            await SpawnSitesAsync(context, change.Campaign, cancellationToken);
        }
        else if (change.To is CampaignStates.PirateWin or CampaignStates.AntipirateWin or CampaignStates.NoWinner)
        {
            foreach (long instance in await _store.OpenSiteInstancesAsync(context.Connection, context.Transaction, change.CampaignId, cancellationToken))
            {
                await _content.AbandonAsync(context.Connection, context.Transaction, instance, $"insurgency {change.CampaignId} ended", cancellationToken);
            }
            // the FOB leaves space with its campaign (a destroyed one is already gone: the timeline refuses the event)
            await _structures.ApplyAsync(context.Connection, context.Transaction, change.Campaign.StructureId, new FobDespawned(), cancellationToken);
        }
    }

    /// <summary>The campaign's goal for the empire side is reached: the FOB is exposed (its shield stage, the attackable one).</summary>
    private async Task OnFobExposedAsync(EventContext context, CancellationToken cancellationToken)
    {
        var exposure = context.Event.Payload<FobExposure>()!;
        await _structures.ApplyAsync(context.Connection, context.Transaction, exposure.StructureId, new FobExposed(), cancellationToken);
    }

    /// <summary>A structure was destroyed: when it is the FOB of an active campaign, the empire side wins.</summary>
    private async Task OnFobStateChangedAsync(EventContext context, CancellationToken cancellationToken)
    {
        var change = context.Event.Payload<StructureStateChanged>()!;
        if (change.Removal == "destroyed")
            await _campaigns.FobDestroyedAsync(context.Connection, context.Transaction, change.StructureId, cancellationToken);
    }

    private async Task SpawnSitesAsync(EventContext context, Campaign campaign, CancellationToken cancellationToken)
    {
        var warzone = InsurgencyWarzones.All.Single(w => w.WarzoneId == campaign.WarzoneId);
        if (!_pirateTemplatesOfPool.TryGetValue(warzone.SpawnPool, out var pirate) || pirate.Count == 0)
            throw new InvalidOperationException($"insurgency campaign {campaign.CampaignId}: the owner data has no site for the pool {warzone.SpawnPool}");
        if (_empireTemplates.Count == 0)
            throw new InvalidOperationException($"insurgency campaign {campaign.CampaignId}: the owner data has no empire site");
        long lifetimeMs = campaign.MaxEndsMs - _scheduler.Now;
        if (lifetimeMs <= 0)
            return;
        foreach (long system in campaign.CoveredSolarSystemIds)
        {
            // the template of a system is fixed by the campaign and the system (a restart of the same event starts the same site)
            await StartSiteAsync(context, campaign, system, pirate[(int)((campaign.CampaignId + system) % pirate.Count)], Side.Pirate, lifetimeMs, cancellationToken);
            await StartSiteAsync(context, campaign, system, _empireTemplates[(int)((campaign.CampaignId + system) % _empireTemplates.Count)], Side.Antipirate, lifetimeMs, cancellationToken);
        }
    }

    private async Task StartSiteAsync(EventContext context, Campaign campaign, long system, long dungeon, Side side, long lifetimeMs, CancellationToken cancellationToken)
    {
        var start = await _sites.StartForSystemAsync((int)system, new double[] { 0, 0, 0 }, dungeon, $"insurgency:{campaign.CampaignId}:{system}:{side}",
            TimeSpan.FromMilliseconds(lifetimeMs), context.Connection, context.Transaction, cancellationToken);
        if (start.InstanceId is not { } instance)
            throw new InvalidOperationException($"insurgency campaign {campaign.CampaignId} {side} site in system {system} (dungeon {dungeon}): {start.Refusal}");
        await _store.RecordSiteAsync(context.Connection, context.Transaction, campaign.CampaignId, system, dungeon, instance, side, cancellationToken);
    }

    /// <summary>A campaign site completed: its system scores the point of the site's side (once; the campaign refuses it unless active).</summary>
    private async Task OnSiteCompletedAsync(EventContext context, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(context.Event.PayloadJson);
        if (!document.RootElement.TryGetProperty("instanceId", out var instanceElement) || !instanceElement.TryGetInt64(out long instanceId))
            return;
        if (await _store.CompleteSiteAsync(context.Connection, context.Transaction, instanceId, _scheduler.Now, cancellationToken) is not { } site)
            return;
        await _campaigns.ScorePointAsync(context.Connection, context.Transaction, site.CampaignId, site.Side, site.SolarSystemId, cancellationToken);
    }

    /// <summary>
    /// An NPC was killed by a pilot: the pilot contributes to each active campaign that covers the system. shortcut: the owner data gives no per-site participation,
    /// so a contribution is any final blow in a covered system while the campaign is active.
    /// </summary>
    private async Task OnKilledAsync(EventContext context, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(context.Event.PayloadJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("characterId", out var characterElement) || !characterElement.TryGetInt64(out long character) || character <= 0)
            return;
        if (!root.TryGetProperty("solarSystemId", out var systemElement) || !systemElement.TryGetInt64(out long system))
            return;
        foreach (long campaignId in await _store.ActiveCoveringAsync(context.Connection, context.Transaction, system, cancellationToken))
            await _store.AddContributorAsync(context.Connection, context.Transaction, campaignId, character, _scheduler.Now, cancellationToken);
    }
}
