using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Ship_fitting_info_uses_crate_store_metadata_through_host_route()
    {
        var root = JsonNode.Parse("""
            {
              "owners": {
                "90000012": {
                  "fittings": {
                    "7001": {
                      "fittingID": 7001,
                      "shipTypeID": 587,
                      "name": "Rifter fit",
                      "nameLabel": "UI/Fitting/RifterFit",
                      "icon": "res:/fit.png",
                      "insurance": "Platinum",
                      "fitData": [[34, 27, 1]]
                    },
                    "7002": {
                      "fittingID": 7002,
                      "shipTypeID": 587,
                      "name": "Alias fit",
                      "labelPath": "UI/Fitting/AliasFit",
                      "texturePath": "res:/alias.png",
                      "insuranceType": "Standard",
                      "fitData": [[34, 27, 1]]
                    },
                    "7003": {
                      "fittingID": 7003,
                      "shipTypeID": 587,
                      "name": "Plain fit",
                      "fitData": [[34, 27, 1]]
                    }
                  }
                }
              }
            }
            """)!.AsObject();
        var service = new ShipService(null!, () => FixedNowMs,
            getShipFittingInfo: (fittingId, _) =>
                ValueTask.FromResult(PersistenceCrateServiceBackend.ShipFittingInfoFromRoot(root, fittingId)));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("stationid", new PyInt(600)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyTuple> CallFitting(int requestId, PyValue fittingId)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, requestId, "ship", "GetShipFittingInfo", Py.Tuple(fittingId), kwargs))));
            return Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
                Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        }

        var canonical = await CallFitting(1, new PyLong(7001));
        Assert.Equal(4, canonical.Items.Count);
        Assert.IsType<PyBytes>(canonical.Items[0]);
        Assert.Equal("UI/Fitting/RifterFit", JsSemantics.StringValue(canonical.Items[0]));
        Assert.IsType<PyBytes>(canonical.Items[1]);
        Assert.Equal("res:/fit.png", JsSemantics.StringValue(canonical.Items[1]));
        Assert.Equal(new PyInt(587), canonical.Items[2]);
        Assert.IsType<PyBytes>(canonical.Items[3]);
        Assert.Equal("Platinum", JsSemantics.StringValue(canonical.Items[3]));

        var aliases = await CallFitting(2, new PyInt(7002));
        Assert.Equal(4, aliases.Items.Count);
        Assert.IsType<PyBytes>(aliases.Items[0]);
        Assert.Equal("UI/Fitting/AliasFit", JsSemantics.StringValue(aliases.Items[0]));
        Assert.IsType<PyBytes>(aliases.Items[1]);
        Assert.Equal("res:/alias.png", JsSemantics.StringValue(aliases.Items[1]));
        Assert.Equal(new PyInt(587), aliases.Items[2]);
        Assert.IsType<PyBytes>(aliases.Items[3]);
        Assert.Equal("Standard", JsSemantics.StringValue(aliases.Items[3]));

        var plain = await CallFitting(3, new PyInt(7003));
        Assert.Equal(4, plain.Items.Count);
        Assert.IsType<PyBytes>(plain.Items[0]);
        Assert.Equal("Plain fit", JsSemantics.StringValue(plain.Items[0]));
        Assert.IsType<PyBytes>(plain.Items[1]);
        Assert.Equal("res:/UI/Texture/classes/Fitting/tabFittings.png", JsSemantics.StringValue(plain.Items[1]));
        Assert.Equal(new PyInt(587), plain.Items[2]);
        Assert.Same(PyNone.Instance, plain.Items[3]);

        var missing = await CallFitting(4, new PyInt(7999));
        Assert.Equal(4, missing.Items.Count);
        Assert.IsType<PyBytes>(missing.Items[0]);
        Assert.Equal("Fitting 7999", JsSemantics.StringValue(missing.Items[0]));
        Assert.IsType<PyBytes>(missing.Items[1]);
        Assert.Equal("res:/UI/Texture/classes/Fitting/tabFittings.png", JsSemantics.StringValue(missing.Items[1]));
        Assert.Equal(new PyInt(0), missing.Items[2]);
        Assert.Same(PyNone.Instance, missing.Items[3]);

        var none = await CallFitting(5, PyNone.Instance);
        Assert.IsType<PyBytes>(none.Items[0]);
        Assert.Equal("Fitting 0", JsSemantics.StringValue(none.Items[0]));
        Assert.Equal(new PyInt(0), none.Items[2]);
        Assert.Same(PyNone.Instance, none.Items[3]);

        var bindArgs = Py.Tuple(
            Py.Tuple(new PyInt(600), new PyInt(15)),
            Py.Tuple(new PyStr("GetDirtTimestamp"), Py.Tuple(new PyInt(9001)), kwargs));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 6, "ship", "MachoBindObject", bindArgs, kwargs))));
        var bound = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(FixedNowMs)), bound.Items[1]);
    }
}
