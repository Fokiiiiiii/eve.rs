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