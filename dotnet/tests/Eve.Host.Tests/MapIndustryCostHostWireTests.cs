using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Map_industry_cost_wire_reply_preserves_solar_system_dictionary_keys()
    {
        const long solarSystemId = 30_000_142;
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres;Timeout=1;Pooling=false");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs)
        {
            ReadDataHook = call => ValueTask.FromResult<PyValue?>(
                call.Method == "GetIndustryCostModifier"
                    ? new PyDict([new(new PyInt(solarSystemId), new PyReal(0.125))])
                    : null),
        };
        var mapRoute = new IncursionAnomalyService(
            "map",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            fallback: map,
            nowUnixMs: () => FixedNowMs);
        Start(configure: registry => registry.Register(mapRoute), strictRpc: true);
        var (front, open) = await OpenFrontAsync();

        var costs = Assert.IsType<PyDict>(await HostRpcAsync(
            front, open, 1, "map", "GetIndustryCostModifier", new PyInt(1)));

        var cost = Assert.Single(costs.Entries);
        Assert.Equal(new PyInt(solarSystemId), cost.Key);
        Assert.Equal(new PyReal(0.125), cost.Value);
    }
}
