using Eve.Characters;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.ProjectDiscovery;

// Owns the optional loopback asset listener; the host must dispose it on shutdown/failure.
internal sealed class ScientificReplayComposition : IAsyncDisposable
{
    private readonly LocalProjectDiscoveryAssetServer assets;
    private ScientificReplayComposition(LocalProjectDiscoveryAssetServer assets, ScientificReplayRpc rpc)
    { this.assets = assets; Rpc = rpc; }
    public ScientificReplayRpc Rpc { get; }
    public Uri AssetUri => assets.AssetUri;
    public GameRpcCallbacks Attach(GameRpcCallbacks callbacks) => callbacks with
    { GetProjectDiscoveryTask = Rpc.GetTaskAsync, SubmitProjectDiscoveryClassification = Rpc.SubmitAsync, ReadProjectDiscoveryStatistics = Rpc.ReadStatisticsAsync };
    public static async Task<ScientificReplayComposition?> StartAsync(string mode, NpgsqlDataSource source,
        ProjectDiscoveryTaskPinStore pins, ScientificReplayOptions options, string schema = "project_discovery", CancellationToken ct = default)
    {
        if (mode == "disabled") return null;
        if (mode != "scientific-replay") throw new ArgumentException("Unsupported Project Discovery provider mode.", nameof(mode));
        var assets = await LocalProjectDiscoveryAssetServer.StartAsync(options.ListenAddress, options.ListenPort, ct, options.AllowNonLoopback, options.AdvertisedOrigin);
        try
        {
            var dims = assets.AssetDimensions;
            var bound = options with { Enabled = true, AssetUri = assets.AssetUri, AssetSha256 = assets.AssetSha256, Width = dims.Width, Height = dims.Height };
            var provider = await ScientificReplayProvider.CreateAsync(source, bound, schema, ct);
            await provider.MigrateAsync(ct);
            return new ScientificReplayComposition(assets, new ScientificReplayRpc(provider, pins));
        }
        catch { await assets.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => assets.DisposeAsync();
}
