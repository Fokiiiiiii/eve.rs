namespace Eve.Services.Crimewatch;

public static class SecurityTagRequirements
{
    private const double GainPerTag = 0.5;
    private static readonly (int TypeId, double Min, double Max)[] Bands =
    [
        (33138, -10, -8),
        (33139, -8, -5),
        (33140, -5, -2),
        (33141, -2, 0),
    ];

    public static IReadOnlyDictionary<int, long> Calculate(double currentStatus, double targetStatus)
    {
        if (!double.IsFinite(currentStatus) || currentStatus < -10 || currentStatus > 10)
            throw new ArgumentOutOfRangeException(nameof(currentStatus));
        if (!double.IsFinite(targetStatus) || targetStatus <= -10 || targetStatus > 10)
            throw new ArgumentOutOfRangeException(nameof(targetStatus));
        if (targetStatus <= currentStatus)
            throw new ArgumentOutOfRangeException(nameof(targetStatus));

        var required = new Dictionary<int, long>();
        var status = currentStatus;
        while (status < targetStatus)
        {
            var band = Bands.FirstOrDefault(b => b.Min <= status && status < b.Max);
            if (band.TypeId == 0)
                throw new ArgumentOutOfRangeException(nameof(targetStatus), "The requested status is beyond the security tag bands.");

            var count = checked((long)Math.Ceiling((Math.Min(targetStatus, band.Max) - status) / GainPerTag));
            required[band.TypeId] = checked(required.GetValueOrDefault(band.TypeId) + count);
            status += count * GainPerTag;
        }
        return required;
    }
}
