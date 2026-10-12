using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Services;

namespace Eve.Services.Corporations;

/// <summary>
/// The client's <c>warRegistry</c>: the wars of a corporation or an alliance, read from the war relations, and the two changes a war's
/// owner makes on its own war (open for allies, mutual retraction). The war negotiations (ally offers, surrenders) are not built: no
/// negotiation can be opened here, so the calls that act on one are refused, and the lists of them are empty.
/// </summary>
[Service("warRegistry")]
public sealed partial class WarRegistryService(Npgsql.NpgsqlDataSource source, RelationStore relations, CharacterSessions? sessions, Func<long> nowMs)
{
    private const string WarKind = "war";
    private const string NegotiationNotice = "War negotiations are not available on this server.";

    /// <summary><c>MachoBindObject((allianceOrCorpID, isMaster), (method, args, kwargs))</c>: binds, then makes the nested call.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowMs())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && Eve.Macho.JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = [.. ArgsOf(nested.Items.Count > 1 ? nested.Items[1] : PyNone.Instance)],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    /// <summary>The reference's answer: the registry is the local one, so the moniker is the master's (<c>eveMoniker.py:197</c>).</summary>
    [Method("IsAllianceOrCorpLocal")]
    public PyValue IsAllianceOrCorpLocal(ServiceCall call) => new PyInt(1);

    /// <summary>The wars the owner (an argument, or the session's alliance or corporation) is in, keyed by war id.</summary>
    [Method("GetWars")]
    public async ValueTask<PyValue> GetWars(ServiceCall call)
    {
        var given = Integral(call.Arg(0)) ?? 0;
        var owner = given > 0 ? given : EntityOf(call.Session);
        var wars = await WarsAsync(call.Cancellation);
        var entries = wars.Where(war => WarsInfoMgrService.Owns(war, owner))
            .OrderBy(war => war.WarId)
            .Select(war => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(war.WarId), Payload(war)));
        return new PyDict([.. entries]);
    }

    /// <summary>No negotiation can be opened here, so there are none.</summary>
    [Method("GetNegotiations")]
    public PyValue GetNegotiations(ServiceCall call) => new PyList([]);

    /// <summary>The negotiation by id: there is none to answer with.</summary>
    [Method("GetWarNegotiation")]
    public PyValue GetWarNegotiation(ServiceCall call) => PyNone.Instance;

    [Method("CreateWarAllyOffer")]
    public PyValue CreateWarAllyOffer(ServiceCall call) => throw Notify(NegotiationNotice);

    [Method("RetractWarAllyOffer")]
    public PyValue RetractWarAllyOffer(ServiceCall call) => throw Notify(NegotiationNotice);

    [Method("CreateSurrenderNegotiation")]
    public PyValue CreateSurrenderNegotiation(ServiceCall call) => throw Notify(NegotiationNotice);

    [Method("AcceptAllyNegotiation")]
    public PyValue AcceptAllyNegotiation(ServiceCall call) => throw Notify(NegotiationNotice);

    [Method("DeclineAllyOffer")]
    public PyValue DeclineAllyOffer(ServiceCall call) => throw Notify(NegotiationNotice);

    [Method("AcceptSurrender")]
    public PyValue AcceptSurrender(ServiceCall call) => throw Notify(NegotiationNotice);

    [Method("DeclineSurrender")]
    public PyValue DeclineSurrender(ServiceCall call) => throw Notify(NegotiationNotice);

    /// <summary>
    /// <c>SetOpenForAllies(warID, state)</c>: the defender (the entity the war is against) opens or closes its war for allies. Another
    /// entity's war is left as it is, as the reference answers.
    /// </summary>
    [Method("SetOpenForAllies")]
    public async ValueTask<PyValue> SetOpenForAllies(ServiceCall call)
    {
        var warId = Integral(call.Arg(0)) ?? 0;
        var open = Truthy(call.Arg(1));
        var entity = EntityOf(call.Session);
        await ChangeWarAsync(warId, war => war.AgainstId == entity, (attrs, _) => attrs["openForAllies"] = open ? 1 : 0, null, call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>
    /// <c>RetractMutualWar(warID)</c>: a mutual war ends now when the entity is one of its two sides. Any other war is left as it is.
    /// </summary>
    [Method("RetractMutualWar")]
    public async ValueTask<PyValue> RetractMutualWar(ServiceCall call)
    {
        var warId = Integral(call.Arg(0)) ?? 0;
        var entity = EntityOf(call.Session);
        var now = nowMs();
        await ChangeWarAsync(warId, war => war.Mutual && (war.DeclaredById == entity || war.AgainstId == entity),
            (attrs, _) =>
            {
                attrs["retracted"] = Py.FileTimeFromUnixMs(now).ToString(System.Globalization.CultureInfo.InvariantCulture);
                attrs["timeFinished"] = Py.FileTimeFromUnixMs(now).ToString(System.Globalization.CultureInfo.InvariantCulture);
                attrs["retractedBy"] = entity;
            }, now, call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>
    /// Applies a change to one war when it passes <paramref name="allowed"/>, in one transaction under the war's row lock. The war is ended
    /// when <paramref name="endAtMs"/> is given. The change is announced to the war's sides after it commits.
    /// </summary>
    private async Task ChangeWarAsync(long warId, Func<WarsInfoMgrService.WarRecord, bool> allowed, Action<JsonObject, WarsInfoMgrService.WarRecord> change, long? endAtMs, CancellationToken token)
    {
        var war = (await WarsAsync(token)).FirstOrDefault(candidate => candidate.WarId == warId);
        if (war is null || !allowed(war)) return;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var current = await relations.GetAsync(WarKind, war.DeclaredById, war.AgainstId, connection, transaction, token, forUpdate: true);
        if (current is null || current.Id != warId) return;
        var attrs = (JsonNode.Parse(string.IsNullOrWhiteSpace(current.AttrsJson) ? "{}" : current.AttrsJson) as JsonObject) ?? new JsonObject();
        change(attrs, war);
        await relations.SetAsync(connection, transaction, WarKind, current.Subject, current.Object, current.Value,
            null, attrs, endAtMs ?? current.UntilMs, token);
        await transaction.CommitAsync(token);
        var updated = await WarsAsync(token);
        var after = updated.FirstOrDefault(candidate => candidate.WarId == warId);
        if (after is not null) CorporationNotificationAdapter.WarChangedBackground(sessions, after.DeclaredById, after.AgainstId, Payload(after));
    }

    private async Task<IReadOnlyList<WarsInfoMgrService.WarRecord>> WarsAsync(CancellationToken token) => await WarsInfoMgrService.ReadWarsAsync(relations, token);

    private static PyValue Payload(WarsInfoMgrService.WarRecord war) => Py.KeyVal(
        ("warID", InventoryWire.Id(war.WarId)),
        ("declaredByID", InventoryWire.Id(war.DeclaredById)),
        ("againstID", InventoryWire.Id(war.AgainstId)),
        ("warHQID", OptionalId(war.WarHqId)),
        ("warHQ", OptionalId(war.WarHqId)),
        ("timeDeclared", new PyLong(war.TimeDeclared)),
        ("timeStarted", new PyLong(war.TimeStarted)),
        ("timeFinished", OptionalTime(war.TimeFinished)),
        ("retracted", OptionalTime(war.Retracted)),
        ("retractedBy", OptionalId(war.RetractedBy)),
        ("billID", OptionalId(war.BillId)),
        ("mutual", new PyInt(war.Mutual ? 1 : 0)),
        ("openForAllies", new PyInt(war.OpenForAllies ? 1 : 0)),
        ("createdFromWarID", OptionalId(war.CreatedFromWarId)),
        ("reward", Py.Number(war.Reward)),
        ("allies", new PyDict([.. war.Allies.Select(ally => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(ally.AllyId), Py.KeyVal(
            ("allyID", InventoryWire.Id(ally.AllyId)),
            ("timeStarted", new PyLong(ally.TimeStarted)),
            ("timeFinished", OptionalTime(ally.TimeFinished)))))])));

    private static PyValue OptionalId(long? id) => id is > 0 ? InventoryWire.Id(id.Value) : PyNone.Instance;

    private static PyValue OptionalTime(long? filetime) => filetime is { } value ? new PyLong(value) : PyNone.Instance;

    private static long EntityOf(ClientSession session)
    {
        var alliance = session.GetInt("allianceid") ?? 0;
        return alliance > 0 ? alliance : session.GetInt("corpid") ?? 0;
    }

    private static long? Integral(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static bool Truthy(PyValue value) => JsSemantics.Truthy(value);

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));

    private static IEnumerable<PyValue> ArgsOf(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => [],
    };
}
