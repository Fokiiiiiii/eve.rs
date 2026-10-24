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
    public async Task Char_mgr_get_public_info_returns_client_keyval_through_host_with_macho_version()
    {
        var first = new Character(
            900001, 1, "Ari K", 1, 1, 1, 10, 35, 1373, 1000006, 500001, null,
            60015249, 60015249, 30100032, 20010001, 10001004, null,
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), null, null);
        var second = new Character(
            900002, 2, "Bex R", 0, 4, 5, 11, 88, 1373, 1000094, 500004, 9900001,
            60000610, 60000610, 30002775, 20000407, 10000033, null,
            new DateTimeOffset(2022, 6, 7, 8, 9, 10, TimeSpan.Zero), null, null);
        var characters = new Dictionary<long, Character> { [first.Id] = first, [second.Id] = second };
        var reads = new List<long>();
        var service = new CharMgrService(
            null!,
            null!,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            (id, _) => ValueTask.FromResult<string?>($"description-{id}"),
            (_, _, _) => ValueTask.CompletedTask,
            (id, _) => ValueTask.FromResult(id == first.Id ? 1.25 : -2.5),
            (id, _) => ValueTask.FromResult<long?>(id == first.Id ? 707 : null),
            readCharacterById: (id, _) =>
            {
                reads.Add(id);
                return ValueTask.FromResult(characters.GetValueOrDefault(id));
            });

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyLong(first.Id)));

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long callId, long characterId, string method = "GetPublicInfo")
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "charMgr", method, new PyTuple([new PyLong(characterId)]), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        static Dictionary<string, PyValue> Fields(PyValue keyVal) =>
            Assert.IsType<PyDict>(Assert.IsType<PyObject>(keyVal).Args).Entries
                .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);

        var firstReply = await Call(1, first.Id);
        var firstFields = Fields(firstReply);
        Assert.Equal(
            ["characterID", "characterName", "typeID", "raceID", "bloodlineID", "ancestryID", "corporationID", "allianceID", "factionID", "empireID", "schoolID",
             "gender", "createDateTime", "startDateTime", "description", "securityRating", "securityStatus", "bounty", "title", "shortName", "stationID",
             "solarSystemID", "militiaFactionID", "medal1GraphicID"],
            firstFields.Keys);
        Assert.Equal(new PyInt(first.Id), firstFields["characterID"]);
        Assert.Equal(first.Name, JsSemantics.StringValue(firstFields["characterName"]));
        Assert.Equal(new PyInt(first.BloodlineId), firstFields["bloodlineID"]);
        Assert.Equal(new PyInt(first.SchoolId), firstFields["schoolID"]);
        Assert.Equal(new PyInt(first.Gender), firstFields["gender"]);
        Assert.Equal(new PyInt(first.CorporationId), firstFields["corporationID"]);
        Assert.Equal(new PyInt(500004), firstFields["empireID"]);
        Assert.Equal($"description-{first.Id}", JsSemantics.StringValue(firstFields["description"]));
        Assert.Equal(Py.Number(1.25), firstFields["securityRating"]);
        Assert.Equal(Py.Number(1.25), firstFields["securityStatus"]);
        Assert.Equal(new PyInt(707), firstFields["medal1GraphicID"]);

        var secondFields = Fields(await Call(2, second.Id));
        Assert.Equal(new PyInt(second.Id), secondFields["characterID"]);
        Assert.Equal(second.Name, JsSemantics.StringValue(secondFields["characterName"]));
        Assert.Equal(new PyInt(second.BloodlineId), secondFields["bloodlineID"]);
        Assert.Equal(new PyInt(second.SchoolId), secondFields["schoolID"]);
        Assert.Equal(new PyInt(second.Gender), secondFields["gender"]);
        Assert.Equal(new PyInt(second.CorporationId), secondFields["corporationID"]);
        Assert.Equal(new PyInt(second.AllianceId!.Value), secondFields["allianceID"]);
        Assert.Equal(new PyInt(500004), secondFields["empireID"]);
        Assert.Equal(PyNone.Instance, secondFields["medal1GraphicID"]);
        var publicInfo3 = Assert.IsType<PyList>(await Call(3, first.Id, "GetPublicInfo3"));
        Assert.Equal(new PyInt(first.Id), Fields(Assert.Single(publicInfo3.Items))["characterID"]);
        Assert.Same(PyNone.Instance, await Call(4, 999999));
        Assert.Equal([first.Id, second.Id, first.Id, 999999], reads);
    }
}
