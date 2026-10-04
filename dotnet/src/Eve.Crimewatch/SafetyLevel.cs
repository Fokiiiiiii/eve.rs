namespace Eve.Crimewatch;

/// <summary>
/// The ship safety level (the client's <c>shipSafetyLevel*</c>): how far the client lets a pilot go before it asks first. The server keeps it per character and tells it
/// with the rest of the crimewatch states. Values outside the three are brought to the nearest (the reference does the same), a value that is no number is full.
/// </summary>
public static class SafetyLevel
{
    public const int None = 0;
    public const int Partial = 1;
    public const int Full = 2;

    public static int Normalize(double? requested) =>
        requested is { } value && double.IsFinite(value) ? (int)Math.Clamp(Math.Truncate(value), None, Full) : Full;
}
