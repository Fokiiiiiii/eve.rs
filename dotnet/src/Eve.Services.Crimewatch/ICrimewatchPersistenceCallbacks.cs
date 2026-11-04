using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

/// <summary>
/// Host persistence boundary for the actual security-status relation history and shared
/// crimewatchRuntime limited-engagement rows. Engagement reads return only rows whose expiry is
/// greater than nowUnixMs, optionally restricted to a character.
/// </summary>
public interface ICrimewatchPersistenceCallbacks
{

    Task<IReadOnlyList<SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(long characterId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a pending challenge with a Host-selected expiry and delivers OnDuelChallenge to the target.
    /// Returns that exact expiry FILETIME, or null when the Host rejects the invitation.
    /// </summary>
    Task<long?> StartDuelChallengeAsync(
        long challengerCharacterId,
        long targetCharacterId,
        long challengerCorporationId,
        long challengerAllianceId,
        CancellationToken cancellationToken);

    /// <summary>Reads active pair rows in the same store used by weapon legality.</summary>
    Task<IReadOnlyList<LimitedEngagement>> LimitedEngagementsAsync(long nowUnixMs, long? characterId, CancellationToken cancellationToken);

    /// <summary>Upserts key le:min:max with kind=limitedEngagement, firstCharacterID, secondCharacterID and engagement.expiresAtMs.</summary>
    Task UpsertLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expiresAtUnixMs, CancellationToken cancellationToken);

    /// <summary>Deletes only the pair whose stored expiry equals expectedExpiresAtUnixMs and is due by nowUnixMs.</summary>
    Task<bool> ExpireLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expectedExpiresAtUnixMs, long nowUnixMs, CancellationToken cancellationToken);

    /// <summary>
    /// Validates and consumes the pending duel challenge in one transaction. A decline consumes the challenge
    /// without creating an engagement; an accepted challenge writes the shared pair row with the supplied expiry.
    /// Returns true only when an accepted, unexpired challenge was committed.
    /// </summary>
    Task<bool> RespondToDuelChallengeAsync(
        long responderCharacterId,
        long challengerCharacterId,
        long challengeExpiresAtFileTime,
        bool accept,
        long? engagementExpiresAtUnixMs,
        CancellationToken cancellationToken);
}
