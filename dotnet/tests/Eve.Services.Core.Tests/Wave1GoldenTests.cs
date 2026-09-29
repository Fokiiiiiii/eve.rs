using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core.Tests;

/// <summary>
/// Replays oracle/fixtures/services/wave1.json: the real JavaScript services ran behind the real
/// dispatcher; the C# services must answer the same request packets with the same response bytes.
/// </summary>
public class Wave1GoldenTests
{
    private static readonly JsonArray Scenarios = (JsonArray)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "services", "wave1.json")))!;

    public static IEnumerable<object[]> Ids() => Scenarios.Select(s => new object[] { (string)s!["id"]! });

    private sealed class Capture : IPacketSink
    {
        public List<string> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(Convert.ToHexStringLower(MarshalCodec.Encode(packet)));
    }

    private sealed class FakeAccounts(JsonObject accounts) : IOmegaAccountStore
    {
        // normalizeRuntimeAccount: only a non-blank string counts, trimmed.
        public string? GetOmegaExpiry(long accountId) =>
            accounts[accountId.ToString()] is JsonObject account
            && account["omegaExpiryFileTime"] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text.Trim().Length > 0
                ? text.Trim()
                : null;
    }

    /// <summary>A session attribute from its JSON form: numbers, strings, {"$big": "..."} and null.</summary>
    private static PyValue? SessionValue(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return PyNone.Instance;
            case JsonObject obj when obj.ContainsKey("$big"):
                return new PyLong(long.Parse((string)obj["$big"]!, System.Globalization.CultureInfo.InvariantCulture));
            case JsonValue value:
                var element = value.GetValue<JsonElement>();
                return element.ValueKind switch
                {
                    JsonValueKind.String => new PyStr(element.GetString()!),
                    JsonValueKind.Number when element.TryGetInt64(out var integer) => new PyInt(integer),
                    JsonValueKind.Number => new PyReal(element.GetDouble()),
                    _ => PyNone.Instance,
                };
            default:
                throw new InvalidDataException("unexpected session value");
        }
    }

    [Fact]
    public void The_recording_covers_the_services_and_their_configurations()
    {
        Assert.True(Scenarios.Count >= 40);
        var services = Scenarios.SelectMany(s => ((JsonArray)s!["steps"]!).Select(step => (string)step!["method"]!)).Distinct().ToList();
        Assert.Contains("Ping", services);
        Assert.Contains("GetFlaggedSitesList", services);
        Assert.Contains("GetSubscriptionInfo", services);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task The_csharp_services_answer_like_the_recorded_javascript_ones(string id)
    {
        var scenario = (JsonObject)Scenarios.Single(s => (string)s!["id"]! == id)!;
        var setup = (JsonObject)scenario["setup"]!;
        var nowMs = (long)setup["nowMs"]!;

        var settings = new SubscriptionSettings
        {
            CloneGradeOverride = (string?)setup["cloneGrade"],
            OmegaLicenseEnabled = (bool)setup["omegaEnabled"]!,
        };
        var licenses = new OmegaLicenses(settings, new FakeAccounts((JsonObject)setup["accounts"]!), () => nowMs);

        var registry = new ServiceRegistry();
        registry.Register((IService)new PingService());
        registry.Register((IService)new BrowserLockdownService());
        registry.Register((IService)new SubscriptionMgrService(settings, licenses));

        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 77 };
        foreach (var (name, node) in (JsonObject)setup["session"]!)
        {
            session.Set(name, SessionValue(node)!);
        }

        var dispatcher = new PacketDispatcher(registry, () => nowMs);
        foreach (var step in (JsonArray)scenario["steps"]!)
        {
            capture.Sent.Clear();
            var method = (string)step!["method"]!;
            await dispatcher.DispatchAsync(MarshalCodec.Decode(Convert.FromHexString((string)step["packet"]!)), session);
            var expected = ((JsonArray)step["responses"]!).Select(r => (string)r!).ToArray();
            Assert.True(
                expected.SequenceEqual(capture.Sent),
                $"{id} / {method}: responses differ\n  expected {string.Join(" | ", expected)}\n  actual   {string.Join(" | ", capture.Sent)}");
        }
    }
}
