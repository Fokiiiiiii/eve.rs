using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record AbyssalSpawnReportRequest(int DifficultyTier, int SpawnTableId, int Repetitions, ClientSession Session);

public delegate ValueTask<PyValue?> AbyssalSpawnReportCallback(
    AbyssalSpawnReportRequest request,
    CancellationToken cancellationToken);

[Service("abyssal_content_manager")]
public sealed partial class AbyssalContentManagerService(AbyssalSpawnReportCallback? report = null)
{
    [Method("generate_abyss_npc_spawn_report")]
    public async ValueTask<PyValue> GenerateAbyssNpcSpawnReport(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var difficultyTier = Integer(call.Arg(0));
        var spawnTableId = Integer(call.Arg(1));
        var repetitions = Math.Clamp(Integer(call.Arg(2)), 0, 1000);
        if (report is { } callback &&
            await callback(new(difficultyTier, spawnTableId, repetitions, call.Session), call.Cancellation).ConfigureAwait(false) is { } payload)
            return payload;
        return Py.Dict(
            ("difficulty_tier", new PyInt(difficultyTier)),
            ("spawn_table_id", new PyInt(spawnTableId)),
            ("repetitions", new PyInt(repetitions)),
            ("average_spawn_points", new PyInt(0)),
            ("average_spent_points", new PyInt(0)),
            ("average_remaining_points", new PyInt(0)),
            ("average_number_of_npcs", new PyInt(0)),
            ("unsupported_tag_names", new PyList([])),
            ("unsupported_tags", new PyList([])),
            ("spawns", new PyList([])));
    }

    private static int Integer(PyValue value)
    {
        var number = JsSemantics.ToNumber(value);
        return double.IsFinite(number) ? (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue) : 0;
    }
}

public delegate ValueTask<PyValue?> ReadEscalatingPathDetailsCallback(
    ClientSession session,
    CancellationToken cancellationToken);

[Service("dungeonExplorationMgr")]
public sealed partial class DungeonExplorationMgrService(
    ReadEscalatingPathDetailsCallback? readDetails = null,
    Func<long>? nowUnixMs = null)
{
    private static readonly string[] Header = [
        "instanceID", "dungeon", "destDungeon", "srcDungeon", "expiryTime", "journalEntryTemplateID",
    ];

    [Method("GetMyEscalatingPathDetails")]
    public async ValueTask<PyValue> GetMyEscalatingPathDetails(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var details = readDetails is { } read
            ? await read(call.Session, call.Cancellation).ConfigureAwait(false)
            : null;
        return ObjectCaching.MethodResult(details ?? EmptyRowset(),
            (nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))(),
            versionCheck: "always");
    }

    private static PyValue EmptyRowset() => new PyObject(
        new PyStr("eve.common.script.sys.rowset.Rowset"),
        Py.Dict(
            ("header", new PyList([.. Header.Select(name => (PyValue)new PyStr(name))])),
            ("columns", new PyList([.. Header.Select(name => (PyValue)new PyStr(name))])),
            ("RowClass", new PyToken("util.Row")),
            ("lines", new PyList([]))));
}

public sealed record KeeperRpcCallbacks(
    Func<ClientSession, PyValue, CancellationToken, ValueTask<PyValue?>>? ActivateAccelerationGateAsync = null,
    Func<ClientSession, CancellationToken, ValueTask<PyValue?>>? ReadCurrentDungeonAsync = null,
    Func<long, long, CancellationToken, ValueTask<long?>>? ReadOperationSpawnpointSolarSystemAsync = null,
    Func<ClientSession, long, long, CancellationToken, ValueTask<bool>>? IsOperationSiteAsync = null);

[Service("keeper")]
public sealed partial class KeeperService(KeeperRpcCallbacks? callbacks = null)
{
    [Method("ActivateAccelerationGate")]
    public async ValueTask<PyValue> ActivateAccelerationGate(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (callbacks?.ActivateAccelerationGateAsync is { } activate &&
            await activate(call.Session, call.Arg(0), call.Cancellation).ConfigureAwait(false) is { } result)
            return result;
        throw MachoErrors.ThrowUserError("DeniedShipChanged");
    }

    [Method("GetCurrentDungeonForCharacter")]
    public async ValueTask<PyValue> GetCurrentDungeonForCharacter(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        return callbacks?.ReadCurrentDungeonAsync is { } read
            ? await read(call.Session, call.Cancellation).ConfigureAwait(false) ?? PyNone.Instance
            : PyNone.Instance;
    }

    [Method("GetLevelEditor")]
    public PyValue GetLevelEditor(ServiceCall call) => PyNone.Instance;

    [Method("GetOperationSpawnpointSolarSystem")]
    public async ValueTask<PyValue> GetOperationSpawnpointSolarSystem(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var (characterId, siteId) = OperationSiteIds(call);
        if (callbacks?.ReadOperationSpawnpointSolarSystemAsync is not { } read || siteId <= 0)
            return PyNone.Instance;
        return await read(characterId, siteId, call.Cancellation).ConfigureAwait(false) is { } systemId and > 0
            ? InventoryWireId(systemId)
            : PyNone.Instance;
    }

    [Method("IsOperationSite")]
    public async ValueTask<PyValue> IsOperationSite(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var (characterId, siteId) = OperationSiteIds(call);
        return PyValue.Of(callbacks?.IsOperationSiteAsync is { } read && siteId > 0 &&
            await read(call.Session, characterId, siteId, call.Cancellation).ConfigureAwait(false));
    }

    private static (long CharacterId, long SiteId) OperationSiteIds(ServiceCall call)
    {
        var first = Positive(call.Arg(0));
        var second = Positive(call.Arg(1));
        if (second > 0)
            return (first > 0 ? first : call.Session.CharacterId ?? 0, second);
        return (call.Session.CharacterId ?? 0, first);
    }

    private static long Positive(PyValue value) => value switch
    {
        PyInt number => Math.Max(0, number.Value),
        PyLong number => Math.Max(0, number.Value),
        _ => 0,
    };

    private static PyValue InventoryWireId(long value) => value is <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);
}

public sealed record MiningScanResult(long EntityId, long YieldTypeId, long RemainingQuantity, double Distance);

public delegate ValueTask<IReadOnlyList<MiningScanResult>> ReadMiningScanResultsCallback(
    ClientSession session,
    CancellationToken cancellationToken);

[Service("miningScanMgr")]
public sealed partial class MiningScanMgrService(ReadMiningScanResultsCallback? readResults = null)
{
    [Method("perform_scan")]
    public async ValueTask<PyValue> PerformScan(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (readResults is null) return new PyList([]);
        var results = await readResults(call.Session, call.Cancellation).ConfigureAwait(false);
        return new PyList([.. results.OrderBy(result => result.Distance).ThenBy(result => result.EntityId)
            .Select(result => (PyValue)new PyList([WireInteger(result.EntityId), WireInteger(result.YieldTypeId), WireInteger(result.RemainingQuantity)]))]);
    }

    private static PyValue WireInteger(long value) => value is >= int.MinValue and <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);
}

public enum NodeGraphOperation
{
    ProcessClientGraphMessage,
    ProcessClientBlackboardRequest,
    QaSubscribe,
    QaUnsubscribe,
    QaStartNodeGraph,
    QaStopNodeGraph,
    QaStartNode,
    QaStopNode,
    QaStopActiveNodes,
}

public sealed record NodeGraphRequest(
    NodeGraphOperation Operation,
    IReadOnlyList<PyValue> Arguments,
    PyValue KeywordArguments,
    ClientSession Session);

public delegate ValueTask NodeGraphRpcCallback(NodeGraphRequest request, CancellationToken cancellationToken);

[Service("node_graph")]
public sealed partial class NodeGraphService(NodeGraphRpcCallback? callback = null)
{
    [Method("process_client_graph_message")]
    public ValueTask<PyValue> ProcessClientGraphMessage(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.ProcessClientGraphMessage, PyNone.Instance,
            messageKeyAllowed: call.Arg(2) is not PyStr key || key.Value.StartsWith("client_", StringComparison.Ordinal));

    [Method("process_client_blackboard_request")]
    public ValueTask<PyValue> ProcessClientBlackboardRequest(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.ProcessClientBlackboardRequest, PyNone.Instance);

    [Method("qa_subscribe")]
    public ValueTask<PyValue> QaSubscribe(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.QaSubscribe, new PyDict([]));

    [Method("qa_unsubscribe")]
    public ValueTask<PyValue> QaUnsubscribe(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.QaUnsubscribe, PyNone.Instance);

    [Method("qa_start_node_graph")]
    public ValueTask<PyValue> QaStartNodeGraph(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.QaStartNodeGraph, PyNone.Instance);

    [Method("qa_stop_node_graph")]
    public ValueTask<PyValue> QaStopNodeGraph(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.QaStopNodeGraph, PyNone.Instance);

    [Method("qa_start_node")]
    public ValueTask<PyValue> QaStartNode(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.QaStartNode, PyNone.Instance);

    [Method("qa_stop_node")]
    public ValueTask<PyValue> QaStopNode(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.QaStopNode, PyNone.Instance);

    [Method("qa_stop_active_nodes")]
    public ValueTask<PyValue> QaStopActiveNodes(ServiceCall call) =>
        Dispatch(call, NodeGraphOperation.QaStopActiveNodes, PyNone.Instance);

    private ValueTask<PyValue> Dispatch(ServiceCall call, NodeGraphOperation operation, PyValue result, bool messageKeyAllowed = true)
    {
        if (!messageKeyAllowed)
            return ValueTask.FromResult(result);
        if (IsQaOperation(operation) && !GameMasterRoles.Has(call.Session, GameMasterRoles.Staff))
            return ValueTask.FromResult(result);
        if (callback is { } handler)
            return ForwardAsync(handler, new NodeGraphRequest(operation, call.Args, call.Kwargs, call.Session), result, call.Cancellation);
        return ValueTask.FromResult(result);
    }

    private static bool IsQaOperation(NodeGraphOperation operation) =>
        operation is NodeGraphOperation.QaSubscribe or NodeGraphOperation.QaUnsubscribe
            or NodeGraphOperation.QaStartNodeGraph or NodeGraphOperation.QaStopNodeGraph
            or NodeGraphOperation.QaStartNode or NodeGraphOperation.QaStopNode
            or NodeGraphOperation.QaStopActiveNodes;

    private static async ValueTask<PyValue> ForwardAsync(
        NodeGraphRpcCallback handler,
        NodeGraphRequest request,
        PyValue result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await handler(request, cancellationToken).ConfigureAwait(false);
        return result;
    }
}

public sealed record PeaceTreaty(
    long OtherOwnerId,
    long? ExpiryFiletime,
    int PeaceReason,
    long WarId,
    int ReasonEnded,
    long? WarHqId);

public sealed record PeaceTreatySnapshot(IReadOnlyList<PeaceTreaty> Outgoing, IReadOnlyList<PeaceTreaty> Incoming);

public delegate ValueTask<PeaceTreatySnapshot?> ReadPeaceTreatiesCallback(long ownerId, CancellationToken cancellationToken);

[Service("peaceTreatyMgr")]
public sealed partial class PeaceTreatyMgrService(ReadPeaceTreatiesCallback? readTreaties = null)
{
    private const string RowsetName = "carbon.common.script.sys.crowset.CRowset";
    private static readonly string[] Columns = ["otherOwnerID", "expiryDate", "peaceReason", "warID", "reasonEnded", "warHQ"];
    private static readonly int[] Types = [0x03, 0x40, 0x11, 0x03, 0x11, 0x14];
    private static readonly Descriptor RowDescriptor = Descriptor.FromHeader(new PyObjectEx(
        ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"),
            Py.Tuple(Py.Tuple([.. Columns.Select((name, index) => (PyValue)Py.Tuple(new PyStr(name), new PyInt(Types[index])))]))),
        [], []));

    [Method("GetPeaceTreatiesForSession")]
    public async ValueTask<PyValue> GetPeaceTreatiesForSession(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var ownerId = call.Session.GetInt("allianceID") ?? 0;
        if (ownerId == 0)
            ownerId = call.Session.GetInt("allianceid") ?? 0;
        if (ownerId == 0)
            ownerId = call.Session.GetInt("corporationID") ?? 0;
        if (ownerId == 0)
            ownerId = call.Session.GetInt("corpid") ?? 0;
        var treaties = ownerId > 0 && readTreaties is { } read
            ? await read(ownerId, call.Cancellation).ConfigureAwait(false)
            : null;
        return Py.Tuple(Rowset(treaties?.Outgoing ?? []), Rowset(treaties?.Incoming ?? []));
    }

    private static PyValue Rowset(IReadOnlyList<PeaceTreaty> treaties)
    {
        var header = Py.Tuple(
            Py.Tuple(new PyToken(RowsetName)),
            Py.Dict(("header", RowDescriptor.Header)));
        var rows = treaties.Select(treaty => (PyValue)new PyPackedRow(RowDescriptor,
        [
            Id(treaty.OtherOwnerId),
            treaty.ExpiryFiletime is { } expiry ? new PyLong(expiry) : PyNone.Instance,
            new PyInt(treaty.PeaceReason),
            Id(treaty.WarId),
            new PyInt(treaty.ReasonEnded),
            treaty.WarHqId is { } hq ? Id(hq) : PyNone.Instance,
        ])).ToArray();
        return new PyObjectEx(ObjectExKind.Ex2, header, rows, []);
    }

    private static PyValue Id(long value) => value is >= int.MinValue and <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);
}

public enum PublicQaOperation { MoveMeTo, SlashCmd, CanGiveItemForMultifit }

public sealed record PublicQaRequest(PublicQaOperation Operation, IReadOnlyList<PyValue> Arguments, PyValue KeywordArguments, PyValue CallId, ClientSession Session);

public sealed record PublicQaToolsRpcCallbacks(
    Func<ClientSession, bool>? IsStaff = null,
    Func<PublicQaRequest, CancellationToken, ValueTask>? MoveMeToAsync = null,
    Func<PublicQaRequest, CancellationToken, ValueTask<PyValue?>>? SlashCmdAsync = null,
    Func<PublicQaRequest, CancellationToken, ValueTask<bool>>? CanGiveItemForMultifitAsync = null);

[Service("publicQaToolsServer")]
public sealed partial class PublicQaToolsServerService(PublicQaToolsRpcCallbacks? callbacks = null)
{
    private const long StaffRoleMask = GameMasterRoles.Staff;

    public static bool HasStaffAccountRole(ClientSession session) =>
        GameMasterRoles.Has(session, StaffRoleMask);

    [Method("MoveMeTo")]
    public async ValueTask<PyValue> MoveMeTo(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (!HasStaffAccountRole(call.Session) || callbacks?.IsStaff?.Invoke(call.Session) == false)
            return PyNone.Instance;
        if (callbacks?.MoveMeToAsync is { } move)
            await move(Request(call, PublicQaOperation.MoveMeTo), call.Cancellation).ConfigureAwait(false);
        return PyNone.Instance;
    }

    [Method("SlashCmd")]
    public async ValueTask<PyValue> SlashCmd(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (!HasStaffAccountRole(call.Session) || callbacks?.IsStaff?.Invoke(call.Session) == false ||
            callbacks?.SlashCmdAsync is not { } slash)
            return PyNone.Instance;
        return await slash(Request(call, PublicQaOperation.SlashCmd), call.Cancellation).ConfigureAwait(false) ?? PyNone.Instance;
    }

    [Method("CanGiveItemForMultifit")]
    public async ValueTask<PyValue> CanGiveItemForMultifit(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var allowed = HasStaffAccountRole(call.Session) && callbacks?.IsStaff?.Invoke(call.Session) != false &&
            callbacks?.CanGiveItemForMultifitAsync is { } check &&
            await check(Request(call, PublicQaOperation.CanGiveItemForMultifit), call.Cancellation).ConfigureAwait(false);
        return PyValue.Of(allowed);
    }

    private static PublicQaRequest Request(ServiceCall call, PublicQaOperation operation) =>
        new(operation, call.Args, call.Kwargs, call.CallId, call.Session);
}

public delegate ValueTask<IReadOnlyDictionary<long, PyValue>> ReadCommunityFittingsCallback(
    ClientSession session,
    CancellationToken cancellationToken);

public sealed record CustomAgencyContentRequest(ClientSession Session, IReadOnlyList<PyValue> Arguments);

public sealed record CustomAgencyProviderRpcCallbacks(
    Func<CustomAgencyContentRequest, CancellationToken, ValueTask<IReadOnlyList<PyValue>>>? ReadContentAsync = null,
    Func<long, ClientSession, CancellationToken, ValueTask>? WarpToContentAsync = null);

[Service("custom_agency_provider")]
public sealed partial class CustomAgencyProviderService(CustomAgencyProviderRpcCallbacks? callbacks = null)
{
    [Method("get_content_data")]
    public async ValueTask<PyValue> GetContentData(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var items = callbacks?.ReadContentAsync is { } read
            ? await read(new CustomAgencyContentRequest(call.Session, call.Args), call.Cancellation).ConfigureAwait(false)
            : [];
        return new PyList([.. items]);
    }

    [Method("warp_to_content")]
    public async ValueTask<PyValue> WarpToContent(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var contentId = Positive(call.Arg(0));
        if (contentId > 0 && callbacks?.WarpToContentAsync is { } warp)
            await warp(contentId, call.Session, call.Cancellation).ConfigureAwait(false);
        return PyNone.Instance;
    }

    private static long Positive(PyValue value) => value switch
    {
        PyInt number => Math.Max(0, number.Value),
        PyLong number => Math.Max(0, number.Value),
        PyReal number when double.IsFinite(number.Value) => Math.Max(0, (long)number.Value),
        _ => 0,
    };
}

public delegate ValueTask<bool> ReadEpicArcStatusCallback(
    long agentId,
    long characterId,
    CancellationToken cancellationToken);

[Service("epicArcStatus")]
public sealed partial class EpicArcStatusService(
    ReadEpicArcStatusCallback? agentHasMissions = null)
{
    [Method("AgentHasEpicMissionsForCharacter")]
    public async ValueTask<PyValue> AgentHasEpicMissionsForCharacter(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var agentId = Positive(call.Arg(0));
        var characterId = call.Session.CharacterId ?? 0;
        var hasMissions = agentId > 0 && characterId > 0 && agentHasMissions is { } read &&
            await read(agentId, characterId, call.Cancellation).ConfigureAwait(false);
        return PyValue.Of(hasMissions);
    }

    private static long Positive(PyValue value) => value switch
    {
        PyInt number => Math.Max(0, number.Value),
        PyLong number => Math.Max(0, number.Value),
        _ => 0,
    };
}

public sealed record SuperWeaponPointRequest(long ModuleId, long EffectId, PyValue Destination, ClientSession Session);
public sealed record SuperWeaponSlashRequest(long ModuleId, PyValue StartPoint, PyValue EndPoint, ClientSession Session);

public sealed record SuperWeaponRpcCallbacks(
    Func<SuperWeaponPointRequest, CancellationToken, ValueTask<bool>>? ActivatePointTargetedAsync = null,
    Func<SuperWeaponSlashRequest, CancellationToken, ValueTask<bool>>? ActivateSlashAsync = null);

[Service("superWeaponMgr")]
public sealed partial class SuperWeaponMgrService(SuperWeaponRpcCallbacks? callbacks = null)
{
    /// <summary>
    /// The user error of a superweapon call this server does not serve. It is an error, not <c>PyNone</c>: the client reads <c>PyNone</c> as an activation
    /// that went through (AGENTS rule 4), so an unwired route must say so.
    /// </summary>
    public static MachoWrappedException NotWired() =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Superweapon activation is not wired on this server.")));

    /// <summary>A callback that answers no: the activation did not go through, and the client is told so (never a silent <c>PyNone</c>).</summary>
    private static MachoWrappedException Refused() =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("The superweapon cannot be activated.")));

    [Method("ActivateSinglePointTargetedModule")]
    public async ValueTask<PyValue> ActivateSinglePointTargetedModule(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var moduleId = Positive(call.Arg(0));
        var effectId = Positive(call.Arg(1));
        var activate = callbacks?.ActivatePointTargetedAsync ?? throw NotWired();
        if (moduleId <= 0)
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        return await activate(new SuperWeaponPointRequest(moduleId, effectId, call.Arg(2), call.Session), call.Cancellation).ConfigureAwait(false)
            ? new PyInt(1)
            : throw Refused();
    }

    [Method("ActivateSlashModule")]
    public async ValueTask<PyValue> ActivateSlashModule(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var moduleId = Positive(call.Arg(0));
        var activate = callbacks?.ActivateSlashAsync ?? throw NotWired();
        if (moduleId <= 0)
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        return await activate(new SuperWeaponSlashRequest(moduleId, call.Arg(1), call.Arg(2), call.Session), call.Cancellation).ConfigureAwait(false)
            ? new PyInt(1)
            : throw Refused();
    }

    private static long Positive(PyValue value) => value switch
    {
        PyInt number => Math.Max(0, number.Value),
        PyLong number => Math.Max(0, number.Value),
        _ => 0,
    };
}

public enum TourneyQuery { GetTourneySetup, QueryShipList, GetPotentialMatches, GetAllCurrentSeriesMatchDetails, QueryTournamentDetails }

public sealed record TourneyQueryRequest(TourneyQuery Query, long? TournamentId, ClientSession Session);

public delegate ValueTask<PyValue?> TourneyQueryCallback(TourneyQueryRequest request, CancellationToken cancellationToken);

[Service("tourneyMgr")]
public sealed partial class TourneyMgrService(TourneyQueryCallback? query = null)
{
    [Method("GetTourneySetup")]
    public ValueTask<PyValue> GetTourneySetup(ServiceCall call) =>
        QueryAsync(call, TourneyQuery.GetTourneySetup, null, Py.Tuple(new PyList([]), new PyList([]), new PyList([])));

    [Method("QueryShipList")]
    public ValueTask<PyValue> QueryShipList(ServiceCall call) =>
        QueryAsync(call, TourneyQuery.QueryShipList, Positive(call.Arg(0)), new PyList([]));

    [Method("GetPotentialMatches")]
    public ValueTask<PyValue> GetPotentialMatches(ServiceCall call) =>
        QueryAsync(call, TourneyQuery.GetPotentialMatches, Positive(call.Arg(0)), new PyList([]));

    [Method("GetAllCurrentSeriesMatchDetails")]
    public ValueTask<PyValue> GetAllCurrentSeriesMatchDetails(ServiceCall call) =>
        QueryAsync(call, TourneyQuery.GetAllCurrentSeriesMatchDetails, Positive(call.Arg(0)), new PyList([]));

    [Method("QueryTournamentDetails")]
    public ValueTask<PyValue> QueryTournamentDetails(ServiceCall call) =>
        QueryAsync(call, TourneyQuery.QueryTournamentDetails, Positive(call.Arg(0)), PyNone.Instance);

    private ValueTask<PyValue> QueryAsync(ServiceCall call, TourneyQuery queryType, long? tournamentId, PyValue fallback)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        return query is { } callback
            ? CompleteAsync(callback, new TourneyQueryRequest(queryType, tournamentId, call.Session), call.Cancellation, fallback)
            : ValueTask.FromResult(fallback);
    }

    private static async ValueTask<PyValue> CompleteAsync(
        TourneyQueryCallback callback,
        TourneyQueryRequest request,
        CancellationToken cancellationToken,
        PyValue fallback) => await callback(request, cancellationToken).ConfigureAwait(false) ?? fallback;

    private static long? Positive(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        _ => null,
    };
}
