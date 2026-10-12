using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>What the fitting managers need from the host: the categories of the types, the corporation roles, the alliance executor and the sessions of an owner.</summary>
public interface IFittingHost
{
    /// <summary>The category of each type that exists; a type that does not exist is absent.</summary>
    Task<IReadOnlyDictionary<int, int>> CategoriesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken);

    /// <summary>Whether the character holds the corporation Fitting Manager role (EveJS <c>sessionHasFittingManagerRole</c>).</summary>
    Task<bool> HasFittingManagerRoleAsync(long characterId, CancellationToken cancellationToken);

    /// <summary>The executor corporation of an alliance, or null.</summary>
    Task<long?> AllianceExecutorAsync(long allianceId, CancellationToken cancellationToken);

    /// <summary>The sessions that play the characters of an owner: the recipients of its fitting notifications.</summary>
    IReadOnlyList<ClientSession> SessionsOf(FittingOwnerScope scope, long ownerId);
}

/// <summary>
/// The fitting calls of <c>charFittingMgr</c>, <c>corpFittingMgr</c> and <c>allianceFittingMgr</c> (EveJS <c>charFittingMgrService.js</c>,
/// <c>corpFittingMgrService.js</c>, <c>allianceFittingMgrService.js</c> and the store in <c>fittingStore.js</c>). The three services share this: the
/// owner is the argument or the session's own, access and mutation are checked as the reference checks them, a refusal is the client's own UserError,
/// and each change is announced to the sessions of the owner (<c>OnFittingAdded</c>, <c>OnFittingDeleted</c>, <c>OnManyFittingsDeleted</c>).
/// </summary>
public sealed class FittingMgrCalls(PgSavedFittingStore store, IFittingHost host, Func<long> nowUnixMs)
{
    private const string Community = "CustomNotify";

    public async ValueTask<PyValue> GetFittings(ServiceCall call, FittingOwnerScope scope)
    {
        var owner = OwnerOf(call, scope);
        Access(call, owner, scope);
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
        var entries = Fields(call.Arg(1)).ToList();
        var temps = new List<long>();
        var drafts = new List<FittingDraft>();
        foreach (var (key, value) in entries)
        {
            temps.Add(IdOf(key) ?? 0);
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
        var ids = (call.Arg(1) switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] })
            .Select(IdOf).OfType<long>().Where(id => id > 0).Distinct().ToList();
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

    /// <summary>The owner the call names, or the session's own when it names none (EveJS <c>resolveRequestedOwnerID</c>).</summary>
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

    /// <summary>EveJS <c>assertSessionCanAccessOwner</c>: the owner is the session's own.</summary>
    private static void Access(ServiceCall call, long owner, FittingOwnerScope scope)
    {
        if (owner > 0 && owner == SessionOwner(call.Session, scope))
        {
            return;
        }
        throw Refusal("OWNER_SCOPE_DENIED");
    }

    /// <summary>EveJS <c>assertSessionCanMutateOwner</c>: a corporation needs the Fitting Manager role; an alliance also needs its executor corporation.</summary>
    private async ValueTask MutateAccessAsync(ServiceCall call, long owner, FittingOwnerScope scope)
    {
        Access(call, owner, scope);
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

    /// <summary>The fitting exists for the owner (EveJS <c>FITTING_NOT_FOUND</c> before any other check).</summary>
    private async Task RequireOwnedAsync(FittingOwnerScope scope, long owner, long fittingId)
    {
        var found = fittingId > 0 ? await store.OwnerOfAsync(fittingId) : null;
        if (found is not { } where || where.Scope != scope || where.OwnerId != owner)
        {
            throw Refusal("FITTING_NOT_FOUND");
        }
    }

    /// <summary>Reads a client fitting, normalises it and checks it (EveJS <c>normalizeIncomingFitting</c> then <c>validateFittingRecord</c>).</summary>
    private async Task<FittingDraft> CheckedDraftAsync(ServiceCall call, PyValue value)
    {
        var fields = Fields(value).ToDictionary(entry => JsSemantics.StringValue(entry.Key) ?? "", entry => entry.Value);
        var (name, description) = SavedFittingRules.Normalize(
            fields.TryGetValue("name", out var n) ? JsSemantics.PlainText(n) : "",
            fields.TryGetValue("description", out var d) ? JsSemantics.PlainText(d) : "");
        var items = (fields.TryGetValue("fitData", out var data) ? data : PyNone.Instance) switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] };
        var draft = new FittingDraft(
            fields.TryGetValue("shipTypeID", out var ship) ? (int)(IdOf(ship) ?? 0) : 0,
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

    private static FittingItem? Item(PyValue value)
    {
        var parts = value switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] };
        return parts.Count < 3 || IdOf(parts[0]) is not { } type || IdOf(parts[1]) is not { } flag || IdOf(parts[2]) is not { } quantity
            ? null
            : new FittingItem((int)type, (int)flag, (int)quantity);
    }

    /// <summary>Saves drafts for an owner, refusing when the owner would go over its limit. Returns the new ids in order.</summary>
    private async Task<IReadOnlyList<long>> SaveDraftsAsync(FittingOwnerScope scope, long owner, IReadOnlyList<FittingDraft> drafts)
    {
        var limit = LimitOf(scope);
        var ids = await store.SaveManyAsync(scope, owner, drafts, (int)limit, Filetime());
        return ids ?? throw Refusal("OWNER_MAX_FITTINGS", limit);
    }

    /// <summary>
    /// Tells every session of the owner what changed, and for a corporation first drops the client's cached list (EveJS
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

    /// <summary>The refusal the client shows: the reference's UserError for the code (EveJS <c>throwFittingError</c>).</summary>
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
