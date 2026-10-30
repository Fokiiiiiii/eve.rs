using Eve.Characters;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Character_creation_info_and_demographic_updates_round_trip_over_registered_host_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var account = await rig.CreateAccountAsync();
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        ClientSession? session = null;

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: opened =>
            {
                opened.UserId = account;
                session = opened;
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var machoVersion = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        async Task<PyValue> CallAsync(long callId, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "charUnboundMgr", method, new PyTuple(args), machoVersion))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(decoded)!;
        }

        var firstName = "Wire Pilot Demographics";
        var doll = Py.KeyVal(("appearance", new PyStr("doll-data")));
        var portrait = Py.KeyVal(("snapshot", new PyStr("portrait-data")));
        var characterId = Assert.IsType<PyInt>(await CallAsync(
            1, "CreateCharacterWithDoll",
            new PyStr(firstName), new PyInt(1), new PyInt(1), new PyInt(1), new PyInt(1),
            doll, portrait,
            new PyInt(35), PyNone.Instance, PyNone.Instance)).Value;
        var (storedDoll, storedPortrait) = await rig.Characters.AppearanceAsync(characterId);
        var savedDoll = Assert.IsType<PyObject>(MarshalCodec.Decode(Assert.IsType<byte[]>(storedDoll)));
        var savedPortrait = Assert.IsType<PyObject>(MarshalCodec.Decode(Assert.IsType<byte[]>(storedPortrait)));
        Assert.Equal("doll-data", System.Text.Encoding.UTF8.GetString(
            Assert.IsType<PyBytes>(Field(savedDoll, "appearance")).Value.Span));
        Assert.Equal("portrait-data", System.Text.Encoding.UTF8.GetString(
            Assert.IsType<PyBytes>(Field(savedPortrait, "snapshot")).Value.Span));

        var characters = Assert.IsType<PyList>(await CallAsync(2, "GetCharacterInfo"));
        var row = Assert.Single(characters.Items);
        Assert.Equal(new PyInt(characterId), Field(row, "characterID"));
        var characterName = Assert.IsType<PyBytes>(Field(row, "characterName"));
        Assert.Equal(firstName, System.Text.Encoding.UTF8.GetString(characterName.Value.Span));

        session!.CharacterId = characterId;
        session.Set("charid", new PyInt(characterId));
        var catalog = await Eve.Characters.CreationCatalog.LoadAsync(new SdeReader(rig.Source));
        var bloodline = Assert.IsType<Bloodline>(catalog.Bloodline(2));
        Assert.Same(PyNone.Instance, await CallAsync(
            3, "UpdateCharacterBloodline", new PyInt(characterId), new PyInt(bloodline.Id)));
        var updated = Assert.IsType<Character>(await rig.Characters.GetAsync(characterId));
        Assert.Equal(bloodline.Id, updated.BloodlineId);
        Assert.Equal(bloodline.RaceId, updated.RaceId);
        Assert.Equal(new PyInt(bloodline.Id), session.Get("bloodlineID"));
        Assert.Equal(new PyInt(bloodline.RaceId), session.Get("raceID"));

        Assert.Same(PyNone.Instance, await CallAsync(
            4, "UpdateCharacterGender", new PyInt(characterId), new PyInt(0)));
        updated = Assert.IsType<Character>(await rig.Characters.GetAsync(characterId));
        Assert.Equal(0, updated.Gender);
        Assert.Equal(new PyInt(0), session.Get("genderID"));

        var selection = Assert.IsType<PyTuple>(await CallAsync(5, "GetCharacterSelectionData"));
        var selectionRows = Assert.IsType<PyList>(selection.Items[2]);
        var updatedRow = Assert.Single(selectionRows.Items,
            value => Field(value, "characterID").Equals(new PyInt(characterId)));
        Assert.Equal(new PyInt(bloodline.Id), Field(updatedRow, "bloodlineID"));
        Assert.Equal(new PyInt(bloodline.RaceId), Field(updatedRow, "raceID"));
        Assert.Equal(new PyInt(0), Field(updatedRow, "gender"));

        await rig.RestartAsync();
        updated = Assert.IsType<Character>(await new PgCharacterStore(rig.Source, rig.CharactersSchema).GetAsync(characterId));
        Assert.Equal(bloodline.Id, updated.BloodlineId);
        Assert.Equal(0, updated.Gender);
    }

}
