using System.Collections.Concurrent;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Core;

[Service("bannedwords")]
public sealed partial class BannedWordsService
{
    public Func<ServiceCall, ValueTask<PyValue?>>? RpcHandler { get; set; }

    [Method("fetch_regexes")]
    public ValueTask<PyValue> FetchRegexes(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () => new PyList([]));

    [Method("check_words_allowed")]
    public ValueTask<PyValue> CheckWordsAllowed(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () => PyNone.Instance);

    [Method("check_search_words_allowed")]
    public ValueTask<PyValue> CheckSearchWordsAllowed(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () => PyNone.Instance);
}

[Service("alphaInjectorMgr")]
public sealed partial class AlphaInjectorMgrService
{
    public Func<ServiceCall, ValueTask<PyValue?>>? RpcHandler { get; set; }

    [Method("GetNextAvailableInjection")]
    public ValueTask<PyValue> GetNextAvailableInjection(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () => new PyLong(0));
}

[Service("cache")]
public sealed partial class CacheService
{
    public Func<ServiceCall, ValueTask<PyValue?>>? RpcHandler { get; set; }

    [Method("GetServerVersionAndBuild")]
    public ValueTask<PyValue> GetServerVersionAndBuild(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () =>
            Py.Tuple(new PyReal(ServerBuildInfo.BootVersion), new PyInt(ServerBuildInfo.BootBuild)));
}

[Service("communityTrackMgr")]
public sealed partial class CommunityTrackMgrService
{
    public Func<ServiceCall, ValueTask<PyValue?>>? RpcHandler { get; set; }

    private readonly ConcurrentDictionary<long, (long DefinitionId, long Progress)> _instances = new();
    private int _qaMode;

    public bool QaMode => Volatile.Read(ref _qaMode) != 0;

    public void SetInstanceAttributes(long instanceId, long definitionId, long progress)
    {
        if (instanceId <= 0) return;
        if (definitionId <= 0) _instances.TryRemove(instanceId, out _);
        else _instances[instanceId] = (definitionId, Math.Max(0, progress));
    }

    [Method("get_instance_attributes_public")]
    public ValueTask<PyValue> GetInstanceAttributesPublic(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () =>
        {
            var instanceId = Positive(call.Arg(0));
            return _instances.TryGetValue(instanceId, out var attributes)
                ? Py.Tuple(new PyLong(attributes.DefinitionId), new PyLong(attributes.Progress))
                : Py.Tuple(PyNone.Instance, PyNone.Instance);
        });

    [Method("change_qa_mode")]
    public ValueTask<PyValue> ChangeQaMode(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () =>
        {
            Interlocked.Exchange(ref _qaMode, Truthy(call.Arg(0)) ? 1 : 0);
            return PyNone.Instance;
        });

    private static long Positive(PyValue value) => value switch
    {
        PyInt number => Math.Max(0, number.Value),
        PyLong number => Math.Max(0, number.Value),
        _ => 0,
    };

    private static bool Truthy(PyValue value) => value switch
    {
        PyNone => false,
        PyBool boolean => boolean.Value,
        PyInt number => number.Value != 0,
        PyLong number => number.Value != 0,
        PyReal number => number.Value != 0 && !double.IsNaN(number.Value),
        PyStr text => text.Value.Length > 0,
        PyBytes text => text.Value.Length > 0,
        PyWString text => text.Value.Length > 0,
        _ => true,
    };
}

[Service("contextualOfferMgr")]
public sealed partial class ContextualOfferMgrService
{
    public Func<ServiceCall, ValueTask<PyValue?>>? RpcHandler { get; set; }

    private readonly ConcurrentQueue<ContextualOfferEvent> _events = new();
    private long _nextEventId;
    public IReadOnlyList<ContextualOfferEvent> Events => _events.ToArray();

    [Method("TrackOfferDelivered")]
    public ValueTask<PyValue> TrackOfferDelivered(ServiceCall call) => Track(call);

    [Method("TrackOfferSeen")]
    public ValueTask<PyValue> TrackOfferSeen(ServiceCall call) => Track(call);

    [Method("TrackOfferBuyPress")]
    public ValueTask<PyValue> TrackOfferBuyPress(ServiceCall call) => Track(call);

    [Method("TrackOfferClosed")]
    public ValueTask<PyValue> TrackOfferClosed(ServiceCall call) => Track(call);

    [Method("TrackOfferPurchased")]
    public ValueTask<PyValue> TrackOfferPurchased(ServiceCall call) => Track(call);

    [Method("TrackOfferPurchasedSeen")]
    public ValueTask<PyValue> TrackOfferPurchasedSeen(ServiceCall call) => Track(call);

    private ValueTask<PyValue> Track(ServiceCall call) => CompatibilityRpc.Invoke(call, RpcHandler, () =>
    {
        _events.Enqueue(new ContextualOfferEvent(
            Interlocked.Increment(ref _nextEventId), call.Method, Positive(call.Arg(0)),
            call.Session.CharacterId ?? 0, call.Session.UserId ?? 0, DateTimeOffset.UtcNow));
        return PyNone.Instance;
    });

    private static long Positive(PyValue value) => value switch
    {
        PyInt number => Math.Max(0, number.Value),
        PyLong number => Math.Max(0, number.Value),
        _ => 0,
    };
}

public sealed record ContextualOfferEvent(
    long EventId, string MethodName, long OfferId, long CharacterId, long AccountId, DateTimeOffset RecordedAt);

[Service("localizationServer")]
public sealed partial class LocalizationServerService
{
    public Func<ServiceCall, ValueTask<PyValue?>>? RpcHandler { get; set; }

    private int _qaWrapActive;
    private readonly ConcurrentQueue<LocalizationServerEvent> _events = new();
    private long _nextEventId;

    public bool QaWrapActive => Volatile.Read(ref _qaWrapActive) != 0;
    public IReadOnlyList<LocalizationServerEvent> Events => _events.ToArray();

    [Method("UpdateLocalizationQAWrap")]
    public ValueTask<PyValue> UpdateLocalizationQAWrap(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () =>
        {
            Interlocked.Exchange(ref _qaWrapActive, Truthy(call.Arg(0)) ? 1 : 0);
            Record(call, "update_qa_wrap");
            return PyNone.Instance;
        });

    [Method("GetAllTextChanges")]
    public ValueTask<PyValue> GetAllTextChanges(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () =>
        {
            Record(call, "get_all_text_changes");
            return Py.Tuple(new PyDict([]), new PyDict([]), new PyDict([]));
        });

    [Method("ReloadFSDPickle")]
    public ValueTask<PyValue> ReloadFSDPickle(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () =>
        {
            Record(call, "reload_fsd_pickle");
            return PyBool.False;
        });

    private void Record(ServiceCall call, string eventType) =>
        _events.Enqueue(new LocalizationServerEvent(
            Interlocked.Increment(ref _nextEventId), eventType, call.Session.CharacterId ?? 0,
            call.Session.UserId ?? 0, DateTimeOffset.UtcNow));

    private static bool Truthy(PyValue value) => value switch
    {
        PyNone => false,
        PyBool boolean => boolean.Value,
        PyInt number => number.Value != 0,
        PyLong number => number.Value != 0,
        PyReal number => number.Value != 0 && !double.IsNaN(number.Value),
        PyStr text => text.Value.Length > 0,
        PyBytes text => text.Value.Length > 0,
        PyWString text => text.Value.Length > 0,
        _ => true,
    };
}

public sealed record LocalizationServerEvent(
    long EventId, string EventType, long CharacterId, long AccountId, DateTimeOffset RecordedAt);



[Service("kiringMgr")]
public sealed partial class KiringMgrService
{
    public Func<ServiceCall, ValueTask<PyValue?>>? RpcHandler { get; set; }
    public string? MicroservicesPublicUrl { get; set; }
    public string? EveGuardServerAddress { get; set; }
    public bool MonochromeStyleEnabled { get; set; }
    public bool ShouldKickCheater { get; set; }

    [Method("GetKiringConfiguration")]
    public ValueTask<PyValue> GetKiringConfiguration(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, Configuration);

    [Method("PerformKiringServerSideAuthenticationFromCode")]
    public ValueTask<PyValue> PerformKiringServerSideAuthenticationFromCode(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () =>
        {
            const int aid = 1;
            const string token = "evejs-local-token";
            var deviceId = Truthy(call.Arg(1)) ? Eve.Macho.JsSemantics.StringValue(call.Arg(1)) : "evejs-local-device";
            var json = System.Text.Json.JsonSerializer.Serialize(new { odi = deviceId, s = token });
            var account = $"{aid}-{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))}";
            return Py.Tuple(new PyInt(aid), new PyStr("evejs-local-user"), new PyStr(token),
                new PyStr(account), new PyStr("EVE.JS Local"));
        });

    [Method("GetSAuthAid")]
    public ValueTask<PyValue> GetSAuthAid(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () => new PyInt(1));

    [Method("GetAntiAddictionCode")]
    public ValueTask<PyValue> GetAntiAddictionCode(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () => new PyInt(201));

    [Method("GetEveGuardServerAddress")]
    public ValueTask<PyValue> GetEveGuardServerAddress(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () =>
            string.IsNullOrWhiteSpace(EveGuardServerAddress) ? PyNone.Instance : new PyStr(EveGuardServerAddress.Trim()));

    [Method("IsMonochromeStyleEnabled")]
    public ValueTask<PyValue> IsMonochromeStyleEnabled(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () => PyValue.Of(MonochromeStyleEnabled));

    [Method("ShouldKickCheater")]
    public ValueTask<PyValue> ShouldKickCheaterRpc(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, () => PyValue.Of(ShouldKickCheater));

    [Method("PlaceKiringOrder")]
    public ValueTask<PyValue> PlaceKiringOrder(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () =>
            new PyStr($"evejs-kiring-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));

    /// <summary>
    /// No redeeming-code store exists, so no code is accepted: the answer is False (DEVIATIONS, "Account and social requests").
    /// </summary>
    [Method("ActivateRedeemingCode")]
    public ValueTask<PyValue> ActivateRedeemingCode(ServiceCall call) =>
        CompatibilityRpc.Invoke(call, RpcHandler, static () => PyBool.False);

    private PyValue Configuration()
    {
        var baseUrl = string.IsNullOrWhiteSpace(MicroservicesPublicUrl)
            ? "http://127.0.0.1:26002/"
            : MicroservicesPublicUrl.Trim();
        string Url(string path) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var root)
            ? new Uri(root, path).AbsoluteUri
            : $"http://127.0.0.1:26002/{path.TrimStart('/')}";
        return Py.Dict(
            ("mode", new PyInt(1)),
            ("client_id", new PyStr("evejs-local-kiring")),
            ("endpoints", Py.Dict(
                ("mpay", new PyStr(Url("/kiring/mpay/"))),
                ("billing", new PyStr(Url("/kiring/billing/"))),
                ("redirect_uri", new PyStr(Url("/kiring/redirect/"))))),
            ("channels", Py.Dict(
                ("login", new PyStr("evejs_local")),
                ("pay", new PyStr("evejs_local")),
                ("app", new PyStr("evejs_local")))));
    }

    private static bool Truthy(PyValue value) => value switch
    {
        PyNone => false,
        PyBool boolean => boolean.Value,
        PyInt number => number.Value != 0,
        PyLong number => number.Value != 0,
        PyReal number => number.Value != 0 && !double.IsNaN(number.Value),
        PyStr text => text.Value.Length > 0,
        PyWString text => text.Value.Length > 0,
        PyBytes bytes => bytes.Value.Length > 0,
        _ => true,
    };
}
