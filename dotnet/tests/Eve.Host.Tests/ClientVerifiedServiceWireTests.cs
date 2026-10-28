using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Hacking;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Mail_mgr_headers_and_compressed_body_match_the_client_consumers_over_host_wire()
    {
        const long characterId = 90_001;
        const long messageId = 401;
        const long sentDate = 1_790_646_377_000;
        const string bodyText = "Capsuleer mail — 日本語";
        var state = new JsonObject
        {
            ["messages"] = new JsonObject
            {
                [messageId.ToString()] = new JsonObject
                {
                    ["messageID"] = messageId,
                    ["senderID"] = 20,
                    ["toCharacterIDs"] = new JsonArray(characterId, 90_002),
                    ["toListID"] = null,
                    ["toCorpOrAllianceID"] = null,
                    ["title"] = "Fleet report",
                    ["sentDate"] = sentDate,
                    ["body"] = bodyText,
                },
            },
            ["mailboxes"] = new JsonObject
            {
                [characterId.ToString()] = new JsonObject
                {
                    ["statuses"] = new JsonObject
                    {
                        [messageId.ToString()] = new JsonObject { ["statusMask"] = 0, ["labelMask"] = 0 },
                    },
                    ["labels"] = new JsonObject(),
                },
            },
        };
        var markCalls = new List<(long CharacterId, long MessageId)>();
        var mail = new MailService(
            _ => Task.FromResult((JsonObject)state.DeepClone()),
            (ownerId, id, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                markCalls.Add((ownerId, id));
                var status = (JsonObject)state["mailboxes"]![characterId.ToString()]!["statuses"]![id.ToString()]!;
                status["statusMask"] = status["statusMask"]!.GetValue<int>() | 1;
                return Task.CompletedTask;
            });
        Start(configure: registry => registry.Register(mail),
            sessionOpened: session => session.Set("charid", new PyInt(characterId)));
        var (front, open) = await OpenFrontAsync();
        long callId = 1;

        async Task<PyValue> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, "mailMgr", method, new PyTuple(args), TransportKwargs()))));
            while (true)
            {
                var message = Assert.IsType<Send>(await front.ReceiveAsync());
                var decoded = MarshalCodec.Decode(message.Payload);
                var type = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type;
                if (type == MachoMessage.Notification)
                {
                    _ = ReadMailNotification(message);
                    continue;
                }
                Assert.Equal(MachoMessage.CallRsp, type);
                return Result(decoded)!;
            }
        }

        var headers = Assert.IsType<PyList>(await Call(
            "GetMailHeaders", new PyTuple([new PyLong(messageId)])));
        var header = Assert.IsType<PyObject>(Assert.Single(headers.Items));
        var headerFields = Assert.IsType<PyDict>(header.Args);
        Assert.Equal(messageId, WireInteger(MailWireField(headerFields, "messageID")));
        Assert.Equal(20, WireInteger(MailWireField(headerFields, "senderID")));
        Assert.Equal("90001,90002", WireText(MailWireField(headerFields, "toCharacterIDs")));
        Assert.Same(PyNone.Instance, MailWireField(headerFields, "toListID"));
        Assert.Same(PyNone.Instance, MailWireField(headerFields, "toCorpOrAllianceID"));
        Assert.Equal("Fleet report", WireText(MailWireField(headerFields, "title")));
        Assert.Equal(sentDate, WireInteger(MailWireField(headerFields, "sentDate")));

        var compressed = Assert.IsType<PyBuffer>(await Call(
            "GetBody", new PyLong(messageId), PyBool.True));
        using var input = new MemoryStream(compressed.Value.ToArray());
        using var decompressor = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);
        Assert.Equal(bodyText, await reader.ReadToEndAsync());
        Assert.Equal([(characterId, messageId)], markCalls);
        Assert.Equal(1, state["mailboxes"]![characterId.ToString()]!["statuses"]![messageId.ToString()]!["statusMask"]!.GetValue<int>());

        Assert.Same(PyNone.Instance, await Call("GetBody", new PyLong(999), PyBool.True));
        Assert.Single(markCalls);
    }

    [Fact]
    public async Task Map_deadspace_agents_and_roaming_weather_keep_the_client_wire_shapes()
    {
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres;Timeout=1;Pooling=false");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs)
        {
            ReadDataHook = call => ValueTask.FromResult<PyValue?>(call.Method switch
            {
                "GetDeadspaceAgentsMap" => PyNone.Instance,
                "GetAllRoamingWeatherSystems" => Py.Rowset(["locationID", "sceneType"], []),
                _ => null,
            }),
        };
        var registeredMap = new IncursionAnomalyService(
            "map",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            fallback: map,
            nowUnixMs: () => FixedNowMs);
        Start(configure: registry => registry.Register(registeredMap), strictRpc: true);
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 1, "map", "GetDeadspaceAgentsMap", new PyStr("en")));
        var weather = Assert.IsType<PyObject>(await HostRpcAsync(
            front, open, 2, "map", "GetAllRoamingWeatherSystems"));
        Assert.Equal(["locationID", "sceneType"],
            Assert.IsType<PyList>(WireField(weather, "header")).Items.Select(WireText).ToArray());
        Assert.Empty(Assert.IsType<PyList>(WireField(weather, "lines")).Items);
    }

    [Fact]
    public async Task Hacking_utility_calls_send_client_events_and_reply_none_over_host_wire()
    {
        const long characterId = 90_003;
        const long targetId = 500;
        var attempts = new HackingAttempts();
        var service = new HackingMgrService(attempts, new ClientVerifiedHackingSite());
        var target = new HackableTarget(targetId, 30_000_142, 1, Relic: false);
        var virusBoard = HackingBoard.Blank();
        var virusGame = new HackingGame(
            new HackingAttempt(targetId, 22_329, HackingWire.GameTypeHacking, 1, 40, 20, 3, 1),
            virusBoard);
        virusGame.Slots[0] = new UtilityItem(1, HackingWire.SubtypeUeSelfRepair);
        attempts.Set(characterId, new HackingWindow(virusGame, target, 0));

        Start(configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(characterId)));
        var (front, open) = await OpenFrontAsync();
        long callId = 1;

        async Task<(string Name, PyValue[] Args)> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, "hackingMgr", method, new PyTuple(args), TransportKwargs()))));
            var first = Assert.IsType<Send>(await front.ReceiveAsync());
            var second = Assert.IsType<Send>(await front.ReceiveAsync());
            var firstDecoded = MarshalCodec.Decode(first.Payload);
            var secondDecoded = MarshalCodec.Decode(second.Payload);
            var firstType = Assert.IsType<MachoPacket>(MachoPacket.Decode(firstDecoded)).Type;
            var secondType = Assert.IsType<MachoPacket>(MachoPacket.Decode(secondDecoded)).Type;
            var notification = firstType == MachoMessage.Notification ? first : second;
            var response = firstType == MachoMessage.CallRsp ? firstDecoded : secondDecoded;
            Assert.Contains(MachoMessage.CallRsp, new[] { firstType, secondType });
            Assert.Contains(MachoMessage.Notification, new[] { firstType, secondType });
            Assert.Same(PyNone.Instance, Result(response));
            return ReadMailNotification(notification);
        }

        var virusTurn = await Call("UsedUtilityElementOnVirus", new PyInt(0));
        Assert.Equal("OnHackingTurnComplete", virusTurn.Name);
        AssertHackingEvent(Assert.Single(TurnEvents(virusTurn.Args)), HackingWire.EventUeInUse, data =>
        {
            Assert.Equal(new PyInt(0), WireField(data, "index"));
            Assert.Same(PyBool.True, WireField(data, "value"));
        });

        var targetBoard = HackingBoard.Blank();
        var core = targetBoard.Tiles[new Coord(0, 3)];
        core.Type = HackingWire.TypeCore;
        core.Hidden = false;
        core.Coherence = 10;
        var targetGame = new HackingGame(
            new HackingAttempt(targetId, 22_329, HackingWire.GameTypeHacking, 1, 40, 20, 3, 2),
            targetBoard);
        targetGame.Slots[0] = new UtilityItem(1, HackingWire.SubtypeUeKernelRot);
        attempts.Set(characterId, new HackingWindow(targetGame, target, 0));

        var targetTurn = await Call("UsedUtilityElement", new PyInt(0), Py.Tuple(new PyInt(0), new PyInt(3)));
        Assert.Equal("OnHackingTurnComplete", targetTurn.Name);
        var events = TurnEvents(targetTurn.Args);
        AssertHackingEvent(Assert.IsType<PyObject>(events[0]), HackingWire.EventKernelRot, data =>
        {
            Assert.Equal(new PyInt(HackingWire.TypeCore), WireField(data, "type"));
            Assert.Equal(new PyInt(5), WireField(data, "coherence"));
            Assert.Equal(Py.Tuple(new PyInt(0), new PyInt(3)), WireField(data, "coord"));
        });
        Assert.Contains(events, item => WireInteger(WireField(item, "eventID")) == HackingWire.EventUeRemoved);
    }

    private sealed class ClientVerifiedHackingSite : IHackingSite
    {
        public ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<HackableTarget?>(null);

        public ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask AttemptEndedAsync(ClientSession session, HackableTarget target, bool won, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private static PyValue[] TurnEvents(PyValue[] notificationArgs) =>
        Assert.IsType<PyTuple>(Assert.Single(notificationArgs)).Items.ToArray();

    private static void AssertHackingEvent(PyValue item, int eventId, Action<PyValue> assertData)
    {
        Assert.Equal(new PyInt(eventId), WireField(item, "eventID"));
        assertData(WireField(item, "eventData"));
    }
}
