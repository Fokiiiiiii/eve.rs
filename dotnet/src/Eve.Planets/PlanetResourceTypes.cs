namespace Eve.Planets;

/// <summary>
/// Resource type IDs admitted by the V24.01 planet resource lookup. The eight native rows are the client build 3396210
/// <c>globaltypes.fsdbinary</c> distribution: the client admits entries whose quality is greater than zero. The other rows match
/// EveJS v0.12.9's fallback for planet types missing from that native distribution. Native quality values determine availability only;
/// they are not extraction-yield multipliers.
/// </summary>
public static class PlanetResourceTypes
{
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<int>> Native = new Dictionary<int, IReadOnlyList<int>>
    {
        [11] = Array.AsReadOnly(new[] { 2073, 2268, 2287, 2288, 2305 }),
        [12] = Array.AsReadOnly(new[] { 2073, 2268, 2272, 2286, 2310 }),
        [13] = Array.AsReadOnly(new[] { 2267, 2268, 2309, 2310, 2311 }),
        [2014] = Array.AsReadOnly(new[] { 2073, 2268, 2286, 2287, 2288 }),
        [2015] = Array.AsReadOnly(new[] { 2267, 2272, 2306, 2307, 2308 }),
        [2016] = Array.AsReadOnly(new[] { 2073, 2267, 2268, 2270, 2288 }),
        [2017] = Array.AsReadOnly(new[] { 2267, 2268, 2308, 2309, 2310 }),
        [2063] = Array.AsReadOnly(new[] { 2267, 2270, 2272, 2306, 2308 }),
    };

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<int>> EveJsFallback = new Dictionary<int, IReadOnlyList<int>>
    {
        [56018] = Array.AsReadOnly(new[] { 2073, 2267, 2268, 2270, 2288 }),
        [56019] = Array.AsReadOnly(new[] { 2073, 2268, 2272, 2286, 2310 }),
        [56020] = Array.AsReadOnly(new[] { 2267, 2272, 2306, 2307, 2308 }),
        [56021] = Array.AsReadOnly(new[] { 2073, 2268, 2286, 2287, 2288 }),
        [56022] = Array.AsReadOnly(new[] { 2267, 2270, 2272, 2306, 2308 }),
        [56023] = Array.AsReadOnly(new[] { 2073, 2268, 2287, 2288, 2305 }),
        [56024] = Array.AsReadOnly(new[] { 2267, 2268, 2308, 2309, 2310 }),
        [73911] = Array.AsReadOnly(new[] { 2073, 2267, 2268, 2270, 2288 }),
    };

    public static IReadOnlyList<int> ForPlanetType(int planetTypeId) =>
        Native.TryGetValue(planetTypeId, out var native) ? native : EveJsFallback.GetValueOrDefault(planetTypeId, []);
}
