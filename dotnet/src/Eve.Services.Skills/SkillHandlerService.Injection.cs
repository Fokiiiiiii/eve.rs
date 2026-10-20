using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills;

public sealed partial class SkillHandlerService
{
    private const int MaximumSkillOperationEntries = 500;

    [Method("InjectSkillIntoBrain")]
    public async ValueTask<PyValue> InjectSkillIntoBrain(ServiceCall call)
    {
        var itemIds = ItemsOf(call.Arg(0)).Take(MaximumSkillOperationEntries + 1)
            .Select(IdOf).OfType<long>().ToArray();
        if (itemIds.Length > MaximumSkillOperationEntries)
            throw MachoErrors.ThrowUserError("SkillBulkLimitExceeded");
        return await InjectSkillBooksAsync(call.Session, itemIds, call.Cancellation);
    }

    public async Task<PyValue> InjectSkillBooksAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count > MaximumSkillOperationEntries)
            throw MachoErrors.ThrowUserError("SkillBulkLimitExceeded");
        if (session.CharacterId is not { } character || session.GetInt("stationid") is not { } station)
            throw MachoErrors.ThrowUserError("SkillExtractorNotDockedInStation");
        var shipId = session.GetInt("shipid");
        var requested = itemIds.Distinct().ToArray();
        if (requested.Length == 0) return None;
        var injected = await store.InTransactionAsync(async (connection, transaction, token) =>
        {
            await using (var characterLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
            {
                characterLock.Parameters.Add(new NpgsqlParameter { Value = $"skill-inject:{character}" });
                await characterLock.ExecuteScalarAsync(token);
            }
            var current = (await store.ForCharacterAsync(connection, transaction, character, token)).Select(skill => skill.TypeId).ToHashSet();
            var result = new List<int>();
            foreach (var itemId in requested)
            {
                if (await items.LockAndGetAsync(connection, transaction, itemId, token) is not { } item
                    || item.OwnerId != character
                    || (item.LocationId != station && !(shipId is { } ship && item.LocationId == ship && item.Flag == CargoFlag))
                    || catalog.Get(item.TypeId) is not { Published: true } skill)
                    throw MachoErrors.ThrowUserError("ItemNotASkill", ("skillName", new PyLong(itemId)));
                if (current.Contains(skill.TypeId))
                    throw MachoErrors.ThrowUserError("CharacterAlreadyKnowsSkill", ("skillName", new PyInt(skill.TypeId)));
                await store.GrantAsync(connection, transaction, character, [new CharacterSkill(skill.TypeId, 0, 0)], token);
                if (item.Singleton || item.Quantity <= 1)
                    await items.DeleteAsync(connection, transaction, itemId, token);
                else
                    await items.SetQuantityAsync(connection, transaction, itemId, item.Quantity - 1, token);
                current.Add(skill.TypeId);
                result.Add(skill.TypeId);
            }
            return result;
        }, cancellationToken);
        return new PyList([.. injected.Select(typeId => (PyValue)new PyInt(typeId))]);
    }

private long DiminishedInjectorSp(long totalSp, int typeId, long quantity, long nonDiminishing)
    {
        var dailyAlpha = catalog.ItemTypeId("Daily Alpha Injector");
        if (dailyAlpha == typeId) return quantity == 1 ? 50_000L : 0L;
        var large = catalog.ItemTypeId("Large Skill Injector");
        var small = catalog.ItemTypeId("Small Skill Injector");
        var scale = large == typeId ? 1L : small == typeId ? 5L : 0L;
        if (scale == 0 || quantity is <= 0 or > 1_000) return 0;
        var total = 0L;
        for (var i = 0L; i < quantity; i++)
        {
            var gained = nonDiminishing > 0 ? 500_000L
                : totalSp < 5_000_000 ? 500_000L
                : totalSp < 50_000_000 ? 400_000L
                : totalSp < 80_000_000 ? 300_000L
                : 150_000L;
            if (scale > 1) gained /= scale;
            if (nonDiminishing > 0) nonDiminishing--;
            totalSp = checked(totalSp + gained);
            total = checked(total + gained);
        }
        return total;
    }

    private bool IsSkillInjectorType(int typeId) =>
        catalog.ItemTypeId("Small Skill Injector") == typeId
        || catalog.ItemTypeId("Large Skill Injector") == typeId
        || catalog.ItemTypeId("Daily Alpha Injector") == typeId;

    [Method("GetDiminishedSpFromInjectors")]
    public async ValueTask<PyValue> GetDiminishedSpFromInjectors(ServiceCall call)
    {
        if (CharacterOf(call) is not { } character || IdOf(call.Arg(0)) is not { } typeId
            || IdOf(call.Arg(1)) is not { } quantity)
            return new PyInt(0);
        var allocated = (await store.TotalPointsAsync([character], call.Cancellation)).GetValueOrDefault(character);
        var free = await store.FreeSkillPointsAsync(character, call.Cancellation);
        return new PyInt(DiminishedInjectorSp(allocated + free, (int)typeId, quantity, IdOf(call.Arg(2)) ?? 0));
    }

    [Method("CheckInjectionConstraints")]
    public async ValueTask<PyValue> CheckInjectionConstraints(ServiceCall call)
    {
        if (CharacterOf(call) is not { } character || IdOf(call.Arg(0)) is not { } itemId
            || IdOf(call.Arg(1)) is not { } quantity || quantity <= 0
            || await items.GetAsync(itemId, call.Cancellation) is not { } item
            || item.OwnerId != character || !IsSkillInjectorType(item.TypeId)
            || item.Singleton && quantity != 1 || !item.Singleton && quantity > item.Quantity)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That skill injector cannot be used.")));
        return None;
    }

    [Method("InjectSkillpoints")]
    public async ValueTask<PyValue> InjectSkillpoints(ServiceCall call)
    {
        if (CharacterOf(call) is not { } character || IdOf(call.Arg(0)) is not { } itemId
            || IdOf(call.Arg(1)) is not { } quantity || quantity <= 0)
            return None;
        var current = await items.GetAsync(itemId, call.Cancellation);
        var dailyAlphaTypeId = catalog.ItemTypeId("Daily Alpha Injector");
        if (current is null || current.OwnerId != character || !IsSkillInjectorType(current.TypeId)
            || current.Singleton && quantity != 1 || !current.Singleton && quantity > current.Quantity
            || current.TypeId == dailyAlphaTypeId
                && (quantity != 1 || isAlphaClone?.Invoke(AccountOf(call)) != true))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That skill injector cannot be used.")));

        await dogmaStatic.EnsureTypesAsync([current.TypeId], call.Cancellation);
        var type = dogmaStatic.Type(current.TypeId)
            ?? throw new InvalidOperationException("The injector's SDE type is unavailable.");
        var committed = await store.InTransactionAsync(async (connection, transaction, token) =>
        {
            await using (var characterLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
            {
                characterLock.Parameters.Add(new NpgsqlParameter { Value = $"skills:{character}" });
                await characterLock.ExecuteScalarAsync(token);
            }
            if (await items.LockAndGetAsync(connection, transaction, itemId, token) is not { } item
                || item.OwnerId != character || item.TypeId != current.TypeId
                || item.Singleton && quantity != 1 || !item.Singleton && quantity > item.Quantity)
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That skill injector cannot be used.")));
            if (item.TypeId == dailyAlphaTypeId
                && !await store.TryUseDailyAlphaSkillInjectorAsync(connection, transaction, character, item.TypeId, nowUnixMs(), token))
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("A Daily Alpha Injector can only be used once per day.")));
            var total = await store.TotalPointsAsync(connection, transaction, character, token);
            var free = await store.FreeSkillPointsAsync(connection, transaction, character, token);
            var award = DiminishedInjectorSp(total + free, item.TypeId, quantity, 0);
            if (award <= 0) throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That skill injector cannot be used.")));
            var newBalance = await store.GrantFreeSkillPointsAsync(connection, transaction, character, award, token);
            if (item.Singleton || quantity == item.Quantity)
                await items.DeleteAsync(connection, transaction, itemId, token);
            else
                await items.SetQuantityAsync(connection, transaction, itemId, item.Quantity - quantity, token);
            var before = new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag,
                item.Singleton ? -1 : item.Quantity, type.GroupId, type.CategoryId);

            var after = item.Singleton || quantity == item.Quantity
                ? before with { LocationId = 6 }
                : before with { Quantity = item.Quantity - quantity };
            return (Balance: newBalance, Award: award, Before: before, After: after);
        }, call.Cancellation);
        announcer.AnnounceInjectedPoints(call.Session, committed.Before, committed.After, committed.Balance);
        return new PyInt(committed.Award);
    }

    [Method("ExtractSkills")]
    public async ValueTask<PyValue> ExtractSkills(ServiceCall call)
    {
        if (CharacterOf(call) is not { } character
            || call.Session.GetInt("stationid") is not { } station
            || call.Session.GetInt("shipid") is not { } shipId
            || catalog.ItemTypeId("Capsule") is not { } capsuleTypeId
            || await items.GetAsync(shipId, call.Cancellation) is not { TypeId: var shipTypeId }
            || shipTypeId != capsuleTypeId)
            throw MachoErrors.ThrowUserError("SkillExtractorNotDockedInStation");

        var extractorTypeId = catalog.ItemTypeId("Skill Extractor");
        var injectorTypeId = catalog.ItemTypeId("Large Skill Injector");
        if (extractorTypeId is null || injectorTypeId is null)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Skill extraction is unavailable.")));

        Eve.Inventory.Item? extractor = null;
        var extractorArgument = -1;
        for (var index = 0; index < call.Args.Count; index++)
        {
            if (IdOf(call.Args[index]) is not { } candidateId
                || await items.GetAsync(candidateId, call.Cancellation) is not { } candidate
                || candidate.TypeId != extractorTypeId.Value)
                continue;
            extractor = candidate;
            extractorArgument = index;
            break;
        }
        if (extractor is null || extractorArgument < 0 || extractor.OwnerId != character
            || extractor.LocationId != station || extractor.Flag != HangarFlag || extractor.Quantity < 1)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That skill extractor cannot be used.")));

        var rawTargets = call.Args
            .Where((_, index) => index != extractorArgument)
            .SelectMany(ExtractionTargetsOf)
            .Take(MaximumSkillOperationEntries + 1)
            .ToArray();
        if (rawTargets.Length > MaximumSkillOperationEntries)
            throw MachoErrors.ThrowUserError("SkillBulkLimitExceeded");

        var requested = rawTargets
            .Where(target => target.SkillTypeId is >= 0 and <= int.MaxValue && target.Points > 0)
            .GroupBy(target => (int)target.SkillTypeId)
            .Select(group => (SkillTypeId: group.Key, Points: group.Aggregate(0L, (sum, target) =>
                target.Points >= long.MaxValue - sum ? long.MaxValue : sum + target.Points)))
            .ToArray();
        if (requested.Length == 0)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Select skills to extract.")));

        var alphaAccount = isAlphaClone?.Invoke(AccountOf(call)) == true;
        var alphaRaceId = 0;
        if (alphaAccount)
        {
            if (await characters.GetAsync(character, call.Cancellation) is not { RaceId: > 0 } characterRecord)
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("The character's Alpha skill limits could not be verified.")));
            alphaRaceId = characterRecord.RaceId;
        }
        var view = await training.ViewAsync(character, AccountOf(call), call.Cancellation);
        var queue = view?.Queue ?? [];
        var extractorPoints = 500_000L;
        var updated = await store.InTransactionAsync(async (connection, transaction, token) =>
        {
            await using (var characterLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
            {
                characterLock.Parameters.Add(new NpgsqlParameter { Value = $"skill-inject:{character}" });
                await characterLock.ExecuteScalarAsync(token);
            }
            if (await items.LockAndGetAsync(connection, transaction, extractor.Id, token) is not { } locked
                || locked.OwnerId != character || locked.TypeId != extractorTypeId.Value
                || locked.LocationId != station || locked.Flag != HangarFlag || locked.Quantity < 1)
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That skill extractor cannot be used.")));

            var skills = await store.ForCharacterAsync(connection, transaction, character, token);
            var byId = skills.ToDictionary(skill => skill.TypeId);
            var total = await store.TotalPointsAsync(connection, transaction, character, token);
            if (total < 5_500_000 || total - extractorPoints < 5_000_000)
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("A character must retain at least 5,000,000 trained skill points to extract skills.")));

            var deductions = new Dictionary<int, long>();
            var remaining = extractorPoints;
            foreach (var target in requested)
            {
                if (!byId.TryGetValue(target.SkillTypeId, out var skill) || skill.SkillPoints <= 0)
                    throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("A selected skill is not trained.")));
                if (queue.Any(entry => entry.TypeId == target.SkillTypeId))
                    throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("A skill in the training queue cannot be extracted.")));
                var take = Math.Min(remaining, Math.Min(skill.SkillPoints, target.Points));
                if (take > 0)
                {
                    deductions[target.SkillTypeId] = take;
                    remaining -= take;
                }
                if (remaining == 0) break;
            }
            if (remaining != 0)
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Selected skills do not contain 500,000 extractable skill points.")));

            var afterPoints = skills.ToDictionary(
                skill => skill.TypeId,
                skill => skill.SkillPoints - deductions.GetValueOrDefault(skill.TypeId));
            var selected = deductions.Keys.ToHashSet();
            var lockedSkills = new HashSet<int>();
            foreach (var dependent in skills)
            {
                if (afterPoints.GetValueOrDefault(dependent.TypeId) <= 0
                    || catalog.Get(dependent.TypeId)?.Requirements is not { } requirements)
                    continue;
                foreach (var requirement in requirements)
                    if (selected.Contains(requirement.TypeId))
                        lockedSkills.Add(requirement.TypeId);
            }
            foreach (var entry in queue)
                if (catalog.Get(entry.TypeId)?.Requirements is { } requirements)
                    foreach (var requirement in requirements)
                        if (selected.Contains(requirement.TypeId))
                            lockedSkills.Add(requirement.TypeId);
            if (deductions.Keys.Any(lockedSkills.Contains))
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("A skill required by another trained or queued skill cannot be extracted.")));

            var changed = new List<CharacterSkill>(deductions.Count);
            foreach (var (skillTypeId, points) in deductions)
            {
                var skillType = catalog.Get(skillTypeId);
                if (skillType is null)
                    throw MachoErrors.ThrowUserError("ItemNotASkill", ("skillName", new PyInt(skillTypeId)));
                var current = byId[skillTypeId];
                var newPoints = current.SkillPoints - points;
                if (alphaAccount && catalog.AlphaMaxLevel(alphaRaceId, skillTypeId) > 0)
                    throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Alpha clones may only extract skills that require Omega clone state.")));
                changed.Add(current with { SkillPoints = newPoints, Level = SkillPoints.LevelAt(skillType.Rank, newPoints) });
            }
            await store.GrantAsync(connection, transaction, character, changed, token);
            if (locked.Singleton || locked.Quantity == 1)
                await items.DeleteAsync(connection, transaction, locked.Id, token);
            else
                await items.SetQuantityAsync(connection, transaction, locked.Id, locked.Quantity - 1, token);
            await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(injectorTypeId.Value, character, station, HangarFlag, 1, Singleton: false), token);
            return changed;
        }, call.Cancellation);

        return PyNone.Instance;
    }

    [Method("SplitSkillInjector")]
    public ValueTask<PyValue> SplitSkillInjector(ServiceCall call) => TransformSkillInjectorsAsync(call, splitting: true);

    [Method("CombineSkillInjector")]
    public ValueTask<PyValue> CombineSkillInjector(ServiceCall call) => TransformSkillInjectorsAsync(call, splitting: false);

    private async ValueTask<PyValue> TransformSkillInjectorsAsync(ServiceCall call, bool splitting)
    {
        if (CharacterOf(call) is not { } character || call.Session.GetInt("stationid") is not { } station
            || IdOf(call.Arg(0)) is not { } itemId || IdOf(call.Arg(1)) is not { } quantity || quantity <= 0)
            return None;
        var smallType = catalog.ItemTypeId("Small Skill Injector");
        var largeType = catalog.ItemTypeId("Large Skill Injector");
        if (smallType is null || largeType is null)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Skill injector splitting is unavailable.")));
        var fromType = splitting ? largeType.Value : smallType.Value;
        var toType = splitting ? smallType.Value : largeType.Value;
        if (!splitting && (quantity < 3 || quantity % 3 != 0))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Three small skill injectors are required to combine.")));

        await store.InTransactionAsync(async (connection, transaction, token) =>
        {
            if (await items.LockAndGetAsync(connection, transaction, itemId, token) is not { } item
                || item.OwnerId != character || item.LocationId != station || item.TypeId != fromType
                || item.Singleton && quantity != 1 || !item.Singleton && quantity > item.Quantity)
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That skill injector stack cannot be transformed.")));
            var outputQuantity = splitting ? checked(quantity * 3) : quantity / 3;
            if (item.Singleton || quantity == item.Quantity)
                await items.DeleteAsync(connection, transaction, itemId, token);
            else
                await items.SetQuantityAsync(connection, transaction, itemId, item.Quantity - quantity, token);
            await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(toType, character, station, HangarFlag, outputQuantity, Singleton: false), token);
            return true;
        }, call.Cancellation);
        return None;
    }

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

    private static IEnumerable<(long SkillTypeId, long Points)> ExtractionTargetsOf(PyValue value)
    {
        if (value is PyDict dictionary)
        {
            foreach (var entry in dictionary.Entries)
                if (IdOf(entry.Key) is { } skillId)
                    yield return (skillId, IdOf(entry.Value) ?? long.MaxValue);
            yield break;
        }

        if (value is PyList or PyTuple)
        {
            var entries = ItemsOf(value);
            if (entries.Count >= 2 && IdOf(entries[0]) is { } skillTypeId && IdOf(entries[^1]) is { } points)
            {
                yield return (skillTypeId, points);
                yield break;
            }
            foreach (var entry in entries)
                foreach (var target in ExtractionTargetsOf(entry))
                    yield return target;
            yield break;
        }

        if (IdOf(value) is { } typeId)
            yield return (typeId, long.MaxValue);
    }
}
