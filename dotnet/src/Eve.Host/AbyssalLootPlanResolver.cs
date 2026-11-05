using System.Security.Cryptography;
using Eve.Content;
using Eve.Inventory;

namespace Eve.Host;

/// <summary>Freezes per-copy Abyssal blueprint run counts before the plan is persisted in ContentRuntime.</summary>
public static class AbyssalLootPlanResolver
{
    public static async Task<AbyssalRunPlan> FreezeBlueprintCopiesAsync(
        AbyssalRunPlan plan,
        ItemTypeCatalog itemTypes,
        int blueprintCategoryId,
        Func<int>? nextRun = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(itemTypes);
        if (blueprintCategoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(blueprintCategoryId));

        nextRun ??= () => RandomNumberGenerator.GetInt32(1, 6);
        var entries = plan.Rooms.SelectMany(room => room.Loot.Concat(room.Artifacts.SelectMany(artifact => artifact.Loot))).ToArray();
        var types = await itemTypes.GetAsync(entries.Select(entry => entry.TypeId), cancellationToken)
            .ConfigureAwait(false);
        var rooms = new AbyssalRoomPlan[plan.Rooms.Count];

        for (var roomIndex = 0; roomIndex < plan.Rooms.Count; roomIndex++)
        {
            var room = plan.Rooms[roomIndex];
            rooms[roomIndex] = room with
            {
                Loot = Freeze(room.Loot),
                Artifacts = room.Artifacts.Select(artifact => artifact with { Loot = Freeze(artifact.Loot) }).ToArray(),
            };
        }
        return plan with { Rooms = Array.AsReadOnly(rooms) };

        IReadOnlyList<AbyssalLootPlan> Freeze(IReadOnlyList<AbyssalLootPlan> allocation)
        {
            var loot = new AbyssalLootPlan[allocation.Count];
            for (var lootIndex = 0; lootIndex < allocation.Count; lootIndex++)
            {
                var entry = allocation[lootIndex];
                if (entry.TypeId <= 0 || entry.Count < 0)
                    throw new InvalidDataException("Abyssal loot plan contains an invalid type or count.");
                if (!types.TryGetValue(entry.TypeId, out var type))
                    throw new InvalidDataException($"Abyssal loot type {entry.TypeId} is absent from the loaded SDE.");

                var isBlueprint = type.CategoryId == blueprintCategoryId;
                if (!isBlueprint)
                {
                    if (entry.IsBlueprintCopy || entry.BlueprintRunsPerCopy is not null)
                        throw new InvalidDataException($"Non-blueprint Abyssal loot type {entry.TypeId} contains blueprint-copy state.");
                    loot[lootIndex] = entry;
                    continue;
                }

                var runs = entry.BlueprintRunsPerCopy;
                if (runs is { } existing &&
                    existing.Count == entry.Count && existing.All(value => value is >= 1 and <= 5))
                {
                    loot[lootIndex] = entry with { IsBlueprintCopy = true, BlueprintRunsPerCopy = Array.AsReadOnly(existing.ToArray()) };
                    continue;
                }

                var generated = new int[entry.Count];
                for (var copy = 0; copy < generated.Length; copy++)
                {
                    var value = nextRun();
                    if (value is < 1 or > 5)
                        throw new InvalidOperationException("The Abyssal blueprint run source must return a value from 1 through 5.");
                    generated[copy] = value;
                }
                loot[lootIndex] = entry with
                {
                    IsBlueprintCopy = true,
                    BlueprintRunsPerCopy = Array.AsReadOnly(generated),
                };
            }
            return Array.AsReadOnly(loot);
        }
    }
}
