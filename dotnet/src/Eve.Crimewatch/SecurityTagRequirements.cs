namespace Eve.Crimewatch;

public sealed record SecurityTagRequirement(int TypeId, int Quantity);

/// <summary>Matches the client tag tiers and 0.5 security-status gain per tag.</summary>
public static class SecurityTagRequirements
{
    public const double GainPerTag = 0.5;

    private static readonly (int TypeId, double Min, double Max)[] Tiers =
    [
        (33138, -10, -8),
        (33139, -8, -5),
        (33140, -5, -2),
        (33141, -2, 0),
    ];

    public static IReadOnlyList<SecurityTagRequirement> Calculate(double currentStatus, double requestedStatus)
    {
        if (!double.IsFinite(currentStatus) || !double.IsFinite(requestedStatus) ||
            requestedStatus <= currentStatus || requestedStatus > 0)
            throw new ArgumentOutOfRangeException(nameof(requestedStatus));

        var requirements = new List<SecurityTagRequirement>();
        var status = currentStatus;
        while (status < requestedStatus)
        {
            var tier = Tiers.FirstOrDefault(tier => tier.Min <= status && status < tier.Max);
            if (tier.TypeId == 0)
                throw new ArgumentOutOfRangeException(nameof(currentStatus));

            var count = (int)Math.Ceiling((Math.Min(requestedStatus, tier.Max) - status) / GainPerTag);
            if (count <= 0)
                throw new InvalidOperationException("Security tag calculation did not advance the status.");
            requirements.Add(new SecurityTagRequirement(tier.TypeId, count));
            status += count * GainPerTag;
        }

        return requirements;
    }
}
