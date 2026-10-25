using Eve.Marshal;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private static PyValue Value(PyDict dict, string key) =>
        dict.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == key).Value;
    [Fact]
    public async Task Dynamic_resource_cache_reads_are_returned_through_host_wire()
    {
        const long systemId = 30_000_142;
        var state = new EssMgrSystemData(
            EssId: 1,
            SolarSystemId: systemId,
            CurrentOutput: 1.25,
            MainValue: 42_000_000,
            ReserveValue: 7_000_000,
            ReserveBankPulsesRemaining: 3,
            ReserveBankPulsesTotal: 5,
            ReserveLinkedCharacterIds: new HashSet<long> { 90000001 });
        var quadrant = EssReserveKeys.All[0].Quadrant;
        var service = new DynamicResourceCacheMgrService(
            readSystem: (id, _) => ValueTask.FromResult<EssMgrSystemData?>(id == systemId ? state : null),
            listSystems: _ => ValueTask.FromResult<IReadOnlyList<EssMgrSystemData>>([state]),
            quadrantOf: _ => quadrant);
        Start(configure: registry => registry.Register(service));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<PyValue?> CallAsync(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                (uint)(args.Length + method.Length),
                "dynamicResourceCacheMgr",
                method,
                new PyTuple(args),
                new PyDict([new(new PyStr("machoVersion"), new PyInt(3396210))])))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        var settings = Assert.IsType<PyDict>(await CallAsync("GetDynamicResourceSettings"));
        Assert.Equal(17, settings.Entries.Count);
        Assert.Equal(new PyInt(9900), Value(settings, "autopaymentIntervalSeconds"));
        Assert.Equal(new PyReal(0.975), Value(settings, "mainBankPayRatio"));

        var agency = Assert.IsType<PyDict>(await CallAsync("GetESSAgencyData"));
        var system = Assert.IsType<PyDict>(agency.Entries.Single(entry => entry.Key is PyInt id && id.Value == systemId).Value);
        Assert.Equal(new PyReal(1.25), Value(system, "currentOutput"));
        Assert.Equal(new PyInt(42_000_000), Value(system, "mainValue"));
        Assert.Equal(new PyInt(7_000_000), Value(system, "reserveValue"));
        Assert.Same(PyBool.True, Value(system, "vaultOpen"));

        var details = Assert.IsType<PyDict>(await CallAsync("GetESSSystemDetails", new PyInt(systemId)));
        Assert.Equal(5, details.Entries.Count);
        Assert.Equal(new PyInt(1), Value(details, "activeLinks"));
        Assert.Equal(new PyInt(5), Value(details, "totalPulses"));
        Assert.Equal(new PyInt(300), Value(details, "reserveTimeChosen"));

        var keys = Assert.IsType<PyList>(await CallAsync("GetESSReserveBankKeys", new PyInt(systemId)));
        Assert.Equal(
            EssReserveKeys.All.Where(key => key.Quadrant == quadrant).Select(key => (PyValue)new PyInt(key.KeyTypeId)),
            keys.Items);
    }

}
