using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record StructureCompressionOutcome(
    long SourceItemId,
    long SourceTypeId,
    long SourceQuantity,
    long OutputItemId,
    long OutputTypeId,
    long OutputQuantity);

public sealed record StructureProfileRecord(
    long ProfileId,
    string Name,
    string Description,
    bool IsDefault);

public sealed record StructureProfileSetting(long GroupId, PyValue Value);

public sealed record StructureAnchorRequest(
    long ItemId,
    double? X,
    double? Z,
    double? RotationYaw,
    long? ProfileId,
    string StructureName,
    string Bio,
    long? ReinforceWeekday,
    long? ReinforceHour,
    PyValue ExtraConfig);

/// <summary>Typed hooks for structure operations whose runtime backend is not yet available.</summary>
public sealed record StructureRpcCallbacks
{
    public Func<ServiceCall, IReadOnlyList<long>, long, long, ValueTask<IReadOnlyList<long>>>? DropOffItemsAsync { get; init; }
    public Func<ServiceCall, long, ValueTask<StructureCompressionOutcome?>>? CompressItemInStructureAsync { get; init; }
    public Func<ServiceCall, long, ValueTask<StructureCompressionOutcome?>>? DecompressGasInStructureAsync { get; init; }
    public Func<ServiceCall, ValueTask<(double Structure, double Character)>>? GetMyGasDecompressionEfficiencyAsync { get; init; }
    public Func<ServiceCall, long, ValueTask<long?>>? GetStructurePilotAsync { get; init; }
    public Func<ServiceCall, long, ValueTask>? TakeControlAsync { get; init; }
    public Func<ServiceCall, long, ValueTask>? BoardStructureAsync { get; init; }
    public Func<ServiceCall, long, ValueTask>? EjectFromStructureAsync { get; init; }
    public Func<ServiceCall, ValueTask>? ReleaseControlAsync { get; init; }
    public Func<ServiceCall, long, long, IReadOnlyList<long>, ValueTask>? DeliverAsync { get; init; }
    public Func<ServiceCall, StructureAnchorRequest, ValueTask>? AnchorAsync { get; init; }
    public Func<ServiceCall, long, ValueTask>? UnanchorAsync { get; init; }
    public Func<ServiceCall, long, ValueTask>? CancelUnanchorAsync { get; init; }
    public Func<ServiceCall, long, string, ValueTask>? RenameStructureAsync { get; init; }
    public Func<ServiceCall, long, long, ValueTask>? DockAsync { get; init; }
    public Func<ServiceCall, long, long, bool, ValueTask>? UndockAsync { get; init; }
    public Func<ServiceCall, long, ValueTask<PyValue?>>? GetMyHangarViewStateAsync { get; init; }
    public Func<long, CancellationToken, ValueTask<IReadOnlyList<StructureProfileRecord>>>? GetProfilesAsync { get; init; }
    public Func<long, long, CancellationToken, ValueTask<IReadOnlyDictionary<long, IReadOnlyList<StructureProfileSetting>>>>? GetProfileSettingsAsync { get; init; }
    public Func<ServiceCall, long, string, string, ValueTask<long?>>? CreateProfileAsync { get; init; }
    public Func<ServiceCall, long, long, string?, string?, ValueTask>? UpdateProfileAsync { get; init; }
    public Func<ServiceCall, long, long, PyValue, ValueTask>? SaveProfileSettingsAsync { get; init; }
    public Func<ServiceCall, long, long, ValueTask>? SetDefaultProfileAsync { get; init; }
    public Func<ServiceCall, long, IReadOnlyList<long>, long, ValueTask>? ChangeProfilesAsync { get; init; }
    public Func<ServiceCall, long, long, ValueTask>? DeleteProfileAsync { get; init; }
    public Func<ServiceCall, long, long, ValueTask<long?>>? DuplicateProfileAsync { get; init; }
    public Func<ServiceCall, IReadOnlyList<long>, int, int, ValueTask>? UpdateReinforceTimesFromClientAsync { get; init; }
}

/// <summary>
/// Implements the EveJS structure RPC surface. Missing domain backends use the same empty/null
/// wire values as the reference handlers and can be supplied through <see cref="StructureRpcCallbacks"/>.
/// </summary>
public sealed class StructureRpcService : IService
{
    private const long StationManagerRole = 2048;
    private const int NoReinforcementWeekday = 255;
    private static readonly IReadOnlyDictionary<string, string[]> Methods = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["structureCargoDelivery"] = ["DropOffItems"],
        ["structureCompressionMgr"] = ["CompressItemInStructure", "DecompressGasInStructure", "GetMyGasDecompressionEfficiency"],
        ["structureControl"] = ["GetStructurePilot", "TakeControl", "BoardStructure", "EjectFromStructure", "ReleaseControl"],
        ["structureDeliveries"] = ["Deliver"],
        ["structureDeployment"] = ["Anchor", "Unanchor", "CancelUnanchor", "RenameStructure"],
        ["structureDocking"] = ["Dock", "Undock"],
        ["structureHangarViewMgr"] = ["GetMyHangarViewState"],
        ["structureProfiles"] = ["GetProfiles", "CreateProfile", "UpdateProfile", "GetProfileSettings", "SaveProfileSettings", "SetDefaultProfile", "ChangeProfiles", "DeleteProfile", "DuplicateProfile"],
        ["structureVulnerability"] = ["UpdateReinforceTimesFromClient"],
    };

    private readonly StructureRpcCallbacks _callbacks;

    public StructureRpcService(string name, StructureRpcCallbacks? callbacks = null)
    {
        if (!Methods.ContainsKey(name))
            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown structure RPC service.");
        Name = name;
        _callbacks = callbacks ?? new StructureRpcCallbacks();
    }

    public string Name { get; }

    public bool HasMethod(string method) => Methods[Name].Contains(method, StringComparer.Ordinal);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        switch ((Name, call.Method))
        {
            case ("structureCargoDelivery", "DropOffItems"):
            {
                if (_callbacks.DropOffItemsAsync is null) throwNotify("The structure service is unavailable.");
                var ids = PositiveIds(call.Arg(0));
                var moved = _callbacks.DropOffItemsAsync is { } callback
                    ? await callback(call, ids, PositiveId(call.Arg(1)), PositiveId(call.Arg(2))).ConfigureAwait(false)
                    : Array.Empty<long>();
                return IdList(moved);
            }
            case ("structureCompressionMgr", "CompressItemInStructure"):
                return CompressionResult(_callbacks.CompressItemInStructureAsync is { } compress
                    ? await compress(call, PositiveId(call.Arg(0))).ConfigureAwait(false)
                    : null);
            case ("structureCompressionMgr", "DecompressGasInStructure"):
                return CompressionResult(_callbacks.DecompressGasInStructureAsync is { } decompress
                    ? await decompress(call, PositiveId(call.Arg(0))).ConfigureAwait(false)
                    : null);
            case ("structureCompressionMgr", "GetMyGasDecompressionEfficiency"):
            {
                var efficiency = _callbacks.GetMyGasDecompressionEfficiencyAsync is { } readEfficiency
                    ? await readEfficiency(call).ConfigureAwait(false)
                    : (Structure: 0d, Character: 0d);
                return new PyList([new PyReal(efficiency.Structure), new PyReal(efficiency.Character)]);
            }
            case ("structureControl", "GetStructurePilot"):
                if (_callbacks.GetStructurePilotAsync is null) throwNotify("The structure service is unavailable.");
                return IdValue(_callbacks.GetStructurePilotAsync is { } getPilot
                    ? await getPilot(call, PositiveId(call.Arg(0))).ConfigureAwait(false)
                    : null);
            case ("structureControl", "TakeControl"):
                if (_callbacks.TakeControlAsync is null) throwNotify("The structure service is unavailable.");
                if (_callbacks.TakeControlAsync is { } takeControl)
                    await takeControl(call, PositiveId(call.Arg(0), SessionStructureId(call))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureControl", "BoardStructure"):
                if (_callbacks.BoardStructureAsync is null) throwNotify("The structure service is unavailable.");
                if (_callbacks.BoardStructureAsync is { } board)
                    await board(call, PositiveId(call.Arg(0))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureControl", "EjectFromStructure"):
                if (_callbacks.EjectFromStructureAsync is null) throwNotify("The structure service is unavailable.");
                if (_callbacks.EjectFromStructureAsync is { } eject)
                    await eject(call, PositiveId(call.Arg(0))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureControl", "ReleaseControl"):
                if (_callbacks.ReleaseControlAsync is null) throwNotify("The structure service is unavailable.");
                if (_callbacks.ReleaseControlAsync is { } release)
                    await release(call).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureDeliveries", "Deliver"):
                if (_callbacks.DeliverAsync is null) throwNotify("The structure service is unavailable.");
                if (_callbacks.DeliverAsync is { } deliver)
                    await deliver(call, PositiveId(call.Arg(0)), PositiveId(call.Arg(1)), PositiveIds(call.Arg(2))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureDeployment", "Anchor"):
                if (_callbacks.AnchorAsync is { } anchor)
                    await anchor(call, new StructureAnchorRequest(
                        PositiveId(call.Arg(0)), Number(call.Arg(1)), Number(call.Arg(2)), Number(call.Arg(3)),
                        OptionalPositiveId(call.Arg(4)), Text(call.Arg(5)), Text(call.Arg(6)),
                        OptionalInteger(call.Arg(7)), OptionalInteger(call.Arg(8)), call.Arg(9))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureDeployment", "Unanchor"):
                if (_callbacks.UnanchorAsync is { } unanchor)
                    await unanchor(call, PositiveId(call.Arg(0))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureDeployment", "CancelUnanchor"):
                if (_callbacks.CancelUnanchorAsync is { } cancel)
                    await cancel(call, PositiveId(call.Arg(0))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureDeployment", "RenameStructure"):
                if (_callbacks.RenameStructureAsync is { } rename)
                    await rename(call, PositiveId(call.Arg(0)), Text(call.Arg(1))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureDocking", "Dock"):
                if (_callbacks.DockAsync is { } dock)
                    await dock(call, PositiveId(call.Arg(0)), PositiveId(call.Arg(1))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureDocking", "Undock"):
                if (_callbacks.UndockAsync is { } undock)
                    await undock(call, PositiveId(call.Arg(0)), PositiveId(call.Arg(1)), PyBool.True.Equals(call.Kwarg("ignoreContraband"))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureHangarViewMgr", "GetMyHangarViewState"):
            {
                var structureId = SessionStructureId(call);
                return structureId > 0 && _callbacks.GetMyHangarViewStateAsync is { } getState
                    ? await getState(call, structureId).ConfigureAwait(false) ?? PyNone.Instance
                    : PyNone.Instance;
            }
            case ("structureProfiles", "GetProfiles"):
            {
                var corporationId = SessionCorporationId(call);
                if (corporationId <= 0)
                    return new PyList([]);
                RequireStationManager(call);
                var profiles = _callbacks.GetProfilesAsync is { } getProfiles
                    ? await getProfiles(corporationId, call.Cancellation).ConfigureAwait(false)
                    : Array.Empty<StructureProfileRecord>();
                return new PyList(profiles.Select(ProfileKeyVal).Cast<PyValue>().ToArray());
            }
            case ("structureProfiles", "CreateProfile"):
            {
                RequireStationManager(call);
                return IdValue(_callbacks.CreateProfileAsync is { } create
                    ? await create(call, SessionCorporationId(call), Text(call.Arg(0)), Text(call.Arg(1))).ConfigureAwait(false)
                    : null);
            }
            case ("structureProfiles", "UpdateProfile"):
                RequireStationManager(call);
                if (_callbacks.UpdateProfileAsync is { } update)
                    await update(call, SessionCorporationId(call), PositiveId(call.Arg(0)), OptionalText(call.Arg(1)), OptionalText(call.Arg(2))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureProfiles", "GetProfileSettings"):
            {
                RequireStationManager(call);
                var settings = _callbacks.GetProfileSettingsAsync is { } getSettings
                    ? await getSettings(SessionCorporationId(call), PositiveId(call.Arg(0)), call.Cancellation).ConfigureAwait(false)
                    : EmptyProfileSettings;
                return ProfileSettingsDict(settings);
            }
            case ("structureProfiles", "SaveProfileSettings"):
                RequireStationManager(call);
                if (_callbacks.SaveProfileSettingsAsync is { } saveSettings)
                    await saveSettings(call, SessionCorporationId(call), PositiveId(call.Arg(0)), call.Arg(1)).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureProfiles", "SetDefaultProfile"):
                RequireStationManager(call);
                if (_callbacks.SetDefaultProfileAsync is { } setDefault)
                    await setDefault(call, SessionCorporationId(call), PositiveId(call.Arg(0))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureProfiles", "ChangeProfiles"):
                RequireStationManager(call);
                if (_callbacks.ChangeProfilesAsync is { } changeProfiles)
                    await changeProfiles(call, SessionCorporationId(call), PositiveIds(call.Arg(0)), PositiveId(call.Arg(1))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureProfiles", "DeleteProfile"):
                RequireStationManager(call);
                if (_callbacks.DeleteProfileAsync is { } deleteProfile)
                    await deleteProfile(call, SessionCorporationId(call), PositiveId(call.Arg(0))).ConfigureAwait(false);
                return PyNone.Instance;
            case ("structureProfiles", "DuplicateProfile"):
                RequireStationManager(call);
                return IdValue(_callbacks.DuplicateProfileAsync is { } duplicate
                    ? await duplicate(call, SessionCorporationId(call), PositiveId(call.Arg(0))).ConfigureAwait(false)
                    : null);
            case ("structureVulnerability", "UpdateReinforceTimesFromClient"):
            {
                var ids = PositiveIds(call.Arg(0));
                if (ids.Count == 0)
                    throwNotify("No structures were selected.");
                var weekday = Integer(call.Arg(1), NoReinforcementWeekday);
                if (weekday != NoReinforcementWeekday && (weekday < 0 || weekday > 6))
                    throwNotify("Invalid reinforcement day.");
                var hour = Integer(call.Arg(2), -1);
                if (hour < 0 || hour > 23)
                    throwNotify("Invalid reinforcement hour.");
                if (_callbacks.UpdateReinforceTimesFromClientAsync is { } updateTimes)
                    await updateTimes(call, ids, weekday, hour).ConfigureAwait(false);
                return PyNone.Instance;
            }
            default:
                return PyNone.Instance;
        }
    }

    private static readonly IReadOnlyDictionary<long, IReadOnlyList<StructureProfileSetting>> EmptyProfileSettings =
        new Dictionary<long, IReadOnlyList<StructureProfileSetting>>();

    private static PyValue CompressionResult(StructureCompressionOutcome? result) => result is null
        ? PyNone.Instance
        : new PyList([
            new PyInt(result.SourceItemId), new PyInt(result.SourceTypeId), new PyInt(result.SourceQuantity),
            new PyInt(result.OutputItemId), new PyInt(result.OutputTypeId), new PyInt(result.OutputQuantity),
        ]);

    private static PyValue IdValue(long? id) => id is > 0 ? new PyInt(id.Value) : PyNone.Instance;

    private static PyList IdList(IEnumerable<long> ids) =>
        new(ids.Where(id => id > 0).Select(id => (PyValue)new PyInt(id)).ToArray());

    private static PyValue ProfileKeyVal(StructureProfileRecord profile) => new PyObjectEx(
        ObjectExKind.Ex1,
        new PyStr("util.KeyVal"),
        [],
        [
            Pair("profileID", new PyInt(profile.ProfileId)),
            Pair("name", new PyStr(profile.Name)),
            Pair("description", new PyStr(profile.Description)),
            Pair("isDefault", profile.IsDefault ? PyBool.True : PyBool.False),
        ]);

    private static PyDict ProfileSettingsDict(
        IReadOnlyDictionary<long, IReadOnlyList<StructureProfileSetting>> settings) => new(settings
        .OrderBy(pair => pair.Key)
        .Where(pair => pair.Key > 0)
        .Select(pair => new KeyValuePair<PyValue, PyValue>(
            new PyInt(pair.Key),
            new PyList(pair.Value.Select(group => (PyValue)new PyObjectEx(
                ObjectExKind.Ex1,
                new PyStr("util.KeyVal"),
                [],
                [Pair("groupID", new PyInt(group.GroupId)), Pair("value", group.Value)])).ToArray())))
        .ToArray());

    private static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static IReadOnlyList<long> PositiveIds(PyValue value)
    {
        var values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => Array.Empty<PyValue>(),
        };
        var seen = new HashSet<long>();
        return values.Select(value => PositiveId(value)).Where(id => id > 0 && seen.Add(id)).ToArray();
    }

    private static long PositiveId(PyValue? value, long fallback = 0) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => fallback,
    };

    private static long? OptionalPositiveId(PyValue? value) => PositiveId(value) is var id && id > 0 ? id : null;

    private static long? OptionalInteger(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static int Integer(PyValue? value, int fallback) => value switch
    {
        PyInt number when number.Value is >= int.MinValue and <= int.MaxValue => (int)number.Value,
        PyLong number when number.Value is >= int.MinValue and <= int.MaxValue => (int)number.Value,
        _ => fallback,
    };

    private static double? Number(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) => number.Value,
        _ => null,
    };

    private static string Text(PyValue? value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        PyBool boolean => boolean.Value ? "true" : "false",
        PyInt integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PyLong integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PyReal number => number.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static string? OptionalText(PyValue? value) => value is PyNone or null ? null : Text(value);

    private static bool Truthy(PyValue? value) => value switch
    {
        PyBool boolean => boolean.Value,
        PyInt integer => integer.Value != 0,
        PyLong integer => integer.Value != 0,
        PyReal number => number.Value != 0 && !double.IsNaN(number.Value),
        _ => false,
    };

    private static long SessionStructureId(ServiceCall call) =>
        PositiveId(call.Session.Get("structureID"), PositiveId(call.Session.Get("structureid")));

    private static long SessionCorporationId(ServiceCall call) =>
        PositiveId(call.Session.Get("corporationID"), PositiveId(call.Session.Get("corpid")));

    private static void RequireStationManager(ServiceCall call)
    {
        var role = IntegerValue(call.Session.Get("corprole")) ??
            IntegerValue(call.Session.Get("corpRole")) ??
            IntegerValue(call.Session.Get("rolesAtAll")) ?? 0;
        if ((role & StationManagerRole) != StationManagerRole)
            throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", new PyStr("Insufficient roles")));
    }

    private static long? IntegerValue(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => null,
    };

    private static void throwNotify(string message) =>
        throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
