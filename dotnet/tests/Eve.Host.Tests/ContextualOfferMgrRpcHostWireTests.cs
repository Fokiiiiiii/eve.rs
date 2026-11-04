using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Contextual_offer_tracking_replies_none_and_records_client_events()
    {
        ContextualOfferMgrService? service = null;
        Start(
            configure: registry => service = Assert.IsType<ContextualOfferMgrService>(
                registry.Lookup("contextualOfferMgr")),
            sessionOpened: session =>
            {
                session.CharacterId = 4242;
                session.UserId = 9001;
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var methods = new[]
        {
            "TrackOfferClosed",
            "TrackOfferDelivered",
            "TrackOfferPurchased",
            "TrackOfferPurchasedSeen",
            "TrackOfferSeen",
        };
        var offers = new[] { 7001L, 7002L, 7003L, 7004L, 7005L };

        for (var i = 0; i < methods.Length; i++)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                (uint)(i + 1),
                "contextualOfferMgr",
                methods[i],
                new PyTuple([new PyLong(offers[i])]),
                MachoVersion()))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(reply.Payload);
            var envelope = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.CallRsp, envelope.Type);
            Assert.Equal(PyNone.Instance, Result(decoded));
        }

        var events = Assert.IsType<ContextualOfferMgrService>(service).Events;
        Assert.Equal(methods, events.Select(item => item.MethodName));
        Assert.Equal(offers, events.Select(item => item.OfferId));
        Assert.All(events, item =>
        {
            Assert.Equal(4242, item.CharacterId);
            Assert.Equal(9001, item.AccountId);
        });
    }
}
