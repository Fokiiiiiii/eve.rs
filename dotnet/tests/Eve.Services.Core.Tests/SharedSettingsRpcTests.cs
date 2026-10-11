using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class SharedSettingsRpcTests
{
    [Fact]
    public async Task Shared_settings_accepts_pyobjectex_and_round_trips_the_payload_through_callbacks()
    {
        JsonObject? savedState = null;
        var callbacks = new GameRpcCallbacks(
            ReadSharedSettings: () => savedState?.DeepClone() as JsonObject,
            WriteSharedSettingsAsync: (state, _) =>
            {
                savedState = state.DeepClone() as JsonObject;
                return ValueTask.FromResult(true);
            });
        var services = CoreServices.Create(new NoAccounts(), () => 0,
            gameRpcCallbacks: callbacks);
        var service = Assert.Single(services, candidate => candidate.Name == "sharedSettingsMgr");
        var session = new ClientSession { CharacterId = 42 };
        var payload = new PyObjectEx(ObjectExKind.Ex1, new PyStr("util.KeyVal"), [],
        [
            Pair("z", new PyStr("last")),
            Pair("a", new PyInt(1)),
        ]);

        var key = await service.CallAsync(Call(
            "StoreSettingLinkAndGetID", session, new PyInt(3), payload));
        Assert.Equal("util.KeyVal", Assert.IsType<PyStr>(Assert.IsType<PyObject>(key).Name).Value);
        Assert.Equal(new PyInt(3), Field(key, "settingTypeID"));
        Assert.Equal(new PyInt(1), Field(key, "sqID"));

        var stored = Assert.IsType<PyStr>(await service.CallAsync(Call(
            "GetStoredSharedSetting", session, key)));
        Assert.Equal("[[\"a\",1],[\"z\",\"last\"]]", stored.Value);
        var entryKey = "3::" + Assert.IsType<PyStr>(Field(key, "hashvalue")).Value + "::1";
        var storedEntry = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(savedState!["entries"])[entryKey]);
        Assert.Equal(42L, storedEntry["ownerID"]!.GetValue<long>());

        var duplicate = await service.CallAsync(Call(
            "StoreSettingLinkAndGetID", session, new PyInt(3), payload));
        Assert.Equal(Field(key, "sqID"), Field(duplicate, "sqID"));
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static KeyValuePair<PyValue, PyValue> Pair(string name, PyValue value) =>
        new(new PyStr(name), value);

    private static PyValue? Field(PyValue value, string name)
    {
        var fields = value switch
        {
            PyObject { Args: PyDict dictionary } => dictionary.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            PyDict dictionary => dictionary.Entries,
            _ => [],
        };
        return fields.FirstOrDefault(pair => pair.Key is PyStr key && key.Value == name).Value;
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
