using System.Globalization;
using System.Text.Json.Nodes;

namespace Eve.Space.Tests;

/// <summary>
/// Replays oracle/fixtures/simlink/messages.json, the golden frames of docs/design/adr/0007 (the Rust simulation replays the same
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

    /// <summary>A dynamic slim-item field value of a case: <c>{"tag": ..., "value": ...}</c>, or <c>{"tag": "tuple", "items": [...]}</c>.</summary>
    private static SimSlimValue SlimValueOf(JsonNode value) => (string)value["tag"]! switch
    {
        "none" => new SimSlimNone(),
        "int" => new SimSlimInt(Wide(value, "value")),
        "real" => new SimSlimReal((double)value["value"]!),
        "bool" => new SimSlimBool((bool)value["value"]!),
        "text" => new SimSlimText((string)value["value"]!),
        "tuple" => new SimSlimTuple(((JsonArray)value["items"]!).Select(item => SlimValueOf(item!)).ToList()),
        var other => throw new InvalidOperationException($"slim value tag {other}"),
    };

    private static long Wide(JsonNode node, string field) => long.Parse((string)node[field]!, CultureInfo.InvariantCulture);

    /// <summary>The ships a command burst's cycle reaches (protocol 43): the fixture's ids are strings, as the other ids are; none for another command.</summary>
    private static IReadOnlyList<long>? RecipientsOf(JsonNode node) =>
        (node["recipients"] as JsonArray)?.Select(ship => long.Parse((string)ship!, CultureInfo.InvariantCulture)).ToArray();

    private static SimShip ShipOf(JsonNode s) => new(
        Wide(s, "shipId"), Wide(s, "characterId"), (int)s["typeId"]!, (int)s["groupId"]!, (int)s["categoryId"]!, (string)s["name"]!, Wide(s, "ownerId"),
        (int)s["corporationId"]!, (int)s["allianceId"]!, (int)s["warFactionId"]!, (double)s["securityStatus"]!, (double)s["radius"]!, (double)s["mass"]!,
        (double)s["maxVelocity"]!, (double)s["agility"]!, (double)s["warpSpeedAu"]!, (double)s["shieldRechargeMs"]!,
        (double)s["maxTargetRange"]!, (double)s["scanResolution"]!, (double)s["signatureRadius"]!, (int)s["maxLockedTargets"]!, (double)s["capacitorCapacity"]!, (double)s["capacitorRechargeS"]!,
        [.. ((JsonArray)s["modules"]!).Select(m => new SimModule(Wide(m!, "itemId"), (int)m!["typeId"]!, (int)m["flag"]!))],
        VitalsOf(s["vitals"]!), ConditionOf(s["condition"]!), SensorsOf(s), s["droneDamageMultiplier"] is { } drone ? (double)drone : 1d,
            s["stance"] is { } stance ? new SimShipStance((int)stance["oldStance"]!, long.Parse((string)stance["switchedAt"]!, CultureInfo.InvariantCulture), (int)stance["stance"]!) : null);

    private static SimSensors SensorsOf(JsonNode s)
    {
        var strength = Four(s["sensorStrength"]!);
        return new SimSensors(strength[0], strength[1], strength[2], strength[3], (double)s["ecmResistance"]!, (double)s["energyWarfareResistance"]!);
    }

    private static double[] Four(JsonNode n) => [.. ((JsonArray)n).Select(x => (double)x!)];

    private static SimVitals VitalsOf(JsonNode v) => new(
        (double)v["shieldCapacity"]!, (double)v["armorHp"]!, (double)v["structureHp"]!,
        new SimResonances(Four(v["resonances"]!["shield"]!), Four(v["resonances"]!["armor"]!), Four(v["resonances"]!["structure"]!)),
        v["armorPinned"] is { } pinned && (bool)pinned);

    private static SimCondition ConditionOf(JsonNode c) => new((double)c["damage"]!, (double)c["armorDamage"]!, (double)c["shieldCharge"]!, (double)c["charge"]!);

    private static SimNumbers? NumbersOf(JsonNode? n) => n is null ? null : new SimNumbers((double)n["mass"]!, (double)n["maxVelocity"]!, (double)n["signatureRadius"]!);

    private static SimChange[] ChangesOf(JsonNode n) => [.. ((JsonArray)n).Select(c => new SimChange(Wide(c!, "itemId"), (int)c!["attributeId"]!, (double)c["value"]!))];

    private static SimEffect EffectOf(JsonNode e) => new(
        Wide(e, "moduleId"), (int)e["moduleType"]!, (int)e["effectId"]!, (string)e["fx"]!, (double)e["durationMs"]!, (double)e["capacitorNeed"]!, (int)e["repeat"]!, Wide(e, "targetId"),
        NumbersOf(e["on"]), NumbersOf(e["off"]), ChangesOf(e["onChanges"]!), ChangesOf(e["offChanges"]!), e["turret"] is { } turret ? TurretOf(turret) : null,
        e["action"] is { } action ? ActionOf(action) : null, BurnsFuel: e["burnsFuel"] is { } burns && (bool)burns,
        Rules: e["rules"] is { } rules ? RulesOf(rules) : null);

    private static SimModuleRules RulesOf(JsonNode r) => new(
        (int)r["groupId"]!, (int)r["maxGroupActive"]!, (bool)r["disallowEarlyDeactivation"]!, (bool)r["requiresIndustrialCore"]!,
        (bool)r["providesIndustrialCore"]!, (double)r["reactivationDelayMs"]!, (double?)r["maxVelocityLimit"] ?? 0, (double?)r["minVelocityLimit"] ?? 0);

    /// <summary>A strike of the fixture (the object of a strike action, or the nested strike of a field).</summary>
    private static SimAction.Strike StrikeOf(JsonNode a)
    {
        var direction = ((JsonArray)a["direction"]!).Select(x => (double)x!).ToArray();
        return new SimAction.Strike(
            (byte)(int)a["shape"]!, direction, (double)a["length"]!, (double)a["radius"]!, (double)a["warningMs"]!, (double)a["pulseMs"]!, (int)a["pulses"]!,
            Four(a["damage"]!), (double)a["signatureRadius"]!, (double)a["immobileMs"]!, (double)a["noJumpMs"]!,
            (double)a["neutAmount"]!, (double)a["neutRadius"]!, (double)a["neutSignatureRadius"]!,
            a["debuffMs"] is { } debuff ? (double)debuff : 0.0,
            a["sweepEnd"] is JsonArray sweepEnd ? sweepEnd.Select(x => (double)x!).ToArray() : null,
            a["beacon"] is JsonArray beacon ? beacon.Select(x => (double)x!).ToArray() : null);
    }

    private static SimDeployable DeployableOf(JsonNode d) =>
        new((int)d["chargeTypeId"]!, (int)d["groupId"]!, (string)d["name"]!, (double)d["radius"]!);

    private static SimAction ActionOf(JsonNode a)
    {
        if ((string)a["kind"]! == "pointDefense")
        {
            return new SimAction.PointDefense((double)a["rangeMeters"]!, Four(a["damage"]!));
        }
        if ((string)a["kind"]! == "mining")
        {
            var family = (string)a["family"]! switch { "ore" => SimMiningFamilies.Ore, "ice" => SimMiningFamilies.Ice, _ => SimMiningFamilies.Gas };
            return new SimAction.Mining(family, (double)a["miningAmountM3"]!, (double)a["wasteMultiplier"]!, (double)a["wasteProbability"]!, (double)a["critProbability"]!, (double)a["critMultiplier"]!);
        }
        if ((string)a["kind"]! == "salvage")
        {
            return new SimAction.Salvage((double)a["accessBonus"]!);
        }
        if ((string)a["kind"]! == "scan")
        {
            return new SimAction.Scan((bool)a["cargo"]! ? SimScanKinds.Cargo : SimScanKinds.Ship, (double)a["optimalMeters"]!, (double)a["falloffMeters"]!);
        }
        if ((string)a["kind"]! == "tractor")
        {
            return new SimAction.Tractor((double)a["velocity"]!, (double)a["hold"]!, (double)a["range"]!);
        }
        if ((string)a["kind"]! == "kamikaze")
        {
            var d = a["damage"]!;
            return new SimAction.Kamikaze([(double)d[0]!, (double)d[1]!, (double)d[2]!, (double)d[3]!], (double)a["rangeMeters"]!);
        }
        if ((string)a["kind"]! == "blast")
        {
            var c = a["centre"]!;
            var d = a["damage"]!;
            return new SimAction.Blast([(double)c[0]!, (double)c[1]!, (double)c[2]!], (double)a["radiusMeters"]!,
                [(double)d[0]!, (double)d[1]!, (double)d[2]!, (double)d[3]!]);
        }
        if ((string)a["kind"]! == "missile")
        {
            var d = a["damage"]!;
            return new SimAction.FighterMissile([(double)d[0]!, (double)d[1]!, (double)d[2]!, (double)d[3]!], (double)a["reachMeters"]!,
                (double)a["explosionRadius"]!, (double)a["explosionVelocity"]!, (double)a["reductionFactor"]!, (double)a["reductionSensitivity"]!);
        }
        if ((string)a["kind"]! == "doomsday")
        {
            return new SimAction.Doomsday((double)a["delayMs"]!, [(double)a["damage"]![0]!, (double)a["damage"]![1]!, (double)a["damage"]![2]!, (double)a["damage"]![3]!], (double)a["immobileMs"]!, (double)a["noJumpMs"]!);
        }
        if ((string)a["kind"]! == "passiveLock")
        {
            return new SimAction.PassiveLock();
        }
        if ((string)a["kind"]! == "autoTarget")
        {
            return new SimAction.AutoTarget((double)a["range"]!);
        }
        if ((string)a["kind"]! == "charged")
        {
            var fallback = a["fallback"] is { } f ? ActionOf(f) : null;
            return new SimAction.Charged(ActionOf(a["action"]!), fallback, (int)a["chargeTypeId"]!, (int)a["chargeFlag"]!, (int)a["chargePerCycle"]!, (long)a["charges"]!);
        }
        if ((string)a["kind"]! == "cloak")
        {
            return new SimAction.Cloak(a["warpSafe"] is { } warpSafe && (bool)warpSafe, a["targetingDelayMs"] is { } delay ? (double)delay : 0.0);
        }
        if ((string)a["kind"]! == "warpBubble")
        {
            return new SimAction.WarpBubble((double)a["range"]!);
        }
        if ((string)a["kind"]! == "warpImmunity")
        {
            return new SimAction.WarpImmunity();
        }
        if ((string)a["kind"]! == "scrambleStatus")
        {
            return new SimAction.ScrambleStatus((double)a["delta"]!);
        }
        if ((string)a["kind"]! == "defender")
        {
            var m = a["missile"]!;
            var missile = new SimMissile((int)m["chargeTypeId"]!, (int)m["groupId"]!, (string)m["name"]!, (double)m["radius"]!, (double)m["mass"]!, (double)m["agility"]!,
                (double)m["hitPoints"]!, (double)m["maxVelocity"]!, (double)m["flightTimeMs"]!, (double)m["explosionRadius"]!, (double)m["explosionVelocity"]!,
                (double)m["damageReductionFactor"]!, (double)m["damageReductionSensitivity"]!);
            return new SimAction.Defender(missile, Four(a["damage"]!));
        }
        if ((string)a["kind"]! == "stasisBurst")
        {
            return new SimAction.StasisBurst((double)a["range"]!, (double)a["durationMs"]!, (double)a["speedFactor"]!);
        }
        if ((string)a["kind"]! == "structureBurst")
        {
            return new SimAction.StructureBurst((double)a["range"]!, (double)a["durationMs"]!, BurstRemote(a));
        }
        if ((string)a["kind"]! == "panic")
        {
            return new SimAction.Panic((double)a["rangeMeters"]!);
        }
        if ((string)a["kind"]! == "commandBurst")
        {
            return new SimAction.CommandBurst((double)a["rangeMeters"]!);
        }
        if ((string)a["kind"]! == "entosis")
        {
            return new SimAction.Entosis((double)a["rangeMeters"]!);
        }
        if ((string)a["kind"]! == "cynoField")
        {
            return new SimAction.CynoField(DeployableOf(a["ball"]!), (double)a["lifetimeMs"]!);
        }
        if ((string)a["kind"]! == "warpProbe")
        {
            return new SimAction.WarpProbe(DeployableOf(a["ball"]!), (double)a["range"]!, (double)a["lifetimeMs"]!);
        }
        if ((string)a["kind"]! == "bomb")
        {
            return new SimAction.Bomb(DeployableOf(a["ball"]!), (SimBombKind)(int)a["bombKind"]!, (double)a["lifetimeMs"]!, (double)a["blastRadius"]!, Four(a["damage"]!), Four(a["ecm"]!), (double)a["neutralize"]!);
        }
        if ((string)a["kind"]! == "ecmBurst")
        {
            return new SimAction.EcmBurst((double)a["range"]!, Four(a["strength"]!));
        }
        if ((string)a["kind"]! == "resistanceShift")
        {
            return new SimAction.ResistanceShift(Four(a["seed"]!), (double)a["shift"]!, [.. ((JsonArray)a["attributeIds"]!).Select(x => (int)x!)]);
        }
        if ((string)a["kind"]! == "strike")
        {
            return StrikeOf(a);
        }
        if ((string)a["kind"]! == "field")
        {
            // the transport field: its sphere is the nested strike, then the destination, the warp block and the two id lists
            return new SimAction.Field(
                StrikeOf(a["strike"]!),
                ((JsonArray)a["destination"]!).Select(x => (double)x!).ToArray(),
                (double)a["warpBlockMs"]!,
                [.. ((JsonArray)a["moved"]!).Select(x => (long)x!)],
                [.. ((JsonArray)a["held"]!).Select(x => (long)x!)]);
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

    /// <summary>The remote effect of a structure burst fixture: the kinds of the burst families, as the generator writes them.</summary>
    private static SimRemote BurstRemote(JsonNode a)
    {
        double Optional(string name) => a[name] is { } value ? (double)value : 0d;
        return (string)a["what"]! switch
        {
            "neutralize" => new SimRemote.Neutralize((double)a["amount"]!, (double)a["signatureResolution"]!),
            "web" => new SimRemote.Web((double)a["percent"]!),
            "paint" => new SimRemote.Paint((double)a["percent"]!),
            "damp" => new SimRemote.Damp(
                (double)a["rangePercent"]!, (double)a["resolutionPercent"]!,
                Optional("gravimetricStrengthPercent"), Optional("ladarStrengthPercent"), Optional("magnetometricStrengthPercent"), Optional("radarStrengthPercent")),
            "disrupt" => new SimRemote.Disrupt((double)a["rangePercent"]!, (double)a["falloffPercent"]!, (double)a["trackingPercent"]!),
            "jam" => new SimRemote.Jam(Four(a["strength"]!)[0], Four(a["strength"]!)[1], Four(a["strength"]!)[2], Four(a["strength"]!)[3]),
            _ => new SimRemote.Scramble((double)a["strength"]!, (bool)a["blocksMwd"]!),
        };
    }

    private static SimTurret TurretOf(JsonNode t) => new(
        (int)t["weaponTypeId"]!, (string)t["fx"]!, VectorOf(t["rawDamage"]!), (double)t["optimalRange"]!, (double)t["falloff"]!, (double)t["trackingSpeed"]!,
        (double)t["signatureResolution"]!, ChargeOf(t["charge"]), MissileOf(t["missile"]), SpoolOf(t["spool"]), AoeOf(t["aoe"]), ArcOf(t["arc"]),
        (bool)t["requiresGunnery"]!);

    private static SimSpool? SpoolOf(JsonNode? s) => s is null ? null : new SimSpool((double)s["perCycle"]!, (double)s["max"]!);

    private static SimAoe? AoeOf(JsonNode? a) => a is null ? null : new SimAoe(
        (double)a["explosionRadius"]!, (double)a["explosionVelocity"]!, (double)a["damageReductionFactor"]!, (double)a["damageReductionSensitivity"]!);

    private static SimArc? ArcOf(JsonNode? a) => a is null ? null : new SimArc((double)a["range"]!, (int)a["targets"]!, a["damageLossTarget"] is { } loss ? (double)loss : null);

    private static SimMissile? MissileOf(JsonNode? m) => m is null ? null : MissileWith(m);

    private static SimMissile MissileWith(JsonNode m) => new SimMissile(
        (int)m["chargeTypeId"]!, (int)m["groupId"]!, (string)m["name"]!, (double)m["radius"]!, (double)m["mass"]!, (double)m["agility"]!, (double)m["hitPoints"]!, (double)m["maxVelocity"]!, (double)m["flightTimeMs"]!, (double)m["explosionRadius"]!, (double)m["explosionVelocity"]!,
        (double)m["damageReductionFactor"]!, (double)m["damageReductionSensitivity"]!,
        m["dot"] is { } d ? new SimDot((double)d["durationMs"]!, (double)d["maxDamagePerTick"]!, (double)d["maxHpPercentPerTick"]!) : null);

    private static SimTurretCharge? ChargeOf(JsonNode? c) => c is null ? null : new SimTurretCharge((int)c["typeId"]!, (int)c["flag"]!, (long)c["rounds"]!, (long)c["perCycle"]!, (bool?)c["reloads"] ?? false);

    private static double[] VectorOf(JsonNode n) => [.. ((JsonArray)n).Select(x => (double)x!)];

    private static SimNpcWeapon WeaponOf(JsonNode w) => new(
        Wide(w, "moduleId"), (int)w["effectId"]!, (double)w["cycleMs"]!, (double)w["capacitorNeed"]!, (int)w["weaponTypeId"]!, (string)w["fx"]!,
        VectorOf(w["rawDamage"]!), (double)w["optimalRange"]!, (double)w["falloff"]!, (double)w["trackingSpeed"]!, (double)w["signatureResolution"]!, ChargeOf(w["charge"]), MissileOf(w["missile"]),
        (bool)w["requiresGunnery"]!);

    private static SimNpcIdentity IdentityOf(JsonNode i) => new(
        (long)i["hostileResponseThreshold"]!, (long)i["friendlyResponseThreshold"]!, (double)i["aggressionRange"]!,
        [.. ((JsonArray)i["weapons"]!).Select(w => WeaponOf(w!))],
        WreckKindOf(i["wreck"]),
        i["hull"] is { } hull && (bool)hull,
        i["hostWreck"] is { } hostWreck && (bool)hostWreck);

    private static SimWreckKind? WreckKindOf(JsonNode? w) => w is null ? null : new SimWreckKind((int)w["typeId"]!, (string)w["name"]!, (int)w["graphicId"]!);

    private static SimStructureHealth HealthOf(JsonNode c)
    {
        var v = c["vitals"]!;
        var resonances = new List<double>();
        foreach (var layer in new[] { "shield", "armor", "structure" })
        {
            resonances.AddRange(((JsonArray)v["resonances"]![layer]!).Select(x => (double)x!));
        }
        return new SimStructureHealth(
            (double)v["shieldCapacity"]!, (double)v["armorHp"]!, (double)v["structureHp"]!, resonances, (double)c["shieldRechargeMs"]!,
            VectorOf(c["perSecond"]!), VectorOf(c["repairPause"]!));
    }
    private static SimAnalysisBeaconState BeaconStateOf(JsonNode s) =>
        new((bool)s["active"]!, Wide(s, "activateAt"), Wide(s, "deployedAt"), (int)s["activationSeconds"]!, (int)s["linkState"]!, Wide(s, "linkCompleteAt"), Wide(s, "linkedShipId"));


    private static SimStructureStatus StatusOf(JsonNode s)
    {
        (long Start, long End, long? Paused)? timer = null;
        if (s["timer"] is JsonArray t)
        {
            timer = ((long)t[0]!, (long)t[1]!, t[2] is null ? (long?)null : (long)t[2]!);
        }
        return new SimStructureStatus(
            (int)s["state"]!,
            (int)s["upkeepState"]!,
            timer,
            s["unanchoring"] is null ? (long?)null : (long)s["unanchoring"]!,
            s["repairing"] is null ? (bool?)null : (bool)s["repairing"]!);
    }

    private static SimMessage MessageOf(JsonObject c)
    {
        ulong Session() => (ulong)Wide(c, "sessionId");
        return (string)c["kind"]! switch
        {
            "Hello" => new SimHello((ushort)(int)c["version"]!),
            "HelloAck" => new SimHelloAck((ushort)(int)c["version"]!, (uint)(long)c["pollHz"]!),
            "Enter" => new SimEnter(Session(), (int)c["solarSystemId"]!, (EnterHow)(int)c["how"]!, Wide(c, "stationId"), ShipOf(c["ship"]!),
                c["position"] is { } position ? VectorOf(position) : null,
                c["direction"] is { } direction ? VectorOf(direction) : null),
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
            "ModuleWorkCycle" => new SimModuleWorkCycle(
                Session(), Wide(c, "characterId"), Wide(c, "shipId"), (int)c["solarSystemId"]!, Wide(c, "moduleId"), Wide(c, "targetId"), (ulong)Wide(c, "cycleId"),
                (byte)(int)c["command"]!, (double)c["miningAmountM3"]!, (double)c["wasteMultiplier"]!, (double)c["wasteProbability"]!, (double)c["critProbability"]!,
                (double)c["critMultiplier"]!, (double)c["accessDifficultyBonus"]!, (double)c["wasteRoll"]!, (double)c["roundingRoll"]!, (double)c["critRoll"]!, RecipientsOf(c)),
            "ModuleWorkResult" => new SimModuleWorkResult(
                Session(), Wide(c, "moduleId"), Wide(c, "targetId"), (ulong)Wide(c, "cycleId"), (bool)c["accepted"]!, (string)c["reason"]!, Wide(c, "unitsRemaining"),
                Wide(c, "deliveredUnits"), (bool)c["complete"]!),
            "BuffState" => new SimBuffState(Session(), Wide(c, "shipId"), ((JsonArray)c["entries"]!).Select(entry => new SimBuffEntry((int)entry!["collectionId"]!, (double)entry["value"]!, (double)entry["remainingMs"]!)).ToArray()),
            "ModuleEnded" => new SimModuleEnded(Session(), Wide(c, "moduleId"), (byte)(int)c["reason"]!),
            "ChargeSpent" => new SimChargeSpent(Session(), Wide(c, "moduleId"), Wide(c, "roundsLeft")),
            "UpdateEffect" => new SimUpdateEffect(Session(), Wide(c, "moduleId"), (double)c["durationMs"]!, (double)c["capacitorNeed"]!, c["turret"] is null ? null : TurretOf(c["turret"]!), ChangesOf(c["changes"]!)),
            "AdjustCharge" => new SimAdjustCharge(
                (ulong)Wide(c, "requestId"), Session(), Wide(c, "moduleId"), (int)c["chargeType"]!,
                (string)c["op"]! == "add" ? new SimChargeOp.Add(Wide(c, "capacity"), Wide(c, "available")) : new SimChargeOp.Remove(Wide(c, "quantity"))),
            "ChargeAdjusted" => new SimChargeAdjusted((ulong)Wide(c, "requestId"), (bool)c["running"]!, Wide(c, "before"), Wide(c, "after")),
            "Targets" => new SimTargets(Session(), (byte)(int)c["command"]!, (bool)c["pending"]!, [.. ((JsonArray)c["targets"]!).Select(x => long.Parse((string)x!, CultureInfo.InvariantCulture))]),
            "TargetRefused" => new SimTargetRefused(Session(), (byte)(int)c["command"]!, (string)c["reason"]!, (int)c["targetType"]!, (double)c["desiredRange"]!, (double)c["actualDistance"]!),
            "Destroyed" => new SimDestroyed(Session(), Wide(c, "shipId"), Wide(c, "wreckId"), VectorOf(c["position"]!), VectorOf(c["direction"]!), (double)c["radius"]!, Wide(c, "killerShipId"), WreckKindOf(c["wreckKind"]), (int)c["shipTypeId"]!, (int)c["corporationId"]!),
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
            "Deployed" => new SimDeployed((int)c["solarSystemId"]!, Wide(c, "deployableId"), (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!, VectorOf(c["position"]!),
                (double)c["radius"]!, Wide(c, "ownerId"), (int)c["corporationId"]!, (int)c["allianceId"]!, Wide(c, "shipId"), (double)c["lifetimeMs"]!),
            "RemoveDeployed" => new SimRemoveDeployed(Wide(c, "deployableId")),
            "PlaceProbe" => new SimPlaceProbe(Wide(c, "probeId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!, VectorOf(c["position"]!),
                (double)c["radius"]!, Wide(c, "ownerId"), (int)c["corporationId"]!, (int)c["allianceId"]!, Wide(c, "shipId")),
            "RemoveProbe" => new SimRemoveProbe(Wide(c, "probeId")),
            "PlaceStructure" => new SimPlaceStructure(Wide(c, "structureId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!,
                (int)c["groupId"]!, VectorOf(c["position"]!), (double)c["radius"]!, Wide(c, "ownerId"), (int)c["corporationId"]!, (int)c["allianceId"]!,
                HealthOf(c), StatusOf(c["status"]!), (long)c["planetId"]!),
            "StructureDamageCandidatesRequest" => new SimStructureDamageCandidatesRequest(
                Session(), Wide(c, "structureId"), Wide(c, "moduleId"), (int)c["effectId"]!, (ulong)Wide(c, "activationNonce"),
                Wide(c, "controlLeaseGeneration"), (ulong)Wide(c, "cycleId"), (uint)(int)c["pulseHopOrdinal"]!,
                [.. ((JsonArray)c["candidateIds"]!).Select(x => long.Parse((string)x!, CultureInfo.InvariantCulture))]),
            "StructureDamageCandidatesResult" => new SimStructureDamageCandidatesResult(
                Session(), Wide(c, "structureId"), Wide(c, "moduleId"), (int)c["effectId"]!, (ulong)Wide(c, "activationNonce"),
                Wide(c, "controlLeaseGeneration"), (ulong)Wide(c, "cycleId"), (uint)(int)c["pulseHopOrdinal"]!,
                [.. ((JsonArray)c["allowedTargetIds"]!).Select(x => long.Parse((string)x!, CultureInfo.InvariantCulture))]),
            "RemoveCynoBeacon" => new SimRemoveCynoBeacon((int)c["solarSystemId"]!, Wide(c, "structureBeaconId")),
            "DamageableEntityUpserted" => new SimDamageableEntityUpserted((int)c["solarSystemId"]!, Wide(c, "entityId"), (int)c["typeId"]!, Wide(c, "ownerId"), Wide(c, "corporationId"), Wide(c, "allianceId"), Wide(c, "warFactionId")),
            "DamageableEntityRemoved" => new SimDamageableEntityRemoved((int)c["solarSystemId"]!, Wide(c, "entityId")),
            "ControlStructure" => new SimControlStructure(ulong.Parse((string)c["sessionId"]!, CultureInfo.InvariantCulture),
                (int)c["solarSystemId"]!, Wide(c, "structureId"), Wide(c, "characterId"), (bool)c["active"]!, Wide(c, "controlLeaseGeneration")),
            "TetherPolicy" => new SimTetherPolicy(ulong.Parse((string)c["sessionId"]!, CultureInfo.InvariantCulture),
                ulong.Parse((string)c["blockedForMs"]!, CultureInfo.InvariantCulture), ulong.Parse((string)c["leaseMs"]!, CultureInfo.InvariantCulture),
                [.. ((JsonArray)c["candidates"]!).Select(candidate => new SimTetherCandidate(Wide(candidate!, "structureId"), (double)candidate!["range"]!))]),
            "TetherChanged" => new SimTetherChanged(ulong.Parse((string)c["sessionId"]!, CultureInfo.InvariantCulture),
                Wide(c, "previousStructureId"), Wide(c, "structureId"), (bool)c["repairing"]!),
            "StructureDamaged" => new SimStructureDamaged(Wide(c, "structureId"), (int)c["depleted"]!, (bool)c["overThreshold"]!),
            "RemoveStructure" => new SimRemoveStructure(Wide(c, "structureId")),
            "PlaceWormhole" => new SimPlaceWormhole(Wide(c, "wormholeId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!,
                (int)c["groupId"]!, VectorOf(c["position"]!), (double)c["radius"]!, (int)c["destinationSystemId"]!, Wide(c, "destinationEnd"),
                (int)c["destinationClass"]!, VectorOf(c["destinationPosition"]!), (double)c["destinationRadius"]!, (double)c["maxJumpMass"]!),
            "RemoveWormhole" => new SimRemoveWormhole(Wide(c, "wormholeId")),
            "SetStructureState" => new SimSetStructureState((int)c["solarSystemId"]!, Wide(c, "structureId"), StatusOf(c["status"]!)),
            "PlaceDisruptor" => new SimPlaceDisruptor(Wide(c, "deployableId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!,
                VectorOf(c["position"]!), (double)c["radius"]!, (double)c["range"]!, (bool)c["active"]!, Wide(c, "ownerId"), (int)c["corporationId"]!,
                (int)c["allianceId"]!, Wide(c, "shipId")),
            "PlaceCynoBeacon" => new SimPlaceCynoBeacon(Wide(c, "deployableId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!,
                VectorOf(c["position"]!), (double)c["radius"]!, (bool)c["active"]!, Wide(c, "ownerId"), (int)c["corporationId"]!,
                (int)c["allianceId"]!, Wide(c, "shipId")),
            "PlaceObservatory" => new SimPlaceObservatory(Wide(c, "deployableId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!,
                VectorOf(c["position"]!), (double)c["radius"]!, (bool)c["active"]!, Wide(c, "ownerId"), (int)c["corporationId"]!, (int)c["allianceId"]!, Wide(c, "shipId")),
            "PlaceAccelerationGate" => new SimPlaceAccelerationGate(Wide(c, "deployableId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!,
                VectorOf(c["position"]!), (double)c["radius"]!),
            "PlaceDeadspacePocket" => new SimPlaceDeadspacePocket(Wide(c, "deployableId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, VectorOf(c["position"]!), (double)c["radius"]!),
            "PlaceControlTower" => new SimPlaceControlTower(Wide(c, "deployableId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!,
                VectorOf(c["position"]!), (double)c["radius"]!, (bool)c["active"]!, Wide(c, "ownerId"), (int)c["corporationId"]!, (int)c["allianceId"]!, Wide(c, "shipId")),
            "RemoteDecloakPing" => new SimRemoteDecloakPing((int)c["solarSystemId"]!, Wide(c, "observatoryId")),
            "SetDisruptorActive" => new SimSetDisruptorActive(Wide(c, "deployableId"), (bool)c["active"]!),
            "PlaceAnalysisBeacon" => new SimPlaceAnalysisBeacon(Wide(c, "deployableId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!,
                VectorOf(c["position"]!), (double)c["radius"]!, Wide(c, "ownerId"), (int)c["corporationId"]!, (int)c["allianceId"]!, Wide(c, "shipId"), BeaconStateOf(c["state"]!)),
            "SetAnalysisBeaconState" => new SimSetAnalysisBeaconState(Wide(c, "deployableId"), BeaconStateOf(c["state"]!)),
            "SetSlimField" => new SimSetSlimField(Wide(c, "itemId"), (string)c["fieldName"]!, SlimValueOf(c["value"]!)),
            "PlaceInhibitor" => new SimPlaceInhibitor(Wide(c, "deployableId"), (int)c["solarSystemId"]!, (string)c["typeName"]!, (int)c["typeId"]!, (int)c["groupId"]!,
                VectorOf(c["position"]!), (double)c["radius"]!, (double)c["range"]!, (bool)c["active"]!, Wide(c, "ownerId"), (int)c["corporationId"]!,
                (int)c["allianceId"]!, Wide(c, "shipId")),
            "Measured" => new SimMeasured(Session(), (byte)(int)c["command"]!, c["distance"] is { } d ? (double)d : null),
            "Spawn" => new SimSpawn(
                (ulong)Wide(c, "requestId"), (int)c["solarSystemId"]!, ShipOf(c["ship"]!), Wide(c, "anchorShipId"), VectorOf(c["position"]!), VectorOf(c["velocity"]!), VectorOf(c["direction"]!),
                (string)c["behavior"]!, IdentityOf(c["identity"]!)),
            "SpawnReply" => (bool)c["ok"]! ? new SimSpawnReply((ulong)Wide(c, "requestId"), (ulong)Wide(c, "session"), null) : new SimSpawnReply((ulong)Wide(c, "requestId"), null, (string)c["reason"]!),
            "LaunchDrone" => new SimLaunchDrone(
                (ulong)Wide(c, "requestId"), Session(), ShipOf(c["ship"]!), c["weapon"] is { } weapon ? WeaponOf(weapon) : null, (int)c["graphicId"]!, (double)c["orbitDistance"]!, (double)c["attackRange"]!, (double)c["cruiseFraction"]!,
                Effect: c["effect"] is { } drone ? new SimDroneEffect((int)drone["effectId"]!, (string)drone["fx"]!, (double)drone["cycleMs"]!, (double)drone["lastingMs"]!, ActionOf(drone["action"]!)) : null),
            "DroneCommand" => new SimDroneCommand(Session(), (byte)(int)c["command"]!, Wide(c, "targetId"), [.. ((JsonArray)c["droneIds"]!).Select(d => long.Parse((string)d!))], (double)c["controlRange"]!),
            "DroneResults" => new SimDroneResults(Session(), (byte)(int)c["command"]!, [.. ((JsonArray)c["results"]!).Select(r => new SimDroneResult(Wide(r!, "droneId"), (string)r!["reason"]!))]),
            "DirectionalTargets" => new SimDirectionalTargets(Session(), (byte)(int)c["command"]!, [.. ((JsonArray)c["targets"]!).Select(t => new SimDirectionalTarget(Wide(t!, "id"), (byte)(int)t!["kind"]!, (int)t!["typeId"]!, VectorOf(t!["position"]!), (double)t!["radius"]!, (bool)t!["cloaked"]!, (bool)t!["warping"]!))]),
            "ScanTargets" => new SimScanTargets(Session(), (byte)(int)c["command"]!, [.. ((JsonArray)c["targets"]!).Select(t => new SimScanTarget(Wide(t!, "id"), (byte)(int)t!["kind"]!, (int)t!["typeId"]!, VectorOf(t!["position"]!)))]),
            "DroneScooped" => new SimDroneScooped(Session(), Wide(c, "droneId")),
            "DroneReleased" => new SimDroneReleased(Session(), Wide(c, "droneId"), VectorOf(c["position"]!)),
            "LaunchFighter" => new SimLaunchFighter((ulong)Wide(c, "requestId"), Session(), ShipOf(c["ship"]!), (int)c["graphicId"]!, (double)c["cruiseFraction"]!, (int)c["squadronSize"]!),
            "FighterSize" => new SimFighterSize(Session(), Wide(c, "fighterId"), (int)c["size"]!),
            "FighterOrder" => new SimFighterOrder(Session(), (byte)(int)c["command"]!, Wide(c, "targetId"), [.. ((JsonArray)c["fighterIds"]!).Select(f => long.Parse((string)f!))], (double)c["range"]!),
            "FighterResults" => new SimFighterResults(Session(), (byte)(int)c["command"]!, [.. ((JsonArray)c["results"]!).Select(r => new SimFighterResult(Wide(r!, "fighterId"), (string)r!["reason"]!))]),
            "FighterGotoPoint" => new SimFighterGotoPoint(Session(), [.. ((JsonArray)c["fighterIds"]!).Select(f => long.Parse((string)f!))], VectorOf(c["point"]!)),
            "FighterLanded" => new SimFighterLanded(Session(), Wide(c, "fighterId")),
            "EcmBurst" => new SimEcmBurst(Session(), Wide(c, "shipId"), [.. ((JsonArray)c["reached"]!).Select(r => new SimBurstReach(Wide(r!, "shipId"), (bool)r!["jammed"]!))]),
            "FighterReleased" => new SimFighterReleased(Session(), Wide(c, "fighterId"), VectorOf(c["position"]!)),
            "Destroy" => new SimDestroy(Wide(c, "shipId"), new SimCredit(Wide(c, "characterId"), Wide(c, "corporationId"), Wide(c, "allianceId"), Wide(c, "warFactionId"))),
            var other => throw new InvalidOperationException(other),
        };
    }

    [Fact]
    public void The_fixtures_cover_every_message_kind()
    {
        var kinds = ((JsonArray)Fixtures["valid"]!).Select(c => (string)c!["kind"]!).Distinct().Order().ToArray();
                Assert.Equal(["Accepted", "Activate", "AdjustCharge", "Approaching", "Arrived", "Bind", "BuffState", "ChangeWreck", "ChargeAdjusted", "ChargeSpent", "Command", "Condition", "ContainerPlaced", "ControlStructure", "DamageableEntityRemoved", "DamageableEntityUpserted", "Deployed", "Destroy", "Destroyed", "DirectionalTargets", "Docked", "DroneCommand", "DroneReleased", "DroneResults", "DroneScooped", "EcmBurst", "Enter", "EnterReply", "FighterGotoPoint", "FighterLanded", "FighterOrder", "FighterReleased", "FighterResults", "FighterSize", "Hello", "HelloAck", "Jumped", "Jumping", "LaunchDrone", "LaunchFighter", "Leave", "Measured", "ModuleEnded", "ModuleWorkCycle", "ModuleWorkResult", "Notify", "PlaceAccelerationGate", "PlaceAnalysisBeacon", "PlaceContainer", "PlaceControlTower", "PlaceCynoBeacon", "PlaceDeadspacePocket", "PlaceDisruptor", "PlaceInhibitor", "PlaceObservatory", "PlaceProbe", "PlaceStructure", "PlaceWormhole", "PlaceWreck", "Refused", "RemoteDecloakPing", "RemoveCynoBeacon", "RemoveDeployed", "RemoveProbe", "RemoveStructure", "RemoveWormhole", "RemoveWreck", "Respawn", "ScanTargets", "SetAnalysisBeaconState", "SetDisruptorActive", "SetSlimField", "SetStructureState", "ShipSpec", "Spawn", "SpawnReply", "StructureDamageCandidatesRequest", "StructureDamageCandidatesResult", "StructureDamaged", "TargetRefused", "Targets", "TetherChanged", "TetherPolicy", "UpdateEffect", "UpdateState"], kinds);
        Assert.True(((JsonArray)Fixtures["invalid"]!).Count >= 10);    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_tether_policy_refuses_nonpositive_or_nonfinite_ranges(double range)
    {
        var error = Assert.Throws<SimLinkException>(() =>
            SimCodec.Encode(new SimTetherPolicy(77, 0, 3000, [new(1_040_000_000_001, range)])));
        Assert.Equal(SimLinkError.BadValue, error.Error);
    }

    [Fact]
    public void A_tether_policy_bounds_its_count_before_allocating_candidates()
    {
        var frame = SimCodec.Encode(new SimTetherPolicy(77, 0, 3000, []));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(25, 4), uint.MaxValue);
        var error = Assert.Throws<SimLinkException>(() => SimCodec.Decode(frame));
        Assert.Equal(SimLinkError.Truncated, error.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Structure_control_round_trips_with_the_reserved_wire_kind(bool active)
    {
        var message = new SimControlStructure(77, 30000142, active ? 1_040_000_000_001 : 0, active ? 90000001 : 0, active, active ? 19 : 20);
        var frame = SimCodec.Encode(message);
        Assert.Equal(84, frame[4]);
        Assert.Equal(message, SimCodec.Decode(frame.AsMemory(4)));
    }

    [Theory]
    [InlineData(0, 1, 1, true)]
    [InlineData(30000142, 0, 1, true)]
    [InlineData(30000142, 1, 0, true)]
    [InlineData(30000142, -1, 1, false)]
    public void Structure_control_rejects_invalid_identities(int system, long structure, long character, bool active)
    {
        var error = Assert.Throws<SimLinkException>(() =>
            SimCodec.Encode(new SimControlStructure(77, system, structure, character, active, active ? 19 : 0)));
        Assert.Equal(SimLinkError.BadValue, error.Error);
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
    public void A_position_query_reply_roundtrips_through_the_sim_codec()
    {
        var message = new SimPosition(77, SimCommands.ResolveWarpTarget, [1_250_000, -20_000, 3_000]);
        var frame = SimCodec.Encode(message);

        var decoded = Assert.IsType<SimPosition>(SimCodec.Decode(frame.AsMemory(4)));

        Assert.Equal((77UL, SimCommands.ResolveWarpTarget), (decoded.SessionId, decoded.Command));
        Assert.Equal(message.Position, decoded.Position);
        Assert.Equal(Convert.ToHexString(frame), Convert.ToHexString(SimCodec.Encode(decoded)));
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
    public void A_doomsday_strike_roundtrips_its_aim_pulses_damage_and_windows_through_the_sim_codec()
    {
        var message = Assert.IsType<SimActivate>(MessageOf(Case("valid", "activate a doomsday strike (Bosonic Field Generator)")));
        var encoded = SimCodec.Encode(message);
        var decoded = Assert.IsType<SimActivate>(SimCodec.Decode(encoded.AsMemory(4)));

        Assert.Equal(Convert.ToHexString(encoded), Convert.ToHexString(SimCodec.Encode(decoded)));
        var strike = Assert.IsType<SimAction.Strike>(decoded.Effect.Action);
        Assert.True(strike.IsValid);
        Assert.Equal(3, strike.Shape);
        Assert.Equal(new double[] { 0.6, 0.0, 0.8 }, strike.Direction);
        Assert.Equal(21, strike.Pulses);
        Assert.Equal(new double[] { 10000, 10000, 10000, 10000 }, strike.Damage);
        Assert.Equal((10000.0, 1000.0, 30000.0, 300000.0), (strike.WarningMs, strike.PulseMs, strike.ImmobileMs, strike.NoJumpMs));
    }

    [Fact]
    public void A_titan_doomsday_roundtrips_its_delay_damage_and_windows_through_the_sim_codec()
    {
        // protocol 26: SimAction tag 14, one hit on the locked target after its delay, the ship held for its windows
        var message = Assert.IsType<SimActivate>(MessageOf(Case("valid", "activate a titan doomsday (Judgment)")));
        var encoded = SimCodec.Encode(message);
        var decoded = Assert.IsType<SimActivate>(SimCodec.Decode(encoded.AsMemory(4)));

        Assert.Equal(Convert.ToHexString(encoded), Convert.ToHexString(SimCodec.Encode(decoded)));
        Assert.Equal(1000000000002, decoded.Effect.TargetId);
        var hit = Assert.IsType<SimAction.Doomsday>(decoded.Effect.Action);
        Assert.True(hit.IsValid);
        Assert.Equal((9000.0, 30000.0, 300000.0), (hit.DelayMs, hit.ImmobileMs, hit.NoJumpMs));
        Assert.Equal(new double[] { 2400000.0, 0.0, 0.0, 0.0 }, hit.Damage);
    }

    [Fact]
    public void A_titan_doomsday_with_a_negative_delay_is_not_encoded()
    {
        var message = Assert.IsType<SimActivate>(MessageOf(Case("valid", "activate a titan doomsday (Judgment)")));
        var bad = message with { Effect = message.Effect with { Action = (message.Effect.Action as SimAction.Doomsday)! with { DelayMs = -1.0 } } };

        Assert.Throws<ArgumentException>(() => SimCodec.Encode(bad));
    }

    [Fact]
    public void A_strike_that_the_simulation_would_refuse_is_not_encoded()
    {
        var message = Assert.IsType<SimActivate>(MessageOf(Case("valid", "activate a doomsday strike (Bosonic Field Generator)")));
        var bad = message with { Effect = message.Effect with { Action = (message.Effect.Action as SimAction.Strike)! with { Direction = [0.6, 0.0, 0.9] } } };

        Assert.Throws<ArgumentException>(() => SimCodec.Encode(bad));
    }

    [Fact]
    public void A_reactive_armor_hardener_action_roundtrips_its_seed_step_and_attributes_through_the_sim_codec()
    {
        var baseline = Assert.IsType<SimActivate>(MessageOf(Case("valid", "activate an ECM burst")));
        var action = new SimAction.ResistanceShift(new double[] { 0.85, 0.85, 0.85, 0.85 }, 0.06, new int[] { 267, 270, 269, 268 });
        var message = baseline with { Effect = baseline.Effect with { Action = action } };
        var encoded = SimCodec.Encode(message);
        var decoded = Assert.IsType<SimActivate>(SimCodec.Decode(encoded.AsMemory(4)));

        Assert.Equal(Convert.ToHexString(encoded), Convert.ToHexString(SimCodec.Encode(decoded)));
        var shift = Assert.IsType<SimAction.ResistanceShift>(decoded.Effect.Action);
        Assert.Equal(new double[] { 0.85, 0.85, 0.85, 0.85 }, shift.Seed);
        Assert.Equal(0.06, shift.Shift);
        Assert.Equal(new int[] { 267, 270, 269, 268 }, shift.AttributeIds);
    }

    [Fact]
    public void The_armor_pin_of_a_ship_roundtrips_and_a_pin_that_is_not_a_flag_is_refused()
    {
        var ship = Assert.IsType<SimShipChanged>(MessageOf(Case("valid", "new ship numbers with the armor pinned by a polarized weapon")));
        Assert.True(ship.Ship.Vitals!.ArmorPinned);
        var decoded = Assert.IsType<SimShipChanged>(SimCodec.Decode(SimCodec.Encode(ship).AsMemory(4)));
        Assert.True(decoded.Ship.Vitals!.ArmorPinned);
        Assert.Equal(ship.Ship.Vitals.Resonances.Armor, decoded.Ship.Vitals.Resonances.Armor);

        var unpinned = Assert.IsType<SimShipChanged>(MessageOf(Case("valid", "new ship numbers")));
        Assert.False(unpinned.Ship.Vitals!.ArmorPinned);
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


    [Fact]
    public void A_drone_work_cycle_roundtrips_its_profile_and_roll()
    {
        var expected = new SimDroneWorkCycle(42, 7, 8, 9, 10, 11, 12, SimDroneCommands.MineRepeatedly, 25, 1.5, 10, 3, 0.25);
        var encoded = SimCodec.Encode(expected);

        Assert.Equal(expected, SimCodec.Decode(encoded.AsMemory(4)));
    }

    [Fact]
    public void A_module_work_cycle_roundtrips_its_profile_and_its_three_rolls()
    {
        var expected = new SimModuleWorkCycle(
            42, 7, 8, 9, 10, 11, 12, SimDroneCommands.MineRepeatedly, 20, 1.0, 0.1, 0.05, 2.0, 0, 0.25, 0.5, 0.75);
        var encoded = SimCodec.Encode(expected);

        Assert.Equal(expected, SimCodec.Decode(encoded.AsMemory(4)));
    }

    [Fact]
    public void A_fuel_cycle_roundtrips_as_a_module_work_cycle_with_no_mining()
    {
        var expected = new SimModuleWorkCycle(42, 7, 8, 9, 10, 0, 13, SimModuleWorkCommands.ConsumeFuel, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var encoded = SimCodec.Encode(expected);

        Assert.Equal(expected, SimCodec.Decode(encoded.AsMemory(4)));
    }

    [Fact]
    public void A_fuel_cycle_that_carries_mining_numbers_is_refused()
    {
        var encoded = SimCodec.Encode(new SimModuleWorkCycle(42, 7, 8, 9, 10, 0, 13, SimModuleWorkCommands.ConsumeFuel, 20, 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.Throws<SimLinkException>(() => SimCodec.Decode(encoded.AsMemory(4)));
    }

    [Fact]
    public void An_effect_that_burns_fuel_roundtrips_its_flag_and_one_that_does_not_keeps_it_clear()
    {
        var burning = new SimEffect(10, 20280, 6582, "siegeMode", 300000, 0, 1, 0, null, null, [], [], null, null, BurnsFuel: true);
        var free = burning with { BurnsFuel = false };

        var decodedBurning = Assert.IsType<SimActivate>(SimCodec.Decode(SimCodec.Encode(new SimActivate(42, burning)).AsMemory(4))).Effect;
        var decodedFree = Assert.IsType<SimActivate>(SimCodec.Decode(SimCodec.Encode(new SimActivate(42, free)).AsMemory(4))).Effect;

        Assert.True(decodedBurning.BurnsFuel);
        Assert.False(decodedFree.BurnsFuel);
        Assert.Equal(Convert.ToHexString(SimCodec.Encode(new SimActivate(42, burning))), Convert.ToHexString(SimCodec.Encode(new SimActivate(42, decodedBurning))));
    }

    [Fact]
    public void An_effect_with_module_rules_roundtrips_them_and_one_without_keeps_the_flag_clear()
    {
        var rules = new SimModuleRules(46, 1, true, false, false, 180000.0);
        var ruled = new SimEffect(10, 21857, 6731, "effects.Afterburner", 10000, 5, 1000, 0, null, null, [], [], null, null, Rules: rules);
        var plain = ruled with { Rules = null };

        var decodedRuled = Assert.IsType<SimActivate>(SimCodec.Decode(SimCodec.Encode(new SimActivate(42, ruled)).AsMemory(4))).Effect;
        var decodedPlain = Assert.IsType<SimActivate>(SimCodec.Decode(SimCodec.Encode(new SimActivate(42, plain)).AsMemory(4))).Effect;

        Assert.Equal(rules, decodedRuled.Rules);
        Assert.Null(decodedPlain.Rules);
        Assert.Equal(Convert.ToHexString(SimCodec.Encode(new SimActivate(42, ruled))), Convert.ToHexString(SimCodec.Encode(new SimActivate(42, decodedRuled))));
    }

    [Fact]
    public void A_module_work_result_and_an_ended_module_roundtrip()
    {
        var result = new SimModuleWorkResult(42, 10, 11, 12, false, "NO_TARGET", 0, 0, true);
        Assert.Equal(result, SimCodec.Decode(SimCodec.Encode(result).AsMemory(4)));

        var ended = new SimModuleEnded(42, 10, SimModuleEndReasons.TargetLost);
        Assert.Equal(ended, SimCodec.Decode(SimCodec.Encode(ended).AsMemory(4)));
    }

    [Fact]
    public void A_module_work_cycle_whose_id_is_zero_or_whose_roll_is_one_is_refused()
    {
        var zero = SimCodec.Encode(new SimModuleWorkCycle(42, 7, 8, 9, 10, 11, 0, SimDroneCommands.MineRepeatedly, 20, 1, 0.1, 0.05, 2, 0, 0.25, 0.5, 0.75));
        Assert.Throws<SimLinkException>(() => SimCodec.Decode(zero.AsMemory(4)));
        var roll = SimCodec.Encode(new SimModuleWorkCycle(42, 7, 8, 9, 10, 11, 12, SimDroneCommands.MineRepeatedly, 20, 1, 0.1, 0.05, 2, 0, 1.0, 0.5, 0.75));
        Assert.Throws<SimLinkException>(() => SimCodec.Decode(roll.AsMemory(4)));
    }

    [Fact]
    public void A_drone_work_result_roundtrips_cargo_and_completion()
    {
        var expected = new SimDroneWorkResult(42, 10, 11, 12, true, "", 3, 2, true);
        var encoded = SimCodec.Encode(expected);

        Assert.Equal(expected, SimCodec.Decode(encoded.AsMemory(4)));
    }

    [Fact]
    public void A_mutadaptive_remote_repair_action_roundtrips_its_spool_parameters()
    {
        var action = new SimAction.Remote(
            new SimRemote.MutadaptiveRepair(SimLayer.Armor, 256, 0.1, 1.5), 12_000, 0, true, "remoteArmorRepair");
        var effect = new SimEffect(700, 35298, 8, "effects.RemoteArmorRepair", 6_000, 513, int.MaxValue, 701,
            null, null, [], [], Action: action);
        var encoded = SimCodec.Encode(new SimActivate(42, effect));
        var decoded = SimCodec.Decode(encoded.AsMemory(4));

        Assert.Equal(encoded, SimCodec.Encode(decoded));
    }

    [Fact]
    public void Remote_repair_cycle_request_and_result_roundtrip_the_correlation_and_lifecycle_fields()
    {
        var request = new SimRemoteRepairCycle(42, 700, 701, 3, 15, 12_345, 6_000, true, 256, 0.1, 1.5);
        var requestBytes = SimCodec.Encode(request);
        Assert.Equal(request, SimCodec.Decode(requestBytes.AsMemory(4)));

        var result = new SimRemoteRepairCycleResult(42, 700, 701, 3, true, "", 640);
        var resultBytes = SimCodec.Encode(result);
        Assert.Equal(result, SimCodec.Decode(resultBytes.AsMemory(4)));
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
    public void In_space_entry_round_trips_the_saved_pose()
    {
        var position = new double[] { 100.0, 200.0, 300.0 };
        var direction = new double[] { 0.0, 0.0, 1.0 };
        var message = new SimEnter(7, 30_000_142, EnterHow.InSpace, 0, ShipOf(Case("valid", "enter by undocking")["ship"]!), position, direction);

        var decoded = Assert.IsType<SimEnter>(SimCodec.Decode(SimCodec.Encode(message).AsMemory(4)));

        Assert.Equal(position, decoded.Position?.ToArray());
        Assert.Equal(direction, decoded.Direction?.ToArray());
    }

    [Fact]
    public void A_module_count_the_frame_cannot_hold_is_refused_without_allocating_for_it()
    {
        var good = SimCodec.Encode(MessageOf(Case("valid", "enter by undocking")));
        // the count is the four bytes before the two 16-byte modules and the seven 8-byte numbers at the end of the frame: the six sensor numbers (protocol 27)
        // and the drone damage multiplier (protocol 47)
        var at = good.Length - (7 * 8) - (2 * 16) - 4;
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
