using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Skills;
using Eve.Ships;
using Npgsql;

namespace Eve.Services.Skills;

/// <summary>Serves character skills, augmentations, booster activation, and free skill point operations.</summary>
[Service("skillHandler", Aliases = ["skillMgr"])]
public sealed partial class SkillHandlerService(
    PgSkillStore store, SkillCatalog catalog, CharacterTraining training, PgCharacterStore characters, Eve.Inventory.PgItemStore items, Eve.Dogma.DogmaStatic dogmaStatic, TrainingAnnouncer announcer, Func<long> nowUnixMs,
    SkillAugmentationReader readAugmentations, Func<double>? nextBoosterRoll = null, Eve.Wallet.WalletTransactions? walletTransactions = null, Func<long, bool>? isAlphaClone = null, ExpertSystems? expertSystems = null)
{
    /// <summary>The durable job that ends an activated booster at its expiry (set by the Host; null where no scheduler runs).</summary>
    public SkillBoosterLapse? BoosterLapse { get; set; }

    /// <summary>The five attributes a character trains with (charisma, intelligence, memory, perception, willpower).</summary>
    private static readonly int[] AttributeIds = [164, 165, 166, 167, 168];

    private const int BoosterFlag = 88;
    private const int ImplantFlag = 89;
    private const int ImplantCategoryId = 20;
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

    internal static async Task PruneExpiredBoosterRowsAsync(
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

    /// <summary>A cerebral accelerator is the booster of booster slot 10 (wiki: they share it, apart from all other boosters); it stays active through a clone jump or a pod death.</summary>
    public const int CerebralAcceleratorSlot = 10;

    public static bool IsCerebralAccelerator(Eve.Dogma.TypeDef? type) =>
        type is not null && type.Attributes.TryGetValue(BoosterSlotAttributeId, out var slot) && slot == CerebralAcceleratorSlot;

    private static bool IsBoosterType(Eve.Dogma.TypeDef? type) =>
        type is { CategoryId: ImplantCategoryId } &&
        type.Attributes.TryGetValue(BoosterSlotAttributeId, out var slot) &&
        double.IsFinite(slot) && slot > 0 && slot <= int.MaxValue && slot == Math.Truncate(slot);

/// <summary>The smuggling chance of a pilot (dogma attribute 445 after its implant and skill modifiers, EVE.js resolveCharacterSmugglingChance), from 0 to 1.</summary>
    public static async Task<double> SmugglingChanceAsync(
        Eve.Inventory.PgItemStore itemStore, Eve.Dogma.DogmaStatic dogma, PgSkillStore skillStore, long characterId, CancellationToken cancellationToken)
    {
        var implants = (await itemStore.InLocationAsync(characterId, characterId, cancellationToken))
            .Where(item => item.Flag == ImplantFlag && item.OwnerId == characterId)
            .ToList();
        var trained = await skillStore.ForCharacterAsync(characterId, cancellationToken);
        await dogma.EnsureTypesAsync(implants.Select(item => item.TypeId).Concat(trained.Select(skill => skill.TypeId)), cancellationToken);
        var (world, _) = BuildAugmentationWorld(characterId, implants, trained, new Dictionary<long, StoredSkillBooster>(), dogma);
        return Math.Clamp(world.Get(characterId, SmugglingChanceAttributeId), 0d, 1d);
    }

    /// <summary>The dogma attribute smugglingChance (445).</summary>
    public const int SmugglingChanceAttributeId = 445;

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
            if (Eve.Ships.PilotAugmentations.ModelOf(dogma, item, characterId, active) is not { } model)
            {
                continue;
            }
            world.Add(model);
            dogmaItems[item.Id] = model;
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
        var use = await ConsumeAsync(characterId, candidate, candidateType, stationId, shipId, cancellationToken);
        await announcer.AnnounceLearningChangedAsync(characterId, use.Training);
        return true;
    }

    /// <summary>
    /// <c>UseBooster(itemID, locationID)</c>, the client's Consume: the booster is used from a station hangar or the cargo of the active ship (EVE
    /// University wiki, Boosters: "right click on it and select Consume"). Returns what changed in the inventory and the training it rebased, for the
    /// client to be told (the caller announces them).
    /// </summary>
    public async Task<(IReadOnlyList<ItemChanged> Changes, TrainingUpdate? Training)> UseBoosterAsync(
        long characterId,
        long itemId,
        long? locationId,
        long? stationId,
        long? shipId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await items.GetAsync(itemId, cancellationToken);
        if (candidate is null || candidate.OwnerId != characterId || (locationId is { } location && location != candidate.LocationId))
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That booster is no longer available.")));
        }
        await dogmaStatic.EnsureTypesAsync([candidate.TypeId], cancellationToken);
        if (dogmaStatic.Type(candidate.TypeId) is not { } candidateType || !IsBoosterType(candidateType))
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That item cannot be consumed as a booster.")));
        }
        return await ConsumeAsync(characterId, candidate, candidateType, stationId, shipId, cancellationToken);
    }

    /// <summary>
    /// Takes a booster into its slot and returns what changed in the inventory. The running skill is rebased at the rate it has first, so the booster's
    /// attributes apply from now on; the client is told that change. A booster for an occupied slot is refused (the wiki: a slot holds one active booster).
    /// </summary>
    private async Task<(IReadOnlyList<ItemChanged> Changes, TrainingUpdate? Training)> ConsumeAsync(
        long characterId, Eve.Inventory.Item candidate, Eve.Dogma.TypeDef candidateType, long? stationId, long? shipId, CancellationToken cancellationToken)
    {
        TrainingUpdate? rebased = null;
        if (await characters.GetAsync(characterId, cancellationToken) is { } owner)
        {
            rebased = await training.RebaseRunningAsync(characterId, owner.AccountId, cancellationToken);
        }
        var activation = await store.InTransactionAsync(async (connection, transaction, ct) =>
        {
            await store.LockActiveSkillBoostersAsync(connection, transaction, characterId, ct);
            var nowMs = nowUnixMs();
            var nowFileTime = Py.FileTimeFromUnixMs(nowMs);
            await PruneExpiredBoosterRowsAsync(store, items, connection, transaction, characterId, nowFileTime, ct);
            var state = await ReadLockedAugmentationStateAsync(
                items, dogmaStatic, store, connection, transaction, characterId, nowMs, nowFileTime, nextBoosterRoll, ct);

            var item = await items.LockAndGetAsync(connection, transaction, candidate.Id, ct);
            if (item is null || item.OwnerId != characterId || item.TypeId != candidate.TypeId)
            {
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("The booster item changed before activation.")));
            }
            if (item.LocationId == characterId && item.Flag == BoosterFlag && state.Active.ContainsKey(item.Id))
            {
                return (Accepted: true, ExistingTypeName: (string?)null, Changes: new List<ItemChanged>());
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
                    return (Accepted: false, ExistingTypeName: activeType.Name, Changes: new List<ItemChanged>());
                }
            }

            var outcome = RollBoosterEffects(
                characterId, item.Id, candidateType, state.Inventory, state.Skills, state.Active, dogmaStatic, nextBoosterRoll);
            var expiryFileTime = Py.FileTimeFromUnixMs(checked(nowMs + (long)Math.Ceiling(outcome.DurationMs)));
            var changes = new List<ItemChanged>();
            long activeItemId;
            if (item.Singleton || item.Quantity == 1)
            {
                await items.MoveAsync(connection, transaction, item.Id, characterId, BoosterFlag, ct);
                activeItemId = item.Id;
                changes.Add(new ItemChanged(item with { LocationId = characterId, Flag = BoosterFlag }, item.LocationId, item.Flag, item.Quantity));
            }
            else
            {
                var moved = await items.MoveStackAsync(connection, transaction, item, 1, characterId, characterId, BoosterFlag, ct);
                activeItemId = moved.Id;
                changes.Add(new ItemChanged(item with { Quantity = item.Quantity - 1 }, item.LocationId, item.Flag, item.Quantity));
                changes.Add(new ItemChanged(moved, item.LocationId, item.Flag, 0));
            }
            if (!await store.TryPersistActiveBoosterAsync(
                    connection, transaction, characterId, activeItemId, item.TypeId, expiryFileTime, outcome.SideEffectIds, ct))
            {
                throw new InvalidOperationException("Booster activation state already exists for the consumed inventory item.");
            }
            if (BoosterLapse is not null)
                await BoosterLapse.ScheduleAsync(connection, transaction, characterId, activeItemId, expiryFileTime, ct);
            return (Accepted: true, ExistingTypeName: (string?)null, Changes: changes);
        }, cancellationToken);

        if (!activation.Accepted)
        {
            throw MachoErrors.ThrowUserError("OnlyOneBoosterActiveBody",
                ("typeName", new PyStr(candidateType.Name)),
                ("typeName2", new PyStr(activation.ExistingTypeName!)));
        }
        return (activation.Changes, rebased);
    }

    private static long AccountOf(ServiceCall call) => call.Session.UserId ?? 0;

    /// <summary>The character's skills and queue as they are now (what has finished is settled first).</summary>
    private async ValueTask<TrainingView?> ViewOf(ServiceCall call) =>
        CharacterOf(call) is { } id ? await training.ViewAsync(id, AccountOf(call), call.Cancellation) : null;

    private async ValueTask<IReadOnlyList<long>> AccountCharacters(ServiceCall call) =>
        call.Session.UserId is { } account ? [.. (await characters.ForAccountAsync((int)account, call.Cancellation)).Select(c => c.Id)] : [];

    /// <summary>
    /// <c>CheckAndSendNotifications()</c>: the reference answers an empty list and sends nothing (EveJS <c>skillMgrService.js</c> 594).
    /// The client calls it only from a development provider (eve-logic <c>skillHistoryProvider.py</c>).
    /// </summary>
    [Method("CheckAndSendNotifications")]
    public PyValue CheckAndSendNotifications(ServiceCall _) => new PyList([]);

    /// <summary>
    /// <c>GetSkillBundleInfo(skillBundleID)</c>: the client unpacks <c>name, icon</c> for the crate's loot line (eve-logic
    /// <c>crate/lootItem.py</c> 87). No bundle name or icon exists in the SDE, EveJS or eve-logic, so the answer is the client's
    /// empty shape: an empty label and no icon (the client then falls back to its default icon). Recorded in DEVIATIONS.
    /// </summary>
    [Method("GetSkillBundleInfo")]
    public PyValue GetSkillBundleInfo(ServiceCall _) => Py.Tuple(new PyWString(string.Empty), PyNone.Instance);

    [Method("GetSkills")]
    public async ValueTask<PyValue> GetSkills(ServiceCall call)
    {
        if (await ViewOf(call) is not { } view)
        {
            return None;
        }
        var characterId = CharacterOf(call)!.Value;
        // the levels the installed expert systems grant show in the skills; the training itself runs from the trained levels
        var grants = expertSystems is null ? new Dictionary<int, int>() : await expertSystems.GrantsAsync(characterId, call.Cancellation);
        return SkillWire.EntryDictionary(characterId, catalog, ExpertSystemRules.Effective(view.Skills, grants), view.InTraining);
    }

    /// <summary>The skills including lapsed ones (a character that let its subscription lapse): the same until subscriptions exist.</summary>
    [Method("GetAllSkills")]
    public ValueTask<PyValue> GetAllSkills(ServiceCall call) => GetSkills(call);

    [Method("GetSkillPoints")]
    public async ValueTask<PyValue> GetSkillPoints(ServiceCall call) => await ViewOf(call) is { } view ? new PyInt(view.TotalPoints) : None;

    [Method("GetFreeSkillPoints")]
    public async ValueTask<PyValue> GetFreeSkillPoints(ServiceCall call) =>
        new PyInt(CharacterOf(call) is { } id ? await store.FreeSkillPointsAsync(id, call.Cancellation) : 0L);


    [Method("GetSkillChangesForISIS")]
    public async ValueTask<PyValue> GetSkillChangesForISIS(ServiceCall call)
    {
        if (CharacterOf(call) is not { } character)
            return new PyList([]);
        var changes = await store.ConsumeRecentSkillPointChangesAsync(character, call.Cancellation);
        return new PyList([.. changes.Select(change =>
            (PyValue)Py.Tuple(new PyInt(change.TypeId), new PyInt(change.PointChange)))]);
    }

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
    public async ValueTask<PyValue> GetAttributes(ServiceCall call)
    {
        var character = CharacterOf(call);
        if (character is { } id)
            await store.NeuralRemapAsync(id, call.Cancellation);
        var owner = character ?? 0;
        return new PyDict([.. AttributeIds.Select(id => new KeyValuePair<PyValue, PyValue>(new PyInt(id), Py.Number(training.Settings.Attribute(owner, id) * training.Settings.Speed)))]);
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
                !type.Attributes.TryGetValue(Eve.Ships.PilotAugmentations.ImplantSlotAttributeId, out var slot) ||
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
    public async ValueTask<PyValue> GetRespecInfo(ServiceCall call)
    {
        var remap = CharacterOf(call) is { } id
            ? await store.NeuralRemapAsync(id, call.Cancellation)
            : CharacterNeuralRemap.Default;
        return Py.KeyVal(
            ("freeRespecs", new PyInt(remap.FreeRespecs)),
            ("lastRespecDate", remap.LastRespecFileTime is { } last ? new PyLong(last) : None),
            ("nextTimedRespec", remap.NextTimedRespecFileTime is { } next ? new PyLong(next) : None));
    }

    [Method("RespecCharacter")]
    public async ValueTask<PyValue> RespecCharacter(ServiceCall call)
    {
        if (CharacterOf(call) is not { } characterId)
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Select a character before remapping attributes.")));

        var attributes = NeuralAttributesOf(call.Arg(0));
        TrainingUpdate? update;
        try
        {
            update = await training.RespecAsync(characterId, call.Session.UserId ?? 0, attributes, call.Cancellation);
        }
        catch (NeuralRemapCooldownException cooldown)
        {
            throw MachoErrors.ThrowUserError("RespecTooSoon", ("nextTime", new PyLong(cooldown.NextAvailableFileTime)));
        }
        if (update is not null)
        {
            await announcer.AnnounceRemappedAsync(call.Session, update);
            SkillNotifications.RespecInfoChanged(call.Session);
        }
        return PyNone.Instance;
    }

    private static IReadOnlyDictionary<int, int> NeuralAttributesOf(PyValue value)
    {
        if (value is not PyDict fields)
            throw MachoErrors.ThrowUserError("RespecAttributesMisallocated");

        var attributes = new Dictionary<int, int>();
        foreach (var (rawId, rawValue) in fields.Entries)
        {
            if (IdOf(rawId) is not { } id || id is < int.MinValue or > int.MaxValue ||
                IdOf(rawValue) is not { } number || number is < int.MinValue or > int.MaxValue ||
                !AttributeIds.Contains((int)id) || !attributes.TryAdd((int)id, (int)number))
                throw MachoErrors.ThrowUserError("RespecAttributesMisallocated");
        }
        if (attributes.Count != AttributeIds.Length)
            throw MachoErrors.ThrowUserError("RespecAttributesMisallocated");
        if (attributes.Values.Any(number => number < CharacterNeuralRemap.MinimumAttributeValue))
            throw MachoErrors.ThrowUserError("RespecAttributesTooLow");
        if (attributes.Values.Any(number => number > CharacterNeuralRemap.MaximumAttributeValue))
            throw MachoErrors.ThrowUserError("RespecAttributesTooHigh");
        if (attributes.Values.Sum() != CharacterNeuralRemap.TotalRemapPoints)
            throw MachoErrors.ThrowUserError("RespecAttributesMisallocated");
        return attributes;
    }

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
}
