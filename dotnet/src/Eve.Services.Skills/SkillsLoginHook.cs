using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>
/// After a character is chosen the reference sends one unchanged skill of the character in <c>OnServerSkillsChanged</c>.
/// The client answers it by refreshing its whole skill cache before applying the change, so that one notification is
/// what makes it load its skills.
/// </summary>
public sealed class SkillsLoginHook(PgSkillStore store, SkillCatalog catalog, Func<long> nowUnixMs) : ICharacterEntryHook
{
    public async ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var skills = await store.ForCharacterAsync(characterId, cancellationToken);
        if (skills.FirstOrDefault(s => catalog.Get(s.TypeId) is not null) is not { } first)
        {
            return;
        }
        session.SendNotification(
            "OnServerSkillsChanged",
            "charid",
            SkillWire.EntryDictionary(characterId, catalog, [first]),
            PyNone.Instance,
            new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())));
    }
}
