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
    private readonly DogmaStatic _statics;
    private readonly SpaceSessions _space;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, long> _due = new();
    private readonly ConcurrentDictionary<ulong, long> _cooldownUntil = new();
    private readonly Timer? _sweeper;

    public SpaceContainers(ISimulation sim, CharacterShips ships, PgItemStore items, DogmaStatic statics, SpaceSessions space, Func<long> nowUnixMs, Action<string>? log = null, TimeSpan? sweepEvery = null)
    {
        (_sim, _ships, _items, _statics, _space, _nowUnixMs, _log) = (sim, ships, items, statics, space, nowUnixMs, log);
        if (sweepEvery is { } every && every > TimeSpan.Zero)
        {
            _sweeper = new Timer(_ => _ = SweepAsync(), null, every, every);
        }
    }

    public void Dispose() => _sweeper?.Dispose();

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
        _due.TryRemove(can.Id, out _);
        session.SendNotification(
            "OnItemsChanged", "charid",
            new PyList([InventoryWire.PackedRow(RowOf(removed with { LocationId = RemovedCanLocation, Flag = 0, Quantity = 1 }))]),
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

        var moved = await _ships.JettisonIntoAsync(characterId, shipId, can.Id, itemIds, cancellationToken);
        foreach (var stack in moved)
        {
            Eve.Services.Ships.ShipNotifications.SendItemsChanged(session, stack, RowOf);
        }
        if (moved.Count == 0)
        {
            // nothing could be jettisoned: the can goes as it came
            await _ships.RemoveContainerAsync(can.Id, withContents: false, CancellationToken.None);
            _sim.RemoveWreck(can.Id, 0);
            return [];
        }
        await TellChangeAsync(id, can.Id, isEmpty: false);
        _due[can.Id] = now + (long)CanLifetime.TotalMilliseconds;
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

    private async Task SweepAsync()
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

    /// <summary>The cans whose two hours are up are taken out of the world with what is in them and out of space (the scene ends them by its own clock).</summary>
    internal async Task ExpireDueAsync(long nowUnixMs)
    {
        foreach (var (canId, due) in _due.ToArray())
        {
            if (due > nowUnixMs || !_due.TryRemove(canId, out _))
            {
                continue;
            }
            await _ships.RemoveContainerAsync(canId, withContents: true);
            _sim.RemoveWreck(canId, 0);
        }
    }

    /// <summary>The cans this host is keeping time for (a test asks what has been scheduled).</summary>
    internal IReadOnlyDictionary<long, long> Due => _due;
}
