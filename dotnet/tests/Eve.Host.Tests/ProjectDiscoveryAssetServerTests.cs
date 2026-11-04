using System.Net;
using System.Security.Cryptography;
using Eve.Host.ProjectDiscovery;

namespace Eve.Host.Tests;

public sealed class ProjectDiscoveryAssetServerTests
{
    [Fact]
    public async Task ServesOnlyTheBundledPngByGetAndHeadOnTheActualLoopbackPort()
    {
        await using var server = await LocalProjectDiscoveryAssetServer.StartAsync();
        Assert.Equal(IPAddress.Loopback, IPAddress.Parse(server.BaseUri.Host));
        Assert.InRange(server.BaseUri.Port, 1, 65535);
        Assert.Equal(new Uri(server.BaseUri, LocalProjectDiscoveryAssetServer.AssetKey), server.AssetUri);
        Assert.Equal("766bb12193db2cd09f0e2881d1c8de865e7747afe102182f2d97e40520b469a4", server.AssetSha256);
        Assert.Equal((530, 530), server.AssetDimensions);

        using var client = new HttpClient();
        using var get = await client.GetAsync(server.AssetUri);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", get.Headers.TryGetValues("X-Content-Type-Options", out var values) ? values.Single() : null);
        var bytes = await get.Content.ReadAsByteArrayAsync();
        Assert.Equal(get.Content.Headers.ContentLength, bytes.LongLength);
        Assert.Equal("766BB12193DB2CD09F0E2881D1C8DE865E7747AFE102182F2D97E40520B469A4", Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal(530, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(530, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));

        // The client stores gates in a bottom-origin normalized frame. The top-most
        // recorded polygon vertex must therefore land near the top of the PNG.
        Assert.Equal((0, 529), LocalProjectDiscoveryAssetServer.GateToPixel(0, 0, 530, 530));
        Assert.Equal((529, 0), LocalProjectDiscoveryAssetServer.GateToPixel(1, 1, 530, 530));
        var publishedTopVertex = LocalProjectDiscoveryAssetServer.GateToPixel(0.469811320754717, 0.05849056603773584, 530, 530);
        Assert.InRange(publishedTopVertex.X, 248, 250);
        Assert.InRange(publishedTopVertex.Y, 497, 499);

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.AssetUri));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var missing = await client.GetAsync(new Uri(server.BaseUri, "projectdiscovery/other.png"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var post = await client.PostAsync(server.AssetUri, new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.True(post.Content.Headers.TryGetValues("Allow", out var allowedMethods), $"Response headers: {post.Headers}; content headers: {post.Content.Headers}");
        Assert.Contains("GET", allowedMethods);
        Assert.Contains("HEAD", allowedMethods);
    }

    [Fact]
    public async Task ExplicitLoopbackBindingPublishesTheActualChosenPort()
    {
        await using var server = await LocalProjectDiscoveryAssetServer.StartAsync(IPAddress.Loopback, 0);
        Assert.Equal(IPAddress.Loopback, IPAddress.Parse(server.BaseUri.Host));
        Assert.InRange(server.BaseUri.Port, 1, 65535);
        using var client = new HttpClient();
        using var response = await client.GetAsync(server.AssetUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
