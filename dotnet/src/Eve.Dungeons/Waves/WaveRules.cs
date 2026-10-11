using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eve.Dungeons;

/// <summary>The triggers the wave runtime of A-W1 runs: a wave starts when the content starts, when the wave before it is cleared, or after a delay.</summary>
public enum WaveTrigger
{
    OnLoad,
    WaveCleared,
    Timer,
}

/// <summary>One wave the eve.rs rule file gives a trigger to.</summary>
public sealed record WaveRule(
    [property: JsonPropertyName("waveIndex")] int WaveIndex,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("delaySeconds")] int? DelaySeconds);

/// <summary>The rules of one dungeon: the source they were taken from (a URL, never a copy of third-party text) and the waves they replace the owner data's trigger of.</summary>
public sealed record DungeonWaveRule(
    [property: JsonPropertyName("dungeonID")] long DungeonID,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("waves")] IReadOnlyList<WaveRule> Waves);

public sealed record WaveRuleFile(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("policy")] string Policy,
    [property: JsonPropertyName("dungeons")] IReadOnlyList<DungeonWaveRule> Dungeons);

/// <summary>
/// The eve.rs-owned trigger rules of the dungeon waves, keyed by the official dungeon id (<c>Data/waves/v1/trigger-rules.json</c>, embedded). A rule replaces the trigger the owner
/// data gives one wave, and only that; the owner data stays what it is. Every rule names its source. The file is a data file so that other tracks add their own dungeons.
/// </summary>
public sealed class WaveRules
{
    public const string Schema = "eve.dungeon-waves.rules.v1";

    private readonly Dictionary<long, DungeonWaveRule> _byDungeon;

    private WaveRules(WaveRuleFile file)
    {
        File = file;
        _byDungeon = new Dictionary<long, DungeonWaveRule>();
        foreach (DungeonWaveRule rule in file.Dungeons)
        {
            if (rule.DungeonID <= 0 || !_byDungeon.TryAdd(rule.DungeonID, rule))
            {
                throw new InvalidDataException($"wave rules: dungeon {rule.DungeonID} is not positive or appears twice");
            }
            if (string.IsNullOrWhiteSpace(rule.Source))
            {
                throw new InvalidDataException($"wave rules: dungeon {rule.DungeonID} names no source");
            }
            var indexes = new HashSet<int>();
            foreach (WaveRule wave in rule.Waves)
            {
                if (wave.WaveIndex < 1 || !indexes.Add(wave.WaveIndex))
                {
                    throw new InvalidDataException($"wave rules: dungeon {rule.DungeonID} has wave {wave.WaveIndex} twice or below 1");
                }
                TriggerOf(wave.Trigger, wave.DelaySeconds, rule.DungeonID, wave.WaveIndex);
            }
        }
    }

    public WaveRuleFile File { get; }

    public static WaveRules LoadEmbedded() => Load(name => typeof(WaveRules).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException($"the wave rules resource '{name}' is not embedded"));

    public static WaveRules Load(Func<string, Stream> open)
    {
        using Stream stream = open("trigger-rules.json");
        WaveRuleFile file = JsonSerializer.Deserialize<WaveRuleFile>(stream, new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("the wave rules are empty");
        if (file.Schema != Schema)
        {
            throw new InvalidDataException($"the wave rules have schema '{file.Schema}', expected '{Schema}'");
        }
        return new WaveRules(file);
    }

    /// <summary>The rules of a dungeon, or null when eve.rs has none for it.</summary>
    public DungeonWaveRule? For(long dungeonID) => _byDungeon.GetValueOrDefault(dungeonID);

    /// <summary>Reads a trigger name of a rule or of the owner data (<c>on_load</c>, <c>wave_cleared</c>, <c>timer</c>); anything else is null.</summary>
    public static WaveTrigger? ParseTrigger(string? name) => name switch
    {
        "on_load" => WaveTrigger.OnLoad,
        "wave_cleared" => WaveTrigger.WaveCleared,
        "timer" => WaveTrigger.Timer,
        _ => null,
    };

    private static void TriggerOf(string trigger, int? delaySeconds, long dungeonID, int waveIndex)
    {
        WaveTrigger kind = ParseTrigger(trigger)
            ?? throw new InvalidDataException($"wave rules: dungeon {dungeonID} wave {waveIndex} names trigger '{trigger}', which is not one of on_load, wave_cleared, timer");
        if (kind == WaveTrigger.Timer && delaySeconds is not > 0)
        {
            throw new InvalidDataException($"wave rules: dungeon {dungeonID} wave {waveIndex} is a timer without a positive delaySeconds");
        }
        if (kind != WaveTrigger.Timer && delaySeconds is not null)
        {
            throw new InvalidDataException($"wave rules: dungeon {dungeonID} wave {waveIndex} gives delaySeconds for a {trigger} trigger");
        }
    }
}

/// <summary>A wave as the runtime plays it: its index (1 is the first), its trigger and delay, the NPCs it asks for, and where the trigger came from.</summary>
public sealed record PlannedWave(int WaveIndex, WaveTrigger Trigger, int DelaySeconds, string SpawnQuery, int Amount, string TriggerSource);

public sealed record WavePlan(long DungeonID, IReadOnlyList<PlannedWave> Waves);

/// <summary>The answer of <see cref="WavePlanner.Plan"/>: the plan, or why the dungeon cannot be run by the wave runtime (never a guess).</summary>
public sealed record WavePlanAnswer(WavePlan? Plan, string? Refusal)
{
    public bool Planned => Plan is not null;
}

/// <summary>
/// Turns a dungeon's waves into the sequence the runtime plays. The trigger of each wave is the rule file's, else the owner data's (<c>on_load</c>, <c>wave_cleared</c>, <c>timer</c>
/// with <c>countdownSeconds</c>), else the default policy of <see cref="WaveRules"/>. A dungeon whose wave needs a trigger the runtime does not implement (gate use, a room, a hack,
/// an entity kill, an object, a visible countdown), or a timer with no delay, is refused with the reason. NPC compositions are not resolved here (the runtime does that against the authority).
/// </summary>
public static class WavePlanner
{
    public static WavePlanAnswer Plan(Dungeon dungeon, WaveRules rules)
    {
        if (dungeon.Waves.Count == 0)
        {
            return Refuse(dungeon.DungeonID, "the owner data has no NPC waves for this dungeon");
        }
        var ordered = dungeon.Waves.OrderBy(wave => wave.WaveIndex ?? int.MaxValue).ToList();
        DungeonWaveRule? rule = rules.For(dungeon.DungeonID);
        foreach (WaveRule named in rule?.Waves ?? [])
        {
            if (named.WaveIndex > ordered.Count)
            {
                return Refuse(dungeon.DungeonID, $"the wave rule names wave {named.WaveIndex}, and the owner data has {ordered.Count} waves");
            }
        }
        var planned = new List<PlannedWave>();
        for (var position = 0; position < ordered.Count; position++)
        {
            DungeonWave wave = ordered[position];
            int index = position + 1;
            if (wave.WaveIndex != index)
            {
                return Refuse(dungeon.DungeonID, $"the owner data numbers its waves {wave.WaveIndex} where {index} is due");
            }
            if (string.IsNullOrWhiteSpace(wave.SpawnQuery) || wave.Amount is not > 0)
            {
                return Refuse(dungeon.DungeonID, $"wave {index} has no spawn query or no count");
            }
            WaveRule? override_ = rule?.Waves.FirstOrDefault(named => named.WaveIndex == index);
            WaveTrigger trigger;
            int delay = 0;
            string source;
            if (override_ is not null)
            {
                trigger = WaveRules.ParseTrigger(override_.Trigger)!.Value;
                delay = override_.DelaySeconds ?? 0;
                source = $"rule: {rule!.Source}";
            }
            else if (wave.Trigger is null)
            {
                trigger = index == 1 ? WaveTrigger.OnLoad : WaveTrigger.WaveCleared;
                source = "default policy (no trigger in the owner data)";
            }
            else
            {
                if (WaveRules.ParseTrigger(wave.Trigger) is not { } known)
                {
                    return Refuse(dungeon.DungeonID, $"wave {index} needs the trigger '{wave.Trigger}', which the wave runtime does not implement yet");
                }
                trigger = known;
                if (trigger == WaveTrigger.Timer)
                {
                    if (wave.CountdownSeconds is not > 0)
                    {
                        return Refuse(dungeon.DungeonID, $"wave {index} is a timer and the owner data gives no delay; a rule must give it");
                    }
                    delay = wave.CountdownSeconds.Value;
                }
                source = "owner data";
            }
            if (index == 1 && trigger == WaveTrigger.WaveCleared)
            {
                return Refuse(dungeon.DungeonID, "the first wave waits for a wave before it, and there is none");
            }
            planned.Add(new PlannedWave(index, trigger, delay, wave.SpawnQuery!, wave.Amount!.Value, source));
        }
        return new WavePlanAnswer(new WavePlan(dungeon.DungeonID, planned), null);
    }

    private static WavePlanAnswer Refuse(long dungeonID, string reason) => new(null, $"dungeon {dungeonID}: {reason}");
}
