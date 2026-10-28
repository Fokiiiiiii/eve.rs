using System.Text;
using Eve.Crimewatch;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Crimewatch;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Bounty_proxy_host_wire_preserves_client_shapes_and_rejects_unwired_operations()
    {
        const long characterId = 90_000_001;
        const long targetId = 90_000_002;
        const long now = 1_790_646_377_000;
        ServiceCall? observed = null;
        string KeyName(PyValue key) => key switch
        {
            PyStr text => text.Value,
            PyBytes bytes => Encoding.ASCII.GetString(bytes.Value.Span),
            _ => string.Empty,
        };
        var killRight = new KillRight(1, characterId, targetId, 30_000_142, now, now + 86_400_000, 12_345m, null);
        var service = new BountyProxyKillRightService(
            readKillRights: (ownerId, _) =>
            {
                Assert.Equal(characterId, ownerId);
                return Task.FromResult<IReadOnlyList<KillRight>>([killRight]);
            },
            nowUnixMs: () => now,
            bountyRpcHandler: call =>
            {
                observed = call;
                if (call.Method != "GetBounties")
                    return ValueTask.FromResult<PyValue?>(null);

                return ValueTask.FromResult<PyValue?>(new PyList([
                    new PyTuple([
                        new PyLong(targetId),
                        Py.KeyVal(
                            ("targetID", new PyLong(targetId)),
                            ("bounty", new PyReal(5_000_000)))
                    ])
                ]));
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyLong(characterId)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))
        ]);
        async Task<Send> Call(string method, int callId, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, callId, "bountyProxy", method, new PyTuple(args), kwargs))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        var bountyReply = await Call("GetBounties", 1, new PyList([new PyLong(targetId)]));
        var bountyPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(bountyReply.Payload)));
        Assert.Equal(MachoMessage.CallRsp, bountyPacket.Type);
        var rows = Assert.IsType<PyList>(Result(MarshalCodec.Decode(bountyReply.Payload)));
        var row = Assert.IsType<PyTuple>(Assert.Single(rows.Items));
        Assert.Equal(new PyLong(targetId), row.Items[0]);
        var payload = Assert.IsType<PyObject>(row.Items[1]);
        var bountyFields = Assert.IsType<PyDict>(payload.Args);
        Assert.Contains(bountyFields.Entries,
            entry => KeyName(entry.Key) == "targetID" && entry.Value == new PyLong(targetId));
        Assert.Contains(bountyFields.Entries,
            entry => KeyName(entry.Key) == "bounty" && entry.Value == new PyReal(5_000_000));
        Assert.Equal("GetBounties", observed?.Method);
        Assert.Equal(1, observed?.Args.Count);
        Assert.Equal(new PyLong(targetId),
            Assert.Single(Assert.IsType<PyList>(observed!.Arg(0)).Items));
        Assert.Null(observed.Kwarg("machoVersion"));

        await Call("GetBounties", 2, new PyList([new PyInt(targetId)]));
        Assert.Equal(new PyInt(targetId),
            Assert.Single(Assert.IsType<PyList>(observed!.Arg(0)).Items));

        var killRightsReply = await Call("GetMyKillRights", 3);
        var killRightsPacket = Assert.IsType<MachoPacket>(
            MachoPacket.Decode(MarshalCodec.Decode(killRightsReply.Payload)));
        Assert.Equal(MachoMessage.CallRsp, killRightsPacket.Type);
        var killRights = Assert.IsType<PyList>(Result(MarshalCodec.Decode(killRightsReply.Payload)));
        var killRightFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(
            Assert.Single(killRights.Items)).Args);
        Assert.Contains(killRightFields.Entries,
            entry => KeyName(entry.Key) == "killRightID" && entry.Value == new PyLong(1));
        Assert.Contains(killRightFields.Entries,
            entry => KeyName(entry.Key) == "price" && entry.Value == new PyReal(12_345));
        Assert.Contains(killRightFields.Entries,
            entry => KeyName(entry.Key) == "restrictedTo" && entry.Value == PyNone.Instance);

        var unwiredBounty = await Call("GetMyBounties", 4);
        AssertWrappedError(unwiredBounty, "eveexceptions.UserError", "CustomNotify");
        Assert.Contains("Bounty service is not wired",
            Encoding.Latin1.GetString(unwiredBounty.Payload.Span));

        var unrestrictedSale = await Call(
            "SellKillRight", 5, new PyLong(1), new PyLong(10), PyNone.Instance);
        AssertWrappedError(unrestrictedSale, "eveexceptions.UserError", "CustomNotify");
        Assert.Contains("Kill-right service is not wired",
            Encoding.Latin1.GetString(unrestrictedSale.Payload.Span));

        foreach (var (method, callId) in new[]
                 {
                     ("GMReimburseBounties", 6),
                     ("GMClearBountyCache", 7),
                 })
        {
            var unsupported = await Call(method, callId);
            AssertWrappedError(unsupported, "eveexceptions.UserError", "CustomNotify");
            Assert.Contains("GM bounty operations are not supported",
                Encoding.Latin1.GetString(unsupported.Payload.Span));
        }
    }
}
