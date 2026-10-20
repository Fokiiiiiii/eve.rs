using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Dogma;
using Eve.Services.Space.Effects;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>A drone a pilot asks to launch: the item (a stack in the drone bay, or a drone of its own) and how many of it.</summary>
public readonly record struct DroneRequest(long ItemId, int Quantity);

/// <summary>What the client can ask of the drones of its ship (<c>ship.LaunchDrones</c>, <c>ship.ScoopDrone</c>, the <c>entity</c> service).</summary>
public interface ISpaceDrones
{
    /// <summary>Launches the drones asked for; answers the dictionary the client reads: for each item asked, the ids of the drones that went out and the refusals (as <c>(key, {notify})</c>) of the rest.</summary>
    Task<PyValue> LaunchAsync(ClientSession session, IReadOnlyList<DroneRequest> requests, CancellationToken cancellationToken);

    /// <summary>Gives drones an order (<see cref="SimDroneCommands"/>); answers a dictionary of the drones that refused it, each with why.</summary>
    Task<PyValue> CommandAsync(ClientSession session, byte command, long targetId, IReadOnlyList<long> droneIds, CancellationToken cancellationToken);

    /// <summary>Takes drones that have nobody to command them into the bay (<c>ship.ScoopDrone</c>); answers a dictionary of the drones that could not be.</summary>
/// <summary>Reconnects the character's nearby abandoned drones in the current solar system.</summary>
    Task<PyValue> ReconnectAsync(ClientSession session, CancellationToken cancellationToken);

    Task<PyValue> ScoopAsync(ClientSession session, IReadOnlyList<long> droneIds, CancellationToken cancellationToken);
}

/// <summary>
/// The drones of the pilots in space, as the host has them: a drone is an item that leaves the drone bay for the solar system when it is launched and goes back when it is scooped, and a ball of
/// the simulation (<c>eve_space::drones</c>) that circles its ship, goes at what the pilot sends it at and shoots it. What the simulation tells of the drones of a session (a drone that was taken
/// in, one that was let go, one that was destroyed) decides what becomes of the item.
/// </summary>
public sealed class SpaceDrones : ISpaceDrones, ISpaceDroneBays, IDisposable
{
    private const int MaxVelocityAttribute = 37;
    private const int FlyRangeAttribute = 416;
    private const int OrbitRangeAttribute = 157;
    private const int CruiseSpeedAttribute = 508;
    private const int EntityAttackRangeAttribute = 247;
    // The current SDE has no Drone Control Unit type; do not synthesize a max-active-drone bonus.
    private const int MaxActiveDronesAttribute = 352;
    private const int DroneControlDistanceAttribute = 458;
    private const double DefaultDroneControlRange = 20_000;
    private const int DroneBandwidthAttribute = 1271;
    private const int DroneBandwidthUsedAttribute = 1272;
    private const int QuantityAttribute = 805;
    private const int MassAttribute = 4;
    private const int VolumeAttribute = 161;
    private const int RadiusAttribute = 162;

    /// <summary>Where a drone circles when its type says nothing of it (the reference's minimum).</summary>
    private const double MinimumOrbitMeters = 500;

    private const double MaximumOrbitMeters = 5_000;

    /// <summary>How long after a drone's item moved the client is sent its row again (the reference's window refresh).</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    /// <summary>Where the items of what is gone are told to be (the client's junkyard).</summary>
    private const long Junkyard = 6;

    private const string NotUnderControl = "That drone is not currently under this ship's control.";

    /// <summary>A drone that is out: the item it is, the ship it belongs to and the pilot to tell.</summary>
    private sealed record Launched(
        long ItemId, int TypeId, long CharacterId, long CorporationId, long AllianceId, long WarFactionId,
        long ShipId, int SolarSystemId, ulong Controller, ClientSession Pilot, IReadOnlyDictionary<int, double> Attributes);

    private readonly ISimulation _sim;
    private readonly CharacterShips _ships;
    private readonly DogmaStatic _statics;
    private readonly SpaceSessions _space;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, Launched> _launched = new();
private readonly ConcurrentDictionary<long, Launched> _abandoned = new();
    private readonly ConcurrentDictionary<long, byte> _letGo = new();
    private long _requests;

    public SpaceDrones(ISimulation sim, CharacterShips ships, DogmaStatic statics, SpaceSessions space, Func<long> nowUnixMs, Action<string>? log = null)
    {
        _sim = sim;
        _ships = ships;
        _statics = statics;
        _space = space;
        _nowUnixMs = nowUnixMs;
        _log = log;
        _sim.Received += OnReceived;
        _sim.Lost += OnLost;
    }

public (long CharacterId, int TypeId, int SolarSystemId, long CorporationId, long AllianceId, long WarFactionId)? DamageOwnerOf(long droneId)
    {
        if (droneId <= 0)
            return null;
        if (_launched.TryGetValue(droneId, out var launched))
            return (launched.CharacterId, launched.TypeId, launched.SolarSystemId, launched.CorporationId, launched.AllianceId, launched.WarFactionId);
        if (_abandoned.TryGetValue(droneId, out var abandoned))
            return (abandoned.CharacterId, abandoned.TypeId, abandoned.SolarSystemId, abandoned.CorporationId, abandoned.AllianceId, abandoned.WarFactionId);
        return null;
    }

    public void Dispose()
    {
        _sim.Received -= OnReceived;
        _sim.Lost -= OnLost;
    }

    private static PyValue None => PyNone.Instance;

    private static PyValue Dict(IEnumerable<KeyValuePair<PyValue, PyValue>> entries) => new PyDict([.. entries]);

    /// <summary>The answer to an order that is not served: every drone it names refuses it with this text (the client shows each refusal as a message).</summary>
    internal static PyValue RefuseAll(IEnumerable<long> droneIds, string text) => Dict(droneIds.Select(id => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(id), Refusal(text))));

    private static PyValue Refusal(string text) => Py.Tuple(new PyStr("CustomNotify"), new PyDict([new(new PyStr("notify"), new PyStr(text))]));

    private long FileTimeNow() => Py.FileTimeFromUnixMs(_nowUnixMs());

    private ItemRow RowOf(Item item)
    {
        var type = _statics.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    /// <summary>The ids a drone order names, as the reference reads them: lists, tuples, sets and the keys of a dictionary, flattened, each once.</summary>
    public static IReadOnlyList<long> IdsOf(PyValue value)
    {
        var ids = new List<long>();
        void Walk(PyValue part, int depth)
        {
            if (depth > 8)
            {
                return;
            }
            switch (part)
            {
                case PyInt number:
                    ids.Add(number.Value);
                    break;
                case PyLong number:
                    ids.Add(number.Value);
                    break;
                case PyList list:
                    list.Items.ToList().ForEach(item => Walk(item, depth + 1));
                    break;
                case PyTuple tuple:
                    tuple.Items.ToList().ForEach(item => Walk(item, depth + 1));
                    break;
                case PyDict dict:
                    dict.Entries.ToList().ForEach(entry => Walk(entry.Key, depth + 1));
                    break;
                case PyObjectEx set:
                    Walk(set.Header, depth + 1);
                    set.List.ToList().ForEach(item => Walk(item, depth + 1));
                    break;
            }
        }
        Walk(value, 0);
        return [.. ids.Where(id => id > 0).Distinct()];
    }

    // ── launching ────────────────────────────────────────────────────────────

    public async Task<PyValue> LaunchAsync(ClientSession session, IReadOnlyList<DroneRequest> requests, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId || _space.SolarSystemOf(session) is not { } system
            || await _ships.LoadAsync(characterId, cancellationToken).ConfigureAwait(false) is not { } snapshot || snapshot.Ship.Id != shipId)
        {
            _log?.Invoke($"drones: session {session.SessionId} launched drones without an active ship in space");
            return Dict([]);
        }
        var controller = (ulong)session.SessionId;
        // the limit is the pilot's (the Drones skill gives it to the character: the client reads it there, DEVIATIONS #12) unless the ship has one of its own
        var maxActive = Math.Max(0, (int)Math.Max(snapshot.World.Get(shipId, MaxActiveDronesAttribute), snapshot.World.Get(snapshot.Character.Id, MaxActiveDronesAttribute)));
        var bandwidth = Math.Max(0, snapshot.World.Get(shipId, DroneBandwidthAttribute));
        var alreadyOut = _launched.Values.Where(d => d.Controller == controller).ToList();
        var active = alreadyOut.Count;
        var used = alreadyOut.Sum(d => BandwidthOf(d.Attributes));

        var answer = new List<KeyValuePair<PyValue, PyValue>>();
        var launchedNow = new List<(Launched Drone, DroneLaunch Launch)>();
        foreach (var request in requests)
        {
            var results = new List<PyValue>();
            answer.Add(new(InventoryWire.Id(request.ItemId), new PyList(results)));
            if (await _ships.ItemAsync(request.ItemId, cancellationToken).ConfigureAwait(false) is not { } source || source.OwnerId != characterId || source.LocationId != shipId
                || source.Flag != CharacterShips.DroneBayFlag)
            {
                results.Add(Refusal("That drone is not available in the active ship drone bay."));
                continue;
            }
            await _statics.EnsureTypesAsync([source.TypeId], cancellationToken).ConfigureAwait(false);
            if (_statics.Type(source.TypeId) is not { CategoryId: 18 } type)
            {
                results.Add(Refusal("That drone is not available in the active ship drone bay."));
                continue;
            }
            // a drone flies with the numbers dogma has of it: a mutated one takes its base from its input and its rolls from its own record
            var attributes = await _ships.DroneAttributesAsync(source, cancellationToken).ConfigureAwait(false);
            var stats = type with { Attributes = attributes };
            for (var count = 0; count < Math.Max(1, request.Quantity); count++)
            {
                if (maxActive <= 0 || active >= maxActive)
                {
                    results.Add(Refusal("Maximum active drones already in space."));
                    break;
                }
                var load = BandwidthOf(attributes);
                if ((load > 0 && bandwidth <= 0) || used + load > bandwidth)
                {
                    results.Add(Refusal("Not enough drone bandwidth to launch that drone."));
                    break;
                }
                var previewWeapon = NpcArmament.HullWeapon(stats, source.Id, _statics);
                var cycleMs = StaticDogma(stats, "duration");
                var workCapable = cycleMs > 0
                    && (StaticDogma(stats, "miningAmount") > 0 || StaticDogma(stats, "accessDifficultyBonus") > 0);
                if (previewWeapon is null && !workCapable && RemoteOf(stats, attribute => attributes.GetValueOrDefault(attribute, 0)) is null)
                {
                    results.Add(Refusal("That drone has no supported operation profile."));
                    break;
                }
                var launch = await _ships.LaunchDroneAsync(characterId, shipId, source.Id, system, cancellationToken).ConfigureAwait(false);
                if (launch is null)
                {
                    results.Add(Refusal("The requested drone stack is no longer available."));
                    break;
                }
                var drone = launch.Drone;
                // the stack that gave a unit is told at once; the drone itself once it is in space
                foreach (var change in launch.Changes.Where(c => c.After.Id != drone.Id))
                {
                    ShipNotifications.SendItemChange(session, change, RowOf);
                }
                snapshot.World.Add(ItemDogma.Of(drone, characterId, shipId, CharacterShips.DroneBayFlag, mutation: launch.Mutation));
                // what the ship's world does to the drone (its modules' bonuses) is part of the numbers the client and the simulation are given
                var effective = snapshot.World.Attributes(drone.Id);
                var flown = type with { Attributes = effective };
                SendPrime(session, InventoryWire.Id(shipId), drone with { LocationId = shipId, Flag = CharacterShips.DroneBayFlag }, effective);
                var weapon = previewWeapon is null ? null : NpcArmament.HullWeapon(flown, drone.Id, _statics);
                var remote = RemoteOf(flown, attribute => snapshot.World.Get(drone.Id, attribute));
                if (!await SpawnAsync(session, snapshot, drone, flown, weapon, remote, cancellationToken).ConfigureAwait(false))
                {
                    results.Add(Refusal("Unable to materialize drone in space."));
                    await _ships.ScoopDroneAsync(characterId, shipId, drone.Id, system, [], CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                var entry = new Launched(
                    drone.Id, drone.TypeId, characterId,
                    snapshot.Character.CorporationId, snapshot.Character.AllianceId ?? 0, snapshot.Character.FactionId ?? 0,
                    shipId, system, controller, session, effective);
                _launched[drone.Id] = entry;
                launchedNow.Add((entry, launch));
                results.Add(InventoryWire.Id(drone.Id));
                active++;
                used += load;
            }
        }

        foreach (var (entry, launch) in launchedNow)
        {
            var change = launch.Changes.First(c => c.After.Id == entry.ItemId);
            ShipNotifications.SendItemChange(session, change, RowOf);
            Later(() => ShipNotifications.SendItemChange(session, change, RowOf));
        }
        return Dict(answer);
    }

    private async Task<bool> SpawnAsync(ClientSession session, ShipSnapshot snapshot, Item drone, TypeDef type, SimNpcWeapon? weapon, SimDroneEffect? remote, CancellationToken cancellationToken)
    {
        var character = snapshot.Character;
        var hull = SimShips.OfType(drone.Id, type);
        var ship = hull with { OwnerId = character.Id, CorporationId = (int)character.CorporationId, AllianceId = (int)(character.AllianceId ?? 0), WarFactionId = (int)(character.FactionId ?? 0) };
        var maxVelocity = type.Attributes.GetValueOrDefault(MaxVelocityAttribute, 0);
        var cruise = type.Attributes.TryGetValue(CruiseSpeedAttribute, out var speed) ? Math.Max(0, speed) : maxVelocity;
        var fraction = maxVelocity > 0 ? Math.Clamp(cruise / maxVelocity, 0, 1) : 0;
        // how far it can shoot: its type's attack range, at least what its weapon reaches (`resolveDroneCombatSnapshot`)
        var remoteReach = remote?.Action is SimAction.Remote reach ? reach.Optimal : 0;
        var attackRange = Math.Max(type.Attributes.GetValueOrDefault(EntityAttackRangeAttribute, 0), Math.Max(weapon?.OptimalRange ?? 0, remoteReach));
        var mining = MiningProfile.From(snapshot.World, _statics, drone.Id);
        var work = new SimDroneWorkProfile(mining.MiningAmountM3, mining.CycleTimeMs, mining.WasteVolumeMultiplier, mining.WasteProbability, mining.AccessDifficultyBonus);
        var launch = new SimLaunchDrone((ulong)Interlocked.Increment(ref _requests), (ulong)session.SessionId, ship, weapon, type.GraphicId, OrbitOf(type), attackRange, fraction, work, remote);
        try
        {
            var reply = await _sim.LaunchDroneAsync(launch, cancellationToken).ConfigureAwait(false);
            if (reply.Session is not null)
            {
                return true;
            }
            _log?.Invoke($"drones: the simulation refused drone {drone.Id}: {reply.Error}");
            return false;
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"drones: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// The remote effect a drone's type carries: the first of its effects that is a projected effect (the same rows that serve the ship modules), worked out from the drone's own
    /// dogma; null when the type has none.
    /// </summary>
    private SimDroneEffect? RemoteOf(TypeDef type, Func<int, double> value) =>
        type.EffectIds.Select(_statics.Effect).Select(effect => effect is null ? null : ProjectedEffect.ForDrone(effect, value)).FirstOrDefault(remote => remote is not null);

    private double StaticDogma(TypeDef type, string attributeName) =>
        _statics.AttributeId(attributeName) is { } attribute
            ? type.Attributes.GetValueOrDefault(attribute, _statics.Attribute(attribute)?.DefaultValue ?? 0d)
            : 0d;

    /// <summary>How far a drone circles (<c>resolveDroneMovementProfile</c>): its type's fly range, else its orbit range, else the least.</summary>
    internal static double OrbitOf(TypeDef type)
    {
        if (type.Attributes.TryGetValue(FlyRangeAttribute, out var fly))
        {
            return Math.Min(MaximumOrbitMeters, Math.Max(0, fly));
        }
        return type.Attributes.TryGetValue(OrbitRangeAttribute, out var orbit) && orbit > 0 ? Math.Clamp(orbit, MinimumOrbitMeters, MaximumOrbitMeters) : MinimumOrbitMeters;
    }

    private static double BandwidthOf(IReadOnlyDictionary<int, double> attributes) => attributes.GetValueOrDefault(DroneBandwidthUsedAttribute, 0);

    /// <summary>
    /// <c>OnGodmaPrimeItem</c> of a drone: what dogma has of it (its attributes as the ship's world gives them), and the quantity, with the item as it is now. The client needs it before the drone comes into space or is told to
    /// come back; <paramref name="location"/> is where the reference says it is primed (the ship for a drone in the bay, the solar system for one in space).
    /// </summary>
    private void SendPrime(ClientSession session, PyValue location, Item drone, IReadOnlyDictionary<int, double> dogma)
    {
        // what the reference primes is the type's dogma attributes and the quantity: not the mass, volume and radius that the type's own columns give (they are told with the numbers of a whole as the
        // JavaScript marshal writes them: an int when whole, the same for the ones that are reals in the ship's own info)
        var attributes = dogma.Where(a => a.Key is not (MassAttribute or VolumeAttribute or RadiusAttribute)).ToDictionary(a => a.Key, a => a.Value);
        attributes[QuantityAttribute] = 1;
        var fileTime = FileTimeNow();
        session.SendNotification(
            "OnGodmaPrimeItem", "clientID", location,
            Py.KeyVal(
                ("itemID", InventoryWire.Id(drone.Id)),
                ("invItem", InventoryWire.PackedRow(RowOf(drone))),
                ("activeEffects", new PyDict([])),
                ("attributes", new PyDict([.. attributes.OrderBy(a => a.Key).Select(a => new KeyValuePair<PyValue, PyValue>(new PyInt(a.Key), Py.Number(a.Value)))])),
                ("description", new PyStr("drone")),
                ("time", new PyLong(fileTime)),
                ("wallclockTime", new PyLong(fileTime))));
    }

    private void Later(Action action) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(Settle).ConfigureAwait(false);
            action();
        }
        catch (Exception error)
        {
            _log?.Invoke($"drones: a notification could not be sent: {error.Message}");
        }
    });

    // ── orders ───────────────────────────────────────────────────────────────

    public async Task<PyValue> CommandAsync(ClientSession session, byte command, long targetId, IReadOnlyList<long> droneIds, CancellationToken cancellationToken)
    {
        if (droneIds.Count == 0 || !_space.IsInSpace(session))
        {
            return Dict([]);
        }
        var controller = (ulong)session.SessionId;
        if (command is SimDroneCommands.ReturnHome or SimDroneCommands.ReturnBay)
        {
            // the client is told what the drone is before it is told it is on its way home
            foreach (var id in droneIds)
            {
                if (_launched.TryGetValue(id, out var drone) && drone.Controller == controller)
                {
                    var inSpace = new Item(drone.ItemId, drone.TypeId, drone.CharacterId, drone.SolarSystemId, CharacterShips.InSpaceFlag, 1, true, null);
                    SendPrime(session, new PyInt(drone.SolarSystemId), inSpace, drone.Attributes);
                }
            }
        }
        if (command == SimDroneCommands.Abandon)
        {
            foreach (var id in droneIds.Where(id => _launched.TryGetValue(id, out var drone) && drone.Controller == controller))
            {
                _letGo[id] = 0;
            }
        }
        var controlRange = DefaultDroneControlRange;
        if (session.CharacterId is { } characterId
            && session.GetInt("shipid") is { } shipId
            && await _ships.LoadAsync(characterId, cancellationToken).ConfigureAwait(false) is { } snapshot
            && snapshot.Ship.Id == shipId)
        {
            var range = snapshot.World.Get(characterId, DroneControlDistanceAttribute);
            if (double.IsFinite(range) && range > 0)
            {
                controlRange = range;
            }
        }
        CommandReply reply;
        try
        {
            reply = await _sim.DroneCommandAsync(controller, command, targetId, droneIds, controlRange, cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"drones: {error.Message}");
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Space is not available right now. Try again in a moment.")));
        }
        var refused = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var result in reply.Drones.Where(r => r.Reason.Length > 0))
        {
            _letGo.TryRemove(result.DroneId, out _);
            refused.Add(new(InventoryWire.Id(result.DroneId), Refusal(Describe(result.Reason, command))));
        }
        return Dict(refused);
    }

    private static string Describe(string reason, byte command) => reason switch
    {
        "NOT_CONTROLLED" => NotUnderControl,
        "NO_PILOT" => "That pilot is not in space.",
        "NO_WRECK" => "No salvageable wreck was found.",
        "NO_TARGET" when command == SimDroneCommands.Engage => "That target cannot be engaged by drones.",
        "OUT_OF_RANGE" when command == SimDroneCommands.Reconnect => "That drone is too far away to reconnect.",
        "OUT_OF_RANGE" => "That target is out of drone control range.",
        "NO_OPPONENT" => "The drones found nothing to attack.",
        _ => "The drone cannot do that.",
    };

public async Task<PyValue> ReconnectAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (!_space.IsInSpace(session) || session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId
            || _space.SolarSystemOf(session) is not { } system
            || await _ships.LoadAsync(characterId, cancellationToken).ConfigureAwait(false) is not { } snapshot
            || snapshot.Ship.Id != shipId)
        {
            return Dict([]);
        }

        var controller = (ulong)session.SessionId;
        var maxActive = Math.Max(0, (int)Math.Max(
            snapshot.World.Get(shipId, MaxActiveDronesAttribute),
            snapshot.World.Get(snapshot.Character.Id, MaxActiveDronesAttribute)));
        var controlled = _launched.Values.Where(drone => drone.Controller == controller).ToList();
        var controlSlots = Math.Max(0, maxActive - controlled.Count);
        if (controlSlots == 0) return Dict([]);

        var bandwidth = Math.Max(0, snapshot.World.Get(shipId, DroneBandwidthAttribute));
        var usedBandwidth = controlled.Sum(drone => BandwidthOf(drone.Attributes));
        var candidates = new List<Launched>(controlSlots);
        foreach (var drone in _abandoned.Values
                     .Where(drone => drone.CharacterId == characterId && drone.SolarSystemId == system && drone.ShipId == shipId)
                     .OrderBy(drone => drone.ItemId))
        {
            if (candidates.Count >= controlSlots) break;
            var droneBandwidth = BandwidthOf(drone.Attributes);
            if (droneBandwidth > 0 && (bandwidth <= 0 || usedBandwidth + droneBandwidth > bandwidth)) continue;
            candidates.Add(drone);
            usedBandwidth += droneBandwidth;
        }
        if (candidates.Count == 0) return Dict([]);

        foreach (var drone in candidates)
        {
            var item = new Item(drone.ItemId, drone.TypeId, characterId, system, CharacterShips.InSpaceFlag, 1, true, null);
            SendPrime(session, InventoryWire.Id(shipId), item with { LocationId = shipId, Flag = CharacterShips.DroneBayFlag }, drone.Attributes);
        }

        var controlRange = snapshot.World.Get(characterId, DroneControlDistanceAttribute);
        if (!double.IsFinite(controlRange) || controlRange <= 0)
            controlRange = DefaultDroneControlRange;

        CommandReply reply;
        try
        {
            reply = await _sim.DroneCommandAsync(controller, SimDroneCommands.Reconnect, 0,
                candidates.Select(drone => drone.ItemId).ToArray(), controlRange, cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"drones: {error.Message}");
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Space is not available right now. Try again in a moment.")));
        }

        var refused = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var result in reply.Drones)
        {
            if (result.Reason.Length == 0 && _abandoned.TryRemove(result.DroneId, out var drone))
            {
                _launched[result.DroneId] = drone with { ShipId = shipId, Controller = controller, Pilot = session };
            }
            else
            {
                refused.Add(new(InventoryWire.Id(result.DroneId), Refusal(Describe(result.Reason.Length == 0 ? "NOT_RECONNECTABLE" : result.Reason, SimDroneCommands.Reconnect))));
            }
        }
        return Dict(refused);
    }

    public Task<PyValue> ScoopAsync(ClientSession session, IReadOnlyList<long> droneIds, CancellationToken cancellationToken)
    {
        // a drone that is out is its ship's until it is let go, and a drone that is let go leaves the scene: there is nothing here that has nobody to command it
        var refused = droneIds.Select(id => new KeyValuePair<PyValue, PyValue>(
            InventoryWire.Id(id),
            Refusal(_launched.ContainsKey(id) ? "That drone cannot currently be scooped into the drone bay." : "That drone is not in local space."))).ToList();
        return Task.FromResult(Dict(refused));
    }

    /// <summary>Capacity for the active ship's in-space drone-bay inventory (flag 87).</summary>
    public async Task<CharacterShips.Room?> RoomAsync(ClientSession session, long shipId, int flag, CancellationToken cancellationToken)
    {
        if (flag != CharacterShips.DroneBayFlag || !_space.IsInSpace(session)
            || session.CharacterId is not { } characterId
            || _space.SolarSystemOf(session) is not { } system)
        {
            return null;
        }
        return await _ships.DroneBayRoomInSpaceAsync(characterId, shipId, system, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes an in-space drone-bay Add through its live controlling scene object. The simulation owns distance and completion;
    /// its scoop event performs the reserved-capacity transaction and the normal item-change notifications.
    /// </summary>
    public async Task<long?> AddAsync(
        ClientSession session, long itemId, long sourceId, long destinationId, int destinationFlag, long? quantity,
        CancellationToken cancellationToken)
    {
        if (destinationFlag != CharacterShips.DroneBayFlag || quantity is { } requested && requested != 1
            || !_space.IsInSpace(session) || session.CharacterId is not { } characterId
            || _space.SolarSystemOf(session) is not { } system || sourceId != system
            || await _ships.LoadAsync(characterId, cancellationToken).ConfigureAwait(false) is not { } snapshot
            || snapshot.Ship.Id != destinationId || snapshot.Ship.LocationId != system
            || !_launched.TryGetValue(itemId, out var drone)
            || drone.CharacterId != characterId || drone.ShipId != destinationId || drone.SolarSystemId != system
            || drone.Controller != (ulong)session.SessionId || _letGo.ContainsKey(itemId)
            || await _ships.ItemAsync(itemId, cancellationToken).ConfigureAwait(false) is not { } item
            || item.OwnerId != characterId || item.LocationId != system || item.Flag != CharacterShips.InSpaceFlag || !item.Singleton)
        {
            return null;
        }

        var reply = await CommandAsync(session, SimDroneCommands.ReturnBay, destinationId, [itemId], cancellationToken).ConfigureAwait(false);
        return reply is PyDict { Entries.Count: 0 } ? itemId : null;
    }

    // ── what the simulation says ─────────────────────────────────────────────

    private void OnReceived(SimMessage message)
    {
        try
        {
            switch (message)
            {
                case SimDroneScooped scooped when _launched.TryRemove(scooped.DroneId, out var taken):
                    Run(() => TakeInAsync(taken), scooped.DroneId, "taken into the bay");
                    break;
                case SimDroneReleased released when _launched.TryRemove(released.DroneId, out var gone):
                    _letGo.TryRemove(released.DroneId, out _);
                    _abandoned[released.DroneId] = gone;
                    break;
                case SimDestroyed destroyed when _launched.TryRemove(destroyed.ShipId, out var lost):
                    Run(() => LoseAsync(lost), destroyed.ShipId, "destroyed");
                    break;
                case SimDestroyed destroyed when _abandoned.TryRemove(destroyed.ShipId, out var lost):
                    Run(() => LoseAsync(lost), destroyed.ShipId, "destroyed");
                    break;
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"drones: a message of the simulation could not be handled: {error}");
        }
    }

    private void Run(Func<Task> work, long droneId, string what) => _ = Task.Run(async () =>
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"drones: drone {droneId} {what} could not be dealt with: {error.Message}");
        }
    });

    /// <summary>The simulation lost its scenes: every drone that was out is gone with them, and its item is where it was (the next sweep takes it into the bay of its ship).</summary>
    private void OnLost()
    {
        _launched.Clear();
        _abandoned.Clear();
    }

    /// <summary>A drone came into reach after it was told to return to the bay: the item goes back into the bay and the client is told.</summary>
    private async Task TakeInAsync(Launched drone)
    {
        var stillOut = _launched.Values.Where(d => d.Controller == drone.Controller).Select(d => d.ItemId).ToArray();
        switch (await _ships.ScoopDroneAsync(drone.CharacterId, drone.ShipId, drone.ItemId, drone.SolarSystemId, stillOut).ConfigureAwait(false))
        {
            case DroneScooped scooped:
                ShipNotifications.SendItemChange(drone.Pilot, scooped.Change, RowOf);
                var settled = scooped.Change with { PreviousLocationId = scooped.Change.After.LocationId, PreviousFlag = scooped.Change.After.Flag };
                Later(() => ShipNotifications.SendItemChange(drone.Pilot, settled, RowOf));
                break;
            case DroneBayFull full:
                _log?.Invoke($"drones: drone {drone.ItemId} was taken in but the bay of ship {drone.ShipId} has {full.Available} m3 and it takes {full.Volume}: it stays in space");
                break;
            default:
                _log?.Invoke($"drones: drone {drone.ItemId} was taken in but is not in space any more");
                break;
        }
    }

    /// <summary>
    /// A drone is out of the scene without having been taken in. One the pilot let go is lost (its item is deleted, the client is told it went to the junkyard); one whose ship left the scene (it docked,
    /// jumped, logged off or was destroyed) is taken into the bay when there is room, as the reference does for a pilot that goes offline, and stays in space otherwise.
    /// </summary>
    /// <summary>A drone was destroyed (or let go): its item is deleted and the client is told it is gone.</summary>
    private async Task LoseAsync(Launched drone)
    {
        _letGo.TryRemove(drone.ItemId, out _);
        if (await _ships.RemoveDroneAsync(drone.CharacterId, drone.ItemId).ConfigureAwait(false) is not { } removed)
        {
            return;
        }
        await _statics.EnsureTypesAsync([removed.TypeId]).ConfigureAwait(false);
        ShipNotifications.SendItemChange(drone.Pilot, new ItemChanged(removed with { LocationId = Junkyard }, removed.LocationId, removed.Flag, removed.Quantity), RowOf);
    }
}
