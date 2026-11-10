using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Content;
using Eve.Events;

namespace Eve.Services.Space.Sites;

/// <summary>
/// The NPC waves of the cosmic sites. A wave is an active objective of a site, <c>wave1</c> to <c>waveN</c>, completed by the kills of its NPCs (the NPCs are counted by
/// <c>npc.killed</c> with the site's id). The NPCs live in the scene of their system, which the simulation keeps only while a pilot is in the system: a wave is made when a
/// pilot is in the system and no NPC of the site is in the scene, with what is left of its target (the target less the progress the content runtime kept). So a scene made again,
/// after everybody left or after a restart, brings back exactly the kills still to come, and a kill is never made twice.
/// </summary>
public sealed class SiteNpcs
{
    public const string SubscriberName = "site.npc-waves";

    /// <summary>The completion of a wave: the next wave of the site starts.</summary>
    public static readonly string[] Topics = ["npc.killed", "content.anomaly.objective_completed", "content.signature.objective_completed"];

    private const string WavePrefix = "wave";

    private readonly CosmicSiteDirector _director;
    private readonly ContentRuntime _content;
    private readonly SpaceSessions _space;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SiteNpcs(CosmicSiteDirector director, ContentRuntime content, SpaceSessions space)
    {
        (_director, _content, _space) = (director, content, space);
    }

    /// <summary>Registers the wave and typed-kill handler with the Host event bus.</summary>
    public void Register(EventBus events) => events.Subscribe(SubscriberName, Topics, HandleAsync);

    /// <summary>Records typed site kills transactionally with the event cursor, or starts the next wave after objective completion.</summary>
    public async Task HandleAsync(EventContext context, CancellationToken cancellationToken = default)
    {
        if (context.Event.Topic == "npc.killed")
        {
            await RecordNpcTypeKillAsync(context, cancellationToken);
            return;
        }
        await OnObjectiveCompletedAsync(context.Event.Json(), cancellationToken);
    }

    /// <summary>The waves of the sites of a system, when a pilot is in it (a system nobody is in has no scene, so nothing is made there).</summary>
    public async Task SpawnForSystemAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        if (!_space.HasPilotsIn(solarSystemId))
        {
            return;
        }
        foreach (var site in await _director.ActiveAsync(solarSystemId, cancellationToken))
        {
            await SpawnActiveWaveAsync(site, cancellationToken);
        }
    }

    /// <summary>A wave of a site is done: the site's next wave is made, in its system when a pilot is there.</summary>
    public async Task OnObjectiveCompletedAsync(JsonElement completed, CancellationToken cancellationToken = default)
    {
        if (!completed.TryGetProperty("owner", out var owner) || !completed.TryGetProperty("instanceId", out var instance))
        {
            return;
        }
        var solarSystemId = (int)owner.GetInt64();
        if (!_space.HasPilotsIn(solarSystemId))
        {
            return;
        }
        foreach (var site in await _director.ActiveAsync(solarSystemId, cancellationToken))
        {
            if (site.InstanceId == instance.GetInt64())
            {
                await SpawnActiveWaveAsync(site, cancellationToken);
            }
        }
    }

    private async Task SpawnActiveWaveAsync(SiteRecord site, CancellationToken cancellationToken)
    {
        if (_director.Templates.ById(site.TemplateId) is not { } template)
        {
            return;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_space.HasSiteNpcs(site.InstanceId))
            {
                return;
            }
            var wave = (await _content.ObjectivesOfAsync(site.InstanceId, cancellationToken)).FirstOrDefault(objective => objective.State == "active");
            if (wave is null || !wave.Id.StartsWith(WavePrefix, StringComparison.Ordinal) || wave.Index >= template.Waves.Count)
            {
                return;
            }
            var left = (int)Math.Ceiling(wave.Target - wave.Progress);
            var waveTemplate = template.Waves[wave.Index];
            if (waveTemplate.ProfileCounts is { Count: > 0 })
            {
                var exactProfiles = await RemainingExactProfilesAsync(site.InstanceId, template, wave.Index, wave, cancellationToken);
                if (exactProfiles is not null && exactProfiles.Count == left)
                {
                    await _space.SpawnSiteProfilesWaveAsync(site.SolarSystemId, site.InstanceId, site.Position, exactProfiles, cancellationToken);
                    return;
                }
            }
            await _space.SpawnSiteWaveAsync(site.SolarSystemId, site.InstanceId, site.Position, waveTemplate.Query, left, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecordNpcTypeKillAsync(EventContext context, CancellationToken cancellationToken)
    {
        var payload = context.Event.Json();
        if (!payload.TryGetProperty("site", out var siteValue) || !siteValue.TryGetInt64(out var siteId)
            || !payload.TryGetProperty("typeId", out var typeValue) || !typeValue.TryGetInt32(out var typeId)
            || siteId <= 0 || typeId <= 0)
        {
            return;
        }
        var instance = await _content.GetForUpdateAsync(context.Connection, context.Transaction, siteId, cancellationToken);
        if (instance is null || SiteRecord.Of(instance) is not { } site
            || _director.Templates.ById(site.TemplateId)?.Waves.Any(wave => wave.ProfileCounts is { Count: > 0 }) != true)
        {
            return;
        }

        var data = JsonNode.Parse(instance.DataJson) as JsonObject ?? new JsonObject();
        var typedKills = data["npcTypeKills"] as JsonObject ?? new JsonObject();
        var key = typeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        typedKills[key] = checked(((int?)typedKills[key] ?? 0) + 1);
        data["npcTypeKills"] = typedKills;
        await _content.UpdateDataAsync(context.Connection, context.Transaction, siteId, data, cancellationToken);
    }

    private async Task<IReadOnlyList<string>?> RemainingExactProfilesAsync(
        long siteId, SiteTemplate template, int waveIndex, Eve.Content.ObjectiveInfo wave, CancellationToken cancellationToken)
    {
        var instance = await _content.GetAsync(siteId, cancellationToken);
        if (instance is null || !TryReadNpcTypeKills(instance.DataJson, out var killsByType))
        {
            return null;
        }
        var typedKillTotal = killsByType.Values.Sum();
        var expectedKillTotal = template.Waves.Take(waveIndex).Sum(previous => previous.Amount) + wave.Progress;
        if (Math.Abs(typedKillTotal - expectedKillTotal) > 0.001)
        {
            // A site that was already in progress when typed kill persistence was introduced has only aggregate progress.
            // Keep generic recovery for the remainder of that site's lifetime; its earlier per-type kills cannot be reconstructed.
            return null;
        }

        var authority = _space.Npcs?.Authority;
        if (authority is null)
        {
            return null;
        }
        var priorByType = new Dictionary<int, int>();
        foreach (var previous in template.Waves.Take(waveIndex))
        {
            foreach (var profileCount in previous.ProfileCounts ?? [])
            {
                if (authority.Profile(profileCount.ProfileId) is not { } profile)
                {
                    return null;
                }
                priorByType[profile.ShipTypeId] = priorByType.GetValueOrDefault(profile.ShipTypeId) + profileCount.Count;
            }
        }

        var result = new List<string>();
        foreach (var profileCount in template.Waves[waveIndex].ProfileCounts ?? [])
        {
            if (authority.Profile(profileCount.ProfileId) is not { } profile)
            {
                return null;
            }
            var killedInThisWave = Math.Clamp(killsByType.GetValueOrDefault(profile.ShipTypeId) - priorByType.GetValueOrDefault(profile.ShipTypeId), 0, profileCount.Count);
            for (var count = killedInThisWave; count < profileCount.Count; count++)
            {
                result.Add(profileCount.ProfileId);
            }
        }
        return result;
    }

    private static bool TryReadNpcTypeKills(string dataJson, out Dictionary<int, int> killsByType)
    {
        killsByType = [];
        using var document = JsonDocument.Parse(dataJson);
        if (!document.RootElement.TryGetProperty("npcTypeKills", out var typedKills) || typedKills.ValueKind != JsonValueKind.Object)
        {
            return true;
        }
        foreach (var property in typedKills.EnumerateObject())
        {
            if (int.TryParse(property.Name, System.Globalization.CultureInfo.InvariantCulture, out var typeId)
                && typeId > 0 && property.Value.TryGetInt32(out var count) && count >= 0)
            {
                killsByType[typeId] = count;
            }
        }
        return true;
    }
}
