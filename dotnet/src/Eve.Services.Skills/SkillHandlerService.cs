using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>An active booster returned to the client, including its expiry and selected side-effect IDs.</summary>
public sealed record SkillActiveBoosterSnapshot(long ItemId, int BoosterTypeId, long ExpiryTimeFileTime, IReadOnlyList<int> SideEffectIds);

/// <summary>A client-visible Dogma modifier from an equipped implant or active booster.</summary>
public sealed record SkillAttributeModifier(int AttributeId, long ItemId, int TypeId, int Operation, double Value);

/// <summary>Current client-visible booster and character attribute modifier state.</summary>
public sealed record SkillAugmentationSnapshot(
    IReadOnlyList<SkillActiveBoosterSnapshot> ActiveBoosters,
    IReadOnlyList<SkillAttributeModifier> AttributeModifiers)
{
    public static SkillAugmentationSnapshot Empty { get; } = new([], []);
}


/// <summary>Reads the character's current active boosters and exact client attribute-modifier wire list.</summary>
public delegate ValueTask<SkillAugmentationSnapshot> SkillAugmentationReader(long characterId, CancellationToken cancellationToken);

/// <summary>Serves character skills, augmentations, booster activation, and free skill point operations.</summary>
[Service("skillHandler", Aliases = ["skillMgr"])]
public sealed partial class SkillHandlerService(
    PgSkillStore store, SkillCatalog catalog, CharacterTraining training, PgCharacterStore characters, Eve.Inventory.PgItemStore items, Eve.Dogma.DogmaStatic dogmaStatic, TrainingAnnouncer announcer, Func<long> nowUnixMs,
    SkillAugmentationReader readAugmentations, Func<double>? nextBoosterRoll = null)
{
    /// <summary>The five attributes a character trains with (charisma, intelligence, memory, perception, willpower).</summary>
    private static readonly int[] AttributeIds = [164, 165, 166, 167, 168];

    private const int FreeRespecs = 3;
    private const int BoosterFlag = 88;
    private const int ImplantFlag = 89;
    private const int ImplantCategoryId = 20;
    private const int ImplantSlotAttributeId = 331;
    private const int BoosterSlotAttributeId = 1087;
    private const int BoosterDurationAttributeId = 330;
    private const int SkillLevelAttributeId = 280;
    private const int HangarFlag = 4;
    private const int CargoFlag = 5;

    private static PyValue None => PyNone.Instance;

    private static long? CharacterOf(ServiceCall call) => call.Session.CharacterId;

public static SkillAugmentationReader CreateAugmentationReader(
        Eve.Inventory.PgItemStore itemStore,
        Eve.Dogma.DogmaStatic dogma,
        PgSkillStore skillStore,
        Func<long> nowUnixMs,
        Func<double>? nextBoosterRoll = null) =>
        (characterId, cancellationToken) =>
            ReadAugmentationSnapshotAsync(itemStore, dogma, skillStore, nowUnixMs, nextBoosterRoll, characterId, cancellationToken);

    private static async Task<(IReadOnlyList<Eve.Inventory.Item> Inventory, IReadOnlyList<CharacterSkill> Skills, Dictionary<long, StoredSkillBooster> Active)> ReadLockedAugmentationStateAsync(
        Eve.Inventory.PgItemStore itemStore,
        Eve.Dogma.DogmaStatic dogma,
        PgSkillStore skillStore,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        long nowMs,
        long nowFileTime,
        Func<double>? nextBoosterRoll,
        CancellationToken cancellationToken)
    {
        var observedInventory = await itemStore.InLocationAsync(characterId, characterId, cancellationToken);
        var inventory = new List<Eve.Inventory.Item>();
        foreach (var observed in observedInventory.Where(item => item.Flag is ImplantFlag or BoosterFlag))
        {
            var item = await itemStore.LockAndGetAsync(connection, transaction, observed.Id, cancellationToken);
            if (item is not null && item.OwnerId == characterId && item.LocationId == characterId)
            {
                inventory.Add(item);
            }
        }

        var skills = await skillStore.ForCharacterAsync(connection, transaction, characterId, cancellationToken);
        await dogma.EnsureTypesAsync(inventory.Select(item => item.TypeId).Concat(skills.Select(skill => skill.TypeId)), cancellationToken);
        var stored = await skillStore.ActiveSkillBoostersAsync(connection, transaction, characterId, nowFileTime, cancellationToken);
        var installedBoosters = inventory.Where(item => item.Flag == BoosterFlag).ToArray();
        var active = await MaterializeLegacyBoosterRowsAsync(
            skillStore, dogma, connection, transaction, characterId, nowMs, inventory, skills,
            installedBoosters, stored, nextBoosterRoll, cancellationToken);
        return (inventory, skills, active);
    }

    private static async ValueTask<SkillAugmentationSnapshot> ReadAugmentationSnapshotAsync(
        Eve.Inventory.PgItemStore itemStore,
        Eve.Dogma.DogmaStatic dogma,
        PgSkillStore skillStore,
        Func<long> nowUnixMs,
        Func<double>? nextBoosterRoll,
        long characterId,
        CancellationToken cancellationToken)
    {
        var state = await skillStore.InTransactionAsync(async (connection, transaction, ct) =>
        {
            await skillStore.LockActiveSkillBoostersAsync(connection, transaction, characterId, ct);
            var nowMs = nowUnixMs();
            var nowFileTime = Py.FileTimeFromUnixMs(nowMs);
            await PruneExpiredBoosterRowsAsync(skillStore, itemStore, connection, transaction, characterId, nowFileTime, ct);
            return await ReadLockedAugmentationStateAsync(
                itemStore, dogma, skillStore, connection, transaction, characterId, nowMs, nowFileTime, nextBoosterRoll, ct);
        }, cancellationToken);

        var (world, dogmaItems) = BuildAugmentationWorld(characterId, state.Inventory, state.Skills, state.Active, dogma);
        var modifiers = new List<SkillAttributeModifier>();
        foreach (var item in state.Inventory)
        {
            if (!dogmaItems.TryGetValue(item.Id, out var dogmaItem))
            {
                continue;
            }
            foreach (var effect in world.RunningEffects(dogmaItem))
            {
                foreach (var modifier in effect.Modifiers.Where(modifier => AttributeIds.Contains(modifier.ModifiedAttributeId)))
                {
                    modifiers.Add(new SkillAttributeModifier(
                        modifier.ModifiedAttributeId, item.Id, item.TypeId, (int)modifier.Operation,
                        world.Get(item.Id, modifier.ModifyingAttributeId)));
                }
            }
        }
        return new SkillAugmentationSnapshot(
            [.. state.Active.Values.Select(booster => new SkillActiveBoosterSnapshot(
                booster.ItemId, booster.BoosterTypeId, booster.ExpiryFileTime, booster.SideEffectIds))],
            modifiers);
    }

    private static async Task PruneExpiredBoosterRowsAsync(
        PgSkillStore skillStore,
        Eve.Inventory.PgItemStore itemStore,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        long nowFileTime,
        CancellationToken cancellationToken)
    {
        var expired = await skillStore.ExpiredSkillBoostersAsync(connection, transaction, characterId, nowFileTime, cancellationToken);
        foreach (var booster in expired)
        {
            var item = await itemStore.LockAndGetAsync(connection, transaction, booster.ItemId, cancellationToken);
            if (item is not null && item.OwnerId == characterId && item.LocationId == characterId &&
                item.Flag == BoosterFlag && item.TypeId == booster.BoosterTypeId)
            {
                await itemStore.DeleteAsync(connection, transaction, item.Id, cancellationToken);
            }
        }
        await skillStore.DeleteExpiredSkillBoostersAsync(connection, transaction, characterId, nowFileTime, cancellationToken);
    }

    private static async Task<Dictionary<long, StoredSkillBooster>> MaterializeLegacyBoosterRowsAsync(
        PgSkillStore skillStore,
        Eve.Dogma.DogmaStatic dogma,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        long nowMs,
        IReadOnlyList<Eve.Inventory.Item> inventory,
        IReadOnlyList<CharacterSkill> skills,
        IReadOnlyList<Eve.Inventory.Item> installedBoosters,
        IReadOnlyList<StoredSkillBooster> stored,
        Func<double>? nextBoosterRoll,
        CancellationToken cancellationToken)
    {
        var active = ActiveBoostersInInventory(characterId, inventory, stored, dogma);
        foreach (var item in installedBoosters)
        {
            if (active.ContainsKey(item.Id) || !IsBoosterType(dogma.Type(item.TypeId)))
            {
                continue;
            }

            var type = dogma.Type(item.TypeId)!;
            var outcome = RollBoosterEffects(
                characterId, item.Id, type, inventory, skills, active, dogma, nextBoosterRoll);
            var expiry = Py.FileTimeFromUnixMs(checked(nowMs + (long)Math.Ceiling(outcome.DurationMs)));
            if (!await skillStore.TryPersistActiveBoosterAsync(
                    connection, transaction, characterId, item.Id, item.TypeId, expiry, outcome.SideEffectIds, cancellationToken))
            {
                throw new InvalidOperationException($"Could not persist legacy active booster {item.Id}.");
            }
            active.Add(item.Id, new StoredSkillBooster(
                characterId, item.Id, item.TypeId, expiry, outcome.SideEffectIds));
        }
        return active;
    }

    private static Dictionary<long, StoredSkillBooster> ActiveBoostersInInventory(
        long characterId,
        IReadOnlyList<Eve.Inventory.Item> inventory,
        IReadOnlyList<StoredSkillBooster> stored,
        Eve.Dogma.DogmaStatic dogma)
    {
        var itemsById = inventory.ToDictionary(item => item.Id);
        return stored.Where(booster =>
                itemsById.TryGetValue(booster.ItemId, out var item) &&
                item.OwnerId == characterId && item.LocationId == characterId && item.Flag == BoosterFlag &&
                item.TypeId == booster.BoosterTypeId && IsBoosterType(dogma.Type(item.TypeId)))
            .ToDictionary(booster => booster.ItemId);
    }

    private static bool IsBoosterType(Eve.Dogma.TypeDef? type) =>
        type is { CategoryId: ImplantCategoryId } &&
        type.Attributes.TryGetValue(BoosterSlotAttributeId, out var slot) &&
        double.IsFinite(slot) && slot > 0 && slot <= int.MaxValue && slot == Math.Truncate(slot);

    private static bool IsImplantType(Eve.Dogma.TypeDef? type) =>
        type is { CategoryId: ImplantCategoryId } &&
        type.Attributes.TryGetValue(ImplantSlotAttributeId, out var slot) &&
        double.IsFinite(slot) && slot > 0 && slot <= int.MaxValue && slot == Math.Truncate(slot);

    private static (Eve.Dogma.DogmaWorld World, Dictionary<long, Eve.Dogma.DogmaItem> Items) BuildAugmentationWorld(
        long characterId,
        IReadOnlyList<Eve.Inventory.Item> inventory,
        IReadOnlyList<CharacterSkill> skills,
        IReadOnlyDictionary<long, StoredSkillBooster> active,
        Eve.Dogma.DogmaStatic dogma)
    {
        var world = new Eve.Dogma.DogmaWorld(dogma, characterId);
        var dogmaItems = new Dictionary<long, Eve.Dogma.DogmaItem>();
        var character = new Eve.Dogma.DogmaItem(characterId, 0, characterId, 0);
        world.Add(character);
        dogmaItems[characterId] = character;
        foreach (var item in inventory)
        {
            var type = dogma.Type(item.TypeId);
            Eve.Dogma.DogmaItem? model = null;
            if (item.Flag == ImplantFlag && IsImplantType(type))
            {
                model = new Eve.Dogma.DogmaItem(item.Id, item.TypeId, characterId, characterId, item.Flag);
            }
            else if (item.Flag == BoosterFlag && active.TryGetValue(item.Id, out var booster) &&
                     type is not null && booster.BoosterTypeId == item.TypeId)
            {
                var effects = type.EffectIds.Where(effectId =>
                    dogma.Effect(effectId) is { ChanceAttributeId: 0 } || booster.SideEffectIds.Contains(effectId)).ToHashSet();
                model = new Eve.Dogma.DogmaItem(item.Id, item.TypeId, characterId, characterId, item.Flag,
                    Eve.Dogma.ItemState.Active, effects);
            }
            if (model is not null)
            {
                world.Add(model);
                dogmaItems[item.Id] = model;
            }
        }
        var skillLevelAttributeId = dogma.AttributeId("skillLevel") ?? SkillLevelAttributeId;
        foreach (var skill in skills.Where(skill => skill.Level > 0))
        {
            var id = long.MinValue + skill.TypeId;
            var model = new Eve.Dogma.DogmaItem(id, skill.TypeId, characterId, characterId, 7, Eve.Dogma.ItemState.Active);
            world.Add(model);
            world.SetValue(id, skillLevelAttributeId, skill.Level);
        }
        return (world, dogmaItems);
    }

    private static (double DurationMs, IReadOnlyList<int> SideEffectIds) RollBoosterEffects(
        long characterId,
        long itemId,
        Eve.Dogma.TypeDef boosterType,
        IReadOnlyList<Eve.Inventory.Item> inventory,
        IReadOnlyList<CharacterSkill> skills,
        IReadOnlyDictionary<long, StoredSkillBooster> active,
        Eve.Dogma.DogmaStatic dogma,
        Func<double>? nextBoosterRoll)
    {
        var (world, _) = BuildAugmentationWorld(characterId, inventory, skills, active, dogma);
        var effectIds = boosterType.EffectIds.ToHashSet();
        var booster = new Eve.Dogma.DogmaItem(itemId, boosterType.TypeId, characterId, characterId, BoosterFlag,
            Eve.Dogma.ItemState.Active, effectIds);
        world.Add(booster);
        var duration = world.Get(itemId, BoosterDurationAttributeId);
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("This booster has no valid SDE duration.")));
        }
        var sideEffects = new List<int>();
        foreach (var effectId in boosterType.EffectIds)
        {
            if (dogma.Effect(effectId) is not { ChanceAttributeId: > 0 } effect)
            {
                continue;
            }
            var chance = world.Get(itemId, effect.ChanceAttributeId);
            chance = chance > 1d ? chance / 100d : chance;
            if (!double.IsFinite(chance) || chance <= 0)
            {
                continue;
            }
            var roll = nextBoosterRoll?.Invoke() ?? Random.Shared.NextDouble();
            if (double.IsFinite(roll) && roll < Math.Clamp(chance, 0d, 1d))
            {
                sideEffects.Add(effectId);
            }
        }
        return (duration, sideEffects);
    }

    public async ValueTask<bool?> TryActivateBoosterAsync(
        long characterId,
        long itemId,
        string effectName,
        long? stationId,
        long? shipId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || itemId <= 0)
        {
            return null;
        }
        var candidate = await items.GetAsync(itemId, cancellationToken);
        if (candidate is null)
        {
            return null;
        }
        await dogmaStatic.EnsureTypesAsync([candidate.TypeId], cancellationToken);
        var candidateType = dogmaStatic.Type(candidate.TypeId);
        if (candidateType is null || !IsBoosterType(candidateType) ||
            !candidateType.EffectIds
                .Select(effectId => dogmaStatic.Effect(effectId))
                .Any(effect => effect is not null &&
                    (string.Equals(effect.Name, effectName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(effect.Guid, effectName, StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }
        if (candidate.OwnerId != characterId)
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("This booster is not in your inventory.")));
        }

        var activation = await store.InTransactionAsync(async (connection, transaction, ct) =>
        {
            await store.LockActiveSkillBoostersAsync(connection, transaction, characterId, ct);
            var nowMs = nowUnixMs();
            var nowFileTime = Py.FileTimeFromUnixMs(nowMs);
            await PruneExpiredBoosterRowsAsync(store, items, connection, transaction, characterId, nowFileTime, ct);
            var state = await ReadLockedAugmentationStateAsync(
                items, dogmaStatic, store, connection, transaction, characterId, nowMs, nowFileTime, nextBoosterRoll, ct);

            var item = await items.LockAndGetAsync(connection, transaction, itemId, ct);
            if (item is null || item.OwnerId != characterId || item.TypeId != candidate.TypeId)
            {
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("The booster item changed before activation.")));
            }
            if (item.LocationId == characterId && item.Flag == BoosterFlag && state.Active.ContainsKey(item.Id))
            {
                return (Accepted: true, ExistingTypeName: (string?)null);
            }
            if (!((stationId == item.LocationId && item.Flag == HangarFlag) ||
                  (shipId == item.LocationId && item.Flag == CargoFlag)))
            {
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Consume boosters from your active ship's cargo or a station hangar.")));
            }

            foreach (var activeBooster in state.Active.Values)
            {
                var activeType = dogmaStatic.Type(activeBooster.BoosterTypeId);
                if (activeType?.Attributes.TryGetValue(BoosterSlotAttributeId, out var activeSlot) == true &&
                    candidateType.Attributes.TryGetValue(BoosterSlotAttributeId, out var requestedSlot) && activeSlot == requestedSlot)
                {
                    return (Accepted: false, ExistingTypeName: activeType.Name);
                }
            }

            var outcome = RollBoosterEffects(
                characterId, item.Id, candidateType, state.Inventory, state.Skills, state.Active, dogmaStatic, nextBoosterRoll);
            var expiryFileTime = Py.FileTimeFromUnixMs(checked(nowMs + (long)Math.Ceiling(outcome.DurationMs)));
            long activeItemId;
            if (item.Singleton || item.Quantity == 1)
            {
                await items.MoveAsync(connection, transaction, item.Id, characterId, BoosterFlag, ct);
                activeItemId = item.Id;
            }
            else
            {
                activeItemId = (await items.MoveStackAsync(connection, transaction, item, 1, characterId, characterId, BoosterFlag, ct)).Id;
            }
            if (!await store.TryPersistActiveBoosterAsync(
                    connection, transaction, characterId, activeItemId, item.TypeId, expiryFileTime, outcome.SideEffectIds, ct))
            {
                throw new InvalidOperationException("Booster activation state already exists for the consumed inventory item.");
            }
            return (Accepted: true, ExistingTypeName: (string?)null);
        }, cancellationToken);

        if (!activation.Accepted)
        {
            throw MachoErrors.ThrowUserError("OnlyOneBoosterActiveBody",
                ("typeName", new PyStr(candidateType.Name)),
                ("typeName2", new PyStr(activation.ExistingTypeName!)));
        }
        return true;
    }

    private static long AccountOf(ServiceCall call) => call.Session.UserId ?? 0;

    /// <summary>The character's skills and queue as they are now (what has finished is settled first).</summary>
    private async ValueTask<TrainingView?> ViewOf(ServiceCall call) =>
        CharacterOf(call) is { } id ? await training.ViewAsync(id, AccountOf(call), call.Cancellation) : null;

    private async ValueTask<IReadOnlyList<long>> AccountCharacters(ServiceCall call) =>
        call.Session.UserId is { } account ? [.. (await characters.ForAccountAsync((int)account, call.Cancellation)).Select(c => c.Id)] : [];

    [Method("GetSkills")]
    public async ValueTask<PyValue> GetSkills(ServiceCall call) =>
        await ViewOf(call) is { } view ? SkillWire.EntryDictionary(CharacterOf(call)!.Value, catalog, view.Skills, view.InTraining) : None;

    /// <summary>The skills including lapsed ones (a character that let its subscription lapse): the same until subscriptions exist.</summary>
    [Method("GetAllSkills")]
    public ValueTask<PyValue> GetAllSkills(ServiceCall call) => GetSkills(call);

    [Method("GetSkillPoints")]
    public async ValueTask<PyValue> GetSkillPoints(ServiceCall call) => await ViewOf(call) is { } view ? new PyInt(view.TotalPoints) : None;

    [Method("GetFreeSkillPoints")]
    public async ValueTask<PyValue> GetFreeSkillPoints(ServiceCall call) =>
        new PyInt(CharacterOf(call) is { } id ? await store.FreeSkillPointsAsync(id, call.Cancellation) : 0L);

    [Method("GetSkillQueue")]
    public async ValueTask<PyValue> GetSkillQueue(ServiceCall call) => SkillNotifications.Queue((await ViewOf(call))?.Queue ?? []);

    [Method("GetMySkillQueue")]
    public ValueTask<PyValue> GetMySkillQueue(ServiceCall call) => GetSkillQueue(call);

    [Method("GetSkillQueueAndFreePoints")]
    public async ValueTask<PyValue> GetSkillQueueAndFreePoints(ServiceCall call)
    {
        var queue = (await ViewOf(call))?.Queue ?? [];
        var freePoints = CharacterOf(call) is { } id ? await store.FreeSkillPointsAsync(id, call.Cancellation) : 0L;
        return Py.Tuple(SkillNotifications.Queue(queue), new PyInt(freePoints));
    }

    /// <summary>
    /// The learning attributes. The client works its training-time estimates out from them, so they carry the server's training speed (the
    /// reference does the same; at the game's own speed they are what they are).
    /// </summary>
    [Method("GetAttributes")]
    public PyValue GetAttributes(ServiceCall call)
    {
        var character = CharacterOf(call) ?? 0;
        return new PyDict([.. AttributeIds.Select(id => new KeyValuePair<PyValue, PyValue>(new PyInt(id), Py.Number(training.Settings.Attribute(character, id) * training.Settings.Speed)))]);
    }

    [Method("GetCharacterAttributeModifiers")]
    public async ValueTask<PyValue> GetCharacterAttributeModifiers(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return new PyList([]);
        }
        var attributeId = IdOf(call.Arg(0));
        var snapshot = await readAugmentations(id, call.Cancellation);
        return new PyList([.. snapshot.AttributeModifiers
            .Where(modifier => attributeId is null || modifier.AttributeId == attributeId)
            .Select(modifier => (PyValue)Py.Tuple(
                new PyLong(modifier.ItemId),
                new PyInt(modifier.TypeId),
                new PyInt(modifier.Operation),
                Py.Number(modifier.Value)))]);
    }

    [Method("GetBoosters")]
    public async ValueTask<PyValue> GetBoosters(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return new PyDict([]);
        }

        var snapshot = await readAugmentations(id, call.Cancellation);
        var active = snapshot.ActiveBoosters
            .Where(booster => booster.ExpiryTimeFileTime > Py.FileTimeFromUnixMs(nowUnixMs()))
            .ToDictionary(booster => booster.ItemId);
        var installed = await items.InLocationAsync(id, id, call.Cancellation);
        var result = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var item in installed)
        {
            if (item.Flag != BoosterFlag ||
                !active.TryGetValue(item.Id, out var booster) || booster.BoosterTypeId != item.TypeId)
            {
                continue;
            }

            result.Add(new KeyValuePair<PyValue, PyValue>(
                new PyInt(booster.BoosterTypeId),
                Py.KeyVal(
                    ("boosterTypeID", new PyInt(booster.BoosterTypeId)),
                    ("expiryTime", new PyLong(booster.ExpiryTimeFileTime)),
                    ("sideEffectIDs", new PyList([.. booster.SideEffectIds.Select(effect => (PyValue)new PyInt(effect))])))));
        }
        return new PyDict(result);
    }

    [Method("GetImplants")]
    public async ValueTask<PyValue> GetImplants(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return new PyDict([]);
        }

        var installed = await items.InLocationAsync(id, id, call.Cancellation);
        await dogmaStatic.EnsureTypesAsync(installed.Select(item => item.TypeId), call.Cancellation);
        var result = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var item in installed)
        {
            if (item.Flag != ImplantFlag)
            {
                continue;
            }

            var type = dogmaStatic.Type(item.TypeId);
            if (type is null || type.CategoryId != ImplantCategoryId ||
                !type.Attributes.TryGetValue(ImplantSlotAttributeId, out var slot) ||
                !double.IsFinite(slot) || slot <= 0 || slot > int.MaxValue || slot != Math.Truncate(slot))
            {
                continue;
            }

            result.Add(new KeyValuePair<PyValue, PyValue>(
                new PyInt((int)slot),
                Py.KeyVal(
                    ("typeID", new PyInt(item.TypeId)),
                    ("itemID", new PyLong(item.Id)))));
        }
        return new PyDict(result);
    }
    [Method("GetSkillHistory")]
    public async ValueTask<PyValue> GetSkillHistory(ServiceCall call) =>
        CharacterOf(call) is { } id ? SkillNotifications.History(await training.HistoryAsync(id, call.Cancellation)) : new PyList([]);

    [Method("GetRespecInfo")]
    public PyValue GetRespecInfo(ServiceCall call) => Py.KeyVal(("freeRespecs", new PyInt(FreeRespecs)), ("lastRespecDate", None), ("nextTimedRespec", None));

    [Method("GetFreeSkillPointsAppliedToQueue")]
    public async ValueTask<PyValue> GetFreeSkillPointsAppliedToQueue(ServiceCall call) =>
        CharacterOf(call) is { } id
            ? SkillPointMap(await training.FreeSkillPointsAppliedAsync(id, AccountOf(call), null, call.Cancellation))
            : new PyDict([]);

    [Method("GetFreeSkillPointsAppliedToSkills")]
    public async ValueTask<PyValue> GetFreeSkillPointsAppliedToSkills(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return new PyDict([]);
        }

        var selectedSkills = SkillPointTargets(call.Arg(0));
        return selectedSkills.Count == 0
            ? new PyDict([])
            : SkillPointMap(await training.FreeSkillPointsAppliedAsync(id, AccountOf(call), selectedSkills, call.Cancellation));
    }

    [Method("ApplyFreeSkillPointsToQueue")]
    public async ValueTask<PyValue> ApplyFreeSkillPointsToQueue(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return None;
        }
        var (_, remaining) = await training.ApplyFreeSkillPointsAsync(id, AccountOf(call), null, call.Cancellation);
        return new PyInt(remaining);
    }

    [Method("ApplyFreeSkillPointsToSkills")]
    public async ValueTask<PyValue> ApplyFreeSkillPointsToSkills(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return None;
        }
        var selectedSkills = SkillPointTargets(call.Arg(0));
        if (selectedSkills.Count == 0)
        {
            return None;
        }
        var (_, remaining) = await training.ApplyFreeSkillPointsAsync(id, AccountOf(call), selectedSkills, call.Cancellation);
        return new PyInt(remaining);
    }

    [Method("ApplyFreeSkillPoints")]
    public async ValueTask<PyValue> ApplyFreeSkillPoints(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id ||
            IdOf(call.Arg(0)) is not { } skillTypeId || skillTypeId <= 0 || skillTypeId > int.MaxValue ||
            IdOf(call.Arg(1)) is not { } pointsToApply || pointsToApply <= 0)
        {
            return None;
        }
        return await training.ApplyFreeSkillPointAmountAsync(id, AccountOf(call), (int)skillTypeId, pointsToApply, call.Cancellation)
            is { } remaining ? new PyInt(remaining) : None;
    }

    private IReadOnlyList<(int SkillTypeId, int TargetLevel)> SkillPointTargets(PyValue value)
    {
        var targets = new List<(int SkillTypeId, int TargetLevel)>();
        foreach (var raw in ItemsOf(value))
        {
            var pair = ItemsOf(raw);
            if (pair.Count != 2 ||
                IdOf(pair[0]) is not { } typeId || typeId <= 0 || typeId > int.MaxValue ||
                IdOf(pair[1]) is not { } targetLevel || targetLevel is < 1 or > 5 ||
                catalog.Get((int)typeId) is null)
            {
                continue;
            }

            targets.Add(((int)typeId, (int)targetLevel));
        }
        return targets;
    }

    private static PyDict SkillPointMap(IReadOnlyDictionary<int, long> pointsBySkillType) =>
        new([.. pointsBySkillType.OrderBy(entry => entry.Key)
            .Select(entry => new KeyValuePair<PyValue, PyValue>(new PyInt(entry.Key), new PyInt(entry.Value)))]);

    /// <summary>Everything the skill window opens with, in one call.</summary>
    [Method("GetMySkillInfo")]
    public async ValueTask<PyValue> GetMySkillInfo(ServiceCall call)
    {
        if (await ViewOf(call) is not { } view || CharacterOf(call) is not { } characterId)
        {
            return None;
        }
        var freePoints = await store.FreeSkillPointsAsync(characterId, call.Cancellation);
        return Py.KeyVal(
            ("skills", SkillWire.EntryDictionary(characterId, catalog, view.Skills, view.InTraining)),
            ("skillPoints", new PyInt(view.TotalPoints)),
            ("freeSkillPoints", new PyInt(freePoints)),
            ("queue", SkillNotifications.Queue(view.Queue)));
    }

    [Method("IsSkillInjected")]
    public async ValueTask<PyValue> IsSkillInjected(ServiceCall call) =>
        CharacterOf(call) is { } id && call.Arg(0) is PyInt type
            ? PyValue.Of((await store.ForCharacterAsync(id, call.Cancellation)).Any(s => s.TypeId == type.Value))
            : PyBool.False;

    [Method("IsSkill")]
    public PyValue IsSkill(ServiceCall call) => PyValue.Of(call.Arg(0) is PyInt type && type.Value is >= 0 and <= int.MaxValue && catalog.Get((int)type.Value) is not null);

    /// <summary>
    /// <c>SaveNewQueue(queue, activate=True)</c>: replaces the queue. It is a dict of position to <c>(skill, level)</c>. The client is told
    /// the queue as it was kept; a queue the rules refuse is answered with the message the client shows.
    /// </summary>
    [Method("SaveNewQueue")]
    public async ValueTask<PyValue> SaveNewQueue(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return None;
        }
        var activate = call.Kwarg("activate") is { } given ? JsSemantics.Truthy(given) : true;
        var outcome = await training.SaveQueueAsync(id, AccountOf(call), QueueInput(call.Arg(0)), activate, await AccountCharacters(call), call.Cancellation);
        return await Answer(call, outcome);
    }

    /// <summary><c>CharStartTrainingSkillByTypeID(typeID, toLevel=None)</c>: puts a skill first in the queue and runs it.</summary>
    [Method("CharStartTrainingSkillByTypeID")]
    public async ValueTask<PyValue> CharStartTrainingSkillByTypeID(ServiceCall call) => await StartAsync(call, call.Arg(0), call.Arg(1));

    /// <summary>
    /// <c>CharStartTrainingSkill(itemID, toLevel=None)</c>: the same for a skill named by its item id (the character's id and the type, see
    /// <see cref="SkillWire.ItemId"/>). An item that is not one of the character's skills starts nothing.
    /// </summary>
    [Method("CharStartTrainingSkill")]
    public async ValueTask<PyValue> CharStartTrainingSkill(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id || IdOf(call.Arg(0)) is not { } item || item / 100_000 != id)
        {
            return None;
        }
        return await StartAsync(call, new PyInt(item % 100_000), call.Arg(1));
    }

    private async ValueTask<PyValue> StartAsync(ServiceCall call, PyValue type, PyValue level)
    {
        if (CharacterOf(call) is not { } id || IdOf(type) is not { } typeId || typeId > int.MaxValue)
        {
            return None;
        }
        var toLevel = IdOf(level) is { } asked and > 0 and <= int.MaxValue ? (int)asked : 0;
        var outcome = await training.StartAsync(id, AccountOf(call), (int)typeId, toLevel, await AccountCharacters(call), call.Cancellation);
        return outcome is null ? None : await Answer(call, outcome);
    }

    private async ValueTask<PyValue> Answer(ServiceCall call, SaveOutcome outcome)
    {
        if (outcome is not QueueSaved saved)
        {
            throw SkillErrors.Of(outcome);
        }
        await announcer.AnnounceSavedAsync(call.Session, saved);
        return None;
    }

    /// <summary><c>CharStopTrainingSkill()</c>: the same as <c>AbortTraining()</c>.</summary>
    [Method("CharStopTrainingSkill")]
    public ValueTask<PyValue> CharStopTrainingSkill(ServiceCall call) => AbortTraining(call);

    /// <summary><c>AbortTraining()</c>: stops the running skill; the queue stays and can be started again.</summary>
    [Method("AbortTraining")]
    public async ValueTask<PyValue> AbortTraining(ServiceCall call)
    {
        if (CharacterOf(call) is { } id && await training.AbortAsync(id, AccountOf(call), call.Cancellation) is { } update)
        {
            await announcer.AnnounceStoppedAsync(call.Session, update);
        }
        return None;
    }

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal real when real.Value == Math.Floor(real.Value) => (long)real.Value,
        _ => null,
    };

    /// <summary>
    /// The lines the client sent, in queue order: a dict of position to <c>(skill, level)</c> (a list is taken as positions from 0). A line
    /// that names no skill or no level, or a negative position, is left out.
    /// </summary>
    private static List<QueueEntry> QueueInput(PyValue queue)
    {
        var lines = new List<(long Position, QueueEntry Entry)>();
        void Add(long position, PyValue value)
        {
            var (type, level) = value switch
            {
                PyTuple { Items.Count: >= 2 } pair => (IdOf(pair.Items[0]), IdOf(pair.Items[1])),
                PyList { Items.Count: >= 2 } pair => (IdOf(pair.Items[0]), IdOf(pair.Items[1])),
                PyDict dict => (Named(dict, "trainingTypeID", "typeID"), Named(dict, "trainingToLevel", "toLevel")),
                _ => (null, null),
            };
            if (position >= 0 && type is > 0 and <= int.MaxValue && level is > 0 and <= int.MaxValue)
            {
                lines.Add((position, new QueueEntry((int)type, (int)level)));
            }
        }
        switch (queue)
        {
            case PyDict dict:
                foreach (var (key, value) in dict.Entries)
                {
                    Add(IdOf(key) ?? -1, value);
                }
                break;
            case PyList list:
                for (var i = 0; i < list.Items.Count; i++)
                {
                    Add(i, list.Items[i]);
                }
                break;
            case PyTuple tuple:
                for (var i = 0; i < tuple.Items.Count; i++)
                {
                    Add(i, tuple.Items[i]);
                }
                break;
        }
        return [.. lines.OrderBy(l => l.Position).Select(l => l.Entry)];
    }

    private static long? Named(PyDict dict, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var (key, value) in dict.Entries)
            {
                if (JsSemantics.StringValue(key) == name)
                {
                    return IdOf(value);
                }
            }
        }
        return null;
    }

    /// <summary>The proxy node that serves this object: the one the client is connected to.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>
    /// Binds the caller to this handler: <c>(bindParams, (method, args, kwargs))</c> answers <c>(bound object, result of the
    /// nested call)</c>, so the client's first call and its bind are one round trip.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = Py.Tuple(new PyStr($"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}"), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())));
        PyValue result = None;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = nested.Items.Count > 1 && nested.Items[1] is PyTuple or PyList ? ItemsOf(nested.Items[1]) : [],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : None,
                Session = call.Session,
                CallId = call.CallId,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(new PySubStruct(new PySubStream(oid)), result);
    }

    private static IReadOnlyList<PyValue> ItemsOf(PyValue value) => value switch
    {
        PyTuple tuple => tuple.Items,
        PyList list => list.Items,
        _ => [],
    };
}
