using Eve.Characters;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.ProjectDiscovery;

// Owns the optional loopback asset listener; the host must dispose it on shutdown/failure.
internal sealed class ScientificReplayComposition : IAsyncDisposable
{
    private readonly LocalProjectDiscoveryAssetServer assets;
    private readonly CovidTutorialProvider tutorials;
    private ScientificReplayComposition(LocalProjectDiscoveryAssetServer assets, ScientificReplayRpc rpc, CovidTutorialProvider tutorials)
    { this.assets = assets; Rpc = rpc; this.tutorials = tutorials; }
    public ScientificReplayRpc Rpc { get; }
    public Uri AssetUri => assets.AssetUri;
    public GameRpcCallbacks Attach(GameRpcCallbacks callbacks)
    {
        async ValueTask<ProjectDiscoveryPlayerState?> TransitionTutorialAsync(ServiceCall call, string action)
        {
            if (callbacks.TransitionProjectDiscoveryTutorialCall is not { } transition)
                return null;
            var state = await transition(call, action);
            if (action == "skip" && await tutorials.TryGrantCompletionRewardAsync(call.Session, call.Cancellation) &&
                callbacks.ReadProjectDiscoveryState is { } readState && call.Session.CharacterId is { } characterId)
                return await readState(characterId, call.Cancellation) ?? state;
            return state;
        }

        return callbacks with
        {
            GetProjectDiscoveryTask = Rpc.GetTaskAsync,
            SubmitProjectDiscoveryClassification = Rpc.SubmitAsync,
            ReadProjectDiscoveryStatistics = Rpc.ReadStatisticsAsync,
            ReadProjectDiscoveryState = Rpc.ReadPlayerStateAsync,
            ReadProjectDiscoveryBonusSamples = Rpc.ReadBonusSamplesAsync,
            NotifyProjectDiscoveryStateAfterSubmit = true,
            TransitionProjectDiscoveryTutorialCall = callbacks.TransitionProjectDiscoveryTutorialCall is null
                ? null
                : TransitionTutorialAsync,
        };
    }
    public static async Task<ScientificReplayComposition?> StartAsync(string mode, NpgsqlDataSource source,
        ProjectDiscoveryTaskPinStore pins, ScientificReplayOptions options, string schema = "project_discovery",
        string characterSchema = "characters", CancellationToken ct = default, Func<long>? now = null)
    {
        if (mode == "disabled") return null;
        if (mode != "scientific-replay") throw new ArgumentException("Unsupported Project Discovery provider mode.", nameof(mode));
        var assets = await LocalProjectDiscoveryAssetServer.StartAsync(options.ListenAddress, options.ListenPort, ct, options.AllowNonLoopback, options.AdvertisedOrigin);
        try
        {
            var dims = assets.AssetDimensions;
            var bound = options with { Enabled = true, AssetUri = assets.AssetUri, AssetSha256 = assets.AssetSha256, Width = dims.Width, Height = dims.Height };
            var progress = new ProjectDiscoveryTutorialStore(source, characterSchema);
            var provider = await ScientificReplayProvider.CreateAsync(source, bound, schema, ct, progress, now);
            await provider.MigrateAsync(ct);
            var tutorials = new CovidTutorialProvider(source, assets, schema, characterSchema, now);
            await tutorials.MigrateAsync(ct);
            return new ScientificReplayComposition(assets, new ScientificReplayRpc(provider, pins, tutorials), tutorials);
        }
        catch { await assets.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => assets.DisposeAsync();
}
