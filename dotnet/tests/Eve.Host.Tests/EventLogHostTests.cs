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
                    new PyList([new PyList([new PyStr("login")])]),
                    new PyList([new PyStr("client-crash")]),
                };
                Assert.IsType<PyNone>(await registered.CallAsync(new ServiceCall
                {
                    Method = "LogClientStats",
                    Args = arguments,
                    Session = new ClientSession { SessionId = 42 },
                }));

                var records = new List<string>();
                var service = new EventLogRpcService(records.Add);
                var response = await service.LogClientStats(new ServiceCall
                {
                    Method = "LogClientStats",
                    Args = arguments,
                    Session = new ClientSession { SessionId = 42 },
                });

                Assert.IsType<PyNone>(response);
                Assert.Equal(["LogClientStats location=30100041 rows=1"], records);

                var refused = new EventLogRpcService(records.Add);
                var wrongSession = await Assert.ThrowsAsync<MachoWrappedException>(() => refused.LogClientStats(new ServiceCall
                {
                    Method = "LogClientStats",
                    Args = [new PyLong(99), new PyInt(30_100_041), new PyList([])],
                    Session = new ClientSession { SessionId = 42 },
                }).AsTask());
                Assert.Equal("CLIENT_STATS_SESSION_INVALID", wrongSession.Error.UserErrorMessage);
                var oversized = await Assert.ThrowsAsync<MachoWrappedException>(() => refused.LogClientStats(new ServiceCall
                {
                    Method = "LogClientStats",
                    Args = [new PyLong(42), new PyInt(30_100_041), new PyList(Enumerable.Repeat<PyValue>(PyNone.Instance, 5001).ToArray())],
                    Session = new ClientSession { SessionId = 42 },
                }).AsTask());
                Assert.Equal("CLIENT_STATS_ROW_LIMIT_EXCEEDED", oversized.Error.UserErrorMessage);
                Assert.Empty(records.Skip(1));
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
