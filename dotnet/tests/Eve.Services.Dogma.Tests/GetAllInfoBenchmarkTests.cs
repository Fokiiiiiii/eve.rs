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
        PyValue reply = PyNone.Instance;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < MeasuredCalls; i++)
        {
            var clock = Stopwatch.StartNew();
            reply = await rig.CallAsync(session, "GetAllInfo", bound, args);
            Assert.IsType<PyObject>(reply);
            session.RunAfterResponse();
            clock.Stop();
            latencies[i] = clock.Elapsed.TotalMilliseconds;
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(latencies);

        const int marshalWarmupCalls = 20;
        const int marshalMeasuredCalls = 1_000;
        for (var i = 0; i < marshalWarmupCalls; i++)
            _ = MarshalCodec.Encode(reply);

        var encodedReply = MarshalCodec.Encode(reply);
        var encodeElapsed = Stopwatch.StartNew();
        var encodeAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < marshalMeasuredCalls; i++)
            _ = MarshalCodec.Encode(reply);
        var encodeAllocated = GC.GetAllocatedBytesForCurrentThread() - encodeAllocatedBefore;
        encodeElapsed.Stop();

        for (var i = 0; i < marshalWarmupCalls; i++)
            _ = MarshalCodec.Decode(encodedReply);

        var decodeElapsed = Stopwatch.StartNew();
        var decodeAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < marshalMeasuredCalls; i++)
            _ = MarshalCodec.Decode(encodedReply);
        var decodeAllocated = GC.GetAllocatedBytesForCurrentThread() - decodeAllocatedBefore;
        decodeElapsed.Stop();

        var snapshot = await rig.Ships.LoadAsync(character.CharacterId) ?? throw new InvalidOperationException("character ship snapshot is missing");
        var info = new DogmaInfo(rig.Statics);
        var duplicateLatencies = new double[MeasuredCalls];
        var duplicateAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < MeasuredCalls; i++)
        {
            var clock = Stopwatch.StartNew();
            _ = info.BuildBrain(snapshot);
            clock.Stop();
            duplicateLatencies[i] = clock.Elapsed.TotalMilliseconds;
        }
        var duplicateAllocated = GC.GetTotalAllocatedBytes(precise: true) - duplicateAllocatedBefore;
        Array.Sort(duplicateLatencies);
        output.WriteLine($"GetAllInfo docked fitted ship calls={MeasuredCalls} p50_ms={latencies[MeasuredCalls / 2]:F3} p95_ms={latencies[(int)(MeasuredCalls * 0.95) - 1]:F3} allocated_bytes_per_call={allocated / MeasuredCalls}");
        output.WriteLine($"Marshal encode GetAllInfo reply calls={marshalMeasuredCalls} payload_bytes={encodedReply.Length} ns_per_call={encodeElapsed.Elapsed.TotalNanoseconds / marshalMeasuredCalls:F0} allocated_bytes_per_call={encodeAllocated / marshalMeasuredCalls}");
        output.WriteLine($"Marshal decode GetAllInfo reply calls={marshalMeasuredCalls} payload_bytes={encodedReply.Length} ns_per_call={decodeElapsed.Elapsed.TotalNanoseconds / marshalMeasuredCalls:F0} allocated_bytes_per_call={decodeAllocated / marshalMeasuredCalls}");
        output.WriteLine($"Prior duplicate brain construction p50_ms={duplicateLatencies[MeasuredCalls / 2]:F3} p95_ms={duplicateLatencies[(int)(MeasuredCalls * 0.95) - 1]:F3} allocated_bytes_per_call={duplicateAllocated / MeasuredCalls}");
        output.WriteLine($"Same-run allocation comparison optimized_bytes_per_call={allocated / MeasuredCalls} same_run_prior_behavior_estimate_bytes_per_call={(allocated + duplicateAllocated) / MeasuredCalls}");
    }
}
