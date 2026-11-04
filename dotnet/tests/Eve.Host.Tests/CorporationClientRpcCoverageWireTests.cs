using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Corporation_client_reads_route_through_host_with_transport_kwargs()
    {
        var readCorporations = new List<long>();
        var corporationManager = new CorpMgrService(
            null!,
            null!,
            readAggressionSettings: (id, _) =>
            {
                readCorporations.Add(id);
                return ValueTask.FromResult<CorpMgrAggressionSettings?>(
                    new CorpMgrAggressionSettings(123, 456));
            });
        var recruitmentProxy = new CorpRecruitmentProxyService(null!, null!, () => 0);

        Start(configure: registry =>
        {
            registry.Register(corporationManager);
            registry.Register(recruitmentProxy);
        });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long callId, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        var single = Assert.IsType<PyObject>(await Call(
            1, "corpmgr", "GetAggressionSettings", new PyLong(70000001)));
        var singleFields = Assert.IsType<PyDict>(single.Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyLong(123), singleFields["_enableAfter"]);
        Assert.Equal(new PyLong(456), singleFields["_disableAfter"]);

        var corporationIds = Enumerable.Range(1, 500)
            .Select(index => (PyValue)new PyLong(70000000 + index))
            .ToArray();
        var bulk = Assert.IsType<PyDict>(await Call(
            2, "corpmgr", "GetAggressionSettingsForCorps", new PyList(corporationIds)));
        Assert.Equal(500, bulk.Entries.Count);

        Assert.Empty(Assert.IsType<PyList>(await Call(
            3, "corpRecProxy", "GetRecruitmentAdsForCorporation")).Items);

        var audit = Assert.IsType<PyTuple>(await Call(
            4, "corpmgr", "AuditMember", new PyInt(0), new PyLong(0), new PyLong(0), new PyInt(10)));
        Assert.Equal(2, audit.Items.Count);
        foreach (var result in audit.Items)
        {
            var rowset = Assert.IsType<PyObject>(result);
            var fields = Assert.IsType<PyDict>(rowset.Args).Entries
                .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
            Assert.Empty(Assert.IsType<PyList>(fields["lines"]).Items);
        }

        var expectedCorporations = new long[] { 70000001 }
            .Concat(Enumerable.Range(1, 500).Select(index => 70000000L + index));
        Assert.Equal(expectedCorporations, readCorporations);
    }
}
