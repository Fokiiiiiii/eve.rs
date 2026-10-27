using System.Text;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public void Add_bulletin_keeps_client_keyword_arguments_and_positional_defaults()
    {
        var keywordCall = new ServiceCall
        {
            Method = "AddBulletin",
            Args = [new PyWString("title"), new PyWString("body")],
            Kwargs = Py.Dict(
                ("bulletinID", new PyLong(4_000_000_001)),
                ("editDateTime", new PyLong(134_273_341_290_000_000)),
                ("machoVersion", new PyInt(1))),
            Session = new ClientSession(),
        };

        var (keywordBulletinId, keywordEditDateTime) =
            AllianceRegistryService.BulletinEditArguments(keywordCall);
        Assert.Equal(new PyLong(4_000_000_001), keywordBulletinId);
        Assert.Equal(new PyLong(134_273_341_290_000_000), keywordEditDateTime);

        var positionalCall = new ServiceCall
        {
            Method = "AddBulletin",
            Args =
            [
                new PyWString("title"),
                new PyWString("body"),
                new PyInt(42),
                new PyLong(134_273_341_290_000_000),
            ],
            Session = new ClientSession(),
        };
        var (positionalBulletinId, positionalEditDateTime) =
            AllianceRegistryService.BulletinEditArguments(positionalCall);
        Assert.Equal(new PyInt(42), positionalBulletinId);
        Assert.Equal(new PyLong(134_273_341_290_000_000), positionalEditDateTime);

        var createCall = new ServiceCall
        {
            Method = "AddBulletin",
            Args = [new PyWString("title"), new PyWString("body")],
            Session = new ClientSession(),
        };
        var (createBulletinId, createEditDateTime) =
            AllianceRegistryService.BulletinEditArguments(createCall);
        Assert.Same(PyNone.Instance, createBulletinId);
        Assert.Same(PyNone.Instance, createEditDateTime);
    }

    [Fact]
    public async Task Alliance_registry_membership_error_uses_the_registered_host_route_with_macho_kwargs()
    {
        var allianceRegistry = new AllianceRegistryService(null!, null!, () => 0);
        Start(configure: registry => registry.Register(allianceRegistry));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = Py.Dict(("machoVersion", new PyInt(1)));
        var request = Packets.CallWithKwargs(
            open, 1, "allianceRegistry", "GetMembers", Py.Tuple([]), kwargs);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(reply, "eveexceptions.UserError", "CustomInfo");
        Assert.Contains("Alliance membership required.", Encoding.ASCII.GetString(reply.Payload.Span));
    }
    [Fact]
    public async Task Alliance_registry_validation_exception_is_wrapped_by_the_host_route()
    {
        var allianceRegistry = new AllianceRegistryService(null!, null!, () => 0);
        Start(configure: registry => registry.Register(allianceRegistry));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var request = Packets.Call(
            open, 1, "allianceRegistry", "GetRankedAlliances", Py.Tuple(new PyInt(-1)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(reply, "exceptions.RuntimeError", "allianceRegistry.GetRankedAlliances");
    }

}
