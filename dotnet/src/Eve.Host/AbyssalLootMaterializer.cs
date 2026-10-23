using Eve.Content;
using Eve.Inventory;
using Eve.Sde;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// Materializes an already-persisted room loot plan using PgItemStore. The caller must invoke this in the
/// EventBus delivery transaction so inventory rows commit atomically with acknowledgement of the room-spawn event.
/// </summary>
public sealed class AbyssalLootMaterializer(
    PgItemStore items,
    ItemTypeCatalog itemTypes,
    int blueprintCategoryId)
{
    public static async Task<int> LoadBlueprintCategoryIdAsync(
        SdeReader sde,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sde);
        var rows = await sde.ReadAsync("categories", cancellationToken: cancellationToken).ConfigureAwait(false);
        var matches = rows.Where(row =>
            row.Doc.TryGetProperty("name", out var name) &&
            name.ValueKind == System.Text.Json.JsonValueKind.Object &&
            name.TryGetProperty("en", out var englishName) &&
            englishName.GetString() == "Blueprint").ToArray();
        if (matches.Length != 1 || !matches[0].Doc.GetProperty("published").GetBoolean())
            throw new InvalidDataException("The loaded SDE must contain exactly one published Blueprint category.");
        return checked((int)matches[0].Key);
    }

    public async Task<IReadOnlyList<long>> MaterializeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long ownerId,
        long containerItemId,
        IReadOnlyList<AbyssalLootPlan> loot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(loot);
        if (ownerId <= 0 || containerItemId <= 0)
            throw new ArgumentOutOfRangeException(ownerId <= 0 ? nameof(ownerId) : nameof(containerItemId));

        var typeIds = loot.Select(item => item.TypeId).Distinct().ToArray();
        var knownTypes = await itemTypes.GetAsync(typeIds, cancellationToken).ConfigureAwait(false);
        foreach (var entry in loot)
        {
            if (entry.TypeId <= 0 || entry.Count < 0)
                throw new InvalidDataException("Abyssal loot plan contains an invalid type or count.");
            if (!knownTypes.TryGetValue(entry.TypeId, out var type))
                throw new InvalidDataException($"Abyssal loot type {entry.TypeId} is absent from the loaded SDE.");
            var isBlueprint = type.CategoryId == blueprintCategoryId;
            if (entry.IsBlueprintCopy != isBlueprint)
                throw new InvalidDataException($"Abyssal loot type {entry.TypeId} disagrees with its SDE Blueprint category.");
            if (isBlueprint)
            {
                if (entry.BlueprintRunsPerCopy is not { } runs ||
                    runs.Count != entry.Count ||
                    runs.Any(value => value is < 1 or > 5))
                    throw new InvalidDataException($"Abyssal blueprint type {entry.TypeId} must have one persisted 1..5 run count per copy.");
            }
            else if (entry.BlueprintRunsPerCopy is not null)
            {
                throw new InvalidDataException($"Non-blueprint Abyssal loot type {entry.TypeId} cannot carry blueprint run counts.");
            }
        }

        var created = new List<long>();
        foreach (var entry in loot)
        {
            if (entry.Count == 0)
                continue;

            if (entry.IsBlueprintCopy)
            {
                foreach (var runs in entry.BlueprintRunsPerCopy!)
                {
                    created.Add(await items.CreateAsync(connection, transaction,
                        new NewItem(
                            entry.TypeId,
                            ownerId,
                            containerItemId,
                            Flag: 0,
                            Quantity: -2,
                            Singleton: true,
                            BlueprintCopy: true,
                            BlueprintLocked: false,
                            BlueprintRuns: runs,
                            MaterialEfficiency: 0,
                            TimeEfficiency: 0),
                        cancellationToken).ConfigureAwait(false));
                }
            }
            else
            {
                created.Add(await items.CreateAsync(connection, transaction,
                    new NewItem(
                        entry.TypeId,
                        ownerId,
                        containerItemId,
                        Flag: 0,
                        Quantity: entry.Count,
                        Singleton: false,
                        BlueprintCopy: false,
                        BlueprintLocked: false,
                        BlueprintRuns: null,
                        MaterialEfficiency: 0,
                        TimeEfficiency: 0),
                    cancellationToken).ConfigureAwait(false));
            }
        }
        return Array.AsReadOnly(created.ToArray());
    }
}
