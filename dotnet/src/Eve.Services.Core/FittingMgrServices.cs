using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;


public interface IFittingHost
{
    /// <summary>The category of each type that exists; a type that does not exist is absent.</summary>
    Task<IReadOnlyDictionary<int, int>> CategoriesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken);


    Task<bool> HasFittingManagerRoleAsync(long characterId, CancellationToken cancellationToken);


    Task<bool> CanAccessOwnerAsync(long characterId, long ownerId, FittingOwnerScope scope, CancellationToken cancellationToken);

    /// <summary>The executor corporation of an alliance, or null.</summary>
    Task<long?> AllianceExecutorAsync(long allianceId, CancellationToken cancellationToken);


    IReadOnlyList<ClientSession> SessionsOf(FittingOwnerScope scope, long ownerId);
}

/// <summary>

/// <c>corpFittingMgrService.js</c>, <c>allianceFittingMgrService.js</c> and the store in <c>fittingStore.js</c>). The three services share this: the


/// </summary>
public sealed class FittingMgrCalls(PgSavedFittingStore store, IFittingHost host, Func<long> nowUnixMs)
{
    private const string Community = "CustomNotify";

    public async ValueTask<PyValue> GetFittings(ServiceCall call, FittingOwnerScope scope)
    {
        var owner = OwnerOf(call, scope);
        await AccessAsync(call, owner, scope);
        var fittings = await store.ListAsync(scope, owner, call.Cancellation);
        var answer = new PyDict([.. fittings.Select(f => new KeyValuePair<PyValue, PyValue>(new PyLong(f.FittingId), FittingKeyVal(f)))]);
        // a corporation's list is cached by the client for 15 minutes; the changes invalidate it (Notify), as the reference does
        return scope == FittingOwnerScope.Corporation ? CacheFittings(answer) : answer;
    }

    internal PyValue CacheFittings(PyValue fittings) => ObjectCaching.MethodResult(fittings, nowUnixMs(), "15 minutes");

    public async ValueTask<PyValue> SaveFitting(ServiceCall call, FittingOwnerScope scope)
    {
        var owner = OwnerOf(call, scope);
        await MutateAccessAsync(call, owner, scope);
        var draft = await CheckedDraftAsync(call, call.Arg(1));
        var ids = await SaveDraftsAsync(scope, owner, [draft]);
        Notify(call, scope, owner, "OnFittingAdded", new PyLong(ids[0]));
        return new PyLong(ids[0]);
    }

    public async ValueTask<PyValue> UpdateFitting(ServiceCall call, FittingOwnerScope scope)
    {
        var owner = OwnerOf(call, scope);
        await MutateAccessAsync(call, owner, scope);
        var fittingId = IdOf(call.Kwarg("fittingID") ?? call.Arg(2)) ?? 0;
        await RequireOwnedAsync(scope, owner, fittingId);
        var draft = await CheckedDraftAsync(call, call.Arg(1));
        if (!await store.UpdateAsync(scope, owner, fittingId, draft, Filetime(), call.Cancellation))
        {
            throw Refusal("FITTING_NOT_FOUND");
        }
        Notify(call, scope, owner, "OnFittingAdded", new PyList([new PyLong(fittingId)]));
        return new PyLong(fittingId);
    }

    public async ValueTask<PyValue> SaveManyFittings(ServiceCall call, FittingOwnerScope scope)
    {
        var owner = OwnerOf(call, scope);
        await MutateAccessAsync(call, owner, scope);
        var input = call.Arg(1);
        var limit = LimitOf(scope);
        var entries = input switch
        {
            PyObject { Args: PyDict dict } => dict.Entries,
            PyDict dict => dict.Entries,
            _ => throw Refusal("FITTING_BULK_ARGUMENT_INVALID"),
        };
        if (entries.Count > limit)
        {
            throw Refusal("FITTING_TOO_MANY_BULK_ENTRIES", limit);
        }

        var temps = new List<long>(entries.Count);
        foreach (var (key, _) in entries)
        {
            var tempId = IdOf(key);
            if (tempId is null or 0)
                throw Refusal("FITTING_BULK_ARGUMENT_INVALID");
            temps.Add(tempId.Value);
        }
        var drafts = new List<FittingDraft>();
        foreach (var (_, value) in entries)
        {
            drafts.Add(await CheckedDraftAsync(call, value));
        }
        var ids = await SaveDraftsAsync(scope, owner, drafts);
        for (var i = 0; i < ids.Count; i++)
        {
            Notify(call, scope, owner, "OnFittingAdded", new PyLong(ids[i]));
        }
        return new PyList([.. temps.Zip(ids, (temp, real) => Py.KeyVal(
            ("tempFittingID", new PyLong(temp)),
            ("realFittingID", new PyLong(real))))]);
    }

    public async ValueTask<PyValue> DeleteFitting(ServiceCall call, FittingOwnerScope scope)
    {
        var owner = OwnerOf(call, scope);
        await MutateAccessAsync(call, owner, scope);
        var fittingId = IdOf(call.Arg(1)) ?? 0;
        await store.DeleteAsync(scope, owner, [fittingId], call.Cancellation);
        Notify(call, scope, owner, "OnFittingDeleted", new PyLong(fittingId));
        return PyNone.Instance;
    }

    public async ValueTask<PyValue> DeleteManyFittings(ServiceCall call, FittingOwnerScope scope)
    {
        var owner = OwnerOf(call, scope);
        await MutateAccessAsync(call, owner, scope);
        var input = call.Arg(1);
        var limit = LimitOf(scope);
        var requestedIds = input switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => throw Refusal("FITTING_BULK_ARGUMENT_INVALID"),
        };
        if (requestedIds.Count > limit)
        {
            throw Refusal("FITTING_TOO_MANY_BULK_ENTRIES", limit);
        }

        var ids = new List<long>(requestedIds.Count);
        foreach (var requestedId in requestedIds)
        {
            var id = IdOf(requestedId);
            if (id is not > 0)
                throw Refusal("FITTING_BULK_ARGUMENT_INVALID");
            if (!ids.Contains(id.Value))
                ids.Add(id.Value);
        }
        var deleted = await store.DeleteAsync(scope, owner, ids, call.Cancellation);
        Notify(call, scope, owner, "OnManyFittingsDeleted", new PyList([.. deleted.Select(id => (PyValue)new PyLong(id))]));
        return new PyList([.. deleted.Select(id => (PyValue)new PyLong(id))]);
    }

    public async ValueTask<PyValue> UpdateNameAndDescription(ServiceCall call, FittingOwnerScope scope)
    {
        var fittingId = IdOf(call.Arg(0)) ?? 0;
        var owner = IdOf(call.Arg(1)) ?? 0;
        await MutateAccessAsync(call, owner, scope);
        await RequireOwnedAsync(scope, owner, fittingId);
        var (name, description) = SavedFittingRules.Normalize(JsSemantics.PlainText(call.Arg(2)), JsSemantics.PlainText(call.Arg(3)));
        if (name.Length == 0)
        {
            throw Refusal("FITTING_NAME_REQUIRED");
        }
        if (name.Contains("@@", StringComparison.Ordinal) || description.Contains("@@", StringComparison.Ordinal))
        {
            throw Refusal("FITTING_INVALID_TEXT");
        }
        if (!await store.RenameAsync(scope, owner, fittingId, name, description, Filetime(), call.Cancellation))
        {
            throw Refusal("FITTING_NOT_FOUND");
        }
        Notify(call, scope, owner, "OnFittingAdded", new PyList([new PyLong(fittingId)]));
        return PyNone.Instance;
    }

    public static long LimitOf(FittingOwnerScope scope) => scope == FittingOwnerScope.Character ? 500 : 600;

    private long Filetime() => Py.FileTimeFromUnixMs(nowUnixMs());


    private static long OwnerOf(ServiceCall call, FittingOwnerScope scope)
    {
        var requested = IdOf(call.Arg(0)) ?? 0;
        return requested > 0 ? requested : SessionOwner(call.Session, scope);
    }

    private static long SessionOwner(ClientSession session, FittingOwnerScope scope) => scope switch
    {
        FittingOwnerScope.Corporation => session.GetInt("corpid") ?? 0,
        FittingOwnerScope.Alliance => session.GetInt("allianceid") ?? 0,
        _ => session.CharacterId ?? 0,
    };


    private async ValueTask AccessAsync(ServiceCall call, long owner, FittingOwnerScope scope)
    {
        var characterId = call.Session.CharacterId ?? call.Session.GetInt("charid") ?? 0;
        if (owner > 0 && owner == SessionOwner(call.Session, scope) &&
            await host.CanAccessOwnerAsync(characterId, owner, scope, call.Cancellation))
        {
            return;
        }
        throw Refusal("OWNER_SCOPE_DENIED");
    }


    private async ValueTask MutateAccessAsync(ServiceCall call, long owner, FittingOwnerScope scope)
    {
        await AccessAsync(call, owner, scope);
        if (scope == FittingOwnerScope.Character)
        {
            return;
        }
        if (!await host.HasFittingManagerRoleAsync(call.Session.CharacterId ?? 0, call.Cancellation))
        {
            throw Refusal("FITTING_MANAGER_ROLE_REQUIRED");
        }
        if (scope == FittingOwnerScope.Alliance)
        {
            var executor = await host.AllianceExecutorAsync(owner, call.Cancellation);
            if (executor is null || executor != (call.Session.GetInt("corpid") ?? 0))
            {
                throw Refusal("ALLIANCE_EXECUTOR_CORP_REQUIRED");
            }
        }
    }


    private async Task RequireOwnedAsync(FittingOwnerScope scope, long owner, long fittingId)
    {
        var found = fittingId > 0 ? await store.OwnerOfAsync(fittingId) : null;
        if (found is not { } where || where.Scope != scope || where.OwnerId != owner)
        {
            throw Refusal("FITTING_NOT_FOUND");
        }
    }


    private async Task<FittingDraft> CheckedDraftAsync(ServiceCall call, PyValue value)
    {
        var fields = Fields(value).ToDictionary(entry => JsSemantics.StringValue(entry.Key) ?? "", entry => entry.Value);
        var (name, description) = SavedFittingRules.Normalize(
            fields.TryGetValue("name", out var n) ? JsSemantics.PlainText(n) : "",
            fields.TryGetValue("description", out var d) ? JsSemantics.PlainText(d) : "");
        var items = (fields.TryGetValue("fitData", out var data) ? data : PyNone.Instance) switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] };
        if (items.Count > 500)
        {
            throw Refusal("FITTING_TOO_MANY_ITEMS", items.Count);
        }
        var draft = new FittingDraft(
            fields.TryGetValue("shipTypeID", out var ship) ? Int32Of(ship) : 0,
            name,
            description,
            [.. items.Select(Item).OfType<FittingItem>()]);
        var typeIds = new HashSet<int> { draft.ShipTypeId };
        foreach (var item in draft.FitData)
        {
            typeIds.Add(item.TypeId);
        }
        var categories = await host.CategoriesAsync(typeIds, call.Cancellation);
        if (SavedFittingRules.Check(draft, id => categories.TryGetValue(id, out var category) ? category : null) is { } fault)
        {
            throw Refusal(fault.Code, fault.Value);
        }
        return draft;
    }

    private static int Int32Of(PyValue value)
    {
        var number = IdOf(value);
        if (number is null || number < int.MinValue || number > int.MaxValue)
        {
            throw Refusal("FITTING_INTEGER_OUT_OF_RANGE");
        }
        return (int)number.Value;
    }

    private static FittingItem? Item(PyValue value)
    {
        var parts = value switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] };
        return parts.Count < 3
            ? null
            : new FittingItem(Int32Of(parts[0]), Int32Of(parts[1]), Int32Of(parts[2]));
    }


    private async Task<IReadOnlyList<long>> SaveDraftsAsync(FittingOwnerScope scope, long owner, IReadOnlyList<FittingDraft> drafts)
    {
        var limit = LimitOf(scope);
        var ids = await store.SaveManyAsync(scope, owner, drafts, (int)limit, Filetime());
        return ids ?? throw Refusal("OWNER_MAX_FITTINGS", limit);
    }

    /// <summary>

    /// <c>notifyFittingMutation</c> and <c>invalidateClientFittingListCache</c>: the invalidation is in the stream before the notification).
    /// </summary>
    private void Notify(ServiceCall call, FittingOwnerScope scope, long owner, string name, PyValue payload)
    {
        var recipients = new HashSet<ClientSession>(host.SessionsOf(scope, owner).Append(call.Session), ReferenceEqualityComparer.Instance);
        foreach (var session in recipients)
        {
            if (scope == FittingOwnerScope.Corporation && session.ClientId is { } clientId and > 0)
            {
                session.Send(ClientCallRequest.Build(clientId, session.UserId ?? 0, "objectCaching", "InvalidateCachedMethodCall",
                    [new PyStr("corpFittingMgr"), new PyStr("GetFittings"), new PyLong(owner)], ClientCallRequest.NextCallId()));
            }
            session.SendNotification(name, "charid", new PyLong(owner), payload);
        }
    }

    private static PyValue FittingKeyVal(SavedFitting fitting) => Py.KeyVal(
        ("description", new PyStr(fitting.Draft.Description)),
        ("fitData", new PyList([.. fitting.Draft.FitData.Select(i => (PyValue)Py.Tuple(new PyInt(i.TypeId), new PyInt(i.Flag), new PyInt(i.Quantity)))])),
        ("fittingID", new PyLong(fitting.FittingId)),
        ("name", new PyStr(fitting.Draft.Name)),
        ("ownerID", new PyLong(fitting.OwnerId)),
        ("savedDate", new PyLong(fitting.SavedAtFileTime)),
        ("shipTypeID", new PyInt(fitting.Draft.ShipTypeId)));

    private static IEnumerable<KeyValuePair<PyValue, PyValue>> Fields(PyValue value) => value switch
    {
        PyObject { Args: PyDict dict } => dict.Entries,
        PyDict dict => dict.Entries,
        _ => [],
    };

    private static long? IdOf(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };


    public static MachoWrappedException Refusal(string code, long value = 0) => code switch
    {
        "FITTING_NAME_REQUIRED" => MachoErrors.ThrowUserError("FittingNeedsToHaveAName"),
        "OWNER_MAX_FITTINGS" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr($"That fitting collection is full ({value} max)."))),
        "FITTING_INVALID_TEXT" => MachoErrors.ThrowUserError("InvalidFittingInvalidCharacter"),
        "FITTING_INVALID_TYPE_ID" => MachoErrors.ThrowUserError("InvalidFittingDataTypeID", ("typeID", new PyInt(value)), ("typeName", new PyInt(value))),
        "FITTING_INVALID_SHIP_TYPE" => MachoErrors.ThrowUserError("InvalidFittingDataShipNotShip", ("typeName", new PyInt(value))),
        "FITTING_DATA_EMPTY" => MachoErrors.ThrowUserError("ParseFittingFittingDataEmpty"),
        "FITTING_INVALID_FLAG" => MachoErrors.ThrowUserError("InvalidFittingDataInvalidFlag", ("type", new PyInt(value))),
        "FITTING_INVALID_QUANTITY" => MachoErrors.ThrowUserError("InvalidFittingDataInvalidQuantity", ("type", new PyInt(value))),
        "FITTING_TOO_MANY_ITEMS" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("This fitting contains too many items."))),
        "FITTING_TOO_MANY_BULK_ENTRIES" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr($"This operation can process at most {value} fittings at once."))),
        "FITTING_BULK_ARGUMENT_INVALID" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("This fitting batch has an invalid shape."))),
        "FITTING_INTEGER_OUT_OF_RANGE" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("This fitting contains an out-of-range integer."))),
        "COMMUNITY_FITTINGS_READ_ONLY" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("Community fittings are read-only."))),
        "OWNER_SCOPE_DENIED" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("You do not have access to that fitting collection."))),
        "FITTING_MANAGER_ROLE_REQUIRED" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("You need the corporation Fitting Manager role to change shared fittings."))),
        "ALLIANCE_EXECUTOR_CORP_REQUIRED" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("Only the alliance executor corporation can change alliance fittings."))),
        "FITTING_NOT_FOUND" => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("That fitting no longer exists."))),
        _ => MachoErrors.ThrowUserError(Community, ("notify", new PyStr("Fitting operation failed."))),
    };
}

/// <summary><c>charFittingMgr</c>: the character's own saved fittings.</summary>
[Service("charFittingMgr")]
public sealed partial class CharFittingMgrService(FittingMgrCalls calls)
{
    [Method("GetFittings")]
    public ValueTask<PyValue> GetFittings(ServiceCall call) => calls.GetFittings(call, FittingOwnerScope.Character);

    [Method("SaveFitting")]
    public ValueTask<PyValue> SaveFitting(ServiceCall call) => calls.SaveFitting(call, FittingOwnerScope.Character);

    [Method("UpdateFitting")]
    public ValueTask<PyValue> UpdateFitting(ServiceCall call) => calls.UpdateFitting(call, FittingOwnerScope.Character);

    [Method("SaveManyFittings")]
    public ValueTask<PyValue> SaveManyFittings(ServiceCall call) => calls.SaveManyFittings(call, FittingOwnerScope.Character);

    [Method("DeleteFitting")]
    public ValueTask<PyValue> DeleteFitting(ServiceCall call) => calls.DeleteFitting(call, FittingOwnerScope.Character);

    [Method("DeleteManyFittings")]
    public ValueTask<PyValue> DeleteManyFittings(ServiceCall call) => calls.DeleteManyFittings(call, FittingOwnerScope.Character);

    [Method("UpdateNameAndDescription")]
    public ValueTask<PyValue> UpdateNameAndDescription(ServiceCall call) => calls.UpdateNameAndDescription(call, FittingOwnerScope.Character);
}

/// <summary><c>corpFittingMgr</c>: the corporation's shared fittings, and the community fittings (read only, none without a provider).</summary>
[Service("corpFittingMgr")]
public sealed partial class CorpFittingMgrService(FittingMgrCalls calls, ReadCommunityFittingsCallback? readCommunityFittings = null)
{
    [Method("GetFittings")]
    public ValueTask<PyValue> GetFittings(ServiceCall call) => calls.GetFittings(call, FittingOwnerScope.Corporation);

    [Method("SaveFitting")]
    public ValueTask<PyValue> SaveFitting(ServiceCall call) => calls.SaveFitting(call, FittingOwnerScope.Corporation);

    [Method("UpdateFitting")]
    public ValueTask<PyValue> UpdateFitting(ServiceCall call) => calls.UpdateFitting(call, FittingOwnerScope.Corporation);

    [Method("SaveManyFittings")]
    public ValueTask<PyValue> SaveManyFittings(ServiceCall call) => calls.SaveManyFittings(call, FittingOwnerScope.Corporation);

    [Method("DeleteFitting")]
    public ValueTask<PyValue> DeleteFitting(ServiceCall call) => calls.DeleteFitting(call, FittingOwnerScope.Corporation);

    [Method("DeleteManyFittings")]
    public ValueTask<PyValue> DeleteManyFittings(ServiceCall call) => calls.DeleteManyFittings(call, FittingOwnerScope.Corporation);

    [Method("UpdateNameAndDescription")]
    public ValueTask<PyValue> UpdateNameAndDescription(ServiceCall call) => calls.UpdateNameAndDescription(call, FittingOwnerScope.Corporation);

    [Method("GetCommunityFittings")]
    public async ValueTask<PyValue> GetCommunityFittings(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var fittings = readCommunityFittings is { } read
            ? await read(call.Session, call.Cancellation).ConfigureAwait(false)
            : new Dictionary<long, PyValue>();
        return calls.CacheFittings(new PyDict([.. fittings.Select(fitting =>
            new KeyValuePair<PyValue, PyValue>(new PyLong(fitting.Key), fitting.Value))]));
    }
}

/// <summary><c>allianceFittingMgr</c>: the alliance's shared fittings (the executor corporation changes them).</summary>
[Service("allianceFittingMgr")]
public sealed partial class AllianceFittingMgrService(FittingMgrCalls calls)
{
    [Method("GetFittings")]
    public ValueTask<PyValue> GetFittings(ServiceCall call) => calls.GetFittings(call, FittingOwnerScope.Alliance);

    [Method("SaveFitting")]
    public ValueTask<PyValue> SaveFitting(ServiceCall call) => calls.SaveFitting(call, FittingOwnerScope.Alliance);

    [Method("UpdateFitting")]
    public ValueTask<PyValue> UpdateFitting(ServiceCall call) => calls.UpdateFitting(call, FittingOwnerScope.Alliance);

    [Method("SaveManyFittings")]
    public ValueTask<PyValue> SaveManyFittings(ServiceCall call) => calls.SaveManyFittings(call, FittingOwnerScope.Alliance);

    [Method("DeleteFitting")]
    public ValueTask<PyValue> DeleteFitting(ServiceCall call) => calls.DeleteFitting(call, FittingOwnerScope.Alliance);

    [Method("DeleteManyFittings")]
    public ValueTask<PyValue> DeleteManyFittings(ServiceCall call) => calls.DeleteManyFittings(call, FittingOwnerScope.Alliance);

    [Method("UpdateNameAndDescription")]
    public ValueTask<PyValue> UpdateNameAndDescription(ServiceCall call) => calls.UpdateNameAndDescription(call, FittingOwnerScope.Alliance);
}
