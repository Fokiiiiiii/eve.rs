using System.Text;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Config_identifier_errors_keep_the_user_error_code_through_host_wire()
    {
        var config = new ConfigService(null!, null!, new StationCatalog([]), new SolarSystemCatalog([]));
        Start(strictRpc: true, configure: registry => registry.Register(config));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        long callId = 1;

        async Task<PyValue> Call(string method, PyValue argument)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId++, "config", method, new PyTuple([argument]), kwargs))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            return MarshalCodec.Decode(response.Payload);
        }

        string UserErrorCode(PyValue response)
        {
            var packet = MachoPacket.Decode(response);
            Assert.NotNull(packet);
            Assert.Equal(MachoMessage.ErrorResponse, packet!.Type);
            var errorState = Assert.IsType<PyTuple>(packet.Payload).Items;
            Assert.Equal((int)MachoErrorType.WrappedException, Assert.IsType<PyInt>(errorState[1]).Value);
            var wrapped = Assert.IsType<PyTuple>(errorState[2]).Items;
            var userError = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
            var header = Assert.IsType<PyTuple>(userError.Header).Items;
            Assert.Equal(MachoErrors.UserErrorClass, Assert.IsType<PyToken>(header[0]).Value);
            var args = Assert.IsType<PyTuple>(header[1]).Items;
            return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(args[0]).Value.Span);
        }

        string[] multiMethods =
        [
            "GetMultiOwnersEx", "GetMultiCorpTickerNamesEx", "GetMultiAllianceShortNamesEx",
            "GetMultiLocationsEx", "GetPositionsEx",
        ];
        foreach (var method in multiMethods)
            Assert.Equal("CONFIG_IDENTIFIER_INVALID", UserErrorCode(await Call(method, new PyList([PyNone.Instance]))));

        Assert.Equal("CONFIG_IDENTIFIER_LIST_INVALID",
            UserErrorCode(await Call("GetMultiOwnersEx", new PyInt(42))));

        string[] singleMethods =
        [
            "GetOwnersEx", "GetCorpTickerNamesEx", "GetAllianceShortNamesEx", "GetLocationsEx", "GetPositionEx",
        ];
        foreach (var method in singleMethods)
            Assert.Equal("CONFIG_IDENTIFIER_INVALID", UserErrorCode(await Call(method, PyNone.Instance)));

        var stationReply = await Call("GetStationSolarSystemsByOwner", PyNone.Instance);
        Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(stationReply)!.Type);
        Assert.Empty(Assert.IsType<PyList>(Result(stationReply)!).Items);
    }
}
