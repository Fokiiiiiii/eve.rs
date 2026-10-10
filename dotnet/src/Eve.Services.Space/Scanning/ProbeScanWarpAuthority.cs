using System.Runtime.CompilerServices;
using Eve.Services;

namespace Eve.Services.Space.Scanning;

/// <summary>
/// The places a pilot may warp to on a scan result (EVE.js <c>probeScanWarpAuthority.js</c>: <c>recordCompletedScan</c>, <c>resolveCompletedScanWarpTarget</c>): the
/// results at 100 percent of the last completed scan of the pilot's ship, in its system, each at its exact place. A later completed scan replaces the set; a
/// target of an earlier scan, or one at less than 100 percent, is not a place to warp to. Kept in memory per session, as the reference keeps it.
/// </summary>
public sealed class ProbeScanWarpAuthority
{
    private sealed record Completed(int SolarSystemId, IReadOnlyDictionary<long, double[]> Places);

    private readonly ConditionalWeakTable<ClientSession, Completed> _bySession = new();

    /// <summary>Records what a completed scan of the session's ship found at 100 percent (it replaces what was recorded before).</summary>
    public void Record(ClientSession session, int solarSystemId, IEnumerable<ScanResultRow> results)
    {
        var places = new Dictionary<long, double[]>();
        foreach (var result in results.Where(result => result.Perfect))
        {
            places[result.Id] = result.Position;
        }
        _bySession.AddOrUpdate(session, new Completed(solarSystemId, places));
    }

    /// <summary>The exact place of a target the session's last completed scan in <paramref name="solarSystemId"/> found at 100 percent; null when there is none.</summary>
    public double[]? Resolve(ClientSession session, int solarSystemId, long targetId) =>
        _bySession.TryGetValue(session, out var completed) && completed.SolarSystemId == solarSystemId && completed.Places.TryGetValue(targetId, out var place)
            ? place
            : null;
}
