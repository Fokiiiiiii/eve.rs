using Eve.Host;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Host.Tests;

public sealed class SearchQueryHostTests
{
    [Fact]
    public async Task Host_registers_search_and_returns_the_client_query_wire_shape()
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
                var search = host.Registry.Lookup("search");
                Assert.NotNull(search);
                Assert.True(search!.HasMethod("Query"));
                var response = Assert.IsType<PyDict>(await search.CallAsync(new ServiceCall
                {
                    Method = "Query",
                    Args = [new PyStr(""), new PyList([new PyInt(2), new PyInt(3), new PyInt(7), new PyInt(9), new PyInt(10)])],
                    Kwargs = new PyDict([
                        new KeyValuePair<PyValue, PyValue>(new PyStr("exact"), new PyInt(0)),
                    ]),
                    Session = new ClientSession { SessionId = 42 },
                }));

                Assert.Equal([2L, 3L, 7L, 9L, 10L], response.Entries.Select(entry => ((PyInt)entry.Key).Value));
                Assert.All(response.Entries, entry => Assert.Empty(Assert.IsType<PyList>(entry.Value).Items));
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
