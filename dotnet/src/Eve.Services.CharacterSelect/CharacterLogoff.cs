using Eve.Characters;
using Eve.Marshal;

namespace Eve.Services.CharacterSelect;

/// <summary>
/// The character logs off to the selection screen (<c>userSvc.UserLogOffCharacter</c>, the client's "log off character").
/// The character leaves the game the way it leaves on a change of character: every entry hook gives up what it holds for it
/// (a pilot in space is taken out of the scene, its guest entry is withdrawn), the logoff time is recorded (the selection
/// screen sorts by it), the character is no longer online, and the session drops the character attributes that selecting it
/// set. The connection stays open, so the client can select a character again. A session with no character has nothing to
/// leave and is answered as done, as the reference answers it.
/// </summary>
public sealed class CharacterLogoff(
    PgCharacterStore characters,
    OnlineCharacters online,
    IReadOnlyList<ICharacterEntryHook> entryHooks,
    Func<long> nowUnixMs)
{
    /// <summary>
    /// The character's attributes in the session, set by selecting it. Logging off sets each to None. The account's own
    /// attributes (the user, the role, the language) stay.
    /// </summary>
    internal static readonly string[] CharacterAttributes =
    [
        "charid", "corpid", "allianceid", "genderID", "bloodlineID", "raceID", "schoolID", "factionid", "warfactionid",
        "stationid", "stationid2", "structureid", "solarsystemid", "solarsystemid2", "constellationid", "regionid",
        "shipid", "locationid", "worldspaceid", "homeStationID", "cloneStationID", "fleetid", "fleetrole", "wingid",
        "squadid", "corpAccountKey", "hqID", "baseID", "corprole", "rolesAtAll", "rolesAtBase", "rolesAtHQ", "rolesAtOther",
    ];

    /// <summary>Logs the session's character off. Returns true when the session no longer holds a character.</summary>
    public async ValueTask<bool> LogOffAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || characterId <= 0)
            return true;

        foreach (var hook in entryHooks)
            await hook.OnCharacterLeavingAsync(session, characterId, cancellationToken);
        await characters.SetLastLogoffAsync(characterId, DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs()), cancellationToken);
        online.Leave(session.SessionId);
        session.Change([.. CharacterAttributes.Select(name => (name, (PyValue)PyNone.Instance))]);
        return true;
    }
}
