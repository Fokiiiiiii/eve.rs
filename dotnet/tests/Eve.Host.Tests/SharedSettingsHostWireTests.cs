using Eve.Macho;
using Eve.Marshal;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Shared_setting_store_returns_the_keyval_consumed_by_the_client_through_host_dispatch()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        var key = Assert.IsType<PyObject>(await HostRpcAsync(
            front, open, 1, "sharedSettingsMgr", "StoreSettingLinkAndGetID",
            new PyInt(2), new PyStr("preset")));
        var hash = WireField(key, "hashvalue");
        var sqId = WireField(key, "sqID");
        var settingTypeId = WireField(key, "settingTypeID");

        Assert.IsType<PyBytes>(hash);
        Assert.False(string.IsNullOrEmpty(JsSemantics.StringValue(hash)));
        Assert.True(WireInteger(sqId) > 0);
        Assert.Equal(2, WireInteger(settingTypeId));
        Assert.Equal(new PyStr("preset"), await HostRpcAsync(
            front, open, 2, "sharedSettingsMgr", "GetStoredSharedSetting",
            new PyTuple([hash, sqId, settingTypeId])));
    }
}
