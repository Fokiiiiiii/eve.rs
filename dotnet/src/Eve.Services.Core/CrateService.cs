using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Eve.Marshal;
using static Eve.Services.MachoErrors;

namespace Eve.Services.Core;

public sealed record CrateItem(
    long ItemId, int TypeId, long OwnerId, long LocationId, int FlagId, long Quantity, bool Singleton,
    int GroupId, int CategoryId, string? Name, string? CustomInfo);

public sealed record CrateLootEntry(int TypeId, long Quantity, bool Singleton = false);
public sealed record CrateRewardItem(
    long ItemId, int TypeId, long Quantity, int GroupId = 0, int CategoryId = 0, bool Singleton = false);
public sealed record CrateInventoryChange(
    CrateItem Item, long PreviousLocationId, int PreviousFlag, long PreviousQuantity, long PreviousStackSize);
public sealed record CrateOpenResult(IReadOnlyList<CrateRewardItem> Items, CrateInventoryChange? CrateChange = null);
public sealed record CrateSkillGrant(int TypeId, int Level);
public sealed record CrateSkillBundle(int SkillBundleId, long FreeSkillPoints, IReadOnlyList<CrateSkillGrant> Skills);
public sealed record CrateSkillClaim(int ChangedSkillCount, long FreeSkillPoints);
public sealed record CrateClaimResult(CrateItem Item, long PreviousLocationId, int PreviousFlag, long PreviousQuantity);

public interface ICrateServiceBackend
{
    Task<CrateItem?> GetItemAsync(long itemId, CancellationToken cancellationToken);
    Task<bool> TypeExistsAsync(int typeId, CancellationToken cancellationToken);
    Task<CrateOpenResult?> OpenAsync(
        long itemId, long characterId, int crateCount, long destinationId, int destinationFlagId,
        long grantLocationId, int grantFlagId, IReadOnlyList<CrateLootEntry> loot,
        CancellationToken cancellationToken);
    Task<CrateClaimResult?> ClaimLootAsync(
        long itemId, long characterId, long pendingLocationId, long destinationId, int destinationFlagId,
        CancellationToken cancellationToken);
    Task<CrateSkillClaim?> ClaimSkillBundleAsync(
        long characterId, CrateSkillBundle bundle, CancellationToken cancellationToken) =>
        Task.FromResult<CrateSkillClaim?>(null);
    Task<long?> ClaimFittingAsync(long characterId, int fittingId, CancellationToken cancellationToken) =>
        Task.FromResult<long?>(null);
}

public sealed class CrateService(ICrateServiceBackend? backend = null) : IService
{
    private const int RewardCrateGroup = 1194;
    private const int StrongBoxGroup = 1818;
    private const int TritaniumTypeId = 34;
    private const int MaximumMultiOpen = 30;
    private const int HangarFlag = 4;
    private const int PendingLootFlag = 0;
    private const long FirstPendingLocationId = -9_000_000_000_000;
    private static readonly string[] Methods =
    [
        "GetCrateLocation", "OpenCrate", "ClaimLootFromCrateStack", "PeekInsideFixedCrate",
        "ClaimLoot", "ClaimSkillBundle", "ClaimFitting",
    ];

    private readonly ConcurrentDictionary<long, PendingLoot> _pendingLoot = new();
    private readonly ConcurrentDictionary<(long CharacterId, long CrateId), ConcurrentDictionary<int, Queue<CrateSkillBundle>>> _pendingSkills = new();
    private readonly ConcurrentDictionary<(long CharacterId, long CrateId), ConcurrentDictionary<int, int>> _pendingFittings = new();
    private long _nextPendingLocationId = FirstPendingLocationId;

    public string Name => "crateService";
    public bool HasMethod(string method) => Methods.Contains(method, StringComparer.Ordinal);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (backend is null) return Unwired(call);

        return call.Method switch
        {
            "GetCrateLocation" => await GetCrateLocationAsync(call),
            "OpenCrate" => await OpenCrateAsync(call),
            "ClaimLootFromCrateStack" => await ClaimLootFromCrateStackAsync(call),
            "PeekInsideFixedCrate" => await PeekInsideFixedCrateAsync(call),
            "ClaimLoot" => await ClaimLootAsync(call),
            "ClaimSkillBundle" => await ClaimSkillBundleAsync(call),
            "ClaimFitting" => await ClaimFittingAsync(call),
            _ => PyNone.Instance,
        };
    }

    private static PyValue Unwired(ServiceCall call) => call.Method switch
    {
        "GetCrateLocation" => PyTuple.Of(new PyInt(0), new PyInt(HangarFlag)),
        "ClaimLootFromCrateStack" => PyTuple.Of(new PyInt(0), new PyInt(RequestedCount(call.Arg(1)))),
        "OpenCrate" or "PeekInsideFixedCrate" => new PyList([]),
        _ => PyNone.Instance,
    };

    private async ValueTask<PyValue> GetCrateLocationAsync(ServiceCall call)
    {
        var crate = await OwnedCrateAsync(call);
        return PyTuple.Of(InventoryWire.Id(crate.LocationId), new PyInt(crate.FlagId));
    }

    private async ValueTask<PyValue> OpenCrateAsync(ServiceCall call)
    {
        var crate = await OwnedCrateAsync(call);
        if (crate.LocationId <= 0) Notify("That crate does not have a valid reward destination.");

        var characterId = CharacterId(call);
        var pendingLocationId = Interlocked.Decrement(ref _nextPendingLocationId);
        var loot = await LootAsync(crate, 1, call.Cancellation);
        var opened = await backend!.OpenAsync(
            crate.ItemId, characterId, 1, crate.LocationId, crate.FlagId,
            pendingLocationId, PendingLootFlag, loot, call.Cancellation);
        if (opened is null) Notify("The crate could not be consumed.");
        SendInventoryChange(call.Session, opened!.CrateChange);

        RememberSpecialLoot(crate, characterId);
        var response = new List<PyValue>();
        foreach (var item in opened.Items)
        {
            if (item.ItemId <= 0 || item.TypeId <= 0 || item.Quantity <= 0) continue;
            _pendingLoot[item.ItemId] = new PendingLoot(
                characterId, crate.ItemId, crate.TypeId, pendingLocationId, crate.LocationId, crate.FlagId);
            response.Add(PyTuple.Of(InventoryWire.Id(item.ItemId), new PyInt(item.TypeId), InventoryWire.Id(item.Quantity)));
        }
        return new PyList(response);
    }

    private async ValueTask<PyValue> ClaimLootFromCrateStackAsync(ServiceCall call)
    {
        var crate = await OwnedCrateAsync(call);
        if (crate.LocationId <= 0) Notify("That crate does not have a valid reward destination.");

        var requested = RequestedCount(call.Arg(1));
        var count = (int)Math.Min(requested, StackQuantity(crate));
        if (count <= 0) Notify("That crate stack is empty.");
        var loot = await LootAsync(crate, count, call.Cancellation);
        var opened = await backend!.OpenAsync(
            crate.ItemId, CharacterId(call), count, crate.LocationId, crate.FlagId,
            crate.LocationId, crate.FlagId, loot, call.Cancellation);
        if (opened is null) Notify("The crate stack could not be consumed.");
        SendInventoryChange(call.Session, opened!.CrateChange);
        foreach (var item in opened.Items)
        {
            if (item.ItemId <= 0 || item.TypeId <= 0 || item.Quantity <= 0) continue;
            SendInventoryChange(call.Session, new CrateInventoryChange(
                new CrateItem(item.ItemId, item.TypeId, CharacterId(call), crate.LocationId, crate.FlagId,
                    item.Quantity, item.Singleton, item.GroupId, item.CategoryId, null, null),
                0, crate.FlagId, 0, 0));
        }
        return PyTuple.Of(new PyInt(count), new PyInt(requested));
    }

    private async ValueTask<PyValue> PeekInsideFixedCrateAsync(ServiceCall call)
    {
        var crate = await OwnedCrateAsync(call);
        var loot = await LootAsync(crate, 1, call.Cancellation);
        return new PyList([.. loot.Select(entry =>
            (PyValue)PyTuple.Of(new PyInt(entry.TypeId), InventoryWire.Id(entry.Quantity), new PyStr("")))]);
    }

    private async ValueTask<PyValue> ClaimLootAsync(ServiceCall call)
    {
        var itemId = Positive(call.Arg(0));
        var characterId = CharacterId(call);
        if (itemId == 0 || characterId == 0)
            Notify("That crate reward is no longer available.");

        var pending = _pendingLoot.TryGetValue(itemId, out var pendingValue) ? pendingValue : null;
        var hasPending = pending is not null;
        CrateItem? persistedItem = null;
        if (pending is not null && pending.CharacterId != characterId)
            Notify("That crate reward is no longer available.");
        if (!hasPending)
        {
            persistedItem = await backend!.GetItemAsync(itemId, call.Cancellation);
            if (persistedItem is null || persistedItem.OwnerId != characterId ||
                persistedItem.LocationId >= 0 || persistedItem.FlagId != PendingLootFlag)
                Notify("That crate reward is no longer available.");
        }

        var destinationId = pending is not null ? PositiveOr(call.Arg(1), pending.DestinationId) : Positive(call.Arg(1));
        var destinationFlagId = (int)Integer(call.Arg(2), pending?.DestinationFlagId ?? HangarFlag);
        if (destinationId <= 0)
            Notify("That crate reward does not have a valid destination.");
        if (pending is not null && (destinationId != pending.DestinationId || destinationFlagId != pending.DestinationFlagId))
            Notify("That crate loot has a different claim destination.");

        var result = await backend!.ClaimLootAsync(
            itemId, characterId, pending?.PendingLocationId ?? persistedItem!.LocationId,
            destinationId, destinationFlagId, call.Cancellation);
        if (result is null) Notify("That crate reward could not be claimed.");
        _pendingLoot.TryRemove(itemId, out _);

        var item = result!.Item;
        call.Session.SendNotification(
            "OnItemChange", "clientID",
            InventoryWire.PackedRow(new ItemRow(item.ItemId, item.TypeId, item.OwnerId, item.LocationId,
                item.FlagId, item.Quantity, item.GroupId, item.CategoryId)),
            new PyDict([
                new(new PyInt(3), InventoryWire.Id(result.PreviousLocationId)),
                new(new PyInt(4), new PyInt(result.PreviousFlag)),
            ]),
            PyNone.Instance);
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> ClaimSkillBundleAsync(ServiceCall call)
    {
        var characterId = CharacterId(call);
        var crateId = Positive(call.Arg(0));
        var bundleId = (int)Positive(call.Arg(1));
        if (backend is null || characterId == 0 || crateId == 0 || bundleId == 0) return PyNone.Instance;
        var key = (characterId, crateId);
        CrateSkillBundle bundle;
        lock (_pendingSkills)
        {
            if (!_pendingSkills.TryGetValue(key, out var bundles) ||
                !bundles.TryGetValue(bundleId, out var rewards) || rewards.Count == 0)
                return PyNone.Instance;
            bundle = rewards.Dequeue();
            if (rewards.Count == 0) bundles.TryRemove(bundleId, out _);
            if (bundles.IsEmpty) _pendingSkills.TryRemove(key, out _);
        }

        var claimed = false;
        try
        {
            var claim = await backend.ClaimSkillBundleAsync(characterId, bundle, call.Cancellation);
            if (claim is null) return PyNone.Instance;
            claimed = true;
            call.Session.SendNotification(
                "OnSkillBundleInjected", "clientID",
                new PyInt(claim.ChangedSkillCount), InventoryWire.Id(bundle.FreeSkillPoints));
            return new PyDict([
                new(new PyStr("changedSkillCount"), new PyInt(claim.ChangedSkillCount)),
                new(new PyStr("freeSkillPoints"), InventoryWire.Id(claim.FreeSkillPoints)),
            ]);
        }
        finally
        {
            if (!claimed)
            {
                lock (_pendingSkills)
                    _pendingSkills.GetOrAdd(key, _ => new ConcurrentDictionary<int, Queue<CrateSkillBundle>>())
                        .GetOrAdd(bundleId, _ => new Queue<CrateSkillBundle>()).Enqueue(bundle);
            }
        }
    }

    private async ValueTask<PyValue> ClaimFittingAsync(ServiceCall call)
    {
        var characterId = CharacterId(call);
        var crateId = Positive(call.Arg(0));
        var fittingId = (int)Positive(call.Arg(1));
        if (backend is null || characterId == 0 || crateId == 0 || fittingId == 0) return PyNone.Instance;
        var key = (characterId, crateId);
        ConcurrentDictionary<int, int> fittings;
        lock (_pendingFittings)
        {
            if (!_pendingFittings.TryGetValue(key, out var pending) ||
                !pending.TryGetValue(fittingId, out var count) || count <= 0)
                return PyNone.Instance;
            fittings = pending;
            fittings[fittingId] = count - 1;
        }
        var saved = false;
        try
        {
            var savedId = await backend.ClaimFittingAsync(characterId, fittingId, call.Cancellation);
            if (savedId is not > 0) return PyNone.Instance;
            saved = true;
            return InventoryWire.Id(savedId.Value);
        }
        finally
        {
            lock (_pendingFittings)
            {
                if (!saved)
                    _pendingFittings.GetOrAdd(key, _ => new ConcurrentDictionary<int, int>())
                        .AddOrUpdate(fittingId, 1, (_, count) => count + 1);
                else if (_pendingFittings.TryGetValue(key, out var current) && ReferenceEquals(current, fittings))
                {
                    if (current.TryGetValue(fittingId, out var count) && count == 0)
                        current.TryRemove(fittingId, out _);
                    if (current.IsEmpty) _pendingFittings.TryRemove(key, out _);
                }
            }
        }
    }

    private async Task<CrateItem> OwnedCrateAsync(ServiceCall call)
    {
        var characterId = CharacterId(call);
        if (characterId == 0) Notify("You need an active character to open crates.");
        var itemId = Positive(call.Arg(0));
        var item = itemId == 0 ? null : await backend!.GetItemAsync(itemId, call.Cancellation);
        if (item is null || item.OwnerId != characterId) Notify("That crate could not be found.");
        if (!IsCrate(item!)) Notify("That item is not a crate.");
        if (StackQuantity(item!) <= 0) Notify("That crate stack is empty.");
        return item!;
    }

    private async Task<IReadOnlyList<CrateLootEntry>> LootAsync(CrateItem crate, int count, CancellationToken cancellationToken)
    {
        var custom = ParseCustomLoot(crate.CustomInfo);
        var valid = new List<CrateLootEntry>();
        foreach (var entry in custom)
        {
            if (await backend!.TypeExistsAsync(entry.TypeId, cancellationToken)) valid.Add(entry);
        }
        if (valid.Count == 0)
            valid.Add(new CrateLootEntry(TritaniumTypeId, 100 + crate.TypeId % 25));
        if (count > 1)
            valid = Aggregate(valid.SelectMany(entry => Enumerable.Repeat(entry, count)));
        return valid;
    }

    private void RememberSpecialLoot(CrateItem crate, long characterId)
    {
        var key = (characterId, crate.ItemId);
        var bundles = ParseSkillBundles(crate.CustomInfo);
        if (bundles.Count > 0)
        {
            var bundlesById = bundles.ToDictionary(bundle => bundle.SkillBundleId);
            lock (_pendingSkills)
            {
                var pending = _pendingSkills.GetOrAdd(key, _ => new ConcurrentDictionary<int, Queue<CrateSkillBundle>>());
                foreach (var bundle in bundlesById.Values)
                    pending.GetOrAdd(bundle.SkillBundleId, _ => new Queue<CrateSkillBundle>()).Enqueue(bundle);
            }
        }

        lock (_pendingFittings)
        {
            foreach (var fittingId in ParseFittingIds(crate.CustomInfo))
                _pendingFittings.GetOrAdd(key, _ => new ConcurrentDictionary<int, int>())
                    .AddOrUpdate(fittingId, 1, (_, count) => count + 1);
        }
    }

    private static List<CrateLootEntry> ParseCustomLoot(string? customInfo)
    {
        if (!TryRoot(customInfo, out var root)) return [];
        using (root)
        {
            if (root.RootElement.ValueKind != JsonValueKind.Object) return [];
            var raw = Property(root.RootElement, "evejsCrateLoot", "crateLoot", "loot");
            if (raw.ValueKind != JsonValueKind.Array) return [];
            var parsed = new List<CrateLootEntry>();
            foreach (var node in raw.EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.Array)
                {
                    var values = node.EnumerateArray().ToArray();
                    if (values.Length == 0) continue;
                    parsed.Add(new CrateLootEntry(
                        (int)Positive(ReadLong(values[0])),
                        Math.Max(1, values.Length > 1 ? ReadLong(values[1], 1) : 1),
                        values.Length > 2 && ReadBool(values[2])));
                }
                else if (node.ValueKind == JsonValueKind.Object)
                {
                    parsed.Add(new CrateLootEntry(
                        (int)Positive(ReadProperty(node, "typeID", "itemTypeID")),
                        Math.Max(1, ReadProperty(node, 1, "quantity", "stacksize")),
                        ReadProperty(node, false, "singleton")));
                }
            }
            return parsed.Where(entry => entry.TypeId > 0).ToList();
        }
    }

    private static List<CrateSkillBundle> ParseSkillBundles(string? customInfo)
    {
        if (!TryRoot(customInfo, out var document)) return [];
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            var raw = Property(document.RootElement, "evejsCrateSkillBundles", "skillBundles", "skillBundle");
            var bundles = new List<CrateSkillBundle>();
            if (raw.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in raw.EnumerateArray()) AddBundle(bundles, entry, 0);
            }
            else if (raw.ValueKind == JsonValueKind.Object)
            {
                if (ReadProperty(raw, 0, "skillBundleID", "bundleID", "id") > 0)
                    AddBundle(bundles, raw, 0);
                else
                    foreach (var property in raw.EnumerateObject())
                        if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                            AddBundle(bundles, property.Value, id);
            }
            return bundles;
        }
    }

    private static void AddBundle(List<CrateSkillBundle> output, JsonElement source, int fallbackId)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        var id = (int)Positive(ReadProperty(source, "skillBundleID", "bundleID", "id"));
        if (id == 0) id = fallbackId;
        if (id <= 0) return;
        var rawSkills = Property(source, "skills", "skillLevels", "skillGrants", "grants");
        var skills = new List<CrateSkillGrant>();
        if (rawSkills.ValueKind == JsonValueKind.Array)
        {
            foreach (var skill in rawSkills.EnumerateArray())
            {
                if (skill.ValueKind == JsonValueKind.Array)
                {
                    var values = skill.EnumerateArray().ToArray();
                    if (values.Length > 0)
                        skills.Add(new CrateSkillGrant((int)Positive(ReadLong(values[0])),
                            Math.Clamp((int)ReadLong(values.Length > 1 ? values[1] : default, 1), 1, 5)));
                }
                else if (skill.ValueKind == JsonValueKind.Object)
                    skills.Add(new CrateSkillGrant((int)Positive(ReadProperty(skill, "typeID", "skillTypeID")),
                        Math.Clamp((int)ReadProperty(skill, 1, "level", "toLevel", "trainedSkillLevel"), 1, 5)));
            }
        }
        output.Add(new CrateSkillBundle(
            id, Math.Max(0, ReadProperty(source, 0, "freeSkillPoints", "skillPoints", "unallocatedSkillPoints", "sp")),
            skills.Where(skill => skill.TypeId > 0).ToArray()));
    }

    private static List<int> ParseFittingIds(string? customInfo)
    {
        if (!TryRoot(customInfo, out var document)) return [];
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            var raw = Property(document.RootElement, "evejsCrateFittings", "crateFittings", "fittings");
            var values = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : [raw];
            return values.Select(value => value.ValueKind == JsonValueKind.Object
                    ? (int)Positive(ReadProperty(value, "fittingID", "id"))
                    : (int)Positive(ReadLong(value)))
                .Where(id => id > 0).Distinct().ToList();
        }
    }

    private static bool TryRoot(string? json, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(json ?? "");
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static JsonElement Property(JsonElement source, params string[] names)
    {
        foreach (var name in names)
            if (source.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value;
        return default;
    }

    private static long ReadProperty(JsonElement source, params string[] names) => ReadLong(Property(source, names));
    private static long ReadProperty(JsonElement source, long fallback, params string[] names) =>
        ReadLong(Property(source, names), fallback);
    private static bool ReadProperty(JsonElement source, bool fallback, params string[] names)
    {
        var value = Property(source, names);
        return value.ValueKind == JsonValueKind.Undefined ? fallback : ReadBool(value);
    }

    private static long ReadLong(JsonElement value, long fallback = 0) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => fallback,
    };

    private static bool ReadBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => ReadLong(value) > 0,
        JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0,
        _ => false,
    };

    private static List<CrateLootEntry> Aggregate(IEnumerable<CrateLootEntry> entries) =>
        entries.GroupBy(entry => (entry.TypeId, entry.Singleton))
            .Select(group => new CrateLootEntry(group.Key.TypeId, group.Sum(entry => entry.Quantity), group.Key.Singleton))
            .ToList();

    private static bool IsCrate(CrateItem item) =>
        item.GroupId is RewardCrateGroup or StrongBoxGroup ||
        (item.GroupId > 0 && item.Name is { } name &&
            (name.Contains("crate", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("gift box", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("strong box", StringComparison.OrdinalIgnoreCase)));

    private static long StackQuantity(CrateItem item) => item.Singleton ? 1 : Math.Max(0, item.Quantity);
    private static int RequestedCount(PyValue value) => (int)Math.Clamp(Integer(value, 1), 1, MaximumMultiOpen);
    private static long CharacterId(ServiceCall call) => Positive(call.Session.Get("charid") ?? PyNone.Instance);

    private static void SendInventoryChange(ClientSession session, CrateInventoryChange? change)
    {
        if (change is null) return;
        var item = change.Item;
        var differences = new List<KeyValuePair<PyValue, PyValue>>();
        if (item.LocationId != change.PreviousLocationId)
            differences.Add(new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId)));
        if (item.FlagId != change.PreviousFlag)
            differences.Add(new(new PyInt(4), new PyInt(change.PreviousFlag)));
        if (item.Singleton)
        {
            if (item.Quantity != change.PreviousQuantity)
                differences.Add(new(new PyInt(5), InventoryWire.Id(change.PreviousQuantity)));
            if ((item.Quantity < 0 ? 1 : item.Quantity) != change.PreviousStackSize)
                differences.Add(new(new PyInt(9), InventoryWire.Id(change.PreviousStackSize)));
        }
        else if (item.Quantity != change.PreviousStackSize)
        {
            differences.Add(new(new PyInt(9), InventoryWire.Id(change.PreviousStackSize)));
        }

        session.SendNotification("OnItemChange", "clientID",
            InventoryWire.PackedRow(new ItemRow(item.ItemId, item.TypeId, item.OwnerId, item.LocationId,
                item.FlagId, item.Quantity, item.GroupId, item.CategoryId)),
            new PyDict(differences), PyNone.Instance);
    }
    private static long Positive(PyValue value) => Math.Max(0, Integer(value));
    private static long Positive(long value) => Math.Max(0, value);
    private static long PositiveOr(PyValue value, long fallback)
    {
        var parsed = Positive(value);
        return parsed > 0 ? parsed : fallback;
    }

    private static long Integer(PyValue value, long fallback = 0) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => fallback,
    };

    [DoesNotReturn]
    private static void Notify(string message) => throw MachoErrors.ThrowUserError(message);

    private sealed record PendingLoot(
        long CharacterId, long CrateId, int CrateTypeId, long PendingLocationId,
        long DestinationId, int DestinationFlagId);
}
