using System.Text;
using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Character_filter_setting_rejects_non_scalar_key_with_client_user_error_over_host_wire()
    {
        const long characterId = 90_000_202;
        var character = new Character(
            characterId, 1, "Filter Pilot", 1, 1, 1, 1, 35, 1373, 1_000_001,
            null, null, 60_000_001, 60_000_001, 30_000_142, 20_000_020, 10_000_002, null,
            DateTimeOffset.UnixEpoch, null, null);
        var service = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            readCharacterDescription: (_, _) => ValueTask.FromResult<string?>(null),
            writeCharacterDescription: (_, _, _) => ValueTask.CompletedTask,
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null),
            readPersonalData: (_, _) => ValueTask.FromResult<CharMgrPersonalData?>(CharMgrPersonalData.Empty),
            updatePersonalData: (_, update, _) =>
                ValueTask.FromResult<CharMgrPersonalData?>(update(CharMgrPersonalData.Empty)),
            readCharacterById: (id, _) =>
                ValueTask.FromResult<Character?>(id == characterId ? character : null));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);
        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var request = Packets.CallWithKwargs(
            open,
            1,
            "charMgr",
            "SaveCharacterSetting",
            Py.Tuple(new PyList([]), new PyStr("invalid key")),
            ClientKwargs());
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(reply, "eveexceptions.UserError", "CustomInfo");
        Assert.Contains(
            "Character setting keys must be scalar values.",
            Encoding.ASCII.GetString(reply.Payload.Span));
    }
}
