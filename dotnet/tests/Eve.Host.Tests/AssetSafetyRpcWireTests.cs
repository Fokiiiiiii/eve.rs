using System.Text;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Asset_safety_client_rpcs_reach_the_real_host_and_persist_wire_visible_state()
    {
        await using var rig = await AssetSafetyPgRig.CreateAsync();
        const long characterId = 910002;
        const long corporationId = 980001;
        await rig.CreateCorporationAsync(corporationId, 910001, characterId, 910003);
        var structureId = rig.AddStructure(rig.SameSystemStation.SolarSystemId, "Asset safety wire source");
        var personalItemId = await rig.AddAssetAsync(characterId, structureId, 1);
        var corporationItemId = await rig.AddAssetAsync(corporationId, structureId, 2);
        await rig.OpenWalletAsync(characterId, 1_000_000_000m);
        await rig.OpenWalletAsync(corporationId, 1_000_000_000m);

        Start(
            configure: registry => registry.Register(new StructureAssetSafetyService(rig.Store)),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("corpid", new PyLong(corporationId));
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var transport = Py.Dict(("machoVersion", new PyInt(1)));
        async Task<Send> SendCall(long id, string method, PyValue args, PyValue kwargs)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, "structureAssetSafety", method, args, kwargs))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }
        async Task<PyValue> Call(long id, string method, params PyValue[] args)
        {
            var reply = await SendCall(id, method, new PyTuple(args), transport);
            return Result(MarshalCodec.Decode(reply.Payload))
                ?? throw new InvalidOperationException("The Host returned no RPC value.");
        }

        Assert.Same(PyNone.Instance, await Call(1, "MovePersonalAssetsToSafety",
            new PyLong(rig.SameSystemStation.SolarSystemId), new PyLong(structureId)));
        Assert.Same(PyNone.Instance, await Call(2, "MoveCorpAssetsToSafety",
            new PyLong(rig.SameSystemStation.SolarSystemId), new PyLong(structureId)));

        var personalWrapId = await rig.WrapIdAsync(characterId, 1, structureId);
        var corporationWrapId = await rig.WrapIdAsync(corporationId, 2, structureId);
        var names = Assert.IsType<PyDict>(await Call(3, "GetWrapNames",
            new PyList([new PyLong(personalWrapId), new PyLong(corporationWrapId)])));
        Assert.Equal(2, names.Entries.Count);

        var targets = Assert.IsType<PyTuple>(await Call(4, "GetStructuresICanDeliverTo",
            new PyLong(rig.SameSystemStation.SolarSystemId)));
        Assert.Equal(2, targets.Items.Count);
        Assert.IsType<PyList>(targets.Items[0]);
        Assert.IsType<PyObject>(targets.Items[1]);

        Assert.Same(PyNone.Instance, await Call(5, "MoveEjectTimeGM",
            new PyLong(personalWrapId), new PyInt(5)));
        var earlyDelivery = await SendCall(6, "MoveSafetyWrapToStructure",
            new PyTuple([new PyLong(personalWrapId), new PyLong(rig.SameSystemStation.SolarSystemId)]),
            Py.Dict(("destinationID", new PyLong(rig.SameSystemStation.Id)), ("machoVersion", new PyInt(1))));
        var earlyError = Encoding.ASCII.GetString(earlyDelivery.Payload.Span);
        Assert.Equal(15, ((PyInt)((PyTuple)((PyObject)MarshalCodec.Decode(earlyDelivery.Payload)).Args).Items[0]).Value);
        Assert.Contains("eveexceptions.UserError", earlyError);

        rig.Now += 10 * AssetSafetyPgRig.DayMs + 1;
        var delivered = await SendCall(7, "MoveSafetyWrapToStructure",
            new PyTuple([new PyLong(personalWrapId), new PyLong(rig.SameSystemStation.SolarSystemId)]),
            Py.Dict(("destinationID", new PyLong(rig.SameSystemStation.Id)), ("machoVersion", new PyInt(1))));
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(delivered.Payload)));
        Assert.Equal(1, await rig.ItemLocationCountAsync(personalItemId, rig.SameSystemStation.Id, characterId));
        Assert.Equal(1, await rig.ItemLocationCountAsync(corporationItemId, corporationWrapId, corporationId));
    }
}
