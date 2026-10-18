namespace Eve.Services.CharacterSelect;

/// <summary>
/// What a deleted character leaves behind (EveJS <c>characterDeletionRuntime.js</c>, <c>deleteCharacter</c>): its assets are moved
/// into the corporation's deliveries, or destroyed when its corporation is an NPC corporation, and its private state is cleared.
/// The host supplies the implementation from PostgreSQL.
/// </summary>
public interface ICharacterDeletionPurge
{
    /// <summary>Handles the character's assets and private state. <paramref name="corporationId"/> is the corporation it had when deleted.</summary>
    Task PurgeAsync(long characterId, long corporationId, CancellationToken cancellationToken);
}
