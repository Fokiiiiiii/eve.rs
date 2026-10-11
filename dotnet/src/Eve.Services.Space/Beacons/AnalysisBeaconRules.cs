using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eve.Services.Space.Beacons;

/// <summary>One Rogue Drone wave of a beacon: the NPC pool (an exact name of the NPC authority) and how many NPCs it makes.</summary>
public sealed record BeaconWaveRule(string SpawnPool, int Amount);

/// <summary>One reward of a completed scan: an SDE item type and its quantity (owner decision D-C1).</summary>
public sealed record BeaconRewardRule(int TypeId, int Quantity);

/// <summary>The owner-decided figures of one beacon type: its one wave and its rewards.</summary>
public sealed record BeaconTypeRule(int BeaconTypeId, BeaconWaveRule Wave, IReadOnlyList<BeaconRewardRule> Rewards);

/// <summary>
/// The CONCORD Rogue Analysis Beacon (CRAB) rules of eve.rs. CCP's figures are constants: the two beacon types (60244 standard, 92183 Carrier, group 4137), the activation
/// delay of 20 seconds, the one-hour lifetime, the four-minute link, the scan of 10 minutes (16 for the Carrier variant, patch 2026-03-18.1), the 50,000 HP layers (in the
/// SDE, read from the static data), the low and null security space rule, and the linkable ship lists (SDE type lists 300 and 946). The scan tick and the deployment distance
/// are EveJS values. CCP publishes no wave roster, count, cadence or reward quantity: those come from <c>Beacons/Data/analysis-beacon-rules.json</c> (owner decision D-C1), which
/// is embedded and read by <see cref="Load"/>. Its figures are eve.rs rule constants with EveJS as their source; they are not CCP data.
/// </summary>
public sealed class AnalysisBeaconRules
{
    public const string Schema = "eve.rs/analysis-beacon-rules/v1";

    /// <summary>The group of the CRAB types (SDE group 4137 "Mobile Analysis Beacon"); the category of its deployable is 22.</summary>
    public const int Group = 4137;
    public const int DeployableCategory = 22;

    /// <summary>The standard CRAB (SDE 60244).</summary>
    public const int StandardType = 60244;

    /// <summary>The CONCORD-Carrier CRAB (SDE 92183), added by patch 2026-03-18.1.</summary>
    public const int CarrierType = 92183;

    /// <summary>The SDE type list of the ships a standard CRAB links (typeList 300, "LinkWithShip valid ships").</summary>
    public const int StandardLinkTypeList = 300;

    /// <summary>The SDE type list of the ships a Carrier CRAB links (typeList 946, "CONCORD Rogue Analysis Beacon (Carrier) - Allowed Ships").</summary>
    public const int CarrierLinkTypeList = 946;

    /// <summary>The time from deployment to the start of the scan (CCP patch notes 2021-10-19.1: "activation delay 20 seconds").</summary>
    public static readonly TimeSpan ActivationDelay = TimeSpan.FromSeconds(20);

    /// <summary>The maximum lifetime of a beacon (CCP patch notes 2021-10-19.1: "maximum lifetime 1 hour").</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>How long a link lasts (CCP patch notes 2021-10-19.1: the linking ship is held for four minutes).</summary>
    public static readonly TimeSpan LinkDuration = TimeSpan.FromMinutes(4);

    /// <summary>The scan of a standard CRAB (CCP patch notes 2021-10-19.1: "after 10 minutes of uninterrupted scanning").</summary>
    public static readonly TimeSpan StandardScan = TimeSpan.FromMinutes(10);

    /// <summary>The scan of a Carrier CRAB (CCP patch notes 2026-03-18.1: "scan 16 minutes (from 10)").</summary>
    public static readonly TimeSpan CarrierScan = TimeSpan.FromMinutes(16);

    /// <summary>How often the scan is advanced (EveJS <c>MOBILE_ANALYSIS_BEACON_SCAN_TICK_MS</c>; CCP gives no tick).</summary>
    public static readonly TimeSpan ScanTick = TimeSpan.FromSeconds(10);

    /// <summary>How far ahead of the ship a beacon is put (EveJS <c>DEPLOY_DISTANCE_METERS</c>; CCP gives no distance).</summary>
    public const double DeployDistanceMeters = 2_500;

    /// <summary>The security at and above which a beacon cannot be deployed (CCP: "for low and null security space"; EveJS <c>MOBILE_ANALYSIS_BEACON_SECURITY_CUTOFF</c> 0.45).</summary>
    public const double SecurityCutoff = 0.45;

    private AnalysisBeaconRules(IReadOnlyDictionary<int, BeaconTypeRule> types) => Types = types;

    /// <summary>The owner-decided figures of each beacon type, keyed by the type id.</summary>
    public IReadOnlyDictionary<int, BeaconTypeRule> Types { get; }

    /// <summary>Whether the type is a CRAB (standard or Carrier).</summary>
    public static bool IsBeacon(int typeId) => typeId is StandardType or CarrierType;

    /// <summary>The scan of a beacon type: 10 minutes, or 16 for the Carrier.</summary>
    public static TimeSpan ScanOf(int typeId) => typeId == CarrierType ? CarrierScan : StandardScan;

    /// <summary>The SDE type list that names the ships a beacon type links.</summary>
    public static int LinkTypeListOf(int typeId) => typeId == CarrierType ? CarrierLinkTypeList : StandardLinkTypeList;

    /// <summary>The figures of a beacon type, or null when the type is not a CRAB.</summary>
    public BeaconTypeRule? For(int typeId) => Types.GetValueOrDefault(typeId);

    /// <summary>The rules embedded in the assembly (<c>Beacons/Data/analysis-beacon-rules.json</c>).</summary>
    public static AnalysisBeaconRules LoadEmbedded() => Load(name => typeof(AnalysisBeaconRules).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException($"the analysis beacon rules resource '{name}' is not embedded"));

    /// <summary>
    /// Reads the rules: the schema, exactly the two beacon types, one wave each at scan progress 0 (the only trigger the first cut builds), a pool and an amount, and
    /// rewards with a positive quantity. Anything else refuses the file.
    /// </summary>
    public static AnalysisBeaconRules Load(Func<string, Stream> open)
    {
        using Stream stream = open("analysis-beacon-rules.json");
        var file = JsonSerializer.Deserialize<RuleFile>(stream, new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("the analysis beacon rules are empty");
        if (file.Schema != Schema)
        {
            throw new InvalidDataException($"the analysis beacon rules have schema '{file.Schema}', expected '{Schema}'");
        }
        var types = new Dictionary<int, BeaconTypeRule>();
        foreach (var entry in file.Beacons ?? [])
        {
            if (!IsBeacon(entry.TypeId) || types.ContainsKey(entry.TypeId))
            {
                throw new InvalidDataException($"the analysis beacon rules name type {entry.TypeId}, which is not a CRAB type once");
            }
            if (entry.Wave is not { } wave || wave.AtScanProgress != 0)
            {
                throw new InvalidDataException($"beacon {entry.TypeId}: its wave must be the one at scan progress 0 (the other triggers are outside the first cut)");
            }
            if (string.IsNullOrWhiteSpace(wave.SpawnPool) || wave.Amount < 1)
            {
                throw new InvalidDataException($"beacon {entry.TypeId}: the wave needs a spawn pool and an amount of one or more");
            }
            var rewards = (entry.Rewards ?? []).Select(reward => reward.TypeId > 0 && reward.Quantity > 0
                ? new BeaconRewardRule(reward.TypeId, reward.Quantity)
                : throw new InvalidDataException($"beacon {entry.TypeId}: a reward needs a type and a quantity above zero")).ToList();
            types[entry.TypeId] = new BeaconTypeRule(entry.TypeId, new BeaconWaveRule(wave.SpawnPool, wave.Amount), rewards);
        }
        if (!types.ContainsKey(StandardType) || !types.ContainsKey(CarrierType))
        {
            throw new InvalidDataException("the analysis beacon rules must give a wave and rewards for both CRAB types");
        }
        return new AnalysisBeaconRules(types);
    }

    private sealed class RuleFile
    {
        [JsonPropertyName("schema")] public string? Schema { get; init; }
        [JsonPropertyName("owner")] public string? Owner { get; init; }
        [JsonPropertyName("beacons")] public List<BeaconEntry>? Beacons { get; init; }
    }

    private sealed class BeaconEntry
    {
        [JsonPropertyName("typeId")] public int TypeId { get; init; }
        [JsonPropertyName("wave")] public WaveEntry? Wave { get; init; }
        [JsonPropertyName("rewards")] public List<RewardEntry>? Rewards { get; init; }
    }

    private sealed class WaveEntry
    {
        [JsonPropertyName("atScanProgress")] public double AtScanProgress { get; init; }
        [JsonPropertyName("spawnPool")] public string? SpawnPool { get; init; }
        [JsonPropertyName("amount")] public int Amount { get; init; }
    }

    private sealed class RewardEntry
    {
        [JsonPropertyName("typeId")] public int TypeId { get; init; }
        [JsonPropertyName("quantity")] public int Quantity { get; init; }
    }
}
