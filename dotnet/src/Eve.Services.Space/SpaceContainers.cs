using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The cans and wrecks beside a pilot's ship: opened from where the ship is, emptied into the hold, filled from it, and made when cargo is jettisoned. The items are the host's
/// (a can is an item in the solar system, what is in it is in it); the simulation has the ball of each and tells those who see it what is in it (ADR 0007). The rules are the
/// reference's: a container is opened within 2,500 m of its surface (<c>TargetTooFar</c> beyond, <c>FakeItemNotFound</c> when the pilot does not see it), what is in one that is
/// not the pilot's own or its corporation's is not taken while the safety is on (<c>SafetyActivated</c>), a jettisoned can lasts two hours and a new one is made once in three minutes,
/// and a can that is emptied goes at once.
/// </summary>
public sealed class SpaceContainers : ISpaceContainers, IDisposable
{
    /// <summary>How far from the surface of a container a pilot can open it.</summary>
    public const double OpenRange = 2500;

    /// <summary>How far ahead of the ship a jettisoned can is put.</summary>
    public const double JettisonDistance = 275;

    // a can has no faction at war: only a pilot that fights in a faction's war gives one (the faction a character is of is not it)

    /// <summary>How long a jettisoned can stays in space.</summary>
    public static readonly TimeSpan CanLifetime = TimeSpan.FromHours(2);

    /// <summary>How long a pilot waits before it can jettison into another can.</summary>
    public static readonly TimeSpan JettisonCooldown = TimeSpan.FromMinutes(3);

    private const int CanGroup = 12;
    private const int WreckGroup = 186;
    private const int CanDestructionEffect = 3;
    private const int RemovedCanLocation = 10;
    private const int CargoFlag = CharacterShips.CargoFlag;
    private const double CanRadiusFallback = 14;

    /// <summary>The drone control distance (attribute 458): how far from its pilot a drone may be sent to work.</summary>
    private const int DroneControlDistanceAttribute = 458;
    /// <summary>The module's maxRange (attribute 54): how far a module's work reaches.</summary>
    private const int ModuleMaxRangeAttribute = 54;

    private readonly ISimulation _sim;
    private readonly CharacterShips _ships;
    private readonly PgItemStore _items;
    private readonly PgSpaceItemStore _spaceItems;
    private readonly PgWorkCycleLedger _cycles;
    private readonly EventBus _events;
    private readonly DogmaStatic _statics;
    private readonly SpaceSessions _space;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, byte> _placed = new();
    private readonly ConcurrentDictionary<(int SystemId, long BeltId), byte> _seededAsteroidBelts = new();
    /// <summary>The belts whose gas cloud is being seeded now (a second arrival does not make a second cloud).</summary>
    private readonly ConcurrentDictionary<(int SystemId, long BeltId), byte> _seedingClouds = new();
    private readonly ConcurrentDictionary<ulong, long> _cooldownUntil = new();

    /// <summary>The roll of a mining crystal's volatility (a number in 0 to 1, a hit at or under the chance): the host draws it, as EVE.js does.</summary>
    public Func<double> VolatilityRoll { get; set; } = Random.Shared.NextDouble;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _abandonedLoot = new();

    public SpaceContainers(
        ISimulation sim, CharacterShips ships, PgItemStore items, PgSpaceItemStore spaceItems, PgWorkCycleLedger cycles, EventBus events, DogmaStatic statics,
        SpaceSessions space, Func<long> nowUnixMs, Action<string>? log = null, TimeSpan? sweepEvery = null)
    {
        (_sim, _ships, _items, _spaceItems, _cycles, _events, _statics, _space, _nowUnixMs, _log) = (sim, ships, items, spaceItems, cycles, events, statics, space, nowUnixMs, log);
        // what the simulation held is gone with it; a scene that is made gets what lies in its system, and a wreck the host puts in space is kept
        _sim.Received += OnReceived;
        _sim.Lost += Forgotten;
        _space.SystemEntered += SystemEntered;
        _space.WreckPlaced += WreckPlaced;
        // the rocks of a belt are items of the store: a module's target is looked up here (SpaceSessions.ActivateAsync)
        _space.TypeOfSpaceItemAsync = async (id, _) => (await _items.GetAsync(id))?.TypeId;
    }

    public void Dispose()
    {
        _sim.Received -= OnReceived;
        _sim.Lost -= Forgotten;
        _space.SystemEntered -= SystemEntered;
        _space.WreckPlaced -= WreckPlaced;
    }

    private void Forgotten() => _placed.Clear();

    public bool IsInSpace(ClientSession session) => _space.IsInSpace(session);

    public int? SolarSystemOf(ClientSession session) => _space.SolarSystemOf(session);

    private static MachoWrappedException Unavailable() => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Space is not available right now. Try again in a moment.")));

    private ItemRow RowOf(Item item)
    {
        var type = _statics.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    private bool IsCan(Item container) => container.TypeId == CharacterShips.CargoContainerTypeId;

    // ── opening ──────────────────────────────────────────────────────────────

    public async Task<Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken)
    {
        if (!_space.IsInSpace(session) || _space.SolarSystemOf(session) is not { } system || await _items.GetAsync(containerId, cancellationToken) is not { } item || item.LocationId != system)
        {
            return null;
        }
        await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (_statics.Type(item.TypeId) is not { GroupId: CanGroup or WreckGroup })
        {
            return null;
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Measure, [containerId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"opening {containerId} for session {session.SessionId}: {error.Message}");
            throw Unavailable();
        }
        // not in the scene, or not in view: the reference answers as for an item that is not there
        if (!reply.Accepted || reply.Distance is not { } distance)
        {
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }
        if (distance > OpenRange)
        {
            throw MachoErrors.ThrowUserError("TargetTooFar");
        }
        return item;
    }

    /// <summary>Whether the pilot may take what is in the container: it is the pilot's own, or its corporation's (a can's owner is the one who jettisoned into it).</summary>
    private async Task<bool> MayTakeFromAsync(long characterId, Item container, CancellationToken cancellationToken, SpaceItem? rights = null)
    {
        if (_abandonedLoot.ContainsKey(container.Id) || container.OwnerId == characterId)
        {
            return true;
        }
        rights ??= await _spaceItems.GetAsync(container.Id, cancellationToken);
        if (IsAbandoned(rights))
        {
            return true;
        }
        return await _ships.CharacterAsync(characterId, cancellationToken) is { } pilot
            && await _ships.CharacterAsync(container.OwnerId, cancellationToken) is { } owner && owner.CorporationId == pilot.CorporationId;
    }


    private static bool IsAbandoned(SpaceItem? item) =>
        item is { CharacterId: 0, CorporationId: 0, AllianceId: 0, WarFactionId: 0 };

    private static SpaceItem Abandon(SpaceItem item) =>
        item with { CharacterId = 0, CorporationId = 0, AllianceId = 0, WarFactionId = 0 };

    /// <summary>Validates the whole request before releasing any loot rights; contents stay in their container.</summary>
    public async Task AbandonLootAsync(ClientSession session, IReadOnlyList<long> containerIds, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || !_space.IsInSpace(session))
        {
            return;
        }

        var targets = new List<(Item Container, SpaceItem? Rights)>();
        foreach (var containerId in containerIds.Distinct())
        {
            if (await OpenAsync(session, containerId, cancellationToken) is not { } container)
            {
                continue;
            }
            var rights = await _spaceItems.GetAsync(containerId, cancellationToken);
            if (!await MayTakeFromAsync(characterId, container, cancellationToken, rights))
            {
                throw MachoErrors.ThrowUserError("SafetyActivated");
            }
            targets.Add((container, rights));
        }

        foreach (var (container, rights) in targets)
        {
            _abandonedLoot.TryAdd(container.Id, 0);
            var current = await _spaceItems.GetAsync(container.Id, cancellationToken) ?? rights;
            if (current is not null && !IsAbandoned(current))
            {
                await _spaceItems.SaveAsync(Abandon(current), cancellationToken);
            }
        }
    }

    // ── moving goods ─────────────────────────────────────────────────────────

    public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken) =>
        MoveAsync(session, itemId, sourceId, destinationId, quantity, null, cancellationToken);

public async Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity,
        int? destinationFlag, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId || !_space.IsInSpace(session))
            return null;
        var toShip = destinationId == shipId;
        if (toShip == (sourceId == shipId)) return null;
        var containerId = toShip ? sourceId : destinationId;
        if (await OpenAsync(session, containerId, cancellationToken) is not { } container) return null;
        if (toShip && !await MayTakeFromAsync(characterId, container, cancellationToken))
            throw MachoErrors.ThrowUserError("SafetyActivated");
        var wasEmpty = (await _items.InContainerAsync(containerId, cancellationToken)).Count == 0;
        var targetFlag = toShip ? destinationFlag ?? CargoFlag : CharacterShips.ContainerFlag;
        switch (await _ships.MoveInSpaceAsync(characterId, itemId, sourceId, destinationId, quantity, targetFlag, cancellationToken))
        {
            case ShipRefused refused:
                throw ShipErrors.Of(refused, _statics);
            case ShipChanged { Stacks: { } stacks } changed:
                await TellMoveAsync(session, shipId, container, toShip, wasEmpty, stacks, targetFlag, cancellationToken);
                return changed.ItemId;
            default:
                return null;
        }
    }



    private async Task TellMoveAsync(ClientSession session, long shipId, Item container, bool toShip, bool wasEmpty,
        IReadOnlyList<ItemChanged> stacks, int destinationFlag, CancellationToken cancellationToken)
    {
        var ulongSession = (ulong)session.SessionId;
        var context = Py.Tuple(new PyStr("Ship"), InventoryWire.Id(shipId), new PyStr("ShipCargo"));
        foreach (var stack in stacks)
        {
            if (toShip)
                Eve.Services.Ships.ShipNotifications.SendItemsChanged(session, stack, RowOf, context);
            else
                Eve.Services.Ships.ShipNotifications.SendItemChange(session, stack, RowOf);
        }
        var empty = (await _items.InContainerAsync(container.Id, cancellationToken)).Count == 0;
        var disposed = toShip && empty && IsCan(container);
        if (!disposed && (toShip || wasEmpty))
            await TellChangeAsync(ulongSession, container.Id, empty);
        if (toShip)
            session.SendNotification("OnClientEvent_MoveFromCargoToHangar", "clientID", InventoryWire.Id(container.Id), InventoryWire.Id(shipId), new PyInt(destinationFlag));
        if (disposed)
            await RemoveEmptyCanAsync(session, container, cancellationToken);
    }

    public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken) =>
        MoveAllAsync(session, itemIds, sourceId, destinationId, null, cancellationToken);

public async Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId,
        int? destinationFlag, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId || !_space.IsInSpace(session)
            || destinationId != shipId || sourceId == shipId)
            return null;
        if (await OpenAsync(session, sourceId, cancellationToken) is not { } container) return null;
        if (!await MayTakeFromAsync(characterId, container, cancellationToken))
            throw MachoErrors.ThrowUserError("SafetyActivated");
        var flag = destinationFlag ?? CargoFlag;
        switch (await _ships.MoveAllInSpaceAsync(characterId, itemIds, sourceId, flag, cancellationToken))
        {
            case ShipRefused refused:
                throw ShipErrors.Of(refused, _statics);
            case ShipChanged { Stacks: { } stacks }:
                await TellMoveAllAsync(session, shipId, container, stacks, flag, cancellationToken);
                return true;
            default:
                return false;
        }
    }



    /// <summary>
    /// What the client is told of a batch that was taken from a can or a wreck (recorded: <c>loot.json</c>): each stack that moved (<c>OnItemsChanged</c>, to the character, with no place for the
    /// loot window), the scene's word for each of them of what is in the container now, the single <c>OnClientEvent_MoveFromCargoToHangar</c>, and when nothing is left the single
    /// <c>OnWreckLootAll</c> with every item that was taken; last a can that is empty goes.
    /// </summary>
    private async Task TellMoveAllAsync(ClientSession session, long shipId, Item container, IReadOnlyList<ItemChanged> stacks,
        int destinationFlag, CancellationToken cancellationToken)
    {
        var ulongSession = (ulong)session.SessionId;
        foreach (var stack in stacks)
            Eve.Services.Ships.ShipNotifications.SendItemsChanged(session, stack, RowOf, idType: "charid");
        var empty = (await _items.InContainerAsync(container.Id, cancellationToken)).Count == 0;
        foreach (var _ in stacks)
            await TellChangeAsync(ulongSession, container.Id, empty);
        session.SendNotification("OnClientEvent_MoveFromCargoToHangar", "clientID", InventoryWire.Id(container.Id), InventoryWire.Id(shipId), new PyInt(destinationFlag));
        if (empty)
        {
            session.SendNotification(
                "OnWreckLootAll", "clientID", Py.Tuple(new PyStr("ItemFloatingCargo"), InventoryWire.Id(container.Id)),
                Py.Tuple([.. stacks.Select(stack => InventoryWire.Tuple(RowOf(stack.After with { LocationId = stack.PreviousLocationId, Flag = stack.PreviousFlag, Quantity = stack.PreviousQuantity })))]));
        }
        if (empty && IsCan(container))
            await RemoveEmptyCanAsync(session, container, cancellationToken);
    }

    /// <summary>The scene is told what is in the wreck now; a scene that is not reached is no reason to fail what was moved.</summary>
    private async Task TellChangeAsync(ulong session, long wreckId, bool isEmpty)
    {
        try
        {
            await _sim.ChangeWreckAsync(session, wreckId, isEmpty);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"the scene was not told that {wreckId} is {(isEmpty ? "empty" : "not empty")}: {error.Message}");
        }
    }

    /// <summary>A can with nothing in it goes at once (what the reference does for a jettisoned can: a wreck stays until its time is up): the item, the ball, and the client is told the item is gone.</summary>
    private async Task RemoveEmptyCanAsync(ClientSession session, Item can, CancellationToken cancellationToken)
    {
        if (await _ships.RemoveContainerAsync(can.Id, withContents: false, cancellationToken) is not { } removed)
        {
            return;
        }
        await ForgetAsync(can.Id);
        session.SendNotification(
            "OnItemsChanged", "charid",
            // the row of what has gone has no custom info (recorded: `loot.json`)
            new PyList([InventoryWire.PackedRow(RowOf(removed with { LocationId = RemovedCanLocation, Flag = 0, Quantity = 1 }), PyNone.Instance)]),
            new PyDict([new(new PyInt(3), InventoryWire.Id(can.LocationId))]), PyNone.Instance);
        _sim.RemoveWreck(can.Id, CanDestructionEffect);
    }

    // ── jettison ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId || _space.SolarSystemOf(session) is not { } system || itemIds.Count == 0)
        {
            return [];
        }
        var now = _nowUnixMs();
        var id = (ulong)session.SessionId;
        if (_cooldownUntil.TryGetValue(id, out var until) && until > now)
        {
            throw MachoErrors.ThrowUserError("ShpJettisonPending", ("eta", new PyStr(Eta(until - now))));
        }
        if (await _ships.CharacterAsync(characterId, cancellationToken) is not { } pilot)
        {
            return [];
        }
        await _statics.EnsureTypesAsync([CharacterShips.CargoContainerTypeId], cancellationToken);
        var canType = _statics.Type(CharacterShips.CargoContainerTypeId);
        var can = await _ships.CreateCanAsync(characterId, system, cancellationToken);

        // the can comes into space first (the client is told the ball and the effect), then the stacks go into it
        CommandReply placed;
        try
        {
            placed = await _sim.PlaceContainerAsync(new SimPlaceContainer(
                id, can.Id, new SimWreckKind(CharacterShips.CargoContainerTypeId, canType?.Name ?? "Cargo Container", canType?.GraphicId ?? 0),
                canType?.Attributes.GetValueOrDefault(162, CanRadiusFallback) ?? CanRadiusFallback, shipId,
                new SimCredit(pilot.Id, pilot.CorporationId, pilot.AllianceId ?? 0, 0), JettisonDistance), cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"the can of session {session.SessionId} was not put in space: {error.Message}");
            placed = new CommandReply(false, 0, "SIM_UNAVAILABLE");
        }
        if (!placed.Accepted)
        {
            await _ships.RemoveContainerAsync(can.Id, withContents: false, CancellationToken.None);
            return [];
        }
        var radius = canType?.Attributes.GetValueOrDefault(162, CanRadiusFallback) ?? CanRadiusFallback;
        await _spaceItems.SaveAsync(new SpaceItem(
            can.Id, system, placed.Position, placed.Direction, radius, CanGroup, shipId, pilot.Id, pilot.CorporationId, pilot.AllianceId ?? 0, 0, now + (long)CanLifetime.TotalMilliseconds), cancellationToken);
        _placed[can.Id] = 0;

        var moved = await _ships.JettisonIntoAsync(characterId, shipId, can.Id, itemIds, cancellationToken);
        foreach (var stack in moved)
        {
            Eve.Services.Ships.ShipNotifications.SendItemsChanged(session, stack, RowOf);
        }
        if (moved.Count == 0)
        {
            // nothing could be jettisoned: the can goes as it came
            await _ships.RemoveContainerAsync(can.Id, withContents: false, CancellationToken.None);
            await ForgetAsync(can.Id);
            _sim.RemoveWreck(can.Id, 0);
            return [];
        }
        await TellChangeAsync(id, can.Id, isEmpty: false);
        var next = now + (long)JettisonCooldown.TotalMilliseconds;
        if (_cooldownUntil.TryAdd(id, next))
        {
            session.Closed += () => _cooldownUntil.TryRemove(id, out _);
        }
        else
        {
            _cooldownUntil[id] = next;
        }
        return [.. moved.Select(m => m.After.Id)];
    }

    /// <summary>How long the pilot waits, as the client's message says it: "2 minutes and 5 seconds".</summary>
    internal static string Eta(long remainingMs)
    {
        var total = Math.Max(1, (long)Math.Ceiling(remainingMs / 1000d));
        var (minutes, seconds) = (total / 60, total % 60);
        var parts = new List<string>();
        if (minutes > 0)
        {
            parts.Add($"{minutes} minute{(minutes == 1 ? "" : "s")}");
        }
        if (seconds > 0 || minutes == 0)
        {
            parts.Add($"{seconds} second{(seconds == 1 ? "" : "s")}");
        }
        return string.Join(" and ", parts);
    }

    // ── time running out ─────────────────────────────────────────────────────

    /// <summary>Takes out of space what has run out of time (a periodic job: the host schedules it on the shared scheduler). A failure is logged, not thrown: the next run tries again.</summary>
    public async Task SweepAsync()
    {
        try
        {
            await ExpireDueAsync(_nowUnixMs());
        }
        catch (Exception error)
        {
            _log?.Invoke($"the cans whose time ran out were not removed: {error.Message}");
        }
    }

    /// <summary>What is due to go by <paramref name="nowUnixMs"/> (the cans whose two hours are up, the wrecks) is taken out of the world with what is in it and out of space.</summary>
    internal async Task ExpireDueAsync(long nowUnixMs)
    {
        foreach (var due in await _spaceItems.DueAsync(nowUnixMs))
        {
            await _ships.RemoveContainerAsync(due.ItemId, withContents: true);
            await ForgetAsync(due.ItemId);
            _sim.RemoveWreck(due.ItemId, 0);
        }
    }

    /// <summary>An item that is not in space any more is forgotten: its place is not kept and the simulation will not be given it again.</summary>
    private async Task ForgetAsync(long itemId)
    {
        _placed.TryRemove(itemId, out _);
        _abandonedLoot.TryRemove(itemId, out _);
        await _spaceItems.RemoveAsync(itemId);
    }

    // ── keeping what lies in space ───────────────────────────────────────────

    /// <summary>The host put the wreck of a destroyed ship (or a corpse) in space: where it lies is kept, for two hours.</summary>
private void OnReceived(SimMessage message)
    {
        if (message is SimDroneWorkCycle cycle)
        {
            _ = HandleWorkCycleAsync(WorkCycle.OfDrone(cycle));
        }
        else if (message is SimModuleWorkCycle moduleCycle)
        {
            _ = HandleWorkCycleAsync(WorkCycle.OfModule(moduleCycle));
        }
        else if (message is SimArrived arrived)
        {
            _ = SeedAsteroidBeltAsync(arrived);
            _ = SeedGasCloudAsync(arrived);
        }
    }


    /// <summary>
    /// A belt holds a harvestable gas cloud beside its rock: the arrival that seeds the rock seeds the cloud too (the belt generation, reused). When no live cloud
    /// is left in the belt, a pilot's arrival makes one, of the published cloud type the belt's id picks (a belt keeps its kind). shortcut: CCP keeps gas clouds in
    /// gas sites, not in belts; the site model is not built, so the belts carry the clouds here (docs/reference/compat/DEVIATIONS.md).
    /// </summary>
    private async Task SeedGasCloudAsync(SimArrived arrived)
    {
        try
        {
            await SeedGasCloudCoreAsync(arrived);
        }
        catch (Exception error)
        {
            _log?.Invoke($"gas cloud for belt {arrived.BeltId} could not be seeded: {error.Message}");
        }
    }

    private async Task SeedGasCloudCoreAsync(SimArrived arrived)
    {
        if (arrived.BeltId <= 0 || arrived.Position.Count != 3 || arrived.Position.Any(position => !double.IsFinite(position)))
        {
            return;
        }
        var session = _space.ClientSessionOf(arrived.SessionId);
        if (session is null || !_space.IsInSpace(session) || _space.SolarSystemOf(session) is not { } system || system <= 0)
        {
            return;
        }
        var belt = (system, arrived.BeltId);
        if (!_seedingClouds.TryAdd(belt, 0))
        {
            return;
        }
        try
        {
            foreach (var spaceItem in await _spaceItems.InSystemAsync(system))
            {
                if (spaceItem.LauncherId != arrived.BeltId || await _items.GetAsync(spaceItem.ItemId) is not { } stored)
                {
                    continue;
                }
                await _statics.EnsureTypesAsync([stored.TypeId]);
                if (_statics.Type(stored.TypeId) is { } storedType && MiningTargets.IsGasCloud(_statics, storedType) && stored.Quantity > 0)
                {
                    return;
                }
            }

            // the clouds that have a gas to yield (MiningTargets); the belt's id picks one of them
            var clouds = new List<TypeDef>();
            foreach (var cloud in await _statics.TypesInGroupsAsync([MiningTargets.GasCloudGroup]))
            {
                if (await _statics.GasYieldTypeAsync(cloud.TypeId) is not null)
                {
                    clouds.Add(cloud);
                }
            }
            if (clouds.Count == 0)
            {
                _log?.Invoke($"no published gas cloud type with a gas to yield is available for belt {arrived.BeltId}");
                return;
            }
            var type = clouds[(int)(arrived.BeltId % clouds.Count)];
            var radiusId = _statics.AttributeId("radius");
            var radius = radiusId is { } id ? type.Attributes.GetValueOrDefault(id, 25d) : 25d;
            if (!double.IsFinite(radius) || radius <= 0)
            {
                radius = 25d;
            }
            var position = arrived.Position.ToArray();
            position[2] += Math.Max(1_000d, radius * 2d);

            // shortcut: a cloud holds 1,000 units, as a belt rock does; the stock of a cloud is not in the SDE (docs/reference/compat/DEVIATIONS.md)
            const long initialUnits = 1_000;
            var created = await _ships.CreateAsteroidAsync(
                type.TypeId, system, arrived.BeltId, initialUnits, position, [0d, 0d, 1d], radius, _spaceItems);
            if (_placed.TryAdd(created.Item.Id, 0))
            {
                try
                {
                    _sim.PlaceAsteroid(new SimPlaceAsteroid(
                        system, created.Item.Id, type.TypeId, type.GroupId, type.CategoryId, type.Name ?? MiningTargets.GasCloudGroup, type.GraphicId,
                        created.SpaceItem.Position, created.SpaceItem.Direction, created.SpaceItem.Radius, created.Item.Quantity));
                }
                catch (Exception error)
                {
                    _placed.TryRemove(created.Item.Id, out _);
                    _log?.Invoke($"gas cloud {created.Item.Id} in belt {arrived.BeltId} could not be placed: {error.Message}");
                }
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"gas cloud for belt {arrived.BeltId} could not be seeded: {error.Message}");
        }
        finally
        {
            _seedingClouds.TryRemove(belt, out _);
        }
    }

    private async Task SeedAsteroidBeltAsync(SimArrived arrived)
    {
        if (arrived.BeltId <= 0 || arrived.Position.Count != 3 || arrived.Position.Any(position => !double.IsFinite(position)))
        {
            return;
        }
        var session = _space.ClientSessionOf(arrived.SessionId);
        if (session is null || !_space.IsInSpace(session))
        {
            return;
        }
        if (_space.SolarSystemOf(session) is not { } system || system <= 0)
        {
            return;
        }

        var belt = (system, arrived.BeltId);
        try
        {
            foreach (var spaceItem in await _spaceItems.InSystemAsync(system))
            {
                if (spaceItem.LauncherId != arrived.BeltId
                    || await _items.GetAsync(spaceItem.ItemId) is not { } stored)
                {
                    continue;
                }
                await _statics.EnsureTypesAsync([stored.TypeId]);
                var storedType = _statics.Type(stored.TypeId);
                if (storedType is null || _statics.CategoryName(storedType.CategoryId) != "Asteroid")
                {
                    continue;
                }

                if (stored.Quantity <= 0)
                {
                    // a depleted rock is not the belt's any more: when only depleted ones are left, the belt is seeded again below (EVE.js resets belts)
                    _placed.TryAdd(stored.Id, 0);
                    continue;
                }
                _seededAsteroidBelts.TryAdd(belt, 0);
                if (_placed.TryAdd(stored.Id, 0))
                {
                    try
                    {
                        _sim.PlaceAsteroid(new SimPlaceAsteroid(
                            system, stored.Id, stored.TypeId, storedType.GroupId, storedType.CategoryId,
                            stored.CustomName ?? storedType.Name ?? "Asteroid", storedType.GraphicId,
                            spaceItem.Position, spaceItem.Direction, spaceItem.Radius, stored.Quantity));
                    }
                    catch (Exception error)
                    {
                        _placed.TryRemove(stored.Id, out _);
                        _log?.Invoke($"asteroid {stored.Id} in belt {arrived.BeltId} could not be placed: {error.Message}");
                    }
                }
                return;
            }

            if (!_seededAsteroidBelts.TryAdd(belt, 0))
            {
                return;
            }

            var type = (await _statics.TypesInCategoryAsync("Asteroid"))
                .FirstOrDefault(candidate => candidate.Name.Equals("Veldspar", StringComparison.OrdinalIgnoreCase));
            if (type is null)
            {
                _seededAsteroidBelts.TryRemove(belt, out _);
                _log?.Invoke($"no published Veldspar asteroid type is available for belt {arrived.BeltId}");
                return;
            }

            var radiusId = _statics.AttributeId("radius");
            var radius = radiusId is { } id ? type.Attributes.GetValueOrDefault(id, 25d) : 25d;
            if (!double.IsFinite(radius) || radius <= 0)
            {
                radius = 25d;
            }
            var position = arrived.Position.ToArray();
            position[0] += Math.Max(1_000d, radius * 2d);

            // ponytail: seed one belt rock with 1,000 units; replace this cap when authoritative per-rock stock data is available.
            const long initialUnits = 1_000;
            var created = await _ships.CreateAsteroidAsync(
                type.TypeId, system, arrived.BeltId, initialUnits, position, [1d, 0d, 0d], radius, _spaceItems);
            if (_placed.TryAdd(created.Item.Id, 0))
            {
                try
                {
                    _sim.PlaceAsteroid(new SimPlaceAsteroid(
                        system, created.Item.Id, type.TypeId, type.GroupId, type.CategoryId, type.Name, type.GraphicId,
                        created.SpaceItem.Position, created.SpaceItem.Direction, created.SpaceItem.Radius, created.Item.Quantity));
                }
                catch (Exception error)
                {
                    _placed.TryRemove(created.Item.Id, out _);
                    _log?.Invoke($"seed asteroid {created.Item.Id} was saved but could not be placed: {error.Message}");
                }
            }
        }
        catch (Exception error)
        {
            _seededAsteroidBelts.TryRemove(belt, out _);
            _log?.Invoke($"asteroids for belt {arrived.BeltId} could not be seeded: {error.Message}");
        }
    }

    /// <summary>What the host answers a work cycle: accepted or refused with the reason, and what it delivered (the fields of <see cref="SimDroneWorkResult"/>).</summary>
    /// <summary>
    /// The answer of a settled work cycle. <see cref="Fact"/> is announced on the bus in the transaction that records the answer; <see cref="Notice"/> is the
    /// <c>OnRemoteMessage</c> the pilot is told once the answer is kept.
    /// </summary>
    private readonly record struct WorkOutcome(
        bool Accepted, string Reason, long Remaining = 0, long Delivered = 0, bool Complete = true, (string Topic, object Payload, string Key)? Fact = null,
        PyValue? Notice = null)
    {
        public static WorkOutcome Refuse(string reason) => new(false, reason);
    }

    /// <summary>
    /// A work cycle the simulation parks on and the host decides (ADR 0013): a drone's (mining or salvage) or a module's (a mining laser's or a salvager's). The
    /// <see cref="OwnerId"/> is the drone or the module; the answer is recorded for the owner (<see cref="PgWorkCycleLedger"/>).
    /// </summary>
    private sealed record WorkCycle(
        bool IsModule, ulong SessionId, long CharacterId, long ShipId, int SolarSystemId, long OwnerId, long TargetId, ulong CycleId, byte Command,
        double MiningAmountM3, double WasteVolumeMultiplier, double WasteProbability, double AccessDifficultyBonus, double Roll,
        double RoundingRoll, double CritRoll, double CritProbability, double CritQuantityMultiplier)
    {
        public static WorkCycle OfDrone(SimDroneWorkCycle cycle) => new(
            false, cycle.SessionId, cycle.CharacterId, cycle.ShipId, cycle.SolarSystemId, cycle.DroneId, cycle.TargetId, cycle.CycleId, cycle.Command,
            cycle.MiningAmountM3, cycle.WasteVolumeMultiplier, cycle.WasteProbability, cycle.AccessDifficultyBonus, cycle.Roll, 0, 0, 0, 0);

        public static WorkCycle OfModule(SimModuleWorkCycle cycle) => new(
            true, cycle.SessionId, cycle.CharacterId, cycle.ShipId, cycle.SolarSystemId, cycle.ModuleId, cycle.TargetId, cycle.CycleId, cycle.Command,
            cycle.MiningAmountM3, cycle.WasteVolumeMultiplier, cycle.WasteProbability, cycle.AccessDifficultyBonus, cycle.WasteRoll,
            cycle.RoundingRoll, cycle.CritRoll, cycle.CritProbability, cycle.CritQuantityMultiplier);

        public string Kind => IsModule ? "module" : "drone";
    }

    /// <summary>
    /// The one path for the work cycles of drones and modules. A repeat of the owner's last settled cycle is answered from the record and never settled again, so
    /// a cycle the simulation re-sends (its answer was lost, or the host restarted) is not granted twice.
    /// </summary>
    private async Task HandleWorkCycleAsync(WorkCycle cycle)
    {
        try
        {
            if (cycle.CycleId > long.MaxValue)
            {
                ReplyToWorkCycle(cycle, WorkOutcome.Refuse("INVALID_PROFILE"));
                return;
            }
            // the fuel of a module's cycle is debited and its answer recorded in one transaction, which also answers a repeat (CharacterShips.ConsumeModuleFuelAsync)
            if (cycle.IsModule && cycle.Command == SimModuleWorkCommands.ConsumeFuel)
            {
                ReplyToWorkCycle(cycle, await SettleFuelAsync(cycle));
                return;
            }
            if (await _cycles.LastAsync(cycle.OwnerId) is { } last && last.CycleId == (long)cycle.CycleId)
            {
                ReplyToWorkCycle(cycle, new WorkOutcome(last.Accepted, last.Reason, last.UnitsRemaining, last.DeliveredUnits, last.Complete));
                return;
            }
            var outcome = await SettleWorkCycleAsync(cycle);
            await RecordOutcomeAsync(cycle, outcome);
            ReplyToWorkCycle(cycle, outcome);
        }
        catch (Exception error)
        {
            _log?.Invoke($"{cycle.Kind} work cycle {cycle.CycleId} was not settled: {error.Message}");
            // a module is parked, and the simulation sends the same cycle again (ADR 0013, rule 3): no answer is sent, so the retry settles it. A drone is not
            // re-sent, so it is refused and goes back to idle.
            if (!cycle.IsModule)
            {
                ReplyToWorkCycle(cycle, WorkOutcome.Refuse("WORK_FAILED"));
            }
        }
    }

    /// <summary>
    /// Records a settled cycle's answer, with its fact when it has one (in one transaction), and then tells the pilot the notice of the answer, once it is kept.
    /// </summary>
    private async Task RecordOutcomeAsync(WorkCycle cycle, WorkOutcome outcome)
    {
        var record = new WorkCycleRecord(cycle.OwnerId, (long)cycle.CycleId, outcome.Accepted, outcome.Reason, outcome.Remaining, outcome.Delivered, outcome.Complete);
        if (outcome.Fact is { } fact)
        {
            await _cycles.RecordWithFactAsync(record, _events, fact.Topic, fact.Payload, fact.Key);
        }
        else
        {
            await _cycles.RecordAsync(record);
        }
        if (outcome.Notice is { } notice && _space.ClientSessionOf(cycle.SessionId) is { } session)
        {
            session.SendNotification("OnRemoteMessage", "clientID", notice);
        }
    }

    /// <summary>
    /// The fuel of one cycle of a module that burns fuel: the module must be running in the pilot's ship, and the fuel is the module's consumption (713, 714) taken
    /// from the ship. A refusal before the debit is not recorded (nothing was taken, and a repeat is checked again).
    /// </summary>
    private async Task<WorkOutcome> SettleFuelAsync(WorkCycle cycle)
    {
        if (cycle.OwnerId <= 0)
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        var session = _space.ClientSessionOf(cycle.SessionId);
        if (session is null || session.CharacterId != cycle.CharacterId
            || session.GetInt("shipid") is not { } shipId || shipId != cycle.ShipId
            || !_space.IsInSpace(session) || _space.SolarSystemOf(session) != cycle.SolarSystemId)
        {
            return WorkOutcome.Refuse("NO_PILOT");
        }
        if (!_space.IsModuleActive(cycle.SessionId, cycle.OwnerId))
        {
            return WorkOutcome.Refuse("NOT_CONTROLLED");
        }
        var ship = await _ships.LoadAsync(cycle.CharacterId);
        if (ship is null || ship.Ship.Id != cycle.ShipId
            || ship.Ship.LocationId != cycle.SolarSystemId || ship.Ship.Flag != CharacterShips.InSpaceFlag)
        {
            return WorkOutcome.Refuse("NO_SHIP");
        }
        var fuelType = ship.World.Get(cycle.OwnerId, Eve.Services.Space.Effects.ModuleAttributes.ConsumptionType);
        var fuelQuantity = ship.World.Get(cycle.OwnerId, Eve.Services.Space.Effects.ModuleAttributes.ConsumptionQuantity);
        if (!double.IsFinite(fuelType) || fuelType <= 0 || fuelType > int.MaxValue
            || !double.IsFinite(fuelQuantity) || fuelQuantity < 0)
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        var record = await _ships.ConsumeModuleFuelAsync(
            _cycles, cycle.CharacterId, cycle.ShipId, cycle.OwnerId, (long)cycle.CycleId, (int)fuelType, (long)Math.Round(fuelQuantity));
        return new WorkOutcome(record.Accepted, record.Reason, record.UnitsRemaining, record.DeliveredUnits, record.Complete);
    }

    private void ReplyToWorkCycle(WorkCycle cycle, WorkOutcome outcome)
    {
        try
        {
            if (cycle.IsModule)
            {
                _sim.ModuleWorkResult(new SimModuleWorkResult(
                    cycle.SessionId, cycle.OwnerId, cycle.TargetId, cycle.CycleId, outcome.Accepted, outcome.Reason, outcome.Remaining, outcome.Delivered, outcome.Complete));
            }
            else
            {
                _sim.DroneWorkResult(new SimDroneWorkResult(
                    cycle.SessionId, cycle.OwnerId, cycle.TargetId, cycle.CycleId, outcome.Accepted, outcome.Reason, outcome.Remaining, outcome.Delivered, outcome.Complete));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"{cycle.Kind} work cycle {cycle.CycleId} response was not sent: {error.Message}");
        }
    }

    /// <summary>The checks and the settlement of one work cycle: the owner must be the pilot's (a drone flown, or a module that runs), the target there, in reach.</summary>
    private async Task<WorkOutcome> SettleWorkCycleAsync(WorkCycle cycle)
    {
        if (cycle.Command is not (SimDroneCommands.MineRepeatedly or SimDroneCommands.Salvage))
        {
            return WorkOutcome.Refuse("UNSUPPORTED_COMMAND");
        }
        if (cycle.OwnerId <= 0 || cycle.TargetId <= 0
            || !double.IsFinite(cycle.Roll) || cycle.Roll < 0 || cycle.Roll >= 1
            || !double.IsFinite(cycle.AccessDifficultyBonus) || cycle.AccessDifficultyBonus < 0)
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        if (cycle.Command == SimDroneCommands.MineRepeatedly
            && (!double.IsFinite(cycle.MiningAmountM3) || cycle.MiningAmountM3 <= 0
                || !double.IsFinite(cycle.WasteVolumeMultiplier) || !double.IsFinite(cycle.WasteProbability)))
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }

        var session = _space.ClientSessionOf(cycle.SessionId);
        if (session is null || session.CharacterId != cycle.CharacterId
            || session.GetInt("shipid") is not { } shipId || shipId != cycle.ShipId
            || !_space.IsInSpace(session) || _space.SolarSystemOf(session) != cycle.SolarSystemId)
        {
            return WorkOutcome.Refuse("NO_PILOT");
        }

        var ship = await _ships.LoadAsync(cycle.CharacterId);
        if (ship is null || ship.Ship.Id != cycle.ShipId
            || ship.Ship.LocationId != cycle.SolarSystemId || ship.Ship.Flag != CharacterShips.InSpaceFlag)
        {
            return WorkOutcome.Refuse("NO_SHIP");
        }

        // how far the work reaches: a drone's control range, or a module's own range (its maxRange)
        double controlRange;
        if (cycle.IsModule)
        {
            if (!_space.IsModuleActive(cycle.SessionId, cycle.OwnerId))
            {
                return WorkOutcome.Refuse("NOT_CONTROLLED");
            }
            controlRange = ship.World.Get(cycle.OwnerId, ModuleMaxRangeAttribute);
            if (!double.IsFinite(controlRange) || controlRange <= 0)
            {
                return WorkOutcome.Refuse("INVALID_PROFILE");
            }
        }
        else
        {
            controlRange = 20_000d;
            var range = ship.World.Get(cycle.CharacterId, DroneControlDistanceAttribute);
            if (double.IsFinite(range) && range > 0)
            {
                controlRange = range;
            }
        }

        var drone = cycle.IsModule ? null : await _items.GetAsync(cycle.OwnerId);
        if (!cycle.IsModule && (drone is null || drone.OwnerId != cycle.CharacterId || drone.LocationId != cycle.SolarSystemId
            || drone.Flag != CharacterShips.InSpaceFlag))
        {
            return WorkOutcome.Refuse("NOT_CONTROLLED");
        }

        var target = await _items.GetAsync(cycle.TargetId);
        var targetSpace = await _spaceItems.GetAsync(cycle.TargetId);
        if (target is null || target.LocationId != cycle.SolarSystemId || target.Flag != CharacterShips.InSpaceFlag
            || targetSpace is null || targetSpace.SolarSystemId != cycle.SolarSystemId)
        {
            return WorkOutcome.Refuse("NO_TARGET");
        }

        if (drone is not null)
        {
            await _statics.EnsureTypesAsync([drone.TypeId, target.TypeId]);
            var droneType = _statics.Type(drone.TypeId);
            var expectedDroneGroup = cycle.Command == SimDroneCommands.MineRepeatedly ? "Mining Drone" : "Salvage Drone";
            if (droneType is null || _statics.CategoryName(droneType.CategoryId) != "Drone"
                || !_statics.GroupName(droneType.GroupId).Equals(expectedDroneGroup, StringComparison.OrdinalIgnoreCase))
            {
                return WorkOutcome.Refuse("NOT_CONTROLLED");
            }
        }
        else
        {
            await _statics.EnsureTypesAsync([target.TypeId]);
        }

        var targetType = _statics.Type(target.TypeId);
        if (targetType is null
            || (cycle.Command == SimDroneCommands.MineRepeatedly
                ? !MiningTargets.IsMineable(_statics, targetType)
                : !_statics.GroupName(targetType.GroupId).Contains("Wreck", StringComparison.OrdinalIgnoreCase)))
        {
            return WorkOutcome.Refuse(cycle.Command == SimDroneCommands.MineRepeatedly ? "NO_TARGET" : "NO_WRECK");
        }

        var measured = await _sim.CommandAsync(cycle.SessionId, SimCommands.Measure, [cycle.TargetId]);
        if (!measured.Accepted || measured.Distance is not { } distance || !double.IsFinite(distance))
        {
            return WorkOutcome.Refuse(cycle.Command == SimDroneCommands.MineRepeatedly ? "NO_TARGET" : "NO_WRECK");
        }
        if (distance > controlRange)
        {
            return WorkOutcome.Refuse("OUT_OF_RANGE");
        }

        if (cycle.Command == SimDroneCommands.MineRepeatedly)
        {
            // a module's rolls are the simulation's three draws; a drone rounds down and never crits, so its crit draw is unused
            var mined = await _ships.MineAsteroidAsync(
                cycle.CharacterId, cycle.ShipId, cycle.SolarSystemId, cycle.OwnerId, cycle.TargetId,
                cycle.IsModule ? MiningSource.Module : MiningSource.Drone,
                new MiningNumbers(cycle.MiningAmountM3, cycle.WasteVolumeMultiplier, cycle.WasteProbability, cycle.CritProbability, cycle.CritQuantityMultiplier),
                cycle.IsModule
                    ? MiningRolls.ForModule(cycle.Roll, cycle.RoundingRoll, cycle.CritRoll, cycle.WasteProbability, cycle.CritProbability)
                    : new MiningRolls(cycle.Roll, 0, 0, 1, 0),
                (long)cycle.CycleId, _cycles, _spaceItems, _events, (int)(session.GetInt("corporationID") ?? session.GetInt("corpid") ?? 0));
            if (mined.Accepted && mined.Depleted)
            {
                _placed.TryRemove(cycle.TargetId, out _);
            }
            if (mined.Accepted && mined.Change is { } changed)
            {
                try
                {
                    await _statics.EnsureTypesAsync([.. changed.Items.Select(item => item.After.TypeId).Distinct()]);
                    Eve.Services.Ships.ShipNotifications.Send(
                        session, cycle.CharacterId, changed, RowOf, Py.FileTimeFromUnixMs(_nowUnixMs()));
                }
                catch (Exception error)
                {
                    _log?.Invoke($"mined cargo for cycle {cycle.CycleId} was saved but its item notification failed: {error.Message}");
                }
            }
            // the miner is told what it mined, after the grant is committed (the cycle is granted once, so this is sent once)
            if (mined.Accepted && cycle.IsModule && mined.Mined is { } yield)
            {
                session.SendNotification(
                    "OnOreMined", "charid", Eve.Services.Ships.ShipNotifications.OreMined(cycle.TargetId, cycle.OwnerId, mined.OreTypeId, yield));
            }
            // a laser with a crystal loaded rolls the crystal's volatility once the grant is kept (EVE.js applyCrystalVolatility)
            if (mined.Accepted && cycle.IsModule && ship.Fitted.FirstOrDefault(m => m.Id == cycle.OwnerId) is { } laser
                && ship.ChargeOf(laser) is { Quantity: > 0 } crystal)
            {
                await _statics.EnsureTypesAsync([crystal.TypeId]);
                if (_statics.Type(crystal.TypeId) is { } crystalType
                    && _statics.AttributeId("crystalVolatilityChance") is { } chanceId
                    && _statics.AttributeId("crystalVolatilityDamage") is { } damageId)
                {
                    await _ships.ApplyCrystalVolatilityAsync(
                        crystal.Id, VolatilityRoll(), crystalType.Attributes.GetValueOrDefault(chanceId, 0d), crystalType.Attributes.GetValueOrDefault(damageId, 0d));
                }
            }
            return new WorkOutcome(mined.Accepted, mined.Reason, mined.UnitsRemaining, mined.DeliveredUnits, mined.Depleted);
        }

        var difficultyId = _statics.AttributeId("accessDifficulty");
        var baseChance = difficultyId is { } id ? targetType.Attributes.GetValueOrDefault(id, 0d) : 0d;
        var chance = Math.Clamp(baseChance + cycle.AccessDifficultyBonus, 0d, 100d);
        if (!double.IsFinite(chance))
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        if (cycle.Roll * 100d >= chance)
        {
            // a failed roll takes nothing from the wreck: the answer that records it carries the fact, and the pilot is told
            var failed = new SalvageFailedEvent(
                cycle.CharacterId, cycle.SolarSystemId, cycle.ShipId, cycle.OwnerId, cycle.IsModule ? MiningSource.Module : MiningSource.Drone,
                cycle.TargetId, targetType.TypeId);
            return new WorkOutcome(
                true, "", 1, 0, false, (SalvageFailedEvent.Topic, failed, $"char:{cycle.CharacterId}"),
                Py.Tuple(new PyStr("SalvagingFailure"), new PyDict([
                    new(new PyStr("itemID"), new PyLong(cycle.TargetId)),
                    new(new PyStr("type"), Py.Tuple(new PyInt(4), new PyInt(targetType.TypeId))),
                ])));
        }

        var salvageProducts = await _statics.TypesInGroupsAsync(["Salvaged Materials"]);
        var salvageTypeId = SelectSalvageProductTypeId(targetType, targetSpace, salvageProducts);
        if (salvageTypeId is null)
        {
            return WorkOutcome.Refuse("NO_SALVAGE_PRODUCTS");
        }
        var salvaged = await _ships.SalvageWreckAsync(
            cycle.CharacterId, cycle.ShipId, cycle.SolarSystemId, cycle.IsModule ? MiningSource.Module : MiningSource.Drone,
            cycle.OwnerId, cycle.TargetId, salvageTypeId,
            targetSpace, _nowUnixMs() + (long)CanLifetime.TotalMilliseconds, _spaceItems, (long)cycle.CycleId, _cycles, _events);
        if (!salvaged.Accepted)
        {
            return WorkOutcome.Refuse(salvaged.Reason);
        }

        _placed.TryRemove(cycle.TargetId, out _);
        if (salvaged.Change is { } salvageChange)
        {
            try
            {
                await _statics.EnsureTypesAsync([.. salvageChange.Items.Select(item => item.After.TypeId).Distinct()]);
                Eve.Services.Ships.ShipNotifications.Send(
                    session, cycle.CharacterId, salvageChange, RowOf, Py.FileTimeFromUnixMs(_nowUnixMs()));
            }
            catch (Exception error)
            {
                _log?.Invoke($"salvage for cycle {cycle.CycleId} was saved but its item notification failed: {error.Message}");
            }
        }
        if (salvaged.Container is { } container && salvaged.ContainerSpaceItem is { } containerSpace)
        {
            _placed[container.Id] = 0;
            var containerType = _statics.Type(container.TypeId);
            try
            {
                _sim.PlaceWreck(new SimPlaceWreck(
                    cycle.SolarSystemId, container.Id, containerSpace.GroupId,
                    new SimWreckKind(container.TypeId, container.CustomName ?? containerType?.Name ?? "Cargo Container", containerType?.GraphicId ?? 0),
                    containerSpace.Position, containerSpace.Direction, containerSpace.Radius, containerSpace.LauncherId,
                    new SimCredit(containerSpace.CharacterId, containerSpace.CorporationId, containerSpace.AllianceId, containerSpace.WarFactionId), false));
            }
            catch (Exception error)
            {
                _placed.TryRemove(container.Id, out _);
                _log?.Invoke($"loot container {container.Id} was saved but not placed in the simulation: {error.Message}");
            }
        }
        // the pilot is told what was recovered once the grant is kept; a replayed grant has no change and is not told again
        var recovered = _statics.Type(salvageTypeId.Value)?.Name ?? string.Empty;
        return new WorkOutcome(true, "", 0, 1, true, Notice: salvaged.Change is null ? null : Py.Tuple(new PyStr("CustomNotify"), new PyDict([
            new(new PyStr("notify"), new PyStr($"Salvaging succeeded. Recovered 1 x {recovered} and placed it in your cargo hold."))])));
    }

    private int? SelectSalvageProductTypeId(TypeDef wreckType, SpaceItem wreckSpace, IReadOnlyList<TypeDef> products)
    {
        if (products.Count == 0)
        {
            return null;
        }

        var factionName = wreckSpace.WarFactionId > 0 && wreckSpace.WarFactionId <= int.MaxValue
            ? _statics.FactionName((int)wreckSpace.WarFactionId)
            : "";
        var identity = $"{factionName} {wreckType.Name}";
        var difficultyId = _statics.AttributeId("accessDifficulty");
        var advanced = identity.Contains("Tech II", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("T2", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Officer", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Faction Wreck", StringComparison.OrdinalIgnoreCase);
        if (difficultyId is { } attributeId
            && wreckType.Attributes.TryGetValue(attributeId, out var baseChance)
            && baseChance <= 0)
        {
            advanced = true;
        }

        // EVE University maps salvage families by wreck faction; unknown families fall back to published SDE salvage types.
        var materialNames = SalvageMaterialNames(identity, advanced);
        IReadOnlyList<TypeDef> pool = materialNames is null
            ? products
            : products.Where(product => materialNames.Contains(product.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (pool.Count == 0)
        {
            pool = products;
        }
        var ordered = pool.OrderBy(product => product.TypeId).ToArray();
        return ordered[(int)(wreckSpace.ItemId % ordered.Length)].TypeId;
    }

    private static string[]? SalvageMaterialNames(string identity, bool advanced)
    {
        static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        if (advanced)
        {
            if (Has(identity, "Angel"))
            {
                return ["Artificial Neural Network", "Impetus Console", "Interface Circuit", "Logic Circuit",
                    "Micro Circuit", "Power Circuit", "Single-crystal Superalloy I-beam", "Trigger Unit"];
            }
            if (Has(identity, "Blood"))
            {
                return ["Artificial Neural Network", "Capacitor Console", "Interface Circuit", "Logic Circuit",
                    "Micro Circuit", "Power Circuit", "Power Conduit"];
            }
            if (Has(identity, "Guristas"))
            {
                return ["Artificial Neural Network", "Enhanced Ward Console", "Intact Shield Emitter",
                    "Interface Circuit", "Logic Circuit", "Micro Circuit", "Power Circuit", "Telemetry Processor"];
            }
            if (Has(identity, "Sansha"))
            {
                return ["Artificial Neural Network", "Current Pump", "Intact Armor Plates", "Interface Circuit",
                    "Logic Circuit", "Micro Circuit", "Nanite Compound", "Power Circuit"];
            }
            if (Has(identity, "Serpentis"))
            {
                return ["Conductive Thermoplastic", "Drone Transceiver", "Interface Circuit", "Logic Circuit",
                    "Lorentz Fluid", "Micro Circuit", "Power Circuit"];
            }
            return null;
        }

        if (Has(identity, "Rogue Drone"))
        {
            return ["Alloyed Tritanium Bar", "Armor Plates", "Broken Drone Transceiver", "Burned Logic Circuit",
                "Charred Micro Circuit", "Conductive Polymer", "Contaminated Lorentz Fluid", "Contaminated Nanite Compound",
                "Defective Current Pump", "Fried Interface Circuit", "Malfunctioning Shield Emitter", "Melted Capacitor Console",
                "Scorched Telemetry Processor", "Smashed Trigger Unit", "Tangled Power Conduit", "Thruster Console",
                "Tripped Power Circuit", "Ward Console"];
        }
        if (Has(identity, "Minmatar") || Has(identity, "Angel"))
        {
            return ["Alloyed Tritanium Bar", "Armor Plates", "Burned Logic Circuit", "Charred Micro Circuit",
                "Contaminated Nanite Compound", "Fried Interface Circuit", "Malfunctioning Shield Emitter",
                "Smashed Trigger Unit", "Thruster Console", "Tripped Power Circuit"];
        }
        if (Has(identity, "Caldari") || Has(identity, "Guristas"))
        {
            return ["Burned Logic Circuit", "Charred Micro Circuit", "Conductive Polymer",
                "Damaged Artificial Neural Network", "Fried Interface Circuit", "Malfunctioning Shield Emitter",
                "Scorched Telemetry Processor", "Tripped Power Circuit", "Ward Console"];
        }
        if (Has(identity, "Gallente") || Has(identity, "Serpentis"))
        {
            return ["Armor Plates", "Broken Drone Transceiver", "Burned Logic Circuit", "Charred Micro Circuit",
                "Conductive Polymer", "Contaminated Lorentz Fluid", "Contaminated Nanite Compound",
                "Damaged Artificial Neural Network", "Fried Interface Circuit", "Tripped Power Circuit"];
        }
        if (Has(identity, "Amarr") || Has(identity, "Blood") || Has(identity, "Sansha"))
        {
            return ["Armor Plates", "Burned Logic Circuit", "Charred Micro Circuit",
                "Contaminated Nanite Compound", "Defective Current Pump", "Fried Interface Circuit",
                "Melted Capacitor Console", "Tangled Power Conduit", "Tripped Power Circuit"];
        }
        return null;
    }

    private void WreckPlaced(SimPlaceWreck wreck)
    {
        _placed[wreck.WreckId] = 0;
        _ = KeepAsync(wreck);
    }

    private async Task KeepAsync(SimPlaceWreck wreck)
    {
        try
        {
            var item = new SpaceItem(
                wreck.WreckId, wreck.SolarSystemId, wreck.Position, wreck.Direction, wreck.Radius, wreck.GroupId, wreck.LauncherId, wreck.Credit.CharacterId, wreck.Credit.CorporationId,
                wreck.Credit.AllianceId, wreck.Credit.WarFactionId, _nowUnixMs() + (long)CanLifetime.TotalMilliseconds);
            var saved = _abandonedLoot.ContainsKey(wreck.WreckId) ? Abandon(item) : item;
            await _spaceItems.SaveAsync(saved);
            if (_abandonedLoot.ContainsKey(wreck.WreckId) && !IsAbandoned(saved))
            {
                await _spaceItems.SaveAsync(Abandon(saved));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"where wreck {wreck.WreckId} lies was not kept: {error.Message}");
        }
    }

    private void SystemEntered(int system) => _ = RestoreAsync(system);

    /// <summary>
    /// A ship has come into the scene of a solar system: what lies there (the cans and the wrecks that were put in space before the host last started, or before the
    /// simulation was last made) and that the simulation does not have is put in it, as it was and with what is in it told. What has run out of time goes instead.
    /// </summary>
    internal async Task RestoreAsync(int system)
    {
        try
        {
            var now = _nowUnixMs();
            foreach (var item in await _spaceItems.InSystemAsync(system))
            {
                if (!_placed.TryAdd(item.ItemId, 0))
                {
                    continue;
                }
                try
                {
                    if (item.ExpiresAtMs <= now || await _items.GetAsync(item.ItemId) is not { } stored)
                    {
                        // out of time, or the item is gone: the row goes (an expired one with what is in it)
                        if (item.ExpiresAtMs <= now)
                        {
                            await _ships.RemoveContainerAsync(item.ItemId, withContents: true);
                        }
                        await ForgetAsync(item.ItemId);
                        continue;
                    }
                    await _statics.EnsureTypesAsync([stored.TypeId]);
                    var type = _statics.Type(stored.TypeId);
                    if (type is not null && _statics.CategoryName(type.CategoryId) == "Asteroid")
                    {
                        if (item.LauncherId > 0)
                        {
                            _seededAsteroidBelts.TryAdd((system, item.LauncherId), 0);
                        }
                        if (stored.Quantity <= 0)
                        {
                            continue;
                        }
                        _sim.PlaceAsteroid(new SimPlaceAsteroid(
                            system, item.ItemId, stored.TypeId, type.GroupId, type.CategoryId, stored.CustomName ?? type.Name ?? "",
                            type.GraphicId, item.Position, item.Direction, item.Radius, stored.Quantity));
                        continue;
                    }
                    // Active ships persist here for login-in-space; the simulator owns their ball, not the container service.
                    if (type is not null && _statics.CategoryName(type.CategoryId) == "Ship" && item.GroupId != WreckGroup)
                    {
                        _placed.TryRemove(item.ItemId, out _);
                        continue;
                    }
                    var empty = (await _items.InContainerAsync(item.ItemId)).Count == 0;
                    _sim.PlaceWreck(new SimPlaceWreck(
                        system, item.ItemId, item.GroupId,
                        new SimWreckKind(stored.TypeId, stored.CustomName ?? type?.Name ?? "", type?.GraphicId ?? 0),
                        item.Position, item.Direction, item.Radius, item.LauncherId,
                        new SimCredit(item.CharacterId, item.CorporationId, item.AllianceId, item.WarFactionId), empty));
                }
                catch
                {
                    _placed.TryRemove(item.ItemId, out _);
                    throw;
                }
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"what lies in space in system {system} was not put back: {error.Message}");
        }
    }
}
