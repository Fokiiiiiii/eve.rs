using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Eve.Host.ProjectDiscovery;

namespace Eve.Host.Tests;

public sealed class MmosApiClientTests
{
    // Independently calculated using Python hmac/hashlib from the official SDK canonicalization.
    private const string ExpectedSignature = "ea36d9e58af74559cded6345075ba10cbbb6c4822f97b449f5c3d01562a63896";
    [Fact]
    public async Task TaskRequestUsesSdkSignatureAndPreservesProjectPayload()
    {
        using var fixture = new Loopback();
        using var http = new HttpClient();
        var client = Create(http, fixture.Uri);
        var pending = client.AllocateTaskAsync("demo", "123", "mcgill-polyseg", "acct-123");
        var context = await fixture.AcceptAsync();
        Assert.Equal("POST", context.Request.HttpMethod);
        Assert.Equal("/games/demo/players/123/tasks", context.Request.RawUrl);
        Assert.Equal(ExpectedSignature, context.Request.Headers["X-MMOS-Signature"]);
        Assert.Equal("test-nonce", context.Request.Headers["X-MMOS-Nonce"]);
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        Assert.Equal("{\"projects\":[\"mcgill-polyseg\"],\"player\":{\"accountCode\":\"acct-123\"}}", await reader.ReadToEndAsync());
        await ReplyAsync(context, 201, "{\"uid\":\"u\",\"game\":\"demo\",\"player\":{\"code\":\"123\"},\"task\":{\"id\":\"t\",\"assets\":{\"custom\":[1,2]}}}");
        var result = await pending;
        Assert.Equal(2, result.Task.GetProperty("assets").GetProperty("custom").GetArrayLength());
    }
    [Fact]
    public async Task PlayerNotFoundAndHttpErrorAreTyped()
    {
        using var fixture = new Loopback(); using var http = new HttpClient(); var client = Create(http, fixture.Uri);
        var pending = client.GetPlayerAsync("demo", "123", "mcgill-polyseg");
        var context = await fixture.AcceptAsync();
        Assert.Equal("/games/demo/players/123?project=mcgill-polyseg", context.Request.RawUrl);
        await ReplyAsync(context, 404, "{}"); Assert.Null(await pending);
        var next = client.AllocateTaskAsync("demo", "123", "p", "a");
        context = await fixture.AcceptAsync(); await ReplyAsync(context, 403, "{\"code\":\"denied\",\"details\":[1]}");
        var ex = await Assert.ThrowsAsync<MmosHttpException>(() => next);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode); Assert.Equal("denied", ex.Error!.Value.GetProperty("code").GetString());
    }
    [Fact]
    public async Task LostClassificationResponseIsUnknownAndNeverResent()
    {
        using var fixture = new Loopback(); using var http = new HttpClient(); var client = Create(http, fixture.Uri);
        using var doc = JsonDocument.Parse("{\"skip\":false,\"polygons\":[]}");
        var pending = client.SubmitClassificationAsync("demo", "123", null, "t", doc.RootElement, doc.RootElement);
        var context = await fixture.AcceptAsync(); Assert.Equal("/classifications", context.Request.RawUrl);
        context.Response.Abort();
        await Assert.ThrowsAsync<MmosSubmissionOutcomeUnknownException>(() => pending);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.AcceptAsync().WaitAsync(timeout.Token));
    }
    [Fact]
    public async Task DisabledAndPreCancelledRequestsNeverSend()
    {
        using var http = new HttpClient();
        Assert.Throws<InvalidOperationException>(() => new MmosApiClient(http, new MmosApiOptions()));
        using var fixture = new Loopback(); var client = Create(http, fixture.Uri);
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPlayerAsync("g", "p", "x", token.Token));
    }
    [Fact]
    public async Task OversizedResponseIsRejectedAndClassificationCancellationIsUnknown()
    {
        using var fixture = new Loopback(); using var http = new HttpClient();
        var options = new MmosApiOptions { Enabled = true, BaseUri = fixture.Uri, Credential = "fixture-key", Secret = "fixture-secret", MaxResponseBytes = 16 };
        var client = new MmosApiClient(http, options);
        var pending = client.GetPlayerAsync("g", "p", "x"); var context = await fixture.AcceptAsync();
        await ReplyAsync(context, 200, "{\"thisIsLongerThanSixteen\":true}");
        await Assert.ThrowsAsync<MmosResponseException>(() => pending);
        using var doc = JsonDocument.Parse("{}"); using var cancel = new CancellationTokenSource();
        var submit = client.SubmitClassificationAsync("g", "p", null, "t", doc.RootElement, doc.RootElement, cancel.Token);
        context = await fixture.AcceptAsync(); cancel.Cancel();
        await Assert.ThrowsAsync<MmosSubmissionOutcomeUnknownException>(() => submit); context.Response.Abort();
    }
    [Fact]
    public async Task ConcurrentRequestsHaveIndependentNonceHeaders()
    {
        using var fixture = new Loopback(); using var http = new HttpClient(); int number = 0;
        var client = new MmosApiClient(http, new MmosApiOptions { Enabled = true, BaseUri = fixture.Uri, Credential = "fixture-key", Secret = "fixture-secret" }, nonce: () => Interlocked.Increment(ref number).ToString());
        var first = client.GetPlayerAsync("g", "a", "p"); var second = client.GetPlayerAsync("g", "b", "p");
        var a = await fixture.AcceptAsync(); var b = await fixture.AcceptAsync();
        Assert.NotEqual(a.Request.Headers["X-MMOS-Nonce"], b.Request.Headers["X-MMOS-Nonce"]);
        Assert.NotEqual(a.Request.Headers["X-MMOS-Signature"], b.Request.Headers["X-MMOS-Signature"]);
        await ReplyAsync(a, 404, "{}"); await ReplyAsync(b, 404, "{}");
        Assert.Null(await first); Assert.Null(await second);
    }
    [Fact]
    public async Task SuccessfulPlayerAndClassificationPreserveEnvelopeAndProjectData()
    {
        using var fixture = new Loopback(); using var http = new HttpClient(); var client = Create(http, fixture.Uri);
        var player = client.GetPlayerAsync("demo", "123", "p"); var context = await fixture.AcceptAsync();
        await ReplyAsync(context, 200, "{\"uid\":\"u\",\"game\":\"demo\",\"player\":{\"code\":\"123\"},\"project\":{\"score\":0.75,\"custom\":[1]}}");
        var playerValue = await player; Assert.NotNull(playerValue); Assert.Equal(0.75, playerValue.Project!.Value.GetProperty("score").GetDouble());
        using var result = JsonDocument.Parse("{\"skip\":false,\"polygons\":[[1,2]]}");
        using var times = JsonDocument.Parse("{\"t\":321,\"tPolygons\":[42]}");
        var classification = client.SubmitClassificationAsync("demo", "123", null, "task-1", result.RootElement, times.RootElement);
        context = await fixture.AcceptAsync();
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        using var request = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("task-1", request.RootElement.GetProperty("task").GetProperty("id").GetString());
        Assert.Equal(321, request.RootElement.GetProperty("circumstances").GetProperty("t").GetInt32());
        Assert.False(request.RootElement.GetProperty("task").GetProperty("result").GetProperty("skip").GetBoolean());
        await ReplyAsync(context, 201, "{\"uid\":\"c\",\"score\":0.9,\"player\":{\"score\":0.8},\"task\":{\"id\":\"task-1\",\"solution\":{\"clusters\":[{\"polygons\":[[3,4]]}]}}}");
        var value = await classification;
        Assert.Equal("c", value.Uid); Assert.Equal(0.8, value.Player.GetProperty("score").GetDouble());
        Assert.Equal(1, value.Task.GetProperty("solution").GetProperty("clusters").GetArrayLength());
        Assert.Equal(0.9, value.Raw.GetProperty("score").GetDouble());
    }
    [Fact]
    public async Task ChunkedResponseIsBoundedWithoutContentLength()
    {
        using var fixture = new Loopback(); using var http = new HttpClient();
        var client = new MmosApiClient(http, new MmosApiOptions { Enabled = true, BaseUri = fixture.Uri, Credential = "fixture-key", Secret = "fixture-secret", MaxResponseBytes = 16 });
        var pending = client.GetPlayerAsync("g", "p", "x"); var context = await fixture.AcceptAsync();
        context.Response.SendChunked = true; context.Response.StatusCode = 200;
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"moreThanSixteenBytes\":true}"));
        context.Response.Close();
        await Assert.ThrowsAsync<MmosResponseException>(() => pending);
    }
    private static MmosApiClient Create(HttpClient http, Uri uri) => new(http, new MmosApiOptions { Enabled = true, BaseUri = uri, Credential = "fixture-key", Secret = "fixture-secret" }, () => DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), () => "test-nonce");
    private static async Task ReplyAsync(HttpListenerContext context, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body); context.Response.StatusCode = status; context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    }
    private sealed class Loopback : IDisposable
    {
        private readonly HttpListener listener = new();
        public Uri Uri { get; }
        public Loopback()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Uri = new Uri($"http://127.0.0.1:{port}/"); listener.Prefixes.Add(Uri.ToString()); listener.Start();
        }
        public Task<HttpListenerContext> AcceptAsync() => listener.GetContextAsync();
        public void Dispose() => listener.Close();
    }
}
