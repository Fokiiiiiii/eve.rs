using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Kiring_monochrome_setting_returns_a_boolean_through_the_registered_host_route()
    {
        var service = new KiringMgrService { MonochromeStyleEnabled = true };
        Start(configure: registry => registry.Register(service), strictRpc: true);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        Assert.Equal(PyBool.True, await HostRpcAsync(front, open, 1, "kiringMgr", "IsMonochromeStyleEnabled"));
        service.MonochromeStyleEnabled = false;
        Assert.Equal(PyBool.False, await HostRpcAsync(front, open, 2, "kiringMgr", "IsMonochromeStyleEnabled"));
    }

    [Fact]
    public async Task Map_security_modified_systems_returns_the_empty_indexable_client_rowset_through_the_host()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs)
        {
            ReadDataHook = call => ValueTask.FromResult<PyValue?>(
                call.Method == "GetSecurityModifiedSystems"
                    ? Py.Rowset(["solarSystemID"], [])
                    : null),
        };
        var service = new IncursionAnomalyService(
            "map",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            fallback: map,
            nowUnixMs: () => FixedNowMs);
        Start(configure: registry => registry.Register(service), strictRpc: true);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var result = Assert.IsType<PyObject>(
            await HostRpcAsync(front, open, 1, "map", "GetSecurityModifiedSystems"));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", WireText(result.Name));
        Assert.Equal(["solarSystemID"], Assert.IsType<PyList>(WireField(result, "header")).Items.Select(WireText));
        Assert.Equal(new PyToken("util.Row"), WireField(result, "RowClass"));
        Assert.Empty(Assert.IsType<PyList>(WireField(result, "lines")).Items);
    }

    [Fact]
    public async Task Multi_login_blocker_returns_the_boolean_that_gates_character_entry_through_the_host()
    {
        var service = new MultiLoginBlockerService(session =>
            session.CharacterId == 101 ? MultiLoginBlockerService.CloneGradeAlpha : 1);
        var opened = 0;
        Start(configure: registry => registry.Register(service), strictRpc: true, sessionOpened: session =>
        {
            opened++;
            session.UserId = opened;
            session.CharacterId = 100 + opened;
        });

        var firstFront = await Handshaken(await ConnectAsync());
        var firstOpen = Open();
        await firstFront.SendAsync(firstOpen);
        _ = await firstFront.ReceiveAsync();
        var secondFront = await Handshaken(await ConnectAsync());
        var secondOpen = Open();
        await secondFront.SendAsync(secondOpen);
        _ = await secondFront.ReceiveAsync();

        Assert.Same(PyBool.True,
            await HostRpcAsync(firstFront, firstOpen, 1, "multiLoginBlocker", "Login", new PyStr("shared-device")));
        Assert.Same(PyBool.False,
            await HostRpcAsync(secondFront, secondOpen, 1, "multiLoginBlocker", "Login", new PyStr("shared-device")));
    }
}
