using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills;

/// <summary>
/// The expert systems of a pilot (<c>expertSystemMgr</c>): the list the client reads, the use of an expert system item (<c>ConsumeExpertSystem</c>) and
/// the removal of an installed one (<c>RemoveMyExpertSystem</c>). An installed expert system lapses when its time is up: the expiry job calls
/// </summary>
[Service("expertSystemMgr")]
public sealed partial class ExpertSystemMgrService(
    ExpertSystems expertSystems, PgSkillStore skills, SkillCatalog skillCatalog, PgItemStore items, NpgsqlDataSource source, CharacterSessions sessions,
    ICharacterDogmaSync? dogma, Eve.Dogma.DogmaStatic statics, Func<long, int, CancellationToken, Task>? writeLapseNotice = null)
{

    private const long JunkLocation = 6;

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    /// <summary><c>GetMyExpertSystems()</c>: the installed expert systems still in time, by type: the times they were installed and lapse (file times).</summary>
    [Method("GetMyExpertSystems")]
    public async ValueTask<PyValue> GetMyExpertSystems(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId)
        {
            return new PyDict([]);
        }
        var nowMs = expertSystems.NowMs;
        return new PyDict([.. (await expertSystems.Store.ForCharacterAsync(characterId, call.Cancellation))
            .Where(entry => entry.ExpiresAtMs > nowMs)
            .OrderBy(entry => entry.TypeId)
            .Select(entry => new KeyValuePair<PyValue, PyValue>(
                new PyInt(entry.TypeId),
                new PyList([new PyLong(Py.FileTimeFromUnixMs(entry.InstalledAtMs)), new PyLong(Py.FileTimeFromUnixMs(entry.ExpiresAtMs))])))]);
    }

    /// <summary><c>ConsumeExpertSystem(itemID)</c>: uses an expert system item from the character's inventory.</summary>
    [Method("ConsumeExpertSystem")]
    public async ValueTask<PyValue> ConsumeExpertSystem(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || IdOf(call.Arg(0)) is not { } itemId)
        {
            return PyNone.Instance;
        }
        await ConsumeAsync(call.Session, characterId, itemId, call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary><c>RemoveMyExpertSystem(typeID)</c>: takes an installed expert system off the character.</summary>
    [Method("RemoveMyExpertSystem")]
    public async ValueTask<PyValue> RemoveMyExpertSystem(ServiceCall call)
    {
        if (call.Session.CharacterId is { } characterId && IdOf(call.Arg(0)) is { } typeId)
        {
            await RemoveAsync(call.Session, characterId, (int)typeId, call.Cancellation);
        }
        return PyNone.Instance;
    }

    /// <summary>
    /// Installs the expert system of an item and uses one of the item: the installation is checked (the limit, the top-up window, the hidden types) and
    /// written with the item's use in one transaction. The client is then told the inventory row, the list, and the skills that changed.
    /// </summary>
    public async Task ConsumeAsync(ClientSession session, long characterId, long itemId, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(itemId, cancellationToken);
        if (item is null || item.OwnerId != characterId)
        {
            throw Refusal("Expert System item was not found in your inventory.");
        }
        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (expertSystems.Catalog.Get(item.TypeId) is not { } type)
        {
            throw Refusal("That item is not an Expert System.");
        }

        var nowMs = expertSystems.NowMs;
        var trainedBefore = await skills.ForCharacterAsync(characterId, cancellationToken);
        Dictionary<int, int> grantsBefore;
        bool removedItem;
        ExpertSystemEntry installed;
        bool added;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
            var entries = await expertSystems.Store.ForCharacterAsync(connection, transaction, characterId, cancellationToken);
            grantsBefore = ExpertSystemRules.GrantsOf(expertSystems.Catalog, entries, nowMs);
            if (ExpertSystemRules.RefusalOf(expertSystems.Catalog, type, entries, nowMs) is { } refusal)
            {
                throw Refusal(refusal);
            }
            added = !entries.Any(entry => entry.TypeId == type.TypeId && entry.ExpiresAtMs > nowMs);
            installed = ExpertSystemRules.InstalledEntry(type, entries, characterId, nowMs, itemId);
            await expertSystems.Store.UpsertAsync(connection, transaction, installed, cancellationToken);

            var locked = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
            if (locked is null || locked.OwnerId != characterId)
            {
                throw Refusal("Expert System item was not found in your inventory.");
            }
            removedItem = locked.Singleton || locked.Quantity <= 1;
            if (removedItem)
            {
                await items.DeleteAsync(connection, transaction, itemId, cancellationToken);
            }
            else
            {
                await items.SetQuantityAsync(connection, transaction, itemId, locked.Quantity - 1, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            await Told(session, characterId, item, removedItem, type.TypeId, added, grantsBefore, trainedBefore, cancellationToken);
        }
    }

    /// <summary>Takes an installed expert system off a character; a type that is not installed changes nothing.</summary>
    public async Task RemoveAsync(ClientSession? session, long characterId, int typeId, CancellationToken cancellationToken)
    {
        var trainedBefore = await skills.ForCharacterAsync(characterId, cancellationToken);
        Dictionary<int, int> grantsBefore;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
            grantsBefore = ExpertSystemRules.GrantsOf(expertSystems.Catalog, await expertSystems.Store.ForCharacterAsync(connection, transaction, characterId, cancellationToken), expertSystems.NowMs);
            if (!await expertSystems.Store.RemoveAsync(connection, transaction, characterId, typeId, null, cancellationToken))
            {
                return;
            }
            await transaction.CommitAsync(cancellationToken);
        }
        await Told(session, characterId, null, false, typeId, false, grantsBefore, trainedBefore, cancellationToken, expired: false);
    }

    /// <summary>
    /// The expiry job: the installed expert systems whose time has run out are removed, and a character that is logged in is told (the reference's
    /// <c>OnExpertSystemExpired</c>). Runs as a recurring job of the durable scheduler, so a restart keeps every installation.
    /// </summary>
    public async Task ExpireDueAsync(CancellationToken cancellationToken)
    {
        foreach (var due in await expertSystems.Store.ExpiredAsync(expertSystems.NowMs, 100, cancellationToken))
        {
            var trainedBefore = await skills.ForCharacterAsync(due.CharacterId, cancellationToken);
            Dictionary<int, int> grantsBefore;
            bool removed;
            await using (var connection = await source.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await LockCharacterAsync(connection, transaction, due.CharacterId, cancellationToken);
                grantsBefore = ExpertSystemRules.GrantsOf(expertSystems.Catalog, await expertSystems.Store.ForCharacterAsync(connection, transaction, due.CharacterId, cancellationToken), expertSystems.NowMs);
                removed = await expertSystems.Store.RemoveAsync(connection, transaction, due.CharacterId, due.TypeId, expertSystems.NowMs, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            if (removed)
            {

                if (writeLapseNotice is not null)
                    await writeLapseNotice(due.CharacterId, due.TypeId, cancellationToken);
                await Told(sessions.Of(due.CharacterId), due.CharacterId, null, false, due.TypeId, false, grantsBefore, trainedBefore, cancellationToken, expired: true);
            }
        }
    }

    /// <summary>
    /// What the client is told of an expert system change, in the reference's order: the inventory row of a used item, the list
    /// (<c>OnExpertSystemsUpdated</c>), the skills whose level changed, the ship's attributes and the brain, and on a lapse <c>OnExpertSystemExpired</c>.
    /// </summary>
    private async Task Told(
        ClientSession? session, long characterId, Item? used, bool removedItem, int typeId, bool added,
        Dictionary<int, int> grantsBefore, IReadOnlyList<CharacterSkill> trainedBefore, CancellationToken cancellationToken, bool expired = false)
    {
        if (session is null)
        {
            return;
        }
        if (used is not null)
        {
            var after = removedItem ? used with { LocationId = JunkLocation } : used with { Quantity = used.Quantity - 1 };
            TellItemChange(session, used, after);
        }
        var nowMs = expertSystems.NowMs;
        var entries = await expertSystems.Store.ForCharacterAsync(characterId, cancellationToken);
        session.SendNotification("OnExpertSystemsUpdated", "clientID", ListOf(entries, nowMs), added ? PyBool.True : PyBool.False, new PyInt(typeId));

        var grantsAfter = ExpertSystemRules.GrantsOf(expertSystems.Catalog, entries, nowMs);
        var trainedAfter = await skills.ForCharacterAsync(characterId, cancellationToken);
        var changed = ChangedSkills(trainedBefore, grantsBefore, trainedAfter, grantsAfter);
        if (changed.Count > 0)
        {
            var rows = SkillWire.EntryDictionary(characterId, skillCatalog, changed, metadata: false);
            SkillNotifications.ServerSkillsChanged(session, rows, null, Py.FileTimeFromUnixMs(nowMs));
            SkillNotifications.SkillsChanged(session, rows);
        }
        if (dogma is not null && (grantsBefore.Count > 0 || grantsAfter.Count > 0))
        {
            await dogma.ExpertGrantsChangedAsync(session, characterId, trainedBefore, grantsBefore, cancellationToken);
        }
        if (expired)
        {
            session.SendNotification("OnExpertSystemExpired", "clientID", new PyInt(typeId));
        }
    }

    /// <summary>The skills whose level, as the client reads it, differs between the two states (a skill that lost its grant shows its trained level).</summary>
    private static IReadOnlyList<CharacterSkill> ChangedSkills(
        IReadOnlyList<CharacterSkill> trainedBefore, IReadOnlyDictionary<int, int> grantsBefore, IReadOnlyList<CharacterSkill> trainedAfter, IReadOnlyDictionary<int, int> grantsAfter)
    {
        var before = ExpertSystemRules.Effective(trainedBefore, grantsBefore).ToDictionary(skill => skill.TypeId);
        var after = ExpertSystemRules.Effective(trainedAfter, grantsAfter).ToDictionary(skill => skill.TypeId);
        var changed = new List<CharacterSkill>();
        foreach (var typeId in before.Keys.Union(after.Keys).OrderBy(id => id))
        {
            var was = before.GetValueOrDefault(typeId)?.Level ?? 0;
            var now = after.GetValueOrDefault(typeId);
            if (now is null)
            {
                changed.Add(new CharacterSkill(typeId, 0, 0));
            }
            else if (now.Level != was)
            {
                changed.Add(now);
            }
        }
        return changed;
    }

    private static PyDict ListOf(IReadOnlyList<ExpertSystemEntry> entries, long nowMs) => new(
        [.. entries.Where(entry => entry.ExpiresAtMs > nowMs).OrderBy(entry => entry.TypeId).Select(entry =>
            new KeyValuePair<PyValue, PyValue>(
                new PyInt(entry.TypeId),
                new PyList([new PyLong(Py.FileTimeFromUnixMs(entry.InstalledAtMs)), new PyLong(Py.FileTimeFromUnixMs(entry.ExpiresAtMs))])))]);

    private static Exception Refusal(string message) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));

    /// <summary>What the client is told of an item that changed: its row, and what differs from before (the place and the flag it left, the size it was).</summary>
    private void TellItemChange(ClientSession session, Item before, Item after)
    {
        var differences = new List<KeyValuePair<PyValue, PyValue>>();
        if (after.LocationId != before.LocationId)
        {
            differences.Add(new(new PyInt(3), InventoryWire.Id(before.LocationId)));
        }
        if (after.Flag != before.Flag)
        {
            differences.Add(new(new PyInt(4), new PyInt(before.Flag)));
        }
        if (after.Quantity != before.Quantity)
        {
            differences.Add(new(new PyInt(9), InventoryWire.Id(before.Quantity)));
        }
        session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(RowOf(after)), new PyDict(differences), PyNone.Instance);
    }

    /// <summary>An item as the client's inventory rows show it.</summary>
    private ItemRow RowOf(Item item)
    {
        var type = statics.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    /// <summary>The character's expert system changes happen one after the other.</summary>
    private static async Task LockCharacterAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('expert-systems:' || $1::text, 0))", connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
