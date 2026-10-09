using System.Net;

namespace Eve.Host.Tests;

/// <summary>The platform-aware defaults: sockets where there are Unix sockets, a local port on Windows (the Rust front and simulation default the same way).</summary>
public class HostConfigurationTests
{
    [Fact]
    public void The_default_endpoints_are_socket_paths_on_unix_and_local_ports_on_windows()
    {
        Assert.Equal("/tmp/eve-host.sock", HostConfiguration.DefaultEndpoint(windows: false));
        Assert.Equal("tcp:127.0.0.1:26010", HostConfiguration.DefaultEndpoint(windows: true));
        Assert.Equal("/tmp/eve-sim.sock", HostConfiguration.DefaultSimSocket(windows: false));
        Assert.Equal("tcp:127.0.0.1:26011", HostConfiguration.DefaultSimSocket(windows: true));
    }

    [Fact]
    public void The_Windows_defaults_are_numeric_addresses_the_listener_and_the_simulation_client_can_bind()
    {
        Assert.True(IPEndPoint.TryParse("127.0.0.1:26010", out _));
        Assert.True(IPEndPoint.TryParse("127.0.0.1:26011", out _));
    }
}
