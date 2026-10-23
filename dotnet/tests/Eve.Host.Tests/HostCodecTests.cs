using System.Text.Json.Nodes;

namespace Eve.Host.Tests;

/// <summary>
/// Replays oracle/fixtures/hostlink/messages.json, the golden frames of docs/design/adr/0002 (the Rust front
/// replays the same file): every valid frame encodes to its bytes and decodes back, every invalid one is
/// refused for the stated reason.
/// </summary>
public class HostCodecTests
{
    private static readonly JsonObject Fixtures = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "hostlink", "messages.json")))!;

    public static IEnumerable<object[]> Valid() => ((JsonArray)Fixtures["valid"]!).Select(c => new object[] { (string)c!["name"]! });

    public static IEnumerable<object[]> Invalid() => ((JsonArray)Fixtures["invalid"]!).Select(c => new object[] { (string)c!["name"]! });

    private static JsonObject Case(string section, string name) =>
        (JsonObject)((JsonArray)Fixtures[section]!).Single(c => (string)c!["name"]! == name)!;

    private static byte[] Hex(string text) => Convert.FromHexString(text);

    private static HostMessage MessageOf(JsonObject c)
    {
        uint U32(string field) => (uint)c[field]!.GetValue<long>();
        ulong U64(string field) => ulong.Parse((string)c[field]!, System.Globalization.CultureInfo.InvariantCulture);
        string Text(string field) => (string)c[field]!;
        return (string)c["kind"]! switch
        {
            "Hello" => new Hello((ushort)U32("version"), U32("nodeId")),
            "HelloAck" => new HelloAck((ushort)U32("version")),
            "AuthRequest" => new AuthRequest(U32("corr"),
                Text("credentialKind") switch
                {
                    "Password" => AuthCredentialKind.Password,
                    "Sso" => AuthCredentialKind.Sso,
                    var other => throw new InvalidOperationException(other),
                },
                Text("userName"), Hex(Text("passwordHash")), Text("ssoToken") is { Length: > 0 } token ? token : null,
                Text("language"), Text("address")),
            "AuthReply" => new AuthReply(U32("corr"), c["ok"]!.GetValue<bool>()
                ? new Accepted(U32("userId"), U64("role"), Text("countryCode"))
                : new Rejected(Text("reason"))),
            "SessionOpen" => new SessionOpen(U64("sessionId"), U64("clientId"), U32("userId"), U64("role"), Text("address"), Text("language"), Text("countryCode")),
            "SessionClose" => new SessionClose(U64("sessionId")),
            "Packet" => new Packet(U64("sessionId"), Hex(Text("payload"))),
            "Send" => new Send(U64("sessionId"), Hex(Text("payload"))),
            "Disconnect" => new Disconnect(U64("sessionId"), Text("reason")),
            "ClusterConfig" => new ClusterConfig(Hex(Text("payload"))),
            var other => throw new InvalidOperationException(other),
        };
    }

    /// <summary>Records compare byte-string members by reference; compare the wire form instead.</summary>
    private static void AssertSame(HostMessage expected, HostMessage actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(Convert.ToHexString(HostCodec.Encode(expected)), Convert.ToHexString(HostCodec.Encode(actual)));
    }

    [Fact]
    public void The_fixtures_cover_every_message_kind()
    {
        var kinds = ((JsonArray)Fixtures["valid"]!).Select(c => (string)c!["kind"]!).Distinct().Order().ToArray();
        Assert.Equal(["AuthReply", "AuthRequest", "ClusterConfig", "Disconnect", "Hello", "HelloAck", "Packet", "Send", "SessionClose", "SessionOpen"], kinds);
        Assert.True(((JsonArray)Fixtures["invalid"]!).Count >= 9);
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void A_valid_frame_encodes_to_its_bytes_and_decodes_back(string name)
    {
        var c = Case("valid", name);
        var expected = Hex((string)c["hex"]!);
        var message = MessageOf(c);

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(HostCodec.Encode(message)));
        AssertSame(message, HostCodec.Decode(expected.AsMemory(4)));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_frame_is_refused_for_its_reason(string name)
    {
        var c = Case("invalid", name);
        var raw = Hex((string)c["hex"]!);
        var error = Assert.Throws<HostLinkException>(() => HostCodec.Decode(raw.AsMemory(4)));
        var expected = (string)c["reason"]! switch
        {
            "empty" => HostLinkError.Empty,
            "unknown-kind" => HostLinkError.UnknownKind,
            "truncated" => HostLinkError.Truncated,
            "trailing" => HostLinkError.Trailing,
            "utf8" => HostLinkError.Utf8,
            "bad-status" => HostLinkError.BadStatus,
            "bad-credential-kind" => HostLinkError.BadCredentialKind,
            var other => throw new InvalidOperationException(other),
        };
        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public void A_decoder_never_reads_past_the_payload_it_was_given()
    {
        for (var length = 0; length < 40; length++)
        {
            var payload = Enumerable.Range(0, length).Select(i => (byte)((i * 37) + 3)).ToArray();
            try
            {
                HostCodec.Decode(payload);
            }
            catch (HostLinkException)
            {
            }
        }
    }

    [Fact]
    public void Text_longer_than_a_length_field_is_refused_when_encoding()
    {
        Assert.Throws<ArgumentException>(() => HostCodec.Encode(new Disconnect(1, new string('x', 70_000))));
    }
}
