using System.Text;
using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Shared_settings_mgr_filter_links_round_trip_over_the_registered_host_route()
    {
        JsonObject? persistedState = null;
        var writesEnabled = true;
        var writeCount = 0;
        var callbacks = new GameRpcCallbacks(
            ReadSharedSettings: () => persistedState?.DeepClone() as JsonObject,
            WriteSharedSettingsAsync: (state, _) =>
            {
                writeCount++;
                if (!writesEnabled)
                    return ValueTask.FromResult(false);
                persistedState = state.DeepClone() as JsonObject;
                return ValueTask.FromResult(true);
            });

        Start(gameRpcCallbacks: callbacks);
        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var filter = Py.Tuple(
            new PyStr("Ammo"),
            new PyInt(1),
            new PyList([Py.Tuple(new PyInt(7), new PyInt(10), new PyStr("Charge"))]));
        var itemPayload = Py.Dict(("filter", filter));
        var key = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 1, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
            Py.Tuple(new PyInt(2), itemPayload)));
        Assert.Equal("util.KeyVal", JsSemantics.StringValue(key.Name));
        var hashBytes = Assert.IsType<PyBytes>(RpcCoverageWireField(key, "hashvalue"));
        var hash = Encoding.UTF8.GetString(hashBytes.Value.Span);
        var sqId = FilterWireLong(RpcCoverageWireField(key, "sqID"));
        Assert.Equal(new PyInt(2), RpcCoverageWireField(key, "settingTypeID"));
        Assert.Equal(1L, sqId);
        Assert.Equal(1, writeCount);

        var stored = Assert.IsType<PyBytes>(await CallOverHost(
            front, open, 2, "sharedSettingsMgr", "GetStoredSharedSetting",
            Py.Tuple(Py.Tuple(new PyStr(hash), new PyLong(sqId), new PyLong(2)))));
        Assert.Equal("[[\"filter\",[\"Ammo\",1,[[7,10,\"Charge\"]]]]]",
            Encoding.UTF8.GetString(stored.Value.Span));
        Assert.Equal(stored, await CallOverHost(
            front, open, 3, "sharedSettingsMgr", "GetStoredSharedSetting",
            Py.Tuple(Py.Tuple(new PyStr(hash), new PyInt(sqId), new PyInt(2)))));

        var duplicate = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 4, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
            Py.Tuple(new PyInt(2), itemPayload)));
        Assert.Equal(sqId, FilterWireLong(RpcCoverageWireField(duplicate, "sqID")));
        Assert.Equal(1, writeCount);

        var broadcastPayload = Py.Dict(("broadcastSettings", new PyList([])));
        var broadcastKey = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 5, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
            Py.Tuple(new PyLong(3), broadcastPayload)));
        Assert.Equal(new PyInt(3), RpcCoverageWireField(broadcastKey, "settingTypeID"));
        Assert.Equal(2, writeCount);

        using var secondFront = await Handshaken(await ConnectAsync());
        var secondOpen = Open();
        await secondFront.SendAsync(secondOpen);
        _ = await secondFront.ReceiveAsync();
        var cargoPayload = Py.Dict(("filter", Py.Tuple(
            new PyStr("Cargo"),
            new PyInt(1),
            new PyList([Py.Tuple(new PyInt(7), new PyInt(10), new PyStr("Container"))]))));
        var concurrent = await Task.WhenAll(
            CallOverHost(front, open, 6, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
                Py.Tuple(new PyInt(2), cargoPayload)),
            CallOverHost(secondFront, secondOpen, 1, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
                Py.Tuple(new PyInt(2), cargoPayload)));
        var firstConcurrentKey = Assert.IsType<PyObject>(concurrent[0]);
        var secondConcurrentKey = Assert.IsType<PyObject>(concurrent[1]);
        Assert.Equal(
            FilterWireLong(RpcCoverageWireField(firstConcurrentKey, "sqID")),
            FilterWireLong(RpcCoverageWireField(secondConcurrentKey, "sqID")));
        Assert.Equal(3, writeCount);
        Assert.Equal(3, Assert.IsType<JsonObject>(persistedState!["entries"]).Count);

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 7, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
            Py.Tuple(new PyInt(4), itemPayload)));
        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 8, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
            Py.Tuple(new PyInt(2), PyNone.Instance)));
        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 9, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
            Py.Tuple(new PyInt(2), Py.Dict([]))));
        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 10, "sharedSettingsMgr", "GetStoredSharedSetting",
            Py.Tuple(PyNone.Instance)));
        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 11, "sharedSettingsMgr", "GetStoredSharedSetting",
            Py.Tuple(Py.Tuple(new PyStr(hash), new PyLong(sqId + 100), new PyInt(2)))));

        writesEnabled = false;
        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 12, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
            Py.Tuple(new PyInt(2), Py.Dict(("filter", Py.Tuple(new PyStr("Other"), new PyInt(1), new PyList([])))))));
        Assert.Equal(4, writeCount);
        Assert.Equal(3, Assert.IsType<JsonObject>(persistedState!["entries"]).Count);
    }

    [Fact]
    public async Task Character_filter_settings_round_trip_through_the_registered_char_mgr_host_route()
    {
        const long characterId = 90_000_201;
        var character = new Character(
            characterId, 1, "Filter Pilot", 1, 1, 1, 1, 35, 1373, 1_000_001,
            null, null, 60_000_001, 60_000_001, 30_000_142, 20_000_020, 10_000_002, null,
            DateTimeOffset.UnixEpoch, null, null);
        var personal = CharMgrPersonalData.Empty;
        var service = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            readCharacterDescription: (_, _) => ValueTask.FromResult<string?>(null),
            writeCharacterDescription: (_, _, _) => ValueTask.CompletedTask,
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null),
            readPersonalData: (_, _) => ValueTask.FromResult<CharMgrPersonalData?>(personal),
            updatePersonalData: (_, update, _) =>
            {
                personal = update(personal);
                return ValueTask.FromResult<CharMgrPersonalData?>(personal);
            },
            readCharacterById: (id, _) =>
                ValueTask.FromResult<Character?>(id == characterId ? character : null));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);
        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var settings = Assert.IsType<PyDict>(await CallOverHost(
            front, open, 1, "charMgr", "GetCharacterSettings", Py.Tuple([])));
        Assert.Empty(settings.Entries);

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 2, "charMgr", "SaveCharacterSetting",
            Py.Tuple(new PyStr("inventoryFilters"), new PyStr("[]\n"))));
        settings = Assert.IsType<PyDict>(await CallOverHost(
            front, open, 3, "charMgr", "GetCharacterSettings", Py.Tuple([])));
        var yamlSetting = Assert.IsType<PyBytes>(settings.Entries.Single(entry =>
            JsSemantics.StringValue(entry.Key) == "inventoryFilters").Value);
        Assert.Equal("[]\n", Encoding.UTF8.GetString(yamlSetting.Value.Span));

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 4, "charMgr", "DeleteCharacterSetting",
            Py.Tuple(new PyStr("inventoryFilters"))));
        settings = Assert.IsType<PyDict>(await CallOverHost(
            front, open, 5, "charMgr", "GetCharacterSettings", Py.Tuple([])));
        Assert.Empty(settings.Entries);

        Assert.Same(PyNone.Instance, await CallOverHost(
            front, open, 6, "charMgr", "SaveCharacterSetting",
            Py.Tuple(new PyStr("inventoryFilters"), PyNone.Instance)));
        settings = Assert.IsType<PyDict>(await CallOverHost(
            front, open, 7, "charMgr", "GetCharacterSettings", Py.Tuple([])));
        Assert.Same(PyNone.Instance, settings.Entries.Single(entry =>
            JsSemantics.StringValue(entry.Key) == "inventoryFilters").Value);
    }

    private static long FilterWireLong(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected PyInt or PyLong; got {value.GetType().Name}."),
    };
}
