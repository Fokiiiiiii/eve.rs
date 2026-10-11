namespace Eve.Services.Space;

/// <summary>A container an analyzer can hack: its site's difficulty and whether it is a relic container.</summary>
public sealed record HackableTarget(long ItemId, long SolarSystemId, int Difficulty, bool Relic);

/// <summary>What the hacking service needs from the space host: the container, the window's reach and the end of an attempt.</summary>
public interface IHackingSite
{
    /// <summary>The hackable container with this id that the character may work on, or null.</summary>
    ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken);

    /// <summary>Whether the pilot's ship is within the analyzer's reach of the container (surface distance, the simulation's positions).</summary>
    ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken);

    /// <summary>An attempt ended: won clears the container, lost counts toward its destruction.</summary>
    ValueTask AttemptEndedAsync(ClientSession session, HackableTarget target, bool won, CancellationToken cancellationToken);
}

/// <summary>
/// The space host's side of <see cref="IHackingSite"/>: the reach from the simulation's positions. The containers and the
/// ends of attempts are seams the site content fills (<see cref="Find"/>, <see cref="Ended"/>). Until a container is placed, <see cref="Find"/>
/// finds none and every analyzer activation is refused as <c>ANALYZER_TARGET_INVALID</c>.
/// </summary>
public sealed class SpaceHackingSite(SpaceSessions space) : IHackingSite
{
    public Func<long, long, CancellationToken, ValueTask<HackableTarget?>> Find { get; set; } =
        (_, _, _) => ValueTask.FromResult<HackableTarget?>(null);

    public Func<ClientSession, HackableTarget, bool, CancellationToken, ValueTask> Ended { get; set; } =
        (_, _, _, _) => ValueTask.CompletedTask;

    public ValueTask<HackableTarget?> FindAsync(long characterId, long targetId, CancellationToken cancellationToken) =>
        Find(characterId, targetId, cancellationToken);

    public async ValueTask<bool> InReachAsync(ClientSession session, long targetId, double maxRange, CancellationToken cancellationToken) =>
        await space.SurfaceDistanceToItemAsync(session, targetId, cancellationToken) is { } distance && distance <= maxRange + 1;

    public ValueTask AttemptEndedAsync(ClientSession session, HackableTarget target, bool won, CancellationToken cancellationToken) =>
        Ended(session, target, won, cancellationToken);
}
