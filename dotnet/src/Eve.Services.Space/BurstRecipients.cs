namespace Eve.Services.Space;

/// <summary>A ship a command burst may reach: its pilot, and its explicit affinity group (0 when it has none).</summary>
public readonly record struct BurstShip(long ShipId, long CharacterId, long AffinityGroupId);

/// <summary>
/// Who a command burst reaches on one cycle (the reference's <c>isBurstRecipient</c> and <c>resolveCommandBurstRecipients</c>, EVE.js
/// <c>commandBurstRuntime.js</c>). The source ship itself always is, when it passes <paramref name="eligible"/>. Any other ship needs a shared explicit
/// affinity group or a shared fleet (two different pilots in one fleet), or, for a burst that reaches every ship in range (a Phenomena Generator), nothing
/// else; and it must be within the range (surface distance, plus one metre, as the reference allows). The caller passes only the ships that can interact
/// ship id, as the reference orders it.
/// </summary>
public static class BurstRecipients
{

    public const int MaxRecipients = 256;

    /// <summary>The ships the burst from <paramref name="source"/> reaches, at <paramref name="rangeMeters"/>.</summary>
    /// <param name="ships">Every ship that can interact locally with the source (the source among them or not).</param>
    /// <param name="surfaceDistance">The surface distance in metres from the source to a ship.</param>
    /// <param name="sameFleet">Whether two pilots (by character id) are in one fleet.</param>
    /// <param name="affectsAllShipsInRange">True for a burst that is not a fleet's (it reaches every ship in range).</param>
    /// <param name="eligible">Whether a ship may receive the burst at all (null: every ship).</param>
    public static IReadOnlyList<long> Resolve(
        BurstShip source,
        IEnumerable<BurstShip> ships,
        double rangeMeters,
        Func<BurstShip, double> surfaceDistance,
        Func<long, long, bool> sameFleet,
        bool affectsAllShipsInRange = false,
        Func<BurstShip, bool>? eligible = null)
    {
        var range = double.IsFinite(rangeMeters) ? Math.Max(0, rangeMeters) : 0;
        var self = new List<long>();
        var others = new List<long>();
        foreach (var ship in ships.OrderBy(ship => ship.ShipId))
        {
            var isSource = ship.ShipId == source.ShipId;
            if (!isSource && !affectsAllShipsInRange && !SharesGroup(source, ship, sameFleet))
            {
                continue;
            }
            if (eligible is not null && !eligible(ship))
            {
                continue;
            }
            if (isSource)
            {
                self.Add(ship.ShipId);
            }
            else if (InRange(surfaceDistance(ship), range))
            {
                others.Add(ship.ShipId);
            }
        }
        var recipients = self.Count > 0 ? self.Concat(others.Take(MaxRecipients - self.Count)) : others.Take(MaxRecipients);
        return recipients.OrderBy(id => id).ToList();
    }

    private static bool SharesGroup(BurstShip source, BurstShip ship, Func<long, long, bool> sameFleet)
    {
        if (source.AffinityGroupId > 0 && source.AffinityGroupId == ship.AffinityGroupId)
        {
            return true;
        }
        return source.CharacterId > 0 && ship.CharacterId > 0 && source.CharacterId != ship.CharacterId && sameFleet(source.CharacterId, ship.CharacterId);
    }

    private static bool InRange(double distance, double range) => double.IsFinite(distance) && distance <= range + 1;
}
