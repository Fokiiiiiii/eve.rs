using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

[Service("allianceRegistry")]
public sealed partial class AllianceRegistryService(CorporationStore store,PgCharacterStore characters,Func<long> now) : IBoundObjectReleaseHandler
{
    private readonly ConcurrentDictionary<string,long> _alliancesByObject = new(StringComparer.Ordinal);
    private static long Number(PyValue value) => value switch { PyInt n => n.Value,PyLong n => n.Value,PyTuple { Items.Count:>0 } t => Number(t.Items[0]),_ => 0 };
    private async Task<long> Id(ServiceCall call)
    {
        if (Number(call.Arg(0)) is > 0 and var requested) return requested;
        if (call.BoundObjectId is { } oid && _alliancesByObject.TryGetValue(oid,out var bound)) return bound;
        return call.Session.CharacterId is { } id ? (await characters.GetAsync(id,call.Cancellation))?.AllianceId ?? 0 : 0;
    }
    [Method("GetAlliance")]
    public async ValueTask<PyValue> GetAlliance(ServiceCall call)
    {
        if (await store.AllianceAsync(await Id(call),call.Cancellation) is not { } a) return PyNone.Instance;
        var members = await store.AllianceMembersAsync(a.Id,call.Cancellation);
        var count = 0;
        foreach (var member in members) count += (await characters.ForCorporationAsync(member.CorporationId,call.Cancellation)).Count;
        (string Key,PyValue Value)[] fields = [
            ("allianceID",new PyInt(a.Id)),("allianceName",new PyStr(a.Name)),("shortName",new PyStr(a.ShortName)),
            ("executorCorpID",CorporationWire.Id(a.ExecutorCorporationId)),("creatorCorpID",new PyInt(a.CreatorCorporationId)),
            ("creatorCharID",new PyInt(a.CreatorCharacterId)),("warFactionID",PyNone.Instance),("description",new PyStr(a.Description)),
            ("url",string.IsNullOrWhiteSpace(a.Url) ? PyNone.Instance : new PyStr(a.Url)),("startDate",CorporationWire.Time(a.CreatedMs)),
            ("memberCount",new PyInt(count)),("dictatorial",new PyInt(a.Dictatorial ? 1 : 0)),("allowWar",new PyInt(0)),
            ("currentCapital",PyNone.Instance),("currentPrimeHour",new PyInt(0)),("newPrimeHour",new PyInt(0)),
            ("newPrimeHourValidAfter",new PyLong(0)),("deleted",new PyInt(a.Deleted ? 1 : 0)),
        ];
        return Py.KeyVal([.. fields,("__header__",Py.Tuple([.. fields.Select(f => (PyValue)new PyStr(f.Key))])),
            ("currentCapitalSystem",PyNone.Instance),("newCapitalSystem",PyNone.Instance),("newCapitalSystemValidAfter",new PyLong(0))]);
    }
    [Method("GetAlliancePublicInfo")]
    public ValueTask<PyValue> GetAlliancePublicInfo(ServiceCall call) => GetAlliance(call);

    private static readonly string[] MemberHeader = ["corporationID","allianceID","chosenExecutorID","startDate","deleted"];
    private static readonly Descriptor MemberDescriptor = CorporationWire.DescriptorOf(
        ("corporationID",3),("allianceID",3),("chosenExecutorID",3),("startDate",64),("deleted",11));
    private static PyValue[] Line(AllianceMember m,long? viewer) => [new PyInt(m.CorporationId),new PyInt(m.AllianceId),
        m.CorporationId == viewer ? CorporationWire.Id(m.ChosenExecutorId) : PyNone.Instance,CorporationWire.Time(m.StartedMs),new PyInt(0)];
    [Method("GetAllianceMembers")]
    public async ValueTask<PyValue> GetAllianceMembers(ServiceCall call) =>
        Py.Rowset(MemberHeader,(await store.AllianceMembersAsync(await Id(call),call.Cancellation)).Select(m => (PyValue)new PyList(Line(m,null))));

    [Method("GetMembers")]
    public async ValueTask<PyValue> GetMembers(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } id || await characters.GetAsync(id,call.Cancellation) is not { AllianceId:not null } c)
            throw MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr("Alliance membership required.")));
        var members = await store.AllianceMembersAsync(c.AllianceId.Value,call.Cancellation);
        return new PyObjectEx(ObjectExKind.Ex2,Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
            Py.Dict(("header",MemberDescriptor.Header),("idName",new PyStr("corporationID")),("columnName",new PyStr("corporationID")))),[],
            [.. members.Select(m => new KeyValuePair<PyValue,PyValue>(new PyInt(m.CorporationId),new PyPackedRow(MemberDescriptor,Line(m,c.CorporationId))))]);
    }
    [Method("MachoResolveObject")]
    public async ValueTask<PyValue> MachoResolveObject(ServiceCall call) =>
        await store.AllianceAsync(await Id(call),call.Cancellation) is not null ? new PyInt(MachoMessage.ProxyNodeId) : PyNone.Instance;

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var alliance = await Id(call);
        if (await store.AllianceAsync(alliance,call.Cancellation) is null) return PyNone.Instance;
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _alliancesByObject[oid] = alliance;
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count:>0 } nested)
        {
            var args = nested.Items.Count>1 ? nested.Items[1] switch { PyTuple t => t.Items,PyList l => l.Items,_ => [] } : [];
            result = await ((IService)this).CallAsync(new ServiceCall { Method=JsSemantics.StringValue(nested.Items[0]) ?? "",Args=args,
                Kwargs=nested.Items.Count>2 ? nested.Items[2] : PyNone.Instance,Session=call.Session,CallId=call.CallId,BoundObjectId=oid,Cancellation=call.Cancellation });
        }
        return Py.Tuple(new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid),CorporationWire.Time(now())))),result);
    }
    public void OnBoundObjectReleased(string oid,ClientSession? session) => _alliancesByObject.TryRemove(oid,out _);
}
