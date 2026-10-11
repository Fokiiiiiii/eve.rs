using System.Globalization;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Recorded answers this server does not give by design. The reference answers Omega after 2100 through its constant fallback
    /// (subscriptionMgrService.js:66-68: no licence and the default on means Omega). Here a stored end time in the past is Alpha;
    /// OmegaStateTests pins that behaviour directly.
    /// </summary>
    private static readonly HashSet<string> IntentionalDeviations = ["subscription:after-default-expiry"];

    public static IEnumerable<object[]> Ids() => Scenarios
        .Select(s => (string)s!["id"]!)
        .Where(id => !IntentionalDeviations.Contains(id))
        .Select(id => new object[] { id });

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

    /// <summary>
    /// The recorded setup as a store. The recording was made from the reference, which floors every account at 2100 while the
    /// Omega default is on (subscriptionMgrService.js:61-68, storeState.js:618-622) and reads the EVE_CLONE_GRADE override
    /// with parseInt (subscriptionMgrService.js:33-52). The replay reproduces that configuration; production has neither the
    /// floor nor the override (an operator's earlier end time lapses the account), so this helper is test-only.
    /// </summary>
    private sealed class RecordedAccounts(JsonObject accounts, string? cloneGrade, bool omegaEnabled) : IOmegaAccountStore
    {
        private readonly FakeAccounts _runtime = new(accounts);

        public string? GetOmegaExpiry(long accountId)
        {
            var forced = ForcedGrade(cloneGrade);
            if (forced == false)
            {
                return null;
            }
            var floor = forced == true || (forced is null && omegaEnabled) ? OmegaLicenses.DefaultOmegaExpiryFileTime : 0L;
            var text = _runtime.GetOmegaExpiry(accountId);
            if (floor == 0)
            {
                return text; // production parses the stored text itself (BigInt semantics)
            }
            var runtime = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored) ? stored : 0L;
            return Math.Max(runtime, floor).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The recorded override: true Omega, false Alpha, null when absent or not 0/1 (the config then decides).</summary>
        private static bool? ForcedGrade(string? raw)
        {
            var value = raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            switch (value)
            {
                case "omega":
                    return true;
                case "alpha" or "trial":
                    return false;
            }
            var leading = Regex.Match(value, @"^[+-]?\d+");
            return leading.Success && long.TryParse(leading.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) && parsed is 0 or 1
                ? parsed == 1
                : null;
        }
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
    [InlineData("alpha", 0)]
    [InlineData("omega", 1)]
    public async Task Login_reward_facilities_returns_the_existing_clone_grade(string cloneGrade, long expected)
    {
        // The grade follows the account's stored end time: an Omega account has the policy end time, an Alpha one has none.
        var accounts = new FakeAccounts(new JsonObject
        {
            ["90000001"] = new JsonObject { ["omegaExpiryFileTime"] = cloneGrade == "omega" ? OmegaLicenses.DefaultOmegaExpiryText : "" },
        });
        var nowUnixMs = 1_700_000_000_000L;
        IService service = CoreServices.Create(accounts, () => nowUnixMs)
            .Single(candidate => candidate.Name == "loginRewardFacilities");
        var session = new ClientSession();
        session.Set("userid", new PyLong(90000001));

        Assert.Equal("loginRewardFacilities", service.Name);
        Assert.True(service.HasMethod("get_my_clone_grade"));
        var result = await service.CallAsync(new ServiceCall
        {
            Method = "get_my_clone_grade",
            Args = [],
            Session = session,
        });

        Assert.Equal(new PyInt(expected), result);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task The_csharp_services_answer_like_the_recorded_javascript_ones(string id)
    {
        var scenario = (JsonObject)Scenarios.Single(s => (string)s!["id"]! == id)!;
        var setup = (JsonObject)scenario["setup"]!;
        var nowMs = (long)setup["nowMs"]!;

        // The recording's configuration (a forced grade, or the Omega default switch) read as stored state.
        var licenses = new OmegaLicenses(
            new RecordedAccounts((JsonObject)setup["accounts"]!, (string?)setup["cloneGrade"], (bool)setup["omegaEnabled"]!),
            () => nowMs);

        var registry = new ServiceRegistry();
        registry.Register((IService)new PingService());
        registry.Register((IService)new BrowserLockdownService());
        registry.Register((IService)new SubscriptionMgrService(licenses));

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
