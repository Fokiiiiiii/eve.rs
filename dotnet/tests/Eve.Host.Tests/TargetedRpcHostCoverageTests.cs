using Eve.Marshal;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Targeted_support_rpcs_keep_the_client_wire_contract_through_host_registration()
    {
        Start(
            strictRpc: true,
            sessionOpened: session => session.Set("charid", new PyInt(9001)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<PyValue> Call(long callId, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(
                open.SessionId,
                MarshalCodec.Encode(Packets.Call(open, callId, service, method, args))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload))
                ?? throw new InvalidOperationException("The host returned an empty result.");
        }

        var reports = new (string Method, PyValue[] Args)[]
        {
            ("report_gambling", [new PyInt(9001), new PyStr("reason")]),
            ("report_inappropriate_character_name", [new PyInt(9001), new PyStr("reason")]),
            ("report_inappropriate_language", [new PyInt(9001), new PyStr("reason"), new PyStr("time"), new PyStr("channel"), new PyStr("content")]),
            ("report_macro_use", [new PyInt(9001), new PyStr("reason")]),
            ("report_offensive_mail", [new PyInt(9001), new PyStr("reason")]),
            ("report_other", [new PyInt(9001), new PyStr("reason")]),
            ("report_questionable_transaction", [new PyInt(9001), new PyStr("reason")]),
        };

        long callId = 1;
        foreach (var (method, args) in reports)
            Assert.Same(PyNone.Instance, await Call(callId++, "ingamereport", method, args));

        var timers = Assert.IsType<PyTuple>(await Call(callId++, "jumpTimers", "GetTimers", new PyInt(9001)));
        Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance, PyNone.Instance]), timers);
        Assert.Same(PyNone.Instance, await Call(callId, "keeper", "GetCurrentDungeonForCharacter"));
    }
}
