using System.Globalization;
using System.Text.Json.Nodes;

namespace Eve.Space.Tests;

/// <summary>
/// Replays oracle/fixtures/simlink/messages.json, the golden frames of docs/adr/0007 (the Rust simulation replays the same
/// file): every valid frame encodes to its bytes and decodes back, every invalid one is refused for the stated reason.
/// </summary>
public class SimCodecTests
{
    private static readonly JsonObject Fixtures = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "simlink", "messages.json")))!;

    public static IEnumerable<object[]> Valid() => ((JsonArray)Fixtures["valid"]!).Select(c => new object[] { (string)c!["name"]! });

    public static IEnumerable<object[]> Invalid() => ((JsonArray)Fixtures["invalid"]!).Select(c => new object[] { (string)c!["name"]! });

    private static JsonObject Case(string section, string name) =>
        (JsonObject)((JsonArray)Fixtures[section]!).Single(c => (string)c!["name"]! == name)!;

    private static byte[] Hex(string text) => Convert.FromHexString(text);

    private static long Wide(JsonNode node, string field) => long.Parse((string)node[field]!, CultureInfo.InvariantCulture);

    private static SimShip ShipOf(JsonNode s) => new(
        Wide(s, "shipId"), Wide(s, "characterId"), (int)s["typeId"]!, (int)s["groupId"]!, (int)s["categoryId"]!, (string)s["name"]!, Wide(s, "ownerId"),
        (int)s["corporationId"]!, (int)s["allianceId"]!, (int)s["warFactionId"]!, (double)s["securityStatus"]!, (double)s["radius"]!, (double)s["mass"]!,
        (double)s["maxVelocity"]!, (double)s["agility"]!, (double)s["warpSpeedAu"]!, (double)s["shieldRechargeMs"]!,
        [.. ((JsonArray)s["modules"]!).Select(m => new SimModule(Wide(m!, "itemId"), (int)m!["typeId"]!, (int)m["flag"]!))]);

    private static SimMessage MessageOf(JsonObject c)
    {
        ulong Session() => (ulong)Wide(c, "sessionId");
        return (string)c["kind"]! switch
        {
            "Hello" => new SimHello((ushort)(int)c["version"]!),
            "HelloAck" => new SimHelloAck((ushort)(int)c["version"]!, (uint)(long)c["pollHz"]!),
            "Enter" => new SimEnter(Session(), (int)c["solarSystemId"]!, (EnterHow)(int)c["how"]!, Wide(c, "stationId"), ShipOf(c["ship"]!)),
            "EnterReply" => new SimEnterReply(Session(), (bool)c["ok"]! ? null : (string)c["reason"]!),
            "Leave" => new SimLeave(Session(), (LeaveWhy)(int)c["why"]!),
            "Bind" => new SimBind(Session()),
            "UpdateState" => new SimUpdateState(Session()),
            "Command" => new SimCommand(Session(), (byte)(int)c["command"]!, [.. ((JsonArray)c["args"]!).Select(a => (double)a!)]),
            "ShipSpec" => new SimShipChanged(Session(), ShipOf(c["ship"]!)),
            "Notify" => new SimNotify(Session(), (string)c["notification"]!, (string)c["idType"]!, Hex((string)c["args"]!)),
            "Docked" => new SimDocked(Session(), Wide(c, "stationId")),
            "Refused" => new SimRefused(Session(), (byte)(int)c["command"]!, (string)c["reason"]!),
            "Accepted" => new SimAccepted(Session(), (byte)(int)c["command"]!, Wide(c, "at")),
            "Approaching" => new SimApproaching(Session(), (byte)(int)c["command"]!, (double)c["distance"]!),
            var other => throw new InvalidOperationException(other),
        };
    }

    [Fact]
    public void The_fixtures_cover_every_message_kind()
    {
        var kinds = ((JsonArray)Fixtures["valid"]!).Select(c => (string)c!["kind"]!).Distinct().Order().ToArray();
        Assert.Equal(["Accepted", "Approaching", "Bind", "Command", "Docked", "Enter", "EnterReply", "Hello", "HelloAck", "Leave", "Notify", "Refused", "ShipSpec", "UpdateState"], kinds);
        Assert.True(((JsonArray)Fixtures["invalid"]!).Count >= 10);
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void A_valid_frame_encodes_to_its_bytes_and_decodes_back(string name)
    {
        var c = Case("valid", name);
        var expected = Hex((string)c["hex"]!);
        var message = MessageOf(c);

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(SimCodec.Encode(message)));

        var decoded = SimCodec.Decode(expected.AsMemory(4));
        Assert.Equal(message.GetType(), decoded.GetType());
        // records compare their lists and byte strings by reference: the wire form says whether they are the same
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(SimCodec.Encode(decoded)));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_frame_is_refused_for_its_reason(string name)
    {
        var c = Case("invalid", name);
        var frame = Hex((string)c["hex"]!);
        var error = Assert.Throws<SimLinkException>(() => SimCodec.Decode(frame.AsMemory(4)));
        var reason = (string)c["reason"]!;
        Assert.Equal(reason, error.Error switch
        {
            SimLinkError.Empty => "empty",
            SimLinkError.UnknownKind => "unknown-kind",
            SimLinkError.Truncated => "truncated",
            SimLinkError.Trailing => "trailing",
            SimLinkError.Utf8 => "utf8",
            SimLinkError.BadValue => "bad-value",
            var other => other.ToString(),
        });
    }

    [Fact]
    public void A_decoded_enter_says_what_was_encoded()
    {
        var c = Case("valid", "enter by undocking");
        var enter = Assert.IsType<SimEnter>(SimCodec.Decode(Hex((string)c["hex"]!).AsMemory(4)));
        Assert.Equal((1790646377000015UL, 30100032, EnterHow.Undock, 60015249L), (enter.SessionId, enter.SolarSystemId, enter.How, enter.StationId));
        Assert.Equal(("Ibis", 9988400001939L, 339.25), (enter.Ship.Name, enter.Ship.ShipId, enter.Ship.MaxVelocity));
        Assert.Equal([(9988400001940L, 21857, 19), (9988400001941L, 3638, 27)], enter.Ship.Modules.Select(m => (m.ItemId, m.TypeId, m.Flag)));
    }

    [Fact]
    public void A_module_count_the_frame_cannot_hold_is_refused_without_allocating_for_it()
    {
        var good = SimCodec.Encode(MessageOf(Case("valid", "enter by undocking")));
        // the count is the four bytes before the two 16-byte modules at the end of the frame
        var at = good.Length - (2 * 16) - 4;
        var forged = (byte[])good.Clone();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(forged, at);
        var error = Assert.Throws<SimLinkException>(() => SimCodec.Decode(forged.AsMemory(4)));
        Assert.Equal(SimLinkError.Truncated, error.Error);
    }

    [Fact]
    public void Text_longer_than_a_length_field_is_refused_on_the_way_out()
    {
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimRefused(1, 4, new string('x', 70_000))));
    }
}
