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

    public static void CorporationChanged(CharacterSessions? sessions,ServiceCall call,long corporationId,
        string name,PyValue before,PyValue after)
    {
        if (sessions is null || before.Equals(after)) return;
        var change = Py.Dict((name,new PyList([before,after])));
        var payload = Py.Tuple(CorporationWire.Id(corporationId),change);
        call.Session.AfterResponse(() =>
        {
            foreach (var session in sessions.OfCorporation(corporationId))
                Send(session,"OnCorporationChanged","ownerid",payload);
        });
    }

    public static void CorporationChanged(CharacterSessions? sessions,long corporationId,string name,
        PyValue before,PyValue after)
    {
        if (sessions is null || before.Equals(after)) return;
        var payload = Py.Tuple(CorporationWire.Id(corporationId),
            Py.Dict((name,new PyList([before,after]))));
        foreach (var session in sessions.OfCorporation(corporationId))
            Send(session,"OnCorporationChanged","ownerid",payload);
    }

      public static void WarChanged(CharacterSessions? sessions,ServiceCall call,long declaredById,long againstId,PyValue war)
      {
          if (sessions is null || war is not PyObject { Args: PyDict fields }) return;
          var owners = new PyList([.. new[] { declaredById,againstId }.Distinct().Order().Select(id => (PyValue)new PyInt(id))]);
          var changes = new PyDict(fields.Entries.Select(entry =>
              new KeyValuePair<PyValue,PyValue>(entry.Key,new PyList([PyNone.Instance,entry.Value]))).ToArray());
          var payload = new PyList([war,owners,changes]);
          call.Session.AfterResponse(() =>
          {
              foreach (var session in sessions.All())
                  Send(session,"OnWarChanged","ownerid",payload);
          });
      }

    public static void WarChangedBackground(
        CharacterSessions? sessions, long declaredById, long againstId, PyValue war)
    {
        if (sessions is null || war is not PyObject { Args: PyDict fields }) return;
        var owners = new PyList([.. new[] { declaredById, againstId }.Distinct().Order()
            .Select(id => (PyValue)new PyInt(id))]);
        var changes = new PyDict(fields.Entries.Select(entry =>
            new KeyValuePair<PyValue, PyValue>(entry.Key, new PyList([PyNone.Instance, entry.Value]))).ToArray());
        var payload = new PyList([war, owners, changes]);
        foreach (var session in sessions.All())
            Send(session, "OnWarChanged", "ownerid", payload);
    }

    public static void RecruitmentAdChanged(CharacterSessions? sessions,ServiceCall call)
    {
        if (sessions is null) return;
        call.Session.AfterResponse(() =>
        {
            foreach (var session in sessions.All())
                Send(session,"OnCorporationRecruitmentAdChanged","ownerid",Py.Tuple());
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

    private static void Send(ClientSession session,string name,string idType,params PyValue[] payload)
    {
        try { session.SendNotification(name,idType,payload); }
        catch (Exception) { }
    }

    public static void AllianceMemberChanged(CharacterSessions? sessions,ServiceCall call,long allianceId,
        long corporationId,Eve.Corporations.AllianceMember? before,Eve.Corporations.AllianceMember? after)
    {
        if (sessions is null) return;
        call.Session.AfterResponse(() =>
        {
            var targets = sessions.All()
                .Where(session => session.GetInt("allianceid") == allianceId ||
                    session.GetInt("corpid") == corporationId)
                .ToHashSet();
            targets.Add(call.Session);
            foreach (var session in targets)
            {
                var change = AllianceMemberChange(before,after,session.GetInt("corpid") == corporationId);
                if (change.Entries.Count > 0)
                    Send(session,"OnAllianceMemberChanged","ownerid",
                        CorporationWire.Id(allianceId),CorporationWire.Id(corporationId),change);
            }
        });
    }

    public static void AllianceMemberChanged(CharacterSessions? sessions,long allianceId,long corporationId,
        Eve.Corporations.AllianceMember? before,Eve.Corporations.AllianceMember? after)
    {
        if (sessions is null) return;
        foreach (var session in sessions.All()
                     .Where(session => session.GetInt("allianceid") == allianceId ||
                         session.GetInt("corpid") == corporationId))
        {
            var change = AllianceMemberChange(before,after,session.GetInt("corpid") == corporationId);
            if (change.Entries.Count > 0)
                Send(session,"OnAllianceMemberChanged","ownerid",
                    CorporationWire.Id(allianceId),CorporationWire.Id(corporationId),change);
        }
    }

    public static void AllianceChanged(CharacterSessions? sessions,ServiceCall call,long allianceId,
        IReadOnlyList<(string Key,PyValue Value)>? before,IReadOnlyList<(string Key,PyValue Value)>? after)
    {
        if (sessions is null) return;
        var change = AllianceChange(before,after);
        if (change.Entries.Count == 0) return;
        var payload = Py.Tuple(CorporationWire.Id(allianceId),change);
        call.Session.AfterResponse(() =>
        {
            var targets = sessions.All().ToHashSet();
            targets.Add(call.Session);
            foreach (var session in targets)
                Send(session,"OnAllianceChanged","ownerid",payload);
        });
    }

    public static void AllianceChanged(CharacterSessions? sessions,long allianceId,
        IReadOnlyList<(string Key,PyValue Value)>? before,IReadOnlyList<(string Key,PyValue Value)>? after)
    {
        if (sessions is null) return;
        var change = AllianceChange(before,after);
        if (change.Entries.Count == 0) return;
        var payload = Py.Tuple(CorporationWire.Id(allianceId),change);
        foreach (var session in sessions.All())
            Send(session,"OnAllianceChanged","ownerid",payload);
    }

    public static void AllianceApplicationChanged(CharacterSessions? sessions,ServiceCall call,long allianceId,
        long corporationId,Eve.Corporations.AllianceApplication? before,Eve.Corporations.AllianceApplication? after,
        IEnumerable<long> recipientCharacterIds)
    {
        if (sessions is null) return;
        var change = AllianceApplicationChange(before,after);
        if (change.Entries.Count == 0) return;
        var recipients = recipientCharacterIds.Distinct().ToArray();
        var payload = Py.Tuple(CorporationWire.Id(allianceId),CorporationWire.Id(corporationId),change);
        call.Session.AfterResponse(() =>
        {
            var targets = new HashSet<ClientSession>();
            foreach (var recipientId in recipients)
                if (sessions.Of(recipientId) is { } session)
                    targets.Add(session);
            if (call.Session.CharacterId is { } actor && recipients.Contains(actor))
                targets.Add(call.Session);
            foreach (var session in targets)
                Send(session,"OnAllianceApplicationChanged","ownerid",payload);
        });
    }

    public static async Task<IReadOnlyList<long>> AllianceApplicationRecipientCharacterIdsAsync(
        Eve.Corporations.CorporationStore store,long allianceId,long applicantCorporationId,
        CancellationToken token = default)
    {
        var recipients = new HashSet<long>();
        await AddDirectors(applicantCorporationId);
        if (await store.AllianceAsync(allianceId,token) is { Deleted:false,ActiveExecutorId: { } executorId })
            await AddDirectors(executorId);
        return [.. recipients];

        async Task AddDirectors(long corporationId)
        {
            var corporation = await store.GetAsync(corporationId,token);
            if (corporation is null || corporation.IsNpc || corporation.Deleted) return;
            var titles = await store.TitlesAsync(corporationId,token);
            foreach (var member in await store.MembersAsync(corporationId,token))
                if ((Eve.Corporations.CorporationRoles.Session(corporation,member,null,titles).AtAll &
                    Eve.Corporations.CorporationRoles.Director) != 0)
                    recipients.Add(member.CharacterId);
        }
    }

    public static void AllianceRelationshipChanged(CharacterSessions? sessions,ServiceCall call,long allianceId,
        long toId,double? before,double? after,IEnumerable<long> memberCorporations)
    {
        if (sessions is null) return;
        var changes = new List<KeyValuePair<PyValue,PyValue>>();
        void Add(string name,PyValue oldValue,PyValue newValue)
        {
            if (!oldValue.Equals(newValue))
                changes.Add(new(new PyStr(name),new PyList([oldValue,newValue])));
        }
        Add("toID",before is null ? PyNone.Instance : new PyInt(toId),after is null ? PyNone.Instance : new PyInt(toId));
        Add("relationship",before is { } old ? Py.Number(old) : PyNone.Instance,
            after is { } current ? Py.Number(current) : PyNone.Instance);
        if (changes.Count == 0) return;
        var payload = Py.Tuple(CorporationWire.Id(allianceId),CorporationWire.Id(toId),new PyDict(changes));
        call.Session.AfterResponse(() =>
        {
            var targets = new HashSet<ClientSession>();
            foreach (var corporationId in memberCorporations.Distinct())
                foreach (var session in sessions.OfCorporation(corporationId))
                    targets.Add(session);
            targets.Add(call.Session);
            foreach (var session in targets)
                Send(session,"OnAllianceRelationshipChanged","ownerid",payload);
        });
    }

    private static PyDict AllianceApplicationChange(Eve.Corporations.AllianceApplication? before,
        Eve.Corporations.AllianceApplication? after)
    {
        var changes = new List<KeyValuePair<PyValue,PyValue>>();
        void Add(string name,PyValue oldValue,PyValue newValue)
        {
            if (!oldValue.Equals(newValue))
                changes.Add(new(new PyStr(name),new PyList([oldValue,newValue])));
        }
        Add("allianceID",CorporationWire.Id(before?.AllianceId),CorporationWire.Id(after?.AllianceId));
        Add("corporationID",CorporationWire.Id(before?.CorporationId),CorporationWire.Id(after?.CorporationId));
        Add("applicationText",before is null ? PyNone.Instance : new PyWString(before.Text),
            after is null ? PyNone.Instance : new PyWString(after.Text));
        Add("state",before is null ? PyNone.Instance : new PyInt(before.Status),
            after is null ? PyNone.Instance : new PyInt(after.Status));
        Add("applicationDateTime",before is null ? PyNone.Instance : CorporationWire.Time(before.CreatedMs),
            after is null ? PyNone.Instance : CorporationWire.Time(after.CreatedMs));
        return new PyDict(changes);
    }

    private static PyDict AllianceMemberChange(Eve.Corporations.AllianceMember? before,
        Eve.Corporations.AllianceMember? after,bool revealSupport)
    {
        var changes = new List<KeyValuePair<PyValue,PyValue>>();
        void Add(string name,PyValue oldValue,PyValue newValue)
        {
            if (before is null || after is null || !oldValue.Equals(newValue))
                changes.Add(new(new PyStr(name),new PyList([oldValue,newValue])));
        }
        Add("corporationID",CorporationWire.Id(before?.CorporationId),CorporationWire.Id(after?.CorporationId));
        Add("allianceID",CorporationWire.Id(before?.AllianceId),CorporationWire.Id(after?.AllianceId));
        Add("chosenExecutorID",revealSupport ? CorporationWire.Id(before?.ChosenExecutorId) : PyNone.Instance,
            revealSupport ? CorporationWire.Id(after?.ChosenExecutorId) : PyNone.Instance);
        Add("startDate",before is null ? PyNone.Instance : CorporationWire.Time(before.StartedMs),
            after is null ? PyNone.Instance : CorporationWire.Time(after.StartedMs));
        Add("deleted",before is null ? PyNone.Instance : new PyInt(0),
            after is null ? PyNone.Instance : new PyInt(0));
        return new PyDict(changes);
    }

    private static PyDict AllianceChange(IReadOnlyList<(string Key,PyValue Value)>? before,
        IReadOnlyList<(string Key,PyValue Value)>? after)
    {
        var changes = new List<KeyValuePair<PyValue,PyValue>>();
        var fields = after ?? before ?? [];
        PyValue Value(IReadOnlyList<(string Key,PyValue Value)>? values,string key)
        {
            if (values is not null)
                foreach (var field in values)
                    if (field.Key == key) return field.Value;
            return PyNone.Instance;
        }
        foreach (var field in fields)
        {
            var oldValue = Value(before,field.Key);
            var newValue = Value(after,field.Key);
            if (!oldValue.Equals(newValue))
                changes.Add(new(new PyStr(field.Key),new PyList([oldValue,newValue])));
        }
        return new PyDict(changes);
    }
}
