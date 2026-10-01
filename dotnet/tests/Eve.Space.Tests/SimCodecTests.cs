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
        (double)s["maxTargetRange"]!, (double)s["scanResolution"]!, (double)s["signatureRadius"]!, (int)s["maxLockedTargets"]!, (double)s["capacitorCapacity"]!, (double)s["capacitorRechargeS"]!,
        [.. ((JsonArray)s["modules"]!).Select(m => new SimModule(Wide(m!, "itemId"), (int)m!["typeId"]!, (int)m["flag"]!))],
        VitalsOf(s["vitals"]!), ConditionOf(s["condition"]!));

    private static double[] Four(JsonNode n) => [.. ((JsonArray)n).Select(x => (double)x!)];

    private static SimVitals VitalsOf(JsonNode v) => new(
        (double)v["shieldCapacity"]!, (double)v["armorHp"]!, (double)v["structureHp"]!,
        new SimResonances(Four(v["resonances"]!["shield"]!), Four(v["resonances"]!["armor"]!), Four(v["resonances"]!["structure"]!)));

    private static SimCondition ConditionOf(JsonNode c) => new((double)c["damage"]!, (double)c["armorDamage"]!, (double)c["shieldCharge"]!, (double)c["charge"]!);

    private static SimNumbers? NumbersOf(JsonNode? n) => n is null ? null : new SimNumbers((double)n["mass"]!, (double)n["maxVelocity"]!, (double)n["signatureRadius"]!);

    private static SimChange[] ChangesOf(JsonNode n) => [.. ((JsonArray)n).Select(c => new SimChange(Wide(c!, "itemId"), (int)c!["attributeId"]!, (double)c["value"]!))];

    private static SimEffect EffectOf(JsonNode e) => new(
        Wide(e, "moduleId"), (int)e["moduleType"]!, (int)e["effectId"]!, (string)e["fx"]!, (double)e["durationMs"]!, (double)e["capacitorNeed"]!, (int)e["repeat"]!, Wide(e, "targetId"),
        NumbersOf(e["on"]), NumbersOf(e["off"]), ChangesOf(e["onChanges"]!), ChangesOf(e["offChanges"]!), e["turret"] is { } turret ? TurretOf(turret) : null);

    private static SimTurret TurretOf(JsonNode t) => new(
        (int)t["weaponTypeId"]!, (string)t["fx"]!, VectorOf(t["rawDamage"]!), (double)t["optimalRange"]!, (double)t["falloff"]!, (double)t["trackingSpeed"]!,
        (double)t["signatureResolution"]!, ChargeOf(t["charge"]));

    private static SimTurretCharge? ChargeOf(JsonNode? c) => c is null ? null : new SimTurretCharge((int)c["typeId"]!, (int)c["flag"]!, (long)c["rounds"]!, (long)c["perCycle"]!);

    private static double[] VectorOf(JsonNode n) => [.. ((JsonArray)n).Select(x => (double)x!)];

    private static SimNpcIdentity IdentityOf(JsonNode i) => new(
        (long)i["hostileResponseThreshold"]!, (long)i["friendlyResponseThreshold"]!, (double)i["aggressionRange"]!,
        [.. ((JsonArray)i["weapons"]!).Select(w => new SimNpcWeapon(
            Wide(w!, "moduleId"), (int)w!["effectId"]!, (double)w["cycleMs"]!, (double)w["capacitorNeed"]!, (int)w["weaponTypeId"]!, (string)w["fx"]!,
            VectorOf(w["rawDamage"]!), (double)w["optimalRange"]!, (double)w["falloff"]!, (double)w["trackingSpeed"]!, (double)w["signatureResolution"]!, ChargeOf(w["charge"])))],
        WreckKindOf(i["wreck"]),
        i["hull"] is { } hull && (bool)hull);

    private static SimWreckKind? WreckKindOf(JsonNode? w) => w is null ? null : new SimWreckKind((int)w["typeId"]!, (string)w["name"]!, (int)w["graphicId"]!);

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
            "Docked" => new SimDocked(Session(), Wide(c, "stationId"), ConditionOf(c["condition"]!)),
            "Condition" => new SimConditionReply(Session(), (byte)(int)c["command"]!, ConditionOf(c["condition"]!)),
            "Refused" => new SimRefused(Session(), (byte)(int)c["command"]!, (string)c["reason"]!),
            "Accepted" => new SimAccepted(Session(), (byte)(int)c["command"]!, Wide(c, "at")),
            "Approaching" => new SimApproaching(Session(), (byte)(int)c["command"]!, (double)c["distance"]!),
            "Jumping" => new SimJumping(Session(), (byte)(int)c["command"]!, (int)c["destinationSystem"]!, Wide(c, "destinationGate")),
            "Jumped" => new SimJumped(Session(), (int)c["fromSystem"]!, Wide(c, "fromGate"), (int)c["toSystem"]!, Wide(c, "toGate")),
            "Activate" => new SimActivate(Session(), EffectOf(c["effect"]!)),
            "ChargeSpent" => new SimChargeSpent(Session(), Wide(c, "moduleId"), Wide(c, "roundsLeft")),
            "Targets" => new SimTargets(Session(), (byte)(int)c["command"]!, (bool)c["pending"]!, [.. ((JsonArray)c["targets"]!).Select(x => long.Parse((string)x!, CultureInfo.InvariantCulture))]),
            "TargetRefused" => new SimTargetRefused(Session(), (byte)(int)c["command"]!, (string)c["reason"]!, (int)c["targetType"]!, (double)c["desiredRange"]!, (double)c["actualDistance"]!),
            "Destroyed" => new SimDestroyed(Session(), Wide(c, "shipId"), Wide(c, "wreckId"), VectorOf(c["position"]!), VectorOf(c["direction"]!), (double)c["radius"]!, Wide(c, "killerShipId"), WreckKindOf(c["wreckKind"])),
            "Respawn" => new SimRespawn(Session(), ShipOf(c["ship"]!), VectorOf(c["position"]!), VectorOf(c["direction"]!)),
            "PlaceWreck" => new SimPlaceWreck(
                (int)c["solarSystemId"]!, Wide(c, "wreckId"), (int)c["groupId"]!, WreckKindOf(c["wreck"])!, VectorOf(c["position"]!), VectorOf(c["direction"]!), (double)c["radius"]!, Wide(c, "launcherId"),
                new SimCredit(Wide(c, "characterId"), Wide(c, "corporationId"), Wide(c, "allianceId"), Wide(c, "warFactionId")), (bool)c["isEmpty"]!),
            "Spawn" => new SimSpawn(
                (ulong)Wide(c, "requestId"), (int)c["solarSystemId"]!, ShipOf(c["ship"]!), Wide(c, "anchorShipId"), VectorOf(c["position"]!), VectorOf(c["velocity"]!), VectorOf(c["direction"]!),
                (string)c["behavior"]!, IdentityOf(c["identity"]!)),
            "SpawnReply" => (bool)c["ok"]! ? new SimSpawnReply((ulong)Wide(c, "requestId"), (ulong)Wide(c, "session"), null) : new SimSpawnReply((ulong)Wide(c, "requestId"), null, (string)c["reason"]!),
            "Destroy" => new SimDestroy(Wide(c, "shipId"), new SimCredit(Wide(c, "characterId"), Wide(c, "corporationId"), Wide(c, "allianceId"), Wide(c, "warFactionId"))),
            var other => throw new InvalidOperationException(other),
        };
    }

    [Fact]
    public void The_fixtures_cover_every_message_kind()
    {
        var kinds = ((JsonArray)Fixtures["valid"]!).Select(c => (string)c!["kind"]!).Distinct().Order().ToArray();
        Assert.Equal(["Accepted", "Activate", "Approaching", "Bind", "ChargeSpent", "Command", "Condition", "Destroy", "Destroyed", "Docked", "Enter", "EnterReply", "Hello", "HelloAck", "Jumped", "Jumping", "Leave", "Notify", "PlaceWreck", "Refused", "Respawn", "ShipSpec", "Spawn", "SpawnReply", "TargetRefused", "Targets", "UpdateState"], kinds);
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
        Assert.Equal((220.0, 137.5, 201.25), (enter.Ship.Vitals!.ShieldCapacity, enter.Ship.Vitals.ArmorHp, enter.Ship.Vitals.StructureHp));
        Assert.Equal([0.0, 0.8, 0.6, 0.5], enter.Ship.Vitals.Resonances.Shield);
        Assert.Equal([0.5, 0.35, 0.25, 0.1], enter.Ship.Vitals.Resonances.Armor);
        Assert.Equal([0.33, 0.33, 0.33, 0.33], enter.Ship.Vitals.Resonances.Structure);
        Assert.Equal(new SimCondition(0.35, 0.6, 1.0, 1.0), enter.Ship.Condition);
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
    public void A_target_count_the_frame_cannot_hold_is_refused_without_allocating_for_it()
    {
        var good = SimCodec.Encode(MessageOf(Case("valid", "targets with a lock going")));
        // the count is the four bytes before the one 8-byte target at the end of the frame
        var forged = (byte[])good.Clone();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(forged, good.Length - 8 - 4);
        var error = Assert.Throws<SimLinkException>(() => SimCodec.Decode(forged.AsMemory(4)));
        Assert.Equal(SimLinkError.Truncated, error.Error);
    }

    [Fact]
    public void A_pending_flag_that_is_not_a_flag_is_refused()
    {
        var forged = SimCodec.Encode(MessageOf(Case("valid", "targets none")));
        // u32 length, kind, u64 session, u8 command, then the flag
        forged[4 + 1 + 8 + 1] = 2;
        Assert.Equal(SimLinkError.BadValue, Assert.Throws<SimLinkException>(() => SimCodec.Decode(forged.AsMemory(4))).Error);
    }

    [Fact]
    public void A_decoded_ship_carries_what_the_ship_may_lock()
    {
        var c = Case("valid", "enter by undocking");
        var enter = Assert.IsType<SimEnter>(SimCodec.Decode(Hex((string)c["hex"]!).AsMemory(4)));
        Assert.Equal((29700.0, 660.0, 28.0, 4), (enter.Ship.MaxTargetRange, enter.Ship.ScanResolution, enter.Ship.SignatureRadius, enter.Ship.MaxLockedTargets));
    }

    [Fact]
    public void Text_longer_than_a_length_field_is_refused_on_the_way_out()
    {
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimRefused(1, 4, new string('x', 70_000))));
    }
}
