using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Fitting;
using Eve.GameStore;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Skills;
using Npgsql;

namespace Eve.Host;

public sealed class PersistenceCrateServiceBackend(
    NpgsqlDataSource source,
    PgItemStore items,
    ItemTypeCatalog itemTypes,
    PgSkillStore? skills = null,
    SkillCatalog? skillCatalog = null,
    GameStoreCache? gameStoreCache = null) : ICrateServiceBackend
{
    private const string FittingsTable = "savedFittings";
    private const int CharacterFittingLimit = 500;
    private const int CargoHoldFlag = 5;
    private const int DroneBayFlag = 87;
    private const int FighterBayFlag = 158;
    private readonly SemaphoreSlim _fittingGate = new(1, 1);

    public async Task<CrateItem?> GetItemAsync(long itemId, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(itemId, cancellationToken);
        if (item is null) return null;
        var type = await itemTypes.GetAsync(item.TypeId, cancellationToken);
        if (type is null) return null;
        return ToCrateItem(item, type);
    }

    public async Task<bool> TypeExistsAsync(int typeId, CancellationToken cancellationToken) =>
        await itemTypes.GetAsync(typeId, cancellationToken) is not null;

    public async Task<CrateOpenResult?> OpenAsync(
        long itemId, long characterId, int crateCount, long destinationId, int destinationFlagId,
        long grantLocationId, int grantFlagId, IReadOnlyList<CrateLootEntry> loot,
        CancellationToken cancellationToken)
    {
        if (crateCount <= 0) return null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var crate = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (crate is null || crate.OwnerId != characterId ||
            crate.LocationId != destinationId || crate.Flag != destinationFlagId)
            return null;
        var crateType = await itemTypes.GetAsync(crate.TypeId, cancellationToken);
        if (crateType?.GroupId is not (1194 or 1818) ||
            (crate.Singleton ? 1 : crate.Quantity) < crateCount)
            return null;

        var granted = new List<CrateRewardItem>(loot.Count);
        foreach (var entry in loot)
        {
            var type = await itemTypes.GetAsync(entry.TypeId, cancellationToken);
            if (type is null || entry.Quantity <= 0) continue;
            if (entry.Singleton)
            {
                for (long count = 0; count < entry.Quantity; count++)
                {
                    var newItemId = await items.CreateAsync(connection, transaction,
                        new NewItem(entry.TypeId, characterId, grantLocationId, grantFlagId, 1, Singleton: true),
                        cancellationToken);
                    granted.Add(new CrateRewardItem(
                        newItemId, entry.TypeId, 1, type.GroupId, type.CategoryId, Singleton: true));
                }
                continue;
            }

            var stackItemId = await items.CreateAsync(connection, transaction,
                new NewItem(entry.TypeId, characterId, grantLocationId, grantFlagId, entry.Quantity), cancellationToken);
            granted.Add(new CrateRewardItem(stackItemId, entry.TypeId, entry.Quantity, type.GroupId, type.CategoryId));
        }

        CrateInventoryChange crateChange;
        if (crate.Singleton || crate.Quantity == crateCount)
        {
            await items.DeleteAsync(connection, transaction, crate.Id, cancellationToken);
            crateChange = new CrateInventoryChange(
                ToCrateItem(crate with { LocationId = 6, Quantity = crate.Singleton ? -1 : crate.Quantity }, crateType),
                crate.LocationId, crate.Flag, crate.Quantity, crate.Singleton ? 1 : crate.Quantity);
        }
        else
        {
            var remaining = crate with { Quantity = crate.Quantity - crateCount };
            await items.SetQuantityAsync(connection, transaction, crate.Id, crate.Quantity - crateCount, cancellationToken);
            crateChange = new CrateInventoryChange(
                ToCrateItem(remaining, crateType), crate.LocationId, crate.Flag, crate.Quantity, crate.Quantity);
        }
        await transaction.CommitAsync(cancellationToken);
        return new CrateOpenResult(granted, crateChange);
    }

    public async Task<CrateClaimResult?> ClaimLootAsync(
        long itemId, long characterId, long pendingLocationId, long destinationId, int destinationFlagId,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (item is null || item.OwnerId != characterId || item.LocationId != pendingLocationId || item.Flag != 0)
            return null;
        var type = await itemTypes.GetAsync(item.TypeId, cancellationToken);
        if (type is null) return null;

        await items.MoveAsync(connection, transaction, itemId, destinationId, destinationFlagId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var after = item with { LocationId = destinationId, Flag = destinationFlagId };
        return new CrateClaimResult(ToCrateItem(after, type), item.LocationId, item.Flag, item.Quantity);
    }

    public async Task<CrateSkillClaim?> ClaimSkillBundleAsync(
        long characterId, CrateSkillBundle bundle, CancellationToken cancellationToken)
    {
        if (skills is null || skillCatalog is null) return null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var known = (await skills.ForCharacterAsync(connection, transaction, characterId, cancellationToken))
            .ToDictionary(skill => skill.TypeId);
        var granted = new List<CharacterSkill>();
        foreach (var grant in bundle.Skills)
        {
            var skillType = skillCatalog.Get(grant.TypeId);
            if (skillType is null || known.GetValueOrDefault(grant.TypeId)?.Level >= grant.Level) continue;
            granted.Add(new CharacterSkill(
                grant.TypeId, (short)grant.Level, SkillPoints.At(skillType.Rank, grant.Level)));
        }
        if (granted.Count > 0)
            await skills.GrantAsync(connection, transaction, characterId, granted, cancellationToken, trackRecentChanges: true);
        if (bundle.FreeSkillPoints > 0)
            await skills.GrantFreeSkillPointsAsync(connection, transaction, characterId, bundle.FreeSkillPoints, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CrateSkillClaim(granted.Count, bundle.FreeSkillPoints);
    }

    public async Task<long?> ClaimFittingAsync(long characterId, int fittingId, CancellationToken cancellationToken)
    {
        if (gameStoreCache is null || characterId <= 0 || fittingId <= 0) return null;
        await gameStoreCache.LoadAsync(FittingsTable, cancellationToken);
        await _fittingGate.WaitAsync(cancellationToken);
        try
        {
            if (gameStoreCache.Read(FittingsTable, "/").Data is not JsonObject root ||
                FindFitting(root, fittingId) is not { } sourceFit)
                return null;

            var shipType = await itemTypes.GetAsync(sourceFit.ShipTypeId, cancellationToken);
            if (shipType?.CategoryId is not (6 or 65) || string.IsNullOrWhiteSpace(sourceFit.Name) ||
                sourceFit.Name.Contains("@@", StringComparison.Ordinal) ||
                sourceFit.Description.Contains("@@", StringComparison.Ordinal) || sourceFit.FitData.Count == 0)
                return null;
            foreach (var entry in sourceFit.FitData)
            {
                if (entry.TypeId <= 0 || entry.FlagId <= 0 || entry.Quantity == 0 ||
                    !Slots.IsSlotFlag(entry.FlagId) && entry.FlagId is not (CargoHoldFlag or DroneBayFlag or FighterBayFlag) ||
                    !await TypeExistsAsync(entry.TypeId, cancellationToken))
                    return null;
            }

            var ownerKey = characterId.ToString(CultureInfo.InvariantCulture);
            var owners = root["owners"] as JsonObject ?? new JsonObject();
            if (root["owners"] is null) root["owners"] = owners;
            var ownerRecord = owners[ownerKey] as JsonObject ?? new JsonObject
            {
                ["ownerID"] = characterId,
                ["scope"] = "character",
                ["fittings"] = new JsonObject(),
            };
            var fittings = ownerRecord["fittings"] as JsonObject ?? new JsonObject();
            if (fittings.Count >= CharacterFittingLimit) return null;

            var meta = root["_meta"] as JsonObject ?? new JsonObject { ["version"] = 1 };
            var maxStoredId = owners
                .SelectMany(owner => (owner.Value?["fittings"] as JsonObject ?? new JsonObject())
                    .Select(fit => ReadInt(fit.Value?["fittingID"], ReadInt(JsonValue.Create(fit.Key)))))
                .DefaultIfEmpty(0)
                .Max();
            var fittingIdToSave = Math.Max(ReadInt(meta["nextFittingID"], 1), maxStoredId + 1);
            if (fittingIdToSave <= 0 || fittingIdToSave == int.MaxValue) return null;

            var saved = new JsonObject
            {
                ["fittingID"] = fittingIdToSave,
                ["ownerID"] = characterId,
                ["shipTypeID"] = sourceFit.ShipTypeId,
                ["name"] = sourceFit.Name,
                ["description"] = sourceFit.Description,
                ["fitData"] = sourceFit.ToJsonArray(),
                ["savedDate"] = Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .ToString(CultureInfo.InvariantCulture),
            };
            fittings[fittingIdToSave.ToString(CultureInfo.InvariantCulture)] = saved;
            ownerRecord["ownerID"] = characterId;
            ownerRecord["scope"] ??= "character";
            ownerRecord["fittings"] = fittings;
            owners[ownerKey] = ownerRecord;
            meta["nextFittingID"] = fittingIdToSave + 1;
            root["_meta"] = meta;

            var write = gameStoreCache.Write(FittingsTable, "/", root);
            if (!write.Success) return null;
            await gameStoreCache.FlushTableAsync(FittingsTable, cancellationToken);
            return fittingIdToSave;
        }
        finally
        {
            _fittingGate.Release();
        }
    }

    private static SavedFitting? FindFitting(JsonObject root, int fittingId)
    {
        if (root["owners"] is not JsonObject owners) return null;
        foreach (var (ownerId, owner) in owners
                     .Select(pair => (OwnerId: ParseLong(pair.Key), Record: pair.Value as JsonObject))
                     .Where(pair => pair.OwnerId > 0 && pair.Record is not null)
                     .OrderBy(pair => pair.OwnerId))
        {
            if (owner!["fittings"] is not JsonObject fittings ||
                fittings[fittingId.ToString(CultureInfo.InvariantCulture)] is not JsonObject fitting)
                continue;
            var fitData = ParseFitData(fitting["fitData"]);
            var name = Text(fitting["name"]).Trim();
            if (ReadInt(fitting["fittingID"], fittingId) != fittingId || ReadInt(fitting["shipTypeID"]) <= 0 ||
                name.Length == 0 || fitData is null || fitData.Count == 0)
                continue;
            return new SavedFitting(
                ReadInt(fitting["shipTypeID"]), name[..Math.Min(50, name.Length)],
                Text(fitting["description"])[..Math.Min(500, Text(fitting["description"]).Length)], fitData);
        }
        return null;
    }

    private static IReadOnlyList<SavedFitItem>? ParseFitData(JsonNode? node)
    {
        if (node is not JsonArray rows) return null;
        var result = new List<SavedFitItem>(rows.Count);
        foreach (var row in rows)
        {
            if (row is not JsonArray values || values.Count < 3) return null;
            result.Add(new SavedFitItem(ReadInt(values[0]), ReadInt(values[1]), ReadInt(values[2])));
        }
        return result;
    }

    private static int ReadInt(JsonNode? node, int fallback = 0)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<long>(out var large) && large is >= int.MinValue and <= int.MaxValue) return (int)large;
            if (value.TryGetValue<string>(out var text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
        }
        return fallback;
    }

    private static long ParseLong(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text)
        ? text
        : node?.ToString() ?? string.Empty;

    private sealed record SavedFitItem(int TypeId, int FlagId, int Quantity);

    private sealed record SavedFitting(int ShipTypeId, string Name, string Description, IReadOnlyList<SavedFitItem> FitData)
    {
        public JsonArray ToJsonArray() => new([.. FitData.Select(item => (JsonNode?)new JsonArray(item.TypeId, item.FlagId, item.Quantity))]);
    }

    private static CrateItem ToCrateItem(Item item, ItemType type) =>
        new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity,
            item.Singleton, type.GroupId, type.CategoryId, item.CustomName, item.CustomInfo);
}
