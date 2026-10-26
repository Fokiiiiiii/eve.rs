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
    public async Task Char_mgr_client_read_shapes_match_the_registered_host_wire_contract()
    {
        const long characterId = 900101;
        var character = new Character(
            characterId, 1, "Wire Pilot", 1, 2, 3, 4, 5, 1373, 1000006, 500001, null,
            60015249, 60015249, 30100032, 20010001, 10001004, null,
            new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero), null, null);
        var personalData = new CharMgrPersonalData(
            [], [], new Dictionary<long, CharMgrContactLabel>(),
            [new CharMgrOwnerNote(7001, "N:Wire note", " note body ")],
            new Dictionary<long, string>());
        var service = new CharMgrService(
            null!,
            null!,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>(null),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult(2.75),
            (_, _) => ValueTask.FromResult<long?>(null),
            readPersonalData: (_, _) => ValueTask.FromResult<CharMgrPersonalData?>(personalData),
            readRecustomizationState: (_, _) => ValueTask.FromResult<int?>(1),
            readBalance: (_, _) => ValueTask.FromResult<decimal?>(1234.5m),
            readCharacterById: (_, _) => ValueTask.FromResult<Character?>(character));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("corporationID", new PyInt(character.CorporationId));
                session.Set("rolesAtAll", new PyInt((long)Eve.Corporations.CorporationRoles.Director));
            });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var labels = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 1, "charMgr", "GetOwnerNoteLabels", Py.Tuple([])));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", JsSemantics.StringValue(labels.Name));
        var labelFields = Assert.IsType<PyDict>(labels.Args);
        Assert.Equal(["noteID", "label"], Assert.IsType<PyList>(ClientReadField(labelFields, "header")).Items.Select(JsSemantics.StringValue));
        Assert.Equal(new PyToken("util.Row"), ClientReadField(labelFields, "RowClass"));
        var noteLabelRow = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(ClientReadField(labelFields, "lines")).Items));
        Assert.Equal(new PyInt(7001), noteLabelRow.Items[0]);
        Assert.Equal("N:Wire note", JsSemantics.StringValue(noteLabelRow.Items[1]));

        Assert.Equal(new PyInt(1), await CallOverHost(
            front, open, 2, "charMgr", "GetPaperdollState", Py.Tuple([])));

        var privateInfo = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 3, "charMgr", "GetPrivateInfo", Py.Tuple(new PyInt(characterId))));
        Assert.Equal("util.Row", JsSemantics.StringValue(privateInfo.Name));
        var privateFields = Assert.IsType<PyDict>(privateInfo.Args);
        Assert.Equal(
            ["characterID", "gender", "createDateTime", "raceID", "bloodlineID", "ancestryID", "balance", "securityRating"],
            Assert.IsType<PyList>(ClientReadField(privateFields, "header")).Items.Select(JsSemantics.StringValue));
        var line = Assert.IsType<PyList>(ClientReadField(privateFields, "line"));
        Assert.Equal(new PyInt(characterId), line.Items[0]);
        Assert.Equal(new PyInt(character.Gender), line.Items[1]);
        Assert.IsType<PyLong>(line.Items[2]);
        Assert.Equal(new PyInt(character.RaceId), line.Items[3]);
        Assert.Equal(new PyInt(character.BloodlineId), line.Items[4]);
        Assert.Equal(new PyInt(character.AncestryId), line.Items[5]);
        Assert.Equal(Py.Number(1234.5), line.Items[6]);
        Assert.Equal(Py.Number(2.75), line.Items[7]);
    }

    private static PyValue ClientReadField(PyDict fields, string name) =>
        fields.Entries.Single(entry => entry.Key == new PyStr(name)).Value;
}
