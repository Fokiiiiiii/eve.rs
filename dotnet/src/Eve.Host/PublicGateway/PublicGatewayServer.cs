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

/// <summary>
/// The public protobuf gateway of the V24.01 client, served on an HTTP/2 listener without TLS (h2c; the client reaches it through
/// EVE_PUBLIC_GATEWAY_ADDRESS). The routes are those of EveJS publicGatewayLocal.js: Requests/Send answers one response frame per request
/// frame, in order; the Ping calls answer an empty frame; Events/Publish is acknowledged and not stored (the reference keeps no events either).
/// Notices/Consume is not served yet: it answers the gRPC status UNIMPLEMENTED.
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

    public static async Task<PublicGatewayServer> StartAsync(IPAddress address, int port, IReadOnlyDictionary<string, GatewayHandler> handlers, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port, listen => listen.Protocols = HttpProtocols.Http2));
        var app = builder.Build();
        app.Run(context => Route(context, handlers));
        await app.StartAsync(cancellationToken);
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new PublicGatewayServer(app, new Uri(url).Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static Task Route(HttpContext context, IReadOnlyDictionary<string, GatewayHandler> handlers) => context.Request.Path.Value switch
    {
        "/eve_public.gateway.Requests/Ping" or "/eve_public.gateway.Notices/Ping" or "/eve_public.gateway.Events/Ping" => Guard(context, () => AnswerEmpty(context, false)),
        "/eve_public.gateway.Requests/Send" => Guard(context, () => Send(context, handlers)),
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

    private static async Task Send(HttpContext context, IReadOnlyDictionary<string, GatewayHandler> handlers)
    {
        await StartGrpc(context);
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
                var response = await Answer(frame, handlers, context.RequestAborted);
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

    private static async Task<byte[]> Answer(byte[] frame, IReadOnlyDictionary<string, GatewayHandler> handlers, CancellationToken cancellationToken)
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
        if (!handlers.TryGetValue(request.TypeName, out var handler))
        {
            answer = new GatewayAnswer(404, $"no public gateway answer for {request.TypeName}", "", []);
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
