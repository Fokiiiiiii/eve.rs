using System.Buffers.Binary;
using System.Text;

namespace Eve.Space;

public enum SimLinkError
{
    Empty,
    UnknownKind,
    Truncated,
    Trailing,
    Utf8,
    BadValue,
    FrameTooLarge,
}

/// <summary>The peer broke the link protocol; the connection should be closed.</summary>
public sealed class SimLinkException(SimLinkError error, string message) : Exception(message)
{
    public SimLinkError Error { get; } = error;
}

/// <summary>
/// The wire layout of <see cref="SimMessage"/>: <c>u32 length | u8 kind | body</c>, little endian, text as <c>u16 length + UTF-8</c>,
/// byte strings as <c>u32 length + bytes</c>. The Rust simulation and this codec are both checked against
/// oracle/fixtures/simlink/messages.json.
/// </summary>
public static partial class SimCodec
{
    public const ushort ProtocolVersion = 49;
    /// <summary>The largest frame either side accepts (1 MiB).</summary>
    public const int MaxFrame = 1 << 20;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decodes a frame's payload: the kind byte and the body (without the length prefix).</summary>
    public static SimMessage Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new SimLinkException(SimLinkError.Empty, "empty frame");
        }
        var r = new Reader(payload);
        SimMessage message;
        switch (payload.Span[0])
        {
            case Kind.Hello:
                message = new SimHello(r.U16());
                break;
            case Kind.HelloAck:
                message = new SimHelloAck(r.U16(), r.U32());
                break;
            case Kind.HeatProfile:
                message = new SimSetHeatProfile(r.U64(), ReadHeatProfile(ref r));
                break;
            case Kind.ModuleOverheat:
                {
                    var session = r.U64();
                    var module = r.I64();
                    var active = r.U8();
                    if (active > 1) throw new SimLinkException(SimLinkError.BadValue, "bad module overheat state");
                    message = new SimSetModuleOverheat(session, module, active == 1);
                    break;
                }
            case Kind.ModuleOffline:
                message = new SimSetModuleOffline(r.U64(), r.I64());
                break;
            case Kind.RackOverheat:
                {
                    var session = r.U64();
                    var rack = r.U8();
                    var active = r.U8();
                    if (rack > 2 || active > 1) throw new SimLinkException(SimLinkError.BadValue, "bad rack overheat state");
                    message = new SimSetRackOverheat(session, rack, active == 1);
                    break;
                }
            case Kind.Enter:
                {
                    var session = r.U64();
                    var system = r.I32();
                    var how = r.U8() switch
                    {
                        0 => EnterHow.Undock,
                        1 => EnterHow.InSpace,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad how"),
                    };
                    var station = r.I64();
                    var ship = ReadShip(ref r);
                    var position = how == EnterHow.InSpace
                        ? ReadOptionalVector(ref r) ?? throw new SimLinkException(SimLinkError.BadValue, "in-space entry has no position")
                        : null;
                    var direction = how == EnterHow.InSpace
                        ? ReadOptionalVector(ref r) ?? throw new SimLinkException(SimLinkError.BadValue, "in-space entry has no direction")
                        : null;
                    message = new SimEnter(session, system, how, station, ship, position, direction);
                    break;
                }
            case Kind.EnterReply:
                {
                    var session = r.U64();
                    var status = r.U8();
                    var reason = r.Text();
                    message = status switch
                    {
                        0 => new SimEnterReply(session, null),
                        1 => new SimEnterReply(session, reason),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad status"),
                    };
                    break;
                }
            case Kind.Leave:
                message = new SimLeave(r.U64(), (LeaveWhy)r.U8());
                break;
            case Kind.Bind:
                message = new SimBind(r.U64());
                break;
            case Kind.UpdateState:
                message = new SimUpdateState(r.U64());
                break;
            case Kind.Command:
                {
                    var session = r.U64();
                    var code = r.U8();
                    var args = new double[r.U8()];
                    for (var i = 0; i < args.Length; i++)
                    {
                        args[i] = r.F64();
                    }
                    message = new SimCommand(session, code, args);
                    break;
                }
            case Kind.ShipChanged:
                message = new SimShipChanged(r.U64(), ReadShip(ref r));
                break;
            case Kind.Notify:
                message = new SimNotify(r.U64(), r.Text(), r.Text(), r.Bytes());
                break;
            case Kind.Docked:
                message = new SimDocked(r.U64(), r.I64(), ReadCondition(ref r));
                break;
            case Kind.Condition:
                message = new SimConditionReply(r.U64(), r.U8(), ReadCondition(ref r));
                break;
            case Kind.Refused:
                message = new SimRefused(r.U64(), r.U8(), r.Text());
                break;
            case Kind.Accepted:
                message = new SimAccepted(r.U64(), r.U8(), r.I64());
                break;
            case Kind.Position:
                message = new SimPosition(r.U64(), r.U8(), ReadVector(ref r));
                break;
            case Kind.Approaching:
                message = new SimApproaching(r.U64(), r.U8(), r.F64());
                break;
            case Kind.Jumping:
                message = new SimJumping(r.U64(), r.U8(), r.I32(), r.I64());
                break;
            case Kind.Jumped:
                message = new SimJumped(r.U64(), r.I32(), r.I64(), r.I32(), r.I64());
                break;
            case Kind.Destroyed:
                message = new SimDestroyed(r.U64(), r.I64(), r.I64(), ReadVector(ref r), ReadVector(ref r), r.F64(), r.I64(), ReadWreckKind(ref r));
                break;
            case Kind.Respawn:
                message = new SimRespawn(r.U64(), ReadShip(ref r), ReadVector(ref r), ReadVector(ref r));
                break;
            case Kind.PlaceWreck:
                {
                    var system = r.I32();
                    var wreckId = r.I64();
                    var group = r.I32();
                    var kind = ReadWreckKind(ref r) ?? throw new SimLinkException(SimLinkError.BadValue, "bad wreck");
                    var position = ReadVector(ref r);
                    var direction = ReadVector(ref r);
                    var radius = r.F64();
                    var launcher = r.I64();
                    var credit = ReadCredit(ref r);
                    var empty = r.U8() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad is_empty"),
                    };
                    message = new SimPlaceWreck(system, wreckId, group, kind, position, direction, radius, launcher, credit, empty);
                    break;
                }
            case Kind.Spawn:
                {
                    var requestId = r.U64();
                    var solarSystemId = r.I32();
                    var ship = ReadShip(ref r);
                    var anchor = r.I64();
                    var position = ReadVector(ref r);
                    var velocity = ReadVector(ref r);
                    var direction = ReadVector(ref r);
                    var behavior = r.Text();
                    message = new SimSpawn(requestId, solarSystemId, ship, anchor, position, velocity, direction, behavior, ReadIdentity(ref r));
                    break;
                }
            case Kind.SpawnReply:
                {
                    var requestId = r.U64();
                    message = r.U8() switch
                    {
                        0 => new SimSpawnReply(requestId, r.U64(), null),
                        1 => new SimSpawnReply(requestId, null, r.Text()),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad status"),
                    };
                    break;
                }
            case Kind.LaunchDrone:
                message = new SimLaunchDrone(r.U64(), r.U64(), ReadShip(ref r), ReadOptionalWeapon(ref r), r.I32(), r.F64(), r.F64(), r.F64(), new SimDroneWorkProfile(r.F64(), r.F64(), r.F64(), r.F64(), r.F64()), ReadOptionalDroneEffect(ref r));
                break;
            case Kind.DroneCommand:
                {
                    var session = r.U64();
                    var command = r.U8();
                    var target = r.I64();
                    var count = r.U32();
                    if (r.Remaining < 8 || count > (r.Remaining - 8) / 8)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var drones = new long[count];
                    for (var i = 0; i < drones.Length; i++)
                    {
                        drones[i] = r.I64();
                    }
                    message = new SimDroneCommand(session, command, target, drones, r.F64());
                    break;
                }
            case Kind.DroneResults:
                {
                    var session = r.U64();
                    var command = r.U8();
                    var count = r.U32();
                    if (count > r.Remaining / 10)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var results = new SimDroneResult[count];
                    for (var i = 0; i < results.Length; i++)
                    {
                        results[i] = new SimDroneResult(r.I64(), r.Text());
                    }
                    message = new SimDroneResults(session, command, results);
                    break;
                }
            case Kind.DroneScooped:
                message = new SimDroneScooped(r.U64(), r.I64());
                break;
            case Kind.DroneReleased:
                message = new SimDroneReleased(r.U64(), r.I64(), ReadVector(ref r));
                break;
            case Kind.LaunchFighter:
                {
                    var requestId = r.U64();
                    var session = r.U64();
                    var ship = ReadShip(ref r);
                    var graphic = r.I32();
                    var cruise = r.F64();
                    var members = r.I32();
                    if (members is < 1 or > 64)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "the squadron has no members");
                    }
                    message = new SimLaunchFighter(requestId, session, ship, graphic, cruise, members);
                }
                break;
            case Kind.FighterSize:
                message = new SimFighterSize(r.U64(), r.I64(), r.I32());
                break;
            case Kind.UpdateEffect:
            {
                var (session, module) = (r.U64(), r.I64());
                var (duration, capacitor) = (r.F64(), r.F64());
                if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(capacitor) || capacitor < 0)
                {
                    throw new SimLinkException(SimLinkError.BadValue, "update effect");
                }
                var turret = r.U8() switch
                {
                    0 => null,
                    1 => ReadTurret(ref r),
                    _ => throw new SimLinkException(SimLinkError.BadValue, "update turret"),
                };
                message = new SimUpdateEffect(session, module, duration, capacitor, turret, ReadChanges(ref r));
                break;
            }
            case Kind.BuffState:
                {
                    var session = r.U64();
                    var ship = r.I64();
                    var count = r.U32();
                    // an entry takes 20 bytes, so a count the frame cannot hold is refused before anything is read for it
                    if (count > r.Remaining / 20)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "buff state count");
                    }
                    var entries = new SimBuffEntry[count];
                    for (var i = 0; i < entries.Length; i++)
                    {
                        var (collection, value, remaining) = (r.I32(), r.F64(), r.F64());
                        if (!double.IsFinite(value) || !double.IsFinite(remaining) || remaining < 0)
                        {
                            throw new SimLinkException(SimLinkError.BadValue, "buff state");
                        }
                        entries[i] = new SimBuffEntry(collection, value, remaining);
                    }
                    message = new SimBuffState(session, ship, entries);
                    break;
                }
            case Kind.FighterOrder:
                {
                    var session = r.U64();
                    var command = r.U8();
                    var target = r.I64();
                    var count = r.U32();
                    if (r.Remaining < 8 || count > (r.Remaining - 8) / 8)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var fighters = new long[count];
                    for (var i = 0; i < fighters.Length; i++)
                    {
                        fighters[i] = r.I64();
                    }
                    message = new SimFighterOrder(session, command, target, fighters, r.F64());
                    break;
                }
            case Kind.FighterResults:
                {
                    var session = r.U64();
                    var command = r.U8();
                    var count = r.U32();
                    if (count > r.Remaining / 10)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var results = new SimFighterResult[count];
                    for (var i = 0; i < results.Length; i++)
                    {
                        results[i] = new SimFighterResult(r.I64(), r.Text());
                    }
                    message = new SimFighterResults(session, command, results);
                    break;
                }
            case Kind.FighterLanded:
                message = new SimFighterLanded(r.U64(), r.I64());
                break;
            case Kind.FighterReleased:
                message = new SimFighterReleased(r.U64(), r.I64(), ReadVector(ref r));
                break;
            case Kind.EcmBurst:
                {
                    var session = r.U64();
                    var ship = r.I64();
                    var count = r.U32();
                    if (count > r.Remaining / 9)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var reached = new SimBurstReach[count];
                    for (var i = 0; i < reached.Length; i++)
                    {
                        var target = r.I64();
                        var jammed = r.U8() switch
                        {
                            0 => false,
                            1 => true,
                            _ => throw new SimLinkException(SimLinkError.BadValue, "ecm burst jam flag"),
                        };
                        reached[i] = new SimBurstReach(target, jammed);
                    }
                    message = new SimEcmBurst(session, ship, reached);
                    break;
                }
            case Kind.FighterGotoPoint:
                {
                    var session = r.U64();
                    var count = r.U32();
                    if (r.Remaining < 8 || count > (r.Remaining - 8) / 8)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var fighters = new long[count];
                    for (var i = 0; i < fighters.Length; i++)
                    {
                        fighters[i] = r.I64();
                    }
                    message = new SimFighterGotoPoint(session, fighters, ReadVector(ref r));
                }
                break;
            case Kind.DroneWorkCycle:
                message = new SimDroneWorkCycle(r.U64(), r.I64(), r.I64(), r.I32(), r.I64(), r.I64(), r.U64(), r.U8(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64());
                break;
            case Kind.DroneWorkResult:
                {
                    var session = r.U64();
                    var drone = r.I64();
                    var target = r.I64();
                    var cycleId = r.U64();
                    var accepted = r.U8();
                    var reason = r.Text();
                    var unitsRemaining = r.I64();
                    var deliveredUnits = r.I64();
                    var complete = r.U8();
                    if (cycleId == 0 || accepted > 1 || unitsRemaining < 0 || deliveredUnits < 0 || complete > 1)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad drone work result");
                    }
                    message = new SimDroneWorkResult(
                        session, drone, target, cycleId, accepted == 1, reason, unitsRemaining, deliveredUnits, complete == 1);
                    break;
                }
            case Kind.RemoteRepairCycle:
                {
                    var session = r.U64();
                    var module = r.I64();
                    var target = r.I64();
                    var cycleId = r.U64();
                    var cycleNumber = r.I32();
                    var cycleAtMs = r.I64();
                    var cycleDurationMs = r.I64();
                    var endsAfterCycle = r.U8();
                    var baseAmount = r.F64();
                    var bonusPerCycle = r.F64();
                    var bonusMax = r.F64();
                    if (cycleId == 0 || cycleNumber <= 0 || cycleDurationMs <= 0 || endsAfterCycle > 1 || !double.IsFinite(baseAmount) || baseAmount < 0 || !double.IsFinite(bonusPerCycle) || bonusPerCycle < 0 || !double.IsFinite(bonusMax) || bonusMax < 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad remote repair cycle");
                    }
                    message = new SimRemoteRepairCycle(session, module, target, cycleId, cycleNumber, cycleAtMs, cycleDurationMs, endsAfterCycle == 1, baseAmount, bonusPerCycle, bonusMax);
                    break;
                }
            case Kind.RemoteRepairCycleResult:
                {
                    var session = r.U64();
                    var module = r.I64();
                    var target = r.I64();
                    var cycleId = r.U64();
                    var accepted = r.U8();
                    var reason = r.Text();
                    var amount = r.F64();
                    if (cycleId == 0 || accepted > 1 || !double.IsFinite(amount) || amount < 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad remote repair cycle result");
                    }
                    message = new SimRemoteRepairCycleResult(session, module, target, cycleId, accepted == 1, reason, amount);
                    break;
                }
            case Kind.ModuleWorkCycle:
                {
                    var session = r.U64();
                    var character = r.I64();
                    var ship = r.I64();
                    var system = r.I32();
                    var module = r.I64();
                    var target = r.I64();
                    var cycleId = r.U64();
                    var command = r.U8();
                    var mining = r.F64();
                    var wasteMultiplier = r.F64();
                    var wasteProbability = r.F64();
                    var critProbability = r.F64();
                    var critMultiplier = r.F64();
                    var accessBonus = r.F64();
                    var wasteRoll = r.F64();
                    var roundingRoll = r.F64();
                    var critRoll = r.F64();
                    var recipients = ReadRecipients(ref r);
                    // probabilities are at most one, the rolls below one, the numbers finite and not negative; a mining cycle mines something
                    if (cycleId == 0 || command is not (SimDroneCommands.MineRepeatedly or SimDroneCommands.Salvage or SimModuleWorkCommands.ConsumeFuel
                            or SimModuleWorkCommands.ScanCargo or SimModuleWorkCommands.ScanShip or SimModuleWorkCommands.Burst or SimModuleWorkCommands.Panic)
                        || new[] { mining, wasteMultiplier, critMultiplier, accessBonus }.Any(n => !double.IsFinite(n) || n < 0)
                        || new[] { wasteProbability, critProbability }.Any(p => !double.IsFinite(p) || p < 0 || p > 1)
                        || new[] { wasteRoll, roundingRoll, critRoll }.Any(roll => !double.IsFinite(roll) || roll < 0 || roll >= 1)
                        || (command == SimDroneCommands.MineRepeatedly && mining <= 0)
                        // a fuel cycle and a scan carry no mining numbers: they ask for fuel or reveal a target only
                        || (command is SimModuleWorkCommands.ConsumeFuel or SimModuleWorkCommands.ScanCargo or SimModuleWorkCommands.ScanShip or SimModuleWorkCommands.Burst or SimModuleWorkCommands.Panic
                            && new[] { mining, wasteMultiplier, wasteProbability, critProbability, critMultiplier, accessBonus }.Any(n => n != 0))
                        // only a command burst names ships: the ships in reach of its cycle
                        || (command is not (SimModuleWorkCommands.Burst or SimModuleWorkCommands.Panic) && recipients is { Count: > 0 }))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad module work cycle");
                    }
                    message = new SimModuleWorkCycle(
                        session, character, ship, system, module, target, cycleId, command, mining, wasteMultiplier, wasteProbability, critProbability,
                        critMultiplier, accessBonus, wasteRoll, roundingRoll, critRoll, recipients);
                    break;
                }
            case Kind.ModuleWorkResult:
                {
                    var session = r.U64();
                    var module = r.I64();
                    var target = r.I64();
                    var cycleId = r.U64();
                    var accepted = r.U8();
                    var reason = r.Text();
                    var remaining = r.I64();
                    var delivered = r.I64();
                    var complete = r.U8();
                    if (cycleId == 0 || accepted > 1 || complete > 1)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad module work result");
                    }
                    message = new SimModuleWorkResult(session, module, target, cycleId, accepted == 1, reason, remaining, delivered, complete == 1);
                    break;
                }
            case Kind.ModuleEnded:
                {
                    var session = r.U64();
                    var module = r.I64();
                    var reason = r.U8();
                    if (reason is < SimModuleEndReasons.Done or > SimModuleEndReasons.NoFuel)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad module end reason");
                    }
                    message = new SimModuleEnded(session, module, reason);
                    break;
                }
            case Kind.Destroy:
                message = new SimDestroy(r.I64(), ReadCredit(ref r));
                break;
            case Kind.Targets:
                {
                    var session = r.U64();
                    var command = r.U8();
                    var pending = r.U8() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad pending"),
                    };
                    var count = r.U32();
                    // a target takes 8 bytes, so a count the frame cannot hold is refused before anything is allocated for it
                    if (count > r.Remaining / 8)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var targets = new long[count];
                    for (var i = 0; i < targets.Length; i++)
                    {
                        targets[i] = r.I64();
                    }
                    message = new SimTargets(session, command, pending, targets);
                    break;
                }
            case Kind.TargetRefused:
                message = new SimTargetRefused(r.U64(), r.U8(), r.Text(), r.I32(), r.F64(), r.F64());
                break;
            case Kind.Activate:
                message = new SimActivate(r.U64(), ReadEffect(ref r));
                break;
            case Kind.ChargeSpent:
                message = new SimChargeSpent(r.U64(), r.I64(), r.I64());
                break;
            case Kind.AdjustCharge:
                {
                    var request = r.U64();
                    var session = r.U64();
                    var module = r.I64();
                    var type = r.I32();
                    var op = r.U8();
                    var (first, second) = (r.I64(), r.I64());
                    message = new SimAdjustCharge(request, session, module, type, op switch
                    {
                        0 => new SimChargeOp.Add(first, second),
                        1 => new SimChargeOp.Remove(first),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "charge op"),
                    });
                    break;
                }
            case Kind.ChargeAdjusted:
                {
                    var request = r.U64();
                    var running = r.U8() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "running"),
                    };
                    message = new SimChargeAdjusted(request, running, r.I64(), r.I64());
                    break;
                }
            case Kind.PlaceContainer:
                {
                    var session = r.U64();
                    var container = r.I64();
                    var kind = ReadWreckKind(ref r) ?? throw new SimLinkException(SimLinkError.BadValue, "bad container");
                    message = new SimPlaceContainer(session, container, kind, r.F64(), r.I64(), ReadCredit(ref r), r.F64());
                    break;
                }
            case Kind.Arrived:
                message = new SimArrived(r.U64(), r.I64(), ReadVector(ref r));
                break;
            case Kind.ContainerPlaced:
                message = new SimContainerPlaced(r.U64(), r.I64(), ReadVector(ref r), ReadVector(ref r));
                break;
            case Kind.ChangeWreck:
                {
                    var session = r.U64();
                    var wreck = r.I64();
                    message = new SimChangeWreck(session, wreck, r.U8() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad is_empty"),
                    });
                    break;
                }
            case Kind.RemoveWreck:
                message = new SimRemoveWreck(r.I64(), r.I32());
                break;
            case Kind.Deployed:
                {
                    var system = r.I32();
                    var deployable = r.I64();
                    var name = r.Text();
                    var type = r.I32();
                    var group = r.I32();
                    var position = ReadVector(ref r);
                    var radius = r.F64();
                    var owner = r.I64();
                    var corporation = r.I32();
                    var alliance = r.I32();
                    var ship = r.I64();
                    var lifetime = r.F64();
                    if (!(double.IsFinite(lifetime) && lifetime >= 0 && double.IsFinite(radius) && radius >= 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad deployed");
                    }
                    message = new SimDeployed(system, deployable, name, type, group, position, radius, owner, corporation, alliance, ship, lifetime);
                    break;
                }
            case Kind.RemoveDeployed:
                message = new SimRemoveDeployed(r.I64());
                break;
            case Kind.PlaceProbe:
                {
                    var probe = r.I64();
                    var system = r.I32();
                    var name = r.Text();
                    var type = r.I32();
                    var group = r.I32();
                    var position = ReadVector(ref r);
                    var radius = r.F64();
                    var owner = r.I64();
                    var corporation = r.I32();
                    var alliance = r.I32();
                    var ship = r.I64();
                    if (!(double.IsFinite(radius) && radius >= 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad probe");
                    }
                    message = new SimPlaceProbe(probe, system, name, type, group, position, radius, owner, corporation, alliance, ship);
                    break;
                }
            case Kind.RemoveProbe:
                message = new SimRemoveProbe(r.I64());
                break;
            case Kind.PlaceDisruptor:
                {
                    var deployable = r.I64();
                    var system = r.I32();
                    var name = r.Text();
                    var type = r.I32();
                    var group = r.I32();
                    var position = ReadVector(ref r);
                    var radius = r.F64();
                    var range = r.F64();
                    var active = ReadFlag(ref r, "disruptor");
                    var owner = r.I64();
                    var corporation = r.I32();
                    var alliance = r.I32();
                    var ship = r.I64();
                    if (!(double.IsFinite(radius) && radius >= 0 && double.IsFinite(range) && range >= 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad disruptor");
                    }
                    message = new SimPlaceDisruptor(deployable, system, name, type, group, position, radius, range, active, owner, corporation, alliance, ship);
                    break;
                }
            case Kind.SetDisruptorActive:
                message = new SimSetDisruptorActive(r.I64(), ReadFlag(ref r, "disruptor"));
                break;
            case Kind.Measured:
                {
                    var session = r.U64();
                    var command = r.U8();
                    message = new SimMeasured(session, command, r.U8() switch
                    {
                        0 => null,
                        1 => r.F64(),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad distance"),
                    });
                    break;
                }
            case var other:
                throw new SimLinkException(SimLinkError.UnknownKind, $"unknown message kind {other}");
        }
        r.Finish();
        return message;
    }
}
