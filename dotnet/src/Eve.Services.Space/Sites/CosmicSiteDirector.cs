using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Content;
using Eve.Events;
using Eve.Scheduler;
using Eve.Universe;
using Eve.Wormholes;
using Npgsql;

namespace Eve.Services.Space.Sites;

/// <summary>Where a site is in the content runtime's data and what it is: the facts of an instance of <c>anomaly</c> or <c>signature</c> content that the scan and the cleanup read.</summary>
public sealed record SiteRecord(long InstanceId, int SolarSystemId, string Kind, int Slot, string TemplateId, string Code, double[] Position, long? ExpiresMs)
{
    public bool IsAnomaly => Kind == SiteTemplate.Anomaly;

    /// <summary>The record of an instance the director made, or null for content of the same kind that it did not make.</summary>
    public static SiteRecord? Of(ContentInstance instance)
    {
        if (!instance.Definition.StartsWith(CosmicSiteDirector.DefinitionPrefix, StringComparison.Ordinal) || JsonNode.Parse(instance.DataJson) is not JsonObject data
            || data["position"] is not JsonArray position || position.Count != 3)
        {
            return null;
        }
        return new SiteRecord(instance.Id, (int)instance.Owner, instance.Kind, (int?)data["slot"] ?? 0, (string?)data["template"] ?? "", (string?)data["targetID"] ?? "",
            [(double)position[0]!, (double)position[1]!, (double)position[2]!], instance.ExpiresMs);
    }
}

/// <summary>
/// The cosmic sites of the solar systems (docs/design/FOUNDATIONS.md, "A feature, end to end: an anomaly"). Each site is an instance of the content runtime: an <c>anomaly</c> (found in
/// the system scanner) or a <c>signature</c> (found with probes), owned by its solar system, started once for a slot of the system (<c>system:{id}:slot:{n}</c>), with the NPCs it holds
/// counted by an objective of <c>npc.killed</c>. The director keeps every slot of a system filled: a recurring job <see cref="SpawnJob"/> per system fills what is missing, and a
/// site that is cleared or runs out of time is replaced at once, somewhere else (the wiki: sites respawn immediately after they are cleared, and not after downtime). No table of sites
/// of its own exists; the instance is the site and its data say where it is.
/// </summary>
public sealed class CosmicSiteDirector
{
    public const string SpawnJob = "site.spawn";
    public const string RespawnSubscriber = "site.respawn";

    /// <summary>The prefix of the definition id of every site (a name stored in the database).</summary>
    public const string DefinitionPrefix = "site.";

    /// <summary>The event that a site's container is emptied or hacked, per site (<c>{ solarSystemId, site }</c>): the objective of a data or a relic site.</summary>
    public const string ContainerClearedTopic = "site.container.cleared";

    /// <summary>How long a site lasts when nobody clears it (the reference's spawn profiles: <c>siteLifetimeMinutes</c> 1440).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>How often a system is looked at for a slot that is empty (a site that ends is replaced at once; this covers what a restart or a failure missed).</summary>
    public static readonly TimeSpan Sweep = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What a system holds: one site of each family, as the reference's default count (<c>universeSiteCountDefault</c> 1 per family). The slot number is part of the unique key of the site.
    /// </summary>
    public static readonly IReadOnlyList<(int Index, string Kind, string Family)> Slots =
    [
        (0, SiteTemplate.Anomaly, "combat"),
        (1, SiteTemplate.Signature, "combat"),
        (2, SiteTemplate.Signature, "data"),
        (3, SiteTemplate.Signature, "relic"),
    ];

    private sealed record SpawnDue(int SolarSystemId);

    private readonly NpgsqlDataSource _source;
    private readonly ContentRuntime _content;
    private readonly DurableScheduler _scheduler;
    private readonly SiteTemplates _templates;
    private readonly ISiteAnchors _anchors;
    private readonly SolarSystemCatalog _systems;
    private readonly Action<string>? _log;
    private readonly Func<int, bool>? _occupied;
    private readonly Func<int, int?>? _wormholeClassOf;

    /// <summary>The store the containers of a site are placed in (their fill is in the site's transaction). Null: a site has no containers placed.</summary>
    public SiteContainerStore? Containers { get; set; }

    /// <summary>Makes the container's inventory item (a ship-less item in the system, its id is the container's id) in the caller's transaction.</summary>
    public Func<NpgsqlConnection, NpgsqlTransaction, int, int, Task<long>>? NewContainerAsync { get; set; }

    /// <summary>The type and group of a container by the name the template gives (the static data) and whether it is a relic container; never null when the kind has a type.</summary>
    public Func<string, bool, (int TypeId, int GroupId)?>? ContainerType { get; set; }
    private readonly ConcurrentDictionary<int, Lazy<Task>> _ensured = new();

    /// <param name="occupied">Whether a pilot is in a system. A system nobody is in gets no site (the wiki: sites do not respawn during downtime, and a
    /// system nobody is in is not seen); null means every system is kept filled.</param>
    /// <param name="wormholeClassOf">The wormhole class of a J-space system (null for any other system). Such a system's sites are the plan of <see cref="WormholeSites"/>;
    /// null means J-space has no sites.</param>
    public CosmicSiteDirector(
        NpgsqlDataSource source, ContentRuntime content, DurableScheduler scheduler, SiteTemplates templates, ISiteAnchors anchors, SolarSystemCatalog systems,
        Action<string>? log = null, Func<int, bool>? occupied = null, Func<int, int?>? wormholeClassOf = null)
    {
        (_source, _content, _scheduler, _templates, _anchors, _systems, _log, _occupied, _wormholeClassOf) =
            (source, content, scheduler, templates, anchors, systems, log, occupied, wormholeClassOf);
    }

    public SiteTemplates Templates => _templates;

    public long Now => _scheduler.Now;

    /// <summary>The content of every site, one definition per site: its waves of NPCs and, after them, its containers, each counted by an objective. Define them before the content runtime attaches.</summary>
    public static IEnumerable<ContentDefinition> Definitions(SiteTemplates templates) => templates.All.Select(Definition);

    public static ContentDefinition Definition(SiteTemplate template)
    {
        var objectives = new List<ObjectiveDef>();
        for (var index = 0; index < template.Waves.Count; index++)
        {
            objectives.Add(new ObjectiveDef(
                $"wave{index + 1}", "counter", new { topic = "npc.killed", where = new { site = "$instance" }, scopeField = "solarSystemId" }, Target: template.Waves[index].Amount));
        }
        if (template.ContainerCount > 0)
        {
            objectives.Add(new ObjectiveDef(
                "containers", "counter", new { topic = ContainerClearedTopic, where = new { site = "$instance" }, scopeField = "solarSystemId" }, Target: template.ContainerCount));
        }
        return new ContentDefinition(template.Kind, template.DefinitionId, objectives) { DurationMs = (long)Lifetime.TotalMilliseconds };
    }

    /// <summary>Defines the sites in the content runtime, registers the job and subscribes to the end of a site. Call it before the foundations start.</summary>
    public void Attach(ContentRuntime content, EventBus events)
    {
        foreach (var definition in Definitions(_templates))
        {
            content.Define(definition);
        }
        _scheduler.Register(SpawnJob, async (job, _) =>
            await FillAsync(job.Connection, job.Transaction, job.Payload<SpawnDue>()!.SolarSystemId));
        events.Subscribe(RespawnSubscriber,
            ["content.anomaly.completed", "content.anomaly.expired", "content.signature.completed", "content.signature.expired"],
            async (context, _) =>
            {
                var ended = context.Event.Json();
                if (ended.TryGetProperty("definition", out var definition) && definition.GetString() is { } id && id.StartsWith(DefinitionPrefix, StringComparison.Ordinal)
                    && ended.TryGetProperty("owner", out var owner) && owner.TryGetInt32(out var system))
                {
                    await FillAsync(context.Connection, context.Transaction, system);
                }
            });
    }

    /// <summary>
    /// Systems that get no sites: Zarzakh (30100000). The wiki names no site of it, and EveJS's site policy refuses them there, so none are placed.
    /// </summary>
    public static readonly IReadOnlySet<int> NoSiteSystems = new HashSet<int> { 30_100_000 };

    /// <summary>The band of security a system's sites are chosen for, or null when it has none (a wormhole system, a system the static data does not have, a system in <see cref="NoSiteSystems"/>).</summary>
    public string? BandOf(int solarSystemId) =>
        _systems.Get(solarSystemId) is not { } system || solarSystemId is >= 31_000_000 and <= 31_999_999 || NoSiteSystems.Contains(solarSystemId)
            ? null
            : Security.ClassFromLevel(system.Security) switch
            {
                SecurityClass.ZeroSec => "nullsec",
                SecurityClass.LowSec => "lowsec",
                _ => "highsec",
            };

    /// <summary>
    /// Makes sure the system has its sites and its recurring job (once in this process; the content runtime's unique key keeps a second start from making a second site). Whoever looks at
    /// a system's sites (a probe scan, the system scanner) or comes to it asks first.
    /// </summary>
    public Task EnsureAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        var ensured = _ensured.GetOrAdd(solarSystemId, system => new Lazy<Task>(() => StartAsync(system)));
        if (ensured.Value.IsFaulted)
        {
            _ensured.TryRemove(new KeyValuePair<int, Lazy<Task>>(solarSystemId, ensured));
        }
        return ensured.Value.WaitAsync(cancellationToken);
    }

    private async Task StartAsync(int solarSystemId)
    {
        if (BandOf(solarSystemId) is null && WormholeClassOf(solarSystemId) is null)
        {
            return;
        }
        await using (var connection = await _source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FillAsync(connection, transaction, solarSystemId);
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(SpawnJob, _scheduler.Now + (long)Sweep.TotalMilliseconds)
            {
                Key = $"system:{solarSystemId}", Payload = new SpawnDue(solarSystemId), EveryMs = (long)Sweep.TotalMilliseconds, OnDuplicate = OnDuplicate.Keep,
            });
            await transaction.CommitAsync();
        }
        _scheduler.Wake();
    }

    /// <summary>The active sites of a system, of both kinds.</summary>
    public async Task<IReadOnlyList<SiteRecord>> ActiveAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        var now = _scheduler.Now;
        var sites = new List<SiteRecord>();
        foreach (var kind in new[] { SiteTemplate.Anomaly, SiteTemplate.Signature })
        {
            foreach (var instance in await _content.ActiveOfAsync(kind, solarSystemId, cancellationToken))
            {
                if (SiteRecord.Of(instance) is { } site && (site.ExpiresMs is not { } expires || expires > now))
                {
                    sites.Add(site);
                }
            }
        }
        return sites;
    }

    /// <summary>
    /// Starts a site for every slot of the system that has none, in the caller's transaction. A slot's site is chosen among the sites of its kind and family that appear in the system's
    /// band, and placed beside one of the system's celestials, both by a seed made of the slot and of the site that was in it before (a respawn is therefore somewhere else, and the same
    /// run of the same history gives the same place).
    /// </summary>
    public async Task FillAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, CancellationToken cancellationToken = default)
    {
        if (WormholeClassOf(solarSystemId) is { } wormholeClass)
        {
            await FillWormholeAsync(connection, transaction, solarSystemId, wormholeClass, cancellationToken);
            return;
        }
        if (BandOf(solarSystemId) is not { } band || _occupied is { } occupied && !occupied(solarSystemId))
        {
            return;
        }
        var active = await ActiveAsync(solarSystemId, cancellationToken);
        var codes = active.Select(site => site.Code).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<SiteAnchor>? anchors = null;
        foreach (var (index, kind, family) in Slots)
        {
            var candidates = _templates.For(kind, family, band);
            if (candidates.Count == 0 || active.Any(site => site.Kind == kind && site.Slot == index))
            {
                continue;
            }
            var uniqueKey = $"system:{solarSystemId}:slot:{index}";
            var before = await _content.FindAsync(connection, transaction, kind, uniqueKey, cancellationToken) ?? 0;
            var seed = $"{kind}:{family}:{solarSystemId}:{index}:{before}";
            var template = candidates[(int)(SiteSeed.Hash(seed + ":template") % (uint)candidates.Count)];
            anchors ??= await _anchors.OfAsync(solarSystemId, cancellationToken);
            await StartSiteAsync(connection, transaction, template, solarSystemId, index, uniqueKey, seed, anchors, codes, cancellationToken);
        }
    }

    /// <summary>
    /// The sites of a J-space system: the plan of <see cref="WormholeSites"/> for its class and id, one site per planned signature, its index as the slot. A site
    /// that ends is replaced in the same slot of the plan, somewhere else (the seed carries what was in the slot before). The plan keeps its template: the wiki
    /// names the site of each signature, and no rule for its respawn is published (DEVIATIONS).
    /// </summary>
    private async Task FillWormholeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, int wormholeClass, CancellationToken cancellationToken)
    {
        if (_occupied is { } occupied && !occupied(solarSystemId))
        {
            return;
        }
        var plan = WormholeSites.Plan(wormholeClass, wormholeClass >= WormholeSites.DrifterFirstClass, (ulong)solarSystemId);
        var active = await ActiveAsync(solarSystemId, cancellationToken);
        var codes = active.Select(site => site.Code).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<SiteAnchor>? anchors = null;
        for (var index = 0; index < plan.Count; index++)
        {
            var planned = plan[index];
            if (planned.Category is "gas" or "ore")
            {
                continue; // no site of these kinds is made yet (DEVIATIONS: wormhole sites, not built)
            }
            var kind = planned.Category == "combat" ? SiteTemplate.Anomaly : SiteTemplate.Signature;
            if (_templates.ById($"wormhole-{planned.DungeonKey}") is not { } template)
            {
                _log?.Invoke($"wormhole system {solarSystemId}: {planned.SiteName} ({planned.DungeonKey}) has no site template and is not placed");
                continue;
            }
            if (active.Any(site => site.Kind == kind && site.Slot == index))
            {
                continue;
            }
            var uniqueKey = $"system:{solarSystemId}:wormhole:{index}";
            var before = await _content.FindAsync(connection, transaction, kind, uniqueKey, cancellationToken) ?? 0;
            var seed = $"wormhole:{solarSystemId}:{index}:{before}";
            anchors ??= await _anchors.OfAsync(solarSystemId, cancellationToken);
            await StartSiteAsync(connection, transaction, template, solarSystemId, index, uniqueKey, seed, anchors, codes, cancellationToken);
        }
    }

    /// <summary>The wormhole class of a J-space system, or null when it is not one (or the director has no J-space).</summary>
    public int? WormholeClassOf(int solarSystemId) => _wormholeClassOf?.Invoke(solarSystemId);

    /// <summary>Starts one site of a template in its slot: placed beside a celestial by the seed, its code unique in the system, its containers in the same transaction.</summary>
    private async Task StartSiteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SiteTemplate template, int solarSystemId, int index, string uniqueKey,
        string seed, IReadOnlyList<SiteAnchor> anchors, HashSet<string> codes, CancellationToken cancellationToken)
    {
        var (anchor, position) = SitePlacement.Place(anchors, seed);
        var code = SiteSeed.Code(seed);
        for (var attempt = 1; !codes.Add(code); attempt++)
        {
            code = SiteSeed.Code($"{seed}:{attempt}");
        }
        var started = await _content.StartAsync(connection, transaction, template.DefinitionId, solarSystemId, Data(template, solarSystemId, index, code, position, anchor), uniqueKey,
            cancellationToken: cancellationToken);
        if (started.Created && Containers is { } containerStore && ContainerType is { } typeOf)
        {
            await PlaceContainersAsync(connection, transaction, containerStore, typeOf, template, started.Id, solarSystemId, position, seed);
        }
        if (started.Created)
        {
            _log?.Invoke($"site {code}: {template.Name} ({template.Kind}, {template.Family}) in system {solarSystemId}, beside {anchor.Id}");
        }
    }

    /// <summary>
    /// The containers of a new site, in its fill transaction: each one beside the site, its type drawn by seed from the names its template gives (a respawn
    /// in the same place draws the same). Each container is an inventory item of its own, and its id is the container's. A name the static data does not
    /// have is not placed.
    /// </summary>
    private async Task PlaceContainersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SiteContainerStore store, Func<string, bool, (int TypeId, int GroupId)?> typeOf,
        SiteTemplate template, long siteId, int solarSystemId, double[] position, string seed)
    {
        if (NewContainerAsync is not { } newContainer)
        {
            _log?.Invoke($"site {siteId}: no container item maker is set, so its containers are not placed");
            return;
        }
        var records = new List<SiteContainerRecord>();
        var index = 0;
        var expires = _scheduler.Now + (long)Lifetime.TotalMilliseconds;
        foreach (var container in template.Containers)
        {
            // the site table names no type for 213 of its containers (relics and graveyards): none is made up, and the site goes on
            if (container.Types.Count == 0)
            {
                _log?.Invoke($"site {siteId}: the {container.Role} container of {template.Name} names no type and is not placed");
                index += container.Count;
                continue;
            }
            for (var copy = 0; copy < container.Count; copy++, index++)
            {
                var name = container.Types[(int)(SiteSeed.Hash($"{seed}:container:{index}") % (uint)container.Types.Count)];
                if (typeOf(name, container.Analyzer == "relic") is not { } type)
                {
                    _log?.Invoke($"site {siteId}: the container {name} has no type in the static data and is not placed");
                    continue;
                }
                var containerId = await newContainer(connection, transaction, type.TypeId, solarSystemId);
                var offset = ContainerSpacingMeters * (index + 1);
                records.Add(new SiteContainerRecord(
                    containerId, siteId, solarSystemId, container.Role, container.Analyzer == "relic", type.TypeId, type.GroupId,
                    [position[0] + offset, position[1], position[2]], ContainerRadiusMeters, template.Difficulty, 0, "secure", expires));
            }
        }
        await store.InsertAsync(connection, transaction, records);
    }

    /// <summary>The distance a container lies from the site's centre per container, and the radius a container is given (metres).</summary>
    public const double ContainerSpacingMeters = 150, ContainerRadiusMeters = 50;

    /// <summary>The facts of a site that go with its instance. The names are the client's own (<c>CosmicAnomalyInfo</c>), which the anomaly reader of the host sends as they are.</summary>
    private static JsonObject Data(SiteTemplate template, int solarSystemId, int slot, string code, double[] position, SiteAnchor anchor) => new()
    {
        ["siteID"] = SiteIdOf(solarSystemId, slot),
        ["solarSystemID"] = solarSystemId,
        ["template"] = template.Id,
        ["family"] = template.Family,
        ["slot"] = slot,
        ["targetID"] = code,
        ["position"] = new JsonArray(position[0], position[1], position[2]),
        ["anchorID"] = anchor.Id,
        ["difficulty"] = template.Difficulty,
        ["dungeonID"] = template.DungeonId,
        ["archetypeID"] = template.ArchetypeId,
        ["dungeonNameID"] = template.DungeonNameId,
        ["factionID"] = template.FactionId,
        ["scanStrengthAttribute"] = template.StrengthAttribute,
        ["allowedTypes"] = new JsonArray(),
        ["entryObjectTypeID"] = template.IsAnomaly ? AnomalyTypeId : SignatureTypeId,
    };

    /// <summary>The type of a cosmic anomaly's beacon (SDE 28356, group 885) and of a cosmic signature's (SDE 19728, group 502).</summary>
    public const int AnomalyTypeId = 28356, SignatureTypeId = 19728, AnomalyGroupId = 885, SignatureGroupId = 502;

    /// <summary>The id the client keys a site of a system by (the overlay): the system and the slot, so a respawn in a slot is the same key with another instance.</summary>
    public static long SiteIdOf(int solarSystemId, int slot) => 5_300_000_000_000L + solarSystemId * 1_000L + slot;
}
