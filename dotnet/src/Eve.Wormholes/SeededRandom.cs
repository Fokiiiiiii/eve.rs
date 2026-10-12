namespace Eve.Wormholes;

/// <summary>
/// SplitMix64. The same seed gives the same sequence on every runtime, so a world generated from a stored seed can be
/// generated again. Next(count) is the modulo of the 64-bit value: the bias is negligible for the counts used here (hundreds).
/// </summary>
public sealed class SeededRandom
{
    private ulong _state;

    public SeededRandom(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A number in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>An index in [0, count); count must be positive.</summary>
    public int Next(int count) => (int)(NextUInt64() % (ulong)count);
}
