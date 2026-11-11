using System.Numerics;
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
    public async Task Char_mgr_get_public_info3_returns_the_client_crowset_shape_over_the_host_wire()
    {
        const long characterId = 900003;
        var character = new Character(
            characterId, 1, "Row Pilot", 1, 1, 1, 10, 35, 1373, 1000006, 500001, null,
            60015249, 60015249, 30100032, 20010001, 10001004, null,
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), null, null);
        const long corporationStartMs = 1_770_000_000_000;
        var service = new CharMgrService(
            null!,
            null!,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>("Description"),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult(0d),
            readCorporationMemberStartedMs: (_, _) => ValueTask.FromResult<long?>(corporationStartMs),
            readCharacterById: (id, _) => ValueTask.FromResult(id == characterId ? character : null));

        Start(configure: registry => registry.Register(service));

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "charMgr", "GetPublicInfo3", Py.Tuple(new PyLong(characterId)), ClientKwargs()))));
        var response = Assert.IsType<PyObject>(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        var responseFields = Assert.IsType<PyTuple>(response.Args).Items;
        var responsePayload = Assert.IsType<PyTuple>(responseFields[4]);
        var rowsetStream = Assert.IsType<PySubStream>(Assert.Single(responsePayload.Items));
        var rowset = Assert.IsType<PyObjectEx>(rowsetStream.Decoded);
        Assert.Equal(
            Convert.FromHexString("7e01000000232c250228636172626f6e2e636f6d6d6f6e2e7363726970742e7379732e63726f777365742e43526f777365741601622c0214626c75652e4442526f7744657363726970746f722514072c117606062c13057469746c650582002c130d73746172744461746554696d6506402c11180582002c111506032c13106d696c6974696146616374696f6e494406032c110806112d2d11242a1b010f8f60c0c439ed93dc0146420f6c01200e130b4465736372697074696f6e2d2d01000000"),
            rowsetStream.Raw.ToArray());
        Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
        var constructor = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(rowset.Header).Items[0]);
        Assert.Equal(new PyToken("carbon.common.script.sys.crowset.CRowset"), Assert.Single(constructor.Items));
        var packedRow = Assert.IsType<PyPackedRow>(Assert.Single(rowset.List));
        Assert.Equal(
            ["bounty", "title", "startDateTime", "description", "corporationID", "militiaFactionID", "bloodlineID"],
            packedRow.Columns.Select(column => column.Name));
        Assert.Equal([6L, 130L, 64L, 130L, 3L, 3L, 17L], packedRow.Columns.Select(column => column.DbType));
        Assert.Equal(new PyReal(0), packedRow.Values[0]);
        Assert.Equal(new PyBytes(ReadOnlyMemory<byte>.Empty), packedRow.Values[1]);
        Assert.Equal(PackedFileTime(Py.FileTimeFromUnixMs(corporationStartMs)), packedRow.Values[2]);
        Assert.Equal(new PyBytes(System.Text.Encoding.UTF8.GetBytes("Description")), packedRow.Values[3]);
        Assert.Equal(new PyInt(character.CorporationId), packedRow.Values[4]);
        Assert.Same(PyNone.Instance, packedRow.Values[5]);
        Assert.Equal(new PyInt(character.BloodlineId), packedRow.Values[6]);
    }

    [Fact]
    public async Task Char_mgr_public_info_marshals_non_ascii_text_as_unicode_over_the_host_wire()
    {
        const long characterId = 900004;
        var character = new Character(
            characterId, 1, "Åri 星", 1, 1, 1, 10, 35, 1373, 1000006, 500001, null,
            60015249, 60015249, 30100032, 20010001, 10001004, null,
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), null, null);
        var service = new CharMgrService(
            null!,
            null!,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>("船の説明"),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult(0d),
            readCharacterById: (id, _) => ValueTask.FromResult(id == characterId ? character : null));

        Start(configure: registry => registry.Register(service));

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var info = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 1, "charMgr", "GetPublicInfo", Py.Tuple(new PyLong(characterId))));
        var fields = Assert.IsType<PyDict>(info.Args).Entries;
        PyValue Field(string name) => fields.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

        Assert.Equal(new PyWString(character.Name), Field("characterName"));
        Assert.Equal(new PyWString("船の説明"), Field("description"));
    }

    [Fact]
    public async Task Char_mgr_public_info_uses_the_reference_field_order_and_scalar_types_over_the_host_wire()
    {
        const long characterId = 3_000_000_005L;
        var character = new Character(
            characterId, 1, "Wíre 星", 1, 2, 3, 4, 35, 1376, 1000006, 500001, null,
            60015249, 60015249, 30100032, 20010001, 10001004, null,
            new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero), null, null);
        const long corporationStartMs = 1_770_000_000_000;
        var service = new CharMgrService(
            null!,
            null!,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>("Pílot description"),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult(0d),
            readCorporationMemberStartedMs: (_, _) => ValueTask.FromResult<long?>(corporationStartMs),
            readCharacterById: (id, _) => ValueTask.FromResult(id == characterId ? character : null),
            readMilitiaFactionId: (_, _) => ValueTask.FromResult<long?>(500004));

        Start(configure: registry => registry.Register(service));

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var info = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 1, "charMgr", "GetPublicInfo", Py.Tuple(new PyLong(characterId))));
        var entries = Assert.IsType<PyDict>(info.Args).Entries;
        Assert.Equal(
            ["characterID", "characterName", "typeID", "raceID", "bloodlineID", "ancestryID", "schoolID", "careerID",
             "careerSpecialityID", "gender", "createDateTime", "corporationID", "corporationDateTime", "allianceID",
             "warFactionID", "age", "description"],
            entries.Select(entry => JsSemantics.StringValue(entry.Key)));
        var fields = entries.ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyLong(character.Id), fields["characterID"]);
        Assert.Equal(new PyWString(character.Name), fields["characterName"]);
        Assert.Equal(new PyInt(character.TypeId), fields["typeID"]);
        Assert.Equal(new PyInt(character.RaceId), fields["raceID"]);
        Assert.Equal(new PyInt(character.BloodlineId), fields["bloodlineID"]);
        Assert.Equal(new PyInt(character.AncestryId), fields["ancestryID"]);
        Assert.Equal(new PyInt(character.SchoolId), fields["schoolID"]);
        Assert.Same(PyNone.Instance, fields["careerID"]);
        Assert.Same(PyNone.Instance, fields["careerSpecialityID"]);
        Assert.Same(PyBool.True, fields["gender"]);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(character.CreatedAt.ToUnixTimeMilliseconds())), fields["createDateTime"]);
        Assert.Equal(new PyInt(character.CorporationId), fields["corporationID"]);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(corporationStartMs)), fields["corporationDateTime"]);
        Assert.Same(PyNone.Instance, fields["allianceID"]);
        Assert.Equal(new PyInt(500004), fields["warFactionID"]);
        Assert.Equal(new PyInt(0), fields["age"]);
        Assert.Equal(new PyWString("Pílot description"), fields["description"]);
    }

    [Fact]
    public async Task Chat_user_data_uses_character_identity_and_current_militia_projection()
    {
        var character = new Character(
            900001, 1, "Pilot & One", 1, 1, 1, 10, 35, 1376, 1000006, 500003, null,
            60015249, 60015249, 30100032, 20010001, 10001004, null,
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), null, null)
        {
            DirectFactionId = 500003,
        };
        var service = new CharMgrService(
            null!,
            null!,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>(null),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult(0d),
            readOrganizationInfo: (_, _) => ValueTask.FromResult<IReadOnlyDictionary<long, CharMgrOrganizationInfo>>(
                new Dictionary<long, CharMgrOrganizationInfo>
                {
                    [character.Id] = new CharMgrOrganizationInfo(1000006, 9900001, 500001),
                }),
            readCharacterById: (id, _) => ValueTask.FromResult(id == character.Id ? character : null));

        var data = await service.ReadChatUserDataAsync(character.Id);
        Assert.Equal(new CharMgrChatUserData(character.Id, character.Name, character.TypeId, true, 1000006, 9900001, 500001), data);
        Assert.Null(await service.ReadChatUserDataAsync(character.Id + 1));

        var withoutMilitiaProjection = new CharMgrService(
            null!,
            null!,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>(null),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult(0d),
            readCharacterById: (id, _) => ValueTask.FromResult(id == character.Id ? character : null));
        var fallback = await withoutMilitiaProjection.ReadChatUserDataAsync(character.Id);
        Assert.Equal(500003, fallback?.WarFactionId);
    }

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
            ["characterID", "characterName", "typeID", "raceID", "bloodlineID", "ancestryID", "schoolID", "careerID", "careerSpecialityID",
             "gender", "createDateTime", "corporationID", "corporationDateTime", "allianceID", "warFactionID", "age", "description"],
            firstFields.Keys);
        Assert.Equal(new PyInt(first.Id), firstFields["characterID"]);
        Assert.Equal(first.Name, JsSemantics.StringValue(firstFields["characterName"]));
        Assert.Equal(new PyInt(first.BloodlineId), firstFields["bloodlineID"]);
        Assert.Equal(new PyInt(first.SchoolId), firstFields["schoolID"]);
        Assert.Same(PyBool.True, firstFields["gender"]);
        Assert.Equal(new PyInt(first.CorporationId), firstFields["corporationID"]);
        Assert.Same(PyNone.Instance, firstFields["careerID"]);
        Assert.Same(PyNone.Instance, firstFields["careerSpecialityID"]);
        Assert.IsType<PyLong>(firstFields["createDateTime"]);
        Assert.Equal(firstFields["createDateTime"], firstFields["corporationDateTime"]);
        Assert.Same(PyNone.Instance, firstFields["allianceID"]);
        Assert.Same(PyNone.Instance, firstFields["warFactionID"]);
        Assert.Equal(new PyInt(0), firstFields["age"]);
        Assert.Equal($"description-{first.Id}", JsSemantics.StringValue(firstFields["description"]));

        var secondFields = Fields(await Call(2, second.Id));
        Assert.Equal(new PyInt(second.Id), secondFields["characterID"]);
        Assert.Equal(second.Name, JsSemantics.StringValue(secondFields["characterName"]));
        Assert.Equal(new PyInt(second.BloodlineId), secondFields["bloodlineID"]);
        Assert.Equal(new PyInt(second.SchoolId), secondFields["schoolID"]);
        Assert.Same(PyBool.False, secondFields["gender"]);
        Assert.Equal(new PyInt(second.CorporationId), secondFields["corporationID"]);
        Assert.Equal(new PyInt(second.AllianceId!.Value), secondFields["allianceID"]);
        Assert.IsType<PyLong>(secondFields["corporationDateTime"]);
        Assert.Same(PyNone.Instance, secondFields["warFactionID"]);
        var publicInfo3 = Assert.IsType<PyObjectEx>(await Call(3, first.Id, "GetPublicInfo3"));
        var publicInfo3Row = Assert.IsType<PyPackedRow>(Assert.Single(publicInfo3.List));
        Assert.Equal(PackedFileTime(Py.FileTimeFromUnixMs(first.CreatedAt.ToUnixTimeMilliseconds())), publicInfo3Row.Values[2]);
        Assert.Equal(new PyInt(first.CorporationId), publicInfo3Row.Values[4]);
        Assert.Equal(new PyInt(first.BloodlineId), publicInfo3Row.Values[6]);
        Assert.Same(PyNone.Instance, await Call(4, 999999));
        Assert.Equal([first.Id, second.Id, first.Id, 999999], reads);
    }

    private static PyBigUnsigned PackedFileTime(long value) =>
        new(new BigInteger(value).ToByteArray(isUnsigned: true, isBigEndian: false));
}
