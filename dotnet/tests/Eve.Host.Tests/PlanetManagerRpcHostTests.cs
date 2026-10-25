using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Planets;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Planet_launch_tax_and_colony_reads_use_registered_host_routes()
    {
        var planetMgr = new PlanetMgrService(
            null!, null!, null!, null!, null!, null!, null!, () => FixedNowMs,
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, "eve");
        var taxRates = new PlanetOrbitalRegistryService(null!, () => FixedNowMs);
        Start(configure: registry =>
        {
            registry.Register(planetMgr);
            registry.Register(taxRates);
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue?> Call(uint callId, string service, string method, PyTuple args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, service, method, args, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        var planets = Assert.IsType<PyObjectEx>(await Call(1, "planetMgr", "GetPlanetsForChar", Py.Tuple()));
        Assert.Equal(ObjectExKind.Ex2, planets.Kind);
        Assert.Empty(planets.List);

        var launches = Assert.IsType<PyObjectEx>(await Call(2, "planetMgr", "GetMyLaunchesDetails", Py.Tuple()));
        Assert.Equal(ObjectExKind.Ex2, launches.Kind);
        Assert.Empty(launches.List);

        Assert.Equal(PyBool.False, await Call(3, "planetMgr", "DeleteLaunch", Py.Tuple(new PyInt(7001))));
        Assert.Equal(PyNone.Instance, await Call(
            4, "planetOrbitalRegistryBroker", "GetTaxRate", Py.Tuple(new PyInt(30000142))));
    }
}
