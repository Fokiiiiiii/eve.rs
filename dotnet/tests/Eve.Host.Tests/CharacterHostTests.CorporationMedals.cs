using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

public sealed partial class CharacterHostTests
{
    private static long _nextCorporationMedalCallId = 90_000;

    [PgFact]
    public async Task Corporation_medal_privacy_is_owned_by_each_recipient_through_registered_host_wire()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var chief = await rig.CreateCharacterAsync();
        var firstRecipient = await rig.CreateCharacterAsync();
        var secondRecipient = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(chief);
        await rig.AddCorporationMemberAsync(firstRecipient);
        await rig.AddCorporationMemberAsync(secondRecipient);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.corporations SET ceo_id = $1, creator_id = $1 WHERE id = 900001001", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = chief });
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var chiefSink = new RecordingPacketSink();
        var firstSink = new RecordingPacketSink();
        var secondSink = new RecordingPacketSink();
        var chiefSession = MedalSession(81_001, chief, CharacterHostRigCorporation, chiefSink);
        var firstSession = MedalSession(81_002, firstRecipient, CharacterHostRigCorporation, firstSink);
        var secondSession = MedalSession(81_003, secondRecipient, CharacterHostRigCorporation, secondSink);
        rig.Sessions.Enter(chief, chiefSession);
        rig.Sessions.Enter(firstRecipient, firstSession);
        rig.Sessions.Enter(secondRecipient, secondSession);
        var dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);

        var created = await DispatchMedalAsync(dispatcher, chiefSession, "corporationSvc", "CreateMedal",
            new PyWString("Service medal"), new PyWString("Visibility regression"),
            new PyList([Py.Tuple(new PyInt(1), new PyWString("medal.host"), new PyInt(7))]));
        var medalId = MedalNumber(MedalResponse(created));
        Assert.True(medalId > 0);
        chiefSink.Sent.Clear();
        firstSink.Sent.Clear();
        secondSink.Sent.Clear();

        var awarded = await DispatchMedalAsync(dispatcher, chiefSession, "corporationSvc", "GiveMedalToCharacters",
            new PyInt(medalId), new PyList([new PyLong(firstRecipient), new PyLong(secondRecipient)]), new PyWString("Award"));
        Assert.Same(PyNone.Instance, MedalResponse(awarded));
        Assert.Equal("OnMedalIssued", MedalNotificationName(Assert.Single(firstSink.Sent)));
        Assert.Equal("OnMedalIssued", MedalNotificationName(Assert.Single(secondSink.Sent)));

        firstSink.Sent.Clear();
        secondSink.Sent.Clear();
        var privacyUpdate = await DispatchMedalAsync(dispatcher, firstSession, "corporationSvc", "SetMedalStatus",
            new PyDict([new(new PyLong(medalId), new PyInt(2))]));
        Assert.Equal(MachoMessage.CallRsp, MedalPacketType(privacyUpdate));
        Assert.Same(PyNone.Instance, MedalResponse(privacyUpdate));
        Assert.DoesNotContain(firstSink.Sent, packet => MedalPacketType(packet) == MachoMessage.Notification);
        Assert.DoesNotContain(secondSink.Sent, packet => MedalPacketType(packet) == MachoMessage.Notification);

        var firstReceived = await DispatchMedalAsync(dispatcher, firstSession, "corporationSvc", "GetMedalsReceived");
        var firstRow = Assert.IsType<PyList>(Assert.Single(MedalReceivedRows(MedalResponse(firstReceived))));
        Assert.Equal(new PyInt(2), firstRow.Items[3]);
        var mixedUpdate = await DispatchMedalAsync(dispatcher, firstSession, "corporationSvc", "SetMedalStatus",
            new PyDict([
                new(new PyLong(medalId), new PyInt(3)),
                new(new PyLong(long.MaxValue), new PyInt(2)),
            ]));
        Assert.Equal(MachoMessage.ErrorResponse, MedalPacketType(mixedUpdate));
        var stillPrivate = await DispatchMedalAsync(dispatcher, firstSession, "corporationSvc", "GetMedalsReceived");
        Assert.Equal(new PyInt(2), Assert.IsType<PyList>(Assert.Single(MedalReceivedRows(MedalResponse(stillPrivate)))).Items[3]);
        var secondReceived = await DispatchMedalAsync(dispatcher, secondSession, "corporationSvc", "GetMedalsReceived");
        var secondRow = Assert.IsType<PyList>(Assert.Single(MedalReceivedRows(MedalResponse(secondReceived))));
        Assert.Equal(new PyInt(3), secondRow.Items[3]);

        var publicRecipients = await DispatchMedalAsync(dispatcher, chiefSession, "corporationSvc", "GetRecipientsOfMedal",
            new PyLong(medalId));
        var recipientRows = MedalRows(MedalResponse(publicRecipients));
        var publicRecipient = Assert.IsType<PyList>(Assert.Single(recipientRows));
        Assert.Equal(new PyInt(secondRecipient), publicRecipient.Items[1]);

        var privateCrossRead = await DispatchMedalAsync(dispatcher, chiefSession, "corporationSvc", "GetMedalsReceived",
            new PyLong(firstRecipient));
        Assert.Empty(MedalReceivedRows(MedalResponse(privateCrossRead)));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"SELECT status FROM {rig.CorporationsSchema}.medal_awards WHERE medal_id = $1 AND character_id = $2", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = medalId });
            command.Parameters.Add(new NpgsqlParameter { Value = firstRecipient });
            Assert.Equal(2, (int)(await command.ExecuteScalarAsync())!);
        }

        await rig.RestartAsync();
        rig.Sessions.Enter(chief, chiefSession);
        rig.Sessions.Enter(firstRecipient, firstSession);
        rig.Sessions.Enter(secondRecipient, secondSession);
        dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);
        var afterRestart = await DispatchMedalAsync(dispatcher, firstSession, "corporationSvc", "GetMedalsReceived");
        Assert.Equal(new PyInt(2), Assert.IsType<PyList>(Assert.Single(MedalReceivedRows(MedalResponse(afterRestart)))).Items[3]);
    }

    private static ClientSession MedalSession(long sessionId, long characterId, long corporationId, RecordingPacketSink sink)
    {
        var session = new ClientSession(sink)
        {
            SessionId = sessionId,
            ClientId = sessionId,
            CharacterId = characterId,
        };
        session.Set("charid", new PyLong(characterId));
        session.Set("corpid", new PyLong(corporationId));
        session.Set("corporationID", new PyLong(corporationId));
        return session;
    }

    private static async Task<PyValue> DispatchMedalAsync(PacketDispatcher dispatcher, ClientSession session,
        string service, string method, params PyValue[] args)
    {
        var request = MedalServiceRequest(session, service, method, args);
        var sink = Assert.IsType<RecordingPacketSink>(session.Sink);
        var before = sink.Sent.Count;
        Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
        return sink.Sent.Skip(before).Last(packet =>
            MedalPacketType(packet) is MachoMessage.CallRsp or MachoMessage.ErrorResponse);
    }

    private static PyValue MedalServiceRequest(ClientSession session, string service, string method, PyValue[] args)
    {
        var callId = Interlocked.Increment(ref _nextCorporationMedalCallId);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyLong(callId), ClientId: new PyInt(session.ClientId));
        var destination = MachoAddress.Any(service).Encode();
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(args), new PyDict([])]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), destination, new PyInt(7), payload, new PyDict([]),
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]));
    }

    private static PyValue MedalResponse(PyValue packet)
    {
        Assert.Equal(MachoMessage.CallRsp, MedalPacketType(packet));
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(fields[4]).Items[0]).Decoded!;
    }

    private static long MedalPacketType(PyValue packet) =>
        Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items[0]).Value;

    private static long MedalNumber(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected a Python integer, got {value.GetType().Name}.")
    };

    private static IReadOnlyList<PyValue> MedalRows(PyValue rowset) =>
        Assert.IsType<PyList>(Assert.IsType<PyDict>(Assert.IsType<PyObject>(rowset).Args).Entries
            .Single(entry => entry.Key is PyStr { Value: "lines" }).Value).Items;

    private static IReadOnlyList<PyValue> MedalReceivedRows(PyValue value) =>
        MedalRows(Assert.IsType<PyTuple>(value).Items[0]);

    private static string MedalNotificationName(PyValue packet)
    {
        Assert.Equal(MachoMessage.Notification, MedalPacketType(packet));
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(fields[2]).Args).Items;
        return Assert.IsType<PyStr>(address[1]).Value;
    }
}
