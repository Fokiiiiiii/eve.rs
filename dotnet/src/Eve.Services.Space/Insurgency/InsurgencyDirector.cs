using System.Text.Json;
using Eve.Content;
using Eve.Dungeons;
using Eve.Dungeons.Incursions;
using Eve.Events;
using Eve.Insurgency;
using Eve.Scheduler;
using Eve.Services.Space.Waves;
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

    /// <summary>How often a warzone without an open campaign is looked at again. shortcut: CCP publishes no start interval; EVE.js starts at once when none is open.</summary>
    public static readonly TimeSpan StartTick = TimeSpan.FromHours(1);

    /// <summary>The campaign site templates of the owner catalog: archetypes 72 to 79 (EVE.js <c>insurgencyFobRuntimeController</c> archetype check).</summary>
    private const long FirstSiteArchetype = 72;
    private const long LastSiteArchetype = 79;

    private readonly InsurgencyStore _store;
    private readonly InsurgencyCampaigns _campaigns;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _bus;
    private readonly ContentRuntime _content;
    private readonly DungeonWaveDirector _sites;
    private readonly SolarSystemCatalog _systems;
    private readonly StargateGraph _stargates;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<long>> _templatesOfPool;

    public InsurgencyDirector(
        InsurgencyStore store, InsurgencyCampaigns campaigns, DurableScheduler scheduler, EventBus bus, ContentRuntime content, DungeonWaveDirector sites,
        SolarSystemCatalog systems, StargateGraph stargates, DungeonCatalog catalog)
    {
        (_store, _campaigns, _scheduler, _bus, _content, _sites, _systems, _stargates) = (store, campaigns, scheduler, bus, content, sites, systems, stargates);
        // the templates of each NPC pool: a site is started from the templates whose first wave draws from that pool and whose waves the simulation can make
        _templatesOfPool = catalog.Dungeons
            .Where(d => d.ArchetypeID is >= FirstSiteArchetype and <= LastSiteArchetype && d.Waves.Count > 0 && sites.StartRefusalOf(d.DungeonID) is null)
            .GroupBy(d => d.Waves[0].SpawnQuery ?? "")
            .ToDictionary(g => g.Key, g => (IReadOnlyList<long>)g.Select(d => d.DungeonID).OrderBy(id => id).ToList());
    }

    /// <summary>Registers the start job and the subscribers, and schedules the start: at once, then every tick. Call it before the scheduler starts.</summary>
    public async Task AttachAsync()
    {
        _scheduler.Register(StartJob, (context, token) => StartAsync(context, token));
        _bus.Subscribe(SpawnSubscriber, [InsurgencyCampaigns.CampaignChangedTopic], (context, token) => OnCampaignChangedAsync(context, token));
        _bus.Subscribe(CompletedSubscriber, ["content." + DungeonWaveDirector.Kind + ".completed"], (context, token) => OnSiteCompletedAsync(context, token));
        _bus.Subscribe(ContributorSubscriber, [DungeonWaveDirector.NpcKilledTopic], (context, token) => OnKilledAsync(context, token));
        // the first look is now (a warzone with no campaign gets one at once); the job then repeats every tick
        await _scheduler.ScheduleAsync(new JobSpec(StartJob, _scheduler.Now)
        {
            Key = "start",
            EveryMs = (long)StartTick.TotalMilliseconds,
            OnDuplicate = OnDuplicate.Keep,
        });
    }

    /// <summary>
    /// Starts a campaign in each warzone that has none open. The hub is a low-security system of the warzone's regions, chosen in turn over the warzone's campaigns;
    /// the covered systems are the hub and the nearest low-security systems of the regions by stargate jumps, as many as the victory needs (the ambition of the pirates).
    /// A warzone with too few such systems starts nothing and is looked at again on the next tick.
    /// </summary>
    private async Task StartAsync(JobContext context, CancellationToken cancellationToken)
    {
        foreach (var warzone in InsurgencyWarzones.All)
        {
            var open = await _store.OpenAsync(context.Connection, context.Transaction, cancellationToken);
            if (open.Any(c => c.WarzoneId == warzone.WarzoneId))
                continue;
            var candidates = _systems.Systems
                .Where(s => warzone.RegionIds.Contains(s.RegionId) && IncursionRules.BandOf(s.Security) == IncursionBand.LowSec)
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
            await _campaigns.StartAsync(context.Connection, context.Transaction,
                new CampaignStart(warzone.PirateFactionId, warzone.WarzoneId, hub.Id, covered), cancellationToken);
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
        }
    }

    private async Task SpawnSitesAsync(EventContext context, Campaign campaign, CancellationToken cancellationToken)
    {
        var warzone = InsurgencyWarzones.All.Single(w => w.WarzoneId == campaign.WarzoneId);
        if (!_templatesOfPool.TryGetValue(warzone.SpawnPool, out var templates) || templates.Count == 0)
            throw new InvalidOperationException($"insurgency campaign {campaign.CampaignId}: the owner data has no site for the pool {warzone.SpawnPool}");
        long lifetimeMs = campaign.MaxEndsMs - _scheduler.Now;
        if (lifetimeMs <= 0)
            return;
        foreach (long system in campaign.CoveredSolarSystemIds)
        {
            // the template of a system is fixed by the campaign and the system (a restart of the same event starts the same site)
            long dungeon = templates[(int)((campaign.CampaignId + system) % templates.Count)];
            var start = await _sites.StartForSystemAsync((int)system, new double[] { 0, 0, 0 }, dungeon, $"insurgency:{campaign.CampaignId}:{system}",
                TimeSpan.FromMilliseconds(lifetimeMs), context.Connection, context.Transaction, cancellationToken);
            if (start.InstanceId is not { } instance)
                throw new InvalidOperationException($"insurgency campaign {campaign.CampaignId} site in system {system} (dungeon {dungeon}): {start.Refusal}");
            await _store.RecordSiteAsync(context.Connection, context.Transaction, campaign.CampaignId, system, dungeon, instance, cancellationToken);
        }
    }

    /// <summary>A campaign site completed: the system of the site scores the pirates' point (once; the campaign refuses it unless active).</summary>
    private async Task OnSiteCompletedAsync(EventContext context, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(context.Event.PayloadJson);
        if (!document.RootElement.TryGetProperty("instanceId", out var instanceElement) || !instanceElement.TryGetInt64(out long instanceId))
            return;
        if (await _store.CompleteSiteAsync(context.Connection, context.Transaction, instanceId, _scheduler.Now, cancellationToken) is not { } site)
            return;
        await _campaigns.ScorePointAsync(context.Connection, context.Transaction, site.CampaignId, Side.Pirate, site.SolarSystemId, cancellationToken);
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
