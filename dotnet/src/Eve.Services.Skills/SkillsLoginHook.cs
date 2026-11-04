using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>
/// After a character is chosen the reference sends one unchanged skill of the character in <c>OnServerSkillsChanged</c>.
/// The client answers it by refreshing its whole skill cache before applying the change, so that one notification is
/// what makes it load its skills. What the character's training finished while it was away is settled first, and quietly:
/// the client is not told about levels it will find already trained. The session is then known to the training, so later
/// levels reach it as they finish.
/// </summary>
public sealed class SkillsLoginHook(CharacterTraining training, SkillCatalog catalog, CharacterSessions sessions, TrainingAnnouncer announcer, Func<long> nowUnixMs) : ICharacterEntryHook
{
    public async ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var account = session.UserId ?? 0;
        var view = await training.ViewAsync(characterId, account, cancellationToken);
        sessions.Enter(characterId, session);
        announcer.Reschedule(characterId, account, view);
        if (view.Skills.FirstOrDefault(s => catalog.Get(s.TypeId) is not null) is not { } first)
        {
            return;
        }
        session.SendNotification(
            "OnServerSkillsChanged",
            "charid",
            SkillWire.EntryDictionary(characterId, catalog, [first], view.InTraining),
            PyNone.Instance,
            new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())));
    }
}
