using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Host.ProjectDiscovery;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class ScientificReplayWireTests
{
    private sealed class Sink : IPacketSink { public List<PyValue> Packets { get; } = []; public void Send(PyValue value) => Packets.Add(value); }
    private static PyValue Request(int id, string method, params PyValue[] args)
    {
        var from = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(7), new PyInt(id), PyNone.Instance]);
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple(args), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[] { new PyInt(6), from, MachoAddress.Any("ProjectDiscovery").Encode(), new PyInt(1001), payload, PyNone.Instance };
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14-head.Length)]));
    }
    [Fact]
    public async Task RegisteredEncodedRpcServesRealImageAndPersistsScientificAnswer()
    {
        string schema = "pd_wire_" + Guid.NewGuid().ToString("N")[..12], pinSchema = schema + "_pins", characterSchema = schema + "_characters";
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var options = Options();
        try
        {
            await using (var setup = source.CreateCommand($"""
                CREATE SCHEMA {pinSchema};
                CREATE TABLE {pinSchema}.project_discovery_task_pins(character_id bigint PRIMARY KEY,task_id bigint NOT NULL,updated_ms bigint NOT NULL);
                CREATE SCHEMA {characterSchema};
                CREATE TABLE {characterSchema}.project_discovery_progress(character_id bigint PRIMARY KEY,experience bigint NOT NULL DEFAULT 0,analysis_kredits bigint NOT NULL DEFAULT 0);
                CREATE TABLE {characterSchema}.project_discovery_tutorials(character_id bigint PRIMARY KEY,finished boolean NOT NULL DEFAULT false,tutorial_level integer NOT NULL DEFAULT 0,updated_ms bigint NOT NULL DEFAULT 0);
                """)) await setup.ExecuteNonQueryAsync();
            var pins = new ProjectDiscoveryTaskPinStore(source,pinSchema);
            Assert.Null(await ScientificReplayComposition.StartAsync("disabled", source, pins, options, schema));
            await using var host = (await ScientificReplayComposition.StartAsync("scientific-replay", source, pins, ScientificReplayOptions.Bundled, schema, characterSchema))!;
            var callbacks = host.Attach(new GameRpcCallbacks());
            var registry = new ServiceRegistry();
            registry.Register(new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
                ReadPlayerStateAsync: callbacks.ReadProjectDiscoveryState,
                ReadBonusSamplesAsync: callbacks.ReadProjectDiscoveryBonusSamples,
                NotifyStateAfterSubmit: callbacks.NotifyProjectDiscoveryStateAfterSubmit,
                GetTaskAsync: callbacks.GetProjectDiscoveryTask,
                SubmitClassificationAsync: callbacks.SubmitProjectDiscoveryClassification,
                ReadPlayerStatisticsAsync: callbacks.ReadProjectDiscoveryStatistics)));
            var dispatcher = new PacketDispatcher(registry) { StrictRpc = true };
            var sink = new Sink(); var session = new ClientSession(sink) { CharacterId = 1001, UserId = 2001, SessionId = 7 };
            async Task<PyValue> Call(int id, string method, params PyValue[] args)
            {
                sink.Packets.Clear(); await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(Request(id,method,args))),session);
                var packet = Assert.Single(sink.Packets, value => MachoPacket.Decode(value)?.Type == MachoMessage.CallRsp); var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(packet))).Args).Items;
                Assert.True(Assert.IsType<PyInt>(state[0]).Value == MachoMessage.CallRsp, System.Text.Encoding.Latin1.GetString(MarshalCodec.Encode(packet)));
                var payload = Assert.IsType<PyTuple>(state[4]);
                return payload.Items[0] is PySubStream { Decoded: { } value } ? value : payload.Items[0];
            }
            PyValue Field(PyValue value, string key) => Assert.Single(Assert.IsType<PyDict>(value).Entries, x => JsSemantics.StringValue(x.Key) == key).Value;
            var task = await Call(1,"get_new_task");
            string taskId = JsSemantics.StringValue(Field(task, "taskID"))!;
            Assert.Equal(new PyBytes(System.Text.Encoding.UTF8.GetBytes("mcgill-polyseg")), Field(task, "projectID"));
            Assert.Equal(PyBool.True, Field(task, "isTrainingSet"));
            Assert.Equal(new PyBytes(System.Text.Encoding.UTF8.GetBytes("Public research replay; not live MMOS")), Field(task, "localMode"));
            var assets = Field(task,"assets");
            string url = JsSemantics.StringValue(Field(assets,"url")) ?? throw new InvalidDataException("Missing asset URL.");
            using var http = new HttpClient(); byte[] png = await http.GetByteArrayAsync(url);
            Assert.True(png.AsSpan(0,8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(options.CohortPath));
            var polygons = json.RootElement[0].GetProperty("polygons").EnumerateArray().Select(poly => (PyValue)Dict(("vertices", new PyList(poly.EnumerateArray().Select(point => (PyValue)new PyList([new PyReal(point[0].GetDouble()),new PyReal(point[1].GetDouble())])).ToArray())))).ToArray();
            var result = Dict(("skip",PyBool.False),("polygons",new PyList(polygons)));
            var timing = Dict(("t",new PyInt(10)),("tPolygons",new PyList([])));
            var response = await Call(2,"post_classification",result,timing);
            var score = Assert.IsType<PyReal>(Field(Field(response,"player"),"score")).Value;
            Assert.Equal(.99, score);
            Assert.Equal(PyBool.True, Field(response, "isSolved"));
            Assert.InRange(Math.Abs(.99115 - Assert.IsType<PyReal>(Field(response, "score")).Value), 0, 1e-10);
            Assert.Equal(1L, Assert.IsType<PyLong>(Field(response, "classificationCount")).Value);
            var stateInResult = Assert.IsType<PyObject>(Field(response, "playerState"));
            Assert.Equal("util.KeyVal", JsSemantics.StringValue(stateInResult.Name));
            Assert.Equal(new PyLong(5), Field(stateInResult.Args, "analysisKredits"));
            var resultTask = Field(response, "task");
            Assert.Equal(new PyBytes(System.Text.Encoding.UTF8.GetBytes("mcgill-polyseg")), Field(resultTask, "projectID"));
            Assert.Equal(taskId, JsSemantics.StringValue(Field(resultTask, "taskID")));
            var taskVotes = Field(resultTask, "votes");
            var highScore = Assert.IsType<PyReal>(Field(taskVotes, "highScore")).Value;
            var averageScore = Assert.IsType<PyReal>(Field(taskVotes, "avgScore")).Value;
            Assert.InRange(highScore, 0, 1);
            Assert.InRange(averageScore, 0, highScore);
            var solutionClusters = Assert.IsType<PyList>(Field(Field(resultTask, "solution"), "clusters")).Items;
            Assert.Equal(2, solutionClusters.Count);
            foreach (var cluster in solutionClusters)
            {
                var solutionPolygons = Assert.IsType<PyList>(Field(cluster, "polygons")).Items;
                Assert.NotEmpty(solutionPolygons);
                Assert.IsType<PyList>(Field(Assert.Single(solutionPolygons), "vertices"));
            }
            var replay = await Call(2,"post_classification",result,timing);
            Assert.Equal(score,Assert.IsType<PyReal>(Field(Field(replay,"player"),"score")).Value);
            // A new listener has a new URL, but the same content revision and durable call result.
            await using var restarted = (await ScientificReplayComposition.StartAsync("scientific-replay", source, pins, ScientificReplayOptions.Bundled, schema, characterSchema))!;
            var nextCallbacks = restarted.Attach(new GameRpcCallbacks());
            var nextRegistry = new ServiceRegistry();
            nextRegistry.Register(new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
                ReadPlayerStateAsync: nextCallbacks.ReadProjectDiscoveryState,
                ReadBonusSamplesAsync: nextCallbacks.ReadProjectDiscoveryBonusSamples,
                NotifyStateAfterSubmit: nextCallbacks.NotifyProjectDiscoveryStateAfterSubmit,
                GetTaskAsync: nextCallbacks.GetProjectDiscoveryTask,
                SubmitClassificationAsync: nextCallbacks.SubmitProjectDiscoveryClassification,
                ReadPlayerStatisticsAsync: nextCallbacks.ReadProjectDiscoveryStatistics)));
            dispatcher = new PacketDispatcher(nextRegistry) { StrictRpc = true };
            var statistics = await Call(5,"get_player_statistics");
            Assert.Equal(score, Assert.IsType<PyReal>(Field(Field(statistics,"project"),"score")).Value);
            var afterRestart = await Call(2,"post_classification",result,timing);
            Assert.Equal(score,Assert.IsType<PyReal>(Field(Field(afterRestart,"player"),"score")).Value);
            // A fresh session preserves completed state rather than silently reassigning or double scoring.
            var freshSink = new Sink(); var fresh = new ClientSession(freshSink) { CharacterId = 1001, UserId = 2001, SessionId = 8 };
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(Request(3,"get_new_task"))),fresh);
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(freshSink.Packets))).Type);
            var otherSink = new Sink(); var other = new ClientSession(otherSink) { CharacterId = 1002, UserId = 2002, SessionId = 9 };
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(Request(4,"get_new_task"))),other);
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(otherSink.Packets))).Type);
        }
        finally { await using var clean = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE; DROP SCHEMA IF EXISTS {pinSchema} CASCADE; DROP SCHEMA IF EXISTS {characterSchema} CASCADE"); await clean.ExecuteNonQueryAsync(); }
    }
    private static PyDict Dict(params (string Key, PyValue Value)[] entries) => new(entries.Select(x => new KeyValuePair<PyValue, PyValue>(new PyStr(x.Key), x.Value)).ToArray());
    private static ScientificReplayOptions Options()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory); string? root = null;
        while (dir is not null) { var path = Path.Combine(dir.FullName,"dotnet","tests","Eve.Host.Tests","fixtures","project-discovery"); if (Directory.Exists(path)) { root = path; break; } dir = dir.Parent; }
        if (root is null) throw new DirectoryNotFoundException();
        return new ScientificReplayOptions { DatasetKey = "frdr-1038-analysis-50", CsvGzipPath = Path.Combine(root,"covid-analysis50.csv.gz"), CsvGzipSha256 = "a458f60d4e299a8c5ca31626452be1f46e82bc2b05fde6611fae3ab87e905425", CohortPath = Path.Combine(root,"covid-analysis50-gates.json"), CohortSha256 = "9810f2bb0c77eb4f8f17e7ddb624f12a0f2218b94f690d8b6e1833fe23ff1539", MinX = 23.201007270813, MinY = -30.203549385070783, MaxX = 128.337999916077, MaxY = 196.826071739197 };
    }
}
