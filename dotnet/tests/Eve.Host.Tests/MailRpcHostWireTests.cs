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
                var mailbox = (JsonObject)state["mailboxes"]!["90000001"]!;
                MailboxMutation.Apply(mailbox, (JsonObject)state["messages"]!, mutation);
                return Task.CompletedTask;
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

        var burstIds = new PyList(Enumerable.Range(1, 500).Select(id => (PyValue)new PyInt(id)).ToArray());
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, callId++, "mailMgr", "MarkAsRead", burstIds));
        Assert.Equal(500, mailMutationSizes[^1]);
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

    private static PyValue MailWireField(PyDict fields, string name) =>
        fields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
}
