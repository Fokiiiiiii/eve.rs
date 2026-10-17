using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Eve.Host.PublicGateway;

/// <summary>What a request type answers: the status (the reference's codes, 200 for success), its message and the response type name.</summary>
internal sealed record GatewayAnswer(uint StatusCode, string StatusMessage, string ResponseTypeName, byte[] Payload);

/// <summary>Answers one request type; the request is the decoded envelope (its character is the active identity).</summary>
internal delegate Task<GatewayAnswer> GatewayHandler(GatewayRequest request, CancellationToken cancellationToken);

/// <summary>What the listener serves: the answers by request type, the notices of the open Notices.Consume streams, and who a request is for.</summary>
internal sealed record PublicGatewayRoutes(IReadOnlyDictionary<string, GatewayHandler> Handlers, NoticeHub Notices, GatewayAuthority Authority);

/// <summary>
/// The public protobuf gateway of the V24.01 client, served on an HTTP/2 listener without TLS (h2c; the client reaches it through
/// EVE_PUBLIC_GATEWAY_ADDRESS). The routes are those of EveJS publicGatewayLocal.js: Requests/Send answers one response frame per request
/// frame, in order; the Ping calls answer an empty frame; Events/Publish is acknowledged and not stored (the reference keeps no events either).
/// Notices/Consume is a stream the client keeps open: the request frames name the active character, and the notices routed to it (or to the
/// token's account) are written to it. A request's character is trusted only when the account of the request's bearer token owns it
/// (<see cref="GatewayAuthority"/>); without a token the request is refused with 401.
/// </summary>
internal sealed class PublicGatewayServer : IAsyncDisposable
{
    private const string GrpcContentType = "application/grpc+proto";
    private const int StatusUnimplemented = 12;

    private readonly WebApplication _app;

    private PublicGatewayServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>The port the listener is bound to (the one asked for, or the one the system chose for 0).</summary>
    public int Port { get; }

    public static async Task<PublicGatewayServer> StartAsync(IPAddress address, int port, PublicGatewayRoutes routes, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port, listen => listen.Protocols = HttpProtocols.Http2));
        var app = builder.Build();
        app.Run(context => Route(context, routes));
        await app.StartAsync(cancellationToken);
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new PublicGatewayServer(app, new Uri(url).Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static Task Route(HttpContext context, PublicGatewayRoutes routes) => context.Request.Path.Value switch
    {
        "/eve_public.gateway.Requests/Ping" or "/eve_public.gateway.Notices/Ping" or "/eve_public.gateway.Events/Ping" => Guard(context, () => AnswerEmpty(context, false)),
        "/eve_public.gateway.Requests/Send" => Guard(context, () => Send(context, routes)),
        "/eve_public.gateway.Notices/Consume" => Guard(context, () => Consume(context, routes)),
        "/eve_public.gateway.Events/Publish" => Guard(context, () => AnswerEmpty(context, true)),
        _ => Unimplemented(context),
    };

    /// <summary>A failure outside the handlers resets the stream (the client sees INTERNAL); the cause goes to the host's log first.</summary>
    private static async Task Guard(HttpContext context, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"eve-host: public gateway {context.Request.Path} failed: {error}");
            throw;
        }
    }

    private static async Task Send(HttpContext context, PublicGatewayRoutes routes)
    {
        await StartGrpc(context);
        var account = routes.Authority.AccountOf(context.Request.Headers["authorization"]);
        var pending = new List<byte>();
        var reader = context.Request.BodyReader;
        while (true)
        {
            var read = await reader.ReadAsync(context.RequestAborted);
            foreach (var segment in read.Buffer)
                pending.AddRange(segment.ToArray());
            reader.AdvanceTo(read.Buffer.End);
            foreach (var frame in GatewayEnvelope.TakeFrames(pending))
            {
                var response = await Answer(frame, routes, account, context.RequestAborted);
                await context.Response.BodyWriter.WriteAsync(GatewayEnvelope.Frame(response), context.RequestAborted);
            }
            if (read.IsCompleted)
                break;
        }
        await FinishGrpc(context, 0);
    }

    /// <summary>Acknowledges each request frame with an empty response frame, as the reference does (Events/Publish and the Ping calls).</summary>
    private static async Task AnswerEmpty(HttpContext context, bool perFrame)
    {
        await StartGrpc(context);
        var reader = context.Request.BodyReader;
        var pending = new List<byte>();
        while (true)
        {
            var read = await reader.ReadAsync(context.RequestAborted);
            foreach (var segment in read.Buffer)
                pending.AddRange(segment.ToArray());
            reader.AdvanceTo(read.Buffer.End);
            if (perFrame)
            {
                foreach (var _ in GatewayEnvelope.TakeFrames(pending))
                    await context.Response.BodyWriter.WriteAsync(GatewayEnvelope.Frame(ReadOnlySpan<byte>.Empty), context.RequestAborted);
            }
            if (read.IsCompleted)
                break;
        }
        if (!perFrame)
            await context.Response.BodyWriter.WriteAsync(GatewayEnvelope.Frame(ReadOnlySpan<byte>.Empty), context.RequestAborted);
        await FinishGrpc(context, 0);
    }

    /// <summary>A trailers-only gRPC answer with the status UNIMPLEMENTED.</summary>
    private static Task Unimplemented(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = GrpcContentType;
        context.Response.Headers["grpc-status"] = StatusUnimplemented.ToString();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Notices/Consume: the stream stays open until the client goes. The request frames are read beside it (each names the active character,
    /// which counts only when the token's account owns it); the notices routed to the stream are written as they come.
    /// </summary>
    private static async Task Consume(HttpContext context, PublicGatewayRoutes routes)
    {
        await StartGrpc(context);
        // StartAsync leaves the headers of a stream that has no body yet unflushed; the client waits for them before it reads a notice
        await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
        var subscriber = routes.Notices.Subscribe(routes.Authority.AccountOf(context.Request.Headers["authorization"]));
        var identifying = IdentifyAsync(context, subscriber, routes.Authority);
        try
        {
            await foreach (var frame in subscriber.Frames.Reader.ReadAllAsync(context.RequestAborted))
                await context.Response.BodyWriter.WriteAsync(frame, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        finally
        {
            routes.Notices.Unsubscribe(subscriber);
        }
        await identifying.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static async Task IdentifyAsync(HttpContext context, NoticeSubscriber subscriber, GatewayAuthority authority)
    {
        var reader = context.Request.BodyReader;
        var pending = new List<byte>();
        while (true)
        {
            var read = await reader.ReadAsync(context.RequestAborted);
            foreach (var segment in read.Buffer)
                pending.AddRange(segment.ToArray());
            reader.AdvanceTo(read.Buffer.End);
            foreach (var frame in GatewayEnvelope.TakeFrames(pending))
            {
                var request = GatewayEnvelope.DecodeRequest(frame);
                subscriber.Character = request.CharacterId is { } character && subscriber.Account is { } account
                    && await authority.OwnsAsync(account, character, context.RequestAborted)
                    ? character
                    : null;
            }
            if (read.IsCompleted)
                break;
        }
    }

    private static async Task<byte[]> Answer(byte[] frame, PublicGatewayRoutes routes, long? account, CancellationToken cancellationToken)
    {
        GatewayRequest request;
        try
        {
            request = GatewayEnvelope.DecodeRequest(frame);
        }
        catch (FormatException error)
        {
            Console.Error.WriteLine($"eve-host: public gateway request frame ({frame.Length} bytes) undecodable: {error.Message}: {Convert.ToHexString(frame, 0, Math.Min(frame.Length, 96))}");
            throw;
        }
        GatewayAnswer answer;
        if (!routes.Handlers.TryGetValue(request.TypeName, out var handler))
        {
            answer = new GatewayAnswer(404, $"no public gateway answer for {request.TypeName}", "", []);
        }
        else if (await routes.Authority.RefusalAsync(account, request.CharacterId, cancellationToken) is { } refusal)
        {
            answer = refusal;
        }
        else
        {
            try
            {
                answer = await handler(request, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // the envelope still answers (status 500, not the stream reset): the cause goes to the host's log
                Console.Error.WriteLine($"eve-host: public gateway {request.TypeName} failed: {error}");
                answer = new GatewayAnswer(500, "internal error", "", []);
            }
        }
        return GatewayEnvelope.EncodeResponse(request.CorrelationUuid, answer.StatusCode, answer.StatusMessage, answer.ResponseTypeName, answer.Payload, DateTimeOffset.UtcNow);
    }

    private static async Task StartGrpc(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = GrpcContentType;
        context.Response.Headers["grpc-encoding"] = "identity";
        context.Response.Headers["grpc-accept-encoding"] = "identity";
        await context.Response.StartAsync(context.RequestAborted);
    }

    private static async Task FinishGrpc(HttpContext context, int status)
    {
        context.Response.AppendTrailer("grpc-status", status.ToString());
        await context.Response.CompleteAsync();
    }
}
