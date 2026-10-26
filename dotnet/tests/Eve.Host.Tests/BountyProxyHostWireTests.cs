using Eve.Crimewatch;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Crimewatch;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Bounty_proxy_client_reads_use_the_registered_host_route_and_wire_shapes()
    {
        const long characterId = 90000003;
        const long targetId = 90000004;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var service = new BountyProxyKillRightService(
            readKillRights: (_, _) => Task.FromResult<IReadOnlyList<KillRight>>(
                [new KillRight(17, characterId, targetId, 30000142, now, now + 60_000, 250m, null)]),
            nowUnixMs: () => now,
            bountyRpcHandler: call => ValueTask.FromResult<PyValue?>(call.Method switch
            {
                "AddToBounty" => Py.KeyVal(("targetID", new PyLong(targetId)), ("bounty", new PyInt(150))),
                "GetBounties" => new PyList([
                    Py.Tuple(InventoryWire.Id(targetId),
                        Py.KeyVal(("targetID", InventoryWire.Id(targetId)), ("bounty", new PyInt(150))))
                ]),
                "GetMyBounties" => new PyList([
                    Py.KeyVal(("contributionID", new PyLong(31)), ("targetID", InventoryWire.Id(targetId)),
                        ("amount", new PyInt(150)))
                ]),
                "GetTopPilotBountyHunters" or "GetTopCorporationBountyHunters" or "GetTopAllianceBountyHunters" =>
                    Py.Tuple(new PyList([
                        Py.KeyVal(("bountyHunterID", InventoryWire.Id(characterId)),
                            ("bountiesClaimed", new PyInt(150)), ("numberOfKills", new PyInt(1)),
                            ("rowNumber", new PyInt(1)))
                    ]), Py.FileTimeLong(Py.FileTimeFromUnixMs(now))),
                "GMReimburseBounties" or "GMClearBountyCache" => PyNone.Instance,
                _ => null,
            }));
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("corpid", new PyLong(98000003));
                session.Set("allianceid", new PyLong(99000003));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        long callId = 0;
        async Task<(int Type, PyValue? Result)> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "bountyProxy", method, new PyTuple(args), kwargs))));
            var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(response));
            return (packet.Type, packet.Type == MachoMessage.CallRsp ? Result(response) : null);
        }

        PyValue Field(PyValue value, string name)
        {
            var fields = value switch
            {
                PyDict dict => dict,
                PyObject { Args: PyDict dict } => dict,
                _ => throw new InvalidOperationException("expected a KeyVal or dictionary"),
            };
            return fields.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;
        }

        var placed = await Call("AddToBounty", new PyLong(targetId), new PyInt(150));
        Assert.Equal(MachoMessage.CallRsp, placed.Type);
        Assert.Equal(new PyInt(150), Field(placed.Result!, "bounty"));

        var bounties = Assert.IsType<PyList>(
            (await Call("GetBounties", new PyList([new PyLong(targetId)]))).Result);
        Assert.Equal(new PyInt(targetId), Assert.IsType<PyTuple>(Assert.Single(bounties.Items)).Items[0]);
        var mine = Assert.IsType<PyList>((await Call("GetMyBounties")).Result);
        Assert.Equal(new PyInt(31), Field(Assert.Single(mine.Items), "contributionID"));

        var killRights = Assert.IsType<PyList>((await Call("GetMyKillRights")).Result);
        Assert.Equal(new PyInt(17), Field(Assert.Single(killRights.Items), "killRightID"));
        foreach (var method in new[]
        {
            "GetTopPilotBountyHunters", "GetTopCorporationBountyHunters", "GetTopAllianceBountyHunters",
        })
        {
            var ranked = Assert.IsType<PyTuple>((await Call(method, new PyInt(0))).Result);
            Assert.Single(Assert.IsType<PyList>(ranked.Items[0]).Items);
            Assert.IsType<PyLong>(ranked.Items[1]);
        }
    }
}
