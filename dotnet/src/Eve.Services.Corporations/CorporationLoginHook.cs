using Eve.Characters;
using Eve.Corporations;
using Eve.Marshal;

namespace Eve.Services.Corporations;

/// <summary>Refresh live role masks from durable membership after login or a committed corporation mutation.</summary>
public sealed class CorporationLoginHook(CorporationStore store,PgCharacterStore characters) : ICharacterEntryHook
{
    public async ValueTask OnCharacterEnteredAsync(ClientSession session,long characterId,CancellationToken cancellationToken)
    {
        if (await characters.GetAsync(characterId,cancellationToken) is not { } character) return;
        var corp = await store.GetAsync(character.CorporationId,cancellationToken);
        var member = await store.MemberAsync(characterId,cancellationToken);
        var roles = corp is not null && !corp.IsNpc && member is not null
            ? CorporationRoles.Session(corp,member,session.GetInt("stationid"),await store.TitlesAsync(corp.Id,cancellationToken))
            : new SessionRoles(0,0,0,0,0,corp?.StationId,null,1000);
        session.Change(("corpid",new PyInt(character.CorporationId)),("allianceid",CorporationWire.Id(character.AllianceId)),
            ("factionid",CorporationWire.Id(character.FactionId)),("hqID",CorporationWire.Id(roles.HqId)),
            ("baseID",CorporationWire.Id(roles.BaseId)),("corpAccountKey",new PyInt(roles.AccountKey)),
            ("corprole",new PyLong(roles.CorpRole)),("rolesAtAll",new PyLong(roles.AtAll)),("rolesAtHQ",new PyLong(roles.AtHq)),
            ("rolesAtBase",new PyLong(roles.AtBase)),("rolesAtOther",new PyLong(roles.AtOther)));
    }
}
