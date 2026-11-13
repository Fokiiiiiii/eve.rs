using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Eve.Host.ProjectDiscovery;

/// <summary>Serves the bundled public research visualization on a local HTTP listener.</summary>
internal sealed class LocalProjectDiscoveryAssetServer : IAsyncDisposable
{
    public const string AssetKey = "projectdiscovery/covid-analysis50-science.png";
    private const string Route = "/projectdiscovery/covid-analysis50-science.png";
    private const string ResourceName = "Eve.Host.Assets.ProjectDiscovery.covid-analysis50-science.png";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly WebApplication _app;
    private readonly byte[] _png;

    private LocalProjectDiscoveryAssetServer(WebApplication app, byte[] png, Uri baseUri)
    {
        _app = app;
        _png = png;
        BaseUri = baseUri;
        AssetDimensions = ReadDimensions(png);
    }

    /// <summary>The actual bound HTTP base URI reported by Kestrel.</summary>
    public Uri BaseUri { get; }
    public (int Width, int Height) AssetDimensions { get; }
    public Uri AssetUri => new(BaseUri, AssetKey);
    public string AssetSha256 => Convert.ToHexString(SHA256.HashData(_png)).ToLowerInvariant();
    public Uri TutorialAssetUri(string code, long sequence)
    {
        if (!CovidTutorialCorpus.Contains(code) || sequence <= 0)
            throw new ArgumentException("Unknown Project Discovery tutorial asset.", nameof(code));
        return new Uri(BaseUri, $"projectdiscovery/tutorials/{code}/{sequence}.png");
    }

    /// <summary>Maps client normalized gate coordinates (x right, y up) to top-origin PNG pixels.</summary>
    public static (int X, int Y) GateToPixel(double x, double y, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(x), "Gate proportions must be finite and within [0,1].");
        var pixelX = Math.Min(width - 1, (int)Math.Floor(x * width));
        var bottomOriginY = Math.Min(height - 1, (int)Math.Floor(y * height));
        return (pixelX, height - 1 - bottomOriginY);
    }

    /// <summary>
    /// Starts an HTTP/1 listener. The default bind is loopback with an ephemeral port;
    /// a non-default bind or advertised base URI must be explicitly supplied by the host.
    /// </summary>
    public static async Task<LocalProjectDiscoveryAssetServer> StartAsync(
        IPAddress? address = null,
        int port = 0,
        CancellationToken cancellationToken = default,
        bool allowNonLoopback = false,
        Uri? advertisedOrigin = null)
    {
        address ??= IPAddress.Loopback;
        if (!IPAddress.IsLoopback(address) && !allowNonLoopback) throw new ArgumentException("Non-loopback binding requires explicit opt-in.", nameof(address));
        if (advertisedOrigin is not null && (!advertisedOrigin.IsAbsoluteUri || (advertisedOrigin.Scheme != Uri.UriSchemeHttp && advertisedOrigin.Scheme != Uri.UriSchemeHttps) || advertisedOrigin.AbsolutePath != "/" || !string.IsNullOrEmpty(advertisedOrigin.Query) || !string.IsNullOrEmpty(advertisedOrigin.Fragment) || !string.IsNullOrEmpty(advertisedOrigin.UserInfo)))
            throw new ArgumentException("The advertised origin must be an absolute HTTP(S) origin.", nameof(advertisedOrigin));
        if (!IPAddress.IsLoopback(address) && advertisedOrigin is null) throw new ArgumentException("Non-loopback binding requires an advertised origin.", nameof(advertisedOrigin));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var png = ReadBundledPng();
        _ = ReadDimensions(png);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port, listen =>
            listen.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.Run(context => RouteRequest(context, png));
        try
        {
            await app.StartAsync(cancellationToken);
            var boundUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(boundUrl))
                throw new InvalidOperationException("Kestrel did not report the bound local asset URL.");
            var baseUri = EnsureBaseUri(advertisedOrigin ?? new Uri(boundUrl, UriKind.Absolute));
            return new LocalProjectDiscoveryAssetServer(app, png, baseUri);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => new(StopAndDisposeAsync());

    private async Task StopAndDisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static Task RouteRequest(HttpContext context, byte[] png)
    {
        byte[]? asset = string.Equals(context.Request.Path.Value, Route, StringComparison.Ordinal)
            ? png
            : TutorialAsset(context.Request.Path.Value);
        if (asset is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        if (HttpMethods.IsHead(context.Request.Method))
        {
            SetPngHeaders(context, asset.Length);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.Headers["Allow"] = "GET, HEAD";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        }
        SetPngHeaders(context, asset.Length);
        context.Response.StatusCode = StatusCodes.Status200OK;
        return context.Response.Body.WriteAsync(asset, context.RequestAborted).AsTask();
    }

    private static byte[]? TutorialAsset(string? path)
    {
        if (path is null) return null;
        var parts = path.Split('/');
        if (parts.Length != 5 || parts[1] != "projectdiscovery" || parts[2] != "tutorials" ||
            !parts[4].EndsWith(".png", StringComparison.Ordinal) ||
            !long.TryParse(parts[4].AsSpan(0, parts[4].Length - 4), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long sequence) || sequence <= 0 ||
            !CovidTutorialCorpus.Contains(parts[3]))
            return null;
        return CovidTutorialCorpus.RenderPng(parts[3], sequence);
    }

    private static void SetPngHeaders(HttpContext context, int length)
    {
        context.Response.ContentType = "image/png";
        context.Response.ContentLength = length;
        context.Response.Headers["Cache-Control"] = "public, max-age=3600";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static byte[] ReadBundledPng()
    {
        using var stream = typeof(LocalProjectDiscoveryAssetServer).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded Project Discovery asset '{ResourceName}' is missing.");
        if (stream.Length > 2 * 1024 * 1024)
            throw new InvalidOperationException("Embedded Project Discovery asset exceeds its size limit.");
        using var memory = new MemoryStream((int)stream.Length);
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static (int Width, int Height) ReadDimensions(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(PngSignature) ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Embedded Project Discovery asset is not a valid PNG header.");
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096)
            throw new InvalidDataException("Embedded Project Discovery asset has invalid dimensions.");
        return (width, height);
    }

    private static Uri EnsureBaseUri(Uri uri) => uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
        ? uri
        : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
}
