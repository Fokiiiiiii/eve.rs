using System.Text.Json;
using Eve.Content;

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
    public static readonly string[] Topics = ["content.anomaly.objective_completed", "content.signature.objective_completed"];

    private const string WavePrefix = "wave";

    private readonly CosmicSiteDirector _director;
    private readonly ContentRuntime _content;
    private readonly SpaceSessions _space;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SiteNpcs(CosmicSiteDirector director, ContentRuntime content, SpaceSessions space)
    {
        (_director, _content, _space) = (director, content, space);
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
            await _space.SpawnSiteWaveAsync(site.SolarSystemId, site.InstanceId, site.Position, template.Waves[wave.Index].Query, left, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
