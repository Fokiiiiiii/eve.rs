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
        VitalsOf(s["vitals"]!), ConditionOf(s["condition"]!), SensorsOf(s));

    private static SimSensors SensorsOf(JsonNode s)
    {
        var strength = Four(s["sensorStrength"]!);
        return new SimSensors(strength[0], strength[1], strength[2], strength[3], (double)s["ecmResistance"]!);
    }

    private static double[] Four(JsonNode n) => [.. ((JsonArray)n).Select(x => (double)x!)];

    private static SimVitals VitalsOf(JsonNode v) => new(
        (double)v["shieldCapacity"]!, (double)v["armorHp"]!, (double)v["structureHp"]!,
        new SimResonances(Four(v["resonances"]!["shield"]!), Four(v["resonances"]!["armor"]!), Four(v["resonances"]!["structure"]!)));

    private static SimCondition ConditionOf(JsonNode c) => new((double)c["damage"]!, (double)c["armorDamage"]!, (double)c["shieldCharge"]!, (double)c["charge"]!);

    private static SimNumbers? NumbersOf(JsonNode? n) => n is null ? null : new SimNumbers((double)n["mass"]!, (double)n["maxVelocity"]!, (double)n["signatureRadius"]!);

    private static SimChange[] ChangesOf(JsonNode n) => [.. ((JsonArray)n).Select(c => new SimChange(Wide(c!, "itemId"), (int)c!["attributeId"]!, (double)c["value"]!))];

    private static SimEffect EffectOf(JsonNode e) => new(
        Wide(e, "moduleId"), (int)e["moduleType"]!, (int)e["effectId"]!, (string)e["fx"]!, (double)e["durationMs"]!, (double)e["capacitorNeed"]!, (int)e["repeat"]!, Wide(e, "targetId"),
        NumbersOf(e["on"]), NumbersOf(e["off"]), ChangesOf(e["onChanges"]!), ChangesOf(e["offChanges"]!), e["turret"] is { } turret ? TurretOf(turret) : null,
        e["action"] is { } action ? ActionOf(action) : null);

    private static SimAction ActionOf(JsonNode a)
    {
        if ((string)a["kind"]! == "charged")
        {
            var fallback = a["fallback"] is { } f ? ActionOf(f) : null;
            return new SimAction.Charged(ActionOf(a["action"]!), fallback, (int)a["chargeTypeId"]!, (int)a["chargeFlag"]!, (int)a["chargePerCycle"]!, (long)a["charges"]!);
        }
        if ((string)a["kind"]! == "ecmBurst")
        {
            return new SimAction.EcmBurst((double)a["range"]!, Four(a["strength"]!));
        }
        if ((string)a["kind"]! == "smartbomb")
        {
            return new SimAction.Smartbomb((double)a["range"]!, Four(a["damage"]!), (bool)a["hurtsPilots"]!);
        }
        var (amount, end) = (a["amount"] is { } given ? (double)given : 0d, (string)a["when"]! == "end");
        SimLayer Layer() => Enum.Parse<SimLayer>((string)a["layer"]!, true);
        if ((string)a["kind"]! == "repair")
        {
            return new SimAction.Repair(Layer(), amount, end);
        }
        double Optional(string name) => a[name] is { } value ? (double)value : 0d;
        SimRemote what = (string)a["what"]! switch
        {
            "repair" => new SimRemote.Repair(Layer(), amount),
            "transfer" => new SimRemote.Transfer(amount),
            "neutralize" => new SimRemote.Neutralize(amount, (double)a["signatureResolution"]!),
            "drain" => new SimRemote.Drain(amount, (double)a["signatureResolution"]!),
            "web" => new SimRemote.Web((double)a["percent"]!),
            "paint" => new SimRemote.Paint((double)a["percent"]!),
            "damp" => new SimRemote.Damp((double)a["rangePercent"]!, (double)a["resolutionPercent"]!, Optional("gravimetricStrengthPercent"), Optional("ladarStrengthPercent"), Optional("magnetometricStrengthPercent"), Optional("radarStrengthPercent")),
            "disrupt" => new SimRemote.Disrupt((double)a["rangePercent"]!, (double)a["falloffPercent"]!, (double)a["trackingPercent"]!),
            "guidanceDisrupt" => new SimRemote.GuidanceDisrupt((double)a["velocityPercent"]!, (double)a["delayPercent"]!, (double)a["aoeVelocityPercent"]!, (double)a["aoeCloudPercent"]!),
            "jam" => new SimRemote.Jam(Four(a["strength"]!)[0], Four(a["strength"]!)[1], Four(a["strength"]!)[2], Four(a["strength"]!)[3]),
            _ => new SimRemote.Scramble((double)a["strength"]!, (bool)a["blocksMwd"]!),
        };
        return new SimAction.Remote(what, (double)a["optimal"]!, (double)a["falloff"]!, end, (string)a["jam"]!);
    }

    private static SimTurret TurretOf(JsonNode t) => new(
        (int)t["weaponTypeId"]!, (string)t["fx"]!, VectorOf(t["rawDamage"]!), (double)t["optimalRange"]!, (double)t["falloff"]!, (double)t["trackingSpeed"]!,
        (double)t["signatureResolution"]!, ChargeOf(t["charge"]), MissileOf(t["missile"]));

    private static SimMissile? MissileOf(JsonNode? m) => m is null ? null : new SimMissile(
        (int)m["chargeTypeId"]!, (int)m["groupId"]!, (string)m["name"]!, (double)m["radius"]!, (double)m["mass"]!, (double)m["agility"]!, (double)m["hitPoints"]!, (double)m["maxVelocity"]!, (double)m["flightTimeMs"]!, (double)m["explosionRadius"]!, (double)m["explosionVelocity"]!,
        (double)m["damageReductionFactor"]!, (double)m["damageReductionSensitivity"]!);

    private static SimTurretCharge? ChargeOf(JsonNode? c) => c is null ? null : new SimTurretCharge((int)c["typeId"]!, (int)c["flag"]!, (long)c["rounds"]!, (long)c["perCycle"]!);

    private static double[] VectorOf(JsonNode n) => [.. ((JsonArray)n).Select(x => (double)x!)];

    private static SimNpcWeapon WeaponOf(JsonNode w) => new(
        Wide(w, "moduleId"), (int)w["effectId"]!, (double)w["cycleMs"]!, (double)w["capacitorNeed"]!, (int)w["weaponTypeId"]!, (string)w["fx"]!,
        VectorOf(w["rawDamage"]!), (double)w["optimalRange"]!, (double)w["falloff"]!, (double)w["trackingSpeed"]!, (double)w["signatureResolution"]!, ChargeOf(w["charge"]), MissileOf(w["missile"]));

    private static SimNpcIdentity IdentityOf(JsonNode i) => new(
        (long)i["hostileResponseThreshold"]!, (long)i["friendlyResponseThreshold"]!, (double)i["aggressionRange"]!,
        [.. ((JsonArray)i["weapons"]!).Select(w => WeaponOf(w!))],
        WreckKindOf(i["wreck"]),
        i["hull"] is { } hull && (bool)hull,
        i["hostWreck"] is { } hostWreck && (bool)hostWreck);

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
            "AdjustCharge" => new SimAdjustCharge(
                (ulong)Wide(c, "requestId"), Session(), Wide(c, "moduleId"), (int)c["chargeType"]!,
                (string)c["op"]! == "add" ? new SimChargeOp.Add(Wide(c, "capacity"), Wide(c, "available")) : new SimChargeOp.Remove(Wide(c, "quantity"))),
            "ChargeAdjusted" => new SimChargeAdjusted((ulong)Wide(c, "requestId"), (bool)c["running"]!, Wide(c, "before"), Wide(c, "after")),
            "Targets" => new SimTargets(Session(), (byte)(int)c["command"]!, (bool)c["pending"]!, [.. ((JsonArray)c["targets"]!).Select(x => long.Parse((string)x!, CultureInfo.InvariantCulture))]),
            "TargetRefused" => new SimTargetRefused(Session(), (byte)(int)c["command"]!, (string)c["reason"]!, (int)c["targetType"]!, (double)c["desiredRange"]!, (double)c["actualDistance"]!),
            "Destroyed" => new SimDestroyed(Session(), Wide(c, "shipId"), Wide(c, "wreckId"), VectorOf(c["position"]!), VectorOf(c["direction"]!), (double)c["radius"]!, Wide(c, "killerShipId"), WreckKindOf(c["wreckKind"])),
            "Respawn" => new SimRespawn(Session(), ShipOf(c["ship"]!), VectorOf(c["position"]!), VectorOf(c["direction"]!)),
            "PlaceWreck" => new SimPlaceWreck(
                (int)c["solarSystemId"]!, Wide(c, "wreckId"), (int)c["groupId"]!, WreckKindOf(c["wreck"])!, VectorOf(c["position"]!), VectorOf(c["direction"]!), (double)c["radius"]!, Wide(c, "launcherId"),
                new SimCredit(Wide(c, "characterId"), Wide(c, "corporationId"), Wide(c, "allianceId"), Wide(c, "warFactionId")), (bool)c["isEmpty"]!),
            "PlaceContainer" => new SimPlaceContainer(
                Session(), Wide(c, "containerId"), WreckKindOf(c["container"])!, (double)c["radius"]!, Wide(c, "launcherId"),
                new SimCredit(Wide(c, "characterId"), Wide(c, "corporationId"), Wide(c, "allianceId"), Wide(c, "warFactionId")), (double)c["distance"]!),
            "Arrived" => new SimArrived(Session(), Wide(c, "beltId"), VectorOf(c["position"]!)),
            "ContainerPlaced" => new SimContainerPlaced(Session(), Wide(c, "containerId"), VectorOf(c["position"]!), VectorOf(c["direction"]!)),
            "ChangeWreck" => new SimChangeWreck(Session(), Wide(c, "wreckId"), (bool)c["isEmpty"]!),
            "RemoveWreck" => new SimRemoveWreck(Wide(c, "wreckId"), (int)c["effect"]!),
            "Measured" => new SimMeasured(Session(), (byte)(int)c["command"]!, c["distance"] is { } d ? (double)d : null),
            "Spawn" => new SimSpawn(
                (ulong)Wide(c, "requestId"), (int)c["solarSystemId"]!, ShipOf(c["ship"]!), Wide(c, "anchorShipId"), VectorOf(c["position"]!), VectorOf(c["velocity"]!), VectorOf(c["direction"]!),
                (string)c["behavior"]!, IdentityOf(c["identity"]!)),
            "SpawnReply" => (bool)c["ok"]! ? new SimSpawnReply((ulong)Wide(c, "requestId"), (ulong)Wide(c, "session"), null) : new SimSpawnReply((ulong)Wide(c, "requestId"), null, (string)c["reason"]!),
            "LaunchDrone" => new SimLaunchDrone(
                (ulong)Wide(c, "requestId"), Session(), ShipOf(c["ship"]!), WeaponOf(c["weapon"]!), (int)c["graphicId"]!, (double)c["orbitDistance"]!, (double)c["attackRange"]!, (double)c["cruiseFraction"]!),
            "DroneCommand" => new SimDroneCommand(Session(), (byte)(int)c["command"]!, Wide(c, "targetId"), [.. ((JsonArray)c["droneIds"]!).Select(d => long.Parse((string)d!))], (double)c["controlRange"]!),
            "DroneResults" => new SimDroneResults(Session(), (byte)(int)c["command"]!, [.. ((JsonArray)c["results"]!).Select(r => new SimDroneResult(Wide(r!, "droneId"), (string)r!["reason"]!))]),
            "DroneScooped" => new SimDroneScooped(Session(), Wide(c, "droneId")),
            "DroneReleased" => new SimDroneReleased(Session(), Wide(c, "droneId"), VectorOf(c["position"]!)),
            "Destroy" => new SimDestroy(Wide(c, "shipId"), new SimCredit(Wide(c, "characterId"), Wide(c, "corporationId"), Wide(c, "allianceId"), Wide(c, "warFactionId"))),
            var other => throw new InvalidOperationException(other),
        };
    }

    [Fact]
    public void The_fixtures_cover_every_message_kind()
    {
        var kinds = ((JsonArray)Fixtures["valid"]!).Select(c => (string)c!["kind"]!).Distinct().Order().ToArray();
        Assert.Equal(["Accepted", "Activate", "AdjustCharge", "Approaching", "Arrived", "Bind", "ChangeWreck", "ChargeAdjusted", "ChargeSpent", "Command", "Condition", "ContainerPlaced", "Destroy", "Destroyed", "Docked", "DroneCommand", "DroneReleased", "DroneResults", "DroneScooped", "Enter", "EnterReply", "Hello", "HelloAck", "Jumped", "Jumping", "LaunchDrone", "Leave", "Measured", "Notify", "PlaceContainer", "PlaceWreck", "Refused", "RemoveWreck", "Respawn", "ShipSpec", "Spawn", "SpawnReply", "TargetRefused", "Targets", "UpdateState"], kinds);
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

    [Fact]
    public void A_heat_profile_roundtrips_through_the_sim_codec()
    {
        var profile = new SimHeatProfile(0.75, 0.85, 3, [100, 100, 100], [1, 2, 3], [0.01, 0.02, 0.03], [0.7, 0.71, 0.72],
            [new SimHeatModule(123, 27, 40, 2, 6.6, 0.01, true, false, true)]);
        var encoded = SimCodec.Encode(new SimSetHeatProfile(42, profile));
        var decoded = Assert.IsType<SimSetHeatProfile>(SimCodec.Decode(encoded.AsMemory(4)));
        Assert.Equal(42UL, decoded.SessionId);
        Assert.Equal(Convert.ToHexString(encoded), Convert.ToHexString(SimCodec.Encode(decoded)));
        Assert.Equal(profile.Modules, decoded.Profile.Modules);
    }

    [Fact]
    public void A_charged_action_roundtrips_its_cycle_charge_and_fallback()
    {
        var baseline = Assert.IsType<SimActivate>(MessageOf(Case("valid", "activate an ECM burst")));
        var charged = new SimAction.Charged(
            new SimAction.Remote(new SimRemote.Transfer(60), 0, 0, false, "energyTransfer"),
            new SimAction.Repair(SimLayer.Armor, 24, true), 16272, 27, 3, 5);
        var message = baseline with { Effect = baseline.Effect with { Action = charged } };
        var encoded = SimCodec.Encode(message);
        var decoded = Assert.IsType<SimActivate>(SimCodec.Decode(encoded.AsMemory(4)));
        Assert.Equal(Convert.ToHexString(encoded), Convert.ToHexString(SimCodec.Encode(decoded)));
        var action = Assert.IsType<SimAction.Charged>(decoded.Effect.Action);
        Assert.Equal((16272, 27, 3, 5L), (action.ChargeTypeId, action.ChargeFlag, action.ChargePerCycle, action.Charges));
        Assert.IsType<SimAction.Repair>(action.Fallback);
    }


    [Fact]
    public void A_micro_jump_drive_action_roundtrips_through_the_sim_codec()
    {
        var baseline = Assert.IsType<SimActivate>(MessageOf(Case("valid", "activate an ECM burst")));
        var action = new SimAction.MicroJumpDrive(100_000, 180_000);
        var message = baseline with { Effect = baseline.Effect with { Action = action } };
        var encoded = SimCodec.Encode(message);
        var decoded = Assert.IsType<SimActivate>(SimCodec.Decode(encoded.AsMemory(4)));

        Assert.Equal(Convert.ToHexString(encoded), Convert.ToHexString(SimCodec.Encode(decoded)));
        Assert.Equal(action, Assert.IsType<SimAction.MicroJumpDrive>(decoded.Effect.Action));
    }

    [Fact]
    public void A_module_overheat_order_roundtrips_through_the_sim_codec()
    {
        var encoded = SimCodec.Encode(new SimSetModuleOverheat(42, 123, true));
        Assert.Equal(new SimSetModuleOverheat(42, 123, true), SimCodec.Decode(encoded.AsMemory(4)));
    }

    [Fact]
    public void A_module_offline_order_roundtrips_through_the_sim_codec()
    {
        var encoded = SimCodec.Encode(new SimSetModuleOffline(42, 123));
        Assert.Equal(new SimSetModuleOffline(42, 123), SimCodec.Decode(encoded.AsMemory(4)));
    }

    [Fact]
    public void A_rack_overheat_order_roundtrips_through_the_sim_codec()
    {
        var encoded = SimCodec.Encode(new SimSetRackOverheat(42, 1, true));
        Assert.Equal(new SimSetRackOverheat(42, 1, true), SimCodec.Decode(encoded.AsMemory(4)));
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
        // the count is the four bytes before the two 16-byte modules and the five 8-byte sensor numbers at the end of the frame
        var at = good.Length - (5 * 8) - (2 * 16) - 4;
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
