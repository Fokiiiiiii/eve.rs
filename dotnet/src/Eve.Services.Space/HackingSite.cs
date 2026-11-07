namespace Eve.Services.Space;

public sealed record HackableTarget(long ItemId, long SolarSystemId, int Difficulty, bool Relic);

/// <summary>One settled loot stack the client reveals on the hacking core.</summary>
public sealed record HackingLootItem(int TypeId, long Quantity);

/// <summary>What the hacking service needs from the space host: the container, the window's reach and the end of an attempt.</summary>
public interface IHackingSite
{
    /// <summary>The hackable container with this id that the character may work on, or null.</summary>
    ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken);

    /// <summary>Whether the pilot's ship is within the analyzer's reach of the container (surface distance, the simulation's positions).</summary>
    ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken);

    /// <summary>Atomically reserves the still-secure target before the analyzer waits for simulation acceptance.</summary>
    ValueTask<bool> TryReserveAsync(ClientSession session, HackableTarget target, Guid reservationId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    /// <summary>Releases a reservation when the analyzer activation is refused before a board opens.</summary>
    ValueTask ReleaseReservationAsync(Guid reservationId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Settles the reserved attempt once. A win returns the loot committed for the core-contents event.</summary>
    ValueTask<IReadOnlyList<HackingLootItem>> AttemptEndedAsync(
        ClientSession session, HackableTarget target, Guid reservationId, bool won, CancellationToken cancellationToken);
}

/// <summary>
/// The space host's side of <see cref="IHackingSite"/>: the reach from the simulation's positions. The containers and the
/// reservation and settlement seams the site content fills (<see cref="Find"/>, <see cref="Reserve"/>, <see cref="Settle"/>). Until a container is placed, <see cref="Find"/>
/// finds none and every analyzer activation is refused as <c>ANALYZER_TARGET_INVALID</c>.
/// </summary>
public sealed class SpaceHackingSite(SpaceSessions? space) : IHackingSite
{
    public Func<long, long, CancellationToken, ValueTask<HackableTarget?>> Find { get; set; } =
        (_, _, _) => ValueTask.FromResult<HackableTarget?>(null);

    public Func<ClientSession, HackableTarget, Guid, CancellationToken, ValueTask<bool>> Reserve { get; set; } =
        (_, _, _, _) => ValueTask.FromResult(false);

    public Func<Guid, CancellationToken, ValueTask> Released { get; set; } =
        (_, _) => ValueTask.CompletedTask;

    public Func<ClientSession, HackableTarget, Guid, bool, CancellationToken, ValueTask<IReadOnlyList<HackingLootItem>>> Settle { get; set; } =
        (_, _, _, _, _) => ValueTask.FromResult<IReadOnlyList<HackingLootItem>>([]);

    public ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken) =>
        Find(characterId, targetId, cancellationToken);

    /// <summary>Without a space simulation nothing is in reach, so an open window closes.</summary>
    public async ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken) =>
        space is not null && await space.SurfaceDistanceToItemAsync(session, targetId, cancellationToken) is { } distance && distance <= maxRange + 1;

    public ValueTask<bool> TryReserveAsync(ClientSession session, HackableTarget target, Guid reservationId, CancellationToken cancellationToken) =>
        Reserve(session, target, reservationId, cancellationToken);

    public ValueTask ReleaseReservationAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Released(reservationId, cancellationToken);

    public ValueTask<IReadOnlyList<HackingLootItem>> AttemptEndedAsync(
        ClientSession session, HackableTarget target, Guid reservationId, bool won, CancellationToken cancellationToken) =>
        Settle(session, target, reservationId, won, cancellationToken);
}
