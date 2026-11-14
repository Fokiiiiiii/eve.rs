using System.IO.Compression;
using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed partial class CharacterHostTests
{
    [PgFact]
    public async Task Mailing_list_join_delivers_welcome_mail_without_duplicate_server_notification_through_host_and_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var owner = await rig.CreateCharacterAsync();
        var recipient = await rig.CreateCharacterAsync();
        var ownerSession = MailSession(owner, 88_001, new RecordingPacketSink());
        var recipientSink = new RecordingPacketSink();
        var recipientSession = MailSession(recipient, 88_002, recipientSink);
        rig.Sessions.Enter(owner, ownerSession);
        rig.Sessions.Enter(recipient, recipientSession);
        var dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);

        var create = await DispatchMailFlowAsync(dispatcher, ownerSession, "mailingListsMgr", "Create",
            new PyWString("Fleet Ops"), new PyInt(1), new PyInt(1), new PyInt(0));
        var listId = Integer(create);
        Assert.True(listId >= 500_000_000);
        Assert.Same(PyNone.Instance, await DispatchMailFlowAsync(dispatcher, ownerSession,
            "mailingListsMgr", "SaveWelcomeMail", new PyLong(listId), new PyWString("Welcome"), new PyWString("Fleet briefing")));

        var joined = await DispatchMailFlowAsync(dispatcher, recipientSession, "mailingListsMgr", "Join", new PyWString("fleet ops"));
        Assert.Equal(listId, Integer(Field(joined, "id")));
        Assert.Collection(Notifications(recipientSink),
            notification => Assert.Equal("OnMailSent", notification.Name));

        var sync = await DispatchMailFlowAsync(dispatcher, recipientSession, "mailMgr", "SyncMail", PyNone.Instance, new PyInt(0));
        var newMail = Assert.IsType<PyList>(Field(sync, "newMail"));
        var header = Assert.IsType<PyObject>(Assert.Single(newMail.Items));
        var messageId = Integer(Field(header, "messageID"));
        var statusRows = Assert.IsType<PyList>(Field(sync, "mailStatus"));
        Assert.Equal(32, Integer(Field(Assert.Single(statusRows.Items), "statusMask")));
        Assert.Equal(0, Integer(Field(Assert.Single(statusRows.Items), "labelMask")));
        Assert.Equal(new PyInt(listId), Field(header, "toListID"));
        Assert.Equal(new PyStr("Welcome"), Field(header, "title"));
        Assert.Single(Assert.IsType<PyList>(Field(sync, "mailStatus")).Items);

        var compressed = Assert.IsType<PyBuffer>(await DispatchMailFlowAsync(dispatcher, recipientSession,
            "mailMgr", "GetBody", new PyLong(messageId), PyBool.False));
        using var input = new MemoryStream(compressed.Value.ToArray());
        using var decompressor = new ZLibStream(input, CompressionMode.Decompress);
        using var body = new MemoryStream();
        await decompressor.CopyToAsync(body);
        Assert.Equal("Fleet briefing", Encoding.UTF8.GetString(body.ToArray()));

        var notificationMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("notificationMgr"));
        Assert.Empty(Assert.IsType<PyList>(await CallAsync(notificationMgr, "GetUnprocessed", recipientSession)).Items);

        var persistedMail = await ReadGameStoreTableAsync(rig, "mail");
        var savedMessage = Assert.IsType<System.Text.Json.Nodes.JsonObject>(persistedMail["messages"]?[messageId.ToString()]);
        Assert.Equal(listId, savedMessage["senderID"]!.GetValue<long>());
        Assert.Equal(listId, savedMessage["toListID"]!.GetValue<long>());
        Assert.Equal("Fleet Ops", savedMessage["senderName"]!.GetValue<string>());
        Assert.Equal("Fleet briefing", savedMessage["body"]!.GetValue<string>());
        Assert.Equal(32, savedMessage["statusMask"]!.GetValue<long>());
        Assert.Equal(32, persistedMail["mailboxes"]![recipient.ToString()]!["statuses"]![messageId.ToString()]!["statusMask"]!.GetValue<long>());
        Assert.Equal(0, persistedMail["mailboxes"]![recipient.ToString()]!["statuses"]![messageId.ToString()]!["labelMask"]!.GetValue<int>());

        await rig.RestartAsync();
        ownerSession = MailSession(owner, 88_001, new RecordingPacketSink());
        recipientSession = MailSession(recipient, 88_002, recipientSink);
        rig.Sessions.Enter(owner, ownerSession);
        rig.Sessions.Enter(recipient, recipientSession);
        dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);
        joined = await DispatchMailFlowAsync(dispatcher, recipientSession, "mailingListsMgr", "Join", new PyWString("fleet ops"));
        Assert.Equal(listId, Integer(Field(joined, "id")));
        sync = await DispatchMailFlowAsync(dispatcher, recipientSession, "mailMgr", "SyncMail", PyNone.Instance, new PyInt(0));
        Assert.Single(Assert.IsType<PyList>(Field(sync, "newMail")).Items);
        Assert.Empty(Assert.IsType<PyList>(await CallAsync(
            Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("notificationMgr")), "GetUnprocessed", recipientSession)).Items);
        Assert.Single((await ReadGameStoreTableAsync(rig, "mail"))["messages"]!.AsObject());

        static ClientSession MailSession(long characterId, long sessionId, RecordingPacketSink sink)
        {
            var session = new ClientSession(sink) { SessionId = sessionId, ClientId = sessionId, CharacterId = characterId, UserId = 1 };
            session.Set("charid", new PyLong(characterId));
            return session;
        }
    }

    [PgFact]
    public async Task Mailing_list_send_welcome_mail_broadcasts_as_an_automated_list_message_through_host_and_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var owner = await rig.CreateCharacterAsync();
        var recipient = await rig.CreateCharacterAsync();
        var ownerSink = new RecordingPacketSink();
        var recipientSink = new RecordingPacketSink();
        var ownerSession = MailSession(owner, 88_011, ownerSink);
        var recipientSession = MailSession(recipient, 88_012, recipientSink);
        rig.Sessions.Enter(owner, ownerSession);
        rig.Sessions.Enter(recipient, recipientSession);
        var dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);

        var listId = Integer(await DispatchMailFlowAsync(dispatcher, ownerSession,
            "mailingListsMgr", "Create", new PyWString("Ops"), new PyInt(1), new PyInt(1), new PyInt(0)));
        await DispatchMailFlowAsync(dispatcher, ownerSession, "mailingListsMgr", "SaveWelcomeMail",
            new PyLong(listId), new PyWString("Welcome"), new PyWString("Fleet briefing"));
        await DispatchMailFlowAsync(dispatcher, recipientSession, "mailingListsMgr", "Join", new PyWString("ops"));

        var response = await DispatchMailFlowAsync(dispatcher, ownerSession, "mailingListsMgr", "SendWelcomeMail",
            new PyLong(listId), new PyWString("Welcome"), new PyWString("Fleet briefing"));
        var manualMessageId = Integer(response);
        Assert.True(manualMessageId > 0);

        var persistedMail = await ReadGameStoreTableAsync(rig, "mail");
        var message = Assert.IsType<System.Text.Json.Nodes.JsonObject>(persistedMail["messages"]?[manualMessageId.ToString()]);
        Assert.Equal(listId, message["senderID"]!.GetValue<long>());
        Assert.Equal("Ops", message["senderName"]!.GetValue<string>());
        Assert.Equal(listId, message["toListID"]!.GetValue<long>());
        Assert.Equal(32, message["statusMask"]!.GetValue<long>());
        Assert.Empty(message["toCharacterIDs"]!.AsArray());
        foreach (var characterId in new[] { owner, recipient })
        {
            Assert.Equal(32, persistedMail["mailboxes"]![characterId.ToString()]!["statuses"]![manualMessageId.ToString()]!["statusMask"]!.GetValue<long>());
            Assert.Equal(0, persistedMail["mailboxes"]![characterId.ToString()]!["statuses"]![manualMessageId.ToString()]!["labelMask"]!.GetValue<int>());
        }

        var ownerSync = await DispatchMailFlowAsync(dispatcher, ownerSession, "mailMgr", "SyncMail", PyNone.Instance, new PyInt(0));
        var ownerStatus = Assert.IsType<PyList>(Field(ownerSync, "mailStatus"));
        Assert.Contains(ownerStatus.Items, row => Integer(Field(row, "messageID")) == manualMessageId &&
            Integer(Field(row, "statusMask")) == 32 && Integer(Field(row, "labelMask")) == 0);
        var ownerMailSent = Assert.Single(Notifications(ownerSink), notification => notification.Name == "OnMailSent");
        Assert.Equal(listId, Integer(ownerMailSent.Args[1]));
        Assert.Equal(32, Integer(ownerMailSent.Args[7]));

        var recipientMailSent = Notifications(recipientSink).Where(notification => notification.Name == "OnMailSent").ToArray();
        Assert.Equal(2, recipientMailSent.Length);
        Assert.Equal(listId, Integer(recipientMailSent[^1].Args[1]));
        Assert.Equal(32, Integer(recipientMailSent[^1].Args[7]));

        await rig.RestartAsync();
        ownerSession = MailSession(owner, 88_011, new RecordingPacketSink());
        recipientSession = MailSession(recipient, 88_012, new RecordingPacketSink());
        rig.Sessions.Enter(owner, ownerSession);
        rig.Sessions.Enter(recipient, recipientSession);
        dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);
        var recipientMail = await DispatchMailFlowAsync(dispatcher, recipientSession, "mailMgr", "SyncMail", PyNone.Instance, new PyInt(0));
        Assert.Equal(2, Assert.IsType<PyList>(Field(recipientMail, "newMail")).Items.Count);
        Assert.Equal(2, (await ReadGameStoreTableAsync(rig, "mail"))["messages"]!.AsObject().Count);

        static ClientSession MailSession(long characterId, long sessionId, RecordingPacketSink sink)
        {
            var session = new ClientSession(sink) { SessionId = sessionId, ClientId = sessionId, CharacterId = characterId, UserId = 1 };
            session.Set("charid", new PyLong(characterId));
            return session;
        }
    }

    private static async Task<PyValue> DispatchMailFlowAsync(
        PacketDispatcher dispatcher,
        ClientSession session,
        string service,
        string method,
        params PyValue[] arguments)
    {
        var callId = Interlocked.Increment(ref _nextMailFlowCallId);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyLong(callId), ClientId: new PyInt(session.ClientId));
        var destination = MachoAddress.Any(service).Encode();
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(arguments), new PyDict([])]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        var request = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), destination, new PyInt(7), payload, new PyDict([]),
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]));
        var sink = Assert.IsType<RecordingPacketSink>(session.Sink);
        var before = sink.Sent.Count;
        Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
        var packet = sink.Sent.Skip(before).Last(item => MailFlowPacketType(item) is MachoMessage.CallRsp or MachoMessage.ErrorResponse);
        Assert.Equal(MachoMessage.CallRsp, MailFlowPacketType(packet));
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(fields[4]).Items[0]).Decoded!;
    }

    private static long MailFlowPacketType(PyValue packet) =>
        Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items[0]).Value;

    private static long _nextMailFlowCallId = 92_000;
}
