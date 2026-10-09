using Eve.Host;

using var sentry = HostLogging.StartSentry();
var settings = HostConfiguration.Read();
var runtime = await HostComposition.BuildAsync(settings);
using var listener = HostListener.Listen(settings.Endpoint);
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
