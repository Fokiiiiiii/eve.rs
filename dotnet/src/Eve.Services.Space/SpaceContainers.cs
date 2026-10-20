using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Planets;
using Eve.Scheduler;
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
public sealed partial class SpaceContainers : ISpaceContainers, IDisposable
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
    private const int AnalysisBeaconGroup = 4137;
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
    private readonly PgDeployedStore? _deployed;
    private readonly Dictionary<string, Func<DeployedRecord, Task>> _deployedExpiry;
    private readonly DurableScheduler? _scheduler;
    private readonly IFactBus _events;
    private readonly DogmaStatic _statics;
    private readonly SpaceSessions _space;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, byte> _placed = new();
    /// <summary>The belts whose rocks are being seeded now (a second arrival does not seed the same belt twice). Whether a belt has rocks is read from the store.</summary>
    private readonly ConcurrentDictionary<(int SystemId, long BeltId), byte> _seedingBelts = new();
    /// <summary>The belts whose gas cloud is being seeded now (a second arrival does not make a second cloud).</summary>
    private readonly ConcurrentDictionary<(int SystemId, long BeltId), byte> _seedingClouds = new();
    private readonly ConcurrentDictionary<ulong, long> _cooldownUntil = new();

    /// <summary>The roll of a mining crystal's volatility (a number in 0 to 1, a hit at or under the chance): the host draws it, as EVE.js does.</summary>
    public Func<double> VolatilityRoll { get; set; } = Random.Shared.NextDouble;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _abandonedLoot = new();

    public SpaceContainers(
        ISimulation sim, CharacterShips ships, PgItemStore items, PgSpaceItemStore spaceItems, PgWorkCycleLedger cycles, IFactBus events, DogmaStatic statics,
        SpaceSessions space, Func<long> nowUnixMs, Action<string>? log = null, TimeSpan? sweepEvery = null, PgDeployedStore? deployed = null,
        DurableScheduler? scheduler = null)
    {
        (_sim, _ships, _items, _spaceItems, _cycles, _events, _statics, _space, _nowUnixMs, _log) = (sim, ships, items, spaceItems, cycles, events, statics, space, nowUnixMs, log);
        (_deployed, _scheduler) = (deployed, scheduler);
        _deployedExpiry = new() { [CynoFieldKind] = ExpireCynoFieldAsync, [DisruptorKind] = ExpireDisruptorAsync, [InhibitorKind] = ExpireDisruptorAsync, [BeaconKind] = ExpireBeaconAsync, [ObservatoryKind] = ExpireDisruptorAsync };
        // what the simulation held is gone with it; a scene that is made gets what lies in its system, and a wreck the host puts in space is kept
        _sim.Received += OnReceived;
        _sim.Lost += Forgotten;
        _space.SystemEntered += SystemEntered;
        _space.WreckPlaced += WreckPlaced;
        // the rocks of a belt are items of the store: a module's target is looked up here (SpaceSessions.ActivateAsync)
        _space.TypeOfSpaceItemAsync = async (id, _) => (await _items.GetAsync(id))?.TypeId;
        _space.MayPullAsync = MayPullAsync;
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
        var type = _statics.Type(item.TypeId);
        if (type is null ||
            (type.GroupId != CanGroup && type.GroupId != WreckGroup && type.GroupId != AnalysisBeaconGroup && type.GroupId != Customs.OfficeGroupId &&
             !Eve.Structures.StructureUnanchorDrop.IsFittingsContainer(item.TypeId, item.CustomInfo) &&
             !(await IsLootedSiteContainerAsync(item.Id, item.TypeId, type.GroupId, cancellationToken))))
        {
            return null;
        }
        if (type.GroupId == AnalysisBeaconGroup && item.CustomInfo != "analysis_beacon_wreck"
            && !HasAnalysisBeaconLootAccess(item, session.CharacterId)) return null;
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

    private async Task<bool> MayTakeFromAsync(long characterId, Item container, CancellationToken cancellationToken, SpaceItem? rights = null, IReadOnlyList<long>? itemIds = null)
    {
        if (container.CustomInfo == "analysis_beacon_wreck")
        {
            return true;
        }
        if (container.CustomInfo?.StartsWith("analysis_beacon_loot:", StringComparison.Ordinal) == true)
            return HasAnalysisBeaconLootAccess(container, characterId);
        if (Eve.Structures.StructureUnanchorDrop.IsFittingsContainer(container.TypeId, container.CustomInfo) ||
            _abandonedLoot.ContainsKey(container.Id) || container.OwnerId == characterId)
        {
            return true;
        }
        if (await CanTakeFromSiteContainerAsync(characterId, container, itemIds, cancellationToken))
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

    private bool HasAnalysisBeaconLootAccess(Item item, long? characterId)
    {
        var fields = item.CustomInfo?.Split(':');
        return characterId is > 0 && fields is { Length: 3 } && fields[0] == "analysis_beacon_loot"
            && long.TryParse(fields[1], out var linkedPilot) && linkedPilot == characterId.Value
            && long.TryParse(fields[2], out var accessUntil) && _nowUnixMs() < accessUntil;
    }

    /// <summary>
    /// Whether the pilot may pull the wreck or can with a tractor beam: the rights that its stored loot rights give (the same rights as taking from it:
    /// </summary>
    private async Task<bool?> MayPullAsync(long characterId, long wreckId, CancellationToken cancellationToken)
    {
        if (await _items.GetAsync(wreckId, cancellationToken) is { } item &&
            Eve.Structures.StructureUnanchorDrop.IsArtifact(item.CustomInfo))
        {
            return true;
        }
        if (await _spaceItems.GetAsync(wreckId, cancellationToken) is not { } rights)
        {
            return null;
        }
        if (IsAbandoned(rights) || rights.CharacterId == characterId)
        {
            return true;
        }
        return rights.CorporationId > 0
            && await _ships.CharacterAsync(characterId, cancellationToken) is { } pilot
            && pilot.CorporationId == rights.CorporationId;
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
        if (toShip && !await MayTakeFromAsync(characterId, container, cancellationToken, itemIds: [itemId]))
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

    public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId,
        int? destinationFlag, CancellationToken cancellationToken) =>
        MoveAllAsync(session, itemIds, sourceId, destinationId, destinationFlag, null, cancellationToken);

    public async Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId,
        int? destinationFlag, long? quantity, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId || !_space.IsInSpace(session)
            || destinationId != shipId || sourceId == shipId)
            return null;
        if (await OpenAsync(session, sourceId, cancellationToken) is not { } container) return null;
        if (!await MayTakeFromAsync(characterId, container, cancellationToken, itemIds: itemIds))
            throw MachoErrors.ThrowUserError("SafetyActivated");
        var flag = destinationFlag ?? CargoFlag;
        switch (await _ships.MoveAllInSpaceAsync(characterId, itemIds, sourceId, flag, cancellationToken, quantity))
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

    public Task RefreshShipNameAsync(ClientSession session, long itemId, CancellationToken cancellationToken) =>
        session.GetInt("shipid") == itemId && _space.IsInSpace(session)
            ? _space.RefreshShipAsync(session, cancellationToken)
            : Task.CompletedTask;

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
        var runSpaceScope = _space.RunSpaceScopeOf(session);
        var expectedScene = runSpaceScope is { } scope
            ? new SimSceneKey(scope.SpaceLocationId, scope.InstanceId, scope.RoomOrdinal)
            : SimSceneKey.SolarSystem(system);
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
        var can = await _ships.CreateCanAsync(characterId, system, cancellationToken, runSpaceScope: runSpaceScope);

        // the can comes into space first (the client is told the ball and the effect), then the stacks go into it
        CommandReply placed;
        try
        {
            placed = await _sim.PlaceContainerAsync(new SimPlaceContainer(
                id, can.Id, new SimWreckKind(CharacterShips.CargoContainerTypeId, canType?.Name ?? "Cargo Container", canType?.GraphicId ?? 0),
                canType?.Attributes.GetValueOrDefault(162, CanRadiusFallback) ?? CanRadiusFallback, shipId,
                new SimCredit(pilot.Id, pilot.CorporationId, pilot.AllianceId ?? 0, 0), JettisonDistance)
                { ExpectedScene = expectedScene }, cancellationToken);
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
            await ExpireBuffsAsync(_nowUnixMs());
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
            // The extraction's durable job removes the complete moon field and publishes its asteroid removal wire.
            if (due.MoonExtractionId > 0)
                continue;
            await _ships.RemoveContainerAsync(due.ItemId, withContents: true);
            await ForgetAsync(due.ItemId);
            _sim.RemoveWreck(due.ItemId, 0);
        }
        await ExpireDeployedAsync(nowUnixMs);
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
        else if (message is SimModuleEnded ended && EntosisCaptures is not null)
        {
            _ = EndEntosisLinkAsync(ended.ModuleId);
        }
        else if (message is SimArrived arrived)
        {
            _ = SeedAsteroidBeltAsync(arrived);
            _ = SeedGasCloudAsync(arrived);
        }
        else if (message is SimDeployed lit)
        {
            _ = KeepDeployedAsync(lit);
        }
    }

    public Eve.Services.Space.Entosis.EntosisCaptureService? EntosisCaptures { get; set; }

    /// <summary>A module's end report: an entosis link stops counting (its capture is moved to now first). A failure is logged; the capture is then moved at its next event.</summary>
    private async Task EndEntosisLinkAsync(long moduleId)
    {
        try
        {
            await EntosisCaptures!.LinkEndedAsync(moduleId, CancellationToken.None);
        }
        catch (Exception error)
        {
            _log?.Invoke($"entosis link of module {moduleId} was not ended: {error.Message}");
        }
    }

    private void WreckPlaced(SimPlaceWreck wreck)
    {
        _placed[wreck.WreckId] = 0;
        _space.Background(() => KeepAsync(wreck));
    }

    private async Task KeepAsync(SimPlaceWreck wreck)
    {
        try
        {
            if (wreck.Scene.InstanceId > 0)
            {
                // Abyssal loot is owned by the run's persisted entity plan, not the normal solar-system restore table.
                return;
            }
            if (Eve.Structures.StructureUnanchorDrop.IsArtifact((await _items.GetAsync(wreck.WreckId))?.CustomInfo))
            {
                // The transactional handoff wrote an unbounded space row; don't replace it with a jettison lifetime.
                return;
            }
            var item = new SpaceItem(
                wreck.WreckId, wreck.Scene.LocationId, wreck.Position, wreck.Direction, wreck.Radius, wreck.GroupId, wreck.LauncherId, wreck.Credit.CharacterId, wreck.Credit.CorporationId,
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

    private void SystemEntered(int system)
    {
        _ = RestoreAsync(system);
        _ = RestoreDeployedAsync(system);
    }

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
                    if (item.MoonExtractionId > 0 && item.ExpiresAtMs <= now)
                    {
                        continue;
                    }
                    var stored = await _items.GetAsync(item.ItemId);
                    if (stored is null && item.MoonExtractionId > 0)
                    {
                        continue;
                    }
                    if (item.ExpiresAtMs <= now || stored is null)
                    {
                        // out of time, or the item is gone: the row goes (an expired one with what is in it)
                        if (item.ExpiresAtMs <= now)
                        {
                            await _ships.RemoveContainerAsync(item.ItemId, withContents: true);
                        }
                        await ForgetAsync(item.ItemId);
                        continue;
                    }
                    // Private-run items are restored/settled by their exact durable run scope, never as public solar-system balls.
                    if (stored.RunSpaceProvenance is not null) continue;
                    await _statics.EnsureTypesAsync([stored.TypeId]);
                    var type = _statics.Type(stored.TypeId);
                    if (type is not null && _statics.CategoryName(type.CategoryId) == "Asteroid")
                    {
                        if (stored.Quantity <= 0)
                        {
                            continue;
                        }
                        _sim.PlaceAsteroid(new SimPlaceAsteroid(
                            system, item.ItemId, stored.TypeId, type.GroupId, type.CategoryId, stored.CustomName ?? type.Name ?? "",
                            item.MoonGraphicId > 0 ? item.MoonGraphicId : type.GraphicId,
                            item.Position, item.Direction, item.Radius, stored.Quantity));
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
                        SimSceneKey.SolarSystem(system), item.ItemId, item.GroupId,
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

    internal void RemoveMoonFieldRocks(IEnumerable<long> asteroidIds)
    {
        foreach (var asteroidId in asteroidIds)
        {
            if (asteroidId <= 0) continue;
            _placed.TryRemove(asteroidId, out _);
            _sim.RemoveWreck(asteroidId, 2);
        }
    }
}
