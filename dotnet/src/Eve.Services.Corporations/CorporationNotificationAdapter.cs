using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

internal static class CorporationNotificationAdapter
{
    public static void MemberChanged(CharacterSessions? sessions,ServiceCall call,long corporationId,long characterId,
        Member? before,Member? after)
    {
        if (sessions is null) return;
        var payload = Py.Tuple(CorporationWire.Id(corporationId),CorporationWire.Id(characterId),MemberChange(before,after));
        call.Session.AfterResponse(() =>
        {
            var sent = new HashSet<ClientSession>();
            foreach (var session in sessions.OfCorporation(corporationId))
            {
                sent.Add(session);
                Send(session,"OnCorporationMemberChanged","corpid",payload);
            }
            if (sessions.Of(characterId) is { } member && sent.Add(member))
                Send(member,"OnCorporationMemberChanged","clientID",payload);
        });
    }

    public static void TitleChanged(CharacterSessions? sessions,ServiceCall call,long corporationId,
        CorporationTitle? before,CorporationTitle? after)
    {
        if (sessions is null) return;
        var titleId = after?.Id ?? before?.Id ?? 0;
        var payload = Py.Tuple(CorporationWire.Id(corporationId),new PyInt(titleId),TitleChange(before,after));
        call.Session.AfterResponse(() =>
        {
            foreach (var session in sessions.OfCorporation(corporationId))
                Send(session,"OnTitleChanged","corpid",payload);
        });
    }

public static void ApplicationChanged(CharacterSessions? sessions,ServiceCall call,long corporationId,
        long applicantId,long applicationId,PyValue applicationRow,IEnumerable<long> recipientIds)
    {
        if (sessions is null) return;
        var ids = recipientIds.Distinct().ToArray();
        var payload = Py.Tuple(CorporationWire.Id(corporationId),CorporationWire.Id(applicantId),
            CorporationWire.Id(applicationId),applicationRow);
        call.Session.AfterResponse(() =>
        {
            var targets = new HashSet<ClientSession>();
            foreach (var id in ids)
                if (sessions.Of(id) is { } recipient) targets.Add(recipient);
            if (call.Session.CharacterId is { } actor && ids.Contains(actor)) targets.Add(call.Session);
            foreach (var recipient in targets)
                Send(recipient,"OnCorporationApplicationChanged","clientID",payload);
        });
    }

    // eve.rs extension: the local EveJS medal service emits no push event on award.
    public static void MedalsReceived(CharacterSessions? sessions,ServiceCall call,long medalId,
        IEnumerable<long> recipients)
    {
        if (sessions is null) return;
        var uniqueRecipients = recipients.Distinct().ToArray();
        call.Session.AfterResponse(() =>
        {
            foreach (var id in uniqueRecipients)
                if (sessions.Of(id) is { } member)
                    Send(member,"OnMedalsReceived","clientID",Py.Tuple(CorporationWire.Id(medalId)));
        });
    }

    private static PyDict MemberChange(Member? before,Member? after)
    {
        var changes = new List<KeyValuePair<PyValue,PyValue>>();
        void Add(string name,PyValue oldValue,PyValue newValue)
        {
            if (oldValue.Equals(newValue)) return;
            changes.Add(new(new PyStr(name),new PyList([oldValue,newValue])));
        }
        Add("characterID",CorporationWire.Id(before?.CharacterId),CorporationWire.Id(after?.CharacterId));
        Add("corporationID",CorporationWire.Id(before?.CorporationId),CorporationWire.Id(after?.CorporationId));
        Add("startDate",before is null ? PyNone.Instance : CorporationWire.Time(before.StartedMs),
            after is null ? PyNone.Instance : CorporationWire.Time(after.StartedMs));
        Add("title",before is null ? PyNone.Instance : new PyWString(before.Title),
            after is null ? PyNone.Instance : new PyWString(after.Title));
        Add("divisionID",before is null ? PyNone.Instance : new PyInt(before.DivisionId),
            after is null ? PyNone.Instance : new PyInt(after.DivisionId));
        Add("squadronID",before is null ? PyNone.Instance : new PyInt(before.SquadronId),
            after is null ? PyNone.Instance : new PyInt(after.SquadronId));
        Add("roles",before is null ? PyNone.Instance : new PyLong(before.Masks.Roles),
            after is null ? PyNone.Instance : new PyLong(after.Masks.Roles));
        Add("grantableRoles",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableRoles),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableRoles));
        Add("rolesAtHQ",before is null ? PyNone.Instance : new PyLong(before.Masks.AtHq),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtHq));
        Add("grantableRolesAtHQ",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableHq),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableHq));
        Add("rolesAtBase",before is null ? PyNone.Instance : new PyLong(before.Masks.AtBase),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtBase));
        Add("grantableRolesAtBase",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableBase),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableBase));
        Add("rolesAtOther",before is null ? PyNone.Instance : new PyLong(before.Masks.AtOther),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtOther));
        Add("grantableRolesAtOther",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableOther),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableOther));
        Add("baseID",CorporationWire.Id(before?.BaseId),CorporationWire.Id(after?.BaseId));
        Add("titleMask",before is null ? PyNone.Instance : new PyInt(before.TitleMask),
            after is null ? PyNone.Instance : new PyInt(after.TitleMask));
        Add("blockRoles",before is null || !before.BlockRoles ? PyNone.Instance : new PyLong(1),
            after is null || !after.BlockRoles ? PyNone.Instance : new PyLong(1));
        Add("accountKey",before is null ? PyNone.Instance : new PyInt(before.AccountKey),
            after is null ? PyNone.Instance : new PyInt(after.AccountKey));
        return new PyDict(changes);
    }

    private static PyDict TitleChange(CorporationTitle? before,CorporationTitle? after)
    {
        var changes = new List<KeyValuePair<PyValue,PyValue>>();
        void Add(string name,PyValue oldValue,PyValue newValue)
        {
            if (oldValue.Equals(newValue)) return;
            changes.Add(new(new PyStr(name),new PyList([oldValue,newValue])));
        }
        Add("titleID",before is null ? PyNone.Instance : new PyInt(before.Id),
            after is null ? PyNone.Instance : new PyInt(after.Id));
        Add("titleName",before is null ? PyNone.Instance : new PyWString(before.Name),
            after is null ? PyNone.Instance : new PyWString(after.Name));
        Add("roles",before is null ? PyNone.Instance : new PyLong(before.Masks.Roles),
            after is null ? PyNone.Instance : new PyLong(after.Masks.Roles));
        Add("grantableRoles",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableRoles),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableRoles));
        Add("rolesAtHQ",before is null ? PyNone.Instance : new PyLong(before.Masks.AtHq),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtHq));
        Add("grantableRolesAtHQ",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableHq),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableHq));
        Add("rolesAtBase",before is null ? PyNone.Instance : new PyLong(before.Masks.AtBase),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtBase));
        Add("grantableRolesAtBase",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableBase),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableBase));
        Add("rolesAtOther",before is null ? PyNone.Instance : new PyLong(before.Masks.AtOther),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtOther));
        Add("grantableRolesAtOther",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableOther),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableOther));
        return new PyDict(changes);
    }

    private static void Send(ClientSession session,string name,string idType,PyValue payload)
    {
        try { session.SendNotification(name,idType,payload); }
        catch (Exception) { }
    }
}
