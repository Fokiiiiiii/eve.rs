using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

public sealed record CorpWarDeclarationRequest(long CorporationId,long DeclaredByCharacterId,long AgainstId,long? WarHQId,
    long TimeDeclaredFiletime,long TimeStartedFiletime,long DueFiletime,int BillTypeId,decimal BillAmount,long CreditorId,
    string OperationKey);
public sealed record CorpWarDeclarationResult(long WarId,long BillId);

[Service("corpRegistry")]
public sealed partial class CorpRegistryService(CorporationStore store,PgCharacterStore characters,Func<long> now,CharacterSessions? characterSessions = null,Eve.Inventory.PgItemStore? items = null,Func<long,bool,int,long?,CancellationToken,ValueTask<IReadOnlyList<PyValue>>>? readRecentCorporationKillmails = null,Func<long,CancellationToken,ValueTask<long>>? readCharacterAllyBaseCost = null,Func<CorpWarDeclarationRequest,CancellationToken,ValueTask<CorpWarDeclarationResult>>? declareWar = null)
{
    private const int MaxBulkMemberIds = 500;
    private const int MaxBulkCorporationActions = 500;
    private const int MaxCorporationQueryCriteria = 100;
    private const int MaxCorporationQueryTextLength = 256;

    public AllianceAffiliationMutationCallback? AllianceAffiliationWriter
    {
        get => store.AllianceAffiliationWriter;
        set => store.AllianceAffiliationWriter = value;
    }

    private async Task<Corporation?> Current(ServiceCall call) =>
        call.Session.CharacterId is { } id && await characters.GetAsync(id,call.Cancellation) is { } c
            ? await store.GetAsync(c.CorporationId,call.Cancellation) : null;
    [Method("GetCorporation")]
    public async ValueTask<PyValue> GetCorporation(ServiceCall call) => await Current(call) is { } c
        ? CorporationWire.Row(c,(await store.MembersAsync(c.Id,call.Cancellation)).Count) : PyNone.Instance;

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyTuple { Items.Count: > 0 } tuple => Number(tuple.Items[0]),
        _ => 0
    };

    private static MachoWrappedException CorporationAccessDenied() =>
        MachoErrors.ThrowUserError("CrpAccessDenied",
            ("reason",Py.List(new PyInt(101),new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));

    [Method("ApplyToJoinAlliance")]
    public async ValueTask<PyValue> ApplyToJoinAlliance(ServiceCall call)
    {
        var actor = call.Session.CharacterId ?? 0;
        if (actor <= 0 ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc:false } corporation ||
            corporation.CeoId != actor || corporation.CeoRolesSuspended)
            throw CorporationAccessDenied();

        var alliance = Number(call.Arg(0));
        var previousApplication = (await store.AllianceApplicationsAsync(alliance,call.Cancellation))
            .LastOrDefault(item => item.CorporationId == corporation.Id && !item.Archived);
        var applicationText = JsSemantics.StringValue(call.Arg(1)) ?? "";
        long applicationId;
        try
        {
            applicationId = await store.ApplyToAllianceAsync(actor,alliance,applicationText,true,true,call.Cancellation);
        }
        catch (CorporationError error) when (error.Code is "CORPORATION_ACCESS_DENIED" or "CHARACTER_NOT_FOUND" or "ALLIANCE_NOT_FOUND" or "CORPORATION_ALREADY_IN_ALLIANCE" or "ALLIANCE_APPLICATION_TEXT_TOO_LONG")
        {
            throw CorporationAccessDenied();
        }
        catch (CorporationError error) when (error.Code == "ALLIANCE_APPLICATION_ALREADY_EXISTS")
        {
            throw MachoErrors.ThrowUserError("CantApplyTwiceToAlliance");
        }
        var application = (await store.AllianceApplicationsAsync(alliance,call.Cancellation))
            .FirstOrDefault(item => item.Id == applicationId);
        if (application is not null)
        {
            var recipients = await CorporationNotificationAdapter.AllianceApplicationRecipientCharacterIdsAsync(
                store,alliance,corporation.Id,call.Cancellation);
            CorporationNotificationAdapter.AllianceApplicationChanged(
                characterSessions,call,alliance,corporation.Id,previousApplication,application,recipients);
        }
        return PyNone.Instance;
    }

    [Method("DeleteAllianceApplication")]
    public async ValueTask<PyValue> DeleteAllianceApplication(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { } corporation)
            throw CorporationAccessDenied();

        var alliance = Number(call.Arg(0));
        var before = (await store.AllianceApplicationsAsync(alliance,call.Cancellation))
            .LastOrDefault(application => application.CorporationId == corporation.Id);
        bool withdrawn;
        try
        {
            withdrawn = await store.WithdrawAllianceApplicationAsync(actor,alliance,call.Cancellation);
        }
        catch (CorporationError error) when (error.Code is "CORPORATION_ACCESS_DENIED" or "CORPORATION_NOT_FOUND" or "CHARACTER_NOT_FOUND" or "ALLIANCE_APPLICATION_INVALID_STATE")
        {
            throw CorporationAccessDenied();
        }
        if (withdrawn && before is not null)
        {
            var recipients = await CorporationNotificationAdapter.AllianceApplicationRecipientCharacterIdsAsync(
                store,alliance,corporation.Id,call.Cancellation);
            CorporationNotificationAdapter.AllianceApplicationChanged(
                characterSessions,call,alliance,corporation.Id,before,null,recipients);
        }
        return PyNone.Instance;
    }
    [Method("GetMember")]
    public async ValueTask<PyValue> GetMember(ServiceCall call) => call.Arg(0) is PyInt id &&
        await Current(call) is { } c && await store.MemberAsync(id.Value,call.Cancellation) is { } m && m.CorporationId == c.Id
            ? CorporationWire.Member(m,(await characters.GetAsync(m.CharacterId,call.Cancellation))!.Name) : PyNone.Instance;

    [Method("MoveCompanyShares")]
    public ValueTask<PyValue> MoveCompanyShares(ServiceCall call) => MoveShareholding(call,true);

    [Method("MovePrivateShares")]
    public ValueTask<PyValue> MovePrivateShares(ServiceCall call) => MoveShareholding(call,false);

    private async ValueTask<PyValue> MoveShareholding(ServiceCall call,bool companyShares)
    {
        var corporationId = Number(call.Arg(0));
        var toShareholderId = Number(call.Arg(1));
        var numberOfShares = Number(call.Arg(2));
        var actorId = call.Session.CharacterId ?? 0;
        if (corporationId <= 0 || toShareholderId <= 0 || actorId <= 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (numberOfShares <= 0)
            throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");

        var callId = Number(call.CallId);
        var operationKey = callId > 0
            ? $"corporation-share:{corporationId}:{actorId}:{callId}"
            : Guid.NewGuid().ToString("N");
        if (companyShares)
            await store.MoveCompanySharesAsync(corporationId,actorId,toShareholderId,numberOfShares,operationKey,call.Cancellation);
        else
            await store.MovePrivateSharesAsync(corporationId,actorId,toShareholderId,numberOfShares,operationKey,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetShareholders")]
    public async ValueTask<PyValue> GetShareholders(ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        if (corporationId <= 0 ||
            await Current(call) is not { IsNpc:false,Deleted:false } corporation ||
            corporation.Id != corporationId ||
            !await CanAdminister(call))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var holdings = await store.ShareholdersAsync(corporationId,call.Cancellation);
        return CorporationWire.ShareholdersRowset(holdings.Select(holding =>
            (holding.ShareholderId,holding.CorporationId,holding.Shares)));
    }

    [Method("GetSharesByShareholder")]
    public async ValueTask<PyValue> GetSharesByShareholder(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is null)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var ownerId = actor;
        long? corporationFilter = null;
        if (call.Args.Count > 0 && Number(call.Arg(0)) == 1)
        {
            if (await Current(call) is not { IsNpc:false,Deleted:false } corporation ||
                !await CanAdminister(call))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            ownerId = corporation.Id;
            corporationFilter = corporation.Id;
        }

        var holdings = await store.SharesByShareholderAsync(ownerId,corporationFilter,call.Cancellation);
        return CorporationWire.ShareholderCRowset(holdings.Select(holding =>
            (holding.ShareholderId,holding.CorporationId,holding.Shares)));
    }

    [Method("GetEveOwners")]
    public async ValueTask<PyValue> GetEveOwners(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null)
            return Py.List([]);

        var rows = new List<PyValue>();
        foreach (var member in await store.MembersAsync(corporation.Id,call.Cancellation))
        {
            var character = await characters.GetAsync(member.CharacterId,call.Cancellation);
            var ownerName = string.IsNullOrEmpty(character?.Name) ? $"Character {member.CharacterId}" : character.Name;
            rows.Add(new PyObject(new PyStr("util.Row"),Py.Dict(
                ("header",Py.Tuple([
                    new PyStr("ownerID"),new PyStr("ownerName"),new PyStr("typeID"),
                    new PyStr("gender"),new PyStr("ownerNameID")
                ])),
                ("line",Py.Tuple([
                    new PyInt(member.CharacterId),new PyWString(ownerName),
                    new PyInt(character is { TypeId: > 0 } ? character.TypeId : 1373),
                    new PyInt(character?.Gender ?? 0),PyNone.Instance
                ])))));
        }
        return Py.List([.. rows]);
    }

    [Method("ExecuteActions")]
    public async ValueTask<PyValue> ExecuteActions(ServiceCall call)
    {
        const long ctvAdd = 1;
        const long ctvRemove = 2;
        const long ctvSet = 3;
        const long ctvGive = 4;

        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var targetValues = Items(call.Arg(0));
        if (targetValues.Count > MaxBulkMemberIds)
            throw new CorporationError("CORPORATION_BULK_LIMIT_EXCEEDED");
        var targetIDs = new long[targetValues.Count];
        var distinctTargets = new HashSet<long>();
        for (var index = 0; index < targetValues.Count; index++)
        {
            var target = Number(targetValues[index]);
            if (target <= 0 || !distinctTargets.Add(target))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            targetIDs[index] = target;
        }

        var actionValues = Items(call.Arg(1));
        if (actionValues.Count > MaxBulkCorporationActions)
            throw new CorporationError("CORPORATION_BULK_LIMIT_EXCEEDED");
        var actions = new List<(long Verb,PyValue Property,PyValue Value)>();
        foreach (var rawAction in actionValues)
        {
            var values = Items(rawAction);
            if (values.Count < 3)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            actions.Add((Number(values[0]),values[1],values[2]));
        }

        var hasGive = actions.Any(action => action.Verb == ctvGive);
        if (hasGive && actions.Any(action => action.Verb != ctvGive))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var members = await store.MembersAsync(corp.Id,call.Cancellation);
        var membersById = members.ToDictionary(member => member.CharacterId);
        if (targetIDs.Any(target => !membersById.ContainsKey(target)))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (actions.Count == 0)
            return PyNone.Instance;

        if (hasGive)
        {
            var grants = new List<Eve.Corporations.CorporationMemberGrant>(actions.Count);
            foreach (var action in actions)
            {
                var property = Number(action.Property);
                if (action.Verb != ctvGive || property != 6 ||
                    action.Value is not PyInt and not PyLong and not PyReal)
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
                if (action.Value is PyReal real && !double.IsFinite(real.Value))
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");

                decimal amount;
                try { amount = DecimalValue(action.Value); }
                catch (OverflowException) { throw new CorporationError("CORPORATION_ACCESS_DENIED"); }
                if (amount <= 0m)
                    throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
                grants.Add(new Eve.Corporations.CorporationMemberGrant((int)property,amount));
            }

            if (grants.Count == 0)
                throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
            var walletAccountKey = CorporationWalletKey(new PyInt(
                call.Session.GetInt("corpAccountKey") ?? call.Session.GetInt("corpaccountkey") ?? 1000));
            var callId = Number(call.CallId);
            var operationKey = callId > 0
                ? $"corporation-share:{corp.Id}:{actor}:{callId}"
                : Guid.NewGuid().ToString("N");
            await store.ExecuteMemberGrantsAsync(corp.Id,actor,targetIDs,grants,walletAccountKey,operationKey,call.Cancellation);
            return PyNone.Instance;
        }

        if (targetIDs.Length == 0)
            return PyNone.Instance;

        var updates = new List<Member>();
        var beforeById = new Dictionary<long,Member>();
        foreach (var target in targetIDs)
        {
            var member = membersById[target];
            var row = new List<PyValue>
            {
                new PyInt(target),PyNone.Instance,PyNone.Instance,PyNone.Instance,
                new PyLong(member.Masks.Roles),new PyLong(member.Masks.GrantableRoles),
                new PyLong(member.Masks.AtHq),new PyLong(member.Masks.GrantableHq),
                new PyLong(member.Masks.AtBase),new PyLong(member.Masks.GrantableBase),
                new PyLong(member.Masks.AtOther),new PyLong(member.Masks.GrantableOther),
                CorporationWire.Id(member.BaseId),new PyInt(member.TitleMask),PyNone.Instance
            };

            foreach (var action in actions)
            {
                var (index,allowed) = Text(action.Property) switch
                {
                    "roles" => (4,CorporationRoles.Administrative),
                    "grantableRoles" => (5,CorporationRoles.Grantable),
                    "rolesAtHQ" => (6,CorporationRoles.Locational),
                    "grantableRolesAtHQ" => (7,CorporationRoles.Locational),
                    "rolesAtBase" => (8,CorporationRoles.Locational),
                    "grantableRolesAtBase" => (9,CorporationRoles.Locational),
                    "rolesAtOther" => (10,CorporationRoles.Locational),
                    "grantableRolesAtOther" => (11,CorporationRoles.Locational),
                    _ => (-1,0L)
                };
                if (index >= 0)
                {
                    var current = RoleMask(row[index],allowed);
                    var value = action.Value is PyNone ? 0 : RoleMask(action.Value,allowed);
                    row[index] = new PyLong(action.Verb switch
                    {
                        ctvAdd => current | value,
                        ctvRemove => current & ~value,
                        ctvSet => value,
                        _ => throw new CorporationError("CORPORATION_ACCESS_DENIED")
                    });
                }
                else if (Text(action.Property) == "titleMask")
                {
                    var current = RoleMask(row[13],65535);
                    var value = action.Value is PyNone ? 0 : RoleMask(action.Value,65535);
                    row[13] = new PyInt((int)(action.Verb switch
                    {
                        ctvAdd => current | value,
                        ctvRemove => current & ~value,
                        ctvSet => value,
                        _ => throw new CorporationError("CORPORATION_ACCESS_DENIED")
                    }));
                }
                else if (Text(action.Property) == "baseID" && action.Verb == ctvSet)
                    row[12] = new PyInt(PositiveId(action.Value) ?? 0);
                else
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
            }

            var (before,after) = await MemberUpdate(target,row,true,corp.Id,call.Cancellation);
            beforeById.Add(target,before);
            updates.Add(after);
        }

        var changed = await store.UpdateMembersAsync(actor,updates,call.Cancellation);
        foreach (var member in changed)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,member.CharacterId,beforeById[member.CharacterId],member);
        if (updates.Any(member => member.CharacterId == actor))
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetNumberOfPotentialCEOs")]
    public async ValueTask<PyValue> GetNumberOfPotentialCEOs(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null)
            return Py.List([]);
        var members = await store.MembersAsync(corporation.Id,call.Cancellation);
        return Py.List([.. members.Select(member => (PyValue)new PyInt(member.CharacterId))]);
    }
    [Method("GetMembersByIds")]
    public async ValueTask<PyValue> GetMembersByIds(ServiceCall call)
    {
        var memberIds = Items(call.Arg(0));
        if (memberIds.Count > MaxBulkMemberIds)
            throw new CorporationError("CORPORATION_BULK_LIMIT_EXCEEDED");

        var rows = new List<PyValue>();
        foreach (var id in memberIds)
        {
            var row = await GetMember(new ServiceCall { Method="GetMember",Args=[id],Session=call.Session,Cancellation=call.Cancellation });
            if (row is not PyNone) rows.Add(row);
        }
        return new PyList(rows);
    }

    [Method("GetMembersPaged")]
    public async ValueTask<PyValue> GetMembersPaged(ServiceCall call)
    {
        var members = await Current(call) is { } c ? await store.MembersAsync(c.Id,call.Cancellation) : [];
        var page = call.Arg(0) is PyInt n ? Math.Max(1,n.Value) : 1;
        const int perPage = 50;
        var pageOffset = page - 1;
        var skip = pageOffset > members.Count / perPage
            ? members.Count
            : (int)(pageOffset * perPage);
        var rows = new List<PyValue>();
        foreach (var m in members.Skip(skip).Take(perPage))
            rows.Add(CorporationWire.Member(m,(await characters.GetAsync(m.CharacterId,call.Cancellation))!.Name));
        return new PyObjectEx(ObjectExKind.Ex1,Py.Tuple(new PyToken("eve.common.script.util.pagedCollection.PagedResultSet"),
            Py.Tuple(new PyList(rows),new PyInt(members.Count),new PyInt(page-1),new PyInt(perPage))),[],[]);
    }

    [Method("GetMemberIDsByQuery")]
    public async ValueTask<PyValue> GetMemberIDsByQuery(ServiceCall call)
    {
        if (await Current(call) is not { } corporation)
            return new PyList([]);

        var rawCriteria = Items(call.Arg(0));
        if (rawCriteria.Count > MaxCorporationQueryCriteria)
            throw new CorporationError("CORPORATION_QUERY_LIMIT_EXCEEDED");
        var criteria = new List<(long? Join,string Property,long Operator,PyValue Value)>();
        foreach (var raw in rawCriteria)
        {
            var values = raw is PyPackedRow row ? row.Values : Items(raw);
            if (values.Count < 3)
                continue;
            var offset = values.Count >= 4 ? 1 : 0;
            var operation = Number(values[offset+1]);
            var value = values[offset+2];
            if (operation is >= 9 and <= 13 && Text(value).Length > MaxCorporationQueryTextLength)
                throw new CorporationError("CORPORATION_QUERY_LIMIT_EXCEEDED");
            criteria.Add((offset == 0 || values[0] is PyNone ? null : Number(values[0]),
                Text(values[offset]),operation,value));
        }

        var members = await store.MembersAsync(corporation.Id,call.Cancellation);
        var includeImplied = Truth(call.Arg(1));
        var searchTitles = Truth(call.Arg(2));
        var titles = includeImplied || searchTitles
            ? await store.TitlesAsync(corporation.Id,call.Cancellation)
            : [];
        var charactersById = (await characters.GetManyAsync(
            members.Select(member => member.CharacterId),call.Cancellation)).ToDictionary(character => character.Id);
        var results = new List<PyValue>();

        foreach (var member in members)
        {
            if (!charactersById.TryGetValue(member.CharacterId,out var character))
                continue;

            bool? matched = null;
            foreach (var criterion in criteria)
            {
                var next = Matches(member,character,titles,criterion.Property,criterion.Operator,criterion.Value,
                    includeImplied,searchTitles);
                if (matched is null || criterion.Join is null)
                    matched = next;
                else if (criterion.Join == 2)
                    matched = matched.GetValueOrDefault() && next;
                else
                    matched = matched.GetValueOrDefault() || next;
            }

            if (matched ?? true)
                results.Add(CorporationWire.Id(member.CharacterId));
        }

        return new PyList(results);

        static bool Matches(Member member,Character character,IReadOnlyList<CorporationTitle> titles,string property,
            long op,PyValue rawValue,bool includeImplied,bool searchTitles)
        {
            if (op is >= 9 and <= 13)
            {
                var expected = Text(rawValue).ToLowerInvariant();
                if (expected.Length == 0)
                    return true;
                return StringCandidates(member,character,titles,property,includeImplied,searchTitles)
                    .Any(candidate => StringMatch(candidate,expected,op));
            }

            var left = NumericValue(member,character,titles,property,includeImplied);
            var right = Number(rawValue);
            return op switch
            {
                1 => left == right,
                2 => left > right,
                3 => left >= right,
                4 => left < right,
                5 => left <= right,
                6 => left != right,
                7 => right == 0 || (left & right) == right,
                8 => right != 0 && (left & right) == 0,
                _ => false
            };
        }

        static IEnumerable<string> StringCandidates(Member member,Character character,IReadOnlyList<CorporationTitle> titles,
            string property,bool includeImplied,bool searchTitles)
        {
            if (property == "characterID")
            {
                yield return character.Name;
                if (searchTitles)
                {
                    yield return member.Title;
                    foreach (var title in AssignedTitles(member,titles))
                        yield return title.Name;
                }
                yield break;
            }
            if (property == "title")
            {
                yield return member.Title;
                yield break;
            }

            if (property is "corporationID" or "baseID" or "divisionID" or "squadronID" or "titleMask" or
                "startDateTime" or "locationID" or "factionID" or "roles" or "grantableRoles" or
                "rolesAtHQ" or "grantableRolesAtHQ" or "rolesAtBase" or "grantableRolesAtBase" or
                "rolesAtOther" or "grantableRolesAtOther" or "accountKey" or "blockRoles")
                yield return NumericValue(member,character,titles,property,includeImplied)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static long NumericValue(Member member,Character character,IReadOnlyList<CorporationTitle> titles,string property,
            bool includeImplied)
        {
            long roles = property switch
            {
                "roles" => member.Masks.Roles,
                "grantableRoles" => member.Masks.GrantableRoles,
                "rolesAtHQ" => member.Masks.AtHq,
                "grantableRolesAtHQ" => member.Masks.GrantableHq,
                "rolesAtBase" => member.Masks.AtBase,
                "grantableRolesAtBase" => member.Masks.GrantableBase,
                "rolesAtOther" => member.Masks.AtOther,
                "grantableRolesAtOther" => member.Masks.GrantableOther,
                _ => 0
            };
            if (includeImplied && property is ("roles" or "grantableRoles" or "rolesAtHQ" or "grantableRolesAtHQ" or
                "rolesAtBase" or "grantableRolesAtBase" or "rolesAtOther" or "grantableRolesAtOther"))
                foreach (var title in AssignedTitles(member,titles))
                    roles |= property switch
                    {
                        "roles" => title.Masks.Roles,
                        "grantableRoles" => title.Masks.GrantableRoles,
                        "rolesAtHQ" => title.Masks.AtHq,
                        "grantableRolesAtHQ" => title.Masks.GrantableHq,
                        "rolesAtBase" => title.Masks.AtBase,
                        "grantableRolesAtBase" => title.Masks.GrantableBase,
                        "rolesAtOther" => title.Masks.AtOther,
                        _ => title.Masks.GrantableOther
                    };

            return property switch
            {
                "characterID" => member.CharacterId,
                "corporationID" => member.CorporationId,
                "baseID" => member.BaseId ?? 0,
                "divisionID" => member.DivisionId,
                "squadronID" => member.SquadronId,
                "titleMask" => member.TitleMask,
                "startDateTime" => Number(CorporationWire.Time(member.StartedMs)),
                "locationID" => (long)character.SolarSystemId,
                "factionID" => character.FactionId ?? 0,
                "accountKey" => member.AccountKey,
                "blockRoles" => member.BlockRoles ? 1 : 0,
                "roles" or "grantableRoles" or "rolesAtHQ" or "grantableRolesAtHQ" or
                    "rolesAtBase" or "grantableRolesAtBase" or "rolesAtOther" or "grantableRolesAtOther" => roles,
                _ => 0
            };
        }

        static IEnumerable<CorporationTitle> AssignedTitles(Member member,IReadOnlyList<CorporationTitle> titles) =>
            titles.Where(title => title.Id > 0 && (title.Id & (title.Id-1)) == 0 &&
                (member.TitleMask & title.Id) == title.Id);

        static bool StringMatch(string candidate,string expected,long op)
        {
            var left = candidate.ToLowerInvariant();
            return op switch
            {
                9 => left.Contains(expected,StringComparison.Ordinal),
                10 => WildcardMatch(left,expected),
                11 => left.StartsWith(expected,StringComparison.Ordinal),
                12 => left.EndsWith(expected,StringComparison.Ordinal),
                13 => left == expected,
                _ => false
            };
        }

        static bool WildcardMatch(string value,string pattern)
        {
            var valueIndex = 0;
            var patternIndex = 0;
            var starIndex = -1;
            var retryIndex = 0;
            while (valueIndex < value.Length)
            {
                if (patternIndex < pattern.Length &&
                    (pattern[patternIndex] == '?' || pattern[patternIndex] == value[valueIndex]))
                {
                    valueIndex++;
                    patternIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] is '*' or '%')
                {
                    starIndex = patternIndex++;
                    retryIndex = valueIndex;
                }
                else if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    valueIndex = ++retryIndex;
                }
                else
                    return false;
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] is '*' or '%')
                patternIndex++;
            return patternIndex == pattern.Length;
        }
    }

    [Method("GetMemberIDsWithMoreThanAvgShares")]
    public async ValueTask<PyValue> GetMemberIDsWithMoreThanAvgShares(ServiceCall call)
    {
        if (await Current(call) is not { } corporation)
            return new PyList([]);
        var members = await store.MembersAsync(corporation.Id,call.Cancellation);
        if (members.Count == 0)
            return new PyList([]);
        var holdings = await store.ShareholdersAsync(corporation.Id,call.Cancellation);
        var corporationShares = holdings
            .Where(holding => holding.ShareholderId == corporation.Id)
            .Select(holding => holding.Shares)
            .FirstOrDefault();
        var sharesByMember = holdings.ToDictionary(holding => holding.ShareholderId,holding => holding.Shares);
        var average = (decimal)corporationShares / members.Count;
        return new PyList(members
            .Where(member => sharesByMember.GetValueOrDefault(member.CharacterId) > average)
            .Select(member => (PyValue)CorporationWire.Id(member.CharacterId))
            .ToArray());
    }

    [Method("GetInfoWindowDataForChar")]
    public async ValueTask<PyValue> GetInfoWindowDataForChar(ServiceCall call)
    {
        var characterId = Number(call.Arg(0));
        if (characterId <= 0)
            characterId = call.Session.CharacterId ?? 0;
        if (characterId <= 0 || await characters.GetAsync(characterId,call.Cancellation) is not { } character)
            return PyNone.Instance;

        var member = await store.MemberAsync(characterId,call.Cancellation);
        var corporation = await store.GetAsync(character.CorporationId,call.Cancellation);
        var titles = corporation is null
            ? []
            : await store.TitlesAsync(corporation.Id,call.Cancellation);
        var fields = new List<(string Name,PyValue Value)>
        {
            ("corpID",CorporationWire.Id(character.CorporationId)),
            ("allianceID",CorporationWire.Id(character.AllianceId)),
            ("factionID",CorporationWire.Id(character.FactionId)),
            ("title",new PyWString(member?.Title ?? ""))
        };
        for (var index = 0; index < 16; index++)
        {
            var titleId = 1 << index;
            var titleName = (member?.TitleMask & titleId) != 0
                ? titles.FirstOrDefault(title => title.Id == titleId)?.Name
                : null;
            fields.Add(($"title{index+1}",titleName is null ? PyValue.None : new PyWString(titleName)));
        }
        return Py.KeyVal(fields.ToArray());
    }

    [Method("GetSuggestedTickerNames")]
    public ValueTask<PyValue> GetSuggestedTickerNames(ServiceCall call)
    {
        var ticker = new char[4];
        for (var index = 0; index < ticker.Length; index++)
            ticker[index] = (char)('A' + System.Security.Cryptography.RandomNumberGenerator.GetInt32(26));
        return ValueTask.FromResult<PyValue>(new PyList([
            Py.KeyVal([("tickerName",(PyValue)new PyStr(new string(ticker)))])
        ]));
    }

    [Method("DoesMyCorpAcceptStructures")]
    public async ValueTask<PyValue> DoesMyCorpAcceptStructures(ServiceCall call) =>
        await Current(call) is { } corporation && SettingBoolean(corporation,"acceptStructures")
            ? new PyInt(1) : new PyInt(0);

    [Method("RegisterNewAcceptStructureSettings")]
    public async ValueTask<PyValue> RegisterNewAcceptStructureSettings(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corporation ||
            !await CanAdminister(call))
            throw CorporationAccessDenied();
        var before = SettingBoolean(corporation,"acceptStructures");
        var after = Truth(call.Arg(0));
        await store.UpdateSettingsAsync(actor,corporation.Id,
            System.Text.Json.JsonSerializer.Serialize(new { acceptStructures = after }),call.Cancellation);
        CorporationNotificationAdapter.CorporationChanged(characterSessions,call,corporation.Id,
            "acceptStructures",new PyInt(before ? 1 : 0),new PyInt(after ? 1 : 0));
        return PyNone.Instance;
    }

    [Method("DoesCorpRestrictCorpMails")]
    public async ValueTask<PyValue> DoesCorpRestrictCorpMails(ServiceCall call) =>
        await Current(call) is { } corporation && SettingBoolean(corporation,"restrictCorpMails")
            ? new PyInt(1) : new PyInt(0);

    [Method("RegisterNewCorpMailRestrictionSettings")]
    public async ValueTask<PyValue> RegisterNewCorpMailRestrictionSettings(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corporation ||
            !await CanAdminister(call))
            throw CorporationAccessDenied();
        var before = SettingBoolean(corporation,"restrictCorpMails");
        var after = Truth(call.Arg(0));
        await store.UpdateSettingsAsync(actor,corporation.Id,
            System.Text.Json.JsonSerializer.Serialize(new { restrictCorpMails = after }),call.Cancellation);
        CorporationNotificationAdapter.CorporationChanged(characterSessions,call,corporation.Id,
            "restrictCorpMails",new PyInt(before ? 1 : 0),new PyInt(after ? 1 : 0));
        return PyNone.Instance;
    }

    [Method("GetStructureReinforceDefault")]
    public async ValueTask<PyValue> GetStructureReinforceDefault(ServiceCall call)
    {
        var hour = await Current(call) is { } corporation
            ? SettingInteger(corporation,"structureReinforceDefault",20) : 20;
        return Py.Tuple(new PyInt(255),new PyInt(hour));
    }

    [Method("SetStructureReinforceDefault")]
    public async ValueTask<PyValue> SetStructureReinforceDefault(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corporation ||
            !await CanAdminister(call))
            throw CorporationAccessDenied();
        var hour = Number(call.Arg(0));
        if (hour is < 0 or > 23)
            throw new CorporationError("CORPORATION_INVALID_REINFORCE_HOUR");
        var before = SettingInteger(corporation,"structureReinforceDefault",20);
        await store.UpdateSettingsAsync(actor,corporation.Id,
            System.Text.Json.JsonSerializer.Serialize(new { structureReinforceDefault = hour }),call.Cancellation);
        CorporationNotificationAdapter.CorporationChanged(characterSessions,call,corporation.Id,
            "structureReinforceDefault",new PyInt(before),new PyInt(hour));
        return PyNone.Instance;
    }

    private static bool SettingBoolean(Corporation corporation,string name) =>
        corporation.Settings.ValueKind == System.Text.Json.JsonValueKind.Object &&
        corporation.Settings.TryGetProperty(name,out var value) &&
        (value.ValueKind == System.Text.Json.JsonValueKind.True ||
            value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0);

    private static int SettingInteger(Corporation corporation,string name,int fallback) =>
        corporation.Settings.ValueKind == System.Text.Json.JsonValueKind.Object &&
        corporation.Settings.TryGetProperty(name,out var value) && value.TryGetInt32(out var result)
            ? result : fallback;

    [Method("GetMemberTrackingInfoSimple")]
    public async ValueTask<PyValue> GetMemberTrackingInfoSimple(ServiceCall call)
    {
        if (await Current(call) is not { } corporation)
            return new PyList([]);
        var rows = await MemberTrackingRows(call,corporation);
        return new PyList(rows.Select(row => (PyValue)new PyList([
            row[0],row[1],row[3],row[4],row[2],row[5],row[6],row[10],row[9],row[11],row[18],row[7],row[8]
        ])).ToArray());
    }

    [Method("GetMemberTrackingInfo")]
    public async ValueTask<PyValue> GetMemberTrackingInfo(ServiceCall call)
    {
        if (await Current(call) is not { } corporation ||
            call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.MemberAsync(actor,call.Cancellation) is not { } member)
            throw CorporationAccessDenied();

        var titles = await store.TitlesAsync(corporation.Id,call.Cancellation);
        var roles = CorporationRoles.Session(corporation,member,character.StationId,titles).CorpRole;
        if ((roles & CorporationRoles.Director) == 0)
            throw CorporationAccessDenied();
        return CorporationWire.MemberTrackingCRowset(await MemberTrackingRows(call,corporation));
    }

    private async Task<IReadOnlyList<PyValue[]>> MemberTrackingRows(ServiceCall call,Corporation corporation)
    {
        var members = await store.MembersAsync(corporation.Id,call.Cancellation);
        var charactersById = (await characters.GetManyAsync(
            members.Select(member => member.CharacterId),call.Cancellation)).ToDictionary(character => character.Id);
        var nowFileTime = Number(CorporationWire.Time(now()));
        const long fileTimeTicksPerHour = 36_000_000_000;
        var rows = new List<PyValue[]>(members.Count);
        foreach (var member in members)
        {
            if (!charactersById.TryGetValue(member.CharacterId,out var character))
                continue;
            var online = characterSessions?.Of(member.CharacterId) is not null;
            var lastOnline = PyValue.None;
            if (online)
                lastOnline = new PyInt(-1);
            else if (character.LastLogoffAt is { } lastLogoff)
                lastOnline = new PyInt(Math.Max(0,(nowFileTime-lastLogoff.ToFileTime())/fileTimeTicksPerHour));
            PyValue shipTypeId = PyValue.None;
            if (items is not null && character.ShipId is { } shipId &&
                await items.GetAsync(shipId,call.Cancellation) is { } ship)
                shipTypeId = new PyInt(ship.TypeId);

            rows.Add([
                CorporationWire.Id(member.CharacterId),CorporationWire.Id(member.CorporationId),
                new PyWString(member.Title),new PyLong(member.Masks.Roles),new PyLong(member.Masks.GrantableRoles),
                CorporationWire.Id(member.BaseId),CorporationWire.Time(member.StartedMs),
                online ? CorporationWire.Time(now()) : PyValue.None,PyValue.None,lastOnline,
                character.SolarSystemId > 0 ? new PyLong(character.SolarSystemId) : PyValue.None,shipTypeId,
                new PyLong(member.Masks.AtHq),new PyLong(member.Masks.GrantableHq),
                new PyLong(member.Masks.AtBase),new PyLong(member.Masks.GrantableBase),
                new PyLong(member.Masks.AtOther),new PyLong(member.Masks.GrantableOther),
                CorporationWire.Id(character.DirectFactionId ?? character.FactionId)
            ]);
        }
        return rows;
    }
    [Method("GetTitles")]
    public async ValueTask<PyValue> GetTitles(ServiceCall call) =>
        CorporationWire.Titles(await Current(call) is { } c ? await store.TitlesAsync(c.Id,call.Cancellation) : []);
    [Method("GetLocationalRoles")]
    public PyValue GetLocationalRoles(ServiceCall call) => new PyList([.. Enumerable.Range(0,63)
        .Select(bit => 1L << bit).Where(role => (CorporationRoles.Locational & role) != 0).Select(role => (PyValue)new PyLong(role))]);
    [Method("GetMyGrantableRoles")]
    public async ValueTask<PyValue> GetMyGrantableRoles(ServiceCall call)
    {
        var masks = await Current(call) is { } c && call.Session.CharacterId is { } id && await store.MemberAsync(id,call.Cancellation) is { } m
            ? CorporationRoles.GrantableFor(c,m,c.Id,await store.TitlesAsync(c.Id,call.Cancellation)) : new RoleMasks();
        return Py.Tuple(new PyLong(masks.GrantableRoles),new PyLong(masks.GrantableHq),new PyLong(masks.GrantableBase),new PyLong(masks.GrantableOther));
    }

    private static readonly Descriptor ApplicationDescriptor = CorporationWire.DescriptorOf(
        ("applicationID",3),("corporationID",3),("characterID",3),("applicationText",130),
        ("status",3),("applicationDateTime",64),("deleted",11),("responseText",130));
    private static PyValue ApplicationRow(Application application) => new PyPackedRow(ApplicationDescriptor,
    [
        new PyInt(application.Id),new PyInt(application.CorporationId),new PyInt(application.CharacterId),
        new PyWString(application.Text),new PyInt(application.Status),CorporationWire.Time(application.CreatedMs),
        PyBool.False,application.Status == 0 ? PyNone.Instance : new PyWString(application.Response),
    ]);
    private static PyValue GroupApplications(IReadOnlyList<Application> applications,Func<Application,long> key) =>
        new PyDict([.. applications.GroupBy(key).Select(group => new KeyValuePair<PyValue,PyValue>(
            new PyInt(group.Key),new PyList([.. group.Select(ApplicationRow)])))]);
    private static long? PositiveId(PyValue value) => value switch
    {
        PyInt id when id.Value > 0 => id.Value,
        PyLong id when id.Value > 0 => id.Value,
        _ => null,
    };
    private static string Text(PyValue value) => JsSemantics.StringValue(value) ?? "";
    private static int Status(PyValue value) => value switch
    {
        PyInt n when n.Value is >= int.MinValue and <= int.MaxValue => (int)n.Value,
        PyLong n when n.Value is >= int.MinValue and <= int.MaxValue => (int)n.Value,
        _ => 0,
    };
    private async Task<Corporation> RecruitmentCorporation(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null || corporation.Deleted || corporation.IsNpc ||
            call.Session.CharacterId is not { } actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var member = await store.MemberAsync(actor,call.Cancellation);
        if (member is null || member.CorporationId != corporation.Id ||
            (CorporationRoles.Session(corporation,member,null,
                await store.TitlesAsync(corporation.Id,call.Cancellation)).AtAll & CorporationRoles.PersonnelManager) == 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return corporation;
    }

    [Method("GetCorpWelcomeMail")]
    public async ValueTask<PyValue> GetCorpWelcomeMail(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null || corporation.Deleted || call.Session.CharacterId is not { } actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var member = await store.MemberAsync(actor,call.Cancellation);
        if (member is null || member.CorporationId != corporation.Id)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return new PyWString(await store.GetCorpWelcomeMailAsync(corporation.Id,call.Cancellation));
    }

    [Method("SetCorpWelcomeMail")]
    public async ValueTask<PyValue> SetCorpWelcomeMail(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corporation)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var welcomeMail = Text(call.Arg(0));
        if (welcomeMail.Length > 10_000)
            throw new CorporationError("CORPORATION_WELCOME_MAIL_LIMIT_EXCEEDED");
        await store.SetCorpWelcomeMailAsync(actor,corporation.Id,welcomeMail,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetMyApplications")]
    public async ValueTask<PyValue> GetMyApplications(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character) return new PyDict([]);
        return GroupApplications(await store.ApplicationsAsync(character:character,token:call.Cancellation),a => a.CorporationId);
    }
    [Method("GetMyOldApplications")]
    public async ValueTask<PyValue> GetMyOldApplications(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character) return new PyList([]);
        return new PyList([.. (await store.ApplicationsAsync(character:character,archived:true,token:call.Cancellation)).Select(ApplicationRow)]);
    }
    [Method("GetApplications")]
    public async ValueTask<PyValue> GetApplications(ServiceCall call)
    {
        var corporation = await RecruitmentCorporation(call);
        return GroupApplications(await store.ApplicationsAsync(corporation:corporation.Id,token:call.Cancellation),a => a.CharacterId);
    }
    [Method("GetOldApplications")]
    public async ValueTask<PyValue> GetOldApplications(ServiceCall call)
    {
        var corporation = await RecruitmentCorporation(call);
        return new PyList([.. (await store.ApplicationsAsync(corporation:corporation.Id,archived:true,token:call.Cancellation)).Select(ApplicationRow)]);
    }
    [Method("GetMyOpenInvitations")]
    public async ValueTask<PyValue> GetMyOpenInvitations(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character) return new PyList([]);
        var applications = await store.ApplicationsAsync(character:character,token:call.Cancellation);
        return new PyList([.. applications.Where(a => a.Status is 6 or 8).Select(a => (PyValue)Py.KeyVal(
            ("invitationID",new PyInt(a.Id)), // EVE.js exposes applicationID in this field.
            ("corporationID",new PyInt(a.CorporationId)),
            ("characterID",new PyInt(a.CharacterId)),("status",new PyInt(a.Status)),
            ("invitationDateTime",CorporationWire.Time(a.CreatedMs))))]);
    }
    [Method("InsertApplication")]
    public async ValueTask<PyValue> InsertApplication(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character || PositiveId(call.Arg(0)) is not { } corporation)
            return PyNone.Instance;
        var id = await store.ApplyAsync(character,corporation,Text(call.Arg(1)),token:call.Cancellation);
        return new PyInt(id);
    }
    [Method("InsertInvitation")]
    public async ValueTask<PyValue> InsertInvitation(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null || call.Session.CharacterId is not { } inviter)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (PositiveId(call.Arg(0)) is not { } character)
            throw new CorporationError("CHARACTER_NOT_FOUND");
        var invitation = await store.CreateInvitationAsync(character,corporation.Id,inviter,call.Cancellation);
        return new PyInt(invitation.InvitationId);
    }
    [Method("UpdateApplicationOffer")]
    public async ValueTask<PyValue> UpdateApplicationOffer(ServiceCall call)
    {
        if (PositiveId(call.Arg(0)) is not { } applicationId || PositiveId(call.Arg(2)) is not { } corporation)
            return PyNone.Instance;
        var application = (await store.ApplicationsAsync(corporation:corporation,token:call.Cancellation))
            .FirstOrDefault(a => a.Id == applicationId);
        if (application is null) return PyNone.Instance;
        if (PositiveId(call.Arg(1)) is { } character && character != application.CharacterId ||
            call.Session.CharacterId is not { } actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var recipients = new HashSet<long> { application.CharacterId, actor };
        if (await store.GetAsync(corporation,call.Cancellation) is { } corp) recipients.Add(corp.CeoId);
        foreach (var member in await store.MembersAsync(corporation,call.Cancellation))
            if ((member.Masks.Roles & (CorporationRoles.Director | CorporationRoles.PersonnelManager)) != 0)
                recipients.Add(member.CharacterId);
        var before = await store.MemberAsync(application.CharacterId,call.Cancellation);
        var status = Status(call.Arg(4));
        var updated = await store.RespondAsync(actor,applicationId,status,Text(call.Arg(3)),Text(call.Arg(5)),call.Cancellation);
        CorporationNotificationAdapter.ApplicationChanged(characterSessions,call,corporation,updated.CharacterId,
            updated.Id,ApplicationRow(updated),recipients);
        Member? after = status == 2
            ? new Member(application.CharacterId,corporation,now(),"",new RoleMasks(),null,0,false,1000)
            : before;
        if (before != after)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corporation,application.CharacterId,before,after);
        if (status == 2)
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }
    private static IReadOnlyList<PyValue> Items(PyValue v) => v switch { PyTuple t => t.Items,PyList l => l.Items,_ => [] };

    private static long[] CorporationBulkIds(PyValue value)
    {
        var requested = Items(value);
        if (requested.Count > MaxBulkCorporationActions)
            throw new CorporationError("CORPORATION_BULK_LIMIT_EXCEEDED");
        var ids = requested.Select(Number).ToArray();
        if (ids.Any(id => id <= 0))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return ids.Distinct().ToArray();
    }
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = Py.Tuple(new PyStr($"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}"),CorporationWire.Time(now()));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count:>0 } nested)
            result = await ((IService)this).CallAsync(new ServiceCall { Method=JsSemantics.StringValue(nested.Items[0]) ?? "",Args=nested.Items.Count>1 ? Items(nested.Items[1]) : [],
                Kwargs=nested.Items.Count>2 ? nested.Items[2] : PyNone.Instance,Session=call.Session,CallId=call.CallId,Cancellation=call.Cancellation });
        return Py.Tuple(new PySubStruct(new PySubStream(oid)),result);
    }

    [Method("AddCorporation")]
    public async ValueTask<PyValue> AddCorporation(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } founder) return PyNone.Instance;
        var settings = new Dictionary<string,object?>
        {
            ["shape1"] = SettingValue(call.Arg(5)),["shape2"] = SettingValue(call.Arg(6)),["shape3"] = SettingValue(call.Arg(7)),
            ["color1"] = SettingValue(call.Arg(8)),["color2"] = SettingValue(call.Arg(9)),["color3"] = SettingValue(call.Arg(10)),
            ["typeface"] = SettingValue(call.Arg(11)),
        };
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(settings));
        var created = await store.CreateAsync(founder,new NewCorporation(Text(call.Arg(0)),Text(call.Arg(1)),Text(call.Arg(2)),Text(call.Arg(3)),
            DecimalValue(call.Arg(4)),DecimalValue(call.Arg(14)),Truth(call.Arg(12)),Truth(call.Arg(13)),document.RootElement.Clone()),
            $"corp-found:{founder}:{call.Session.SessionId}:{Number(call.CallId)}",call.Cancellation);
        await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,founder,call.Cancellation);
        var members = await store.MembersAsync(created.Id,call.Cancellation);
        if (members.FirstOrDefault(m => m.CharacterId == founder) is { } member)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,created.Id,founder,null,member);
        return CorporationWire.Row(created,members.Count);
    }

    [Method("UpdateCorporation")]
    public async ValueTask<PyValue> UpdateCorporation(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.UpdateCorporationAsync(actor,corp.Id,Text(call.Arg(0)),Text(call.Arg(1)),DecimalValue(call.Arg(2)),
            DecimalValue(call.Arg(4)),Truth(call.Arg(3)),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateLogo")]
    public async ValueTask<PyValue> UpdateLogo(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var patch = new Dictionary<string,long?>
        {
            ["shape1"] = PositiveId(call.Arg(0)),["shape2"] = PositiveId(call.Arg(1)),["shape3"] = PositiveId(call.Arg(2)),
            ["color1"] = PositiveId(call.Arg(3)),["color2"] = PositiveId(call.Arg(4)),["color3"] = PositiveId(call.Arg(5)),
            ["typeface"] = PositiveId(call.Arg(6))
        };
        await store.UpdateSettingsAsync(actor,corp.Id,System.Text.Json.JsonSerializer.Serialize(patch),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateDivisionNames")]
    public async ValueTask<PyValue> UpdateDivisionNames(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var names = new Dictionary<string,string>();
        for (var i = 0; i < 14; i++)
            if (call.Arg(i) is not PyNone) names[(i+1).ToString(System.Globalization.CultureInfo.InvariantCulture)] = Text(call.Arg(i));
        await store.UpdateDivisionNamesAsync(actor,corp.Id,System.Text.Json.JsonSerializer.Serialize(names),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateCorporationAbilities")]
    public async ValueTask<PyValue> UpdateCorporationAbilities(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.UpdateSettingsAsync(actor,corp.Id,
            System.Text.Json.JsonSerializer.Serialize(new { applicationsEnabled = NormalizeBoolean(call.Arg(0),true) }),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("SetAccountKey")]
    public async ValueTask<PyValue> SetAccountKey(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character) return PyBool.True;
        var key = CorporationWalletKey(call.Arg(0));
        var before = await store.MemberAsync(character,call.Cancellation);
        await store.SetAccountKeyAsync(character,key,call.Cancellation);
        if (before is not null)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,before.CorporationId,character,before,before with { AccountKey=key });
        call.Session.Set("corpaccountkey",new PyInt(key));
        await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,character,call.Cancellation);
        return PyBool.True;
    }

    [Method("CanLeaveCurrentCorporation")]
    public async ValueTask<PyValue> CanLeaveCurrentCorporation(ServiceCall call)
    {
        var corp = await Current(call);
        var actor = call.Session.CharacterId;
        var member = actor is { } id ? await store.MemberAsync(id,call.Cancellation) : null;
        if (corp is null || actor is null || member is null || member.CorporationId != corp.Id)
            return Py.Tuple(new PyInt(0),new PyStr("CrpAccessDenied"),new PyDict([]));
        if (corp.CeoId == actor)
            return Py.Tuple(new PyInt(0),new PyStr("CrpCEOCanNotQuit"),new PyDict([]));
        var roles = RoleFields(member);
        var active = roles.Where(r => r.Value != 0).Select(r => (PyValue)new PyStr(r.Name)).ToList();
        if (member.TitleMask != 0) active.Add(new PyStr("titleMask"));
        if (active.Count != 0)
            return Py.Tuple(new PyInt(0),new PyStr("CrpCantQuitNotInStasis"),Py.Dict(("rolelist",new PyList(active))));
        return Py.Tuple(new PyInt(1),PyNone.Instance,new PyDict([]));
    }

    [Method("CanBeKickedOut")]
    public async ValueTask<PyValue> CanBeKickedOut(ServiceCall call)
    {
        var corp = await Current(call);
        var target = PositiveId(call.Arg(0));
        if (corp is null || target is null || await store.MemberAsync(target.Value,call.Cancellation) is not { } member ||
            member.CorporationId != corp.Id || corp.CeoId == target || !Kickable(member))
            return new PyInt(0);
        return new PyInt(1);
    }

    [Method("KickOutMember")]
    public async ValueTask<PyValue> KickOutMember(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp || PositiveId(call.Arg(0)) is not { } target)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (target == actor)
        {
            if (!AssertCanLeave(await CanLeaveCurrentCorporation(call))) return PyNone.Instance;
            var character = await characters.GetAsync(actor,call.Cancellation) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var destination = await store.GetAsync(StarterCorporation(character),call.Cancellation) ?? throw new CorporationError("NPC_CORPORATION_REQUIRED");
            var before = await store.MemberAsync(actor,call.Cancellation);
            await store.LeaveAsync(actor,destination.Id,call.Cancellation);
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,actor,before,null);
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
            return MoveResult(actor,destination);
        }
        if (await store.MemberAsync(target,call.Cancellation) is not { } member || member.CorporationId != corp.Id || !Kickable(member))
            return PyNone.Instance;
        var kicked = await characters.GetAsync(target,call.Cancellation) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
        var fallback = await store.GetAsync(StarterCorporation(kicked),call.Cancellation) ?? throw new CorporationError("NPC_CORPORATION_REQUIRED");
        await store.KickAsync(actor,target,fallback.Id,call.Cancellation);
        CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,target,member,null);
        return MoveResult(target,fallback);
    }

    [Method("KickOutMembers")]
    public async ValueTask<PyValue> KickOutMembers(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || !await CanAdminister(call))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var requested = Items(call.Arg(0));
        if (requested.Count > MaxBulkMemberIds)
            throw new CorporationError("CORPORATION_BULK_LIMIT_EXCEEDED");
        var kicked = new List<PyValue>();
        var notKicked = new List<PyValue>();
        foreach (var raw in requested)
        {
            if (PositiveId(raw) is not { } target || target == actor ||
                await CanBeKickedOut(new ServiceCall { Method="CanBeKickedOut",Args=[new PyInt(target)],Session=call.Session,Cancellation=call.Cancellation }) is not PyInt { Value: 1 })
            {
                if (PositiveId(raw) is { } skipped) notKicked.Add(new PyInt(skipped));
                continue;
            }
            var character = await characters.GetAsync(target,call.Cancellation);
            if (character is null)
            {
                notKicked.Add(new PyInt(target));
                continue;
            }
            var before = await store.MemberAsync(target,call.Cancellation);
            if (before is null)
            {
                notKicked.Add(new PyInt(target));
                continue;
            }
            var corporationId = before.CorporationId;
            await store.KickAsync(actor,target,StarterCorporation(character),call.Cancellation);
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corporationId,target,before,null);
            kicked.Add(new PyInt(target));
        }
        return Py.Dict(("kicked",new PyList(kicked)),("notKicked",new PyList(notKicked)));
    }

    private static PyValue MoveResult(long character,Corporation corporation) => Py.Dict(
        ("success",PyBool.True),
        ("data",Py.Dict(("characterID",new PyInt(character)),("corporationID",new PyInt(corporation.Id)),
            ("allianceID",corporation.AllianceId is { } alliance ? new PyInt(alliance) : PyNone.Instance))));

    [Method("ResignFromCEO")]
    public async ValueTask<PyValue> ResignFromCEO(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await Current(call) is not { CeoId: var ceo,CeoRolesSuspended:false } corp || ceo != actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (PositiveId(call.Arg(0)) is { } successor)
        {
            var actorBefore = await store.MemberAsync(actor,call.Cancellation);
            var successorBefore = await store.MemberAsync(successor,call.Cancellation);
            await store.ResignAsync(actor,successor,call.Cancellation);
            var actorAfter = actorBefore is null ? null : actorBefore with
            {
                Masks = actorBefore.Masks with { Roles=actorBefore.Masks.Roles & ~CorporationRoles.Director }
            };
            var successorAfter = successorBefore is null ? null : successorBefore with
            {
                Masks = successorBefore.Masks with { Roles=successorBefore.Masks.Roles | CorporationRoles.Director }
            };
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,actor,actorBefore,actorAfter);
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,successor,successorBefore,successorAfter);
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
            return PyNone.Instance;
        }
        var members = await store.MembersAsync(corp.Id,call.Cancellation);
        if (members.Count <= 1) return PyNone.Instance;
        return new PyList([.. members.Where(m => m.CharacterId != actor).Select(m => (PyValue)new PyInt(m.CharacterId))]);
    }

    [Method("UpdateMember")]
    public async ValueTask<PyValue> UpdateMember(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp ||
            PositiveId(call.Arg(0)) is not { } target)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var (before,desired) = await MemberUpdate(target,call.Args,false,corp.Id,call.Cancellation);
        var after = await store.UpdateMemberAsync(actor,desired,call.Cancellation);
        CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,target,before,after);
        if (target == actor) await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateMembers")]
    public async ValueTask<PyValue> UpdateMembers(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var requested = Items(call.Arg(0));
        if (requested.Count > MaxBulkMemberIds)
            throw new CorporationError("CORPORATION_BULK_LIMIT_EXCEEDED");
        List<IReadOnlyList<PyValue>> memberRows = [];
        HashSet<long> targetIds = [];
        foreach (var raw in requested)
        {
            var row = Items(raw);
            if (row.Count == 0 || PositiveId(row[0]) is not { } target || !targetIds.Add(target))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            memberRows.Add(row);
        }
        var updates = new List<Member>();
        var beforeById = new Dictionary<long,Member>();
        foreach (var row in memberRows)
        {
            var target = PositiveId(row[0])!.Value;
            var (before,after) = await MemberUpdate(target,row,true,corp.Id,call.Cancellation);
            beforeById.Add(target,before);
            updates.Add(after);
        }
        var changed = await store.UpdateMembersAsync(actor,updates,call.Cancellation);
        foreach (var member in changed)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,member.CharacterId,beforeById[member.CharacterId],member);
        if (updates.Any(m => m.CharacterId == actor))
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateTitle")]
    public async ValueTask<PyValue> UpdateTitle(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var title = Title(call.Args);
        var before = (await store.TitlesAsync(corp.Id,call.Cancellation)).FirstOrDefault(t => t.Id == title.Id);
        var updated = title with { Name=title.Name.Length > 100 ? title.Name[..100] : title.Name };
        await store.UpdateTitleAsync(actor,corp.Id,updated,call.Cancellation);
        CorporationNotificationAdapter.TitleChanged(characterSessions,call,corp.Id,before,updated);
        return PyNone.Instance;
    }

    [Method("UpdateTitles")]
    public async ValueTask<PyValue> UpdateTitles(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var requested = Items(call.Arg(0));
        if (requested.Count > MaxBulkCorporationActions)
            throw new CorporationError("CORPORATION_BULK_LIMIT_EXCEEDED");
        var titles = requested.Select(row => Title(Items(row)))
            .Select(title => title with { Name=title.Name.Length > 100 ? title.Name[..100] : title.Name }).ToArray();
        var before = await store.TitlesAsync(corp.Id,call.Cancellation);
        await store.UpdateTitlesAsync(actor,corp.Id,titles,call.Cancellation);
        foreach (var title in titles)
            CorporationNotificationAdapter.TitleChanged(characterSessions,call,corp.Id,
                before.FirstOrDefault(t => t.Id == title.Id),title);
        return PyNone.Instance;
    }

    [Method("DeleteTitle")]
    public async ValueTask<PyValue> DeleteTitle(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var titleId = Number(call.Arg(0));
        if (titleId is < 1 or > 32768) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var before = (await store.TitlesAsync(corp.Id,call.Cancellation)).FirstOrDefault(t => t.Id == (int)titleId);
        await store.DeleteTitleAsync(actor,corp.Id,(int)titleId,call.Cancellation);
        if (before is not null)
            CorporationNotificationAdapter.TitleChanged(characterSessions,call,corp.Id,before,null);
        return PyNone.Instance;
    }

    private static bool AssertCanLeave(PyValue value) =>
        value is PyTuple { Items.Count: > 0 } tuple && tuple.Items[0] is PyInt { Value: 1 };

    private bool Kickable(Member member) =>
        RoleFields(member).All(r => r.Value == 0) && member.TitleMask == 0 &&
        (member.StasisUntilMs is null || member.StasisUntilMs <= now());

    private static (string Name,long Value)[] RoleFields(Member m) =>
    [
        ("roles",m.Masks.Roles),("grantableRoles",m.Masks.GrantableRoles),
        ("rolesAtHQ",m.Masks.AtHq),("grantableRolesAtHQ",m.Masks.GrantableHq),
        ("rolesAtBase",m.Masks.AtBase),("grantableRolesAtBase",m.Masks.GrantableBase),
        ("rolesAtOther",m.Masks.AtOther),("grantableRolesAtOther",m.Masks.GrantableOther),
    ];

    private async Task<bool> CanAdminister(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc: false,Deleted: false } corp ||
            await store.MemberAsync(actor,call.Cancellation) is not { } member || member.CorporationId != corp.Id)
            return false;
        if (corp.CeoId == actor && !corp.CeoRolesSuspended) return true;
        return (CorporationRoles.Session(corp,member,character.StationId,await store.TitlesAsync(corp.Id,call.Cancellation)).AtAll & CorporationRoles.Director) != 0;
    }

    private static long StarterCorporation(Eve.Characters.Character character) =>
        Eve.Characters.CreationCatalog.ResolveSchool(character.SchoolId,character.RaceId)?.CorporationId ?? 1000004;

    private async Task<(Member Before,Member After)> MemberUpdate(long character,IReadOnlyList<PyValue> values,bool bulk,long corporation,CancellationToken token)
    {
        var old = await store.MemberAsync(character,token);
        if (old is null || old.CorporationId != corporation) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        PyValue At(int index) => index < values.Count ? values[index] : PyNone.Instance;
        long OptionalMask(int index,long current,long allowed) => At(index) is PyNone ? current : RoleMask(At(index),allowed);
        var masks = new RoleMasks(OptionalMask(4,old.Masks.Roles,CorporationRoles.Administrative),
            OptionalMask(5,old.Masks.GrantableRoles,CorporationRoles.Grantable),
            OptionalMask(6,old.Masks.AtHq,CorporationRoles.Locational),OptionalMask(7,old.Masks.GrantableHq,CorporationRoles.Locational),
            OptionalMask(8,old.Masks.AtBase,CorporationRoles.Locational),OptionalMask(9,old.Masks.GrantableBase,CorporationRoles.Locational),
            OptionalMask(10,old.Masks.AtOther,CorporationRoles.Locational),OptionalMask(11,old.Masks.GrantableOther,CorporationRoles.Locational));
        var titleMask = At(13) is PyNone ? old.TitleMask : (int)RoleMask(At(13),65535);
        var blockRoles = At(14) is PyNone ? old.BlockRoles : BlockRoles(At(14));
        var baseId = At(12) is PyNone ? old.BaseId : PositiveId(At(12));
        var after = old with
        {
            Title = bulk || At(1) is PyNone ? old.Title : Text(At(1))[..Math.Min(100,Text(At(1)).Length)],
            DivisionId = bulk || At(2) is PyNone ? old.DivisionId : (int)Number(At(2)),
            SquadronId = bulk || At(3) is PyNone ? old.SquadronId : (int)Number(At(3)),
            Masks = masks,TitleMask = titleMask,BlockRoles = blockRoles,BaseId = baseId
        };
        return (old,after);
    }

    private static CorporationTitle Title(IReadOnlyList<PyValue> values)
    {
        if (values.Count == 0 || PositiveId(values[0]) is not { } id || id > 32768 || (id & (id-1)) != 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        long At(int index,long allowed) => index < values.Count ? RoleMask(values[index],allowed) : throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return new CorporationTitle((int)id,values.Count > 1 ? Text(values[1]) : "",new RoleMasks(
            At(2,CorporationRoles.Grantable),At(3,CorporationRoles.Grantable),
            At(4,CorporationRoles.Locational),At(5,CorporationRoles.Locational),
            At(6,CorporationRoles.Locational),At(7,CorporationRoles.Locational),
            At(8,CorporationRoles.Locational),At(9,CorporationRoles.Locational)));
    }

    private static long RoleMask(PyValue value,long allowed)
    {
        var mask = value switch { PyInt n => n.Value,PyLong n => n.Value,_ => throw new CorporationError("CORPORATION_ACCESS_DENIED") };
        if (!CorporationRoles.IsKnown(mask,allowed)) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return mask;
    }

    private static int CorporationWalletKey(PyValue value)
    {
        var number = Number(value);
        if (number is >= 1000 and <= 1006) return (int)number;
        var text = Text(value).Trim().ToLowerInvariant();
        return text switch
        {
            "cash2" => 1001,"cash3" => 1002,"cash4" => 1003,"cash5" => 1004,
            "cash6" => 1005,"cash7" => 1006,_ => 1000
        };
    }

    private static bool BlockRoles(PyValue value) => value switch
    {
        PyBool boolean => boolean == PyBool.True,
        PyInt number when number.Value is 0 or 1 => number.Value == 1,
        PyLong number when number.Value is 0 or 1 => number.Value == 1,
        _ => throw new CorporationError("CORPORATION_ACCESS_DENIED")
    };

    private static decimal DecimalValue(PyValue value) => value switch
    {
        PyInt n => n.Value,PyLong n => n.Value,PyReal n when double.IsFinite(n.Value) => (decimal)n.Value,_ => 0m
    };

    private static bool Truth(PyValue value) => value switch
    {
        PyNone => false,PyBool boolean => boolean == PyBool.True,PyInt n => n.Value != 0,PyLong n => n.Value != 0,
        PyReal n => n.Value != 0,PyStr s => s.Value.Length != 0,PyWString => !string.IsNullOrEmpty(JsSemantics.StringValue(value)),_ => true
    };

    private static bool NormalizeBoolean(PyValue value,bool fallback)
    {
        if (value is PyNone) return fallback;
        if (value is PyStr or PyWString)
        {
            var text = JsSemantics.StringValue(value)?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text)) return fallback;
            if (text is "0" or "false") return false;
            if (text is "1" or "true") return true;
        }
        return Truth(value);
    }

    private static object? SettingValue(PyValue value) => value switch
    {
        PyInt n => n.Value,PyLong n => n.Value,PyReal n => n.Value,PyBool b => b == PyBool.True,
        PyStr s => s.Value,PyWString => JsSemantics.StringValue(value),_ => null
    };

      private async Task<Corporation> RequireCorporationSocialRole(ServiceCall call,long role)
      {
          var actor = call.Session.CharacterId ?? 0;
          if (actor <= 0 || await characters.GetAsync(actor,call.Cancellation) is not { } character ||
              await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc:false,Deleted:false } corporation ||
              await store.MemberAsync(actor,call.Cancellation) is not { } member || member.CorporationId != corporation.Id)
              throw CorporationAccessDenied();
          var roles = CorporationRoles.Session(corporation,member,character.StationId,
              await store.TitlesAsync(corporation.Id,call.Cancellation)).AtAll;
          if ((roles & (CorporationRoles.Director | role)) == 0)
              throw CorporationAccessDenied();
          return corporation;
      }

      private static PyValue CorporationMaskValue(long value) =>
          value <= int.MaxValue ? new PyInt(value) : new PyLong(value);

      [Method("GetCorporateContacts")]
      public async ValueTask<PyValue> GetCorporateContacts(ServiceCall call)
      {
          var corporation = await Current(call);
          var contacts = corporation is null ? [] : await store.CorporationContactsAsync(corporation.Id,call.Cancellation);
          return new PyDict(contacts.Select(contact => new KeyValuePair<PyValue,PyValue>(
              CorporationMaskValue(contact.ContactId),Py.KeyVal(
                  ("contactID",CorporationMaskValue(contact.ContactId)),
                  ("relationshipID",new PyInt(contact.RelationshipId)),
                  ("labelMask",CorporationMaskValue(contact.LabelMask)),
                  ("inWatchlist",new PyInt(contact.InWatchlist ? 1 : 0))))).ToArray());
      }

      [Method("GetContactList")]
      public ValueTask<PyValue> GetContactList(ServiceCall call) => GetCorporateContacts(call);

      [Method("AddCorporateContact")]
      public async ValueTask<PyValue> AddCorporateContact(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.Diplomat);
          var contactId = PositiveId(call.Arg(0));
          var relationship = Number(call.Arg(1));
          if (contactId is null || relationship is < -10 or > 10)
              return PyNone.Instance;
          await store.AddCorporationContactAsync(corporation.Id,contactId.Value,(int)relationship,call.Cancellation);
          return PyNone.Instance;
      }

      [Method("EditCorporateContact")]
      public ValueTask<PyValue> EditCorporateContact(ServiceCall call) => AddCorporateContact(call);

      [Method("RemoveCorporateContacts")]
      public async ValueTask<PyValue> RemoveCorporateContacts(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.Diplomat);
          await store.RemoveCorporationContactsAsync(corporation.Id,CorporationBulkIds(call.Arg(0)),call.Cancellation);
          return PyNone.Instance;
      }

      [Method("EditContactsRelationshipID")]
      public async ValueTask<PyValue> EditContactsRelationshipID(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.Diplomat);
          var relationship = Number(call.Arg(1));
          if (relationship is < -10 or > 10)
              return PyNone.Instance;
          await store.EditCorporationContactRelationshipsAsync(corporation.Id,
              CorporationBulkIds(call.Arg(0)),(int)relationship,call.Cancellation);
          return PyNone.Instance;
      }

      [Method("GetLabels")]
      public async ValueTask<PyValue> GetLabels(ServiceCall call)
      {
          var corporation = await Current(call);
          var labels = corporation is null ? [] : await store.CorporationContactLabelsAsync(corporation.Id,call.Cancellation);
          return new PyDict(labels.Select(label => new KeyValuePair<PyValue,PyValue>(
              CorporationMaskValue(label.LabelId),Py.KeyVal(
                  ("labelID",CorporationMaskValue(label.LabelId)),("name",new PyWString(label.Name)),("color",new PyInt(label.Color))))).ToArray());
      }

      [Method("CreateLabel")]
      public async ValueTask<PyValue> CreateLabel(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.Diplomat);
          var name = Text(call.Arg(0)).Trim();
          var color = Number(call.Arg(1));
          if (color is < 0 or > 16777215)
              throw CorporationAccessDenied();
          var label = await store.CreateCorporationContactLabelAsync(corporation.Id,name,(int)color,call.Cancellation);
          return CorporationMaskValue(label.LabelId);
      }

      [Method("DeleteLabel")]
      public async ValueTask<PyValue> DeleteLabel(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.Diplomat);
          var labelId = Number(call.Arg(0));
          if (labelId > 0)
              await store.DeleteCorporationContactLabelAsync(corporation.Id,labelId,call.Cancellation);
          return PyNone.Instance;
      }

      [Method("EditLabel")]
      public async ValueTask<PyValue> EditLabel(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.Diplomat);
          var labelId = Number(call.Arg(0));
          var name = call.Args.Count > 1 ? Text(call.Arg(1)) : null;
          var rawColor = call.Args.Count > 2 ? Number(call.Arg(2)) : (long?)null;
          if (rawColor is < 0 or > 16777215)
              throw CorporationAccessDenied();
          await store.EditCorporationContactLabelAsync(corporation.Id,labelId,name,rawColor is { } color ? (int)color : null,call.Cancellation);
          return PyNone.Instance;
      }

      [Method("AssignLabels")]
      public async ValueTask<PyValue> AssignLabels(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.Diplomat);
          await store.ChangeCorporationContactLabelsAsync(corporation.Id,CorporationBulkIds(call.Arg(0)),Number(call.Arg(1)),true,call.Cancellation);
          return PyNone.Instance;
      }

      [Method("RemoveLabels")]
      public async ValueTask<PyValue> RemoveLabels(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.Diplomat);
          await store.ChangeCorporationContactLabelsAsync(corporation.Id,CorporationBulkIds(call.Arg(0)),Number(call.Arg(1)),false,call.Cancellation);
          return PyNone.Instance;
      }

      private async ValueTask<PyValue> CorporationBulletins(ServiceCall call)
      {
          var corporation = await Current(call);
          return CorporationWire.BulletinsCRowset(corporation is null
              ? Array.Empty<CorporationBulletin>()
              : await store.CorporationBulletinsAsync(corporation.Id,call.Cancellation));
      }

      [Method("GetBulletins")]
      public ValueTask<PyValue> GetBulletins(ServiceCall call) => CorporationBulletins(call);

      [Method("GetBulletinEntries")]
      public ValueTask<PyValue> GetBulletinEntries(ServiceCall call) => CorporationBulletins(call);

      [Method("GetBulletin")]
      public async ValueTask<PyValue> GetBulletin(ServiceCall call)
      {
          var corporation = await Current(call);
          var bulletinId = PositiveId(call.Arg(0));
          var bulletin = corporation is null || bulletinId is null
              ? null
              : await store.CorporationBulletinAsync(corporation.Id,bulletinId.Value,call.Cancellation);
          return bulletin is null ? PyNone.Instance : CorporationWire.BulletinRow(bulletin);
      }

      private async ValueTask<PyValue> UpsertCorporationBulletin(ServiceCall call,long? bulletinId,PyValue title,PyValue body,PyValue editTime)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.ChatManager);
          var actor = call.Session.CharacterId!.Value;
          var createFiletime = Number(CorporationWire.Time(now()));
          var editFiletime = editTime is PyNone ? createFiletime : Number(editTime);
          if (editFiletime <= 0) editFiletime = createFiletime;
          var id = await store.AddCorporationBulletinAsync(corporation.Id,actor,Text(title),Text(body),bulletinId,
              createFiletime,editFiletime,call.Cancellation);
          return CorporationMaskValue(id);
      }

      [Method("AddBulletin")]
      public ValueTask<PyValue> AddBulletin(ServiceCall call) =>
          UpsertCorporationBulletin(call,call.Args.Count > 2 ? PositiveId(call.Arg(2)) : null,
              call.Arg(0),call.Arg(1),call.Args.Count > 3 ? call.Arg(3) : PyNone.Instance);

      [Method("UpdateBulletin")]
      public ValueTask<PyValue> UpdateBulletin(ServiceCall call) =>
          UpsertCorporationBulletin(call,PositiveId(call.Arg(0)),call.Arg(1),call.Arg(2),
              call.Args.Count > 3 ? call.Arg(3) : PyNone.Instance);

      [Method("DeleteBulletin")]
      public async ValueTask<PyValue> DeleteBulletin(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.ChatManager);
          var bulletinId = PositiveId(call.Arg(0));
          if (bulletinId is { } id)
              await store.DeleteCorporationBulletinAsync(corporation.Id,id,call.Cancellation);
          return PyNone.Instance;
      }

      [Method("UpdateBulletinOrder")]
      public async ValueTask<PyValue> UpdateBulletinOrder(ServiceCall call)
      {
          var corporation = await RequireCorporationSocialRole(call,CorporationRoles.ChatManager);
          await store.UpdateCorporationBulletinOrderAsync(corporation.Id,CorporationBulkIds(call.Arg(0)),call.Cancellation);
          return PyNone.Instance;
      }

      [Method("GetPendingAutoKicks")]
      public PyValue GetPendingAutoKicks(ServiceCall call) => new PyList([]);

      [Method("PayoutDividend")]
      public async ValueTask<PyValue> PayoutDividend(ServiceCall call)
      {
          Corporation corporation;
          try
          {
              corporation = await RequireCorporationSocialRole(call, CorporationRoles.WalletTakeRole(1000));
          }
          catch (MachoWrappedException)
          {
              return PyNone.Instance;
          }
          decimal payoutAmount;
          try
          {
              payoutAmount = decimal.Round(DecimalValue(call.Arg(1)),2,MidpointRounding.AwayFromZero);
          }
          catch (OverflowException)
          {
              return PyNone.Instance;
          }
          if (payoutAmount <= 0)
              return PyNone.Instance;

          var payShareholders = call.Args.Count == 0 || Truth(call.Arg(0));
          var recipients = new List<(Eve.Wallet.WalletAccount Account,decimal Weight)>();
          if (payShareholders)
          {
              foreach (var holding in await store.ShareholdersAsync(corporation.Id,call.Cancellation))
              {
                  if (holding.Shares <= 0)
                      continue;
                  if (await characters.GetAsync(holding.ShareholderId,call.Cancellation) is not null)
                      recipients.Add((new Eve.Wallet.WalletAccount(holding.ShareholderId),holding.Shares));
                  else if (await store.GetAsync(holding.ShareholderId,call.Cancellation) is not null)
                      recipients.Add((new Eve.Wallet.WalletAccount(holding.ShareholderId,1000),holding.Shares));
              }
          }
          else
          {
              foreach (var member in await store.MembersAsync(corporation.Id,call.Cancellation))
                  recipients.Add((new Eve.Wallet.WalletAccount(member.CharacterId),1m));
          }

          var totalWeight = recipients.Sum(recipient => recipient.Weight);
          if (totalWeight <= 0)
              return PyNone.Instance;
          var remainingAmount = payoutAmount;
          var allocations = new List<Eve.Wallet.WalletAllocation>(recipients.Count);
          for (var index = 0; index < recipients.Count; index++)
          {
              var recipient = recipients[index];
              var amount = index == recipients.Count-1
                  ? remainingAmount
                  : decimal.Round(payoutAmount*(recipient.Weight/totalWeight),2,MidpointRounding.AwayFromZero);
              if (amount > remainingAmount)
                  amount = remainingAmount;
              remainingAmount -= amount;
              if (amount > 0)
                  allocations.Add(new Eve.Wallet.WalletAllocation(recipient.Account,amount));
          }

          var actor = call.Session.CharacterId ?? 0;
          var callId = Number(call.CallId);
          var operationKey = callId > 0
              ? $"corporation-dividend:{corporation.Id}:{actor}:{callId}"
              : Guid.NewGuid().ToString("N");
          try
          {
              await store.DistributeDividendAsync(corporation.Id,allocations,operationKey,call.Cancellation);
          }
          catch (Exception error) when (error is Eve.Wallet.InsufficientFundsException or Eve.Wallet.MoneyLimitException or
              Eve.Wallet.WalletNotFoundException or Eve.Wallet.WalletOperationConflictException)
          {
              return PyNone.Instance;
          }
          return PyNone.Instance;
      }

      private async ValueTask<PyValue> RecentCorporationKillmails(ServiceCall call,bool losses)
      {
          var corporation = await Current(call);
          if (corporation is null)
              return CorporationWire.KillmailCRowset([]);
          var requestedLimit = Number(call.Arg(0));
          var limit = requestedLimit > int.MaxValue ? int.MaxValue : requestedLimit < int.MinValue ? int.MinValue : (int)requestedLimit;
          var startKillId = call.Args.Count > 1 ? PositiveId(call.Arg(1)) : null;
          var records = readRecentCorporationKillmails is null
              ? Array.Empty<PyValue>()
              : await readRecentCorporationKillmails(corporation.Id,losses,limit,startKillId,call.Cancellation);
          return CorporationWire.KillmailCRowset(records);
      }

      [Method("GetRecentKills")]
      public ValueTask<PyValue> GetRecentKills(ServiceCall call) => RecentCorporationKillmails(call,false);

      [Method("GetRecentLosses")]
      public ValueTask<PyValue> GetRecentLosses(ServiceCall call) => RecentCorporationKillmails(call,true);

      private static (long? EnableAfter,long? DisableAfter) AggressionSettings(Corporation? corporation)
      {
          long? enableAfter = corporation?.IsNpc == true ? null : 0L;
          long? disableAfter = corporation?.IsNpc == true ? 0L : null;
          if (corporation?.Settings.ValueKind != System.Text.Json.JsonValueKind.Object ||
              !corporation.Settings.TryGetProperty("aggressionSettings",out var settings) ||
              settings.ValueKind != System.Text.Json.JsonValueKind.Object)
              return (enableAfter,disableAfter);
          return (
              AggressionFiletime(settings,"enableAfter",enableAfter),
              AggressionFiletime(settings,"disableAfter",disableAfter));
      }

      private static long? AggressionFiletime(System.Text.Json.JsonElement settings,string name,long? fallback)
      {
          if (!settings.TryGetProperty(name,out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null)
              return fallback;
          if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var number))
              return number;
          if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
              long.TryParse(value.GetString(),System.Globalization.NumberStyles.Integer,
                  System.Globalization.CultureInfo.InvariantCulture,out number))
              return number;
          return fallback;
      }

      private static bool AggressionTimePassed(long? time,long nowFiletime) =>
          time is { } value && value > 0 && value <= nowFiletime;

      private static bool FriendlyFireLegalAt((long? EnableAfter,long? DisableAfter) settings,long nowFiletime)
      {
          var disablePast = AggressionTimePassed(settings.DisableAfter,nowFiletime);
          var enablePast = AggressionTimePassed(settings.EnableAfter,nowFiletime);
          if (disablePast && enablePast)
              return settings.DisableAfter < settings.EnableAfter;
          if (disablePast)
              return false;
          if (enablePast)
              return true;
          return true;
      }

      private static bool AggressionChangePending((long? EnableAfter,long? DisableAfter) settings,long nowFiletime) =>
          settings.EnableAfter is { } enable && enable > nowFiletime ||
          settings.DisableAfter is { } disable && disable > nowFiletime;

      private static PyValue AggressionSettingsPayload((long? EnableAfter,long? DisableAfter) settings) =>
          new PyObject(new PyStr("crimewatch.corp_aggression.settings.AggressionSettings"),Py.Dict(
              ("_enableAfter",settings.EnableAfter is { } enable ? new PyLong(enable) : PyNone.Instance),
              ("_disableAfter",settings.DisableAfter is { } disable ? new PyLong(disable) : PyNone.Instance)));

      [Method("GetAggressionSettings")]
      public async ValueTask<PyValue> GetAggressionSettings(ServiceCall call)
      {
          var corporation = await Current(call);
          return AggressionSettingsPayload(AggressionSettings(corporation));
      }

      [Method("RegisterNewAggressionSettings")]
      public async ValueTask<PyValue> RegisterNewAggressionSettings(ServiceCall call)
      {
          if (call.Session.CharacterId is not { } actor || !await CanAdminister(call) ||
              await Current(call) is not { IsNpc:false,Deleted:false } corporation)
              throw CorporationAccessDenied();
          var currentSettings = AggressionSettings(corporation);
          var nowFiletime = Number(CorporationWire.Time(now()));
          var desired = Truth(call.Arg(0));
          var current = FriendlyFireLegalAt(currentSettings,nowFiletime);
          var updated = current == desired && !AggressionChangePending(currentSettings,nowFiletime)
              ? currentSettings
              : desired
                  ? (nowFiletime+864_000_000_000L,0L)
                  : (0L,nowFiletime+864_000_000_000L);
          var json = System.Text.Json.JsonSerializer.Serialize(new
          {
              aggressionSettings = new
              {
                  enableAfter = updated.EnableAfter?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                  disableAfter = updated.DisableAfter?.ToString(System.Globalization.CultureInfo.InvariantCulture)
              }
          });
          await store.UpdateAggressionSettingsAsync(actor,corporation.Id,json,
              FriendlyFireLegalAt(updated,nowFiletime),call.Cancellation);
          CorporationNotificationAdapter.CorporationChanged(characterSessions,call,corporation.Id,"friendlyFire",
              new PyInt(corporation.FriendlyFire ? 1 : 0),new PyInt(FriendlyFireLegalAt(updated,nowFiletime) ? 1 : 0));
          return AggressionSettingsPayload(updated);
      }

      [Method("CharGetAllyBaseCost")]
      public async ValueTask<PyValue> CharGetAllyBaseCost(ServiceCall call)
      {
          var characterId = call.Session.CharacterId ?? (PositiveId(call.Arg(0)) ?? 0);
          if (characterId <= 0)
              return new PyInt(10_000_000);
          var cost = readCharacterAllyBaseCost is null
              ? 10_000_000L
              : await readCharacterAllyBaseCost(characterId,call.Cancellation);
          return new PyInt(Math.Max(0,cost));
      }

      [Method("DeclareWarAgainst")]
      public async ValueTask<PyValue> DeclareWarAgainst(ServiceCall call)
      {
          var corporation = await Current(call);
          var actor = call.Session.CharacterId ?? 0;
          var againstId = PositiveId(call.Arg(0));
          if (corporation is not { IsNpc:false,Deleted:false } || actor <= 0 ||
              againstId is null || againstId.Value == corporation.Id)
              return PyNone.Instance;
          if (declareWar is null)
              throw new CorporationError("WAR_DECLARATION_UNAVAILABLE");
          var declaredAt = Number(CorporationWire.Time(now()));
          var startedAt = checked(declaredAt+864_000_000_000L);
          var dueAt = checked(declaredAt+6_048_000_000_000L);
          var warHqId = call.Args.Count > 1 ? PositiveId(call.Arg(1)) : null;
          var callId = Number(call.CallId);
          var operationKey = callId > 0
              ? $"corporation-war:{corporation.Id}:{actor}:{callId}"
              : Guid.NewGuid().ToString("N");
          var result = await declareWar(new CorpWarDeclarationRequest(
              corporation.Id,actor,againstId.Value,warHqId,declaredAt,startedAt,dueAt,4,100_000_000m,1_000_125,operationKey),
              call.Cancellation);
          var warRecord = Py.KeyVal(
              ("warID",CorporationMaskValue(result.WarId)),("declaredByID",new PyInt(corporation.Id)),
              ("againstID",new PyInt(againstId.Value)),
              ("warHQID",warHqId is { } hq ? CorporationMaskValue(hq) : PyNone.Instance),
              ("timeDeclared",new PyLong(declaredAt)),("timeStarted",new PyLong(startedAt)),
              ("timeFinished",PyNone.Instance),("retracted",PyNone.Instance),("retractedBy",PyNone.Instance),
              ("billID",CorporationMaskValue(result.BillId)),("mutual",new PyInt(0)),
              ("fightingLegalNotified",new PyInt(0)),("openForAllies",new PyInt(0)),
              ("allies",new PyDict([])),("createdFromWarID",PyNone.Instance),("reward",Py.Number(0)));
          CorporationNotificationAdapter.WarChanged(characterSessions,call,corporation.Id,againstId.Value,warRecord);
          return warRecord;
      }
}

[Service("corpRecProxy")]
public sealed partial class CorpRecruitmentProxyService(
    CorporationStore store,PgCharacterStore characters,Func<long> now,CharacterSessions? sessions = null)
{
    private const int MaxRecruiterIds = 100;
    private const long FileTimeTicksPerDay = 864_000_000_000L;

    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = Py.Tuple(new PyStr($"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}"),CorporationWire.Time(now()));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count:>0 } nested)
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method=Text(nested.Items[0]),
                Args=nested.Items.Count>1 ? Items(nested.Items[1]) : [],
                Kwargs=nested.Items.Count>2 ? nested.Items[2] : PyNone.Instance,
                Session=call.Session,
                CallId=call.CallId,
                Cancellation=call.Cancellation
            });
        return Py.Tuple(new PySubStruct(new PySubStream(oid)),result);
    }

    [Method("GetRecruitmentAdsForCorporation")]
    public async ValueTask<PyValue> GetRecruitmentAdsForCorporation(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { } corporation)
            return new PyList([]);
        return await AdsForCorporation(corporation,call.Cancellation);
    }

    [Method("GetRecruitmentAdsByCorpID")]
    public async ValueTask<PyValue> GetRecruitmentAdsByCorpID(ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        if (corporationId <= 0 || await store.GetAsync(corporationId,call.Cancellation) is not { } corporation)
            return new PyList([]);
        return await AdsForCorporation(corporation,call.Cancellation);
    }

    [Method("GetRecruiters")]
    public async ValueTask<PyValue> GetRecruiters(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character)
            return new PyList([]);
        var ad = await store.RecruitmentAdAsync(character.CorporationId,Number(call.Arg(0)),call.Cancellation);
        return ad is null ? new PyList([]) : new PyList(ad.Recruiters.Select(id => (PyValue)CorporationWire.Id(id)).ToArray());
    }

    [Method("GetRecruitmentAd")]
    public async ValueTask<PyValue> GetRecruitmentAd(ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        var adId = Number(call.Arg(1));
        if (corporationId <= 0 || adId <= 0 ||
            await store.GetAsync(corporationId,call.Cancellation) is not { } corporation ||
            await store.RecruitmentAdAsync(corporationId,adId,call.Cancellation) is not { } ad)
            return PyNone.Instance;
        var count = (await store.MembersAsync(corporationId,call.Cancellation)).Count;
        return CorporationWire.RecruitmentAdRow(ad,corporation.AllianceId,count);
    }

    [Method("SearchCorpAds")]
    public async ValueTask<PyValue> SearchCorpAds(ServiceCall call)
    {
        var ads = await store.RecruitmentAdsAsync(token:call.Cancellation);
        if (ads.Count == 0)
            return new PyList([]);
        var corporationIds = ads.Select(ad => ad.CorporationId).Distinct().ToArray();
        var corporations = (await store.GetManyAsync(corporationIds,call.Cancellation))
            .ToDictionary(corporation => corporation.Id);
        var memberCounts = await store.RecruitmentMemberCountsAsync(corporationIds,call.Cancellation);
        var nowFiletime = Number(CorporationWire.Time(now()));
        var typeMask = Number(call.Arg(0));
        var langMask = Number(call.Arg(1));
        var excludeAlliances = Truth(call.Arg(2));
        var excludeFriendlyFire = Truth(call.Arg(3));
        var spRestriction = Number(call.Arg(4));
        var minMembers = Number(call.Arg(5));
        var maxMembers = Number(call.Arg(6));
        var maxIskTaxRate = DecimalLimit(call.Arg(7));
        var maxLpTaxRate = DecimalLimit(call.Arg(8));
        var searchTimeMask = Number(call.Arg(9));
        var otherMask = Number(call.Arg(10));
        var rows = new List<PyValue>();

        foreach (var ad in ads)
        {
            if (ad.ExpiryFiletime <= nowFiletime ||
                !corporations.TryGetValue(ad.CorporationId,out var corporation))
                continue;
            var memberCount = memberCounts.GetValueOrDefault(ad.CorporationId);
            if (typeMask > 0 && (ad.TypeMask & typeMask) == 0 ||
                langMask > 0 && (ad.LangMask & langMask) == 0 ||
                excludeAlliances && corporation.AllianceId is > 0 ||
                excludeFriendlyFire && corporation.FriendlyFire ||
                spRestriction > 0 && ad.MinSP > spRestriction ||
                minMembers > 0 && memberCount < minMembers ||
                maxMembers > 0 && memberCount > maxMembers ||
                maxIskTaxRate is >= 0 && corporation.TaxRate > maxIskTaxRate ||
                maxLpTaxRate is >= 0 && corporation.LoyaltyPointTaxRate > maxLpTaxRate ||
                searchTimeMask > 0 && ad.HourMask1 > 0 && (ad.HourMask1 & searchTimeMask) == 0 ||
                otherMask > 0 && (ad.OtherMask & otherMask) == 0)
                continue;
            rows.Add(CorporationWire.RecruitmentAdRow(ad,corporation.AllianceId,memberCount));
        }
        return new PyList(rows);
    }

    [Method("CreateRecruitmentAd")]
    public async ValueTask<PyValue> CreateRecruitmentAd(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { } corporation)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var days = call.Arg(0) is PyNone ? 1 : Number(call.Arg(0));
        if (days <= 0)
            throw new CorporationError("CORPORATION_INVALID_RECRUITMENT_DURATION");
        var typeMask = Number(call.Arg(1));
        var langMask = Number(call.Arg(2));
        var description = Text(call.Arg(3));
        var recruiters = RecruiterIds(call.Arg(4));
        var title = Text(call.Arg(5));
        var hourMask = Number(call.Arg(6));
        var minSP = Number(call.Arg(7));
        var otherMask = Number(call.Arg(8));
        var createFiletime = Number(CorporationWire.Time(now()));
        long expiryFiletime;
        try { expiryFiletime = checked(createFiletime + checked(days*FileTimeTicksPerDay)); }
        catch (OverflowException) { throw new CorporationError("CORPORATION_INVALID_RECRUITMENT_DURATION"); }

        var callId = Number(call.CallId);
        var requestKey = callId > 0 ? $"corp-recruitment:{corporation.Id}:{actor}:{callId}" : null;
        var fingerprint = requestKey is null ? null : System.Text.Json.JsonSerializer.Serialize(new
        {
            CorporationId=corporation.Id,Actor=actor,Days=days,TypeMask=typeMask,LangMask=langMask,
            Description=description,Recruiters=recruiters,Title=title,HourMask=hourMask,MinSP=minSP,OtherMask=otherMask
        });
        var adId = await store.CreateRecruitmentAdAsync(corporation.Id,actor,0,typeMask,langMask,description,title,
            createFiletime,expiryFiletime,hourMask,minSP,otherMask,recruiters,requestKey,fingerprint,call.Cancellation);
        CorporationNotificationAdapter.RecruitmentAdChanged(sessions,call);
        return new PyInt(adId);
    }

    [Method("UpdateRecruitmentAd")]
    public async ValueTask<PyValue> UpdateRecruitmentAd(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var corporationId = character.CorporationId;
        var adId = Number(call.Arg(0));
        var ad = await store.RecruitmentAdAsync(corporationId,adId,call.Cancellation);
        if (ad is null)
            return PyNone.Instance;
        var addedDays = Number(call.Arg(6));
        if (addedDays < 0)
            throw new CorporationError("CORPORATION_INVALID_RECRUITMENT_DURATION");
        var changed = await store.UpdateRecruitmentAdAsync(corporationId,actor,adId,
            OptionalNumber(call.Arg(1),ad.TypeMask),OptionalNumber(call.Arg(2),ad.LangMask),
            call.Arg(3) is PyNone ? ad.Description : Text(call.Arg(3)),
            call.Arg(5) is PyNone ? ad.Title : Text(call.Arg(5)),
            OptionalNumber(call.Arg(7),ad.HourMask1),OptionalNumber(call.Arg(8),ad.MinSP),
            OptionalNumber(call.Arg(9),ad.OtherMask),addedDays,Number(CorporationWire.Time(now())),
            call.Arg(4) is PyNone ? ad.Recruiters : RecruiterIds(call.Arg(4)),call.Cancellation);
        if (changed)
            CorporationNotificationAdapter.RecruitmentAdChanged(sessions,call);
        return PyNone.Instance;
    }

    [Method("DeleteRecruitmentAd")]
    public async ValueTask<PyValue> DeleteRecruitmentAd(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (await store.DeleteRecruitmentAdAsync(character.CorporationId,actor,Number(call.Arg(0)),call.Cancellation))
            CorporationNotificationAdapter.RecruitmentAdChanged(sessions,call);
        return PyNone.Instance;
    }

    private async Task<PyValue> AdsForCorporation(Corporation corporation,CancellationToken token)
    {
        var ads = await store.RecruitmentAdsAsync(corporation.Id,token);
        var memberCount = (await store.MembersAsync(corporation.Id,token)).Count;
        return new PyList(ads.Select(ad => (PyValue)CorporationWire.RecruitmentAdRow(ad,corporation.AllianceId,memberCount)).ToArray());
    }

    private static long[] RecruiterIds(PyValue value)
    {
        var requested = Items(value);
        if (requested.Count > MaxRecruiterIds)
            throw new CorporationError("CORPORATION_BULK_LIMIT_EXCEEDED");
        var ids = requested.Select(Number).ToArray();
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
            throw new CorporationError("CORPORATION_INVALID_RECRUITER");
        return ids;
    }

    private static long OptionalNumber(PyValue value,long fallback) => value is PyNone ? fallback : Number(value);

    private static decimal? DecimalLimit(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) => (decimal)number.Value,
        PyStr text when decimal.TryParse(text.Value,System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,out var result) => result,
        _ => null
    };

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) => (long)Math.Truncate(number.Value),
        PyTuple { Items.Count: >0 } tuple => Number(tuple.Items[0]),
        _ => 0
    };

    private static string Text(PyValue value) => JsSemantics.StringValue(value) ?? "";

    private static bool Truth(PyValue value) => value switch
    {
        PyBool boolean => boolean == PyBool.True,
        PyNone => false,
        PyInt integer => integer.Value != 0,
        PyLong integer => integer.Value != 0,
        PyReal real => real.Value != 0,
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!.Length != 0,
        _ => true
    };

    private static IReadOnlyList<PyValue> Items(PyValue value) => value switch
    {
        PyTuple tuple => tuple.Items,
        PyList list => list.Items,
        PyPackedRow row => row.Values,
        _ => []
    };
}
