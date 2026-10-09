using Eve.Characters;
using Eve.Corporations;
using Eve.Marshal;

namespace Eve.Services.Corporations;

/// <summary>Refresh live role masks from durable membership after login or a committed corporation mutation.</summary>
public sealed class CorporationLoginHook(CorporationStore store,PgCharacterStore characters) : ICharacterEntryHook
{
    public async ValueTask OnCharacterEnteredAsync(ClientSession session,long characterId,CancellationToken cancellationToken) =>
        session.Change((await GetSessionAttributesAsync(session,characterId,cancellationToken)).ToArray());

    public async ValueTask<IReadOnlyList<(string Name,PyValue Value)>> GetSessionAttributesAsync(ClientSession session,long characterId,CancellationToken cancellationToken)
    {
        if (await characters.GetAsync(characterId,cancellationToken) is not { } character) return [];
        var corp = await store.GetAsync(character.CorporationId,cancellationToken);
        var member = await store.MemberAsync(characterId,cancellationToken);
        var roles = corp is not null && !corp.IsNpc && member is not null
            ? CorporationRoles.Session(corp,member,character.StationId,await store.TitlesAsync(corp.Id,cancellationToken))
            : new SessionRoles(0,0,0,0,0,null,null,1000);
        return [("corpid",new PyInt(character.CorporationId)),("allianceid",CorporationWire.Id(character.AllianceId)),
            ("factionid",CorporationWire.Id(character.FactionId)),
            ("warfactionid",CorporationWire.Id(character.DirectFactionId ?? (corp is { IsNpc: false } ? CorporationWire.FactionId(corp) : null))),
            ("hqID",CorporationWire.Id(roles.HqId)),
            ("baseID",CorporationWire.Id(roles.BaseId)),("corpAccountKey",new PyInt(roles.AccountKey)),
            ("corprole",new PyLong(roles.CorpRole)),("rolesAtAll",new PyLong(roles.AtAll)),("rolesAtHQ",new PyLong(roles.AtHq)),
            ("rolesAtBase",new PyLong(roles.AtBase)),("rolesAtOther",new PyLong(roles.AtOther))];
    }
}
