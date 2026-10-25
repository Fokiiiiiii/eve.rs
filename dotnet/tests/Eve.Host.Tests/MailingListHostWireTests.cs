using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Mailing_lists_management_calls_cross_the_registered_host_service_on_wire()
    {
        var mutations = new List<MailingListMutation>();
        MailingListMutationCallback mutate = (mutation, _) =>
        {
            mutations.Add(mutation);
            PyValue result = mutation.Method switch
            {
                MailingListManagerMethod.Join => Py.KeyVal(
                    ("id", new PyLong(500000001)),
                    ("name", new PyStr("fleetops")),
                    ("displayName", new PyStr("Fleet Ops")),
                    ("isMuted", PyBool.False),
                    ("isOperator", PyBool.False),
                    ("isOwner", PyBool.False)),
                MailingListManagerMethod.SendWelcomeMail => new PyLong(700000001),
                _ => PyNone.Instance,
            };
            return ValueTask.FromResult(result);
        };
        var service = new MailingListService(
            _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            mutate);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(90000001)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var requests = new (string Method, MailingListManagerMethod ManagerMethod, PyValue[] Arguments)[]
        {
            ("Join", MailingListManagerMethod.Join, [new PyStr("Fleet Ops")]),
            ("KickMembers", MailingListManagerMethod.KickMembers, [new PyLong(500000001), new PyList([new PyInt(90000002)])]),
            ("Leave", MailingListManagerMethod.Leave, [new PyLong(500000001)]),
            ("SaveWelcomeMail", MailingListManagerMethod.SaveWelcomeMail, [new PyLong(500000001), new PyStr("Welcome"), new PyStr("Body")]),
            ("SendWelcomeMail", MailingListManagerMethod.SendWelcomeMail, [new PyLong(500000001), new PyStr("Welcome"), new PyStr("Body")]),
            ("SetDefaultAccess", MailingListManagerMethod.SetDefaultAccess, [new PyLong(500000001), new PyInt(0), new PyInt(1)]),
            ("SetEntitiesAccess", MailingListManagerMethod.SetEntitiesAccess, [new PyLong(500000001), new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyLong(90000003), new PyInt(0)),
                new KeyValuePair<PyValue, PyValue>(new PyLong(90000004), new PyInt(1)),
            ])]),
            ("SetMembersClear", MailingListManagerMethod.SetMembersClear, [new PyLong(500000001), new PyList([new PyInt(90000002)])]),
            ("SetMembersMuted", MailingListManagerMethod.SetMembersMuted, [new PyLong(500000001), new PyList([new PyInt(90000002)])]),
            ("SetMembersOperator", MailingListManagerMethod.SetMembersOperator, [new PyLong(500000001), new PyList([new PyInt(90000002)])]),
        };

        for (var i = 0; i < requests.Length; i++)
        {
            var request = requests[i];
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, i + 1, "mailingListsMgr", request.Method, request.Arguments))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var result = Result(MarshalCodec.Decode(reply.Payload));
            if (request.ManagerMethod == MailingListManagerMethod.Join)
            {
                var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(result).Args)
                    .Entries.ToDictionary(entry => T(entry.Key)!, entry => entry.Value);
                Assert.Equal(new PyLong(500000001), fields["id"]);
                Assert.Equal("fleetops", T(fields["name"]));
                Assert.Equal("Fleet Ops", T(fields["displayName"]));
            }
            else if (request.ManagerMethod == MailingListManagerMethod.SendWelcomeMail)
                Assert.Equal(new PyLong(700000001), result);
            else
                Assert.Same(PyNone.Instance, result);
        }

        Assert.Equal(requests.Select(request => request.ManagerMethod), mutations.Select(mutation => mutation.Method));
        Assert.All(mutations, mutation => Assert.Equal(
            new MailingListCaller((long)open.SessionId, (long)open.ClientId, (long)open.UserId, 90000001),
            mutation.Caller));
        Assert.Equal("Fleet Ops", T(mutations[0].PositionalArguments[0]));
        Assert.Equal(new PyLong(500000001), mutations[1].PositionalArguments[0]);
        Assert.Equal(3, mutations[5].PositionalArguments.Count);
        Assert.Equal(2, Assert.IsType<PyDict>(mutations[6].PositionalArguments[1]).Entries.Count);
    }
}
