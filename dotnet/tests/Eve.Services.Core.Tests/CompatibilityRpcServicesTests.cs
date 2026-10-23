using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class CompatibilityRpcServicesTests
{
    private static readonly (IService Service, string[] Methods)[] Services =
    [
        (new BannedWordsService(), ["check_search_words_allowed", "check_words_allowed", "fetch_regexes"]),
        (new CacheService(), ["GetServerVersionAndBuild"]),
        (new CommunityTrackMgrService(), ["change_qa_mode", "get_instance_attributes_public"]),
        (new ContextualOfferMgrService(), ["TrackOfferBuyPress", "TrackOfferClosed", "TrackOfferDelivered", "TrackOfferPurchased", "TrackOfferPurchasedSeen", "TrackOfferSeen"]),
        (new LocalizationServerService(), ["GetAllTextChanges", "ReloadFSDPickle", "UpdateLocalizationQAWrap"]),
        (new KiringMgrService(), ["GetKiringConfiguration", "PerformKiringServerSideAuthenticationFromCode", "GetSAuthAid", "GetAntiAddictionCode", "GetEveGuardServerAddress", "IsMonochromeStyleEnabled", "ShouldKickCheater", "PlaceKiringOrder", "ActivateRedeemingCode"]),
    ];

    [Fact]
    public void Every_requested_RPC_is_registered()
    {
        foreach (var (service, methods) in Services)
        {
            Assert.All(methods, method => Assert.True(service.HasMethod(method), $"{service.Name}.{method}"));
        }
    }

    [Fact]
    public async Task Unwired_responses_keep_the_EveJS_wire_shapes()
    {
        var bannedWords = (IService)Services[0].Service;
        Assert.Empty(Assert.IsType<PyList>(await bannedWords.CallAsync(Call("fetch_regexes", new PyStr("blocked")))).Items);
        Assert.IsType<PyNone>(await bannedWords.CallAsync(Call("check_words_allowed", new PyStr("name"))));
        Assert.IsType<PyNone>(await bannedWords.CallAsync(Call("check_search_words_allowed", new PyStr("query"))));

        Assert.Equal(new PyTuple([new PyReal(ServerBuildInfo.BootVersion), new PyInt(ServerBuildInfo.BootBuild)]),
            await ((IService)Services[1].Service).CallAsync(Call("GetServerVersionAndBuild")));

        var community = (CommunityTrackMgrService)Services[2].Service;
        var communityService = (IService)community;
        Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance]), await communityService.CallAsync(Call("get_instance_attributes_public", new PyLong(1337))));
        Assert.IsType<PyNone>(await communityService.CallAsync(StaffCall("change_qa_mode", PyBool.True)));
        Assert.True(community.QaMode);

        foreach (var method in Services[3].Methods)
            Assert.IsType<PyNone>(await ((IService)Services[3].Service).CallAsync(Call(method, new PyInt(91))));

        var localization = (LocalizationServerService)Services[4].Service;
        var localizationService = (IService)localization;
        var textChanges = Assert.IsType<PyTuple>(await localizationService.CallAsync(StaffCall("GetAllTextChanges", new PyDict([]))));
        Assert.Equal(3, textChanges.Items.Count);
        Assert.All(textChanges.Items, value => Assert.Empty(Assert.IsType<PyDict>(value).Entries));
        Assert.Equal(PyBool.False, await localizationService.CallAsync(StaffCall("ReloadFSDPickle")));
        Assert.IsType<PyNone>(await localizationService.CallAsync(StaffCall("UpdateLocalizationQAWrap", PyBool.True)));
        Assert.True(localization.QaWrapActive);
    }

    [Fact]
    public async Task Banned_words_rpcs_refuse_malformed_and_unbounded_input_before_callbacks()
    {
        var callbackCalls = 0;
        var service = (IService)new BannedWordsService
        {
            RpcHandler = _ =>
            {
                callbackCalls++;
                return ValueTask.FromResult<PyValue?>(PyNone.Instance);
            },
        };
        var oversized = new PyStr(new string('x', 4097));
        var calls = new[]
        {
            Call("fetch_regexes"),
            Call("fetch_regexes", new PyStr("other")),
            Call("fetch_regexes", new PyStr("blocked"), new PyDict([])),
            Call("check_words_allowed"),
            Call("check_words_allowed", new PyInt(7)),
            new ServiceCall { Method = "check_words_allowed", Args = Enumerable.Repeat<PyValue>(new PyStr("x"), 33).ToArray(), Session = new ClientSession() },
            Call("check_search_words_allowed", oversized),
            Call("check_words_allowed", new PyStr("word"), new PyDict([new(new PyStr("unexpected"), new PyStr("x"))])),
            Call("check_words_allowed", new PyStr("word"), new PyDict([new(new PyStr("err_message"), oversized)])),
        };

        foreach (var call in calls)
        {
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal(MachoErrors.UserErrorClass, refusal.Error.ClassName);
            Assert.StartsWith("BANNED_WORDS_", refusal.Error.UserErrorMessage);
        }

        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public async Task Cache_build_read_refuses_client_arguments_before_the_handler()
    {
        var callbackCalls = 0;
        var service = (IService)new CacheService
        {
            RpcHandler = _ =>
            {
                callbackCalls++;
                return ValueTask.FromResult<PyValue?>(PyNone.Instance);
            },
        };
        foreach (var call in new[]
        {
            Call("GetServerVersionAndBuild", new PyInt(1)),
            new ServiceCall { Method = "GetServerVersionAndBuild", Args = [], Kwargs = new PyDict([]), Session = new ClientSession() },
        })
        {
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal(MachoErrors.UserErrorClass, refusal.Error.ClassName);
            Assert.Equal("CACHE_ARGUMENTS_INVALID", refusal.Error.UserErrorMessage);
        }

        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public async Task Kiring_initialization_rpcs_keep_the_reference_shapes()
    {
        var rpc = (IService)new KiringMgrService();

        Assert.True(rpc.HasMethod("IsMonochromeStyleEnabled"));
        Assert.True(rpc.HasMethod("GetKiringConfiguration"));
        Assert.True(rpc.HasMethod("PerformKiringServerSideAuthenticationFromCode"));
        Assert.True(rpc.HasMethod("GetSAuthAid"));
        Assert.True(rpc.HasMethod("GetAntiAddictionCode"));
        Assert.True(rpc.HasMethod("GetEveGuardServerAddress"));
        Assert.True(rpc.HasMethod("ShouldKickCheater"));
        Assert.True(rpc.HasMethod("PlaceKiringOrder"));
        Assert.True(rpc.HasMethod("ActivateRedeemingCode"));

        static PyValue Value(PyDict dictionary, string key) =>
            dictionary.Entries.Single(entry => entry.Key == new PyStr(key)).Value;

        var configuration = Assert.IsType<PyDict>(await rpc.CallAsync(Call("GetKiringConfiguration")));
        Assert.Equal(new PyInt(1), Value(configuration, "mode"));
        Assert.Equal(new PyStr("evejs-local-kiring"), Value(configuration, "client_id"));
        var endpoints = Assert.IsType<PyDict>(Value(configuration, "endpoints"));
        Assert.Equal(new PyStr("http://127.0.0.1:26002/kiring/mpay/"), Value(endpoints, "mpay"));
        Assert.Equal(new PyStr("http://127.0.0.1:26002/kiring/billing/"), Value(endpoints, "billing"));
        Assert.Equal(new PyStr("http://127.0.0.1:26002/kiring/redirect/"), Value(endpoints, "redirect_uri"));

        Assert.Equal(new PyInt(201), await rpc.CallAsync(Call("GetAntiAddictionCode")));
        Assert.Same(PyNone.Instance, await rpc.CallAsync(Call("GetEveGuardServerAddress")));
        Assert.Same(PyBool.False, await rpc.CallAsync(Call("IsMonochromeStyleEnabled")));
        Assert.Same(PyBool.False, await rpc.CallAsync(Call("ShouldKickCheater")));
        Assert.Same(PyBool.False, await rpc.CallAsync(Call("ActivateRedeemingCode")));

        foreach (var (method, args, error) in new[]
        {
            ("PerformKiringServerSideAuthenticationFromCode", Array.Empty<PyValue>(), "KIRING_AUTH_UNAVAILABLE"),
            ("PerformKiringServerSideAuthenticationFromCode", new PyValue[] { new PyStr("code"), new PyStr("device-42"), new PyStr("client-1") }, "KIRING_AUTH_UNAVAILABLE"),
            ("GetSAuthAid", new PyValue[] { new PyStr("forged-user"), new PyStr("forged-account"), new PyStr("forged-device") }, "KIRING_AUTH_UNAVAILABLE"),
            ("PlaceKiringOrder", Array.Empty<PyValue>(), "KIRING_PURCHASE_UNAVAILABLE"),
        })
        {
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => rpc.CallAsync(new ServiceCall
            {
                Method = method,
                Args = args,
                Session = new ClientSession(),
            }).AsTask());
            Assert.Equal(MachoErrors.UserErrorClass, refusal.Error.ClassName);
            Assert.Equal(error, refusal.Error.UserErrorMessage);
        }
    }

    [Fact]
    public async Task Contextual_offer_tracking_keeps_a_bounded_event_history()
    {
        var service = new ContextualOfferMgrService();
        var rpc = (IService)service;

        for (var id = 1; id <= 1100; id++)
            await rpc.CallAsync(Call("TrackOfferSeen", new PyLong(id)));

        Assert.Equal(1024, service.Events.Count);
        Assert.Equal(77, service.Events[0].EventId);
        Assert.Equal(1100, service.Events[^1].EventId);
        Assert.Equal(1100, service.Events[^1].OfferId);
    }

    [Fact]
    public async Task Contextual_offer_tracking_refuses_invalid_offer_ids_without_recording()
    {
        var callbackCalled = false;
        var service = new ContextualOfferMgrService
        {
            RpcHandler = _ =>
            {
                callbackCalled = true;
                return ValueTask.FromResult<PyValue?>(PyNone.Instance);
            },
        };
        var rpc = (IService)service;

        foreach (var value in new PyValue[] { PyNone.Instance, new PyStr("17"), new PyLong(0), new PyLong(-1) })
        {
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => rpc.CallAsync(Call("TrackOfferSeen", value)).AsTask());
            Assert.Equal(MachoErrors.UserErrorClass, refusal.Error.ClassName);
            Assert.Equal("CONTEXTUAL_OFFER_ID_INVALID", refusal.Error.UserErrorMessage);
            Assert.Empty(service.Events);
            Assert.False(callbackCalled);
        }
    }

    [Fact]
    public async Task Missing_backends_can_be_injected_without_rebuilding_service_calls()
    {
        ServiceCall? received = null;
        var expected = new PyStr("backend response");
        ValueTask<PyValue?> Handler(ServiceCall call)
        {
            received = call;
            return ValueTask.FromResult<PyValue?>(expected);
        }

        (IService Service, string Method)[] services =
        {
            (new BannedWordsService { RpcHandler = Handler }, "check_words_allowed"),
            (new CommunityTrackMgrService { RpcHandler = Handler }, "get_instance_attributes_public"),
            (new ContextualOfferMgrService { RpcHandler = Handler }, "TrackOfferSeen"),
            (new LocalizationServerService { RpcHandler = Handler }, "GetAllTextChanges"),
            (new KiringMgrService { RpcHandler = Handler }, "IsMonochromeStyleEnabled"),
        };

        foreach (var (service, method) in services)
        {
            var request = service is BannedWordsService
                ? new ServiceCall
                {
                    Method = method,
                    Args = [new PyStr("name")],
                    Kwargs = new PyDict([new(new PyStr("err_message"), new PyStr("custom error"))]),
                    Session = new ClientSession { CharacterId = 42 },
                }
                : StaffCall(method, new PyInt(42), kwargs: new PyDict([new(new PyStr("source"), new PyStr("test"))]));
            Assert.Same(expected, await service.CallAsync(request));
            Assert.Same(request, received);
        }
    }

    [Fact]
    public async Task Localization_server_returns_configured_maps_and_bounds_public_read_events()
    {
        var expected = Py.Tuple(
            new PyDict([new(new PyStr("en"), new PyStr("configured text"))]),
            new PyDict([new(new PyStr("removed"), PyBool.True)]),
            new PyDict([]));
        var service = new LocalizationServerService(expected);

        for (var index = 0; index <= 1024; index++)
            Assert.Equal(expected, await service.GetAllTextChanges(Call("GetAllTextChanges", new PyDict([]))));

        Assert.Equal(1024, service.Events.Count);
        Assert.Throws<ArgumentException>(() => new LocalizationServerService(new PyDict([])));
    }

    [Fact]
    public async Task Qa_rpcs_refuse_sessions_without_a_staff_account_role_and_change_nothing()
    {
        var handlerCalls = new List<string>();
        ValueTask<PyValue?> Handler(ServiceCall call)
        {
            handlerCalls.Add(call.Method);
            return ValueTask.FromResult<PyValue?>(PyBool.True);
        }
        var ordinary = new ClientSession { CharacterId = 42 };
        ordinary.Set("role", new PyLong(GameMasterRoles.Staff)); // the generic role field is not a grant
        var community = new CommunityTrackMgrService { RpcHandler = Handler };
        var localization = new LocalizationServerService { RpcHandler = Handler };

        Assert.IsType<PyNone>(await ((IService)community).CallAsync(Call("change_qa_mode", PyBool.True, session: ordinary)));
        Assert.IsType<PyNone>(await ((IService)localization).CallAsync(Call("UpdateLocalizationQAWrap", PyBool.True, session: ordinary)));
        Assert.Equal(PyBool.False, await ((IService)localization).CallAsync(Call("ReloadFSDPickle", session: ordinary)));
        Assert.Empty(handlerCalls);
        Assert.False(community.QaMode);
        Assert.False(localization.QaWrapActive);
        Assert.Empty(localization.Events);

        var staffCommunity = new CommunityTrackMgrService();
        Assert.IsType<PyNone>(await ((IService)staffCommunity).CallAsync(StaffCall("change_qa_mode", PyBool.True)));
        Assert.True(staffCommunity.QaMode);
    }

    private static ServiceCall StaffCall(string method, PyValue? arg = null, PyValue? kwargs = null)
    {
        var session = new ClientSession { CharacterId = 42 };
        session.Set("accountRole", new PyLong(GameMasterRoles.Staff));
        return Call(method, arg, kwargs, session);
    }

    private static ServiceCall Call(string method, PyValue? arg = null, PyValue? kwargs = null, ClientSession? session = null) => new()
    {
        Method = method,
        Args = arg is null ? [] : [arg],
        Kwargs = kwargs ?? PyNone.Instance,
        Session = session ?? new ClientSession { CharacterId = 42 },
    };
}
