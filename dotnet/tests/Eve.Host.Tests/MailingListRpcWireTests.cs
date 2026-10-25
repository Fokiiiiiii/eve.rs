using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Mailing_list_manager_mutations_are_forwarded_through_registered_host_wire()
    {
        var received = new List<MailingListMutation>();
        var bulkMembers = new PyList(Enumerable.Range(1, 1000).Select(id => (PyValue)new PyLong(id)).ToList());
        var entityAccess = new PyDict(Enumerable.Range(1, 1000).Select(id =>
            new KeyValuePair<PyValue, PyValue>(new PyLong(id), new PyInt(id % 2))).ToArray());
        var joinInfo = Py.KeyVal(("id", new PyLong(500_000_001)));

        MailingListMutationCallback mutate = (mutation, _) =>
        {
            received.Add(mutation);
            return ValueTask.FromResult<PyValue>(mutation.Method switch
            {
                MailingListManagerMethod.Join => joinInfo,
                MailingListManagerMethod.SendWelcomeMail => new PyLong(700_001),
                _ => PyNone.Instance,
            });
        };
        var service = new MailingListService(
            _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            mutate);
        Start(
            strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(90_000_001)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync(); // the initial state

        var calls = new (string Method, MailingListManagerMethod ManagerMethod, PyValue[] Args)[]
        {
            ("Join", MailingListManagerMethod.Join, [new PyStr("fleet ops")]),
            ("KickMembers", MailingListManagerMethod.KickMembers, [new PyLong(500_000_001), bulkMembers]),
            ("Leave", MailingListManagerMethod.Leave, [new PyLong(500_000_001)]),
            ("SaveWelcomeMail", MailingListManagerMethod.SaveWelcomeMail, [new PyLong(500_000_001), new PyStr("Welcome"), new PyStr("Fleet briefing")]),
            ("SendWelcomeMail", MailingListManagerMethod.SendWelcomeMail, [new PyLong(500_000_001), new PyStr("Welcome"), new PyStr("Fleet briefing")]),
            ("SetDefaultAccess", MailingListManagerMethod.SetDefaultAccess, [new PyLong(500_000_001), new PyInt(0), new PyInt(1), new PyInt(0)]),
            ("SetEntitiesAccess", MailingListManagerMethod.SetEntitiesAccess, [new PyLong(500_000_001), entityAccess]),
            ("SetMembersClear", MailingListManagerMethod.SetMembersClear, [new PyLong(500_000_001), bulkMembers]),
            ("SetMembersMuted", MailingListManagerMethod.SetMembersMuted, [new PyLong(500_000_001), bulkMembers]),
            ("SetMembersOperator", MailingListManagerMethod.SetMembersOperator, [new PyLong(500_000_001), bulkMembers]),
        };

        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, (uint)(index + 1), "mailingListsMgr", call.Method, call.Args))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var result = Result(MarshalCodec.Decode(reply.Payload));
            if (call.ManagerMethod == MailingListManagerMethod.Join)
            {
                var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(result).Args);
                Assert.Equal(500_000_001L, Assert.IsType<PyLong>(
                    fields.Entries.Single(entry => T(entry.Key) == "id").Value).Value);
            }
            else if (call.ManagerMethod == MailingListManagerMethod.SendWelcomeMail)
                Assert.Equal(700_001L, Assert.IsType<PyLong>(result).Value);
            else
                Assert.Same(PyNone.Instance, result);
        }

        Assert.Equal(calls.Length, received.Count);
        for (var index = 0; index < calls.Length; index++)
        {
            var expected = calls[index];
            var actual = received[index];
            Assert.Equal(expected.ManagerMethod, actual.Method);
            Assert.Equal(90_000_001L, actual.Caller.CharacterId);
            Assert.Equal(expected.Args.Length, actual.PositionalArguments.Count);
            for (var argIndex = 0; argIndex < expected.Args.Length; argIndex++)
            {
                if (expected.Args[argIndex] is PyList)
                    Assert.Equal(1000, Assert.IsType<PyList>(actual.PositionalArguments[argIndex]).Items.Count);
                else if (expected.Args[argIndex] is PyDict)
                    Assert.Equal(1000, Assert.IsType<PyDict>(actual.PositionalArguments[argIndex]).Entries.Count);
                else if (expected.Args[argIndex] is PyStr expectedText)
                    Assert.Equal(expectedText.Value, T(actual.PositionalArguments[argIndex]));
                else
                    Assert.Equal(expected.Args[argIndex], actual.PositionalArguments[argIndex]);
            }
        }
    }
}
