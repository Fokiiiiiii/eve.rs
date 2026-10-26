using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Support_and_insurgency_rpcs_return_client_shapes_through_host_registration()
    {
        var events = new PyDict([new(new PyInt(7001), new PyList([
            new PyList(Enumerable.Repeat<PyValue>(PyNone.Instance, 12).ToArray()),
        ]))]);
        var campaign = new InsurgencyCampaignSnapshot(
            10, Py.FileTimeFromUnixMs(FixedNowMs + 60_000), 500012, 7000001, [30000142, 30000143], 7,
            1000, 250, 90000001, 3, 30000141, 500);
        Start(
            configure: registry => registry.Register(new InsurgencySolarsystemService(
                new InsurgencySolarsystemRpcCallbacks((systemId, _) =>
                    ValueTask.FromResult<IReadOnlyList<InsurgencyCampaignSnapshot>>(
                        systemId is null or 30000142 ? [campaign] : [])))),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(42));
                session.Set("solarsystemid2", new PyInt(30000142));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<PyValue?> Call(uint id, string service, string method, params PyValue[] args)
        {
            var kwargs = new PyDict([new(new PyStr("machoVersion"), new PyInt(496))]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, service, method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        var config = Assert.IsType<PyObject>(await Call(1, "infoGatheringMgr", "GetStateAndConfig"));
        Assert.Equal("util.KeyVal", T(config.Name));
        var configFields = Assert.IsType<PyDict>(config.Args);
        var expectedConfigFields = new Dictionary<string, PyValue>
        {
            ["isEnabled"] = new PyInt(0),
            ["infoTypes"] = new PyList([]),
            ["clientWorkerInterval"] = new PyInt(0),
            ["infoTypesOncePerRun"] = new PyList([]),
            ["infoTypeAggregates"] = new PyDict([]),
            ["infoTypeParameters"] = new PyDict([]),
        };
        Assert.Equal(expectedConfigFields.Count, configFields.Entries.Count);
        foreach (var (name, expected) in expectedConfigFields)
            Assert.Equal(expected, configFields.Entries.Single(entry => T(entry.Key) == name).Value);
        var statuses = Assert.IsType<PyDict>(await Call(2, "infoGatheringMgr", "LogInfoEventsFromClient", events));
        Assert.Empty(statuses.Entries);

        var reportMethods = new (string Method, PyValue[] Args)[]
        {
            ("report_inappropriate_language", [new PyInt(9001), new PyStr("reason"), new PyStr("time"), new PyStr("channel"), new PyStr("chat")]),
            ("report_offensive_mail", [new PyInt(9001), new PyStr("reason")]),
            ("report_macro_use", [new PyInt(9001), new PyStr("reason")]),
            ("report_questionable_transaction", [new PyInt(9001), new PyStr("reason")]),
            ("report_inappropriate_character_name", [new PyInt(9001), new PyStr("reason")]),
            ("report_exploit_abuse", [new PyInt(9001), new PyStr("reason")]),
            ("report_gambling", [new PyInt(9001), new PyStr("reason")]),
            ("report_other", [new PyInt(9001), new PyStr("reason")]),
        };
        uint correlationId = 3;
        foreach (var (method, args) in reportMethods)
            Assert.Same(PyNone.Instance, await Call(correlationId++, "ingamereport", method, args));

        var all = Assert.IsType<PyList>(await Call(correlationId++, "insurgencySolarsystem", "GetAllVisibleCampaigns"));
        var snapshot = Assert.IsType<PyObjectEx>(Assert.Single(all.Items));
        Assert.Equal(ObjectExKind.Ex2, snapshot.Kind);
        var local = Assert.IsType<PyList>(await Call(correlationId, "insurgencySolarsystem", "GetLocalCampaignClientSnapshot"));
        Assert.Equal(new PyInt(30000142), local.Items[0]);
        Assert.Equal(snapshot, local.Items[1]);
    }
}
