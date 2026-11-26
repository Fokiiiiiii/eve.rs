using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public sealed class SignalTrackerRegistrationProviderTests
{
    [Fact]
    public async Task Scan_manager_callback_registers_per_character_and_publishes_full_state_as_notification()
    {
        const long systemId = 30_000_142;
        var sessions = new CharacterSessions();
        var firstSink = new RecordingSink();
        var secondSink = new RecordingSink();
        var first = CreateSession(sessions, 42, systemId, firstSink);
        var second = CreateSession(sessions, 43, systemId + 1, secondSink);
        var provider = new SignalTrackerRegistrationProvider(sessions);
        var service = new IncursionAnomalyService(
            "scanMgr",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            signalTrackerRegister: provider.RegisterAsync);

        Assert.Equal(PyNone.Instance, await service.CallAsync(RegisterCall(first)));
        first.RunAfterResponse();
        Assert.Equal(PyNone.Instance, await service.CallAsync(RegisterCall(second)));
        second.RunAfterResponse();
        AssertSignalTrackerNotification(Assert.Single(firstSink.Packets), systemId, shouldRemoveOldSites: null);
        AssertSignalTrackerNotification(Assert.Single(secondSink.Packets), systemId + 1, shouldRemoveOldSites: null);

        var fullState = new PyList([new PyDict([]), new PyDict([]), new PyDict([]), new PyDict([])]);
        await provider.PublishFullStateAsync(systemId, fullState, shouldRemoveOldSites: true);

        AssertSignalTrackerNotification(firstSink.Packets[1], systemId, shouldRemoveOldSites: true);
        Assert.Single(secondSink.Packets);
    }

    [Fact]
    public async Task Registration_rejects_stale_session_and_prunes_session_that_changed_system()
    {
        const long systemId = 30_000_142;
        var sessions = new CharacterSessions();
        var liveSink = new RecordingSink();
        var staleSink = new RecordingSink();
        var live = CreateSession(sessions, 42, systemId, liveSink);
        var stale = new ClientSession(staleSink) { UserId = 7, CharacterId = 42 };
        stale.Set("solarsystemid2", new PyInt(systemId));
        var provider = new SignalTrackerRegistrationProvider(sessions);

        await provider.RegisterAsync(RegisterCall(stale), new PyList([]));
        await provider.RegisterAsync(RegisterCall(live), new PyList([]));
        live.Set("solarsystemid2", new PyInt(systemId + 1));
        await provider.PublishFullStateAsync(systemId, new PyList([]));

        Assert.Empty(liveSink.Packets);
        Assert.Empty(staleSink.Packets);
    }

    [Fact]
    public async Task Refresh_sends_a_full_state_that_replaces_old_sites_when_a_static_site_changes()
    {
        const long systemId = 30_000_142;
        var sessions = new CharacterSessions();
        var sink = new RecordingSink();
        var session = CreateSession(sessions, 42, systemId, sink);
        var provider = new SignalTrackerRegistrationProvider(sessions);
        var current = FullStateWithStaticSite(8001);
        provider.ReadFullState = (_, _) => ValueTask.FromResult<PyValue>(current);

        await provider.RegisterAsync(RegisterCall(session), current);
        session.RunAfterResponse();
        AssertSignalTrackerNotification(Assert.Single(sink.Packets), systemId, shouldRemoveOldSites: null);

        current = FullStateWithStaticSite(8002);
        await provider.RefreshAsync();

        Assert.Equal(2, sink.Packets.Count);
        AssertSignalTrackerNotification(sink.Packets[1], systemId, shouldRemoveOldSites: true);
    }

    [Fact]
    public async Task Refresh_sends_a_structure_delta_when_a_structure_appears_in_the_system()
    {
        const long systemId = 30_000_142;
        const long structureId = 1_000_000_000_001;
        var sessions = new CharacterSessions();
        var sink = new RecordingSink();
        var session = CreateSession(sessions, 42, systemId, sink);
        var provider = new SignalTrackerRegistrationProvider(sessions);
        var current = FullStateWithStructures(null);
        provider.ReadFullState = (_, _) => ValueTask.FromResult<PyValue>(current);

        await provider.RegisterAsync(RegisterCall(session), current);
        session.RunAfterResponse();
        Assert.Single(sink.Packets);

        current = FullStateWithStructures(structureId);
        await provider.RefreshAsync();

        Assert.Equal(2, sink.Packets.Count);
        var args = DeltaArguments(sink.Packets[1], "OnSignalTrackerStructureUpdate");
        Assert.Equal(3, args.Count);
        Assert.Equal(systemId, Assert.IsType<PyInt>(args[0]).Value);
        var added = Assert.IsType<PyDict>(args[1]);
        Assert.Equal(new PyLong(structureId), Assert.Single(added.Entries).Key);
        Assert.Empty(Assert.IsType<PyList>(args[2]).Items);
    }

    private static PyList FullStateWithStructures(long? structureId)
    {
        var structures = new List<KeyValuePair<PyValue, PyValue>>();
        if (structureId is { } id)
            structures.Add(new(new PyLong(id), Py.KeyVal(("typeID", new PyInt(35_832)), ("targetID", new PyStr("ABC-123")))));
        return new PyList([new PyDict([]), new PyDict([]), new PyDict([]), new PyDict(structures)]);
    }

    private static IReadOnlyList<PyValue> DeltaArguments(PyValue packetValue, string notification)
    {
        var packet = Assert.IsType<PyObject>(packetValue);
        Assert.Equal(ClientNotification.ClassName, Str(packet.Name));
        var packetItems = Assert.IsType<PyTuple>(packet.Args).Items;
        var destination = Assert.IsType<PyObject>(packetItems[2]);
        var destinationItems = Assert.IsType<PyTuple>(destination.Args).Items;
        Assert.Equal(notification, Str(destinationItems[1]));
        var envelope = Assert.IsType<PyTuple>(packetItems[4]).Items[0];
        var payload = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(envelope).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(payload.Value));
        var eventCall = Assert.IsType<PyTuple>(decoded.Items[1]);
        return Assert.IsType<PyTuple>(eventCall.Items[1]).Items;
    }

    private static PyList FullStateWithStaticSite(long siteId) => new([
        new PyDict([]),
        new PyDict([]),
        new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyLong(siteId), Py.KeyVal(
                ("position", new PyList([new PyReal(1), new PyReal(2), new PyReal(3)])),
                ("dungeonNameID", new PyInt(555)),
                ("factionID", PyNone.Instance))),
        ]),
        new PyDict([]),
    ]);

    private static ClientSession CreateSession(CharacterSessions sessions, long characterId, long systemId, RecordingSink sink)
    {
        var session = new ClientSession(sink) { UserId = 7, CharacterId = characterId };
        session.Set("solarsystemid2", new PyInt(systemId));
        sessions.Enter(characterId, session);
        return session;
    }

    private static ServiceCall RegisterCall(ClientSession session) => new()
    {
        Method = "SignalTrackerRegister",
        Args = [],
        Session = session,
    };

    private static void AssertSignalTrackerNotification(PyValue packetValue, long systemId, bool? shouldRemoveOldSites)
    {
        var packet = Assert.IsType<PyObject>(packetValue);
        Assert.Equal(ClientNotification.ClassName, Str(packet.Name));
        var packetItems = Assert.IsType<PyTuple>(packet.Args).Items;
        var destination = Assert.IsType<PyObject>(packetItems[2]);
        var destinationItems = Assert.IsType<PyTuple>(destination.Args).Items;
        Assert.Equal("OnSignalTrackerFullState", Str(destinationItems[1]));
        Assert.Equal("charid", Str(destinationItems[3]));

        var envelope = Assert.IsType<PyTuple>(packetItems[4]).Items[0];
        var payload = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(envelope).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(payload.Value));
        var eventCall = Assert.IsType<PyTuple>(decoded.Items[1]);
        var args = Assert.IsType<PyTuple>(eventCall.Items[1]).Items;
        Assert.Equal(systemId, Assert.IsType<PyInt>(args[0]).Value);
        Assert.IsType<PyList>(args[1]);
        if (shouldRemoveOldSites is { } removeOldSites)
        {
            Assert.Equal(3, args.Count);
            Assert.Equal(removeOldSites, Assert.IsType<PyBool>(args[2]).Value);
        }
        else
        {
            Assert.Equal(2, args.Count);
        }
    }

    private static string Str(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        _ => throw new InvalidOperationException($"Expected a marshaled string, got {value.GetType().Name}."),
    };

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}