using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Host.Tests;

public sealed class EventLogHostTests
{
    [Fact]
    public async Task The_host_registers_event_log_and_records_client_stats_before_returning_none()
    {
        var names = new[] { "EVE_PG_URL", "EVE_XMPP_ENABLED", "EVE_SIM" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var name in names)
                Environment.SetEnvironmentVariable(name, null);

            var host = await HostComposition.BuildAsync(HostConfiguration.Read());
            try
            {
                var registered = host.Registry.Lookup("eventLog");
                Assert.NotNull(registered);
                Assert.True(registered!.HasMethod("LogClientStats"));
                var arguments = new PyValue[]
                {
                    new PyLong(42), new PyInt(30_100_041),
                    new PyList([new PyList([new PyStr("login")])]), PyBool.False,
                };
                Assert.IsType<PyNone>(await registered.CallAsync(new ServiceCall
                {
                    Method = "LogClientStats",
                    Args = arguments,
                    Session = new ClientSession(),
                }));

                var records = new List<string>();
                var service = new EventLogRpcService(records.Add);
                var response = await service.CallAsync(new ServiceCall
                {
                    Method = "LogClientStats",
                    Args = arguments,
                    Session = new ClientSession(),
                });

                Assert.IsType<PyNone>(response);
                Assert.Equal(["LogClientStats location=30100041 rows=1"], records);
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
