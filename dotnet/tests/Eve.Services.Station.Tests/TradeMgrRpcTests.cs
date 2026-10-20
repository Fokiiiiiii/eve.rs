using Eve.Marshal;
using Eve.Services;
using Eve.Services.Station;

namespace Eve.Services.Station.Tests;

/// <summary>The checks <c>InitiateTrade</c> makes before a trade is opened. The trade rules and their persistence are tested in Eve.Trade.Tests.</summary>
public sealed class TradeMgrRpcTests
{
    [Fact]
    public void The_service_is_the_trademgr_door_with_the_trade_methods()
    {
        IService service = new TradeMgrService(new CharacterSessions());

        Assert.Equal("trademgr", service.Name);
        foreach (var method in new[] { "InitiateTrade", "MachoBindObject", "List", "Add", "MultiAdd", "OfferMoney", "MakeOffer", "Abort", "GetSelfInvItem" })
            Assert.True(service.HasMethod(method), method);
    }

    [Fact]
    public async Task InitiateTrade_rejects_a_peer_at_a_different_docked_location()
    {
        var sessions = new CharacterSessions();
        var initiator = Session(1001, stationId: 600001);
        var target = Session(1002, stationId: 600002);
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

    [Fact]
    public async Task InitiateTrade_rejects_self_and_offline_targets()
    {
        var sessions = new CharacterSessions();
        var initiator = Session(1001, stationId: 600001);
        sessions.Enter(1001, initiator);
        IService service = new TradeMgrService(sessions);

        foreach (var targetId in new long[] { 1001, 1002 })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                await service.CallAsync(new ServiceCall
                {
                    Method = "InitiateTrade",
                    Args = [new PyInt(targetId)],
                    Session = initiator,
                }));
            Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        }
    }

    [Fact]
    public async Task InitiateTrade_without_a_trade_store_says_so_to_the_pilot()
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

    [Fact]
    public void The_manifest_names_the_trade_its_money_and_its_item_ids()
    {
        var item = new PyObjectEx(ObjectExKind.Ex1, new PyStr("InvItem"), [], [new(new PyStr("itemID"), new PyLong(1_000_000_000_042))]);
        var manifest = new PyObjectEx(ObjectExKind.Ex1, new PyStr("KeyVal"), [], [
            new(new PyStr("tradeContainerID"), new PyLong(9_000_000_000_000_001)),
            new(new PyStr("money"), new PyList([new PyReal(250.5), new PyInt(0)])),
            new(new PyStr("tradeItems"), new PyObjectEx(ObjectExKind.Ex1,
                new PyTuple([new PyToken("__builtin__.set"), new PyTuple([new PyList([item, new PyLong(77)])])]), [], [])),
        ]);

        var (container, money, items) = TradeMgrService.ManifestOf(manifest);

        Assert.Equal(9_000_000_000_000_001, container);
        Assert.Equal([250.5m, 0m], money);
        Assert.Equal(new HashSet<long> { 1_000_000_000_042, 77 }, items);
    }

    [Fact]
    public void The_manifest_reads_every_form_of_a_trade_item_the_reference_accepts()
    {
        var packed = new PyObject(new PyStr("carbon.common.script.sys.row.Row"), new PyDict([
            new(new PyStr("header"), new PyList([new PyStr("itemID")])),
            new(new PyStr("line"), new PyList([new PyLong(1_000_000_000_050)])),
        ]));
        var manifest = new PyObjectEx(ObjectExKind.Ex1, new PyStr("KeyVal"), [], [
            new(new PyStr("tradeContainerID"), new PyLong(9_000_000_000_000_002)),
            new(new PyStr("money"), new PyList([new PyInt(0), new PyInt(0)])),
            new(new PyStr("tradeItems"), new PyObjectEx(ObjectExKind.Ex1,
                new PyTuple([new PyToken("__builtin__.set"), new PyTuple([new PyList([new PyLong(61)])])]), [packed, new PyLong(62)], [])),
        ]);

        var (_, _, items) = TradeMgrService.ManifestOf(manifest);

        Assert.Equal(new HashSet<long> { 61, 62, 1_000_000_000_050 }, items);
    }

    [Fact]
    public void Bulk_trade_item_lists_are_bounded_and_require_positive_ids()
    {
        var tooMany = new PyList(Enumerable.Range(1, TradeMgrService.MaxTradeBulkItems + 1).Select(id => (PyValue)new PyInt(id)).ToList());
        var error = Assert.Throws<MachoWrappedException>(() => TradeMgrService.ItemIdsOf(tooMany));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);

        error = Assert.Throws<MachoWrappedException>(() => TradeMgrService.ItemIdsOf(new PyList([new PyInt(1), new PyInt(-2)])));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.Equal([1L, 2L], TradeMgrService.ItemIdsOf(new PyList([new PyInt(1), new PyInt(2), new PyInt(1)])));
    }

    [Fact]
    public void Trade_manifests_reject_non_finite_and_overflowing_isk_amounts()
    {
        foreach (PyValue amount in new PyValue[] { new PyReal(double.NaN), new PyReal(double.PositiveInfinity), new PyReal(double.NegativeInfinity), new PyReal(1e300) })
        {
            var manifest = new PyObjectEx(ObjectExKind.Ex1, new PyStr("KeyVal"), [], [
                new(new PyStr("tradeContainerID"), new PyLong(1)),
                new(new PyStr("money"), new PyList([amount, new PyInt(0)])),
                new(new PyStr("tradeItems"), new PyList([])),
            ]);

            var error = Assert.Throws<MachoWrappedException>(() => TradeMgrService.ManifestOf(manifest));
            Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        }
    }

    private static ClientSession Session(long characterId, long? stationId = null, long? structureId = null)
    {
        var session = new ClientSession { CharacterId = characterId };
        if (stationId is { } station) session.Set("stationid", new PyInt(station));
        if (structureId is { } structure) session.Set("structureid", new PyInt(structure));
        return session;
    }
}
