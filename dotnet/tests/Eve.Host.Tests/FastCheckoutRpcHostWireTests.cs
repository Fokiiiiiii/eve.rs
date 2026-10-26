using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Fast_checkout_reads_keep_the_client_shapes_and_buy_offer_fails_closed()
    {
        var catalog = NewEdenStoreCatalog.Default;
        Start(configure: registry => registry.Register(new FastCheckoutService(catalog)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<(MachoPacket Envelope, PyValue Result)> Invoke(uint id, string method, PyDict? kwargs = null)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                id,
                "FastCheckoutService",
                method,
                new PyTuple([]),
                kwargs ?? MachoVersion()))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(reply.Payload);
            var envelope = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            return (envelope, envelope.Type == MachoMessage.CallRsp
                ? Result(decoded) ?? throw new InvalidOperationException("RPC response has no result")
                : PyNone.Instance);
        }

        async Task<PyValue> Read(uint id, string method)
        {
            var (response, result) = await Invoke(id, method);
            Assert.Equal(MachoMessage.CallRsp, response.Type);
            return result;
        }

        Assert.Equal(PyValue.Of(catalog.FastCheckoutEnabled), await Read(1, "IsFastCheckoutEnabledForUser"));
        Assert.Equal(PyBool.True, await Read(2, "ClearFastCheckoutCacheForUser"));

        var testing = Assert.IsType<PyTuple>(await Read(3, "GetTestingConfiguration"));
        Assert.Equal(3, testing.Items.Count);
        Assert.Equal(PyValue.Of(catalog.ChinaFunnelEnabled), testing.Items[0]);
        Assert.Equal(new PyBytes(Encoding.UTF8.GetBytes(catalog.FakeBuyPlexOfferUrl)), testing.Items[1]);
        Assert.Equal(PyValue.Of(catalog.UseShellExecuteToBuyPlexOffer), testing.Items[2]);

        var offers = Assert.IsType<PyDict>(await Read(4, "GetOffersForUser"));
        var plex = Assert.Single(offers.Entries);
        Assert.Equal(new PyBytes(Encoding.UTF8.GetBytes("plex")), plex.Key);
        Assert.Empty(Assert.IsType<PyList>(plex.Value).Items);

        var (buy, _) = await Invoke(5, "BuyOffer", new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("offer"), new PyDict([])),
            new KeyValuePair<PyValue, PyValue>(new PyStr("password"), new PyStr("client-password")),
            new KeyValuePair<PyValue, PyValue>(new PyStr("purchaseTraceID"), new PyStr("trace")),
            new KeyValuePair<PyValue, PyValue>(new PyStr("journeyID"), new PyStr("journey")),
        ]));
        Assert.Equal(MachoMessage.ErrorResponse, buy.Type);
        var errorHeader = Assert.IsType<PyTuple>(buy.Payload);
        Assert.Equal(new PyInt(MachoMessage.CallReq), errorHeader.Items[0]);
        var wrapped = Assert.IsType<PyTuple>(errorHeader.Items[2]);
        var userError = Assert.IsType<PyObjectEx>(
            Assert.IsType<PySubStream>(Assert.Single(wrapped.Items)).Decoded);
        var expected = Assert.IsType<PyObjectEx>(MachoErrors.UserErrorPayload("FAST_CHECKOUT_UNAVAILABLE", []));
        Assert.Equal(Assert.IsType<PyTuple>(expected.Header).Items[0],
            Assert.IsType<PyTuple>(userError.Header).Items[0]);
    }
}
