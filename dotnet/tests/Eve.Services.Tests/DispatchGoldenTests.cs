using System.Text.Json.Nodes;
using Eve.Marshal;

namespace Eve.Services.Tests;

/// <summary>
/// Replays oracle/fixtures/services/dispatch.json: request packets recorded from the reference
/// server go into the C# dispatcher and the response packets must match byte for byte, and leave the
/// same bound-object state behind.
/// </summary>
public class DispatchGoldenTests
{
    private const long FixedNowMs = 1_700_000_000_123;

    private static readonly JsonArray Scenarios = (JsonArray)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "services", "dispatch.json")))!;

    public static IEnumerable<object[]> Ids() => Scenarios.Select(s => new object[] { (string)s!["id"]! });

    private sealed class Capture : IPacketSink
    {
        public List<string> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(Convert.ToHexStringLower(MarshalCodec.Encode(packet)));
    }

    [Fact]
    public void The_recording_covers_the_dispatch_paths()
    {
        Assert.True(Scenarios.Count >= 20);
        Assert.True(Scenarios.Sum(s => ((JsonArray)s!["steps"]!).Count) >= 70);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Every_recorded_step_gets_the_recorded_response_and_leaves_the_recorded_state(string id)
    {
        var scenario = (JsonObject)Scenarios.Single(s => (string)s!["id"]! == id)!;
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 77, UserId = 90000001 };
        var registry = new ServiceRegistry();
        Eve.Services.Generated.Eve_Services_TestsServiceCatalog.Register(registry, new EmptyProvider());
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs);
        var problems = new List<string>();
        dispatcher.OnProblem = (message, _) => problems.Add(message);

        foreach (var step in (JsonArray)scenario["steps"]!)
        {
            var note = (string)step!["note"]!;
            capture.Sent.Clear();
            var packet = MarshalCodec.Decode(Convert.FromHexString((string)step["packet"]!));
            await dispatcher.DispatchAsync(packet, session);

            var expected = ((JsonArray)step["responses"]!).Select(r => (string)r!).ToArray();
            Assert.True(
                expected.SequenceEqual(capture.Sent),
                $"{id} / {note}: responses differ\n  expected {string.Join(" | ", expected.Select(Clip))}\n  actual   {string.Join(" | ", capture.Sent.Select(Clip))}");

            var state = (JsonObject)step["state"]!;
            var bound = ((JsonObject)state["boundObjects"]!).ToDictionary(
                pair => pair.Key,
                pair => ((string)pair.Value!["service"]!, (string?)pair.Value["refID"]));
            var actual = registry.BoundObjects.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.Service, pair.Value.RefId?.ToString()));
            Assert.Equal(bound.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
            Assert.Equal((string?)state["currentBoundObjectID"], session.CurrentBoundObjectId);
            Assert.Equal((string?)state["lastBoundObjectID"], session.LastBoundObjectId);
        }
    }

    private static string Clip(string text) => text.Length > 120 ? text[..120] + "…" : text;

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
