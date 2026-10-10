namespace Eve.Dogma;

/// <summary>One buff of an item as the client is told it: its collection, the value it gives, and the expiry it runs to (the registry's time, in milliseconds).</summary>
public readonly record struct BuffState(int CollectionId, double Value, double ExpiresAtMs);

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

    /// <summary>
    /// The buffs the item carries at <paramref name="nowMs"/>, as the client is told them (EVE.js <c>buildClientDbuffStateEntries</c>): each collection's value and the
    /// latest expiry of its active inputs. A collection with no active input is absent.
    /// </summary>
    public IReadOnlyList<BuffState> StateOf(long itemId, double nowMs) =>
        [.. Current(itemId, nowMs).Select(entry => new BuffState(entry.Collection.Id, entry.Value, entry.Expiry))];

    /// <summary>
    /// The inputs the item carries at <paramref name="nowMs"/>: each active input with its collection, value and expiry. The host keeps them, so a restart can
    /// restore the buffs as they were (<see cref="Restore"/>).
    /// </summary>
    public IReadOnlyList<(int CollectionId, double Value, double ExpiresAtMs)> InputsOf(long itemId, double nowMs)
    {
        if (!_items.TryGetValue(itemId, out var series))
        {
            return [];
        }
        return [.. series.Values.SelectMany(entry => entry.Inputs
            .Where(input => input.Expiry >= nowMs)
            .Select(input => (entry.Collection.Id, input.Value, input.Expiry)))];
    }

    /// <summary>Restores an input the host kept (the inverse of <see cref="InputsOf"/>): a buff of the collection with the value, until its expiry.</summary>
    public void Restore(long itemId, BuffCollection collection, double value, double expiresAtMs)
    {
        if (!double.IsFinite(value) || !double.IsFinite(expiresAtMs))
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
        entry.Inputs.Add((expiresAtMs, value));
    }

    /// <summary>The buff effects the item carries at <paramref name="nowMs"/>, as the dogma world takes them (<see cref="DogmaWorld.SetBuffs"/>).</summary>
    public IReadOnlyList<EffectDef> EffectsOf(long itemId, double nowMs) =>
        [.. Active(itemId, nowMs).Select(pair => pair.Collection.EffectFor(pair.Value))];

    /// <summary>Makes the item's buffs of <paramref name="nowMs"/> the dogma world's: the world then reads the item's attributes with them.</summary>
    public void Sync(DogmaWorld world, long itemId, double nowMs) => world.SetBuffs(itemId, EffectsOf(itemId, nowMs));

    /// <summary>
    /// Drops every input that expired before <paramref name="nowMs"/> (an input at its expiry is still active). Returns the items that lost an input, so the
    /// host can send them their buffs again.
    /// </summary>
    public IReadOnlyList<long> Prune(double nowMs)
    {
        var changed = new List<long>();
        foreach (var (itemId, series) in _items.ToArray())
        {
            foreach (var (collectionId, entry) in series.ToArray())
            {
                if (entry.Inputs.RemoveAll(input => input.Expiry < nowMs) > 0)
                {
                    changed.Add(itemId);
                }
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
        return changed.Distinct().ToList();
    }

    /// <summary>Drops every buff of an item (it left the space it was buffed in).</summary>
    public void Forget(long itemId) => _items.Remove(itemId);

    private IEnumerable<(BuffCollection Collection, double Value)> Active(long itemId, double nowMs) =>
        Current(itemId, nowMs).Select(entry => (entry.Collection, entry.Value));

    /// <summary>Each collection of the item with an active input: its value, and the latest expiry among the active inputs.</summary>
    private IEnumerable<(BuffCollection Collection, double Value, double Expiry)> Current(long itemId, double nowMs)
    {
        if (!_items.TryGetValue(itemId, out var series))
        {
            yield break;
        }
        foreach (var entry in series.Values)
        {
            var active = entry.Inputs.Where(input => input.Expiry >= nowMs).ToArray();
            if (active.Length == 0)
            {
                continue;
            }
            var values = active.Select(input => input.Value).ToArray();
            yield return (entry.Collection, entry.Collection.Weakest ? values.Min() : values.Max(), active.Max(input => input.Expiry));
        }
    }
}
