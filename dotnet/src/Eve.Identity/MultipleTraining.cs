namespace Eve.Identity;

/// <summary>
/// Multiple Character Training: an account has two extra skill-training queues (slots 2 and 3, the numbers the client and EveJS use),
/// so up to three of its characters train at once. Each slot runs until its end time. A certificate or a purchase adds 30 days to one slot:
/// a free slot is opened from now, otherwise the slot that ends first is extended (or the slot the client named is extended).
/// </summary>
public static class MultipleTrainingRules
{
    /// <summary>The first extra queue: the number of the first slot the client knows.</summary>
    public const int FirstSlot = 2;

    /// <summary>The last extra queue: an account has two.</summary>
    public const int LastSlot = 3;

    /// <summary>A certificate (one store unit) adds this many days to one slot.</summary>
    public const int CertificateDays = 30;

    public static bool IsSlot(int slot) => slot is >= FirstSlot and <= LastSlot;

    /// <summary>
    /// Plans one unit of extra training time. <paramref name="stored"/> holds every slot row of the account, including slots whose end
    /// time has passed (those are free). Returns the slot and its new end time.
    /// </summary>
    public static (int Slot, DateTimeOffset EndsAt) Plan(
        IReadOnlyDictionary<int, DateTimeOffset> stored, int? requested, DateTimeOffset now, TimeSpan duration)
    {
        bool Running(int slot) => stored.TryGetValue(slot, out var endsAt) && endsAt > now;

        int slot;
        if (requested is { } named)
        {
            if (!IsSlot(named))
            {
                throw new ArgumentOutOfRangeException(nameof(requested), $"Training slot {named} does not exist; the extra slots are {FirstSlot} and {LastSlot}.");
            }
            slot = named;
        }
        else if (Enumerable.Range(FirstSlot, LastSlot - FirstSlot + 1).FirstOrDefault(candidate => !Running(candidate)) is var free and not 0)
        {
            slot = free;
        }
        else
        {
            slot = Enumerable.Range(FirstSlot, LastSlot - FirstSlot + 1)
                .OrderBy(candidate => stored[candidate])
                .ThenBy(candidate => candidate)
                .First();
        }

        var from = Running(slot) ? stored[slot] : now;
        var endsAt = SubscriptionPolicy.Extend(from, now, duration);
        if (endsAt <= from)
        {
            throw new InvalidOperationException($"Training slot {slot} already runs to the policy horizon.");
        }
        return (slot, endsAt);
    }
}
