using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Corporation_read_rpcs_keep_client_shapes_through_host_with_transport_kwargs()
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
        var corporationService = new CorporationService(null!, null!);

        Start(configure: registry =>
        {
            registry.Register(corporationManager);
            registry.Register(corporationService);
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

        var corporationIds = Enumerable.Range(1, 500)
            .Select(index => (PyValue)new PyLong(70000000 + index))
            .ToArray();
        var settings = Assert.IsType<PyDict>(await Call(
            1, "corpmgr", "GetAggressionSettingsForCorps", new PyList(corporationIds)));
        Assert.Equal(500, settings.Entries.Count);
        Assert.Equal(Enumerable.Range(1, 500).Select(index => 70000000L + index), readCorporations);
        var firstSettings = Assert.IsType<PyObject>(settings.Entries[0].Value);
        var firstFields = Assert.IsType<PyDict>(firstSettings.Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyLong(123), firstFields["_enableAfter"]);
        Assert.Equal(new PyLong(456), firstFields["_disableAfter"]);

        Assert.Same(PyNone.Instance, await Call(
            2, "corporationSvc", "GetCorpInfo", new PyInt(0)));

        var recipients = Assert.IsType<PyObject>(await Call(
            3, "corporationSvc", "GetRecipientsOfMedal", new PyInt(700001)));
        var fields = Assert.IsType<PyDict>(recipients.Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Empty(Assert.IsType<PyList>(fields["lines"]).Items);
    }
}
