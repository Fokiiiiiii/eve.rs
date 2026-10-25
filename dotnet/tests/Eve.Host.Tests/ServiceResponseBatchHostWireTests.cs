using System.Text;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Corporations;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private static PyDict TransportKwargs() => new([
        new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
    ]);

    private async Task<PyValue> HostRpcAsync(TestFront front, SessionOpen open, long callId, string service, string method, params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
        return Result(decoded)!;
    }

    private async Task<(TestFront Front, SessionOpen Open)> OpenFrontAsync()
    {
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        return (front, open);
    }

    private static string WireText(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        _ => throw new Xunit.Sdk.XunitException($"Expected a wire string, got {value?.GetType().Name ?? "null"}")
    };

    private static PyDict WireRow(PyValue row) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args);

    private static PyValue WireField(PyValue row, string name) =>
        WireRow(row).Entries.Single(pair => WireText(pair.Key) == name).Value;

    private static long WireInteger(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected a wire integer, got {value?.GetType().Name ?? "null"}")
    };

    [Fact]
    public async Task Dynamic_item_missing_character_is_a_host_wire_refusal()
    {
        Start(configure: registry => registry.Register(new DynamicItemRpcService(null!, null!, null!, null!, null)));
        var (front, open) = await OpenFrontAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "dynamicItemService", "CreateDynamicItem",
                Py.Tuple(new PyInt(47297), new PyLong(5001)), TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type);
        Assert.Contains("CustomNotify", Encoding.ASCII.GetString(send.Payload.Span));
    }

    [Fact]
    public async Task Decode_item_returns_its_owned_supported_resource_through_host_dispatch()
    {
        const long pilot = 90_001;
        var item = new Item(55, 123, pilot, 400, 0, 1, true, null,
            CustomInfo: """{"evejsEncodedItemUrl":"https://assets.example/item.png"}""");
        var service = new EncodedItemsRpcService((_, _) => ValueTask.FromResult<Item?>(item),
            (_, candidate, _) => ValueTask.FromResult(candidate.TypeId == 123));
        Start(configure: registry => registry.Register(service), sessionOpened: session => session.Set("charid", new PyInt(pilot)));
        var (front, open) = await OpenFrontAsync();
        Assert.Equal("https://assets.example/item.png",
            WireText(await HostRpcAsync(front, open, 1, "encodedItems", "DecodeItem", new PyLong(55))));
    }

    [Fact]
    public async Task Mutual_war_invite_methods_keep_their_host_wire_shapes_and_argument_order()
    {
        const long corporation = 50, other = 300;
        var invites = new List<MutualWarInvite> { new(other, corporation, 100), new(corporation, 400, 200), new(999, 888, 300) };
        var calls = new List<string>();
        var service = new MutualWarInviteMgrService(
            (_, _) => ValueTask.FromResult<IReadOnlyList<MutualWarInvite>>(invites),
            (_, _) => ValueTask.FromResult(false),
            (_, from, to, _) => { calls.Add($"send:{from}:{to}"); return ValueTask.CompletedTask; },
            (_, from, to, _) => { calls.Add($"withdraw:{from}:{to}"); return ValueTask.CompletedTask; },
            (_, from, to, accepts, _) => { calls.Add($"respond:{from}:{to}:{accepts}"); return ValueTask.CompletedTask; },
            (_, owner, value, _) => { calls.Add($"block:{owner}:{value}"); return ValueTask.CompletedTask; });
        Start(configure: registry => registry.Register(service), sessionOpened: session =>
        {
            session.Set("charid", new PyInt(200));
            session.Set("corpid", new PyInt(corporation));
        });
        var (front, open) = await OpenFrontAsync();
        var pending = Assert.IsType<PyList>(await HostRpcAsync(front, open, 1, "mutualWarInviteMgr", "GetPendingInvitesForSession"));
        Assert.Equal(2, pending.Items.Count);
        Assert.Equal(corporation, WireInteger(WireField(pending.Items[0], "fromOwnerID")));
        Assert.Equal(400, WireInteger(WireField(pending.Items[0], "toOwnerID")));
        Assert.Equal(200, WireInteger(WireField(pending.Items[0], "sentDate")));
        Assert.Equal(other, WireInteger(WireField(pending.Items[1], "fromOwnerID")));
        Assert.Equal(corporation, WireInteger(WireField(pending.Items[1], "toOwnerID")));
        Assert.Equal(new PyInt(0), await HostRpcAsync(front, open, 2, "mutualWarInviteMgr", "IsCorpInvitesBlockedPlayer"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 3, "mutualWarInviteMgr", "SendInviteByPlayer", new PyInt(other)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 4, "mutualWarInviteMgr", "WithdrawInviteByPlayer", new PyInt(other)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 5, "mutualWarInviteMgr", "RespondToInviteByPlayer", new PyInt(other), PyBool.True));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 6, "mutualWarInviteMgr", "SetInvitesBlockedByPlayer", PyBool.True));
        Assert.Equal(["send:50:300", "withdraw:50:300", "respond:300:50:True", "block:50:True"], calls);
    }

    [Fact]
    public async Task Asset_safety_reads_forward_character_and_corporation_rows_through_host_dispatch()
    {
        var callbacks = new TestAssetSafetyCallbacks();
        Start(configure: registry => registry.Register(new StructureAssetSafetyService(callbacks)),
            sessionOpened: session => { session.Set("charid", new PyInt(200)); session.Set("corpid", new PyInt(50)); });
        var (front, open) = await OpenFrontAsync();
        var personal = Assert.IsType<PyList>(await HostRpcAsync(front, open, 1, "structureAssetSafety", "GetItemsInSafetyForCharacter"));
        var corporate = Assert.IsType<PyList>(await HostRpcAsync(front, open, 2, "structureAssetSafety", "GetItemsInSafetyForCorp"));
        Assert.Single(personal.Items);
        Assert.Equal(7001, WireInteger(WireField(personal.Items[0], "assetWrapID")));
        Assert.Equal("Personal assets", WireText(WireField(personal.Items[0], "wrapName")));
        Assert.Equal(133801234567890123, WireInteger(WireField(personal.Items[0], "ejectTime")));
        Assert.Same(PyNone.Instance, WireField(personal.Items[0], "nearestNPCStationInfo"));
        Assert.Single(corporate.Items);
        Assert.Equal(7002, WireInteger(WireField(corporate.Items[0], "assetWrapID")));
        Assert.Equal("Corporation assets", WireText(WireField(corporate.Items[0], "wrapName")));
        Assert.Equal((200L, 50L), callbacks.CorporateCaller);
        Assert.Equal(2, callbacks.ProcessedReads);
    }

    [Fact]
    public async Task Item_locking_without_a_corporation_context_returns_the_empty_client_collections_through_host()
    {
        Start(configure: registry => registry.Register(new ItemLockingService(null!)));
        var (front, open) = await OpenFrontAsync();
        Assert.Equal(new PyDict([]), await HostRpcAsync(front, open, 1, "itemLocking", "GetItemsByLocation", new PyLong(6001)));
        Assert.Equal(InventoryWire.EmptySet(), await HostRpcAsync(front, open, 2, "itemLocking", "GetLockedItemLocations"));
    }

    private sealed class TestAssetSafetyCallbacks : IStructureAssetSafetyReadCallbacks
    {
        public PyList Personal { get; } = new([
            Py.KeyVal(("assetWrapID", new PyLong(7001)), ("solarSystemID", new PyInt(30000142)),
                ("wrapName", new PyStr("Personal assets")), ("ejectTime", new PyLong(133801234567890123)),
                ("daysUntilCanDeliverConst", new PyInt(5)), ("daysUntilAutoMoveConst", new PyInt(20)),
                ("nearestNPCStationInfo", PyNone.Instance))]);
        public PyList Corporate { get; } = new([
            Py.KeyVal(("assetWrapID", new PyLong(7002)), ("solarSystemID", new PyInt(30000142)),
                ("wrapName", new PyStr("Corporation assets")), ("ejectTime", new PyLong(133801234567890123)),
                ("daysUntilCanDeliverConst", new PyInt(5)), ("daysUntilAutoMoveConst", new PyInt(20)),
                ("nearestNPCStationInfo", PyNone.Instance))]);
        public (long Character, long Corporation) CorporateCaller { get; private set; }
        public int ProcessedReads { get; private set; }
        public ValueTask ProcessDueAutomaticDeliveriesAsync(CancellationToken token) { ProcessedReads++; return ValueTask.CompletedTask; }
        public ValueTask<PyList> GetItemsInSafetyForCharacterAsync(long id, CancellationToken token) => ValueTask.FromResult(id == 200 ? Personal : new PyList([]));
        public ValueTask<PyList> GetItemsInSafetyForCorpAsync(long character, long corporation, CancellationToken token)
        { CorporateCaller = (character, corporation); return ValueTask.FromResult(Corporate); }
        public ValueTask<PyDict> GetWrapNamesAsync(long character, long corporation, IReadOnlyList<long> wraps, CancellationToken token) => ValueTask.FromResult(new PyDict([]));
        public ValueTask<PyTuple> GetStructuresICanDeliverToAsync(long character, long corporation, long system, CancellationToken token) => ValueTask.FromResult(new PyTuple([]));
        public ValueTask<AssetSafetyActionState> MovePersonalAssetsToSafetyAsync(ServiceCall call, long system, long structure, CancellationToken token) => ValueTask.FromResult(AssetSafetyActionState.NotCompleted);
        public ValueTask<AssetSafetyActionState> MoveCorpAssetsToSafetyAsync(ServiceCall call, long system, long structure, CancellationToken token) => ValueTask.FromResult(AssetSafetyActionState.NotCompleted);
        public ValueTask<AssetSafetyActionState> MoveSafetyWrapToStructureAsync(ServiceCall call, long wrap, long system, long destination, CancellationToken token) => ValueTask.FromResult(AssetSafetyActionState.NotCompleted);
        public ValueTask<bool> MoveEjectTimeGMAsync(ServiceCall call, long wrap, int days, CancellationToken token) => ValueTask.FromResult(false);
    }
}
