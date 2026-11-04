using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class XmppHostConfigurationTests
{
    [Fact]
    public void XMPP_listener_and_client_endpoint_are_configured_from_environment()
    {
        var names = new[]
        {
            "EVE_XMPP_ENABLED",
            "EVE_XMPP_BIND_ADDRESS",
            "EVE_XMPP_PORT",
            "EVE_XMPP_DOMAIN",
            "EVE_XMPP_CONNECT_HOST",
            "EVE_XMPP_CERTIFICATE_PATH",
            "EVE_XMPP_CERTIFICATE_PASSWORD",
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            Environment.SetEnvironmentVariable("EVE_XMPP_ENABLED", "true");
            Environment.SetEnvironmentVariable("EVE_XMPP_BIND_ADDRESS", "127.0.0.1");
            Environment.SetEnvironmentVariable("EVE_XMPP_PORT", "26211");
            Environment.SetEnvironmentVariable("EVE_XMPP_DOMAIN", "chat.example");
            Environment.SetEnvironmentVariable("EVE_XMPP_CONNECT_HOST", "192.0.2.10");
            Environment.SetEnvironmentVariable("EVE_XMPP_CERTIFICATE_PATH", null);
            Environment.SetEnvironmentVariable("EVE_XMPP_CERTIFICATE_PASSWORD", null);

            var xmpp = HostConfiguration.Read().Xmpp
                ?? throw new InvalidOperationException("HostConfiguration.Read must provide XMPP defaults.");

            Assert.True(xmpp.Enabled);
            Assert.Equal("127.0.0.1", xmpp.BindAddress);
            Assert.Equal(26211, xmpp.Port);
            Assert.Equal("chat.example", xmpp.Domain);
            Assert.Equal("192.0.2.10", xmpp.ConnectHost);
            Assert.Null(xmpp.CertificatePath);
            Assert.Null(xmpp.CertificatePassword);
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task Xmpp_chat_manager_is_registered_and_returns_the_hostname_when_the_listener_is_disabled()
    {
        var names = new[] { "EVE_PG_URL", "EVE_XMPP_ENABLED", "EVE_XMPP_DOMAIN", "EVE_SIM" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            Environment.SetEnvironmentVariable("EVE_PG_URL", null);
            Environment.SetEnvironmentVariable("EVE_XMPP_ENABLED", "false");
            Environment.SetEnvironmentVariable("EVE_XMPP_DOMAIN", "chat.example");
            Environment.SetEnvironmentVariable("EVE_SIM", "off");

            var host = await HostComposition.BuildAsync(HostConfiguration.Read());
            try
            {
                var chat = host.Registry.Lookup("XmppChatMgr");
                Assert.NotNull(chat);
                Assert.True(chat!.HasMethod("Hostname"));
                var result = await chat.CallAsync(new ServiceCall
                {
                    Method = "Hostname",
                    Args = [],
                    Session = new ClientSession(),
                });
                Assert.Equal(new PyStr("chat.example"), result);
            }
            finally
            {
                await host.ShutdownAsync();
            }
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
