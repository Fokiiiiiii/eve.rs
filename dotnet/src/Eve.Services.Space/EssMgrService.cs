using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

public enum EssMgrOperation
{
    AttemptLinkToMainBank,
    AttemptLinkToReserveBank,
    RequestMainBankUnlink,
    RequestReserveBankUnlink,
    RequestUnlockReserveBank,
}

public sealed record EssMgrMainBankLink(
    string LinkId,
    long CharacterId,
    long StartedAtFileTime,
    long CompletesAtFileTime);

public sealed record EssMgrSystemData(
    long EssId,
    long SolarSystemId,
    double CurrentOutput,
    double MainValue,
    double ReserveValue,
    long? BeaconId = null,
    long? TypeId = null,
    EssMgrMainBankLink? MainBankLink = null,
    long? ReserveBankLastPulseInitiatedFileTime = null,
    int ReserveBankPulsesRemaining = 0,
    int ReserveBankPulsesTotal = 0,
    IReadOnlySet<long>? ReserveLinkedCharacterIds = null,
    IReadOnlyList<PyValue>? MainBankThefts = null,
    IReadOnlyList<PyValue>? ReserveBankThefts = null);

public sealed record EssMgrOperationRequest(
    EssMgrOperation Operation,
    long SolarSystemId,
    long CharacterId,
    long KeyTypeId,
    ServiceCall Call);

public sealed record EssMgrOperationResult(
    bool Succeeded,
    EssMgrSystemData? State = null,
    EssMgrMainBankLink? RemovedMainBankLink = null);

public sealed record EssMgrCallbacks(
    Func<long, CancellationToken, ValueTask<EssMgrSystemData?>>? ReadSystemStateAsync = null,
    Func<EssMgrOperationRequest, CancellationToken, ValueTask<EssMgrOperationResult>>? ExecuteAsync = null);

[Service("essMgr")]
public sealed partial class EssMgrService
{
    private const long EncounterSurveillanceSystemTypeId = 55914;
    private readonly EssMgrCallbacks _callbacks;

    public EssMgrService(EssMgrCallbacks? callbacks = null) => _callbacks = callbacks ?? new();

    [Method("GetDataForClientSolarSystem")]
    public async ValueTask<PyValue> GetDataForClientSolarSystem(ServiceCall call)
    {
        var systemId = call.Args.Count == 0 ? SessionSystemId(call.Session) : PositiveInteger(call.Arg(0));
        var state = await ReadStateAsync(call, systemId).ConfigureAwait(false);
        return state is null ? PyNone.Instance : DataPayload(state);
    }

    [Method("IsClientLinkedToReserveBank")]
    public async ValueTask<PyValue> IsClientLinkedToReserveBank(ServiceCall call)
    {
        var systemId = SessionSystemId(call.Session);
        var characterId = call.Session.CharacterId ?? 0;
        if (systemId <= 0 || characterId <= 0)
            return PyBool.False;

        var state = await ReadStateAsync(call, systemId).ConfigureAwait(false);
        return state?.ReserveLinkedCharacterIds?.Contains(characterId) == true ? PyBool.True : PyBool.False;
    }

    [Method("AttemptLinkToMainBank")]
    public ValueTask<PyValue> AttemptLinkToMainBank(ServiceCall call) =>
        MutateAsync(call, EssMgrOperation.AttemptLinkToMainBank, requiresCharacter: true);

    [Method("AttemptLinkToReserveBank")]
    public ValueTask<PyValue> AttemptLinkToReserveBank(ServiceCall call) =>
        MutateAsync(call, EssMgrOperation.AttemptLinkToReserveBank, requiresCharacter: true);

    [Method("RequestMainBankUnlink")]
    public ValueTask<PyValue> RequestMainBankUnlink(ServiceCall call) =>
        MutateAsync(call, EssMgrOperation.RequestMainBankUnlink);

    [Method("RequestReserveBankUnlink")]
    public ValueTask<PyValue> RequestReserveBankUnlink(ServiceCall call) =>
        MutateAsync(call, EssMgrOperation.RequestReserveBankUnlink, requiresCharacter: true);

    [Method("RequestUnlockReserveBank")]
    public ValueTask<PyValue> RequestUnlockReserveBank(ServiceCall call) =>
        MutateAsync(call, EssMgrOperation.RequestUnlockReserveBank, NumberOrZero(call.Arg(0)));

    [Method("GetMainBankTheftsForClientSolarSystem")]
    public async ValueTask<PyValue> GetMainBankTheftsForClientSolarSystem(ServiceCall call)
    {
        var systemId = call.Args.Count == 0 ? SessionSystemId(call.Session) : PositiveInteger(call.Arg(0));
        var state = await ReadStateAsync(call, systemId).ConfigureAwait(false);
        return new PyList(state?.MainBankThefts ?? []);
    }

    [Method("GetReserveBankTheftsForClientSolarSystem")]
    public async ValueTask<PyValue> GetReserveBankTheftsForClientSolarSystem(ServiceCall call)
    {
        var systemId = call.Args.Count == 0 ? SessionSystemId(call.Session) : PositiveInteger(call.Arg(0));
        var state = await ReadStateAsync(call, systemId).ConfigureAwait(false);
        return new PyList(state?.ReserveBankThefts ?? []);
    }

    private async ValueTask<EssMgrSystemData?> ReadStateAsync(ServiceCall call, long systemId)
    {
        if (systemId <= 0 || _callbacks.ReadSystemStateAsync is not { } read)
            return null;
        return await read(systemId, call.Cancellation).ConfigureAwait(false);
    }

    private async ValueTask<PyValue> MutateAsync(
        ServiceCall call,
        EssMgrOperation operation,
        long keyTypeId = 0,
        bool requiresCharacter = false)
    {
        var systemId = SessionSystemId(call.Session);
        var characterId = call.Session.CharacterId ?? 0;
        if (systemId <= 0 || (requiresCharacter && characterId <= 0) || _callbacks.ExecuteAsync is not { } execute)
            return PyNone.Instance;

        var result = await execute(new(operation, systemId, characterId, keyTypeId, call), call.Cancellation).ConfigureAwait(false);
        if (!result.Succeeded)
            return PyNone.Instance;

        switch (operation)
        {
            case EssMgrOperation.AttemptLinkToMainBank:
                call.Session.SendNotification("OnESSMainBankLinkNotification", "clientID", Py.Number(systemId));
                break;
            case EssMgrOperation.AttemptLinkToReserveBank:
                call.Session.SendNotification("OnESSReserveBankLinkNotification", "clientID", Py.Number(systemId));
                break;
            case EssMgrOperation.RequestMainBankUnlink:
                call.Session.SendNotification(
                    "OnMainBankPlayerLinkDisconnected", "clientID",
                    Py.Dict(
                        ("solarSystemID", Py.Number(result.State?.SolarSystemId ?? 0)),
                        ("linkID", new PyStr(result.RemovedMainBankLink?.LinkId ?? string.Empty)),
                        ("characterID", Py.Number(result.RemovedMainBankLink?.CharacterId ?? 0)),
                        ("reason", new PyInt(0))));
                break;
            case EssMgrOperation.RequestReserveBankUnlink:
                call.Session.SendNotification(
                    "OnReserveBankPlayerLinkDisconnected", "clientID",
                    Py.Dict(
                        ("solarSystemID", Py.Number(result.State?.SolarSystemId ?? 0)),
                        ("characterID", Py.Number(characterId)),
                        ("reason", new PyInt(0))));
                break;
        }

        if (result.State is { } state)
            NotifyEssDataChanged(call.Session, state);
        return PyNone.Instance;
    }

    /// <summary>What a pilot is told when the ESS of its system changes (<c>OnSystemESSDataChanged</c>).</summary>
    public static void NotifyEssDataChanged(ClientSession session, EssMgrSystemData state) =>
        session.SendNotification(
            "OnSystemESSDataChanged", "clientID",
            Py.Number(state.SolarSystemId), DataPayload(state));

    private static PyValue DataPayload(EssMgrSystemData state)
    {
        if (state.EssId <= 0)
            return PyNone.Instance;

        PyValue link = state.MainBankLink is { } mainLink
            ? Py.Dict(
                ("linkID", new PyStr(mainLink.LinkId)),
                ("characterID", Py.Number(mainLink.CharacterId)),
                ("startedAt", Py.FileTimeLong(mainLink.StartedAtFileTime)),
                ("completesAt", Py.FileTimeLong(mainLink.CompletesAtFileTime)))
            : PyNone.Instance;

        return Py.Dict(
            ("essID", Py.Number(state.EssId)),
            ("beaconID", Py.Number(state.BeaconId is > 0 ? state.BeaconId.Value : state.EssId)),
            ("typeID", Py.Number(state.TypeId is > 0 ? state.TypeId.Value : EncounterSurveillanceSystemTypeId)),
            ("solarSystemID", Py.Number(state.SolarSystemId)),
            ("currentOutput", Number(state.CurrentOutput)),
            ("mainValue", Number(state.MainValue)),
            ("reserveValue", Number(state.ReserveValue)),
            ("mainBankLink", link),
            ("reserveBankLastPulseInitiated", state.ReserveBankLastPulseInitiatedFileTime is > 0
                ? Py.FileTimeLong(state.ReserveBankLastPulseInitiatedFileTime.Value)
                : PyNone.Instance),
            ("reserveBankPulsesRemaining", Py.Number(Math.Max(0, state.ReserveBankPulsesRemaining))),
            ("reserveBankPulsesTotal", Py.Number(Math.Max(0, state.ReserveBankPulsesTotal))),
            ("reserveBankActiveLinks", Py.Number(state.ReserveLinkedCharacterIds?.Count ?? 0)));
    }

    private static PyValue Number(double value) => double.IsFinite(value) ? Py.Number(value) : new PyInt(0);

    private static long SessionSystemId(ClientSession session)
    {
        var systemId = session.GetInt("solarsystemid2");
        return systemId is null or 0
            ? PositiveInteger(session.Get("solarsystemid"))
            : Math.Max(0, systemId.Value);
    }

    private static long NumberOrZero(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value == Math.Truncate(number.Value) &&
            number.Value >= long.MinValue && number.Value <= long.MaxValue => (long)number.Value,
        _ => 0,
    };

    private static long PositiveInteger(PyValue? value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        _ => 0,
    };
}
