using System.Diagnostics;
using Eve.Marshal;
using Xunit;
using Xunit.Abstractions;

namespace Eve.Services.Dogma.Tests;

public sealed class GetAllInfoBenchmarkFactAttribute : FactAttribute
{
    public GetAllInfoBenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set";
        else if (Environment.GetEnvironmentVariable("EVE_DOGMA_BENCH") != "1") Skip = "opt-in benchmark: set EVE_DOGMA_BENCH=1";
    }
}

public sealed class GetAllInfoBenchmarkTests(ITestOutputHelper output)
{
    private const int WarmupCalls = 10;
    private const int MeasuredCalls = 100;

    [GetAllInfoBenchmarkFact]
    public async Task Measure_docked_GetAllInfo_with_a_fitted_rookie_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = Rig.Session(character.CharacterId);
        var bound = await rig.BindAsync(session);
        var args = new PyValue[] { PyBool.True, PyBool.True, PyNone.Instance };
        for (var i = 0; i < WarmupCalls; i++)
            _ = await rig.CallAsync(session, "GetAllInfo", bound, args);

        var latencies = new double[MeasuredCalls];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < MeasuredCalls; i++)
        {
            var clock = Stopwatch.StartNew();
            var result = await rig.CallAsync(session, "GetAllInfo", bound, args);
            clock.Stop();
            Assert.IsType<PyObject>(result);
            latencies[i] = clock.Elapsed.TotalMilliseconds;
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(latencies);
        output.WriteLine($"GetAllInfo docked fitted ship calls={MeasuredCalls} p50_ms={latencies[MeasuredCalls / 2]:F3} p95_ms={latencies[(int)(MeasuredCalls * 0.95) - 1]:F3} allocated_bytes_per_call={allocated / MeasuredCalls}");
    }
}
