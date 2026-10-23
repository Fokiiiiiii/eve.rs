using Eve.Characters;

namespace Eve.Host.PublicGateway;

/// <summary>Applies persistent title reward grants and revocations, then publishes the corresponding character notice.</summary>
internal sealed class CharacterTitleRewardService(CharacterTitleStore store, IReadOnlySet<Guid> knownTitles, NoticeHub notices)
{
    private const string GrantedNoticeType = "eve_public.cosmetic.character.title.api.GrantedNotice";
    private const string RevokedNoticeType = "eve_public.cosmetic.character.title.api.RevokedNotice";

    /// <summary>Grants a known SDE title once. A duplicate grant does not emit another notice.</summary>
    public async Task<bool> GrantAsync(long characterId, Guid titleId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || !knownTitles.Contains(titleId))
            return false;
        if (!await store.GrantAsync(characterId, titleId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken))
            return false;
        notices.Publish(GrantedNoticeType, SkillPlanWire.EncodeIdentifier(titleId), characterId);
        return true;
    }

    /// <summary>Revokes an owned title once and notifies the character after the persisted mutation.</summary>
    public async Task<bool> RevokeAsync(long characterId, Guid titleId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || !knownTitles.Contains(titleId))
            return false;
        if (!await store.RevokeAsync(characterId, titleId, cancellationToken))
            return false;
        notices.Publish(RevokedNoticeType, SkillPlanWire.EncodeIdentifier(titleId), characterId);
        return true;
    }
}
