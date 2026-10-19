using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>One blueprint item as the client's blueprint instance is built from it.</summary>
public sealed record BlueprintRecord(long ItemId, int TypeId, long OwnerId, long LocationId, int LocationTypeId, int Flag, bool IsCopy,
    int? Runs, int MaterialEfficiency, int TimeEfficiency, long SolarSystemId, bool IsImpounded);

/// <summary>
/// The client's <c>blueprintManager</c>: the blueprints a character or corporation holds, for the industry windows. The answers are the
/// reference's (<c>blueprintManagerService.js</c>, <c>industryPayloads.js</c>). A blueprint's owner is the only one who sees it.
/// </summary>
[Service("blueprintManager")]
public sealed partial class BlueprintManagerService(
    Func<long, CancellationToken, ValueTask<BlueprintRecord?>> readBlueprint,
    Func<long, CancellationToken, ValueTask<IReadOnlyList<BlueprintRecord>>> readOwned,
    Func<long, CancellationToken, ValueTask<IReadOnlyDictionary<long, long>>> readActiveJobs)
{
    /// <summary>The most blueprints one listing returns (<c>GetLimits</c> of the reference).</summary>
    public const int MaxBlueprintResults = 500;

    private const int QuantitySingletonOriginal = -1;
    private const int QuantityCopy = -2;

    [Method("GetLimits")]
    public PyValue GetLimits(ServiceCall call) => Py.Dict(("maxBlueprintResults", new PyInt(MaxBlueprintResults)));

    /// <summary>The blueprint with that item id. Not held by the caller (or not a blueprint) is the client's own <c>IndustryBlueprintNotFound</c>.</summary>
    [Method("GetBlueprintData")]
    public async ValueTask<PyValue> GetBlueprintData(ServiceCall call)
    {
        var itemId = Integral(call.Arg(0)) ?? 0;
        var blueprint = itemId > 0 ? await readBlueprint(itemId, call.Cancellation) : null;
        if (blueprint is null || !CanRead(blueprint, call.Session))
            throw MachoErrors.ThrowUserError("IndustryBlueprintNotFound");
        var jobs = await readActiveJobs(blueprint.OwnerId, call.Cancellation);
        return Payload(blueprint, jobs.TryGetValue(blueprint.ItemId, out var job) ? job : null);
    }

    /// <summary>
    /// The blueprints of an owner: [list, counts]. The counts are per facility (the key is null for a blueprint in no facility). The
    /// listing is limited to <see cref="MaxBlueprintResults"/>. A facility filter lists none: no blueprint is in an industry facility here.
    /// </summary>
    [Method("GetBlueprintDataByOwner")]
    public async ValueTask<PyValue> GetBlueprintDataByOwner(ServiceCall call)
    {
        var owner = Integral(call.Arg(0)) ?? 0;
        var facility = Integral(call.Arg(1));
        if (!CanListOwner(owner, call.Session))
            return Py.Tuple(new PyList([]), new PyDict([]));
        var owned = await readOwned(owner, call.Cancellation);
        var held = owned.Where(blueprint => CanRead(blueprint, call.Session)).ToArray();
        var jobs = await readActiveJobs(owner, call.Cancellation);
        IEnumerable<PyValue> listed = facility is null
            ? held.Take(MaxBlueprintResults).Select(blueprint => Payload(blueprint, jobs.TryGetValue(blueprint.ItemId, out var job) ? job : null))
            : Array.Empty<PyValue>();
        var counts = new PyDict([new KeyValuePair<PyValue, PyValue>(PyNone.Instance, new PyInt(held.Length))]);
        return Py.Tuple(new PyList([.. listed]), counts);
    }

    /// <summary>The character or corporation the session acts for: the owner's own blueprints only.</summary>
    private static bool CanRead(BlueprintRecord blueprint, ClientSession session)
    {
        if (blueprint.OwnerId == session.CharacterId)
            return true;
        if (!HoldsCorporation(blueprint.OwnerId, session))
            return false;

        var roles = new Eve.Corporations.SessionRoles(
            session.GetInt("corprole") ?? 0,
            session.GetInt("rolesAtAll") ?? 0,
            session.GetInt("rolesAtHQ") ?? 0,
            session.GetInt("rolesAtBase") ?? 0,
            session.GetInt("rolesAtOther") ?? 0,
            session.GetInt("hqID"),
            session.GetInt("baseID"),
            (int)(session.GetInt("corpAccountKey") ?? 1000));
        return (roles.CorpRole & Eve.Corporations.CorporationRoles.FactoryManager) != 0 &&
            Eve.Corporations.CorporationRoles.HasHangarAccess(roles, blueprint.LocationId, blueprint.Flag, take: false);
    }

    private static bool CanListOwner(long owner, ClientSession session) =>
        owner > 0 && (owner == session.CharacterId ||
            HoldsCorporation(owner, session) &&
            ((session.GetInt("corprole") ?? 0) & Eve.Corporations.CorporationRoles.FactoryManager) != 0);

    private static bool HoldsCorporation(long owner, ClientSession session) =>
        owner > 0 && owner == (session.GetInt("corpid") ?? 0);

    /// <summary>
    /// The blueprint instance payload. The client reads a blueprint as an original when its quantity is not -2 and as singleton when it
    /// is negative (<c>industryCommon.py:331-333</c>); an original's runs are the EVE convention of -1 (unlimited).
    /// </summary>
    private static PyValue Payload(BlueprintRecord blueprint, long? jobId) => Py.KeyVal(
        ("typeID", new PyInt(blueprint.TypeId)),
        ("itemID", InventoryWire.Id(blueprint.ItemId)),
        ("timeEfficiency", new PyInt(blueprint.TimeEfficiency)),
        ("materialEfficiency", new PyInt(blueprint.MaterialEfficiency)),
        ("runs", new PyInt(blueprint.Runs ?? (blueprint.IsCopy ? 0 : -1))),
        ("quantity", new PyInt(blueprint.IsCopy ? QuantityCopy : QuantitySingletonOriginal)),
        ("locationID", InventoryWire.Id(blueprint.LocationId)),
        ("locationTypeID", new PyInt(blueprint.LocationTypeId)),
        ("locationFlagID", new PyInt(blueprint.Flag)),
        ("flagID", new PyInt(blueprint.Flag)),
        ("facilityID", PyNone.Instance),
        ("ownerID", InventoryWire.Id(blueprint.OwnerId)),
        ("jobID", jobId is { } job ? InventoryWire.Id(job) : PyNone.Instance),
        ("isImpounded", PyValue.Of(blueprint.IsImpounded)),
        ("original", PyValue.Of(!blueprint.IsCopy)),
        ("solarSystemID", InventoryWire.Id(blueprint.SolarSystemId)));

    private static long? Integral(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };
}
