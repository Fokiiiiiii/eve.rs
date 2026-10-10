using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

[Service("beyonce")]
public sealed partial class BeyonceService(SpaceSessions space, Func<long> nowUnixMs, FleetStore? fleetStore = null, Func<long, ClientSession?>? sessionForCharacter = null) : IBoundObjectReleaseHandler
{
    private readonly Binding _binding = new(nowUnixMs);

    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _binding.Release(oid);

    private static double? Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number => number.Value,
        _ => null,
    };


    /// <summary>Resolves a bookmark only when the caller may warp to it. The host supplies the persisted/access-checked lookup.</summary>
    public Func<long, long, CancellationToken, Task<WarpCoordinates?>>? BookmarkTargetForCharacter { get; set; }

public Func<long, long, CancellationToken, Task<int>>? SkillLevelOfCharacter { get; set; }

    private static WarpCoordinates? Coordinates(PyValue value)
    {
        if (value is PyObject objectValue)
        {
            return Coordinates(objectValue.Args);
        }
        if (value is PyTuple tuple && tuple.Items.Count >= 3
            && Number(tuple.Items[0]) is { } tx
            && Number(tuple.Items[1]) is { } ty
            && Number(tuple.Items[2]) is { } tz)
        {
            return new WarpCoordinates(0, tx, ty, tz);
        }
        if (value is PyList list && list.Items.Count >= 3
            && Number(list.Items[0]) is { } lx
            && Number(list.Items[1]) is { } ly
            && Number(list.Items[2]) is { } lz)
        {
            return new WarpCoordinates(0, lx, ly, lz);
        }

        var entries = value switch
        {
            PyDict dictionary => dictionary.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => null,
        };
        if (entries is null)
        {
            return null;
        }

        double? Field(string name)
        {
            foreach (var entry in entries)
            {
                if (JsSemantics.PlainText(entry.Key) == name)
                {
                    return Number(entry.Value);
                }
            }
            return null;
        }

        return Field("x") is { } x && Field("y") is { } y && Field("z") is { } z
            ? new WarpCoordinates(0, x, y, z)
            : null;
    }

    /// <summary>The proxy node serves every ballpark.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>
    /// <c>MachoBindObject((solarSystemID, groupID), (method, args, kwargs))</c>: binds the ballpark of the solar system the ship is in. Once the
    /// answer has gone the simulation sends the scene.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        if (Binding.PlaceOf(call.Arg(0)) is not { } place || call.Session.GetInt("solarsystemid") != place || !space.IsInSpace(call.Session))
        {
            return None;
        }
        var (bound, oid) = _binding.Bind(call.Session);
        var result = await Binding.NestedAsync(this, call, oid);
        call.Session.AfterResponse(() => space.Bind(call.Session));
        return Py.Tuple(bound, result);
    }

    private const long BeyonceViewRoleMask = 140667119611150336L;

    /// <summary>The host can provide the live bubble lookup when the simulation exposes it.</summary>
    public Func<ServiceCall, long, CancellationToken, ValueTask<long?>>? BubbleIdForItem { get; set; }

    [Method("AdminGetBubbleID")]
    public async ValueTask<PyValue> AdminGetBubbleID(ServiceCall call)
    {
        var itemId = call.Arg(0) switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => 0,
        };
        var roleMask = call.Session.GetInt("role") ??
            call.Session.GetInt("accountRole") ??
            call.Session.GetInt("accountRoles") ?? 0;
        if (itemId <= 0 || (roleMask & BeyonceViewRoleMask) == 0)
            return None;

        var bubbleId = BubbleIdForItem is { } lookup
            ? await lookup(call, itemId, call.Cancellation)
            : call.Session.GetInt("shipid") == itemId
                ? call.Session.GetInt("ballparkid")
                : null;
        return bubbleId is > 0 ? new PyLong(bubbleId.Value) : None;
    }

    /// <summary>The host supplies the persisted cyno jammer activation time for a solar system.</summary>
    public Func<long, CancellationToken, ValueTask<long?>>? CynoJammerOnlineSimTimeForSystem { get; set; }

    [Method("GetCynoJammerState")]
    public async ValueTask<PyValue> GetCynoJammerState(ServiceCall call)
    {
        var solarSystemId = call.Session.GetInt("solarsystemid2") ?? 0;
        if (solarSystemId == 0)
            solarSystemId = call.Session.GetInt("solarsystemid") ?? 0;
        if (solarSystemId <= 0 || CynoJammerOnlineSimTimeForSystem is null)
            return None;

        var activationFileTime = await CynoJammerOnlineSimTimeForSystem(solarSystemId, call.Cancellation);
        return activationFileTime is { } fileTime ? new PyLong(fileTime) : None;
    }

    /// <summary><c>GetFormations()</c>: the formations a fleet can fly in, which the client keeps for the length of the session.</summary>
    [Method("GetFormations")]
    public PyValue GetFormations(ServiceCall call) => ObjectCaching.MethodResult(Formations(), nowUnixMs());

    private static PyValue Formations()
    {
        static PyValue Point(int x, int y, int z) => Py.Tuple(new PyInt(x), new PyInt(y), new PyInt(z));
        return new PyList([
            Py.Tuple(new PyStr("Diamond"), new PyList([Point(100, 0, 0), Point(0, 100, 0), Point(-100, 0, 0), Point(0, -100, 0)])),
            Py.Tuple(new PyStr("Arrow"), new PyList([Point(100, 0, -50), Point(50, 0, 0), Point(-100, 0, -50), Point(-50, 0, 0)])),
        ]);
    }

    /// <summary><c>UpdateStateRequest()</c>: the client wants the state of every ball again.</summary>
    [Method("UpdateStateRequest")]
    public PyValue UpdateStateRequest(ServiceCall call)
    {
        if (_binding.Owns(call))
        {
            space.UpdateState(call.Session);
        }
        return None;
    }

    /// <summary><c>CmdSetSpeedFraction(fraction)</c>: how much of its top speed the ship is to fly at.</summary>
    [Method("CmdSetSpeedFraction")]
    public PyValue CmdSetSpeedFraction(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } fraction)
        {
            space.Command(call.Session, SimCommands.SetSpeedFraction, fraction);
        }
        return None;
    }

    /// <summary><c>CmdGotoDirection(x, y, z)</c>: fly along this direction.</summary>
    [Method("CmdGotoDirection")]
    public PyValue CmdGotoDirection(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } x && Number(call.Arg(1)) is { } y && Number(call.Arg(2)) is { } z)
        {
            space.Command(call.Session, SimCommands.GotoDirection, x, y, z);
        }
        return None;
    }

    /// <summary><c>CmdStop()</c>: slow to a stop.</summary>
    [Method("CmdStop")]
    public PyValue CmdStop(ServiceCall call)
    {
        if (_binding.Owns(call))
        {
            space.Command(call.Session, SimCommands.Stop);
        }
        return None;
    }

    /// <summary><c>CmdFollowBall(targetID, range)</c>: go to a body and stay <c>range</c> metres off it.</summary>
    [Method("CmdFollowBall")]
    public PyValue CmdFollowBall(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } target)
        {
            space.Command(call.Session, SimCommands.FollowBall, target, Number(call.Arg(1)) ?? 0);
        }
        return None;
    }

    /// <summary><c>CmdOrbit(targetID, distance)</c>: circle a body <c>distance</c> metres off it.</summary>
    [Method("CmdOrbit")]
    public PyValue CmdOrbit(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } target)
        {
            space.Command(call.Session, SimCommands.Orbit, target, Number(call.Arg(1)) ?? 0);
        }
        return None;
    }

    /// <summary><c>CmdAlignTo(targetID)</c> (or <c>dstID=</c>): turn towards a body and fly at it.</summary>
    [Method("CmdAlignTo")]
    public PyValue CmdAlignTo(ServiceCall call)
    {
        var target = Number(call.Arg(0)) is { } positional && positional != 0
            ? positional
            : call.Kwarg("dstID") is { } named ? Number(named) : null;
        if (_binding.Owns(call) && target is { } id && id > 0)
        {
            space.Command(call.Session, SimCommands.AlignTo, id);
        }
        return None;
    }

    /// <summary>
    /// <c>CmdWarpToStuff(type, target, minRange=)</c>: warp to a system body (<c>"item"</c>) or bookmark (<c>"bookmark"</c>). Other destination types,
    /// scanned sites and the like are told as a warp that cannot be done).
    /// </summary>
    [Method("CmdWarpToStuff")]
    public async ValueTask<PyValue> CmdWarpToStuff(ServiceCall call)
    {
        if (!_binding.Owns(call))
        {
            return None;
        }

        var type = JsSemantics.PlainText(call.Arg(0));
        if (type is not ("item" or "bookmark" or "char" or "position" or "scan"))
        {
            throw WarpRefusal();
        }

        var coordinate = type == "position" ? Coordinates(call.Arg(1)) : null;
        var requestedTarget = type == "position" ? null : Number(call.Arg(1));
        if (type == "position")
        {
            if (coordinate is not { } parsedPoint
                || !double.IsFinite(parsedPoint.X)
                || !double.IsFinite(parsedPoint.Y)
                || !double.IsFinite(parsedPoint.Z))
            {
                throw WarpRefusal();
            }
        }
        else if (requestedTarget is not { } targetNumber
            || !double.IsFinite(targetNumber)
            || targetNumber <= 0
            || targetNumber > long.MaxValue
            || targetNumber != Math.Truncate(targetNumber))
        {
            throw WarpRefusal();
        }
        var targetId = requestedTarget is { } number ? (long)number : 0;
        var range = call.Kwarg("minRange") is { } named ? Number(named) ?? 0 : 0;
        if (!double.IsFinite(range))
        {
            throw WarpRefusal();
        }
        var fleetWarp = call.Kwarg("fleet") switch
        {
            PyBool flag => flag.Value,
            PyInt integer => integer.Value != 0,
            PyLong integer => integer.Value != 0,
            _ => false
        };
        var characterId = call.Session.CharacterId ?? call.Session.GetInt("charid") ?? 0;

        FleetSnapshot? fleet = null;
        FleetMemberSnapshot? requester = null;
        if (fleetWarp || type == "char")
        {
            if (fleetStore is null || characterId <= 0
                || await fleetStore.FleetIdForCharacterAsync(characterId, call.Cancellation) is not { } fleetId
                || await fleetStore.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            {
                throw WarpRefusal();
            }
            fleet = snapshot;
            requester = snapshot.Members.FirstOrDefault(member => member.CharacterId == characterId);
            if (requester is null)
            {
                throw WarpRefusal();
            }
            if (fleetWarp && requester.Role is not (FleetStore.Leader or FleetStore.WingCommander or FleetStore.SquadCommander))
            {
                throw WarpRefusal();
            }
        }

        var callerSystem = space.SolarSystemOf(call.Session);
        if (type == "position")
        {
            if (callerSystem is not { } systemId || coordinate is not { } position)
            {
                throw WarpRefusal();
            }
            coordinate = position with { SolarSystemId = systemId };
        }
        else if (type == "bookmark")
        {
            if (characterId <= 0 || callerSystem is not { } systemId
                || BookmarkTargetForCharacter is not { } resolveBookmark
                || await resolveBookmark(characterId, targetId, call.Cancellation) is not { } bookmark
                || bookmark.SolarSystemId != systemId
                || !double.IsFinite(bookmark.X)
                || !double.IsFinite(bookmark.Y)
                || !double.IsFinite(bookmark.Z))
            {
                throw WarpRefusal();
            }
            coordinate = bookmark;
        }
        else if (type == "scan")
        {
            // a scan result is warped to only as the reference allows: the 100 percent place of the last completed scan in this system (no fleet warp)
            if (fleetWarp || callerSystem is not { } scanSystem || space.WarpAuthority.Resolve(call.Session, scanSystem, targetId) is not { } place)
            {
                throw WarpRefusal();
            }
            coordinate = new WarpCoordinates(scanSystem, place[0], place[1], place[2]);
        }
        else if (type == "char")
        {
            var targetCharacterId = targetId;
            if (fleet is null || fleet.Members.All(member => member.CharacterId != targetCharacterId)
                || sessionForCharacter?.Invoke(targetCharacterId) is not { } targetSession
                || targetSession.CharacterId != targetCharacterId
                || callerSystem is not { } targetSystem
                || space.SolarSystemOf(targetSession) != targetSystem
                || space.ShipIdOf(targetSession) is not { } targetShipId)
            {
                throw WarpRefusal();
            }
            targetId = targetShipId;
        }

        if (!fleetWarp)
        {
            return coordinate is { } destinationPoint
                ? await space.WarpToPointAsync(call.Session, destinationPoint.X, destinationPoint.Y, destinationPoint.Z, range, call.Cancellation)
                : await space.WarpAsync(call.Session, targetId, range, call.Cancellation);
        }
        if (fleet is null || requester is null || sessionForCharacter is null || callerSystem is not { } warpSystem)
        {
            throw WarpRefusal();
        }

        var scopedMembers = fleet.Members.Where(member => requester.Role switch
        {
            FleetStore.Leader => true,
            FleetStore.WingCommander => member.WingId == requester.WingId,
            FleetStore.SquadCommander => member.WingId == requester.WingId && member.SquadId == requester.SquadId,
            _ => false
        });
        if (!scopedMembers.Any(member => member.CharacterId == characterId))
        {
            throw WarpRefusal();
        }

        var recipients = scopedMembers
            .Where(member => member.CharacterId == characterId || member.AcceptsFleetWarp)
            .Select(member => (
                Member: member,
                Session: member.CharacterId == characterId ? call.Session : sessionForCharacter(member.CharacterId)))
            .Where(recipient => recipient.Session is { } session && space.SolarSystemOf(session) == warpSystem)
            .OrderBy(recipient => recipient.Member.CharacterId == characterId ? 0 : 1)
            .ToArray();

        var formationValue = call.Kwarg("fleetFormationSettings");
        var settings = FleetWarpFormation.Parse(formationValue);
        if (formationValue is not null && formationValue != PyNone.Instance && settings is null)
        {
            throw WarpRefusal();
        }
        if (settings is { } requestedFormation)
        {
            var formationSkillLevel = SkillLevelOfCharacter is { } readFormationSkill
                ? await readFormationSkill(characterId, FleetWarpFormation.FormationSkillTypeId, call.Cancellation)
                : 0;
            var distanceSkillLevel = requestedFormation.Type is >= 1 and <= 4 && SkillLevelOfCharacter is { } readDistanceSkill
                ? await readDistanceSkill(characterId, FleetWarpFormation.FormationDistanceSkillTypeId, call.Cancellation)
                : 0;
            if (!FleetWarpFormation.Available(requestedFormation, formationSkillLevel, distanceSkillLevel))
            {
                throw WarpRefusal();
            }
        }
        Dictionary<long, WarpCoordinates> formationDestinations = [];
        if (settings is { } formation)
        {
            var commanderShip = space.ShipIdOf(call.Session);
            if (commanderShip is not { } commanderShipId
                || await space.PositionOfAsync(call.Session, commanderShipId, call.Cancellation) is not { } commanderStart)
            {
                throw WarpRefusal();
            }
            var landingPoint = coordinate is { } requestedPoint
                ? FleetWarpFormation.LandingPoint(requestedPoint, commanderStart, range)
                : await space.WarpTargetPointAsync(call.Session, targetId, range, call.Cancellation);
            if (landingPoint is not { } landing)
            {
                throw WarpRefusal();
            }

            Dictionary<long, WarpCoordinates> starts = [];
            if (formation.Type == 5)
            {
                foreach (var recipient in recipients.Where(item => item.Member.CharacterId != characterId))
                {
                    if (recipient.Session is not { } memberSession
                        || space.ShipIdOf(memberSession) is not { } shipId
                        || await space.PositionOfAsync(memberSession, shipId, call.Cancellation) is not { } memberStart)
                    {
                        continue;
                    }
                    starts[recipient.Member.CharacterId] = memberStart;
                }
            }

            var followerCount = recipients.Count(item => item.Member.CharacterId != characterId);
            var followerIndex = 0;
            foreach (var recipient in recipients.Where(item => item.Member.CharacterId != characterId))
            {
                starts.TryGetValue(recipient.Member.CharacterId, out var memberStart);
                if (FleetWarpFormation.Destination(
                    formation,
                    followerIndex++,
                    followerCount,
                    commanderStart,
                    landing,
                    starts.ContainsKey(recipient.Member.CharacterId) ? memberStart : null) is { } destination)
                {
                    formationDestinations[recipient.Member.CharacterId] = destination;
                }
            }
        }

        MachoWrappedException? requesterRefusal = null;
        foreach (var recipient in recipients)
        {
            if (recipient.Session is not { } session)
            {
                continue;
            }
            try
            {
                if (recipient.Member.CharacterId != characterId
                    && settings is not null)
                {
                    if (!formationDestinations.TryGetValue(recipient.Member.CharacterId, out var destination))
                    {
                        continue;
                    }
                    await space.WarpToPointAsync(session, destination.X, destination.Y, destination.Z, 0, call.Cancellation);
                }
                else if (coordinate is { } fleetPoint)
                {
                    await space.WarpToPointAsync(session, fleetPoint.X, fleetPoint.Y, fleetPoint.Z, range, call.Cancellation);
                }
                else
                {
                    await space.WarpAsync(session, targetId, range, call.Cancellation);
                }
                if (recipient.Member.CharacterId != characterId)
                {
                    List<System.Collections.Generic.KeyValuePair<PyValue, PyValue>> warpInfo = [];
                    if (targetId > 0)
                        warpInfo.Add(new(new PyStr("celestialID"), new PyLong(targetId)));
                    session.SendNotification("OnRemoteMessage", "clientID", new PyStr("FleetWarp"), new PyDict(warpInfo));
                }
            }
            catch (MachoWrappedException error) when (recipient.Member.CharacterId == characterId)
            {
                requesterRefusal = error;
            }
            catch (MachoWrappedException)
            {
                // A single member's warp refusal must not stop the rest of the command scope.
            }
        }
        if (requesterRefusal is not null)
        {
            throw requesterRefusal;
        }
        return None;
    }


    private static MachoWrappedException WarpRefusal() =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot warp there right now.")));




    /// <summary>Autopilot uses the same warp command but arrives 10 km from its destination.</summary>
    [Method("CmdWarpToStuffAutopilot")]
    public async ValueTask<PyValue> CmdWarpToStuffAutopilot(ServiceCall call)
    {
        if (!_binding.Owns(call))
        {
            return None;
        }
        var target = Number(call.Arg(0)) ?? 0;
        return await space.WarpAsync(call.Session, (long)target, 10_000, call.Cancellation);
    }

    /// <summary>
    /// <c>CmdStargateJump(fromGate, toGate, shipID)</c>: go into a stargate. The answer is the bound object and the time it can be used from again;
    /// the ship is in the other solar system a few seconds later, when the client is told the session has changed.
    /// </summary>
    [Method("CmdStargateJump")]
    public async ValueTask<PyValue> CmdStargateJump(ServiceCall call) =>
        _binding.Owns(call) && Number(call.Arg(0)) is { } gate
            ? await space.JumpAsync(call.Session, (long)gate, (long)(Number(call.Arg(1)) ?? 0), (long)(Number(call.Arg(2)) ?? 0), call.BoundObjectId, call.Cancellation)
            : None;

    /// <summary>
    /// <c>CmdJumpToFleetModuleBeacon(charID, beaconID, solarsystemID)</c>: the jump drive to the cynosural field a fleet member lit (the beacon is the field, and
    /// its lighter is the character). The answer is the bound object and the time the ship can be used from again.
    /// </summary>
    [Method("CmdJumpToFleetModuleBeacon")]
    public async ValueTask<PyValue> CmdJumpToFleetModuleBeacon(ServiceCall call) =>
        _binding.Owns(call) && space.JumpDrive is { } drive && Number(call.Arg(0)) is { } lighter && Number(call.Arg(1)) is { } beacon && Number(call.Arg(2)) is { } system
            ? await drive.JumpToFieldAsync(call.Session, (long)beacon, (long)lighter, (int)system, call.BoundObjectId, call.Cancellation)
            : None;

    /// <summary>
    /// <c>CmdJumpToFleetDeployableBeacon(deployableID, solarsystemID, beaconID)</c>: the jump drive to a cynosural field of the fleet by its deployable id.
    /// </summary>
    [Method("CmdJumpToFleetDeployableBeacon")]
    public async ValueTask<PyValue> CmdJumpToFleetDeployableBeacon(ServiceCall call) =>
        _binding.Owns(call) && space.JumpDrive is { } drive && Number(call.Arg(0)) is { } field && Number(call.Arg(1)) is { } system
            ? await drive.JumpToFieldAsync(call.Session, (long)field, 0, (int)system, call.BoundObjectId, call.Cancellation)
            : None;

    /// <summary>
    /// <c>CmdBridgeToFleetModuleBeacon(charID, beaconID, solarsystemID)</c>: the bridging ship opens a jump bridge to the cynosural field a fleet member lit (the
    /// beacon is the field). The client does not read an answer, so none is given.
    /// </summary>
    [Method("CmdBridgeToFleetModuleBeacon")]
    public async ValueTask<PyValue> CmdBridgeToFleetModuleBeacon(ServiceCall call)
    {
        if (_binding.Owns(call) && space.Bridges is { } bridges && Number(call.Arg(1)) is { } beacon && Number(call.Arg(2)) is { } system)
        {
            await bridges.OpenAsync(call.Session, (long)beacon, (int)system, call.Cancellation);
        }
        return None;
    }

    /// <summary>
    /// <c>CmdBridgeToFleetDeployableBeacon(deployableID, solarsystemID, beaconID)</c>: the bridging ship opens a jump bridge to the field of that deployable id.
    /// </summary>
    [Method("CmdBridgeToFleetDeployableBeacon")]
    public async ValueTask<PyValue> CmdBridgeToFleetDeployableBeacon(ServiceCall call)
    {
        if (_binding.Owns(call) && space.Bridges is { } bridges && Number(call.Arg(0)) is { } field && Number(call.Arg(1)) is { } system)
        {
            await bridges.OpenAsync(call.Session, (long)field, (int)system, call.Cancellation);
        }
        return None;
    }

    /// <summary>
    /// <c>CmdGroupJumpToFleetModuleBeacon(charID, beaconID, solarsystemID)</c>: the Black Ops makes a conduit jump to the cynosural field a fleet member lit,
    /// and the fleet ships in its 10 km go with it.
    /// </summary>
    [Method("CmdGroupJumpToFleetModuleBeacon")]
    public async ValueTask<PyValue> CmdGroupJumpToFleetModuleBeacon(ServiceCall call) =>
        _binding.Owns(call) && space.Conduits is { } conduits && Number(call.Arg(1)) is { } beacon && Number(call.Arg(2)) is { } system
            ? await conduits.GroupJumpAsync(call.Session, (long)beacon, (int)system, call.BoundObjectId, call.Cancellation)
            : None;

    /// <summary>
    /// <c>CmdGroupJumpToFleetDeployableBeacon(deployableID, solarsystemID, beaconID)</c>: the conduit jump to the field of that deployable id.
    /// </summary>
    [Method("CmdGroupJumpToFleetDeployableBeacon")]
    public async ValueTask<PyValue> CmdGroupJumpToFleetDeployableBeacon(ServiceCall call) =>
        _binding.Owns(call) && space.Conduits is { } conduits && Number(call.Arg(0)) is { } field && Number(call.Arg(1)) is { } system
            ? await conduits.GroupJumpAsync(call.Session, (long)field, (int)system, call.BoundObjectId, call.Cancellation)
            : None;

    /// <summary>
    /// <c>CmdJumpThroughFleet(otherCharID, otherShipID, beaconID, solarsystemID)</c>: the session's ship passes through the bridge of the ship
    /// <c>otherShipID</c> to the field <c>beaconID</c> in <c>solarsystemID</c>.
    /// </summary>
    [Method("CmdJumpThroughFleet")]
    public async ValueTask<PyValue> CmdJumpThroughFleet(ServiceCall call) =>
        _binding.Owns(call) && space.Bridges is { } bridges && Number(call.Arg(1)) is { } bridgeShip && Number(call.Arg(2)) is { } beacon && Number(call.Arg(3)) is { } system
            ? await bridges.JumpThroughAsync(call.Session, (long)bridgeShip, (long)beacon, (int)system, call.BoundObjectId, call.Cancellation)
            : None;

    /// <summary>
    /// <c>CmdDock(stationID, shipID)</c>: dock at a station. The answer is the time the docking was accepted; the docking itself is done a few
    /// seconds later, when the client is told the session has changed.
    /// </summary>
    [Method("CmdDock")]
    public async ValueTask<PyValue> CmdDock(ServiceCall call) =>
        _binding.Owns(call) && Number(call.Arg(0)) is { } station ? await space.DockAsync(call.Session, (long)station, call.Cancellation) : None;
}
