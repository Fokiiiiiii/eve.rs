namespace Eve.Dogma;

/// <summary>
/// The timed buffs of the items a buff collection reaches (a command burst's, the P.A.N.I.C. fleet effect's). Each applied buff is an input of its
/// collection that lasts until its expiry (applied time plus duration). A collection's value on an item is the strongest of its active inputs (a Maximum
/// collection) or the weakest (a Minimum one), so two bursts of one kind do not stack. The rules are EVE.js <c>commandBurstRuntime.js</c>
/// (<c>applyTimedCommandBurstToEntity</c> and the aggregators): an input is active up to and including its expiry, and a refresh is a new input.
/// In memory only: a restart drops every buff, as the reference's buff history is in memory.
/// Not thread-safe: one registry belongs to the host that runs the bursts.
/// </summary>
public sealed class BuffRegistry
{
    private readonly Dictionary<long, Dictionary<int, Series>> _items = [];

    private sealed record Series(BuffCollection Collection, List<(double Expiry, double Value)> Inputs);

    /// <summary>
    /// Applies a buff of <paramref name="collection"/> with <paramref name="value"/> to an item from <paramref name="nowMs"/> for
    /// <paramref name="durationMs"/>. A value that is not finite, or a duration that is not positive, applies nothing.
    /// </summary>
    public void Apply(long itemId, BuffCollection collection, double value, double nowMs, double durationMs)
    {
        if (!double.IsFinite(value) || !double.IsFinite(nowMs) || !double.IsFinite(durationMs) || durationMs <= 0)
        {
            return;
        }
        if (!_items.TryGetValue(itemId, out var series))
        {
            _items[itemId] = series = [];
        }
        if (!series.TryGetValue(collection.Id, out var entry))
        {
            series[collection.Id] = entry = new Series(collection, []);
        }
        entry.Inputs.Add((nowMs + durationMs, value));
    }

    /// <summary>The value of each collection on the item at <paramref name="nowMs"/>: the strongest or weakest of its active inputs. A collection with none is absent.</summary>
    public IReadOnlyDictionary<int, double> ValuesOf(long itemId, double nowMs) =>
        Active(itemId, nowMs).ToDictionary(pair => pair.Collection.Id, pair => pair.Value);

    /// <summary>The buff effects the item carries at <paramref name="nowMs"/>, as the dogma world takes them (<see cref="DogmaWorld.SetBuffs"/>).</summary>
    public IReadOnlyList<EffectDef> EffectsOf(long itemId, double nowMs) =>
        [.. Active(itemId, nowMs).Select(pair => pair.Collection.EffectFor(pair.Value))];

    /// <summary>Makes the item's buffs of <paramref name="nowMs"/> the dogma world's: the world then reads the item's attributes with them.</summary>
    public void Sync(DogmaWorld world, long itemId, double nowMs) => world.SetBuffs(itemId, EffectsOf(itemId, nowMs));

    /// <summary>Drops every input that expired before <paramref name="nowMs"/> (an input at its expiry is still active).</summary>
    public void Prune(double nowMs)
    {
        foreach (var (itemId, series) in _items.ToArray())
        {
            foreach (var (collectionId, entry) in series.ToArray())
            {
                entry.Inputs.RemoveAll(input => input.Expiry < nowMs);
                if (entry.Inputs.Count == 0)
                {
                    series.Remove(collectionId);
                }
            }
            if (series.Count == 0)
            {
                _items.Remove(itemId);
            }
        }
    }

    /// <summary>Drops every buff of an item (it left the space it was buffed in).</summary>
    public void Forget(long itemId) => _items.Remove(itemId);

    private IEnumerable<(BuffCollection Collection, double Value)> Active(long itemId, double nowMs)
    {
        if (!_items.TryGetValue(itemId, out var series))
        {
            yield break;
        }
        foreach (var entry in series.Values)
        {
            var values = entry.Inputs.Where(input => input.Expiry >= nowMs).Select(input => input.Value).ToArray();
            if (values.Length == 0)
            {
                continue;
            }
            yield return (entry.Collection, entry.Collection.Weakest ? values.Min() : values.Max());
        }
    }
}
