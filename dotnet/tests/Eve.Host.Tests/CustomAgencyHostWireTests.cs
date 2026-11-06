using System.Net;
using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class CustomAgencyHostWireTests
{
    private sealed class OmegaStore : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }

    private sealed class RegistryInfo(ServiceRegistry registry, PyDict config) : IMachoNetInfo
    {
        public IReadOnlyList<KeyValuePair<string, string?>> Services() =>
            [.. registry.ServiceNames.Select(name => new KeyValuePair<string, string?>(name, ServiceLocations.Of(name)))];

        public PyDict GlobalConfig() => config;
        public int UserCount() => 0;
        public PyValue ClusterGameStatisticsForClient() => Py.Tuple(new PyDict([]), new PyDict([]), new PyInt(1));
    }

    [Fact]
    public async Task Warp_to_content_reaches_the_composed_custom_agency_callback_over_host_wire()
    {
        const long contentId = 7_001_234;
        long receivedContentId = 0;
        var callbacks = new CustomAgencyProviderRpcCallbacks(
            WarpToContentAsync: (id, _, _) =>
            {
                receivedContentId = id;
                return ValueTask.CompletedTask;
            });
        Func<long> now = () => 1_790_646_377_000;
        var registry = new ServiceRegistry();
        foreach (var service in CoreServices.Create(new OmegaStore(), now,
                     customAgencyProviderRpcCallbacks: callbacks))
            registry.Register(service);

        var config = GlobalConfig.Build(new ClusterOptions());
        registry.Register(new MachoNetService(new RegistryInfo(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now);
        using var cancel = new CancellationTokenSource();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(16);
        var server = new HostServer(
            new HostOptions(),
            dispatcher,
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => MarshalCodec.Encode(config));
        var serving = server.ServeAsync(listener, cancel.Token);
        using var front = await TestFront.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        try
        {
            await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
            Assert.IsType<HelloAck>(await front.ReceiveAsync());
            Assert.IsType<ClusterConfig>(await front.ReceiveAsync());

            var open = new SessionOpen(1_790_646_377_000UL * 15, (42UL * 1_000_000) + 0xFFAA, 42, 0,
                "127.0.0.1", "EN", "GB");
            await front.SendAsync(open);
            await front.ReceiveAsync();

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, 1, "custom_agency_provider", "warp_to_content", new PyLong(contentId)))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            var state = ((PyTuple)((PyObject)MarshalCodec.Decode(response.Payload)).Args).Items;
            Assert.Equal(7, ((PyInt)state[0]).Value);
            var payload = (PyTuple)state[4];
            var result = payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];

            Assert.Same(PyNone.Instance, result);
            Assert.Equal(contentId, receivedContentId);
        }
        finally
        {
            front.Dispose();
            await cancel.CancelAsync();
            listener.Dispose();
            await serving;
        }
    }
}
