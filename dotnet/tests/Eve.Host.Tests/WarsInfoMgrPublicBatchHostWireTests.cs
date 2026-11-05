using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Corporations;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Public_war_info_accepts_structure_war_id_batches_through_registered_host_wire()
    {
        await using var rig = await WarsWireRig.CreateAsync();
        await rig.SetAsync("war", 1001, 1002, new
        {
            timeDeclared = 1_700_000_000_000L,
            timeStarted = 1_700_000_000_001L,
        });
        await rig.SetAsync("war", 1003, 1004, new
        {
            timeDeclared = 1_700_000_000_002L,
            timeStarted = 1_700_000_000_003L,
        });
        var wars = await rig.Relations.ListAsync("war");
        Assert.Equal(2, wars.Count);
        var firstWar = wars[0];
        var secondWar = wars[1];

        Start(
            configure: registry => registry.Register(
                new WarsInfoMgrService(rig.Relations, () => 1_700_000_000_100L)),
            sessionOpened: session => session.CharacterId = 9001);

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Call(long callId, PyValue argument)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                callId,
                "warsInfoMgr",
                "GetPublicWarInfo",
                new PyTuple([argument]),
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload))!;
        }

        var batch = Assert.IsType<PyList>(await Call(
            1,
            PyList.Of(new PyLong(firstWar.Id), new PyLong(999_999_999), new PyLong(secondWar.Id))));
        Assert.Equal(2, batch.Items.Count);
        Assert.Equal(firstWar.Id, ReadWarNumber(WarKeyValFields(batch.Items[0])["warID"]));
        Assert.Equal(secondWar.Id, ReadWarNumber(WarKeyValFields(batch.Items[1])["warID"]));

        var tupleBatch = Assert.IsType<PyList>(await Call(
            2,
            new PyTuple([new PyLong(secondWar.Id), new PyLong(firstWar.Id)])));
        Assert.Equal(
            [secondWar.Id, firstWar.Id],
            tupleBatch.Items.Select(item => ReadWarNumber(WarKeyValFields(item)["warID"])).ToArray());

        var scalar = WarKeyValFields(await Call(3, new PyLong(firstWar.Id)));
        Assert.Equal(firstWar.Id, ReadWarNumber(scalar["warID"]));
        Assert.Same(PyValue.None, await Call(4, new PyLong(999_999_999)));
    }
}
