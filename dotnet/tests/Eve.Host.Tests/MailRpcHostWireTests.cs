using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Mail_and_mailing_list_Rpcs_keep_registered_wire_shapes_and_mutation_arguments()
    {
        var state = new JsonObject
        {
            ["messages"] = new JsonObject
            {
                ["100"] = new JsonObject
                {
                    ["messageID"] = 100, ["senderID"] = 20, ["toCharacterIDs"] = new JsonArray(90000001),
                    ["toListID"] = null, ["toCorpOrAllianceID"] = null, ["title"] = "Subject", ["sentDate"] = 0,
                    ["body"] = "Body",
                },
            },
            ["mailboxes"] = new JsonObject
            {
                ["90000001"] = new JsonObject
                {
                    ["statuses"] = new JsonObject { ["100"] = new JsonObject { ["statusMask"] = 0, ["labelMask"] = 2 } },
                    ["labels"] = new JsonObject(),
                },
            },
        };
        var mailMutations = new List<string>();
        var mailMutationSizes = new List<int>();
        var listMutations = new List<string>();
        var mail = new MailService(
            _ => Task.FromResult((JsonObject)state.DeepClone()),
            (_, _, _, _) => Task.CompletedTask,
            mutateMailbox: (mutation, _) =>
            {
                mailMutations.Add(mutation.Method);
                mailMutationSizes.Add(mutation.Arguments.FirstOrDefault() is PyList ids ? ids.Items.Count : 0);
                var result = MailboxMutation.Apply(state, mutation);
                return Task.FromResult(result);
            });
        var lists = new MailingListService(
            _ => ValueTask.FromResult<JsonObject?>(new JsonObject()),
            (mutation, _) =>
            {
                listMutations.Add(mutation.Method.ToString());
                return ValueTask.FromResult<PyValue>(
                    mutation.Method == MailingListManagerMethod.Create ? new PyInt(500000000) : PyNone.Instance);
            });
        Start(
            configure: registry =>
            {
                registry.Register(mail);
                registry.Register(lists);
            },
            sessionOpened: session => session.Set("charid", new PyInt(90000001)));
        var (front, open) = await OpenFrontAsync();

        var sync = Assert.IsType<PyObject>(await HostRpcAsync(front, open, 1, "mailMgr", "SyncMail", PyNone.Instance, new PyInt(0)));
        var syncFields = Assert.IsType<PyDict>(sync.Args);
        var newMail = Assert.IsType<PyList>(MailWireField(syncFields, "newMail"));
        Assert.Single(newMail.Items);
        Assert.Single(Assert.IsType<PyList>(MailWireField(syncFields, "mailStatus")).Items);

        long callId = 2;
        foreach (var (method, args) in new (string, PyValue[])[]
        {
            ("MarkAsRead", [new PyList([new PyInt(100)])]),
            ("MarkAsUnread", [new PyList([new PyInt(100)])]),
            ("MarkAsReadByLabel", [new PyInt(2)]),
            ("MarkAsReadByList", [new PyInt(500000000)]),
            ("MarkAsUnreadByLabel", [new PyInt(2)]),
            ("MarkAsUnreadByList", [new PyInt(500000000)]),
            ("MoveFromTrash", [new PyList([new PyInt(100)])]),
            ("MoveToTrash", [new PyList([new PyInt(100)])]),
            ("MoveAllFromTrash", []),
            ("MoveAllToTrash", []),
            ("MoveToTrashByLabel", [new PyInt(2)]),
            ("MoveToTrashByList", [new PyInt(500000000)]),
            ("RemoveLabels", [new PyList([new PyInt(100)]), new PyInt(2)]),
            ("ReplaceLabels", [new PyList([new PyInt(100)]), new PyInt(4)]),
        })
        {
            Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailMgr", method, args));
        }
        Assert.Equal(14, mailMutations.Count);
        Assert.Contains("MoveAllFromTrash", mailMutations);
        Assert.Contains("ReplaceLabels", mailMutations);
        var persistedStatus = (JsonObject)state["mailboxes"]!["90000001"]!["statuses"]!["100"]!;
        Assert.Equal(8, persistedStatus["statusMask"]!.GetValue<long>());
        Assert.Equal(4, persistedStatus["labelMask"]!.GetValue<long>());

        var burstIds = new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyInt(id)).ToArray());
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailMgr", "MarkAsRead", burstIds));
        Assert.Equal(501, mailMutationSizes[^1]);
        Assert.Equal(9, persistedStatus["statusMask"]!.GetValue<long>());

        Assert.Empty(Assert.IsType<PyDict>(await HostRpcAsync(front, open, callId++, "mailingListsMgr", "GetJoinedLists")).Entries);
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailingListsMgr", "GetInfo", new PyInt(500000000)));
        Assert.Empty(Assert.IsType<PyDict>(await HostRpcAsync(front, open, callId++, "mailingListsMgr", "GetMembers", new PyInt(500000000))).Entries);
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailingListsMgr", "GetSettings", new PyInt(500000000)));
        Assert.Empty(Assert.IsType<PyList>(await HostRpcAsync(front, open, callId++, "mailingListsMgr", "GetWelcomeMail", new PyInt(500000000))).Items);
        Assert.Equal(new PyInt(500000000), await HostRpcAsync(front, open, callId++, "mailingListsMgr", "Create",
            new PyStr("Fleet Ops"), new PyInt(1), new PyInt(1), new PyInt(2500)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailingListsMgr", "Delete", new PyInt(500000000)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailingListsMgr", "ClearEntityAccess",
            new PyInt(500000000), new PyInt(800)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailingListsMgr", "ClearWelcomeMail",
            new PyInt(500000000)));
        Assert.Equal(
            ["Create", "Delete", "ClearEntityAccess", "ClearWelcomeMail"],
            listMutations);
    }

    [Fact]
    public async Task Mail_mgr_added_methods_mutate_state_return_client_values_and_notify_over_host_wire()
    {
        const long characterId = 90000001;
        var state = new JsonObject
        {
            ["messages"] = new JsonObject
            {
                ["100"] = new JsonObject
                {
                    ["messageID"] = 100, ["senderID"] = 20, ["toCharacterIDs"] = new JsonArray(characterId),
                    ["toListID"] = null, ["toCorpOrAllianceID"] = null, ["title"] = "Trash one", ["sentDate"] = 0,
                    ["body"] = "Body",
                },
                ["101"] = new JsonObject
                {
                    ["messageID"] = 101, ["senderID"] = 20, ["toCharacterIDs"] = new JsonArray(characterId),
                    ["toListID"] = null, ["toCorpOrAllianceID"] = null, ["title"] = "Trash two", ["sentDate"] = 0,
                    ["body"] = "Body",
                },
                ["102"] = new JsonObject
                {
                    ["messageID"] = 102, ["senderID"] = 20, ["toCharacterIDs"] = new JsonArray(characterId, 90000002),
                    ["toListID"] = null, ["toCorpOrAllianceID"] = null, ["title"] = "Keep", ["sentDate"] = 0,
                    ["body"] = "Body",
                },
            },
            ["mailboxes"] = new JsonObject
            {
                [characterId.ToString()] = new JsonObject
                {
                    ["statuses"] = new JsonObject
                    {
                        ["100"] = new JsonObject { ["statusMask"] = 8, ["labelMask"] = 0 },
                        ["101"] = new JsonObject { ["statusMask"] = 8, ["labelMask"] = 0 },
                        ["102"] = new JsonObject { ["statusMask"] = 0, ["labelMask"] = 0 },
                    },
                    ["labels"] = new JsonObject(),
                },
                ["90000002"] = new JsonObject
                {
                    ["statuses"] = new JsonObject
                    {
                        ["102"] = new JsonObject { ["statusMask"] = 0, ["labelMask"] = 0 },
                    },
                    ["labels"] = new JsonObject(),
                },
            },
        };
        var sessions = new List<ClientSession>();
        var mail = new MailService(
            _ => Task.FromResult((JsonObject)state.DeepClone()),
            (_, _, _, _) => Task.CompletedTask,
            mutateMailbox: (mutation, _) =>
            {
                var result = MailboxMutation.Apply(state, mutation);
                var recipient = sessions.Single(session => !ReferenceEquals(session, mutation.Session));
                MailboxMutationNotifications.Publish(mutation, result, recipient);
                return Task.FromResult(result);
            });
        Start(configure: registry => registry.Register(mail), sessionOpened: session =>
        {
            session.Set("charid", new PyInt(characterId));
            sessions.Add(session);
        });
        var (front, open) = await OpenFrontAsync();
        var (otherFront, _) = await OpenFrontAsync();
        long callId = 1;

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, callId++, "mailMgr", "MarkAllAsRead"));
        var readNotice = ReadMailNotification(await otherFront.ReceiveAsync());
        Assert.Equal("OnMailUpdatedByExternal", readNotice.Name);
        Assert.Equal(PyBool.True, readNotice.Args[1]);
        Assert.Equal(1, ((JsonObject)state["mailboxes"]![characterId.ToString()]!["statuses"]!["102"]!)["statusMask"]!.GetValue<long>());

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, callId++, "mailMgr", "MarkAllAsUnread"));
        var unreadNotice = ReadMailNotification(await otherFront.ReceiveAsync());
        Assert.Equal("OnMailUpdatedByExternal", unreadNotice.Name);
        Assert.Equal(PyBool.False, unreadNotice.Args[1]);
        Assert.Equal(0, ((JsonObject)state["mailboxes"]![characterId.ToString()]!["statuses"]!["102"]!)["statusMask"]!.GetValue<long>());

        Assert.Equal(new PyInt(16),
            await HostRpcAsync(front, open, callId++, "mailMgr", "CreateLabel", new PyStr("Research"), PyNone.Instance));
        var createdNotice = ReadMailNotification(await otherFront.ReceiveAsync());
        Assert.Equal("OnLabelsCreatedByExternal", createdNotice.Name);
        Assert.Equal(16, WireInteger(MailWireField(Assert.IsType<PyDict>(Assert.IsType<PyObject>(createdNotice.Args[0]).Args), "labelID")));
        Assert.Equal("Research", JsSemantics.StringValue(MailWireField(
            Assert.IsType<PyDict>(Assert.IsType<PyObject>(createdNotice.Args[0]).Args), "name")));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailMgr",
            "EditLabel", new PyLong(16), PyNone.Instance, new PyLong(12)));
        var labels = Assert.IsType<PyDict>(await HostRpcAsync(front, open, callId++, "mailMgr", "GetLabels"));
        var labelRow = Assert.IsType<PyObject>(labels.Entries.Single(entry => WireInteger(entry.Key) == 16).Value);
        Assert.Equal(12, WireInteger(MailWireField(Assert.IsType<PyDict>(labelRow.Args), "color")));
        Assert.Equal("Research", JsSemantics.StringValue(MailWireField(Assert.IsType<PyDict>(labelRow.Args), "name")));

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailMgr",
            "AssignLabels", new PyTuple([new PyLong(102)]), new PyLong(16)));
        var assignNotice = ReadMailNotification(await otherFront.ReceiveAsync());
        Assert.Equal("OnMailUpdatedByExternal", assignNotice.Name);
        Assert.Same(PyNone.Instance, assignNotice.Args[1]);
        Assert.Equal(16, WireInteger(assignNotice.Args[2]));
        var sync = Assert.IsType<PyObject>(await HostRpcAsync(front, open, callId++, "mailMgr",
            "SyncMail", PyNone.Instance, new PyInt(0)));
        var statusRows = Assert.IsType<PyList>(MailWireField(Assert.IsType<PyDict>(sync.Args), "mailStatus"));
        var status102 = statusRows.Items.Single(row =>
            WireInteger(MailWireField(Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args), "messageID")) == 102);
        Assert.Equal(16, WireInteger(MailWireField(
            Assert.IsType<PyDict>(Assert.IsType<PyObject>(status102).Args), "labelMask")));

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailMgr", "DeleteLabel", new PyLong(16)));
        sync = Assert.IsType<PyObject>(await HostRpcAsync(front, open, callId++, "mailMgr",
            "SyncMail", PyNone.Instance, new PyInt(0)));
        statusRows = Assert.IsType<PyList>(MailWireField(Assert.IsType<PyDict>(sync.Args), "mailStatus"));
        status102 = statusRows.Items.Single(row =>
            WireInteger(MailWireField(Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args), "messageID")) == 102);
        Assert.Equal(0, WireInteger(MailWireField(
            Assert.IsType<PyDict>(Assert.IsType<PyObject>(status102).Args), "labelMask")));

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailMgr", "EmptyTrash"));
        var emptiedNotice = ReadMailNotification(await otherFront.ReceiveAsync());
        Assert.Equal("OnMailDeleted", emptiedNotice.Name);
        Assert.Equal([100L, 101L], Assert.IsType<PyList>(emptiedNotice.Args[0]).Items.Select(WireInteger));
        Assert.False(((JsonObject)state["messages"]!).ContainsKey("100"));
        Assert.False(((JsonObject)state["messages"]!).ContainsKey("101"));

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailMgr",
            "DeleteMail", new PyTuple([new PyLong(102)])));
        var deletedNotice = ReadMailNotification(await otherFront.ReceiveAsync());
        Assert.Equal("OnMailDeleted", deletedNotice.Name);
        Assert.Equal([102L], Assert.IsType<PyList>(deletedNotice.Args[0]).Items.Select(WireInteger));
        Assert.True(((JsonObject)state["messages"]!).ContainsKey("102"));
        Assert.True(((JsonObject)state["mailboxes"]!["90000002"]!["statuses"]!).ContainsKey("102"));
        var headersAfterDelete = Assert.IsType<PyList>(await HostRpcAsync(front, open, callId++, "mailMgr",
            "GetMailHeaders", new PyList([new PyLong(100), new PyLong(101), new PyLong(102)])));
        Assert.Empty(headersAfterDelete.Items);
    }

    [Fact]
    public async Task Mail_mgr_SendMail_returns_the_message_id_and_sends_OnMailSent_with_client_fields()
    {
        const long senderId = 90000011;
        const long recipientId = 90000012;
        const long messageId = 200;
        const long sentFiletime = 133801234567890123;
        MailSendRequest? sentRequest = null;
        var state = new JsonObject
        {
            ["messages"] = new JsonObject(),
            ["mailboxes"] = new JsonObject
            {
                [senderId.ToString()] = new JsonObject { ["statuses"] = new JsonObject(), ["labels"] = new JsonObject() },
                [recipientId.ToString()] = new JsonObject { ["statuses"] = new JsonObject(), ["labels"] = new JsonObject() },
            },
        };
        var sessions = new List<ClientSession>();
        var mail = new MailService(
            _ => Task.FromResult((JsonObject)state.DeepClone()),
            (_, _, _, _) => Task.CompletedTask,
            sendMail: (request, _, _) =>
            {
                sentRequest = request;
                var organizationId = request.CorporationOrAllianceRecipientIds?.FirstOrDefault();
                state["messages"]![messageId.ToString()] = new JsonObject
                {
                    ["messageID"] = messageId,
                    ["senderID"] = request.SenderCharacterId,
                    ["toCharacterIDs"] = new JsonArray(request.CharacterRecipientIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                    ["toListID"] = request.MailingListId,
                    ["toCorpOrAllianceID"] = organizationId is > 0 ? JsonValue.Create(organizationId.Value) : null,
                    ["title"] = request.Title,
                    ["sentDate"] = sentFiletime,
                    ["body"] = request.Body,
                };
                state["mailboxes"]![recipientId.ToString()]!["statuses"]![messageId.ToString()] =
                    new JsonObject { ["statusMask"] = 0, ["labelMask"] = 1 };
                state["mailboxes"]![senderId.ToString()]!["statuses"]![messageId.ToString()] =
                    new JsonObject { ["statusMask"] = 1, ["labelMask"] = 2 };
                var recipientSession = sessions.Single(session => session.CharacterId == recipientId);
                recipientSession.SendNotification("OnMailSent", "clientID",
                    MailSentNotification.Build(request, messageId, sentFiletime, 0));
                return Task.FromResult<long?>(messageId);
            });
        Start(configure: registry => registry.Register(mail), sessionOpened: session =>
        {
            session.Set("charid", new PyInt(sessions.Count == 0 ? senderId : recipientId));
            sessions.Add(session);
        });
        var (senderFront, senderOpen) = await OpenFrontAsync();
        var (recipientFront, recipientOpen) = await OpenFrontAsync();

        Assert.Equal(new PyLong(messageId), await HostRpcAsync(
            senderFront, senderOpen, 1, "mailMgr", "SendMail",
            new PyList([new PyLong(recipientId)]),
            PyNone.Instance,
            new PyList([]),
            new PyWString("  Subject  "),
            new PyWString("\tBody\r\n"),
            new PyLong(0),
            new PyInt(0)));

        Assert.Equal("Subject", sentRequest?.Title);
        Assert.Equal("Body", sentRequest?.Body);
        var notice = ReadMailNotification(await recipientFront.ReceiveAsync());
        Assert.Equal("OnMailSent", notice.Name);
        Assert.Equal(8, notice.Args.Length);
        Assert.Equal(messageId, WireInteger(notice.Args[0]));
        Assert.Equal(senderId, WireInteger(notice.Args[1]));
        Assert.Equal(sentFiletime, WireInteger(notice.Args[2]));
        Assert.Equal([recipientId], Assert.IsType<PyList>(notice.Args[3]).Items.Select(WireInteger));
        Assert.Same(PyNone.Instance, notice.Args[4]);
        Assert.Same(PyNone.Instance, notice.Args[5]);
        Assert.Equal("Subject", JsSemantics.StringValue(notice.Args[6]));
        Assert.Equal(0, WireInteger(notice.Args[7]));

        var headers = Assert.IsType<PyList>(await HostRpcAsync(
            recipientFront, recipientOpen, 2, "mailMgr", "GetMailHeaders", new PyTuple([new PyLong(messageId)])));
        var header = Assert.Single(headers.Items);
        Assert.Same(PyNone.Instance, MailWireField(Assert.IsType<PyDict>(Assert.IsType<PyObject>(header).Args),
            "toCorpOrAllianceID"));
    }

[Fact]
    public async Task Mail_mgr_PokePlayerAboutChatMsgGm_accepts_unicode_channel_text_over_host_wire()
    {
        const long characterId = 90000013;
        const long messageId = 201;
        GmChatPokeMailRequest? sent = null;
        var mail = new MailService(
            _ => Task.FromResult(new JsonObject()),
            (_, _, _, _) => Task.CompletedTask,
            pokePlayerAboutChatMsgGm: (request, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                sent = request;
                return Task.FromResult<long?>(messageId);
            });
        Start(configure: registry => registry.Register(mail), sessionOpened: session =>
        {
            session.Set("role", new PyLong(GameMasterRoles.Gml));
            session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
        });
        var (front, open) = await OpenFrontAsync();
        var channelName = new string('x', 257);

        Assert.Equal(new PyLong(messageId), await HostRpcAsync(
            front, open, 1, "mailMgr", "PokePlayerAboutChatMsgGm",
            new PyLong(characterId), new PyWString(channelName)));

        Assert.Equal(characterId, sent?.CharacterId);
        Assert.Equal(channelName, sent?.ChannelName);
        Assert.Equal("GM chat request", sent?.Title);
        Assert.Equal(GmChatPokeMailRequest.BuildBody(channelName), sent?.Body);
        Assert.False(sent?.SaveSenderCopy);
    }

    private static (string Name, PyValue[] Args) ReadMailNotification(HostMessage message)
    {
        var send = Assert.IsType<Send>(message);
        var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload));
        var state = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal(MachoMessage.Notification, WireInteger(state[0]));
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(state[2]).Args).Items;
        var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(
            Assert.IsType<PyTuple>(state[4]).Items[0]).Items[1]);
        var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray()));
        var args = Assert.IsType<PyTuple>(
            Assert.IsType<PyTuple>(inner.Items[1]).Items[1]).Items;
        return (JsSemantics.StringValue(address[1])!, [.. args]);
    }

    private static PyValue MailWireField(PyDict fields, string name) =>
        fields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
}
