using System.Runtime.InteropServices;
using Eve.Host;
using Eve.Host.PublicGateway;

using var sentry = HostLogging.StartSentry();
var settings = HostConfiguration.Read();
var runtime = await HostComposition.BuildAsync(settings);
try
{
    using var listener = HostListener.Listen(settings.Endpoint);
    var gatewayListen = HostConfiguration.PublicGatewayListen();
    using var gatewayCertificate = gatewayListen is null ? null : HostConfiguration.PublicGatewayCertificate();
    await using var gateway = gatewayListen is { } address
        ? await PublicGatewayServer.StartAsync(address.Address, address.Port, runtime.PublicGateway, certificate: gatewayCertificate)
        : null;
    if (gateway is not null)
        Console.Error.WriteLine($"eve-host: public gateway listening on port {gateway.Port} ({(gatewayCertificate is null ? "h2c" : "HTTP/2 TLS")})");
    using var cancel = new CancellationTokenSource();
    using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(
        PosixSignal.SIGTERM,
        context =>
        {
            context.Cancel = true;
            cancel.Cancel();
        });
    Console.CancelKeyPress += (_, args) =>
    {
        args.Cancel = true;
        cancel.Cancel();
    };
    Console.Error.WriteLine($"eve-host: listening on {settings.Endpoint}; {runtime.Registry.Count} services");
    await runtime.Server.ServeAsync(listener, cancel.Token);
}
finally
{
    await runtime.ShutdownAsync();
}
return 0;
