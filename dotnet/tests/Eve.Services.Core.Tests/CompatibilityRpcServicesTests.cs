using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class CompatibilityRpcServicesTests
{
    private static readonly (IService Service, string[] Methods)[] Services =
    [
        (new BannedWordsService(), ["check_search_words_allowed", "check_words_allowed", "fetch_regexes"]),
        (new AlphaInjectorMgrService(), ["GetNextAvailableInjection"]),
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

        Assert.Equal(new PyLong(0), await ((IService)Services[1].Service).CallAsync(Call("GetNextAvailableInjection")));
        Assert.Equal(new PyTuple([new PyReal(ServerBuildInfo.BootVersion), new PyInt(ServerBuildInfo.BootBuild)]),
            await ((IService)Services[2].Service).CallAsync(Call("GetServerVersionAndBuild")));

        var community = (CommunityTrackMgrService)Services[3].Service;
        var communityService = (IService)community;
        Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance]), await communityService.CallAsync(Call("get_instance_attributes_public", new PyLong(1337))));
        Assert.IsType<PyNone>(await communityService.CallAsync(Call("change_qa_mode", PyBool.True)));
        Assert.True(community.QaMode);

        foreach (var method in Services[4].Methods)
            Assert.IsType<PyNone>(await ((IService)Services[4].Service).CallAsync(Call(method, new PyInt(91))));

        var localization = (LocalizationServerService)Services[5].Service;
        var localizationService = (IService)localization;
        var textChanges = Assert.IsType<PyTuple>(await localizationService.CallAsync(Call("GetAllTextChanges", new PyDict([]))));
        Assert.Equal(3, textChanges.Items.Count);
        Assert.All(textChanges.Items, value => Assert.Empty(Assert.IsType<PyDict>(value).Entries));
        Assert.Equal(PyBool.False, await localizationService.CallAsync(Call("ReloadFSDPickle")));
        Assert.IsType<PyNone>(await localizationService.CallAsync(Call("UpdateLocalizationQAWrap", PyBool.True)));
        Assert.True(localization.QaWrapActive);
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

        Assert.Equal(new PyInt(1), await rpc.CallAsync(Call("GetSAuthAid")));
        Assert.Equal(new PyInt(201), await rpc.CallAsync(Call("GetAntiAddictionCode")));
        Assert.Same(PyNone.Instance, await rpc.CallAsync(Call("GetEveGuardServerAddress")));
        Assert.Same(PyBool.False, await rpc.CallAsync(Call("IsMonochromeStyleEnabled")));
        Assert.Same(PyBool.False, await rpc.CallAsync(Call("ShouldKickCheater")));
        Assert.Same(PyBool.False, await rpc.CallAsync(Call("ActivateRedeemingCode")));

        var order = Assert.IsType<PyStr>(await rpc.CallAsync(Call("PlaceKiringOrder")));
        Assert.StartsWith("evejs-kiring-", order.Value);
        var authentication = Assert.IsType<PyTuple>(await rpc.CallAsync(new ServiceCall
        {
            Method = "PerformKiringServerSideAuthenticationFromCode",
            Args = [new PyStr("code"), new PyStr("device-42"), new PyStr("client-1")],
            Session = new ClientSession(),
        }));
        Assert.Equal(new PyInt(1), authentication.Items[0]);
        Assert.Equal(new PyStr("evejs-local-user"), authentication.Items[1]);
        Assert.Equal(new PyStr("evejs-local-token"), authentication.Items[2]);
        var encoded = Assert.IsType<PyStr>(authentication.Items[3]).Value;
        var accountJson = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(encoded[(encoded.IndexOf('-') + 1)..]));
        Assert.Equal("{\"odi\":\"device-42\",\"s\":\"evejs-local-token\"}", accountJson);
        Assert.Equal(new PyStr("EVE.JS Local"), authentication.Items[4]);
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
            (new AlphaInjectorMgrService { RpcHandler = Handler }, "GetNextAvailableInjection"),
            (new CommunityTrackMgrService { RpcHandler = Handler }, "get_instance_attributes_public"),
            (new ContextualOfferMgrService { RpcHandler = Handler }, "TrackOfferSeen"),
            (new LocalizationServerService { RpcHandler = Handler }, "GetAllTextChanges"),
            (new KiringMgrService { RpcHandler = Handler }, "IsMonochromeStyleEnabled"),
        };

        foreach (var (service, method) in services)
        {
            var request = Call(method, new PyInt(42), kwargs: new PyDict([new(new PyStr("source"), new PyStr("test"))]));
            Assert.Same(expected, await service.CallAsync(request));
            Assert.Same(request, received);
        }
    }

    private static ServiceCall Call(string method, PyValue? arg = null, PyValue? kwargs = null) => new()
    {
        Method = method,
        Args = arg is null ? [] : [arg],
        Kwargs = kwargs ?? PyNone.Instance,
        Session = new ClientSession { CharacterId = 42 },
    };
}
