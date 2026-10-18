using Eve.Host;
using Eve.Host.PublicGateway;

using var sentry = HostLogging.StartSentry();
var settings = HostConfiguration.Read();
var runtime = await HostComposition.BuildAsync(settings);
using var listener = HostListener.Listen(settings.Endpoint);
await using var gateway = HostConfiguration.PublicGatewayListen() is { } gatewayListen
    ? await PublicGatewayServer.StartAsync(gatewayListen.Address, gatewayListen.Port, runtime.PublicGateway)
    : null;
if (gateway is not null)
    Console.Error.WriteLine($"eve-host: public gateway listening on port {gateway.Port} (HTTP/2, cleartext)");
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancel.Cancel();
};
Console.Error.WriteLine($"eve-host: listening on {settings.Endpoint}; {runtime.Registry.Count} services");
await runtime.Server.ServeAsync(listener, cancel.Token);
await runtime.ShutdownAsync();
return 0;
