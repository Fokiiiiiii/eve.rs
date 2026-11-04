using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Char_mgr_recent_kills_returns_client_filter_fields_over_the_registered_host_route()
    {
        const long characterId = 900201;
        var character = new Character(
            characterId, 1, "Killmail Pilot", 1, 2, 3, 4, 5, 1373, 1000006, 500001, null,
            60015249, 60015249, 30100032, 20010001, 10001004, null,
            new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero), null, null);
        var killmail = Py.KeyVal(
            ("killID", new PyLong(123456)),
            ("finalCharacterID", new PyLong(characterId)),
            ("victimCharacterID", new PyLong(900202)));
        (long CharacterId, int PageSize, int StartIndex)? requestedPage = null;
        var service = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (id, pageSize, startIndex, _) =>
            {
                requestedPage = (id, pageSize, startIndex);
                return ValueTask.FromResult<IReadOnlyList<PyValue>>([killmail]);
            },
            readCharacterDescription: (_, _) => ValueTask.FromResult<string?>(null),
            writeCharacterDescription: (_, _, _) => ValueTask.CompletedTask,
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null),
            readCharacterById: (id, _) =>
                ValueTask.FromResult<Character?>(id == characterId ? character : null));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);
        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var rows = Assert.IsType<PyList>(await CallOverHost(
            front, open, 1, "charMgr", "GetRecentShipKillsAndLosses",
            Py.Tuple(new PyInt(40), new PyInt(12))));
        var row = Assert.IsType<PyObject>(Assert.Single(rows.Items));
        Assert.Equal(new PyLong(characterId), RpcCoverageWireField(row, "finalCharacterID"));
        Assert.Equal(new PyLong(900202), RpcCoverageWireField(row, "victimCharacterID"));
        Assert.Equal((characterId, 25, 12), requestedPage);
    }
}
