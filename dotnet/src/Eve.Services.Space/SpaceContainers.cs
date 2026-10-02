using System.Collections.Concurrent;
using Eve.Dogma;
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

    private readonly ISimulation _sim;
    private readonly CharacterShips _ships;
    private readonly PgItemStore _items;
    private readonly PgSpaceItemStore _spaceItems;
    private readonly DogmaStatic _statics;
    private readonly SpaceSessions _space;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, byte> _placed = new();
    private readonly ConcurrentDictionary<ulong, long> _cooldownUntil = new();
    private readonly Timer? _sweeper;

    public SpaceContainers(
        ISimulation sim, CharacterShips ships, PgItemStore items, PgSpaceItemStore spaceItems, DogmaStatic statics, SpaceSessions space, Func<long> nowUnixMs, Action<string>? log = null,
        TimeSpan? sweepEvery = null)
    {
        (_sim, _ships, _items, _spaceItems, _statics, _space, _nowUnixMs, _log) = (sim, ships, items, spaceItems, statics, space, nowUnixMs, log);
        // what the simulation held is gone with it; a scene that is made gets what lies in its system, and a wreck the host puts in space is kept
        _sim.Lost += Forgotten;
        _space.SystemEntered += SystemEntered;
        _space.WreckPlaced += WreckPlaced;
        if (sweepEvery is { } every && every > TimeSpan.Zero)
        {
            _sweeper = new Timer(_ => _ = SweepAsync(), null, every, every);
        }
    }

    public void Dispose()
    {
        _sim.Lost -= Forgotten;
        _space.SystemEntered -= SystemEntered;
        _space.WreckPlaced -= WreckPlaced;
        _sweeper?.Dispose();
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

    private bool IsCan(Item container) => container.TypeId == CharacterShips.CargoContainerType;

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
    private async Task<bool> MayTakeFromAsync(long characterId, Item container, CancellationToken cancellationToken)
    {
        if (container.OwnerId == characterId)
        {
            return true;
        }
        return await _ships.CharacterAsync(characterId, cancellationToken) is { } pilot
            && await _ships.CharacterAsync(container.OwnerId, cancellationToken) is { } owner && owner.CorporationId == pilot.CorporationId;
    }

    // ── moving goods ─────────────────────────────────────────────────────────

    public async Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId || !_space.IsInSpace(session))
        {
            return null;
        }
        var toShip = destinationId == shipId;
        if (toShip == (sourceId == shipId))
        {
            return null; // goods go between the ship and a container, not within one of them
        }
        var containerId = toShip ? sourceId : destinationId;
        if (await OpenAsync(session, containerId, cancellationToken) is not { } container)
        {
            return null;
        }
        if (toShip && !await MayTakeFromAsync(characterId, container, cancellationToken))
        {
            throw MachoErrors.ThrowUserError("SafetyActivated");
        }
        var wasEmpty = (await _items.InContainerAsync(containerId, cancellationToken)).Count == 0;
        switch (await _ships.MoveInSpaceAsync(characterId, itemId, sourceId, destinationId, quantity, cancellationToken))
        {
            case ShipRefused refused:
                throw ShipErrors.Of(refused, _statics);
            case ShipChanged { Stacks: { } stacks } changed:
                await TellMoveAsync(session, shipId, container, toShip, wasEmpty, stacks, cancellationToken);
                return changed.ItemId;
            default:
                return null;
        }
    }

    private async Task TellMoveAsync(ClientSession session, long shipId, Item container, bool toShip, bool wasEmpty, IReadOnlyList<ItemChanged> stacks, CancellationToken cancellationToken)
    {
        var ulongSession = (ulong)session.SessionId;
        var context = Py.Tuple(new PyStr("Ship"), InventoryWire.Id(shipId), new PyStr("ShipCargo"));
        foreach (var stack in stacks)
        {
            if (toShip)
            {
                Eve.Services.Ships.ShipNotifications.SendItemsChanged(session, stack, RowOf, context);
            }
            else
            {
                Eve.Services.Ships.ShipNotifications.SendItemChange(session, stack, RowOf);
            }
        }
        var empty = (await _items.InContainerAsync(container.Id, cancellationToken)).Count == 0;
        var disposed = toShip && empty && IsCan(container);
        if (!disposed && (toShip || wasEmpty))
        {
            await TellChangeAsync(ulongSession, container.Id, empty);
        }
        if (toShip)
        {
            session.SendNotification("OnClientEvent_MoveFromCargoToHangar", "clientID", InventoryWire.Id(container.Id), InventoryWire.Id(shipId), new PyInt(CargoFlag));
        }
        if (disposed)
        {
            await RemoveEmptyCanAsync(session, container, cancellationToken);
        }
    }

    public async Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId || !_space.IsInSpace(session) || destinationId != shipId || sourceId == shipId)
        {
            return null;
        }
        if (await OpenAsync(session, sourceId, cancellationToken) is not { } container)
        {
            return null;
        }
        if (!await MayTakeFromAsync(characterId, container, cancellationToken))
        {
            throw MachoErrors.ThrowUserError("SafetyActivated");
        }
        switch (await _ships.MoveAllInSpaceAsync(characterId, itemIds, sourceId, cancellationToken))
        {
            case ShipRefused refused:
                throw ShipErrors.Of(refused, _statics);
            case ShipChanged { Stacks: { } stacks }:
                await TellMoveAllAsync(session, shipId, container, stacks, cancellationToken);
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
    private async Task TellMoveAllAsync(ClientSession session, long shipId, Item container, IReadOnlyList<ItemChanged> stacks, CancellationToken cancellationToken)
    {
        var ulongSession = (ulong)session.SessionId;
        foreach (var stack in stacks)
        {
            Eve.Services.Ships.ShipNotifications.SendItemsChanged(session, stack, RowOf, idType: "charid");
        }
        var empty = (await _items.InContainerAsync(container.Id, cancellationToken)).Count == 0;
        foreach (var _ in stacks)
        {
            await TellChangeAsync(ulongSession, container.Id, empty);
        }
        session.SendNotification("OnClientEvent_MoveFromCargoToHangar", "clientID", InventoryWire.Id(container.Id), InventoryWire.Id(shipId), new PyInt(CargoFlag));
        if (empty)
        {
            session.SendNotification(
                "OnWreckLootAll", "clientID", Py.Tuple(new PyStr("ItemFloatingCargo"), InventoryWire.Id(container.Id)),
                Py.Tuple([.. stacks.Select(stack => InventoryWire.Tuple(RowOf(stack.After with { LocationId = stack.PreviousLocationId, Flag = stack.PreviousFlag, Quantity = stack.PreviousQuantity })))]));
        }
        if (empty && IsCan(container))
        {
            await RemoveEmptyCanAsync(session, container, cancellationToken);
        }
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
        await _statics.EnsureTypesAsync([CharacterShips.CargoContainerType], cancellationToken);
        var canType = _statics.Type(CharacterShips.CargoContainerType);
        var can = await _ships.CreateCanAsync(characterId, system, cancellationToken);

        // the can comes into space first (the client is told the ball and the effect), then the stacks go into it
        CommandReply placed;
        try
        {
            placed = await _sim.PlaceContainerAsync(new SimPlaceContainer(
                id, can.Id, new SimWreckKind(CharacterShips.CargoContainerType, canType?.Name ?? "Cargo Container", canType?.GraphicId ?? 0),
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
        await _spaceItems.RemoveAsync(itemId);
    }

    // ── keeping what lies in space ───────────────────────────────────────────

    /// <summary>The host put the wreck of a destroyed ship (or a corpse) in space: where it lies is kept, for two hours.</summary>
    private void WreckPlaced(SimPlaceWreck wreck)
    {
        _placed[wreck.WreckId] = 0;
        _ = KeepAsync(wreck);
    }

    private async Task KeepAsync(SimPlaceWreck wreck)
    {
        try
        {
            await _spaceItems.SaveAsync(new SpaceItem(
                wreck.WreckId, wreck.SolarSystemId, wreck.Position, wreck.Direction, wreck.Radius, wreck.GroupId, wreck.LauncherId, wreck.Credit.CharacterId, wreck.Credit.CorporationId,
                wreck.Credit.AllianceId, wreck.Credit.WarFactionId, _nowUnixMs() + (long)CanLifetime.TotalMilliseconds));
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
                if (_placed.ContainsKey(item.ItemId))
                {
                    continue;
                }
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
                var empty = (await _items.InContainerAsync(item.ItemId)).Count == 0;
                _placed[item.ItemId] = 0;
                _sim.PlaceWreck(new SimPlaceWreck(
                    system, item.ItemId, item.GroupId, new SimWreckKind(stored.TypeId, stored.CustomName ?? type?.Name ?? "", type?.GraphicId ?? 0), item.Position, item.Direction, item.Radius,
                    item.LauncherId, new SimCredit(item.CharacterId, item.CorporationId, item.AllianceId, item.WarFactionId), empty));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"what lies in space in system {system} was not put back: {error.Message}");
        }
    }
}
