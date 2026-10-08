using Eve.Marshal;
using Eve.Services;
using Eve.Services.Station;

namespace Eve.Services.Station.Tests;

public sealed class TradeMgrRpcTests
{
    [Fact]
    public async Task InitiateTrade_uses_the_online_peer_and_returns_the_backend_moniker()
    {
        var sessions = new CharacterSessions();
        var initiator = Session(1001, stationId: 600001);
        var target = Session(1002, stationId: 600001);
        sessions.Enter(1001, initiator);
        sessions.Enter(1002, target);
        ClientSession? observedTarget = null;
        var moniker = new PyTuple([new PyStr("trade"), new PyInt(17)]);
        IService service = new TradeMgrService(sessions, (call, peer) =>
        {
            observedTarget = peer;
            return ValueTask.FromResult<PyValue>(moniker);
        });

        Assert.True(service.HasMethod("InitiateTrade"));
        Assert.Equal("trademgr", service.Name);
        var result = await service.CallAsync(new ServiceCall
        {
            Method = "InitiateTrade",
            Args = [new PyInt(1002)],
            Session = initiator,
        });

        Assert.Same(target, observedTarget);
        Assert.Same(moniker, result);
    }

    [Fact]
    public async Task InitiateTrade_rejects_a_peer_at_a_different_docked_location()
    {
        var sessions = new CharacterSessions();
        var initiator = Session(1001, stationId: 600001);
        var target = Session(1002, stationId: 600002);
        sessions.Enter(1001, initiator);
        sessions.Enter(1002, target);
        var backendCalled = false;
        IService service = new TradeMgrService(sessions, (_, _) =>
        {
            backendCalled = true;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        });

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(new ServiceCall
            {
                Method = "InitiateTrade",
                Args = [new PyInt(1002)],
                Session = initiator,
            }));

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);

        Assert.False(backendCalled);
    }

    [Fact]
    public async Task InitiateTrade_rejects_self_and_offline_targets_before_backend()
    {
        var sessions = new CharacterSessions();
        var initiator = Session(1001, stationId: 600001);
        sessions.Enter(1001, initiator);
        var backendCalled = false;
        IService service = new TradeMgrService(sessions, (_, _) =>
        {
            backendCalled = true;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        });

        foreach (var targetId in new long[] { 1001, 1002 })
        {
            await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                await service.CallAsync(new ServiceCall
                {
                    Method = "InitiateTrade",
                    Args = [new PyInt(targetId)],
                    Session = initiator,
                }));
        }

        Assert.False(backendCalled);
    }

    [Fact]
    public async Task InitiateTrade_uses_a_custom_notify_when_trade_backend_is_unwired()
    {
        var sessions = new CharacterSessions();
        var initiator = Session(1001, stationId: 600001);
        var target = Session(1002, stationId: 600001);
        sessions.Enter(1001, initiator);
        sessions.Enter(1002, target);
        IService service = new TradeMgrService(sessions);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(new ServiceCall
            {
                Method = "InitiateTrade",
                Args = [new PyInt(1002)],
                Session = initiator,
            }));

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
    }

    private static ClientSession Session(long characterId, long? stationId = null, long? structureId = null)
    {
        var session = new ClientSession { CharacterId = characterId };
        if (stationId is { } station) session.Set("stationid", new PyInt(station));
        if (structureId is { } structure) session.Set("structureid", new PyInt(structure));
        return session;
    }
}
