using System.Globalization;

namespace Eve.Npc;

/// <summary>A kill that earned a bounty: whose it is, where it happened, what was killed and what it was worth.</summary>
public sealed record BountyKill(long CharacterId, int SolarSystemId, int EnemyTypeId, long VictimShipId, decimal Amount, bool IsModified = false);

/// <summary>What a character has earned in a solar system and is paid together when its time comes.</summary>
public sealed record BountyBucket(
    string Key, long CharacterId, int SolarSystemId, long PayoutAtMs, long PayoutTime, decimal Amount, int Kills, IReadOnlyDictionary<int, int> NpcTypes,
    IReadOnlyList<long> SourceReferences);

/// <summary>What a kill came to: the bounty it added and when it will be paid (what <c>OnBountyAddedToPayout</c> tells the client), and whether it was already counted.</summary>
public sealed record BountyAdded(int EnemyTypeId, decimal Amount, long PayoutTime, bool IsModified, bool AlreadyRecorded);

/// <summary>A bucket that was paid: to whom, how much, how many kills it was and what the balance is now.</summary>
public sealed record BountyPaid(long CharacterId, decimal Amount, int Kills, decimal Balance, string Key);

/// <summary>The corporation a bounty's character belongs to and its ISK tax rate (a fraction); the corporation is credited unless it is an NPC corporation.</summary>
public sealed record BountyTaxTerms(long CorporationId, bool IsNpc, decimal TaxRate);

/// <summary>
/// The rules of an NPC bounty, a port, as they are, of <c>server/src/services/bounty/bountyRuntime.js</c> of EVE.js (ADR 0009): a kill is worth the NPC's bounty to
/// whoever dealt the last of its hull, it is paid twenty minutes after the character's first unpaid kill in one credit with a description of what was killed
/// (<c>NBL:</c>), and the client is told at once what was added and when it will be paid.
/// </summary>
public static class NpcBounty
{
    /// <summary>How long after the first unpaid kill a character's bounties are paid (the reference's: twenty minutes).</summary>
    public const long PayoutIntervalMs = 20 * 60 * 1000;

    /// <summary>How many victims a bucket remembers by ship.</summary>
    public const int MaxSourceReferences = 32;

    /// <summary>The journal entry type of the corporation's cut of a bounty (<c>refCorporationTaxNpcBounties</c>).</summary>
    public const int CorporationTaxRefType = 92;

    /// <summary>The smallest payout the corporation tax is taken from (<c>appConst.minCorporationTaxAmount</c>); each payout is measured on its own.</summary>
    public const decimal MinimumCorporationTax = 100_000m;

    /// <summary>The journal entry types of a bounty paid for one kill and for several (<c>NPC_BOUNTY_PRIZE</c>, <c>NPC_BOUNTY_PRIZES</c>).</summary>
    public const int PrizeRefType = 17;

    public const int PrizesRefType = 85;

    private const long FiletimeEpochOffset = 116_444_736_000_000_000;

    /// <summary>A moment as a Windows FILETIME (what the client reads a payout time as).</summary>
    public static long Filetime(long unixMs) => unixMs * 10_000 + FiletimeEpochOffset;

    /// <summary><c>normalizeMoney</c>: to the cent, halves up (<c>Math.round(n * 100) / 100</c>).</summary>
    public static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// What a kill of an NPC worth <paramref name="bounty"/> pays (<c>resolveNpcBountyAmount</c>: the bounty to the cent; the reference's multipliers for the solar
    /// system, for the spawn and for the ESS are all one and nothing is split off), or null when it pays nothing.
    /// </summary>
    public static decimal? AmountOf(decimal bounty)
    {
        var amount = Money(Math.Max(0, Money(bounty)));
        return amount > 0 ? amount : null;
    }

    public static string BucketKey(long characterId, int solarSystemId, long payoutTime) =>
        string.Create(CultureInfo.InvariantCulture, $"{characterId}:{solarSystemId}:{payoutTime}");

    /// <summary>The key a bucket's credit is made under, so that a retry of its payment is a no-op (the wallet journal refuses a second of the same terms).</summary>
    public static string OperationKey(string bucketKey) => "npc-bounty:" + bucketKey;

    /// <summary>The journal entry type a bucket is paid under.</summary>
    public static int RefType(int kills) => kills > 1 ? PrizesRefType : PrizeRefType;

    /// <summary>The corporation's ISK tax of a payout: none below <see cref="MinimumCorporationTax"/>, else the rate of the amount to the cent.</summary>
    public static decimal CorporationTaxOf(decimal amount, decimal taxRate) =>
        amount >= MinimumCorporationTax && taxRate > 0 ? Money(amount * Math.Min(1m, taxRate)) : 0m;

    /// <summary>
    /// The description of a paid bucket with the corporation's tax line appended (<c>buildCorporationTaxDescription</c>): a top-level
    /// <c>92: [corporation, tax]</c> key after the <c>NBL:</c> lines, which the client reads as the tax row of the journal.
    /// </summary>
    public static string DescriptionWithTax(BountyBucket bucket, long corporationId, decimal tax)
    {
        var amount = tax.ToString("0.########", CultureInfo.InvariantCulture);
        return Description(bucket) + "\n" + string.Create(CultureInfo.InvariantCulture, $"{CorporationTaxRefType}: [{corporationId}, {amount}]");
    }

    /// <summary><c>buildWalletDescription</c>: <c>NBL:</c>, the types killed with how many of each (by type id), the solar system and the number of kills.</summary>
    public static string Description(BountyBucket bucket)
    {
        var lines = new List<string> { "NBL:" };
        var types = bucket.NpcTypes.Where(type => type.Key > 0 && type.Value > 0).OrderBy(type => type.Key).ToList();
        if (types.Count > 0)
        {
            lines.AddRange(types.Select(type => string.Create(CultureInfo.InvariantCulture, $"  {type.Key}: {type.Value}")));
        }
        else
        {
            lines[0] = "NBL: {}";
        }
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"solarSystemID: {Math.Max(0, bucket.SolarSystemId)}"));
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"kills: {Math.Max(0, bucket.Kills)}"));
        return string.Join('\n', lines);
    }

    /// <summary><c>addToBucket</c>: the bucket with one more kill (its amount, its count of the type and, while there is room, the victim).</summary>
    public static BountyBucket Add(BountyBucket bucket, BountyKill kill)
    {
        var types = new Dictionary<int, int>(bucket.NpcTypes);
        if (kill.EnemyTypeId > 0)
        {
            types[kill.EnemyTypeId] = types.GetValueOrDefault(kill.EnemyTypeId) + 1;
        }
        var sources = bucket.SourceReferences.ToList();
        if (kill.VictimShipId > 0 && sources.Count < MaxSourceReferences)
        {
            sources.Add(kill.VictimShipId);
        }
        return bucket with { Amount = Money(bucket.Amount + Money(kill.Amount)), Kills = bucket.Kills + 1, NpcTypes = types, SourceReferences = sources };
    }

    /// <summary>A bucket with no kills in it (the first kill of a window makes the real one).</summary>
    public static BountyBucket Empty(long characterId, int solarSystemId, long payoutAtMs)
    {
        var payoutTime = Filetime(payoutAtMs);
        return new BountyBucket(BucketKey(characterId, solarSystemId, payoutTime), characterId, solarSystemId, payoutAtMs, payoutTime, 0, 0, new Dictionary<int, int>(), []);
    }

    /// <summary>
    /// When a new kill of <paramref name="characterId"/> is paid: with the bucket that comes due first of those that are not yet due (the first kill starts the
    /// window and later ones join it), else <see cref="PayoutIntervalMs"/> from now.
    /// </summary>
    public static long PayoutAt(IEnumerable<long> pendingPayoutsAtMs, long nowMs)
    {
        long? soonest = null;
        foreach (var at in pendingPayoutsAtMs)
        {
            if (at > nowMs && (soonest is null || at < soonest))
            {
                soonest = at;
            }
        }
        return soonest ?? nowMs + PayoutIntervalMs;
    }
}
