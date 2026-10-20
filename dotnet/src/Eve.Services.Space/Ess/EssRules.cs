using System.Globalization;
using System.Text.Json;

namespace Eve.Services.Space;

/// <summary>
/// The rules of the Encounter Surveillance System (ESS): the Main Bank that takes a share of the NPC bounties of its system, the Reserve Bank that a
/// key opens for a number of pulses, and what the client is told of them. Every number says where it comes from; a number with no source is not here.
/// </summary>
public static class EssRules
{
    /// <summary>The type of the ESS in space (the SDE's Encounter Surveillance System).</summary>
    public const int TypeEncounterSurveillanceSystem = 55914;

    /// <summary>The SDE type list of the ships that may link (<c>ESSLinkableShips</c>, list 231).</summary>
    public const int LinkableShipsTypeList = 231;

    public const double MaxLinkDistanceMeters = 10_000;

    public const decimal MainBankShare = 0.40m;

    /// <summary>
    /// The share of a bounty earned in an ESS system that goes into the Reserve Bank. CCP's notes say "a smaller portion" without a number; a community
    /// </summary>
    public const decimal ReserveBankShare = 0.05m;

    public const decimal ThiefShare = 0.80m;

    /// <summary>
    /// </summary>
    public const long AutoPayIntervalMs = 9_900_000;

    public const long LinkDurationMs = 360_000;

    /// <summary>The time between two pulses of a Reserve Bank (CCP 19.06: a payout every minute).</summary>
    public const long PulseIntervalMs = 60_000;

    public const double EquilibriumOutput = 1.5;

    public const double ReserveWindowAccessFactor = 1.5;

    public const double ReservePayoutExponentLead = 2;
    public const double ReservePayoutExponentTail = 2;

    /// <summary>
    /// The largest payout of one Reserve Bank pulse: 150 million ISK per minute, the peak that CCP's 2021 ESS reserve bank article gives (quoted by
    /// </summary>
    public const double ReserveBankMaxPayout = 150_000_000;

    public const long WeaponsTimerMs = 60_000;

    /// <summary>
    /// Where an ESS ball is put in its system: the system's origin. The operator places systems, not positions, so the position is fixed by the rule and is not
    /// a per-system value.
    /// </summary>
    public const double BallX = 0;
    public const double BallY = 0;
    public const double BallZ = 0;

    public const double ReservePayoutPaddingValue = 0;

    /// <summary>The journal reference of ESS money (the client's <c>refESSMainBankAutopayment</c>, the only ESS reference the client names).</summary>
    public const int EssRefType = 155;

    /// <summary>Why a link ends (the client's <c>UNLINKED_MANUAL</c>, <c>UNLINKED_PROXIMITY</c>, <c>UNLINKED_SHIP_CHANGE</c>).</summary>
    public const int UnlinkedManual = 0;
    public const int UnlinkedProximity = 1;

    /// <summary>The link ends because the pilot changed ship (the client's <c>UNLINKED_SHIP_CHANGE</c>).</summary>
    public const int UnlinkedShipChange = 2;

    public const int AutopayStateLong = 0;
    public const int AutopayStateMedium = 1;
    public const int AutopayStateShort = 2;

    public const int ReserveWindowInactive = 0;
    public const int ReserveWindowEarly = 1;
    public const int ReserveWindowBest = 2;
    public const int ReserveWindowLate = 3;

    /// <summary><c>normalizeMoney</c>: to the cent, halves up, never negative.</summary>
    public static decimal Money(decimal value) => decimal.Round(Math.Max(0, value), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The split of a bounty earned in a system: the pilot keeps the rest, the Main Bank takes <see cref="MainBankShare"/> and the Reserve Bank
    /// <see cref="ReserveBankShare"/> when the system has an ESS online; with no ESS the pilot keeps all of it.
    /// </summary>
    public static (decimal Pilot, decimal MainBank, decimal ReserveBank) SplitBounty(decimal amount, bool essOnline)
    {
        var total = Money(amount);
        if (!essOnline)
        {
            return (total, 0m, 0m);
        }
        var mainBank = Money(total * MainBankShare);
        var reserveBank = Money(total * ReserveBankShare);
        return (total - mainBank - reserveBank, mainBank, reserveBank);
    }

    /// <summary>What a thief takes from a Main Bank of this value.</summary>
    public static decimal ThiefPayout(decimal mainValue) => Money(mainValue * ThiefShare);

    /// <summary>
    /// The payout of one Reserve Bank pulse (the client's <c>formulas.GetPayout</c>): it rises to the window's edge and falls after it. Pulses are numbered from 1
    /// to <paramref name="totalPulses"/>.
    /// </summary>
    public static double ReservePulsePayout(int pulse, int totalPulses, double maxPayout = ReserveBankMaxPayout)
    {
        if (pulse < 1 || pulse > totalPulses)
        {
            throw new ArgumentOutOfRangeException(nameof(pulse), "the reserve bank is closed after its last pulse");
        }
        var windowSize = totalPulses / ReserveWindowAccessFactor;
        var payout = pulse < windowSize
            ? maxPayout * Math.Pow(pulse / windowSize, ReservePayoutExponentLead)
            : maxPayout * Math.Pow(windowSize / pulse, ReservePayoutExponentTail);
        return payout + ReservePayoutPaddingValue;
    }

    /// <summary><c>deriveAutopaymentWindowState</c>: long while the bank is empty, short while a thief is at it, medium otherwise.</summary>
    public static int AutopayWindowState(decimal mainValue, bool linked) =>
        mainValue <= 0 ? AutopayStateLong : linked ? AutopayStateShort : AutopayStateMedium;

    /// <summary>
    /// <c>deriveReserveWindowState</c>: early before the window, best on its middle pulse, late after it; inactive when the bank has no pulses left.
    /// <paramref name="pulsesRemaining"/> and <paramref name="pulsesTotal"/> are the bank's own counts.
    /// </summary>
    public static int ReserveWindowState(int pulsesRemaining, int pulsesTotal)
    {
        if (pulsesRemaining <= 0 || pulsesTotal <= 0)
        {
            return ReserveWindowInactive;
        }
        var pulse = pulsesTotal - pulsesRemaining + 1;
        var windowSize = pulsesTotal / ReserveWindowAccessFactor;
        if (pulse < windowSize)
        {
            return ReserveWindowEarly;
        }
        return pulse == (int)Math.Round(windowSize, MidpointRounding.AwayFromZero) ? ReserveWindowBest : ReserveWindowLate;
    }
}

/// <summary>The Reserve Security Keys of the SDE: each opens a Reserve Bank for <see cref="TotalPulses"/> pulses in its <see cref="Quadrant"/>.</summary>
public sealed record EssReserveKey(long KeyTypeId, string Quadrant, int TotalPulses, long LocationListId);

/// <summary>The SDE's eight ESS Reserve Security Keys, as <c>tools/dev/extract-ess-reserve-keys.py</c> wrote them into the assembly.</summary>
public static class EssReserveKeys
{
    private static readonly Lazy<IReadOnlyList<EssReserveKey>> Embedded = new(Load);

    public static IReadOnlyList<EssReserveKey> All => Embedded.Value;

    /// <summary>The pulses a key opens its bank for, or false when the type is not an ESS reserve key.</summary>
    public static bool TryGetPulses(long keyTypeId, out int pulses)
    {
        foreach (var key in All)
        {
            if (key.KeyTypeId == keyTypeId)
            {
                pulses = key.TotalPulses;
                return true;
            }
        }
        pulses = 0;
        return false;
    }

    private static IReadOnlyList<EssReserveKey> Load()
    {
        using var stream = typeof(EssReserveKeys).Assembly.GetManifestResourceStream("ess-reserve-keys.json")
            ?? throw new InvalidOperationException("ess-reserve-keys.json is not in the assembly");
        using var document = JsonDocument.Parse(stream);
        return [.. document.RootElement.GetProperty("keys").EnumerateArray()
            .Select(key => new EssReserveKey(
                key.GetProperty("keyTypeId").GetInt64(),
                key.GetProperty("quadrant").GetString() ?? throw new InvalidOperationException("an ESS key has no quadrant"),
                key.GetProperty("totalPulses").GetInt32(),
                key.GetProperty("locationListId").GetInt64()))
            .OrderBy(key => key.KeyTypeId)];
    }

    /// <summary>The key record of a type, or null when the type is not an ESS reserve key.</summary>
    public static EssReserveKey? Find(long keyTypeId)
    {
        foreach (var key in All)
        {
            if (key.KeyTypeId == keyTypeId)
            {
                return key;
            }
        }
        return null;
    }
}

/// <summary>
/// The quadrant of each system a reserve key works in (CCP's ESS reserve key article: the systems of SW, SE, NE and NW). A key opens a bank only in a system
/// of its own quadrant.
/// </summary>
public static class EssQuadrants
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> Embedded = new(Load);

    /// <summary>The system names of each quadrant, as the data file has them.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByName => Embedded.Value;

    /// <summary>The quadrant of each solar system id, given the names of the systems (the SDE's names; a name that is in no quadrant is in none).</summary>
    public static Dictionary<int, string> BySystemId(IReadOnlyDictionary<int, string> namesById)
    {
        var quadrantByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (quadrant, names) in ByName)
        {
            foreach (var name in names)
            {
                quadrantByName[name] = quadrant;
            }
        }
        var result = new Dictionary<int, string>();
        foreach (var (systemId, name) in namesById)
        {
            if (quadrantByName.TryGetValue(name, out var quadrant))
            {
                result[systemId] = quadrant;
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Load()
    {
        using var stream = typeof(EssQuadrants).Assembly.GetManifestResourceStream("ess-quadrants.json")
            ?? throw new InvalidOperationException("ess-quadrants.json is not in the assembly");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("quadrants").EnumerateObject()
            .ToDictionary(
                quadrant => quadrant.Name,
                quadrant => (IReadOnlyList<string>)[.. quadrant.Value.EnumerateArray().Select(name => name.GetString()!)],
                StringComparer.Ordinal);
    }
}
