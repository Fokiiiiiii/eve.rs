using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Dogma;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Create_newbie_ship_reaches_its_registered_host_callback_and_returns_none()
    {
        var calls = new List<(long CharacterId, long StationId)>();
        var service = new DogmaIMService(
            new DogmaStatic([], [], []), null!, () => FixedNowMs, null!,
            createNewbieShip: (session, _) =>
            {
                calls.Add((session.CharacterId ?? 0, session.GetInt("stationid") ?? 0));
                return ValueTask.CompletedTask;
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(9001));
                session.Set("stationid", new PyInt(600));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "dogmaIM", "CreateNewbieShip",
                new PyTuple([new PyLong(7001), new PyLong(600)]), kwargs))));

        Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal([(9001L, 600L)], calls);
    }
}
