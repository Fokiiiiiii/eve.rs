using Eve.Marshal;

namespace Eve.Services.Tests;

public class SessionChangeTests
{
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(packet);
    }

    private static (ClientSession Session, Capture Sink) NewSession(long userId = 20)
    {
        var sink = new Capture();
        return (new ClientSession(sink) { SessionId = 26_859_804_797_290, ClientId = 20_065_450, NodeId = 65_450, UserId = userId }, sink);
    }

    private static PyValue[] State(PyValue packet) => [.. ((PyTuple)((PyObject)packet).Args).Items];

    private static List<(string Name, PyValue Old, PyValue New)> Changes(PyValue packet)
    {
        var payload = (PyTuple)State(packet)[4];
        var dict = (PyDict)((PyTuple)payload.Items[1]).Items[1];
        return [.. dict.Entries.Select(e => (Eve.Macho.JsSemantics.StringValue(e.Key)!, ((PyTuple)e.Value).Items[0], ((PyTuple)e.Value).Items[1]))];
    }

    [Fact]
    public void The_notification_has_the_shape_the_reference_sends()
    {
        var packet = SessionChangeNotification.Build(
            26_859_804_797_290, 20_065_450, 20, 65_450,
            [new SessionChange("charid", PyNone.Instance, new PyInt(140_000_021)), new SessionChange("hqID", new PyInt(0), PyNone.Instance)]);

        var obj = Assert.IsType<PyObject>(packet);
        Assert.Equal("carbon.common.script.net.machoNetPacket.SessionChangeNotification", Eve.Macho.JsSemantics.StringValue(obj.Name));
        var state = State(packet);
        Assert.Equal(14, state.Length);
        Assert.Equal(new PyInt(16), state[0]);
        // source: the proxy node; destination: the client. The reference passes callID 0, which its
        // address encoder turns into None (`callID || null`), so the client sees None there too.
        Assert.Equal(new PyTuple([new PyInt(1), new PyInt(65_450), PyNone.Instance, PyNone.Instance]), ((PyObject)state[1]).Args);
        Assert.Equal(new PyTuple([new PyInt(2), new PyLong(20_065_450), PyNone.Instance, PyNone.Instance]), ((PyObject)state[2]).Args);
        Assert.Equal(new PyInt(20), state[3]);
        var payload = (PyTuple)state[4];
        Assert.Equal(new PyLong(26_859_804_797_290), payload.Items[0]);
        Assert.Equal(new PyInt(0), ((PyTuple)payload.Items[1]).Items[0]);
        Assert.Equal(new PyList([new PyInt(65_450)]), payload.Items[2]);
        Assert.Equal(new PyDict([]), state[5]);
        Assert.All(state[6..], v => Assert.Equal(PyNone.Instance, v));
        Assert.Equal(
            [("charid", (PyValue)PyNone.Instance, (PyValue)new PyInt(140_000_021)), ("hqID", new PyInt(0), PyNone.Instance)],
            Changes(packet));
    }

    [Fact]
    public void A_session_without_an_account_names_none_as_the_user()
    {
        var packet = SessionChangeNotification.Build(1, 2, 0, 3, [new SessionChange("charid", PyNone.Instance, new PyInt(1))]);
        Assert.Equal(PyNone.Instance, State(packet)[3]);
    }

    [Fact]
    public void Changing_attributes_sets_them_and_announces_the_client_visible_ones_in_order()
    {
        var (session, sink) = NewSession();
        var announced = session.Change(("charid", new PyInt(5)), ("corpid", new PyInt(6)), ("shipid", new PyLong(7)));

        Assert.Equal(["charid", "corpid", "shipid"], announced.Select(c => c.Name));
        Assert.Equal(new PyInt(5), session.Get("charid"));
        Assert.Equal(new PyLong(7), session.Get("shipid"));
        var packet = Assert.Single(sink.Sent);
        Assert.Equal([("charid", (PyValue)PyNone.Instance, (PyValue)new PyInt(5)), ("corpid", PyNone.Instance, new PyInt(6)), ("shipid", PyNone.Instance, new PyLong(7))], Changes(packet));
    }

    [Fact]
    public void Attributes_the_client_does_not_track_are_set_but_never_announced()
    {
        var (session, sink) = NewSession();
        Assert.Empty(session.Change(("accountRole", new PyLong(1)), ("somethingInternal", new PyInt(2))));
        Assert.Empty(sink.Sent);
        Assert.Equal(new PyLong(1), session.Get("accountRole"));
    }

    [Fact]
    public void A_mixed_change_announces_only_the_client_visible_part()
    {
        var (session, sink) = NewSession();
        var announced = session.Change(("accountRole", new PyLong(1)), ("charid", new PyInt(5)));
        Assert.Equal(["charid"], announced.Select(c => c.Name));
        Assert.Equal(["charid"], Changes(Assert.Single(sink.Sent)).Select(c => c.Name));
    }

    [Fact]
    public void A_second_change_carries_the_previous_values_and_skips_what_did_not_change()
    {
        var (session, sink) = NewSession();
        session.Change(("charid", new PyInt(5)), ("stationid", new PyInt(60_000_001)));
        sink.Sent.Clear();

        session.Change(("charid", new PyInt(5)), ("stationid", new PyInt(60_000_002)));
        Assert.Equal([("stationid", (PyValue)new PyInt(60_000_001), (PyValue)new PyInt(60_000_002))], Changes(Assert.Single(sink.Sent)));

        sink.Sent.Clear();
        Assert.Empty(session.Change(("charid", new PyInt(5)), ("stationid", new PyInt(60_000_002))));
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void A_value_of_another_kind_is_a_change_even_when_the_number_is_the_same()
    {
        // the client can tell an int from a long, so the notification says so
        var (session, sink) = NewSession();
        session.Change(("shipid", new PyInt(7)));
        sink.Sent.Clear();
        Assert.Single(session.Change(("shipid", new PyLong(7))));
    }

    [Fact]
    public void Clearing_an_attribute_announces_it_going_to_none()
    {
        var (session, sink) = NewSession();
        session.Change(("stationid", new PyInt(60_000_001)));
        sink.Sent.Clear();
        session.Change(("stationid", PyNone.Instance));
        Assert.Equal([("stationid", (PyValue)new PyInt(60_000_001), (PyValue)PyNone.Instance)], Changes(Assert.Single(sink.Sent)));
    }

    [Fact]
    public void A_session_without_a_connection_still_records_the_change()
    {
        var session = new ClientSession { SessionId = 1 };
        Assert.Single(session.Change(("charid", new PyInt(5))));
        Assert.Equal(new PyInt(5), session.Get("charid"));
    }

    [Fact]
    public void The_client_visible_set_is_the_reference_list()
    {
        string[] reference =
        [
            "role", "countryCode", "charid", "corpid", "corpAccountKey", "hqID", "baseID", "allianceid", "factionid", "warfactionid", "genderID",
            "bloodlineID", "raceID", "schoolID", "stationid", "stationid2", "structureid", "solarsystemid", "solarsystemid2", "locationid",
            "worldspaceid", "constellationid", "regionid", "shipid", "fleetid", "fleetrole", "wingid", "squadid", "corprole", "rolesAtAll",
            "rolesAtBase", "rolesAtHQ", "rolesAtOther",
        ];
        Assert.Equal(reference.Order(), SessionAttributes.ClientVisible.Order());
    }

    [Fact]
    public async Task Concurrent_changes_are_each_announced_once_with_consistent_old_values()
    {
        var (session, sink) = NewSession();
        await Task.WhenAll(Enumerable.Range(1, 50).Select(i => Task.Run(() => session.Change(("shipid", new PyLong(i))))));
        var chain = sink.Sent.SelectMany(p => Changes(p)).ToList();
        Assert.Equal(50, chain.Count);
        var current = (PyValue)PyNone.Instance;
        foreach (var (_, old, next) in chain)
        {
            Assert.Equal(current, old);
            current = next;
        }
        Assert.Equal(session.Get("shipid"), current);
    }
}
