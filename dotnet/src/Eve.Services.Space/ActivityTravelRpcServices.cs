using System.Globalization;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

public sealed record SpaceRpcItem(long ItemId, long OwnerId, int TypeId, int GroupId, long Quantity);

public sealed record WormholePolarization(long EndAtUnixMs, int DurationSeconds);

public sealed record PvpFilamentQueueError(int Code, IReadOnlyList<PyValue> Arguments);

public sealed record PvpFilamentQueueParticipant(long CharacterId, ClientSession Session);

public sealed record PvpFilamentAdmissionResult(
    IReadOnlyList<PvpFilamentQueueParticipant> Participants,
    IReadOnlyList<PvpFilamentQueueError> Errors)
{
    public static PvpFilamentAdmissionResult Reject(int code, params PyValue[] arguments) =>
        new([], [new PvpFilamentQueueError(code, arguments)]);
}

public static class PvpFilamentJumpErrorCodes
{
    public const int InAbyssSystem = 1;
    public const int InvalidKeyType = 2;
    public const int InvalidOwner = 3;
    public const int InvalidShipType = 4;
    public const int ItemNotReachable = 5;
    public const int KeyNotFound = 6;
    public const int ProximityRestriction = 7;
    public const int PvpTimerActive = 8;
    public const int SafetyEngaged = 9;
    public const int SessionChangeInProgress = 10;
    public const int InvalidKeyAmount = 11;
    public const int ShipInDungeon = 12;
    public const int ShipInForceField = 13;
    public const int ShipInWarp = 14;
    public const int ShipIsCloaked = 15;
    public const int ShipIsDocked = 16;
    public const int ShipIsInvulnerable = 17;
    public const int ShipIsSelfDestructing = 18;
    public const int ShipIsTethered = 19;
    public const int ShipIsWarpScrambled = 20;
    public const int ShipNotFound = 21;
    public const int UnknownError = 22;
    public const int ShipNotInFleet = 23;
    public const int TooManyInFleet = 24;
    public const int FleetMemberNotInRange = 25;
    public const int DisableModuleBeforeJumping = 26;
    public const int OverloadedCargo = 27;
    public const int FleetRolesNeeded = 28;
    public const int TooFewInFleet = 29;
    public const int ProximityTraceAnchor = 30;
    public const int FleetSizeChanged = 31;
    public const int AlreadyInQueue = 32;
    public const int EventNotActive = 33;
    public const int CongestionBan = 34;
    public const int ModuleAndImplantRestrictions = 35;
    public const int MaxOfShipTypeRestrictions = 36;
    public const int MaxOfShipGroupRestrictions = 37;
    public const int FrigateEscapebayRestrictions = 38;
    public const int BannedModulesRestrictions = 39;
    public const int LimitedNumberOfModulesRestriction = 40;
}

public sealed class PvpFilamentMgrService(
    PvpFilamentEventCatalog? catalog = null,
    Func<ServiceCall, PvpFilamentEvent, CancellationToken, ValueTask<PvpFilamentAdmissionResult>>? admit = null,
    Func<DateTimeOffset>? utcNow = null) : IService
{
    private readonly PvpFilamentEventCatalog _catalog = catalog ?? PvpFilamentEventCatalog.LoadDefault();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly object _queueLock = new();
    // shortcut: no match runtime or scene-tick hook, queue membership lasts until leave or disconnect; add tick revalidation with the match runtime.
    private readonly Dictionary<long, QueueRecord> _queuedByCharacter = [];

    public string Name => "pvpFilamentMgr";

    public bool HasMethod(string method) => method is
        "AbyssalPVPEndGateActivation" or "GetActiveEvents" or "GetAllEvents" or
        "GetCharacterStatistics" or "GetLeaderboard" or "GetMostRecentEvent" or
        "GetNextEventDate" or "JoinPVPQueue" or "LeavePVPQueue";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        switch (call.Method)
        {
            case "GetAllEvents":
                return EventDictionary(_catalog.ListAllEvents());
            case "GetActiveEvents":
                return EventDictionary(_catalog.ListActiveEvents(_utcNow()));
            case "GetMostRecentEvent":
                return _catalog.GetMostRecentEvent(_utcNow()) is { } recent ? EventPayload(recent) : PyNone.Instance;
            case "GetNextEventDate":
                return _catalog.GetNextEventDate(_utcNow()) is { } next ? PythonDate(next) : PyNone.Instance;
            case "GetLeaderboard":
                call.Session.SendNotification("OnPVPFilamentsLeaderboard", "clientID", Leaderboard(call));
                return PyNone.Instance;
            case "GetCharacterStatistics":
                call.Session.SendNotification("OnPVPFilamentsCharacterStatistics", "clientID", CharacterStatistics(call));
                return PyNone.Instance;
            case "JoinPVPQueue":
                return await JoinQueueAsync(call).ConfigureAwait(false);
            case "LeavePVPQueue":
                LeaveQueue(call);
                return PyNone.Instance;
            case "AbyssalPVPEndGateActivation":
                throw MachoErrors.ThrowUserError("DeniedTargetAttemptFailed");
            default:
                return PyNone.Instance;
        }
    }

    private async ValueTask<PyValue> JoinQueueAsync(ServiceCall call)
    {
        var typeId = TravelRpcWire.Integer(call.Arg(1));
        var activeEvent = typeId is > 0 and <= int.MaxValue
            ? _catalog.GetActiveEventByFilamentTypeId((int)typeId, _utcNow())
            : null;
        if (activeEvent is null)
            throw PvpError([new PvpFilamentQueueError(PvpFilamentJumpErrorCodes.EventNotActive, [])]);

        var admission = admit is null
            ? PvpFilamentAdmissionResult.Reject(PvpFilamentJumpErrorCodes.ShipNotFound)
            : await admit(call, activeEvent, call.Cancellation).ConfigureAwait(false);
        if (admission.Errors.Count != 0)
            throw PvpError(admission.Errors);
        if (!ReferenceEquals(_catalog.GetActiveEventByFilamentTypeId((int)typeId, _utcNow()), activeEvent))
            throw PvpError([new PvpFilamentQueueError(PvpFilamentJumpErrorCodes.EventNotActive, [])]);

        var characterId = TravelRpcWire.CharacterId(call.Session);
        if (admission.Participants.Count == 0 ||
            !admission.Participants.Any(participant => participant.CharacterId == characterId && ReferenceEquals(participant.Session, call.Session)) ||
            admission.Participants.Any(participant => participant.CharacterId <= 0 || participant.Session.CharacterId != participant.CharacterId) ||
            admission.Participants.Select(participant => participant.CharacterId).Distinct().Count() != admission.Participants.Count)
            throw PvpError([new PvpFilamentQueueError(PvpFilamentJumpErrorCodes.ShipNotFound, [])]);

        var record = new QueueRecord(admission.Participants);
        lock (_queueLock)
        {
            if (admission.Participants.Any(participant => _queuedByCharacter.ContainsKey(participant.CharacterId)))
                throw PvpError([new PvpFilamentQueueError(PvpFilamentJumpErrorCodes.AlreadyInQueue, [])]);
            foreach (var participant in admission.Participants)
            {
                _queuedByCharacter.Add(participant.CharacterId, record);
                Action handler = () => OnSessionClosed(participant.Session);
                record.CloseHandlers.Add(participant.Session, handler);
                participant.Session.Closed += handler;
            }
        }
        return new PyInt(0);
    }

    private void LeaveQueue(ServiceCall call)
    {
        var characterId = TravelRpcWire.CharacterId(call.Session);
        QueueRecord? record;
        lock (_queueLock)
        {
            if (!_queuedByCharacter.TryGetValue(characterId, out record) ||
                !record.Participants.Any(participant => participant.CharacterId == characterId && ReferenceEquals(participant.Session, call.Session)))
                return;
            RemoveQueueRecord(record);
        }

        var failure = ErrorList([new PvpFilamentQueueError(PvpFilamentJumpErrorCodes.UnknownError, [])]);
        foreach (var participant in record.Participants)
            participant.Session.SendNotification("OnMatchMakingFailure", "clientID", failure);
    }

    private void OnSessionClosed(ClientSession session)
    {
        var characterId = TravelRpcWire.CharacterId(session);
        QueueRecord? record;
        lock (_queueLock)
        {
            if (!_queuedByCharacter.TryGetValue(characterId, out record) ||
                !record.Participants.Any(participant => ReferenceEquals(participant.Session, session)))
                return;
            RemoveQueueRecord(record);
        }

        var failure = ErrorList([new PvpFilamentQueueError(PvpFilamentJumpErrorCodes.ShipNotFound, [])]);
        foreach (var participant in record.Participants)
            participant.Session.SendNotification("OnMatchMakingFailure", "clientID", failure);
    }

    private void RemoveQueueRecord(QueueRecord record)
    {
        foreach (var participant in record.Participants)
        {
            if (_queuedByCharacter.TryGetValue(participant.CharacterId, out var queued) && ReferenceEquals(queued, record))
                _queuedByCharacter.Remove(participant.CharacterId);
            if (record.CloseHandlers.Remove(participant.Session, out var handler))
                participant.Session.Closed -= handler;
        }
    }

    private static MachoWrappedException PvpError(IReadOnlyList<PvpFilamentQueueError> errors) =>
        MachoErrors.ThrowUserError("PVPJumpError", ("errors", ErrorList(errors)));

    private static PyList ErrorList(IReadOnlyList<PvpFilamentQueueError> errors) => new(
        [.. errors.Select(error => (PyValue)PyList.Of(new PyInt(error.Code), new PyList(error.Arguments)))]);

    private static PyDict Leaderboard(ServiceCall call) => Dict(
        ("matchTypeID", new PyInt(TravelRpcWire.Integer(call.Arg(0)))),
        ("scheduleID", new PyInt(TravelRpcWire.Integer(call.Arg(1)))),
        ("entries", new PyList([])));

    private static PyDict CharacterStatistics(ServiceCall call) => Dict(
        ("matchTypeID", new PyInt(TravelRpcWire.Integer(call.Arg(0)))),
        ("scheduleID", new PyInt(TravelRpcWire.Integer(call.Arg(1)))),
        ("statistics", Dict(
            ("rank", new PyInt(0)),
            ("wins", new PyInt(0)),
            ("losses", new PyInt(0)),
            ("draws", new PyInt(0)))));

    private static PyDict EventDictionary(IReadOnlyList<PvpFilamentEvent> events) => new(
        [.. events.Select(entry => new KeyValuePair<PyValue, PyValue>(
            new PyStr($"{entry.MatchTypeId}_{entry.ScheduleId}"), EventPayload(entry)))]);

    private static PyObject EventPayload(PvpFilamentEvent entry) => KeyVal(
        ("matchTypeID", new PyInt(entry.MatchTypeId)),
        ("scheduleID", new PyInt(entry.ScheduleId)),
        ("filamentTypeID", new PyInt(entry.FilamentTypeId)),
        ("numberOfFleets", new PyInt(entry.NumberOfFleets)),
        ("fleetSize", new PyInt(entry.FleetSize)),
        ("shipRestrictions", new PyInt(entry.ShipRestrictions)),
        ("eventNameID", new PyInt(entry.EventNameId)),
        ("eventDescriptionID", new PyInt(entry.EventDescriptionId)),
        ("filamentDescriptionID", new PyInt(entry.FilamentDescriptionId)),
        ("restrictionsDescriptionID", new PyInt(entry.RestrictionsDescriptionId)),
        ("rewardsHintID", new PyInt(entry.RewardsHintId)),
        ("shipRestrictionsHintID", new PyInt(entry.ShipRestrictionsHintId)),
        ("rulesHintID", new PyInt(entry.RulesHintId)),
        ("QA", new PyInt(entry.Qa)),
        ("moduleAndImplantMetaLevelRestrictions", new PyInt(entry.ModuleAndImplantMetaLevelRestrictions)),
        ("maxOfShipTypeRestriction", new PyInt(entry.MaxOfShipTypeRestriction)),
        ("maxOfShipGroupRestriction", new PyInt(entry.MaxOfShipGroupRestriction)),
        ("minimumQueueSizeMultiplier", new PyInt(entry.MinimumQueueSizeMultiplier)),
        ("provingGroundLootTableID", new PyInt(entry.ProvingGroundLootTableId)),
        ("bannedModulesTypeListID", entry.BannedModulesTypeListId is { } banned ? new PyInt(banned) : PyNone.Instance),
        ("limitedAmountOfModulesTypeListID", entry.LimitedAmountOfModulesTypeListId is { } limited ? new PyInt(limited) : PyNone.Instance),
        ("numberOfLimitedModules", new PyInt(entry.NumberOfLimitedModules)),
        ("rewards", new PyList([.. entry.Rewards.Select(reward => (PyValue)KeyVal(
            ("typeID", new PyInt(reward.TypeId)), ("amount", new PyInt(reward.Amount))))])),
        ("startDate", PythonDate(entry.StartDate)),
        ("endDate", PythonDate(entry.EndDate)));

    private static PyObject KeyVal(params (string Key, PyValue Value)[] pairs) =>
        new(new PyStr("util.KeyVal"), Dict(pairs));

    private static PyDict Dict(params (string Key, PyValue Value)[] pairs) => new(
        [.. pairs.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Key), pair.Value))]);

    private static PyObjectEx PythonDate(DateOnly date) => new(
        ObjectExKind.Ex1,
        new PyTuple([
            new PyToken("datetime.date"),
            new PyTuple([new PyBytes(new byte[] { (byte)(date.Year >> 8), (byte)date.Year, (byte)date.Month, (byte)date.Day })]),
        ]),
        [],
        []);

    private sealed class QueueRecord(IReadOnlyList<PvpFilamentQueueParticipant> participants)
    {
        public IReadOnlyList<PvpFilamentQueueParticipant> Participants { get; } = participants;
        public Dictionary<ClientSession, Action> CloseHandlers { get; } = [];
    }
}

public sealed class RandomJumpMgrService(
    Func<long, CancellationToken, ValueTask<SpaceRpcItem?>>? readItem = null,
    Func<ServiceCall, SpaceRpcItem, CancellationToken, ValueTask<PyValue?>>? activate = null,
    Func<ServiceCall, long, CancellationToken, ValueTask<PyValue?>>? replay = null) : IService
{
    private static readonly HashSet<int> RandomJumpKeyGroups = [4041, 4087];

    public string Name => "randomJumpMgr";

    public bool HasMethod(string method) => method == "ActivateRandomJumpFilament";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        var itemId = TravelRpcWire.Integer(call.Arg(0));
        var characterId = TravelRpcWire.CharacterId(call.Session);
        if (itemId > 0 && replay is not null &&
            await replay(call, itemId, call.Cancellation).ConfigureAwait(false) is { } replayed)
            return replayed;

        List<PyValue> errors = [];
        SpaceRpcItem? item = null;

        if (itemId <= 0)
        {
            errors.Add(Error(6));
        }
        else
        {
            item = readItem is null ? null : await readItem(itemId, call.Cancellation).ConfigureAwait(false);
            if (item is null)
            {
                errors.Add(Error(6));
            }
            else
            {
                if (!RandomJumpKeyGroups.Contains(item.GroupId))
                    errors.Add(Error(2, characterId > 0 ? [new PyInt(characterId)] : []));
                if (characterId > 0 && item.OwnerId != characterId)
                    errors.Add(Error(3, [new PyInt(characterId)]));
                if (item.Quantity < 1)
                    errors.Add(Error(11));
            }
        }

        if (errors.Count > 0)
            throw RandomJumpError(errors);

        if (item is not null && activate is not null &&
            await activate(call, item, call.Cancellation).ConfigureAwait(false) is { } response)
            return response;

        throw RandomJumpError([Error(22)]);
    }

    private static PyList Error(int code, PyValue[]? args = null) =>
        PyList.Of(new PyInt(code), new PyList(args ?? []));

    private static MachoWrappedException RandomJumpError(IReadOnlyList<PyValue> errors) =>
        MachoErrors.ThrowUserError("RandomJumpError", ("errors", new PyList(errors.ToArray())));
}

public sealed class WarpVectorMgrService(
    Func<long, CancellationToken, ValueTask<SpaceRpcItem?>>? readItem = null,
    Func<ServiceCall, SpaceRpcItem, CancellationToken, ValueTask<PyValue?>>? useVector = null) : IService
{
    private const int WarpVectorGroupId = 4599;
    private const string DestinationUnavailable = "No verified warp vector destination is available.";

    public string Name => "warpVectorMgr";

    public bool HasMethod(string method) => method == "UseVector";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        var itemId = TravelRpcWire.Integer(call.Arg(0));
        var characterId = TravelRpcWire.CharacterId(call.Session);
        var item = itemId > 0 && readItem is not null
            ? await readItem(itemId, call.Cancellation).ConfigureAwait(false)
            : null;

        if (itemId <= 0 || item is null)
            throw Notify("Warp vector item not found.");
        if (item.GroupId != WarpVectorGroupId)
            throw Notify("The selected item is not warp vector data.");
        if (characterId > 0 && item.OwnerId != characterId)
            throw Notify("You do not own this warp vector data.");
        if (item.Quantity < 1)
            throw Notify("Warp vector data is not available.");

        if (useVector is not null &&
            await useVector(call, item, call.Cancellation).ConfigureAwait(false) is { } response)
            return response;

        throw Notify(DestinationUnavailable);
    }

    private static MachoWrappedException Notify(string text) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}

// The simulation's Jump command accepts Stargates only; actual wormhole entity and mass handling is supplied by this full-transition callback.
public sealed class WormholeMgrService(
    SpaceSessions? spaceSessions,
    Func<long, long, CancellationToken, ValueTask<WormholePolarization?>>? readPolarization = null,
    Func<ServiceCall, long, CancellationToken, ValueTask<PyValue?>>? jump = null,
    Func<ServiceCall, bool>? isPilotWarpLandingPending = null) : IService
{
    public string Name => "wormholeMgr";

    public bool HasMethod(string method) => method is "GetWormholePolarization" or "WormholeJump";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (call.Method == "GetWormholePolarization")
        {
            var endpointId = TravelRpcWire.Integer(call.Arg(0));
            var characterId = TravelRpcWire.CharacterId(call.Session);
            if (endpointId <= 0 || characterId <= 0 || readPolarization is null)
                return PyNone.Instance;

            var polarization = await readPolarization(endpointId, characterId, call.Cancellation).ConfigureAwait(false);
            return polarization is null
                ? PyNone.Instance
                : PyList.Of(
                    new PyLong(Py.FileTimeFromUnixMs(polarization.EndAtUnixMs)),
                    new PyInt(Math.Max(0, polarization.DurationSeconds)));
        }

        if (call.Method != "WormholeJump")
            return PyNone.Instance;

        var sourceEndpointId = TravelRpcWire.Integer(call.Arg(0));
        var session = call.Session;
        var character = TravelRpcWire.CharacterId(session);
        if (sourceEndpointId <= 0 || character <= 0)
            return PyNone.Instance;

        if (isPilotWarpLandingPending?.Invoke(call) == true)
        {
            session.SendNotification("OnWormholeJumpCancel", "clientID");
            return PyNone.Instance;
        }

        if (jump is null || (spaceSessions is not null && spaceSessions.ShipIdOf(session) is not > 0))
            return PyNone.Instance;

        var result = await jump(call, sourceEndpointId, call.Cancellation).ConfigureAwait(false);
        return result switch
        {
            null => PyNone.Instance,
            PyTuple tuple => new PyList(tuple.Items),
            _ => result,
        };
    }
}

internal static class TravelRpcWire
{
    public static long CharacterId(ClientSession session) =>
        session.CharacterId ?? session.GetInt("characterID") ?? session.GetInt("characterId") ?? 0;

    public static long Integer(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyBool boolean => boolean.Value ? 1 : 0,
        PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value < 9_223_372_036_854_775_808d =>
            (long)Math.Truncate(number.Value),
        PyStr text when double.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number) && number >= long.MinValue && number < 9_223_372_036_854_775_808d =>
            (long)Math.Truncate(number),
        _ => 0,
    };
}
