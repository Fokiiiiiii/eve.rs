using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

// Wire contracts follow EveJS v0.12.9 allianceRegistryRuntime.js and allianceViewState.js (828823a75f211fdecb945b087a26c225f111491f).

[Service("allianceRegistry")]
public sealed partial class AllianceRegistryService(CorporationStore store,PgCharacterStore characters,Func<long> now,AllianceApplicationScheduleCallback? scheduleAllianceApplication = null,Action? wakeAllianceApplicationScheduler = null) : IBoundObjectReleaseHandler
{
public AllianceRegistryService(CorporationStore store, PgCharacterStore characters, Func<long> now,
        Func<long, CancellationToken, Task>? legacyAffiliationChanged)
        : this(store, characters, now)
    {
        _ = legacyAffiliationChanged;
    }
    private readonly ConcurrentDictionary<string,long> _alliancesByObject = new(StringComparer.Ordinal);

    private static readonly string[] AllianceHeader =
    [
        "allianceID","allianceName","shortName","executorCorpID","creatorCorpID","creatorCharID","warFactionID",
        "description","url","startDate","memberCount","dictatorial","allowWar","currentCapital","currentPrimeHour",
        "newPrimeHour","newPrimeHourValidAfter","deleted"
    ];

    private static (string Key,PyValue Value)[] AllianceFields(Alliance a,long memberCount) =>
    [
        ("allianceID",new PyInt(a.Id)),("allianceName",new PyStr(a.Name)),("shortName",new PyStr(a.ShortName)),
        ("executorCorpID",CorporationWire.Id(a.ExecutorCorporationId)),("creatorCorpID",new PyInt(a.CreatorCorporationId)),
        ("creatorCharID",new PyInt(a.CreatorCharacterId)),("warFactionID",PyNone.Instance),("description",new PyStr(a.Description)),
        ("url",string.IsNullOrWhiteSpace(a.Url) ? PyNone.Instance : new PyStr(a.Url)),("startDate",CorporationWire.Time(a.CreatedMs)),
        ("memberCount",new PyInt(memberCount)),("dictatorial",new PyInt(a.Dictatorial ? 1 : 0)),("allowWar",new PyInt(0)),
        ("currentCapital",PyNone.Instance),("currentPrimeHour",new PyInt(0)),("newPrimeHour",new PyInt(0)),
        ("newPrimeHourValidAfter",new PyLong(0)),("deleted",new PyInt(a.Deleted ? 1 : 0))
    ];

    private static PyValue AllianceRow(Alliance a,long memberCount)
    {
        var fields = AllianceFields(a,memberCount);
        return new PyObject(new PyStr("util.Row"),Py.Dict(
            ("header",Py.Tuple([.. AllianceHeader.Select(name => (PyValue)new PyStr(name))])),
            ("line",Py.Tuple([.. fields.Select(field => field.Value)]))));
    }

    private long AllianceMembershipDays(long startedMs)
    {
        var elapsed = now() - startedMs;
        return elapsed > 0 ? elapsed / 86400000 : 0;
    }

    private static long Number(PyValue value) => value switch { PyInt n => n.Value,PyLong n => n.Value,PyTuple { Items.Count:>0 } t => Number(t.Items[0]),_ => 0 };
    private async Task<long> Id(ServiceCall call)
    {
        if (Number(call.Arg(0)) is > 0 and var requested) return requested;
        if (call.BoundObjectId is { } oid && _alliancesByObject.TryGetValue(oid,out var bound)) return bound;
        return call.Session.CharacterId is { } id ? (await characters.GetAsync(id,call.Cancellation))?.AllianceId ?? 0 : 0;
    }
    [Method("IsAllianceLocal")]
    public async ValueTask<PyValue> IsAllianceLocal(ServiceCall call) =>
        new PyInt(await store.AllianceAsync(await Id(call),call.Cancellation) is null ? 0 : 1);

    [Method("GetAlliance")]
    public async ValueTask<PyValue> GetAlliance(ServiceCall call)
    {
        if (await store.AllianceAsync(await Id(call),call.Cancellation) is not { } alliance) return PyNone.Instance;
        var counts = await store.AllianceCharacterCountsAsync([alliance.Id],call.Cancellation);
        var fields = AllianceFields(alliance,counts.GetValueOrDefault(alliance.Id));
        return Py.KeyVal([.. fields,
            ("__header__",Py.Tuple([.. AllianceHeader.Select(name => (PyValue)new PyStr(name))])),
            ("currentCapitalSystem",PyNone.Instance),("newCapitalSystem",PyNone.Instance),("newCapitalSystemValidAfter",new PyLong(0))]);
    }
    [Method("GetAlliancePublicInfo")]
    public ValueTask<PyValue> GetAlliancePublicInfo(ServiceCall call) => GetAlliance(call);

    [Method("GetRankedAlliances")]
    public async ValueTask<PyValue> GetRankedAlliances(ServiceCall call)
    {
        var maxLength = call.Arg(0) is PyNone ? 100 : Math.Max(0,Number(call.Arg(0)));
        var alliances = await store.RankedAlliancesAsync(maxLength == 0 ? null : maxLength,call.Cancellation);
        var counts = await store.AllianceCharacterCountsAsync(alliances.Select(alliance => alliance.Id).ToArray(),call.Cancellation);
        return new PyList([.. alliances.Select(alliance => AllianceRow(alliance,counts.GetValueOrDefault(alliance.Id)))]);
    }

    private static readonly string[] MemberHeader = ["corporationID","allianceID","chosenExecutorID","startDate","deleted"];
    private static readonly Descriptor MemberDescriptor = CorporationWire.DescriptorOf(
        ("corporationID",3),("allianceID",3),("chosenExecutorID",3),("startDate",64),("deleted",11));
    private static PyValue[] Line(AllianceMember m,long? viewer) => [new PyInt(m.CorporationId),new PyInt(m.AllianceId),
        m.CorporationId == viewer ? CorporationWire.Id(m.ChosenExecutorId) : PyNone.Instance,CorporationWire.Time(m.StartedMs),new PyInt(0)];
    [Method("GetAllianceMembers")]
    public async ValueTask<PyValue> GetAllianceMembers(ServiceCall call)
    {
        return Py.Rowset(MemberHeader,(await store.AllianceMembersAsync(await Id(call),call.Cancellation)).Select(m => (PyValue)new PyList(Line(m,null))));
    }

    [Method("GetMembers")]
    public async ValueTask<PyValue> GetMembers(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } id || await characters.GetAsync(id,call.Cancellation) is not { AllianceId:not null } c)
            throw MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr("Alliance membership required.")));
        var members = await store.AllianceMembersAsync(c.AllianceId.Value,call.Cancellation);
        return new PyObjectEx(ObjectExKind.Ex2,Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
            Py.Dict(("header",MemberDescriptor.Header),("idName",new PyStr("corporationID")),("columnName",new PyStr("corporationID")))),[],
            [.. members.Select(m => new KeyValuePair<PyValue,PyValue>(new PyInt(m.CorporationId),new PyPackedRow(MemberDescriptor,Line(m,c.CorporationId))))]);
    }

    [Method("GetEmploymentRecord")]
    public async ValueTask<PyValue> GetEmploymentRecord(ServiceCall call)
    {
        var requested = Number(call.Arg(0));
        if (requested > 0 && await store.GetAsync(requested,call.Cancellation) is { } corporation)
        {
            var history = (await store.CorporationAllianceHistoryAsync(requested,call.Cancellation))
                .Select(row => ((long?)row.AllianceId,row.StartedMs,row.Deleted)).ToList();
            if (corporation.AllianceId is { } current && history.All(row => row.Item1 != current))
            {
                var member = (await store.AllianceMembersAsync(current,call.Cancellation))
                    .FirstOrDefault(row => row.CorporationId == corporation.Id);
                if (member is not null)
                {
                    var currentAlliance = await store.AllianceAsync(current,call.Cancellation);
                    history.Add((current,member.StartedMs,currentAlliance?.Deleted ?? false));
                }
            }
            var rows = history.OrderByDescending(row => row.StartedMs).ThenByDescending(row => row.Item1 ?? 0)
                .Select(row => (PyValue)Py.List(CorporationWire.Id(row.Item1),CorporationWire.Time(row.StartedMs),new PyInt(row.Deleted ? 1 : 0))).ToList();
            rows.Add(Py.List(PyNone.Instance,CorporationWire.Time(corporation.CreatedMs),new PyInt(corporation.Deleted ? 1 : 0)));
            return Py.Rowset(["allianceID","startDate","deleted"],rows);
        }

        var allianceRecord = await store.AllianceAsync(await Id(call),call.Cancellation);
        var allianceRows = allianceRecord is { } alliance
            ? new PyValue[] { Py.List(new PyInt(alliance.Id),CorporationWire.Time(alliance.CreatedMs),new PyInt(alliance.Deleted ? 1 : 0)) }
            : [];
        return Py.Rowset(["allianceID","startDate","deleted"],allianceRows);
    }

    [Method("GetDaysInAlliance")]
    public async ValueTask<PyValue> GetDaysInAlliance(ServiceCall call)
    {
        var alliance = await Id(call);
        var corporationId = Number(call.Arg(1));
        if (alliance <= 0 || corporationId <= 0 || await store.GetAsync(corporationId,call.Cancellation) is not { } corporation || corporation.AllianceId != alliance)
            return new PyInt(0);
        var member = (await store.AllianceMembersAsync(alliance,call.Cancellation)).FirstOrDefault(row => row.CorporationId == corporationId);
        return new PyInt(member is null ? 0 : AllianceMembershipDays(member.StartedMs));
    }

    [Method("GetAllianceMembersOlderThan")]
    public async ValueTask<PyValue> GetAllianceMembersOlderThan(ServiceCall call)
    {
        var alliance = await Id(call);
        var minimumDays = Math.Max(0,Number(call.Arg(1)));
        var members = await store.AllianceMembersAsync(alliance,call.Cancellation);
        return new PyList([.. members.Where(member => AllianceMembershipDays(member.StartedMs) >= minimumDays)
            .Select(member => (PyValue)new PyInt(member.CorporationId))]);
    }

    private static readonly Descriptor ApplicationDescriptor = CorporationWire.DescriptorOf(
        ("allianceID",3),("corporationID",3),("applicationText",130),("state",3),("applicationDateTime",64));

    private static Exception AllianceAccessDenied() => MachoErrors.ThrowUserError("CrpAccessDenied",
        ("reason",Py.List(new PyInt(101),new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));

    private async Task<(long Actor,long Alliance)> RequireAllianceExecutorDirector(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor) throw AllianceAccessDenied();
        if (await characters.GetAsync(actor,call.Cancellation) is not { } character) throw AllianceAccessDenied();
        var corporation = await store.GetAsync(character.CorporationId,call.Cancellation);
        if (corporation is null || corporation.IsNpc || corporation.AllianceId is not { } allianceId)
            throw AllianceAccessDenied();

        var alliance = await store.AllianceAsync(allianceId,call.Cancellation);
        if (alliance is not { Deleted:false } || alliance.ActiveExecutorId != corporation.Id)
            throw AllianceAccessDenied();

        if (corporation.CeoId != actor)
        {
            var member = await store.MemberAsync(actor,call.Cancellation);
            if (member is null || member.CorporationId != corporation.Id ||
                (CorporationRoles.Session(corporation,member,null,await store.TitlesAsync(corporation.Id,call.Cancellation)).AtAll &
                    CorporationRoles.Director) == 0)
                throw AllianceAccessDenied();
        }
        return (actor,allianceId);
    }

    [Method("UpdateAlliance")]
    public async ValueTask<PyValue> UpdateAlliance(ServiceCall call)
    {
        var (actor,alliance) = await RequireAllianceExecutorDirector(call);
        var description = JsSemantics.StringValue(call.Arg(0)) ?? "";
        var url = JsSemantics.StringValue(call.Arg(1)) ?? "";
        try
        {
            await store.UpdateAllianceAsync(actor,alliance,description,url,call.Cancellation);
        }
        catch (CorporationError error) when (error.Code is "CHARACTER_NOT_FOUND" or "CORPORATION_NOT_FOUND" or
            "CORPORATION_ACCESS_DENIED" or "ALLIANCE_EXECUTOR_REQUIRED" or "ALLIANCE_PUBLIC_INFO_INVALID_LENGTH")
        {
            throw AllianceAccessDenied();
        }
        return PyNone.Instance;
    }

    [Method("GetApplications")]
    public async ValueTask<PyValue> GetApplications(ServiceCall call)
    {
        var (_,allianceId) = await RequireAllianceExecutorDirector(call);
        var applications = await store.AllianceApplicationsAsync(allianceId,call.Cancellation);
        return new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
                Py.Dict(("header",ApplicationDescriptor.Header),("idName",new PyStr("corporationID")),
                    ("columnName",new PyStr("corporationID")))),[],
            [.. applications.Select(application => new KeyValuePair<PyValue,PyValue>(
                new PyInt(application.CorporationId),
                new PyPackedRow(ApplicationDescriptor,[
                    new PyInt(application.AllianceId),new PyInt(application.CorporationId),new PyWString(application.Text),
                    new PyInt(application.Status),CorporationWire.Time(application.CreatedMs)])))]);
    }

    [Method("UpdateApplication")]
    public async ValueTask<PyValue> UpdateApplication(ServiceCall call)
    {
        var (actor, allianceId) = await RequireAllianceExecutorDirector(call);
        var corporationId = Number(call.Arg(0));
        var applicationText = JsSemantics.StringValue(call.Arg(1)) ?? "";
        var state = Number(call.Arg(2));
        if (corporationId <= 0) throw new CorporationError("ALLIANCE_APPLICATION_NOT_FOUND");
        if (state is not (2 or 4)) throw new CorporationError("ALLIANCE_APPLICATION_INVALID_STATE");

        var application = (await store.AllianceApplicationsAsync(allianceId, call.Cancellation))
            .LastOrDefault(item => item.CorporationId == corporationId);
        if (application is null) throw new CorporationError("ALLIANCE_APPLICATION_NOT_FOUND");

        if (state == 2)
        {
            if (scheduleAllianceApplication is null) throw new CorporationError("ALLIANCE_DUE_SCHEDULER_UNAVAILABLE");
            await store.RespondToAllianceAsync(actor, application.Id, applicationText, (int)state,
                scheduleAllianceApplication, call.Cancellation);
            wakeAllianceApplicationScheduler?.Invoke();
        }
        else
        {
            await store.RespondToAllianceAsync(actor, application.Id, applicationText, (int)state, call.Cancellation);
        }
        return PyNone.Instance;
    }
    [Method("MachoResolveObject")]
    public async ValueTask<PyValue> MachoResolveObject(ServiceCall call) =>
        await store.AllianceAsync(await Id(call),call.Cancellation) is not null ? new PyInt(MachoMessage.ProxyNodeId) : PyNone.Instance;

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var alliance = await Id(call);
        if (await store.AllianceAsync(alliance,call.Cancellation) is null) return PyNone.Instance;
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _alliancesByObject[oid] = alliance;
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count:>0 } nested)
        {
            var args = nested.Items.Count>1 ? nested.Items[1] switch { PyTuple t => t.Items,PyList l => l.Items,_ => [] } : [];
            result = await ((IService)this).CallAsync(new ServiceCall { Method=JsSemantics.StringValue(nested.Items[0]) ?? "",Args=args,
                Kwargs=nested.Items.Count>2 ? nested.Items[2] : PyNone.Instance,Session=call.Session,CallId=call.CallId,BoundObjectId=oid,Cancellation=call.Cancellation });
        }
        return Py.Tuple(new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid),CorporationWire.Time(now())))),result);
    }
    public void OnBoundObjectReleased(string oid,ClientSession? session) => _alliancesByObject.TryRemove(oid,out _);
}


// Alliance application deadlines, war holds and affiliations use the shared Scheduler, Relations and EventBus.
public sealed class AllianceApplicationDueWorker(
    CorporationStore store,
    PgCharacterStore characters,
    Eve.Scheduler.DurableScheduler scheduler,
    Eve.Relations.RelationStore relations,
    Eve.Events.EventBus events,
    Func<long, CancellationToken, Task> affiliationChanged)
{
    public const string JobKind = "alliance.application.effective";
    private const string AffiliationSubscriber = "alliance.affiliation.sessions";

    public async Task RegisterAsync(CancellationToken token = default)
    {
        store.CorporationMemberAffiliationWriter = WriteCorporationMemberAffiliationAsync;
        scheduler.Register(JobKind, HandleAsync);
        events.Subscribe(AffiliationSubscriber,
            ["relation.affiliation.corporation.changed", "relation.affiliation.corporation.ended",
             "relation.affiliation.alliance.changed", "relation.affiliation.alliance.ended"],
            NotifyAffiliationChangedAsync);
        await ReconcileCorporationMemberAffiliationsAsync(token);
        await ReconcileAllianceAffiliationsAsync(token);
        foreach (var job in await store.PendingAllianceApplicationJobsAsync(token))
            await scheduler.ScheduleAsync(Spec(job), token);
    }


    private async Task ReconcileCorporationMemberAffiliationsAsync(CancellationToken token)
    {
        var kind = Eve.Relations.RelationKinds.CorporationMember.Name;
        var existing = await relations.ListAsync(kind, token);
        var staleSubjects = existing.Select(relation => relation.Subject).ToHashSet();
        long afterCharacterId = 0;
        while (true)
        {
            var page = await characters.AffiliationsAfterAsync(
                afterCharacterId, Eve.Characters.PgCharacterStore.CorporationAffiliationPageSize, token);
            if (page.Count == 0) break;
            var desired = new Dictionary<long, long?>(page.Count);
            foreach (var affiliation in page)
            {
                if (affiliation.CorporationId <= 0)
                    throw new InvalidOperationException($"character {affiliation.CharacterId} has no valid corporation id");
                desired[affiliation.CharacterId] = affiliation.CorporationId;
                staleSubjects.Remove(affiliation.CharacterId);
            }
            await relations.ReconcileOnePerSubjectBatchAsync(kind, desired,
                "corporation membership reconciliation", token);
            afterCharacterId = page[^1].CharacterId;
        }

        foreach (var batch in staleSubjects.Order().Chunk(Eve.Relations.RelationStore.ReconciliationBatchSize))
        {
            var desired = batch.ToDictionary(subject => subject, _ => (long?)null);
            await relations.ReconcileOnePerSubjectBatchAsync(kind, desired,
                "stale corporation membership reconciliation", token);
        }
    }

    private async Task ReconcileAllianceAffiliationsAsync(CancellationToken token)
    {
        var kind = Eve.Relations.RelationKinds.AllianceMember.Name;
        var desired = (await store.CorporationAllianceAffiliationsAsync(token))
            .ToDictionary(affiliation => affiliation.CorporationId,
                affiliation => affiliation.AllianceId is > 0 ? affiliation.AllianceId : null);
        foreach (var relation in await relations.ListAsync(kind, token))
            desired.TryAdd(relation.Subject, null);

        foreach (var batch in desired.OrderBy(pair => pair.Key).Chunk(Eve.Relations.RelationStore.ReconciliationBatchSize))
        {
            var changes = batch.ToDictionary(pair => pair.Key, pair => pair.Value);
            await relations.ReconcileOnePerSubjectBatchAsync(kind, changes,
                "alliance affiliation reconciliation", token);
        }
    }

    public async Task ScheduleAcceptedAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, AllianceApplicationDueJob job, CancellationToken token)
    {
        await scheduler.ScheduleAsync((Npgsql.NpgsqlConnection)connection, (Npgsql.NpgsqlTransaction)transaction,
            Spec(job), token);
    }

    public void Wake() => scheduler.Wake();

    private async Task WriteCorporationMemberAffiliationAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, long characterId, long corporationId, CancellationToken token)
    {
        await relations.SetAsync((Npgsql.NpgsqlConnection)connection, (Npgsql.NpgsqlTransaction)transaction,
            Eve.Relations.RelationKinds.CorporationMember.Name, characterId, corporationId, 1,
            "corporation membership", cancellationToken: token);
    }

    public async Task WriteAffiliationAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, long corporationId, long? allianceId, CancellationToken token)
    {
        var kind = Eve.Relations.RelationKinds.AllianceMember.Name;
        var pgConnection = (Npgsql.NpgsqlConnection)connection;
        var pgTransaction = (Npgsql.NpgsqlTransaction)transaction;
        if (allianceId is > 0)
        {
            await relations.SetAsync(pgConnection, pgTransaction, kind, corporationId, allianceId.Value, 1,
                "alliance membership", cancellationToken: token);
            return;
        }

        foreach (var current in await relations.FromAsync(kind, corporationId, pgConnection, pgTransaction, token))
            await relations.EndAsync(pgConnection, pgTransaction, kind, corporationId, current.Object,
                "alliance membership ended", token);
    }

    private async Task<IReadOnlyList<AllianceApplicationWar>> ReadWarsAsync(
        long corporationId, long allianceId, CancellationToken token)
    {
        var outgoing = await relations.FromAsync(Eve.Relations.RelationKinds.War.Name, corporationId, cancellationToken: token);
        var incoming = await relations.ToAsync(Eve.Relations.RelationKinds.War.Name, corporationId, cancellationToken: token);
        return outgoing.Concat(incoming)
            .DistinctBy(war => war.Id)
            .Where(war => war.UntilMs is null || war.UntilMs > scheduler.Now)
            .Select(war =>
            {
                var attrs = war.Attrs<System.Text.Json.JsonElement>();
                var mutual = attrs.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    attrs.TryGetProperty("mutual", out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.True;
                return new AllianceApplicationWar(war.Subject, war.Object, mutual);
            })
            .ToArray();
    }

    private async Task HandleAsync(Eve.Scheduler.JobContext job, CancellationToken token)
    {
        var payload = job.Payload<AllianceApplicationDueJob>()
            ?? throw new InvalidOperationException("alliance application job has no payload");
        var result = await store.ProcessDueAllianceApplicationAsync(
            job.Connection, job.Transaction, payload, ReadWarsAsync, WriteAffiliationAsync, token);
        if (result.RescheduleAtMs is { } at)
            job.RunAgainAt(at, payload with { DueAtMs = at });
    }

    private async Task NotifyAffiliationChangedAsync(Eve.Events.EventContext context, CancellationToken token)
    {
        var change = context.Event.Payload<Eve.Relations.RelationChange>()
            ?? throw new InvalidOperationException("affiliation event has no payload");
        var current = await relations.FromAsync(change.Kind, change.Subject,
            (Npgsql.NpgsqlConnection)context.Connection, (Npgsql.NpgsqlTransaction)context.Transaction, token);
        var ended = context.Event.Topic.EndsWith(".ended", StringComparison.Ordinal);
        if (ended ? current.Count != 0 : !current.Any(relation => relation.Object == change.Object))
            return;

        if (change.Kind == Eve.Relations.RelationKinds.CorporationMember.Name)
        {
            await affiliationChanged(change.Subject, token);
            return;
        }
        if (change.Kind == Eve.Relations.RelationKinds.AllianceMember.Name)
        {
            foreach (var character in await characters.ForCorporationAsync(change.Subject, token))
                await affiliationChanged(character.Id, token);
            return;
        }
        throw new InvalidOperationException($"unexpected affiliation event kind '{change.Kind}'");
    }

    private static Eve.Scheduler.JobSpec Spec(AllianceApplicationDueJob job) =>
        new(JobKind, job.DueAtMs)
        {
            Key = $"alliance-application:{job.ApplicationId}",
            Payload = job,
            OnDuplicate = Eve.Scheduler.OnDuplicate.Replace
        };
}

// CorpRegistry Alliance RPCs follow EveJS v0.12.9 corpRegistryRuntime.js (828823a75f211fdecb945b087a26c225f111491f).
public sealed partial class CorpRegistryService
{
    private static readonly Descriptor AllianceApplicationDescriptor = CorporationWire.DescriptorOf(
        ("allianceID",3),("corporationID",3),("applicationText",130),("state",3),("applicationDateTime",64));

    private async Task<long> RequireAllianceApplicationDirector(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc:false } corporation)
            throw CorporationAccessDenied();
        if (corporation.CeoId != actor)
        {
            var member = await store.MemberAsync(actor,call.Cancellation);
            if (member is null || member.CorporationId != corporation.Id ||
                (CorporationRoles.Session(corporation,member,null,await store.TitlesAsync(corporation.Id,call.Cancellation)).AtAll & CorporationRoles.Director) == 0)
                throw CorporationAccessDenied();
        }
        return corporation.Id;
    }

    [Method("CreateAlliance")]
    public async ValueTask<PyValue> CreateAlliance(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor, call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId, call.Cancellation) is not { IsNpc: false } corporation ||
            corporation.CeoId != actor)
            throw CorporationAccessDenied();

        var request = new NewAlliance(
            JsSemantics.StringValue(call.Arg(0)) ?? "", JsSemantics.StringValue(call.Arg(1)) ?? "",
            JsSemantics.StringValue(call.Arg(2)) ?? "", JsSemantics.StringValue(call.Arg(3)) ?? "");
        var operationKey = $"alliance-create:{call.Session.SessionId}:{actor}:{Number(call.CallId)}";
        if (store.AllianceAffiliationWriter is not { } writer)
            throw new CorporationError("ALLIANCE_AFFILIATION_UNAVAILABLE");
        var alliance = await store.CreateAllianceAsync(actor, request, operationKey, writer, call.Cancellation);
        return new PyInt(alliance.Id);
    }

    [Method("GetAllianceApplications")]
    public async ValueTask<PyValue> GetAllianceApplications(ServiceCall call)
    {
        var corporationId = await RequireAllianceApplicationDirector(call);
        var applications = await store.AllianceApplicationsForCorporationAsync(corporationId,call.Cancellation);
        return new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
                Py.Dict(("header",AllianceApplicationDescriptor.Header),("idName",new PyStr("allianceID")),
                    ("columnName",new PyStr("allianceID")))),[],
            [.. applications.Select(application => new KeyValuePair<PyValue,PyValue>(
                new PyInt(application.AllianceId),new PyPackedRow(AllianceApplicationDescriptor,[
                    new PyInt(application.AllianceId),new PyInt(application.CorporationId),new PyWString(application.Text),
                    new PyInt(application.Status),CorporationWire.Time(application.CreatedMs)])))]);
    }

    [Method("GetSuggestedAllianceShortNames")]
    public ValueTask<PyValue> GetSuggestedAllianceShortNames(ServiceCall call)
    {
        var normalized = new string((JsSemantics.StringValue(call.Arg(0)) ?? "")
            .Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).Take(5).ToArray());
        var basis = normalized.Length == 0 ? "ALLY" : normalized;
        var suggestions = new[] { basis,basis[..Math.Min(4,basis.Length)],basis[..Math.Min(3,basis.Length)] + "X" }
            .Select(value => value.Length == 0 ? "ALLY" : value).Distinct(StringComparer.Ordinal);
        return ValueTask.FromResult<PyValue>(new PyList([.. suggestions.Select(value =>
            (PyValue)Py.KeyVal(("shortName",new PyStr(value))))]));
    }
}
