using System.Text;
using Eve.Characters;
using Eve.Host;
using Eve.Marshal;
using Eve.Market;
using Eve.Services.Market;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Market_proxy_database_failures_are_client_user_errors_over_the_host_route()
    {
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=market_probe;Username=market_probe;Timeout=1;Pooling=false");
        var market = new MarketProxyService(
            new MarketStore(source), null!, null!, new PgCharacterStore(source), null!, null!, null!,
            () => FixedNowMs, null!);

        Start(
            configure: registry => registry.Register(market),
            strictRpc: true,
            sessionOpened: session =>
            {
                session.CharacterId = 90_000_001;
                session.Set("charid", new PyInt(90_000_001));
                session.Set("stationid", new PyLong(60_000_001));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(496)),
        ]);
        async Task AssertUnavailableAsync(uint callId, string method, params PyValue[] args)
        {
            var call = Packets.CallWithKwargs(open, callId, "marketProxy", method, new PyTuple(args), kwargs);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(call)));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var state = ((PyTuple)((PyObject)MarshalCodec.Decode(reply.Payload)).Args).Items;
            Assert.Equal(15, ((PyInt)state[0]).Value);
            var wire = Encoding.ASCII.GetString(reply.Payload.Span);
            Assert.Contains("eveexceptions.UserError", wire);
            Assert.Contains("CustomInfo", wire);
            Assert.Contains("Market is currently offline, elysian says sorry!", wire);
            Assert.DoesNotContain("RuntimeError", wire);
        }

        await AssertUnavailableAsync(1, "StartupCheck");
        await AssertUnavailableAsync(2, "GetStationAsks");
        await AssertUnavailableAsync(3, "CharGetTransactions", PyNone.Instance);
        await AssertUnavailableAsync(4, "GetOrders", new PyLong(35));
    }
}
