using Eve.Skills;
using Eve.Wallet;

namespace Eve.Corporations.Tests;

public class AllianceTests
{
    private static readonly NewAlliance Request = new("Orbital Alliance","ORB","An alliance.","");
    private static async Task<(long Actor,Corporation Corp)> Founder(
        Rig rig,string name,string ticker,decimal money = 2000000000m,bool skill = true,CorporationStore? store = null)
    {
        store ??= rig.Store;
        var actor = await rig.CharacterAsync(name,money);
        var corp = await store.CreateAsync(actor,new(name+" Corporation",ticker,"","",0),"corp-"+actor);
        if (skill)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection,tx,actor,[new CharacterSkill(3732,5,1024000)]);
            await tx.CommitAsync();
        }
        return (actor,corp);
    }

    private static Task<AllianceApplication> AcceptAllianceApplicationAsync(CorporationStore store, long actor, long applicationId, string? text = null) =>
        store.RespondToAllianceAsync(actor, applicationId, text, 2, static (_, _, _, _) => Task.CompletedTask);

    private static async Task<IReadOnlyList<long>> ProcessAllianceDueAsync(CorporationStore store)
    {
        AllianceWarReadCallback readWars = static (_, _, _) => Task.FromResult<IReadOnlyList<AllianceApplicationWar>>([]);
        AllianceAffiliationMutationCallback writeAffiliation = static (_, _, _, _, _) => Task.CompletedTask;
        var changed = new List<long>();
        foreach (var job in await store.PendingAllianceApplicationJobsAsync())
            if ((await store.ProcessDueAllianceApplicationAsync(job, readWars, writeAffiliation)).Effective)
                changed.Add(job.CorporationId);
        return changed;
    }

    [PgFact]
    public async Task Founding_fee_membership_and_replay_are_one_transaction()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor,corp) = await Founder(rig,"Founder One","FIRST");
        var before = (await rig.Wallets.GetAsync(actor))!.Isk;
        var alliance = await rig.Store.CreateAllianceAsync(actor,Request,"alliance-once");
        Assert.Equal(99000000,alliance.Id);
        Assert.Equal(corp.Id,alliance.ActiveExecutorId);
        Assert.Equal(alliance.Id,(await rig.Characters.GetAsync(actor))!.AllianceId);
        Assert.Equal(alliance.Id,(await rig.Store.GetAsync(corp.Id))!.AllianceId);
        Assert.Contains(alliance, await rig.Store.AllAlliancesAsync());
        Assert.Single(await rig.Store.AllianceMembersAsync(alliance.Id));
        Assert.Equal(before-1000000000m,(await rig.Wallets.GetAsync(actor))!.Isk);
        Assert.Equal(alliance,await rig.Store.CreateAllianceAsync(actor,Request,"alliance-once"));
        Assert.Equal(2,(await rig.Transactions.JournalAsync(actor)).Count);
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.CreateAllianceAsync(actor,Request with { Name="Other Alliance" },"alliance-once"));
        Assert.Equal(before-1000000000m,(await rig.Wallets.GetAsync(actor))!.Isk);
    }

    [PgFact]
    public async Task Missing_skill_or_insufficient_money_cannot_leave_an_alliance_or_fee()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor,corp) = await Founder(rig,"Poor Founder","POOR",10000000m,false);
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.CreateAllianceAsync(actor,Request,"no-skill"));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection,tx,actor,[new CharacterSkill(3732,5,1024000)]);
            await tx.CommitAsync();
        }
        var before = (await rig.Wallets.GetAsync(actor))!.Isk;
        await Assert.ThrowsAsync<InsufficientFundsException>(() => rig.Store.CreateAllianceAsync(actor,Request,"poor"));
        Assert.Null((await rig.Store.GetAsync(corp.Id))!.AllianceId);
        Assert.Null((await rig.Characters.GetAsync(actor))!.AllianceId);
        Assert.Null(await rig.Store.AllianceAsync(99000000));
        Assert.Equal(before,(await rig.Wallets.GetAsync(actor))!.Isk);
        Assert.Single(await rig.Transactions.JournalAsync(actor));
    }

    [PgFact]
    public async Task Accepted_applications_wait_a_full_day_and_activate_once_after_recovery()
    {
        await using var rig = await Rig.CreateAsync();
        var (a, corpA) = await Founder(rig, "Founder A", "AAAAA");
        var (b, corpB) = await Founder(rig, "Founder B", "BBBBB");
        var alliance = await rig.Store.CreateAllianceAsync(a, Request, "first");
        var app = await rig.Store.ApplyToAllianceAsync(b, alliance.Id, "Join us");
        Assert.Equal(app, await rig.Store.ApplyToAllianceAsync(b, alliance.Id, "Join us"));
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.RespondToAllianceAsync(b, app, 2));
        var accepted = await AcceptAllianceApplicationAsync(rig.Store, a, app);

        rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs - 1;
        Assert.Empty(await ProcessAllianceDueAsync(rig.Store));
        Assert.Null((await rig.Characters.GetAsync(b))!.AllianceId);
        Assert.Single(await rig.Store.AllianceMembersAsync(alliance.Id));
        Assert.Equal(2, (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
        Assert.Equal(accepted.DueMs, (await AcceptAllianceApplicationAsync(rig.Store, a, app)).DueMs);

        rig.Now++;
        Assert.Equal([corpB.Id], await ProcessAllianceDueAsync(rig.ReopenStore()));
        Assert.Empty(await ProcessAllianceDueAsync(rig.Store));
        Assert.Equal(alliance.Id, (await rig.Characters.GetAsync(b))!.AllianceId);
        Assert.Equal(alliance.Id, (await rig.Store.GetAsync(corpB.Id))!.AllianceId);
        Assert.Equal(2, (await rig.Store.AllianceMembersAsync(alliance.Id)).Count);
        Assert.Equal(3, (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
        await rig.Store.ChooseExecutorAsync(b, corpB.Id);
        Assert.Equal(corpA.Id, (await rig.Store.AllianceAsync(alliance.Id))!.ActiveExecutorId);
        await rig.Store.ChooseExecutorAsync(a, corpB.Id);
        Assert.Equal(corpB.Id, (await rig.Store.AllianceAsync(alliance.Id))!.ActiveExecutorId);
    }

    [PgFact]
    public async Task Alliance_application_responses_update_text_atomically_and_replay_only_matching_payloads()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,_) = await Founder(rig,"Executor","EXE");
        var (applicant,_) = await Founder(rig,"Applicant","APP");
        var (rejectedApplicant,_) = await Founder(rig,"Rejected","REJ");
        var alliance = await rig.Store.CreateAllianceAsync(executor,Request,"response-text");
        var approvedId = await rig.Store.ApplyToAllianceAsync(applicant,alliance.Id,"Original approved text");
        var rejectedId = await rig.Store.ApplyToAllianceAsync(rejectedApplicant,alliance.Id,"Original rejected text");

        var approved = await AcceptAllianceApplicationAsync(rig.Store, executor, approvedId, "Updated approved text");
        var approvedReplay = await AcceptAllianceApplicationAsync(rig.Store, executor, approvedId, "Updated approved text");
        Assert.Equal(approved,approvedReplay);
        Assert.Equal("Updated approved text",approved.Text);
        await Assert.ThrowsAsync<CorporationError>(() =>
            AcceptAllianceApplicationAsync(rig.Store, executor, approvedId, "Conflicting approved text"));

        var rejected = await rig.Store.RespondToAllianceAsync(executor,rejectedId,"Updated rejected text",4);
        var rejectedReplay = await rig.Store.RespondToAllianceAsync(executor,rejectedId,"Updated rejected text",4);
        Assert.Equal(rejected,rejectedReplay);
        Assert.Equal("Updated rejected text",rejected.Text);
        Assert.True(rejected.Archived);
        await Assert.ThrowsAsync<CorporationError>(() =>
            rig.Store.RespondToAllianceAsync(executor,rejectedId,"Conflicting rejected text",4));

        var applications = await rig.Store.AllianceApplicationsAsync(alliance.Id);
        Assert.Equal(2,applications.Single(item => item.Id == approvedId).Status);
        Assert.Equal("Updated approved text",applications.Single(item => item.Id == approvedId).Text);
        Assert.Equal(4,applications.Single(item => item.Id == rejectedId).Status);
        Assert.Equal("Updated rejected text",applications.Single(item => item.Id == rejectedId).Text);
    }

    [PgFact]
    public async Task Alliance_public_member_rows_bind_and_private_votes_match_the_client_contract()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "alliance_public_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);

            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            var (actor,corp) = await Founder(rig,"Founder One","FIRST",store:store);
            var alliance = await store.CreateAllianceAsync(actor,Request,"first");
            var session = new Eve.Services.ClientSession { SessionId=77 };
            session.Set("charid",new Eve.Marshal.PyInt(actor));
            var service = new Eve.Services.Corporations.AllianceRegistryService(store,rig.Characters,() => rig.Now);
            Eve.Services.ServiceCall Call(string name,params Eve.Marshal.PyValue[] args) => new() { Session=session,Method=name,Args=args };
            var info = await service.GetAlliance(Call("GetAlliance",new Eve.Marshal.PyInt(alliance.Id)));
            var fields = Assert.IsType<Eve.Marshal.PyDict>(Assert.IsType<Eve.Marshal.PyObject>(info).Args);
            Assert.Equal(new Eve.Marshal.PyInt(1),fields.Entries.Single(e => e.Key == new Eve.Marshal.PyStr("memberCount")).Value);
            Assert.Equal(new Eve.Marshal.PyInt(corp.Id),fields.Entries.Single(e => e.Key == new Eve.Marshal.PyStr("executorCorpID")).Value);
            Assert.Equal(new Eve.Marshal.PyInt(0),fields.Entries.Single(e => e.Key == new Eve.Marshal.PyStr("currentPrimeHour")).Value);
            Assert.Equal(Eve.Marshal.PyNone.Instance,fields.Entries.Single(e => e.Key == new Eve.Marshal.PyStr("currentCapital")).Value);
            var publicRows = Assert.IsType<Eve.Marshal.PyObject>(await service.GetAllianceMembers(Call("GetAllianceMembers",new Eve.Marshal.PyInt(alliance.Id))));
            var publicFields = Assert.IsType<Eve.Marshal.PyDict>(publicRows.Args);
            var lines = Assert.IsType<Eve.Marshal.PyList>(publicFields.Entries.Single(e => e.Key == new Eve.Marshal.PyStr("lines")).Value);
            Assert.IsType<Eve.Marshal.PyNone>(Assert.IsType<Eve.Marshal.PyList>(Assert.Single(lines.Items)).Items[2]);
            var own = Assert.IsType<Eve.Marshal.PyObjectEx>(Eve.Marshal.MarshalCodec.Decode(Eve.Marshal.MarshalCodec.Encode(await service.GetMembers(Call("GetMembers")))));
            var packed = Assert.IsType<Eve.Marshal.PyPackedRow>(Assert.Single(own.Dict).Value);
            Assert.Equal(new Eve.Marshal.PyInt(corp.Id),packed.Values[2]);
            var bound = Assert.IsType<Eve.Marshal.PyTuple>(await service.MachoBindObject(Call("MachoBindObject",new Eve.Marshal.PyInt(alliance.Id),
                Eve.Services.Py.Tuple(new Eve.Marshal.PyStr("GetAlliance"),Eve.Services.Py.Tuple(),Eve.Marshal.PyNone.Instance))));
            Assert.Equal(Eve.Marshal.MarshalCodec.Encode(info),Eve.Marshal.MarshalCodec.Encode(bound.Items[1]));
            Assert.IsType<Eve.Marshal.PySubStruct>(bound.Items[0]);
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Election_threshold_ties_and_name_policy_follow_the_reference()
    {
        AllianceMember Member(long corp,long vote) => new(99,corp,0,vote);
        Assert.Equal((1L,false),AllianceRules.Elect([Member(1,1),Member(2,2)],1,false));
        Assert.Equal(((long?)null,true),AllianceRules.Elect([Member(1,1),Member(2,2)],null,false));
        Assert.Equal((2L,false),AllianceRules.Elect([Member(1,2),Member(2,2),Member(3,3)],1,false));
        Assert.Equal((1L,true),AllianceRules.Elect([Member(1,1),Member(2,2),Member(3,3)],1,false));
        Assert.Equal((1L,false),AllianceRules.Elect([Member(1,2),Member(2,2),Member(3,3)],1,true));
        Assert.Equal("ORB",AllianceRules.Validate(Request with { ShortName=" orb " }).ShortName);
        Assert.Throws<CorporationError>(() => AllianceRules.Validate(Request with { Name="----" }));
        Assert.Throws<CorporationError>(() => AllianceRules.Validate(Request with { ShortName="ABCDEF" }));
        Assert.Throws<CorporationError>(() => AllianceRules.Validate(Request with { Url=new string('a',2049) }));
    }

    [Fact]
    public void Alliance_and_corporation_registry_expose_application_methods()
    {
        var allianceRegistry = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(null!,null!,() => 0);
        Assert.True(allianceRegistry.HasMethod("GetApplications"));
        Assert.True(allianceRegistry.HasMethod("UpdateApplication"));

        var corpRegistry = (Eve.Services.IService)new Eve.Services.Corporations.CorpRegistryService(null!,null!,() => 0);
        Assert.Equal("corpRegistry", corpRegistry.Name);
        Assert.True(corpRegistry.HasMethod("ApplyToJoinAlliance"));
        Assert.True(corpRegistry.HasMethod("DeleteAllianceApplication"));
        Assert.True(corpRegistry.HasMethod("CreateAlliance"));
        Assert.True(corpRegistry.HasMethod("GetAllianceApplications"));
        Assert.True(corpRegistry.HasMethod("GetSuggestedAllianceShortNames"));
    }

    [PgFact]
    public async Task Alliance_registry_lists_pending_requests_and_executor_can_accept_or_reject_them()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor, _) = await Founder(rig, "Executor", "EXE");
        var (member, memberCorp) = await Founder(rig, "Member", "MEM");
        var (applicant, applicantCorp) = await Founder(rig, "Applicant", "APP");
        var (rejectedApplicant, rejectedCorp) = await Founder(rig, "Rejected", "REJ");
        var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "registry");
        var memberApplication = await rig.Store.ApplyToAllianceAsync(member, alliance.Id, "Join as member");
        await AcceptAllianceApplicationAsync(rig.Store, executor, memberApplication);
        rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs;
        Assert.Equal([memberCorp.Id], await ProcessAllianceDueAsync(rig.Store));

        var application = await rig.Store.ApplyToAllianceAsync(applicant, alliance.Id, "Please accept");
        var rejected = await rig.Store.ApplyToAllianceAsync(rejectedApplicant, alliance.Id, "Please reject");
        var scheduledJobs = new List<AllianceApplicationDueJob>();
        AllianceApplicationScheduleCallback schedule = (connection, transaction, job, _) =>
        {
            Assert.Same(connection, transaction.Connection);
            scheduledJobs.Add(job);
            return Task.CompletedTask;
        };
        var service = new Eve.Services.Corporations.AllianceRegistryService(
            rig.Store, rig.Characters, () => rig.Now, scheduleAllianceApplication: schedule);
        Eve.Services.ClientSession Session(long actor)
        {
            var session = new Eve.Services.ClientSession { SessionId = actor };
            session.Set("charid", new Eve.Marshal.PyInt(actor));
            return session;
        }
        Eve.Services.ServiceCall Call(long actor, string method, params Eve.Marshal.PyValue[] args) =>
            new() { Session = Session(actor), Method = method, Args = args };

        var executorCall = Call(executor, "GetApplications");
        var indexed = Assert.IsType<Eve.Marshal.PyObjectEx>(
            await ((Eve.Services.IService)service).CallAsync(executorCall));
        var applicantRow = Assert.IsType<Eve.Marshal.PyPackedRow>(
            indexed.Dict.Single(row => row.Key == new Eve.Marshal.PyInt(applicantCorp.Id)).Value);
        Assert.Equal(new Eve.Marshal.PyInt(alliance.Id), applicantRow.Values[0]);
        Assert.Equal(new Eve.Marshal.PyInt(applicantCorp.Id), applicantRow.Values[1]);
        Assert.Equal(new Eve.Marshal.PyWString("Please accept"), applicantRow.Values[2]);
        Assert.Equal(new Eve.Marshal.PyInt(1), applicantRow.Values[3]);

        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(member, "GetApplications")).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(member, "UpdateApplication", new Eve.Marshal.PyInt(applicantCorp.Id),
                new Eve.Marshal.PyWString("Please accept"), new Eve.Marshal.PyInt(4))).AsTask());

        var acceptCall = Call(executor, "UpdateApplication", new Eve.Marshal.PyInt(applicantCorp.Id),
            new Eve.Marshal.PyWString("Updated acceptance text"), new Eve.Marshal.PyInt(2));
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(acceptCall));
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(acceptCall));
        Assert.Equal(2, scheduledJobs.Count);
        Assert.All(scheduledJobs, job =>
        {
            Assert.Equal(application, job.ApplicationId);
            Assert.Equal(rig.Now + Eve.Corporations.AllianceRules.ApplicationDelayMs, job.DueAtMs);
        });
        await Assert.ThrowsAsync<CorporationError>(() => ((Eve.Services.IService)service).CallAsync(
            Call(executor, "UpdateApplication", new Eve.Marshal.PyInt(applicantCorp.Id),
                new Eve.Marshal.PyWString("Conflicting acceptance text"), new Eve.Marshal.PyInt(2))).AsTask());

        var rejectCall = Call(executor, "UpdateApplication", new Eve.Marshal.PyInt(rejectedCorp.Id),
            new Eve.Marshal.PyWString("Updated rejection text"), new Eve.Marshal.PyInt(4));
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(rejectCall));
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(rejectCall));
        await Assert.ThrowsAsync<CorporationError>(() => ((Eve.Services.IService)service).CallAsync(
            Call(executor, "UpdateApplication", new Eve.Marshal.PyInt(rejectedCorp.Id),
                new Eve.Marshal.PyWString("Conflicting rejection text"), new Eve.Marshal.PyInt(4))).AsTask());

        var applications = await rig.Store.AllianceApplicationsAsync(alliance.Id);
        Assert.Equal(2, applications.Single(item => item.Id == application).Status);
        Assert.Equal("Updated acceptance text", applications.Single(item => item.Id == application).Text);
        Assert.Equal(rig.Now + Eve.Corporations.AllianceRules.ApplicationDelayMs,
            applications.Single(item => item.Id == application).DueMs);
        Assert.Equal(4, applications.Single(item => item.Id == rejected).Status);
        Assert.Equal("Updated rejection text", applications.Single(item => item.Id == rejected).Text);
        Assert.True(applications.Single(item => item.Id == rejected).Archived);
        Assert.Null((await rig.Characters.GetAsync(applicant))!.AllianceId);
    }

    [PgFact]
    public async Task Alliance_registry_reads_leave_due_applications_for_the_durable_worker()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor, _) = await Founder(rig, "Due Executor", "DUEEX");
        var (applicant, applicantCorp) = await Founder(rig, "Due Applicant", "DUEAP");
        var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "due");
        var application = await rig.Store.ApplyToAllianceAsync(applicant, alliance.Id, "Join after a day");
        await AcceptAllianceApplicationAsync(rig.Store, executor, application);

        var legacyAffiliationCallbacks = 0;
        var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
            rig.Store, rig.Characters, () => rig.Now, (_, _) =>
            {
                legacyAffiliationCallbacks++;
                return Task.CompletedTask;
            });
        Eve.Services.ServiceCall Call(long actor, string method, params Eve.Marshal.PyValue[] args)
        {
            var session = new Eve.Services.ClientSession { SessionId = actor };
            session.Set("charid", new Eve.Marshal.PyInt(actor));
            return new() { Session = session, Method = method, Args = args };
        }

        rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs;
        await service.CallAsync(Call(executor, "GetAllianceMembers", new Eve.Marshal.PyInt(alliance.Id)));
        var applications = Assert.IsType<Eve.Marshal.PyObjectEx>(
            await service.CallAsync(Call(executor, "GetApplications")));
        var pending = Assert.IsType<Eve.Marshal.PyPackedRow>(
            applications.Dict.Single(row => row.Key == new Eve.Marshal.PyInt(applicantCorp.Id)).Value);
        Assert.Equal(new Eve.Marshal.PyInt(2), pending.Values[3]);
        Assert.Null((await rig.Characters.GetAsync(applicant))!.AllianceId);
        Assert.Single(await rig.Store.AllianceMembersAsync(alliance.Id));
        Assert.Equal(2, (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
        Assert.Equal(0, legacyAffiliationCallbacks);
    }

    [PgFact]
    public async Task Corp_registry_application_calls_use_session_corporation_for_create_and_withdrawal()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,executorCorp) = await Founder(rig,"Executor","EXE");
        var (applicant,applicantCorp) = await Founder(rig,"Applicant","APP");
        var outsider = await rig.CharacterAsync("Alliance outsider");
        var alliance = await rig.Store.CreateAllianceAsync(executor,Request,"corp-registry");
        var sessions = new Eve.Services.CharacterSessions();
        var executorSink = new PacketSink();
        var applicantSink = new PacketSink();
        Eve.Services.ClientSession Session(long actor,long corporation)
        {
            var sink = actor == executor ? executorSink : applicantSink;
            var session = new Eve.Services.ClientSession(sink) { SessionId=actor,CharacterId=actor };
            session.Set("corpid",new Eve.Marshal.PyInt(corporation));
            sessions.Enter(actor,session);
            return session;
        }
        var executorSession = Session(executor,executorCorp.Id);
        var applicantSession = Session(applicant,applicantCorp.Id);
        var outsiderSession = new Eve.Services.ClientSession { SessionId=outsider,CharacterId=outsider };
        sessions.Enter(outsider,outsiderSession);
        var service = new Eve.Services.Corporations.CorpRegistryService(rig.Store,rig.Characters,() => rig.Now,
            characterSessions:sessions);
        Eve.Services.ServiceCall Call(Eve.Services.ClientSession session,string method,params Eve.Marshal.PyValue[] args) =>
            new() { Session=session,Method=method,Args=args };

        var oversizedApplication = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() =>
            ((Eve.Services.IService)service).CallAsync(Call(applicantSession,"ApplyToJoinAlliance",
                new Eve.Marshal.PyInt(alliance.Id),new Eve.Marshal.PyWString(new string('x',1001)))).AsTask());
        Assert.Equal("ALLIANCE_APPLICATION_TEXT_TOO_LONG",oversizedApplication.Code);
        Assert.Empty(await rig.Store.AllianceApplicationsAsync(alliance.Id));

        Eve.Marshal.PyValue[] applicationArgs =
        [
            new Eve.Marshal.PyInt(alliance.Id),
            new Eve.Marshal.PyWString("Please join"),
            new Eve.Marshal.PyInt(executorCorp.Id)
        ];
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicantSession,"ApplyToJoinAlliance",applicationArgs)));
        applicantSession.RunAfterResponse();
        Assert.Equal(["OnAllianceApplicationChanged"],applicantSink.Packets.Select(NotificationName));
        Assert.Equal(["OnAllianceApplicationChanged"],executorSink.Packets.Select(NotificationName));
        var applications = await rig.Store.AllianceApplicationsAsync(alliance.Id);
        var application = Assert.Single(applications,item => item.CorporationId == applicantCorp.Id);
        Assert.Equal("Please join",application.Text);

        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(outsiderSession,"ApplyToJoinAlliance",new Eve.Marshal.PyInt(alliance.Id),
                new Eve.Marshal.PyWString("Impersonated request"))).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(applicantSession,"ApplyToJoinAlliance",new Eve.Marshal.PyInt(alliance.Id),
                new Eve.Marshal.PyWString("Please join again"))).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(outsiderSession,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id))).AsTask());

        executorSink.Packets.Clear();
        applicantSink.Packets.Clear();
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(executorSession,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id),
                new Eve.Marshal.PyInt(applicantCorp.Id))));
        Assert.Contains(await rig.Store.AllianceApplicationsAsync(alliance.Id),
            item => item.CorporationId == applicantCorp.Id);

        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicantSession,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id))));
        applicantSession.RunAfterResponse();
        Assert.Equal(["OnAllianceApplicationChanged"],applicantSink.Packets.Select(NotificationName));
        Assert.Equal(["OnAllianceApplicationChanged"],executorSink.Packets.Select(NotificationName));
        Assert.DoesNotContain(await rig.Store.AllianceApplicationsAsync(alliance.Id),
            item => item.CorporationId == applicantCorp.Id);
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicantSession,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id))));

        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicantSession,"ApplyToJoinAlliance",new Eve.Marshal.PyInt(alliance.Id),
                new Eve.Marshal.PyWString("Please join again"))));
        applicantSession.RunAfterResponse();
        var rejected = Assert.Single(await rig.Store.AllianceApplicationsAsync(alliance.Id),
            item => item.CorporationId == applicantCorp.Id);
        await rig.Store.RespondToAllianceAsync(executor,rejected.Id,"Rejected request",4);
        Assert.True((await rig.Store.AllianceApplicationsAsync(alliance.Id))
            .Single(item => item.Id == rejected.Id).Archived);
        executorSink.Packets.Clear();
        applicantSink.Packets.Clear();
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicantSession,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id))));
        applicantSession.RunAfterResponse();
        Assert.Equal(["OnAllianceApplicationChanged"],applicantSink.Packets.Select(NotificationName));
        Assert.Equal(["OnAllianceApplicationChanged"],executorSink.Packets.Select(NotificationName));
        Assert.DoesNotContain(await rig.Store.AllianceApplicationsAsync(alliance.Id),
            item => item.CorporationId == applicantCorp.Id);
    }

    [Fact]
    public void Alliance_core_registry_methods_cover_reference_calls()
    {
        var alliance = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(null!,null!,() => 0);
        foreach (var method in new[]
        {
            "IsAllianceLocal","GetRankedAlliances","GetEmploymentRecord","GetDaysInAlliance",
            "GetAllianceMembersOlderThan","UpdateAlliance","GetAlliancePublicInfo",
            "GetRelationships","SetRelationship","DeleteRelationship","GetContactList","GetAllianceContacts",
            "AddAllianceContact","EditAllianceContact","RemoveAllianceContacts","EditContactsRelationshipID",
            "GetLabels","CreateLabel","DeleteLabel","EditLabel","AssignLabels","RemoveLabels",
            "SetPrimeHour","GetPrimeTimeInfo","GetCapitalSystemInfo","SetCapitalSystem","CancelCapitalSystemTransition"
        }) Assert.True(alliance.HasMethod(method),method);

        var corporation = (Eve.Services.IService)new Eve.Services.Corporations.CorpRegistryService(null!,null!,() => 0);
        foreach (var method in new[] { "CreateAlliance","GetAllianceApplications","GetSuggestedAllianceShortNames" })
            Assert.True(corporation.HasMethod(method),method);
    }

    [PgFact]
    public async Task Alliance_social_contacts_labels_and_relationships_persist_with_executor_checks()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var corporationSchema = "alliance_social_corp_" + suffix;
        var schedulerSchema = "alliance_social_sched_" + suffix;
        var eventsSchema = "alliance_social_events_" + suffix;
        var relationsSchema = "alliance_social_relations_" + suffix;
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,corporationSchema);

            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source,schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source,eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source,relationsSchema);
            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,corporationSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source,() => rig.Now,schema:schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source,scheduler,schema:eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source,scheduler,events,relationsSchema);
            var (executor,_) = await Founder(rig,"Social Executor","SOCEX",store:store);
            var (outsider,outsiderCorp) = await Founder(rig,"Social Outsider","SOCOUT",store:store);
            var alliance = await store.CreateAllianceAsync(executor,Request,"social");
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                store,rig.Characters,() => rig.Now,relations:relations);

            Eve.Services.ServiceCall Call(long actor,string method,params Eve.Marshal.PyValue[] args)
            {
                var session = new Eve.Services.ClientSession { SessionId=actor };
                session.Set("charid",new Eve.Marshal.PyInt(actor));
                return new() { Session=session,Method=method,Args=args };
            }

            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(executor,"SetRelationship",
                new Eve.Marshal.PyInt(8),new Eve.Marshal.PyInt(outsiderCorp.Id))));
            var relationships = Assert.IsType<Eve.Marshal.PyObjectEx>(
                await service.CallAsync(Call(executor,"GetRelationships")));
            var relationshipRow = Assert.IsType<Eve.Marshal.PyPackedRow>(Assert.Single(relationships.Dict).Value);
            Assert.Equal(new Eve.Marshal.PyInt(outsiderCorp.Id),relationships.Dict.Single().Key);
            Assert.Equal(new Eve.Marshal.PyInt(8),relationshipRow.Values[1]);
            var malformedStanding = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() =>
                service.CallAsync(Call(executor,"SetRelationship",new Eve.Marshal.PyStr("bad"),new Eve.Marshal.PyInt(outsiderCorp.Id))).AsTask());
            Assert.Equal("ALLIANCE_RELATIONSHIP_INVALID",malformedStanding.Code);
            var unchangedStanding = Assert.IsType<Eve.Marshal.PyObjectEx>(
                await service.CallAsync(Call(executor,"GetRelationships")));
            Assert.Equal(new Eve.Marshal.PyInt(8),Assert.IsType<Eve.Marshal.PyPackedRow>(Assert.Single(unchangedStanding.Dict).Value).Values[1]);
            await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(Call(outsider,"SetRelationship",
                new Eve.Marshal.PyInt(3),new Eve.Marshal.PyInt(outsiderCorp.Id))).AsTask());
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(executor,"DeleteRelationship",
                new Eve.Marshal.PyInt(outsiderCorp.Id))));
            Assert.Null(await relations.GetAsync(Eve.Relations.RelationKinds.Standing.Name,alliance.Id,outsiderCorp.Id));

            await service.CallAsync(Call(executor,"AddAllianceContact",new Eve.Marshal.PyInt(outsiderCorp.Id),new Eve.Marshal.PyInt(5)));
            await service.CallAsync(Call(executor,"EditAllianceContact",new Eve.Marshal.PyInt(outsiderCorp.Id),new Eve.Marshal.PyInt(6)));
            await service.CallAsync(Call(executor,"EditContactsRelationshipID",
                new Eve.Marshal.PyList([new Eve.Marshal.PyInt(outsiderCorp.Id)]),new Eve.Marshal.PyInt(7)));
            var labelId = Assert.IsType<Eve.Marshal.PyInt>(await service.CallAsync(Call(executor,"CreateLabel",
                new Eve.Marshal.PyWString("operations"),new Eve.Marshal.PyInt(0x123456)))).Value;
            var labelMask = 1L << ((int)labelId - 1);
            await service.CallAsync(Call(executor,"AssignLabels",
                new Eve.Marshal.PyList([new Eve.Marshal.PyInt(outsiderCorp.Id)]),new Eve.Marshal.PyInt(labelMask)));
            var contact = Assert.Single(await store.AllianceContactsAsync(alliance.Id));
            Assert.Equal(7,contact.RelationshipId);
            Assert.Equal(labelMask,contact.LabelMask);
            Assert.Single(Assert.IsType<Eve.Marshal.PyDict>(await service.CallAsync(Call(executor,"GetLabels"))).Entries);
            await service.CallAsync(Call(executor,"EditLabel",new Eve.Marshal.PyInt(labelId),
                new Eve.Marshal.PyWString("operations edited"),new Eve.Marshal.PyInt(0x654321)));
            var edited = Assert.Single(await store.AllianceContactLabelsAsync(alliance.Id));
            Assert.Equal("operations edited",edited.Name);
            Assert.Equal(0x654321,edited.Color);

            var contacts = await service.CallAsync(Call(executor,"GetAllianceContacts"));
            Assert.Equal(Eve.Marshal.MarshalCodec.Encode(contacts),
                Eve.Marshal.MarshalCodec.Encode(await service.CallAsync(Call(executor,"GetContactList"))));
            await service.CallAsync(Call(executor,"RemoveLabels",
                new Eve.Marshal.PyList([new Eve.Marshal.PyInt(outsiderCorp.Id)]),new Eve.Marshal.PyInt(labelMask)));
            Assert.Equal(0,Assert.Single(await store.AllianceContactsAsync(alliance.Id)).LabelMask);
            await service.CallAsync(Call(executor,"AssignLabels",
                new Eve.Marshal.PyList([new Eve.Marshal.PyInt(outsiderCorp.Id)]),new Eve.Marshal.PyInt(labelMask)));
            await service.CallAsync(Call(executor,"DeleteLabel",new Eve.Marshal.PyInt(labelId)));
            Assert.Equal(0,Assert.Single(await store.AllianceContactsAsync(alliance.Id)).LabelMask);
            Assert.Empty(await store.AllianceContactLabelsAsync(alliance.Id));
            var oversizedAllianceLabel = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(async () =>
                await service.CallAsync(Call(executor,"CreateLabel",new Eve.Marshal.PyWString(new string('x',41)),new Eve.Marshal.PyInt(0))));
            Assert.Equal("ALLIANCE_CONTACT_LABEL_NAME_LIMIT_EXCEEDED",oversizedAllianceLabel.Code);
            var maximumAllianceLabelName = new string('A',40);
            var maximumAllianceLabelId = Assert.IsType<Eve.Marshal.PyInt>(await service.CallAsync(Call(executor,"CreateLabel",
                new Eve.Marshal.PyWString(maximumAllianceLabelName),new Eve.Marshal.PyInt(0)))).Value;
            var maximumAllianceLabel = Assert.Single(await store.AllianceContactLabelsAsync(alliance.Id));
            Assert.Equal(maximumAllianceLabelName,maximumAllianceLabel.Name);
            var oversizedAllianceLabelEdit = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(async () =>
                await service.CallAsync(Call(executor,"EditLabel",new Eve.Marshal.PyInt(maximumAllianceLabelId),
                    new Eve.Marshal.PyWString(maximumAllianceLabelName + "x"),Eve.Marshal.PyNone.Instance)));
            Assert.Equal("ALLIANCE_CONTACT_LABEL_NAME_LIMIT_EXCEEDED",oversizedAllianceLabelEdit.Code);
            Assert.Equal(maximumAllianceLabelName,Assert.Single(await store.AllianceContactLabelsAsync(alliance.Id)).Name);
            await service.CallAsync(Call(executor,"DeleteLabel",new Eve.Marshal.PyInt(maximumAllianceLabelId)));
            Assert.Empty(await store.AllianceContactLabelsAsync(alliance.Id));
            var oversizedContacts = new Eve.Marshal.PyList(Enumerable.Range(1, 501)
                .Select(id => (Eve.Marshal.PyValue)new Eve.Marshal.PyInt(id)).ToArray());
            var bulkRefusal = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(async () =>
                await service.CallAsync(Call(executor,"RemoveAllianceContacts",oversizedContacts)));
            Assert.Equal("ALLIANCE_BULK_LIMIT_EXCEEDED",bulkRefusal.Code);

            await service.CallAsync(Call(executor,"RemoveAllianceContacts",
                new Eve.Marshal.PyList([new Eve.Marshal.PyInt(outsiderCorp.Id)])));
            Assert.Empty(await store.AllianceContactsAsync(alliance.Id));
        }
        finally
        {
            foreach (var schema in new[] { corporationSchema, schedulerSchema, eventsSchema, relationsSchema })
            {
                await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Alliance_registry_SetPrimeHour_uses_the_next_vulnerability_window()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "alliance_prime_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);

            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            var actor = await rig.CharacterAsync("Prime Executor",2_000_000_000m);
            await store.CreateAsync(actor,new("Prime Executor Corporation","PRIMEX","","",0),"prime-corp-"+actor);
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await rig.Skills.GrantAsync(connection,tx,actor,[new CharacterSkill(3732,5,1024000)]);
                await tx.CommitAsync();
            }
            var alliance = await store.CreateAllianceAsync(actor,Request,"prime-alliance-"+actor);
            var windowEnd = checked(store.VoteNowFiletime + TimeSpan.FromHours(5).Ticks);
            var sov = new Eve.Services.Corporations.AllianceSovSnapshot(
                new Dictionary<long,long?>(),
                new Dictionary<long,(long? AllianceId,long? HubId)>(),
                windowEnd);
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                store,rig.Characters,() => rig.Now,
                readSovSnapshot: (_, _) => Task.FromResult<Eve.Services.Corporations.AllianceSovSnapshot?>(sov));

            Eve.Services.ServiceCall Call(long character,string method,params Eve.Marshal.PyValue[] args)
            {
                var session = new Eve.Services.ClientSession { SessionId=character };
                session.Set("charid",new Eve.Marshal.PyInt(character));
                return new() { Session=session,Method=method,Args=args };
            }

            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(actor,"SetPrimeHour",new Eve.Marshal.PyInt(17))));
            await using (var state = rig.Source.CreateCommand($"SELECT current_prime_hour,new_prime_hour,new_prime_hour_valid_after_filetime FROM {schema}.alliances WHERE id=$1"))
            {
                state.Parameters.Add(new Npgsql.NpgsqlParameter { Value=alliance.Id });
                await using var reader = await state.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)0,reader.GetInt16(0));
                Assert.Equal((short)17,reader.GetInt16(1));
                Assert.Equal(windowEnd,reader.GetInt64(2));
            }

            var primeInfo = Assert.IsType<Eve.Marshal.PyObject>(await service.CallAsync(Call(actor,"GetPrimeTimeInfo")));
            var primeFields = Assert.IsType<Eve.Marshal.PyDict>(primeInfo.Args);
            Assert.Equal(new Eve.Marshal.PyInt(0),primeFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("currentPrimeHour")).Value);
            Assert.Equal(new Eve.Marshal.PyInt(17),primeFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("newPrimeHour")).Value);
            Assert.Equal(new Eve.Marshal.PyLong(windowEnd),primeFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("newPrimeHourValidAfter")).Value);

            await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(Call(actor,"SetPrimeHour",new Eve.Marshal.PyInt(24))).AsTask());
            var outsider = await rig.CharacterAsync("Prime Outsider");
            await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(Call(outsider,"SetPrimeHour",new Eve.Marshal.PyInt(18))).AsTask());
            var unwired = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(store,rig.Characters,() => rig.Now);
            await Assert.ThrowsAnyAsync<Exception>(() => unwired.CallAsync(Call(actor,"SetPrimeHour",new Eve.Marshal.PyInt(18))).AsTask());
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

[PgFact]
    public async Task Alliance_registry_capital_transition_uses_seven_days_and_the_single_system_exception()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "alliance_capital_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);

            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            var actor = await rig.CharacterAsync("Capital Executor",2_000_000_000m);
            await store.CreateAsync(actor,new("Capital Executor Corporation","CAPEX","","",0),"capital-corp-"+actor);
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await rig.Skills.GrantAsync(connection,tx,actor,[new CharacterSkill(3732,5,1024000)]);
                await tx.CommitAsync();
            }
            var alliance = await store.CreateAllianceAsync(actor,Request,"capital-alliance-"+actor);
            var sov = new Eve.Services.Corporations.AllianceSovSnapshot(
                new Dictionary<long,long?> { [30000142] = alliance.Id, [30000144] = alliance.Id },
                new Dictionary<long,(long? AllianceId,long? HubId)>
                {
                    [30000142] = (alliance.Id,90001),
                    [30000144] = (alliance.Id,90002)
                },
                null);
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                store,rig.Characters,() => rig.Now,
                readSovSnapshot: (_, _) => Task.FromResult<Eve.Services.Corporations.AllianceSovSnapshot?>(sov));

            Eve.Services.ServiceCall Call(string method,params Eve.Marshal.PyValue[] args)
            {
                var session = new Eve.Services.ClientSession { SessionId=actor };
                session.Set("charid",new Eve.Marshal.PyInt(actor));
                return new() { Session=session,Method=method,Args=args };
            }

            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call("SetCapitalSystem",new Eve.Marshal.PyInt(30000142))));
            var due = checked(store.VoteNowFiletime + 7 * TimeSpan.TicksPerDay);
            await using (var state = rig.Source.CreateCommand($"SELECT current_capital_system_id,new_capital_system_id,new_capital_system_valid_after_filetime FROM {schema}.alliances WHERE id=$1"))
            {
                state.Parameters.Add(new Npgsql.NpgsqlParameter { Value=alliance.Id });
                await using var reader = await state.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(30000142L,reader.GetInt64(1));
                Assert.Equal(due,reader.GetInt64(2));
            }

            var info = Assert.IsType<Eve.Marshal.PyObject>(await service.CallAsync(Call("GetCapitalSystemInfo")));
            var fields = Assert.IsType<Eve.Marshal.PyDict>(info.Args);
            Assert.Equal(new Eve.Marshal.PyLong(30000142),fields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("newCapitalSystem")).Value);
            Assert.Equal(new Eve.Marshal.PyLong(due),fields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("newCapitalSystemValidAfter")).Value);

            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call("CancelCapitalSystemTransition")));
            await using (var state = rig.Source.CreateCommand($"SELECT current_capital_system_id,new_capital_system_id,new_capital_system_valid_after_filetime FROM {schema}.alliances WHERE id=$1"))
            {
                state.Parameters.Add(new Npgsql.NpgsqlParameter { Value=alliance.Id });
                await using var reader = await state.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.True(reader.IsDBNull(1));
                Assert.Equal(0L,reader.GetInt64(2));
            }

            sov = new Eve.Services.Corporations.AllianceSovSnapshot(
                new Dictionary<long,long?> { [30000142] = alliance.Id },
                new Dictionary<long,(long? AllianceId,long? HubId)> { [30000142] = (alliance.Id,90001) },
                null);
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call("SetCapitalSystem",new Eve.Marshal.PyInt(30000142))));
            await using (var state = rig.Source.CreateCommand($"SELECT current_capital_system_id,new_capital_system_id,new_capital_system_valid_after_filetime FROM {schema}.alliances WHERE id=$1"))
            {
                state.Parameters.Add(new Npgsql.NpgsqlParameter { Value=alliance.Id });
                await using var reader = await state.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(30000142L,reader.GetInt64(0));
                Assert.True(reader.IsDBNull(1));
                Assert.Equal(0L,reader.GetInt64(2));
            }

            sov = new Eve.Services.Corporations.AllianceSovSnapshot(
                new Dictionary<long,long?> { [30000142] = alliance.Id, [30000144] = alliance.Id },
                new Dictionary<long,(long? AllianceId,long? HubId)>
                {
                    [30000142] = (alliance.Id,90001),
                    [30000144] = (alliance.Id,90002),
                    [999999999] = (alliance.Id,90003)
                },
                null);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.CallAsync(Call("SetCapitalSystem",new Eve.Marshal.PyLong(999999999))).AsTask());
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Alliance_membership_reads_and_public_info_updates_match_reference_shapes()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,_) = await Founder(rig,"Read Executor","RDEXE");
        var (member,memberCorp) = await Founder(rig,"Read Member","RDMEM");
        var alliance = await rig.Store.CreateAllianceAsync(executor,Request,"reads");
        var application = await rig.Store.ApplyToAllianceAsync(member,alliance.Id,"Read-side join");
        await AcceptAllianceApplicationAsync(rig.Store, executor, application);
        rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs;
        Assert.Equal([memberCorp.Id],await ProcessAllianceDueAsync(rig.Store));
        rig.Now += 2 * 86400000L;

        var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(rig.Store,rig.Characters,() => rig.Now);
        Eve.Services.ServiceCall Call(long actor,string method,params Eve.Marshal.PyValue[] args)
        {
            var session = new Eve.Services.ClientSession { SessionId=actor };
            session.Set("charid",new Eve.Marshal.PyInt(actor));
            return new() { Session=session,Method=method,Args=args };
        }

        Assert.Equal(new Eve.Marshal.PyInt(1),await service.CallAsync(Call(executor,"IsAllianceLocal",new Eve.Marshal.PyInt(alliance.Id))));
        Assert.Equal(new Eve.Marshal.PyInt(0),await service.CallAsync(Call(executor,"IsAllianceLocal",new Eve.Marshal.PyInt(999999999))));
        Assert.Equal(new Eve.Marshal.PyInt(2),await service.CallAsync(Call(executor,"GetDaysInAlliance",
            new Eve.Marshal.PyInt(alliance.Id),new Eve.Marshal.PyInt(memberCorp.Id))));
        var older = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Call(executor,"GetAllianceMembersOlderThan",
            new Eve.Marshal.PyInt(alliance.Id),new Eve.Marshal.PyInt(2))));
        Assert.Contains(new Eve.Marshal.PyInt(memberCorp.Id),older.Items);
        var invalidMinimumDays = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() =>
            service.CallAsync(Call(executor,"GetAllianceMembersOlderThan",new Eve.Marshal.PyInt(alliance.Id),new Eve.Marshal.PyInt(-1))).AsTask());
        Assert.Equal("ALLIANCE_MEMBERSHIP_DAYS_INVALID",invalidMinimumDays.Code);

        var ranked = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Call(executor,"GetRankedAlliances",new Eve.Marshal.PyInt(10))));
        var invalidRankLimit = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() =>
            service.CallAsync(Call(executor,"GetRankedAlliances",new Eve.Marshal.PyInt(-1))).AsTask());
        Assert.Equal("ALLIANCE_RANKED_LIMIT_INVALID",invalidRankLimit.Code);
        var malformedRankLimit = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() =>
            service.CallAsync(Call(executor,"GetRankedAlliances",new Eve.Marshal.PyStr("all"))).AsTask());
        Assert.Equal("ALLIANCE_RANKED_LIMIT_INVALID",malformedRankLimit.Code);
        var row = Assert.IsType<Eve.Marshal.PyObject>(Assert.Single(ranked.Items));
        var rowFields = Assert.IsType<Eve.Marshal.PyDict>(row.Args);
        var header = Assert.IsType<Eve.Marshal.PyTuple>(rowFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("header")).Value);
        Assert.Equal(new[] { "allianceID","allianceName","shortName","executorCorpID","creatorCorpID","creatorCharID","warFactionID",
            "description","url","startDate","memberCount","dictatorial","allowWar","currentCapital","currentPrimeHour",
            "newPrimeHour","newPrimeHourValidAfter","deleted" },header.Items.Select(value => Assert.IsType<Eve.Marshal.PyStr>(value).Value));
        var values = Assert.IsType<Eve.Marshal.PyTuple>(rowFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("line")).Value);
        Assert.Equal(new Eve.Marshal.PyInt(2),values.Items[10]);

        var history = Assert.IsType<Eve.Marshal.PyObject>(await service.CallAsync(Call(executor,"GetEmploymentRecord",new Eve.Marshal.PyInt(memberCorp.Id))));
        var historyFields = Assert.IsType<Eve.Marshal.PyDict>(history.Args);
        var historyRows = Assert.IsType<Eve.Marshal.PyList>(historyFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("lines")).Value);
        Assert.Equal(2,historyRows.Items.Count);
        Assert.Equal(new Eve.Marshal.PyInt(alliance.Id),Assert.IsType<Eve.Marshal.PyList>(historyRows.Items[0]).Items[0]);
        var allianceHistory = Assert.IsType<Eve.Marshal.PyObject>(await service.CallAsync(Call(executor,"GetEmploymentRecord",new Eve.Marshal.PyInt(alliance.Id))));
        var allianceHistoryFields = Assert.IsType<Eve.Marshal.PyDict>(allianceHistory.Args);
        Assert.Single(Assert.IsType<Eve.Marshal.PyList>(allianceHistoryFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("lines")).Value).Items);

        Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(executor,"UpdateAlliance",
            new Eve.Marshal.PyWString("Updated description"),new Eve.Marshal.PyWString("https://example.invalid"))));
        var publicInfo = Assert.IsType<Eve.Marshal.PyObject>(await service.CallAsync(Call(executor,"GetAlliancePublicInfo",new Eve.Marshal.PyInt(alliance.Id))));
        var publicFields = Assert.IsType<Eve.Marshal.PyDict>(publicInfo.Args);
        Assert.Equal(new Eve.Marshal.PyStr("Updated description"),publicFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("description")).Value);
        Assert.Equal(new Eve.Marshal.PyStr("https://example.invalid"),publicFields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("url")).Value);
        await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(Call(member,"UpdateAlliance",
            new Eve.Marshal.PyWString("Unauthorized"),new Eve.Marshal.PyWString(""))).AsTask());
        await Assert.ThrowsAsync<CorporationError>(async () => await rig.Store.UpdateAllianceAsync(executor,alliance.Id,
            string.Concat(Enumerable.Repeat("😀",2501)),""));
    }

    [PgFact]
    public async Task Alliance_sov_and_owner_queries_return_rows_from_their_authoritative_sources()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await Founder(rig, "Alliance Owner Reader", "AOWR");
        var alliance = await rig.Store.CreateAllianceAsync(actor, Request, "alliance-owner-reader");
        const long systemId = 30000142;
        const long hubId = 9000042;
        const long teamId = 990001;
        var filetime = DateTimeOffset.FromUnixTimeMilliseconds(rig.Now).ToFileTime();
        var sovereignty = new Eve.Services.Corporations.AllianceSovSnapshot(
            new Dictionary<long, long?> { [systemId] = alliance.Id },
            new Dictionary<long, (long? AllianceId, long? HubId)>
            {
                [systemId] = (alliance.Id, hubId)
            },
            null)
        {
            ClaimStructureIdsBySolarSystemId = new Dictionary<long, long?> { [systemId] = hubId },
            StructuresBySolarSystemId = new Dictionary<long, IReadOnlyList<Eve.Services.Corporations.AllianceSovStructure>>
            {
                [systemId] =
                [
                    new Eve.Services.Corporations.AllianceSovStructure(
                        hubId, systemId, corporation.Id, alliance.Id, 1, filetime,
                        new Dictionary<long, double> { [teamId] = 42.5 },
                        filetime + TimeSpan.FromHours(1).Ticks,
                        filetime + TimeSpan.FromHours(2).Ticks,
                        1.25)
                ]
            }
        };
        var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
            rig.Store, rig.Characters, () => rig.Now,
            readSovSnapshot: (_, _) => Task.FromResult<Eve.Services.Corporations.AllianceSovSnapshot?>(sovereignty),
            readStaticOwners: (ownerIds, _) => Task.FromResult<IReadOnlyDictionary<long,
                Eve.Services.Corporations.AllianceOwnerLookupRecord>>(
                new Dictionary<long, Eve.Services.Corporations.AllianceOwnerLookupRecord>
                {
                    [500001] = new(500001, "Caldari State", Eve.Services.Corporations.EveOwnerTypeIds.Faction, 0)
                }));

        var session = new Eve.Services.ClientSession { SessionId = actor, CharacterId = actor };
        session.Set("corpid", new Eve.Marshal.PyInt(corporation.Id));
        session.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
        var sovResult = Assert.IsType<Eve.Marshal.PyTuple>(await service.CallAsync(new Eve.Services.ServiceCall
        {
            Session = session,
            Method = "GetAllianceSovereigntyStructuresInfo",
            Args = []
        }));
        var tcuRows = Assert.IsType<Eve.Marshal.PyList>(sovResult.Items[0]);
        var hubRows = Assert.IsType<Eve.Marshal.PyList>(sovResult.Items[1]);
        var campaignRows = Assert.IsType<Eve.Marshal.PyList>(sovResult.Items[2]);
        var tcu = Assert.Single(tcuRows.Items);
        var hub = Assert.Single(hubRows.Items);
        var campaign = Assert.Single(campaignRows.Items);
        Eve.Marshal.PyValue RowField(Eve.Marshal.PyValue row, string name)
        {
            var fields = Assert.IsType<Eve.Marshal.PyDict>(Assert.IsType<Eve.Marshal.PyObject>(row).Args);
            return fields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr(name)).Value;
        }
        Eve.Marshal.PyTuple OwnerLine(Eve.Marshal.PyValue row)
        {
            var fields = Assert.IsType<Eve.Marshal.PyDict>(Assert.IsType<Eve.Marshal.PyObject>(row).Args);
            return Assert.IsType<Eve.Marshal.PyTuple>(
                fields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("line")).Value);
        }
        Assert.Equal(new Eve.Marshal.PyInt(hubId), RowField(tcu, "structureID"));
        Assert.DoesNotContain(Assert.IsType<Eve.Marshal.PyDict>(Assert.IsType<Eve.Marshal.PyObject>(tcu).Args).Entries,
            entry => entry.Key == new Eve.Marshal.PyStr("corporationID"));
        Assert.Equal(new Eve.Marshal.PyInt(corporation.Id), RowField(hub, "corporationID"));
        Assert.Equal(new Eve.Marshal.PyLong(filetime), RowField(hub, "campaignStartTime"));
        Assert.Equal(new Eve.Marshal.PyInt(teamId), RowField(campaign, "teamID"));
        Assert.Equal(Eve.Services.Py.Number(42.5), RowField(campaign, "score"));

        var owners = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(new Eve.Services.ServiceCall
        {
            Session = session,
            Method = "GetEveOwners",
            Args =
            [
                Eve.Services.Py.List(
                    new Eve.Marshal.PyInt(actor),
                    new Eve.Marshal.PyInt(corporation.Id),
                    new Eve.Marshal.PyInt(alliance.Id),
                    new Eve.Marshal.PyInt(500001),
                    new Eve.Marshal.PyInt(999999999))
            ]
        }));
        Assert.Equal(4, owners.Items.Count);
        var factionOwner = OwnerLine(owners.Items[3]);
        Assert.Equal(new Eve.Marshal.PyInt(500001), factionOwner.Items[0]);
        Assert.Equal(new Eve.Marshal.PyWString("Caldari State"), factionOwner.Items[1]);
        Assert.Equal(new Eve.Marshal.PyInt(Eve.Services.Corporations.EveOwnerTypeIds.Faction),
            factionOwner.Items[2]);
        Assert.Equal(Eve.Marshal.PyNone.Instance, factionOwner.Items[4]);

        var duplicateOwners = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(new Eve.Services.ServiceCall
        {
            Session = session,
            Method = "GetEveOwners",
            Args = [Eve.Services.Py.List(new Eve.Marshal.PyInt(actor),new Eve.Marshal.PyInt(actor))]
        }));
        Assert.Single(duplicateOwners.Items);
        var oversizedOwners = new Eve.Marshal.PyList(Enumerable.Range(1,501)
            .Select(id => (Eve.Marshal.PyValue)new Eve.Marshal.PyInt(id)).ToArray());
        var bulkRefusal = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(async () =>
            await service.CallAsync(new Eve.Services.ServiceCall
            {
                Session = session,
                Method = "GetEveOwners",
                Args = [oversizedOwners]
            }));
        Assert.Equal("ALLIANCE_BULK_LIMIT_EXCEEDED",bulkRefusal.Code);
    }

    [PgFact]
    public async Task Alliance_departure_schedules_sovereignty_transfer_to_the_active_executor()
    {
        await using var rig = await Rig.CreateAsync();
        var schedulerSchema = "alliance_sov_transfer_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var (executor, executorCorp) = await Founder(rig, "SOV Executor", "SOVEX");
            var (leaver, leaverCorp) = await Founder(rig, "SOV Leaver", "SOVLV");
            var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "alliance-sov-transfer");
            var application = await rig.Store.ApplyToAllianceAsync(leaver, alliance.Id, "Join for sovereignty");
            var accepted = await AcceptAllianceApplicationAsync(rig.Store, executor, application);
            rig.Now = accepted.DueMs!.Value;
            Assert.Equal([leaverCorp.Id], await ProcessAllianceDueAsync(rig.Store));

            var transfers = new List<(long AllianceId, long FromCorporationId, long ToCorporationId)>();
            var worker = new Eve.Services.Corporations.AllianceSovereigntyTransferWorker(
                rig.Store, scheduler, () => rig.Now);
            worker.TransferSovereigntyAsync = (allianceId, fromCorporationId, toCorporationId, _) =>
            {
                transfers.Add((allianceId, fromCorporationId, toCorporationId));
                return Task.FromResult(true);
            };
            await worker.RegisterAsync();

            var departure = await rig.Store.RemoveAllianceMemberAsync(
                alliance.Id, leaverCorp.Id, leaver, "self-leave", rig.Now,
                static (_, _, _, _, _) => Task.CompletedTask);
            Assert.False(departure.Duplicate);
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal([(alliance.Id, leaverCorp.Id, executorCorp.Id)], transfers);
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Alliance_departure_transfer_still_runs_if_leaver_joins_a_different_alliance_before_job()
    {
        await using var rig = await Rig.CreateAsync();
        var schedulerSchema = "alliance_sov_transfer_rejoin_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var (executor, executorCorp) = await Founder(rig, "Original SOV Executor", "OSVEX");
            var (leaver, leaverCorp) = await Founder(rig, "Alliance Leaver", "OSVLV");
            var (newExecutor, _) = await Founder(rig, "New SOV Executor", "NSVEX");
            var originalAlliance = await rig.Store.CreateAllianceAsync(executor,
                new Eve.Corporations.NewAlliance("Original SOV Alliance", "OSOVA", "Original.", ""),
                "original-sov-owner");
            var newAlliance = await rig.Store.CreateAllianceAsync(newExecutor,
                new Eve.Corporations.NewAlliance("New SOV Alliance", "NSOVA", "New.", ""),
                "new-sov-owner");
            var worker = new Eve.Services.Corporations.AllianceSovereigntyTransferWorker(
                rig.Store, scheduler, () => rig.Now);
            var transfers = new List<(long AllianceId, long FromCorporationId, long ToCorporationId)>();
            worker.TransferSovereigntyAsync = (allianceId, fromCorporationId, toCorporationId, _) =>
            {
                transfers.Add((allianceId, fromCorporationId, toCorporationId));
                return Task.FromResult(true);
            };
            await worker.RegisterAsync();

            var originalApplication = await rig.Store.ApplyToAllianceAsync(
                leaver, originalAlliance.Id, "Join original alliance");
            var originalAccepted = await AcceptAllianceApplicationAsync(
                rig.Store, executor, originalApplication);
            rig.Now = originalAccepted.DueMs!.Value;
            Assert.Equal([leaverCorp.Id], await ProcessAllianceDueAsync(rig.Store));
            await rig.Store.RemoveAllianceMemberAsync(
                originalAlliance.Id, leaverCorp.Id, leaver, "self-leave", rig.Now,
                static (_, _, _, _, _) => Task.CompletedTask);

            var newApplication = await rig.Store.ApplyToAllianceAsync(
                leaver, newAlliance.Id, "Join another alliance before SOV transfer");
            var newAccepted = await AcceptAllianceApplicationAsync(rig.Store, newExecutor, newApplication);
            rig.Now = newAccepted.DueMs!.Value;
            Assert.Equal([leaverCorp.Id], await ProcessAllianceDueAsync(rig.Store));

            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal([(originalAlliance.Id, leaverCorp.Id, executorCorp.Id)], transfers);
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Alliance_departure_transfer_waits_until_the_suspended_alliance_has_an_executor()
    {
        await using var rig = await Rig.CreateAsync();
        var schedulerSchema = "alliance_sov_transfer_suspended_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var (executor, executorCorp) = await Founder(rig, "Suspended SOV Executor", "SSVEX");
            var (leaver, leaverCorp) = await Founder(rig, "Suspended SOV Leaver", "SSVLV");
            var alliance = await rig.Store.CreateAllianceAsync(
                executor,
                new Eve.Corporations.NewAlliance("Suspended SOV Alliance", "SSOVA", "Suspended.", ""),
                "suspended-sov-alliance");
            var application = await rig.Store.ApplyToAllianceAsync(leaver, alliance.Id, "Join for SOV");
            var accepted = await AcceptAllianceApplicationAsync(rig.Store, executor, application);
            rig.Now = accepted.DueMs!.Value;
            Assert.Equal([leaverCorp.Id], await ProcessAllianceDueAsync(rig.Store));

            var worker = new Eve.Services.Corporations.AllianceSovereigntyTransferWorker(
                rig.Store, scheduler, () => rig.Now);
            var transfers = new List<(long AllianceId, long FromCorporationId, long ToCorporationId)>();
            worker.TransferSovereigntyAsync = (allianceId, fromCorporationId, toCorporationId, _) =>
            {
                transfers.Add((allianceId, fromCorporationId, toCorporationId));
                return Task.FromResult(true);
            };
            await worker.RegisterAsync();
            await rig.Store.RemoveAllianceMemberAsync(
                alliance.Id, leaverCorp.Id, leaver, "self-leave", rig.Now,
                static (_, _, _, _, _) => Task.CompletedTask);

            await using (var suspend = rig.Source.CreateCommand(
                $"UPDATE {rig.CorporationSchema}.alliances SET executor_corporation_id=NULL,suspended=true WHERE id=$1"))
            {
                suspend.Parameters.AddWithValue(alliance.Id);
                Assert.Equal(1, await suspend.ExecuteNonQueryAsync());
            }
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Empty(transfers);

            await using (var resume = rig.Source.CreateCommand(
                $"UPDATE {rig.CorporationSchema}.alliances SET executor_corporation_id=$2,suspended=false WHERE id=$1"))
            {
                resume.Parameters.AddWithValue(alliance.Id);
                resume.Parameters.AddWithValue(executorCorp.Id);
                Assert.Equal(1, await resume.ExecuteNonQueryAsync());
            }
            rig.Now = checked(rig.Now + 60_000);

            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal([(alliance.Id, leaverCorp.Id, executorCorp.Id)], transfers);
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Corp_registry_creates_alliance_idempotently_and_lists_its_applications()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,executorCorp) = await Founder(rig, "RPC Executor", "RPCEX");
        var (applicant,applicantCorp) = await Founder(rig, "RPC Applicant", "RPCAP");
        var sessions = new Eve.Services.CharacterSessions();
        var executorSink = new PacketSink();
        var executorSession = new Eve.Services.ClientSession(executorSink)
        {
            SessionId = executor,
            CharacterId = executor
        };
        executorSession.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
        sessions.Enter(executor,executorSession);
        var applicantSink = new PacketSink();
        var applicantSession = new Eve.Services.ClientSession(applicantSink)
        {
            SessionId = applicant,
            CharacterId = applicant
        };
        applicantSession.Set("corpid", new Eve.Marshal.PyInt(applicantCorp.Id));
        sessions.Enter(applicant,applicantSession);
        var affiliationWrites = 0;
        var registry = new Eve.Services.Corporations.CorpRegistryService(rig.Store, rig.Characters,
            () => rig.Now, characterSessions: sessions)
        {
            AllianceAffiliationWriter = (connection, transaction, corporationId, _, _) =>
            {
                Assert.Same(connection, transaction.Connection);
                Assert.Equal(System.Data.ConnectionState.Open, transaction.Connection!.State);
                affiliationWrites++;
                return Task.CompletedTask;
            }
        };
        var service = (Eve.Services.IService)registry;
        Eve.Services.ServiceCall Call(Eve.Services.ClientSession session,string method,params Eve.Marshal.PyValue[] args) =>
            new() { Session=session,Method=method,Args=args,CallId=new Eve.Marshal.PyInt(51) };

        var before = (await rig.Wallets.GetAsync(executor))!.Isk;
        var create = Call(executorSession, "CreateAlliance", new Eve.Marshal.PyWString("RPC Alliance"),
            new Eve.Marshal.PyWString("RPCA"), new Eve.Marshal.PyWString("Created by RPC"),
            new Eve.Marshal.PyWString(""));
        var allianceId = Assert.IsType<Eve.Marshal.PyInt>(await service.CallAsync(create));
        Assert.Empty(executorSink.Packets);
        executorSession.RunAfterResponse();
        Assert.Equal(["OnAllianceChanged", "OnAllianceMemberChanged", "OnCorporationChanged"],
            executorSink.Packets.Select(NotificationName));
        var creationNotificationCount = executorSink.Packets.Count;
        Assert.Equal(allianceId, await service.CallAsync(create));
        executorSession.RunAfterResponse();
        Assert.Equal(creationNotificationCount,executorSink.Packets.Count);
        Assert.Equal(2, affiliationWrites);
        Assert.Equal(before - 1000000000m, (await rig.Wallets.GetAsync(executor))!.Isk);
        Assert.Equal(["OnAllianceChanged"],applicantSink.Packets.Select(NotificationName));

        Eve.Marshal.PyValue[] applicationArgs =
        [
            new Eve.Marshal.PyInt(allianceId.Value),
            new Eve.Marshal.PyWString("Please join"),
            new Eve.Marshal.PyInt(executorCorp.Id)
        ];
        executorSink.Packets.Clear();
        applicantSink.Packets.Clear();
        Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(
            Call(applicantSession,"ApplyToJoinAlliance",applicationArgs)));
        applicantSession.RunAfterResponse();
        Assert.Equal(["OnAllianceApplicationChanged"],applicantSink.Packets.Select(NotificationName));
        Assert.Equal(["OnAllianceApplicationChanged"],executorSink.Packets.Select(NotificationName));
        var applications = Assert.IsType<Eve.Marshal.PyObjectEx>(
            await service.CallAsync(Call(applicantSession,"GetAllianceApplications")));
        var application = Assert.IsType<Eve.Marshal.PyPackedRow>(
            applications.Dict.Single(entry => entry.Key == allianceId).Value);
        Assert.Equal(allianceId, application.Values[0]);
        Assert.Equal(new Eve.Marshal.PyInt(applicantCorp.Id), application.Values[1]);
        Assert.Equal(new Eve.Marshal.PyWString("Please join"), application.Values[2]);
        Assert.Equal(new Eve.Marshal.PyInt(1), application.Values[3]);
    }

    [PgFact]
    public async Task Alliance_application_text_limit_uses_reference_utf16_code_units()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,_) = await Founder(rig,"Text Executor","TXTEX");
        var (applicant,_) = await Founder(rig,"Text Applicant","TXTAP");
        var (longApplicant,_) = await Founder(rig,"Long Applicant","TXTLG");
        var alliance = await rig.Store.CreateAllianceAsync(executor,Request,"text-limit");
        var maximum = string.Concat(Enumerable.Repeat("😀",500));
        var tooLong = string.Concat(Enumerable.Repeat("😀",501));

        var application = await rig.Store.ApplyToAllianceAsync(applicant,alliance.Id,maximum);
        Assert.Equal(maximum,(await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single(row => row.Id == application).Text);
        await Assert.ThrowsAsync<CorporationError>(async () => await rig.Store.ApplyToAllianceAsync(longApplicant,alliance.Id,tooLong));
        var response = await rig.Store.ApplyToAllianceAsync(longApplicant,alliance.Id,"initial");
        await Assert.ThrowsAsync<CorporationError>(async () => await rig.Store.RespondToAllianceAsync(executor,response,tooLong,4));
        var rejected = await rig.Store.RespondToAllianceAsync(executor,response,maximum,4);
        Assert.Equal(maximum,rejected.Text);
    }

    [PgFact]
    public async Task Alliance_application_response_text_limit_matches_the_client_utf16_maximum()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor, _) = await Founder(rig, "Response Executor", "RSPEX");
        var (applicant, applicantCorp) = await Founder(rig, "Response Applicant", "RSPAP");
        var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "response-limit");
        var applicationId = await rig.Store.ApplyToAllianceAsync(applicant, alliance.Id, "Initial text");
        var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
            rig.Store, rig.Characters, () => rig.Now);
        Eve.Services.ClientSession Session(long actor)
        {
            var session = new Eve.Services.ClientSession { SessionId = actor };
            session.Set("charid", new Eve.Marshal.PyInt(actor));
            return session;
        }
        Eve.Services.ServiceCall Call(long actor, string method, params Eve.Marshal.PyValue[] args) =>
            new() { Session = Session(actor), Method = method, Args = args };

        var maximum = string.Concat(Enumerable.Repeat("😀", 500));
        var tooLong = string.Concat(Enumerable.Repeat("😀", 501));

        var refused = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() => service.CallAsync(
            Call(executor, "UpdateApplication", new Eve.Marshal.PyInt(applicantCorp.Id),
                new Eve.Marshal.PyWString(tooLong), new Eve.Marshal.PyInt(4))).AsTask());
        Assert.Equal("ALLIANCE_APPLICATION_TEXT_TOO_LONG", refused.Code);
        var unchanged = Assert.Single(await rig.Store.AllianceApplicationsAsync(alliance.Id));
        Assert.Equal(applicationId, unchanged.Id);
        Assert.Equal("Initial text", unchanged.Text);
        Assert.Equal(1, unchanged.Status);

        Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(executor, "UpdateApplication",
            new Eve.Marshal.PyInt(applicantCorp.Id), new Eve.Marshal.PyWString(maximum), new Eve.Marshal.PyInt(4))));
        var updated = Assert.Single(await rig.Store.AllianceApplicationsAsync(alliance.Id));
        Assert.Equal(maximum, updated.Text);
        Assert.Equal(4, updated.Status);
    }

    [Fact]
    public async Task Suggested_alliance_short_names_match_reference_variants()
    {
        var service = (Eve.Services.IService)new Eve.Services.Corporations.CorpRegistryService(null!,null!,() => 0);
        var result = await service.CallAsync(new Eve.Services.ServiceCall
        {
            Session=new Eve.Services.ClientSession { SessionId=1 },Method="GetSuggestedAllianceShortNames",
            Args=[new Eve.Marshal.PyWString("Orbital!")]
        });
        var suggestions = Assert.IsType<Eve.Marshal.PyList>(result).Items.Select(value =>
        {
            var fields = Assert.IsType<Eve.Marshal.PyDict>(Assert.IsType<Eve.Marshal.PyObject>(value).Args);
            return Assert.IsType<Eve.Marshal.PyStr>(fields.Entries.Single(entry => entry.Key == new Eve.Marshal.PyStr("shortName")).Value).Value;
        });
        Assert.Equal(["ORBIT","ORBI","ORBX"],suggestions);
    }

    [Fact]
    public void Alliance_application_war_holds_match_EveJs_and_recheck_after_one_hour()
    {
        const long applicant = 1001;
        const long alliance = 2001;
        const long foreign = 3001;
        Assert.Equal(3_600_000, Eve.Corporations.AllianceRules.ApplicationWarRecheckDelayMs);
        Assert.Equal("AGGRESSOR_WAR", Eve.Corporations.AllianceRules.ApplicationWarHoldReason(applicant, alliance,
            [new(applicant, foreign, false)]));
        Assert.Equal("MUTUAL_WAR", Eve.Corporations.AllianceRules.ApplicationWarHoldReason(applicant, alliance,
            [new(foreign, applicant, true)]));
        Assert.Equal("FOREIGN_ALLY_WAR", Eve.Corporations.AllianceRules.ApplicationWarHoldReason(applicant, alliance,
            [new(foreign, 3002, false)]));
        Assert.Null(Eve.Corporations.AllianceRules.ApplicationWarHoldReason(applicant, alliance,
            [new(foreign, applicant, false)]));
        Assert.Null(Eve.Corporations.AllianceRules.ApplicationWarHoldReason(applicant, alliance,
            [new(foreign, alliance, false)]));
        Assert.Null(Eve.Corporations.AllianceRules.ApplicationWarHoldReason(applicant, alliance, []));
    }

    [PgFact]
    public async Task Alliance_acceptance_and_durable_schedule_commit_together()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor, _) = await Founder(rig, "Atomic Executor", "ATEXE");
        var (applicant, _) = await Founder(rig, "Atomic Applicant", "ATAPP");
        var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "atomic");
        var application = await rig.Store.ApplyToAllianceAsync(applicant, alliance.Id, "schedule me");

        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.RespondToAllianceAsync(executor, application, 2));
        Assert.Equal(1, (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);

        AllianceApplicationScheduleCallback unavailable = (_, _, _, _) => throw new InvalidOperationException("schedule rejected");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Store.RespondToAllianceAsync(executor, application, null, 2, unavailable));
        Assert.Equal(1, (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);

        AllianceApplicationDueJob? scheduled = null;
        AllianceApplicationScheduleCallback schedule = (connection, transaction, job, _) =>
        {
            Assert.Same(connection, transaction.Connection);
            scheduled = job;
            return Task.CompletedTask;
        };
        var accepted = await rig.Store.RespondToAllianceAsync(executor, application, null, 2, schedule);
        Assert.Equal(2, accepted.Status);
        Assert.Equal(accepted.DueMs, scheduled!.DueAtMs);
        Assert.Equal(application, scheduled.ApplicationId);
        Assert.Equal(2, (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
    }

    [PgFact]
    public async Task Alliance_due_worker_uses_the_durable_scheduler_relations_and_transactional_affiliation_event()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "alliance_sched_" + suffix;
        var eventsSchema = "alliance_events_" + suffix;
        var relationsSchema = "alliance_relations_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            var (executor, executorCorp) = await Founder(rig, "War Executor", "WAREX");
            var (applicant, applicantCorp) = await Founder(rig, "War Applicant", "WARAP");
            var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "war hold");
            var sessions = new Eve.Services.CharacterSessions();
            var executorSink = new PacketSink();
            var executorSession = new Eve.Services.ClientSession(executorSink)
            {
                SessionId = executor,
                CharacterId = executor
            };
            executorSession.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
            executorSession.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(executor, executorSession);
            var applicantSink = new PacketSink();
            var applicantSession = new Eve.Services.ClientSession(applicantSink)
            {
                SessionId = applicant,
                CharacterId = applicant
            };
            applicantSession.Set("corpid", new Eve.Marshal.PyInt(applicantCorp.Id));
            sessions.Enter(applicant, applicantSession);
            var changedCharacters = new List<long>();
            var worker = new Eve.Services.Corporations.AllianceApplicationDueWorker(
                rig.Store, rig.Characters, scheduler, relations, events,
                async (characterId, token) =>
                {
                    Assert.Equal(alliance.Id, (await rig.Characters.GetAsync(characterId, token))!.AllianceId);
                    changedCharacters.Add(characterId);
                },characterSessions:sessions);
            await worker.RegisterAsync();
            await events.PumpAsync(); // initialize the subscriber cursor before publishing test events
            executorSink.Packets.Clear();
            applicantSink.Packets.Clear();

            Assert.Equal(alliance.Id,
                Assert.Single(await relations.FromAsync(Eve.Relations.RelationKinds.AllianceMember.Name, executorCorp.Id)).Object);
            changedCharacters.Clear();

            var applicationId = await rig.Store.ApplyToAllianceAsync(applicant, alliance.Id, "join later");
            var accepted = await rig.Store.RespondToAllianceAsync(
                executor, applicationId, null, 2, worker.ScheduleAcceptedAsync);
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await relations.SetAsync(connection, transaction, Eve.Relations.RelationKinds.War.Name,
                    999999, applicantCorp.Id, 0, "mutual war", new { mutual = true });
                await transaction.CommitAsync();
            }

            rig.Now = accepted.DueMs!.Value;
            Assert.Equal(1, await scheduler.RunDueAsync());
            var heldApplication = Assert.Single(await rig.Store.AllianceApplicationsAsync(alliance.Id));
            Assert.Equal(2, heldApplication.Status);
            Assert.Equal(rig.Now + Eve.Corporations.AllianceRules.ApplicationWarRecheckDelayMs, heldApplication.DueMs);
            Assert.Empty(changedCharacters);

            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await relations.EndAsync(connection, transaction, Eve.Relations.RelationKinds.War.Name,
                    999999, applicantCorp.Id, "war ended");
                await transaction.CommitAsync();
            }

            rig.Now = heldApplication.DueMs!.Value;
            Assert.Equal(1, await scheduler.RunDueAsync());
            var effective = Assert.Single(await rig.Store.AllianceApplicationsAsync(alliance.Id));
            Assert.Equal(3, effective.Status);
            Assert.Null(effective.DueMs);
            Assert.Equal(alliance.Id, (await rig.Characters.GetAsync(applicant))!.AllianceId);
            Assert.Equal(alliance.Id, (await rig.Store.GetAsync(applicantCorp.Id))!.AllianceId);
            var affiliation = Assert.Single(await relations.FromAsync(Eve.Relations.RelationKinds.AllianceMember.Name, applicantCorp.Id));
            Assert.Equal(alliance.Id, affiliation.Object);

            await events.PumpAsync();
            Assert.Equal([applicant], changedCharacters);
            Assert.Equal(["OnAllianceChanged", "OnAllianceMemberChanged", "OnCorporationChanged"],
                applicantSink.Packets.Select(NotificationName));
            Assert.Equal(["OnAllianceChanged", "OnAllianceMemberChanged"],
                executorSink.Packets.Select(NotificationName));
            var memberChangePacket = applicantSink.Packets.Single(packet =>
                NotificationName(packet) == "OnAllianceMemberChanged");
            var memberChangeArgs = NotificationArgs(memberChangePacket);
            var memberChanges = Assert.IsType<Eve.Marshal.PyDict>(memberChangeArgs.Items[2]);
            Assert.Equal(["corporationID", "allianceID", "chosenExecutorID", "startDate", "deleted"],
                memberChanges.Entries.Select(entry => entry.Key switch
                {
                    Eve.Marshal.PyBytes key => System.Text.Encoding.UTF8.GetString(key.Value.Span),
                    Eve.Marshal.PyStr key => key.Value,
                    _ => throw new InvalidOperationException($"Unexpected notification key: {entry.Key.GetType().Name}")
                }));
            await events.PumpAsync();
            Assert.Single(changedCharacters);

            changedCharacters.Clear();
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await relations.EndAsync(connection, transaction, Eve.Relations.RelationKinds.AllianceMember.Name,
                    applicantCorp.Id, alliance.Id, "alliance membership ended");
                await transaction.CommitAsync();
            }
            await events.PumpAsync();
            Assert.Equal([applicant], changedCharacters);
            await events.PumpAsync();
            Assert.Single(changedCharacters);
        }
        finally
        {
            foreach (var schema in new[] { schedulerSchema, eventsSchema, relationsSchema })
            {
                await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Startup_reconciles_npc_and_player_affiliations_and_removes_stale_alliance_edges()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "affiliation_sched_" + suffix;
        var eventsSchema = "affiliation_events_" + suffix;
        var relationsSchema = "affiliation_relations_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            await rig.SeedNpcCorporationAsync(1000044);
            var npcMember = await rig.CharacterAsync("NPC Graph Member");
            var worker = new Eve.Services.Corporations.AllianceApplicationDueWorker(
                rig.Store, rig.Characters, scheduler, relations, events, (_, _) => Task.CompletedTask);
            rig.Store.AllianceAffiliationWriter = worker.WriteAffiliationAsync;
            var (founder, corporation) = await Founder(rig, "Player Graph", "PGRPH");
            await relations.SetAsync(Eve.Relations.RelationKinds.CorporationMember.Name,
                npcMember, corporation.Id, 1, "stale corporation affiliation");
            await relations.SetAsync(Eve.Relations.RelationKinds.AllianceMember.Name,
                corporation.Id, 0, 1, "legacy no-alliance sentinel");
            await relations.SetAsync(Eve.Relations.RelationKinds.AllianceMember.Name,
                1000044, 99000001, 1, "stale alliance affiliation");

            await worker.RegisterAsync();
            await events.PumpAsync();

            var npcAffiliation = Assert.Single(await relations.FromAsync(Eve.Relations.RelationKinds.CorporationMember.Name, npcMember));
            Assert.Equal(1000044, npcAffiliation.Object);
            var playerAffiliation = Assert.Single(await relations.FromAsync(Eve.Relations.RelationKinds.CorporationMember.Name, founder));
            Assert.Equal(corporation.Id, playerAffiliation.Object);
            Assert.Empty(await relations.FromAsync(Eve.Relations.RelationKinds.AllianceMember.Name, 1000044));
            Assert.Empty(await relations.FromAsync(Eve.Relations.RelationKinds.AllianceMember.Name, corporation.Id));
        }
        finally
        {
            foreach (var schema in new[] { schedulerSchema, eventsSchema, relationsSchema })
            {
                await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Alliance_bulletins_use_persistent_alliance_rows_and_require_member_directors()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "alliance_bulletin_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source, schema);
            var store = new CorporationStore(rig.Source, rig.Characters, rig.Wallets,
                rig.Transactions, rig.Skills, () => rig.Now, schema);
            var (director, _) = await Founder(rig, "Bulletin Director", "BULLETIN", store: store);
            var (outsider, _) = await Founder(rig, "Bulletin Outsider", "OUTSIDE", store: store);
            var alliance = await store.CreateAllianceAsync(director, Request, "bulletins");
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                store, rig.Characters, () => rig.Now);

            Eve.Services.ServiceCall Call(long actor, string method, params Eve.Marshal.PyValue[] args)
            {
                var session = new Eve.Services.ClientSession { SessionId = actor };
                session.Set("charid", new Eve.Marshal.PyInt(actor));
                return new() { Session = session, Method = method, Args = args };
            }

            await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() => service.CallAsync(Call(director,
                "AddBulletin", new Eve.Marshal.PyWString(new string('T', 101)),
                new Eve.Marshal.PyWString("Body"))).AsTask());
            await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() => service.CallAsync(Call(director,
                "AddBulletin", new Eve.Marshal.PyWString("Title"),
                new Eve.Marshal.PyWString(new string('B', 4001)))).AsTask());
            var maximumBulletinId = Assert.IsType<Eve.Marshal.PyInt>(await service.CallAsync(Call(director,
                "AddBulletin", new Eve.Marshal.PyWString(new string('T', 100)),
                new Eve.Marshal.PyWString(new string('B', 4000)))));
            await service.CallAsync(Call(director, "DeleteBulletin", maximumBulletinId));
            var bulletinId = Assert.IsType<Eve.Marshal.PyInt>(await service.CallAsync(Call(director,
                "AddBulletin", new Eve.Marshal.PyWString("Maintenance"),
                new Eve.Marshal.PyWString("Docking services will restart.")))).Value;
            var rows = Assert.IsType<Eve.Marshal.PyObject>(await service.CallAsync(Call(director, "GetBulletins")));
            var fields = Assert.IsType<Eve.Marshal.PyDict>(rows.Args);
            var lines = Assert.IsType<Eve.Marshal.PyList>(fields.Entries.Single(entry =>
                entry.Key == new Eve.Marshal.PyStr("lines")).Value);
            var row = Assert.IsType<Eve.Marshal.PyPackedRow>(Assert.Single(lines.Items));
            Assert.Equal(8, row.Values.Count);
            Assert.Equal(new Eve.Marshal.PyInt(bulletinId), row.Values[0]);
            Assert.Equal(new Eve.Marshal.PyInt(alliance.Id), row.Values[1]);
            Assert.Equal(new Eve.Marshal.PyWString("Maintenance"), row.Values[5]);
            Assert.Equal(Eve.Marshal.MarshalCodec.Encode(rows),
                Eve.Marshal.MarshalCodec.Encode(await service.CallAsync(Call(director, "GetBulletinEntries"))));
            var single = Assert.IsType<Eve.Marshal.PyPackedRow>(await service.CallAsync(Call(director,
                "GetBulletin", new Eve.Marshal.PyInt(bulletinId))));
            Assert.Equal(Eve.Marshal.MarshalCodec.Encode(row), Eve.Marshal.MarshalCodec.Encode(single));

            await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(() => service.CallAsync(Call(director,
                "UpdateBulletin", new Eve.Marshal.PyInt(bulletinId),
                new Eve.Marshal.PyWString(new string('T', 101)),
                new Eve.Marshal.PyWString("Body"))).AsTask());
            rig.Now += 1;
            Assert.Equal(new Eve.Marshal.PyInt(bulletinId), await service.CallAsync(Call(director,
                "UpdateBulletin", new Eve.Marshal.PyInt(bulletinId),
                new Eve.Marshal.PyWString("Maintenance complete"),
                new Eve.Marshal.PyWString("Services are online."))));
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(director,
                "UpdateBulletinOrder", new Eve.Marshal.PyList([new Eve.Marshal.PyInt(bulletinId)]))));
            var reopenedStore = new CorporationStore(rig.Source, rig.Characters, rig.Wallets,
                rig.Transactions, rig.Skills, () => rig.Now, schema);
            var reopenedService = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                reopenedStore, rig.Characters, () => rig.Now);
            var reopenedRows = Assert.IsType<Eve.Marshal.PyObject>(await reopenedService.CallAsync(
                Call(director, "GetBulletins")));
            var reopenedFields = Assert.IsType<Eve.Marshal.PyDict>(reopenedRows.Args);
            var reopenedLines = Assert.IsType<Eve.Marshal.PyList>(reopenedFields.Entries.Single(entry =>
                entry.Key == new Eve.Marshal.PyStr("lines")).Value);
            var persisted = Assert.IsType<Eve.Marshal.PyPackedRow>(Assert.Single(reopenedLines.Items));
            Assert.Equal(new Eve.Marshal.PyWString("Maintenance complete"), persisted.Values[5]);
            Assert.Equal(new Eve.Marshal.PyWString("Services are online."), persisted.Values[6]);
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(director,
                "DeleteBulletin", new Eve.Marshal.PyInt(bulletinId))));
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(director,
                "GetBulletin", new Eve.Marshal.PyInt(bulletinId))));
            await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(Call(outsider, "AddBulletin",
                new Eve.Marshal.PyWString("Unauthorized"), new Eve.Marshal.PyWString("No."))).AsTask());
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Alliance_executor_support_from_member_directors_is_persisted_and_elects_the_executor()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "alliance_support_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source, schema);
            var store = new CorporationStore(rig.Source, rig.Characters, rig.Wallets,
                rig.Transactions, rig.Skills, () => rig.Now, schema);
            var (executor, executorCorp) = await Founder(rig, "Support Executor", "SUPPEX", store: store);
            var (member, memberCorp) = await Founder(rig, "Support Member", "SUPMEM", store: store);
            var (secondMember, secondMemberCorp) = await Founder(rig, "Second Support Member", "SUPMEM2", store: store);
            var alliance = await store.CreateAllianceAsync(executor, Request, "support");
            var application = await store.ApplyToAllianceAsync(member, alliance.Id, "Join alliance");
            var secondApplication = await store.ApplyToAllianceAsync(secondMember, alliance.Id, "Join alliance");
            await AcceptAllianceApplicationAsync(store, executor, application);
            await AcceptAllianceApplicationAsync(store, executor, secondApplication);
            rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs;
            var joined = await ProcessAllianceDueAsync(store);
            Assert.Equal(new[] { memberCorp.Id, secondMemberCorp.Id }.Order(), joined.Order());

            var sessions = new Eve.Services.CharacterSessions();
            var executorSink = new PacketSink();
            var executorSession = new Eve.Services.ClientSession(executorSink)
            {
                SessionId = executor,
                CharacterId = executor
            };
            executorSession.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
            executorSession.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(executor, executorSession);
            var memberSink = new PacketSink();
            var memberSession = new Eve.Services.ClientSession(memberSink)
            {
                SessionId = member,
                CharacterId = member
            };
            memberSession.Set("corpid", new Eve.Marshal.PyInt(memberCorp.Id));
            memberSession.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(member, memberSession);
            var secondMemberSink = new PacketSink();
            var secondMemberSession = new Eve.Services.ClientSession(secondMemberSink)
            {
                SessionId = secondMember,
                CharacterId = secondMember
            };
            secondMemberSession.Set("corpid", new Eve.Marshal.PyInt(secondMemberCorp.Id));
            secondMemberSession.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(secondMember, secondMemberSession);
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                store, rig.Characters, () => rig.Now, characterSessions: sessions);

            Eve.Services.ServiceCall SupportCall(Eve.Services.ClientSession session, long candidateCorp) => new()
            {
                Session = session,
                Method = "DeclareExecutorSupport",
                Args = [new Eve.Marshal.PyInt(candidateCorp)]
            };

            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(SupportCall(memberSession, memberCorp.Id)));
            memberSession.RunAfterResponse();
            Assert.Equal(["OnAllianceMemberChanged"], memberSink.Packets.Select(NotificationName));
            Assert.Empty(executorSink.Packets);
            Assert.Empty(secondMemberSink.Packets);

            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(SupportCall(secondMemberSession, memberCorp.Id)));
            secondMemberSession.RunAfterResponse();
            Assert.Equal(["OnAllianceMemberChanged", "OnAllianceChanged"],
                secondMemberSink.Packets.Select(NotificationName));
            Assert.Equal(["OnAllianceChanged"], executorSink.Packets.Select(NotificationName));
            Assert.Equal(["OnAllianceMemberChanged", "OnAllianceChanged"],
                memberSink.Packets.Select(NotificationName));

            var members = await store.AllianceMembersAsync(alliance.Id);
            Assert.Equal(memberCorp.Id, members.Single(item => item.CorporationId == memberCorp.Id).ChosenExecutorId);
            Assert.Equal(memberCorp.Id, members.Single(item => item.CorporationId == secondMemberCorp.Id).ChosenExecutorId);
            Assert.Equal(memberCorp.Id, (await store.AllianceAsync(alliance.Id))!.ActiveExecutorId);
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Alliance_member_director_can_leave_and_affiliation_and_notifications_follow()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var corporationSchema = "alliance_leave_corp_" + suffix;
        var schedulerSchema = "alliance_leave_sched_" + suffix;
        var eventsSchema = "alliance_leave_events_" + suffix;
        var relationsSchema = "alliance_leave_relations_" + suffix;
        try
        {
            await CorporationStore.MigrateAsync(rig.Source, corporationSchema);
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var store = new CorporationStore(rig.Source, rig.Characters, rig.Wallets,
                rig.Transactions, rig.Skills, () => rig.Now, corporationSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            var (executor, executorCorp) = await Founder(rig, "Leave Executor", "LEAVEEX", store: store);
            var (member, memberCorp) = await Founder(rig, "Leaving Member", "LEAVEME", store: store);
            var alliance = await store.CreateAllianceAsync(executor, Request, "leave");
            var application = await store.ApplyToAllianceAsync(member, alliance.Id, "Join alliance");
            await AcceptAllianceApplicationAsync(store, executor, application);
            rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs;
            Assert.Equal([memberCorp.Id], await ProcessAllianceDueAsync(store));
            await relations.SetAsync(Eve.Relations.RelationKinds.AllianceMember.Name,
                memberCorp.Id, alliance.Id, 1, "test alliance affiliation");

            var sessions = new Eve.Services.CharacterSessions();
            var executorSink = new PacketSink();
            var executorSession = new Eve.Services.ClientSession(executorSink)
            {
                SessionId = executor,
                CharacterId = executor
            };
            executorSession.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
            executorSession.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(executor, executorSession);
            var memberSink = new PacketSink();
            var memberSession = new Eve.Services.ClientSession(memberSink)
            {
                SessionId = member,
                CharacterId = member
            };
            memberSession.Set("corpid", new Eve.Marshal.PyInt(memberCorp.Id));
            memberSession.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(member, memberSession);
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                store, rig.Characters, () => rig.Now, relations: relations, characterSessions: sessions);
            var call = new Eve.Services.ServiceCall
            {
                Session = memberSession,
                Method = "DeleteMember",
                Args = [new Eve.Marshal.PyInt(memberCorp.Id)]
            };
            await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(new Eve.Services.ServiceCall
            {
                Session = memberSession,
                Method = "DeleteMember",
                Args = [new Eve.Marshal.PyInt(executorCorp.Id)]
            }).AsTask());

            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(call));
            memberSession.RunAfterResponse();
            var notificationCount = memberSink.Packets.Count;
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(call));
            memberSession.RunAfterResponse();
            Assert.Equal(notificationCount, memberSink.Packets.Count);

            Assert.Null((await store.GetAsync(memberCorp.Id))!.AllianceId);
            Assert.Null((await rig.Characters.GetAsync(member))!.AllianceId);
            Assert.DoesNotContain(await store.AllianceMembersAsync(alliance.Id),
                item => item.CorporationId == memberCorp.Id);
            Assert.Null(await relations.GetAsync(Eve.Relations.RelationKinds.AllianceMember.Name,
                memberCorp.Id, alliance.Id));
            Assert.Equal(["OnAllianceMemberChanged", "OnAllianceChanged", "OnCorporationChanged"],
                memberSink.Packets.Select(NotificationName));
            Assert.Equal(["OnAllianceMemberChanged", "OnAllianceChanged"],
                executorSink.Packets.Select(NotificationName));
            Assert.Equal(executorCorp.Id, (await store.AllianceAsync(alliance.Id))!.ActiveExecutorId);
        }
        finally
        {
            foreach (var schema in new[] { corporationSchema, schedulerSchema, eventsSchema, relationsSchema })
            {
                await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

      [PgFact]
    public async Task Alliance_maintenance_bills_auto_pay_and_renew_once_from_the_prior_due_date()
    {
        await using var rig = await Rig.CreateAsync();
        var schedulerSchema = "alliance_maintenance_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var (actor, executorCorp) = await Founder(rig, "Maintenance Executor", "ALLYFEE");
            var alliance = await rig.Store.CreateAllianceAsync(actor, Request, "alliance-maintenance");
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await rig.Transactions.TransferAsync(connection, transaction,
                    new Eve.Wallet.WalletAccount(actor, 1000),
                    new Eve.Wallet.WalletAccount(executorCorp.Id, 1003),
                    3_000_000m, 17, "Test alliance maintenance funding",
                    rig.Now, $"alliance-maintenance-funding:{actor}", "test_wallet_transfer");
                await rig.Wallets.OpenAsync(connection, transaction, 1_000_125, 0m, 0);
                await transaction.CommitAsync();
            }
            await rig.OfficeStore.SetAutomaticPaySettingsAsync(new Dictionary<long, Eve.Corporations.CorporationBillAutomaticPaySettings>
            {
                [alliance.Id] = new(1000, new Dictionary<int, bool> { [5] = true }),
                [executorCorp.Id] = new(1003, new Dictionary<int, bool>())
            });
            var notices = new HashSet<(long RecipientId, long AllianceId, long BillId)>();
            var worker = new Eve.Services.Corporations.AllianceMaintenanceBillWorker(
                rig.Store, rig.OfficeStore, scheduler, () => rig.Now,
                (recipientId, allianceId, billId, _) =>
                {
                    notices.Add((recipientId, allianceId, billId));
                    return Task.CompletedTask;
                });
            await worker.RegisterAsync();
            await worker.ReconcileAsync();

            var initial = Assert.Single(await rig.Store.AllianceMaintenanceBillsAsync(alliance.Id));
            Assert.Equal(5, initial.BillTypeId);
            Assert.Equal(2_000_000m, initial.AmountIsk);
            var initialDue = initial.DueFiletime;
            var expectedInitialDue = checked((rig.Now + 11_644_473_600_000L) * 10_000L) +
                30L * 24 * 60 * 60 * 10_000_000;
            Assert.Equal(expectedInitialDue, initialDue);
            Assert.Contains((actor, alliance.Id, initial.BillId), notices);
            var balanceBefore = await rig.Transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(executorCorp.Id, 1003));

            rig.Now = initialDue / 10_000L - 11_644_473_600_000L;
            Assert.Equal(1, await scheduler.RunDueAsync());

            var renewed = Assert.Single(await rig.Store.AllianceBillsAsync(alliance.Id));
            Assert.Equal(5, renewed.BillTypeId);
            Assert.Equal(2_000_000m, renewed.AmountIsk);
            Assert.Contains((actor, alliance.Id, renewed.BillId), notices);
            Assert.Equal(initialDue + 30L * 24 * 60 * 60 * 10_000_000, renewed.DueFiletime);
            var settledBills = await rig.Store.AllianceMaintenanceBillsAsync(alliance.Id);
            var settled = Assert.Single(settledBills, item => item.BillId == initial.BillId);
            Assert.True(settled.Paid);
            var balanceAfter = await rig.Transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(executorCorp.Id, 1003));
            Assert.Equal(2_000_000m, balanceBefore - balanceAfter);
            Assert.Equal(0, await scheduler.RunDueAsync());
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Unpaid_alliance_maintenance_closes_at_downtime_and_releases_member_affiliations()
    {
        await using var rig = await Rig.CreateAsync();
        var schedulerSchema = "alliance_close_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var (actor, executorCorp) = await Founder(rig, "Unpaid Alliance", "ALLYDUE");
            var alliance = await rig.Store.CreateAllianceAsync(actor, Request, "alliance-unpaid-close");
            rig.Store.AllianceAffiliationWriter = static (_, _, _, _, _) => Task.CompletedTask;

            var sessions = new Eve.Services.CharacterSessions();
            var sink = new PacketSink();
            var session = new Eve.Services.ClientSession(sink) { SessionId = actor, CharacterId = actor };
            session.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            session.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
            sessions.Enter(actor, session);

            var sovereigntyDrops = 0;
            var worker = new Eve.Services.Corporations.AllianceMaintenanceBillWorker(
                rig.Store, rig.OfficeStore, scheduler, () => rig.Now);
            worker.CharacterSessions = sessions;
            worker.NextDowntimeAtMs = dueAt => dueAt + 1000;
            worker.DropSovereigntyClaimsAsync = (_, _, _) =>
            {
                sovereigntyDrops++;
                return Task.FromResult(true);
            };
            await worker.RegisterAsync();

            var bill = Assert.Single(await rig.Store.AllianceMaintenanceBillsAsync(alliance.Id));
            var dueAtMs = bill.DueFiletime / 10_000L - 11_644_473_600_000L;
            rig.Now = dueAtMs;
            Assert.Equal(1, await scheduler.RunDueAsync());

            rig.Now = dueAtMs + 1000;
            Assert.Equal(1, await scheduler.RunDueAsync());
            rig.Now += 1;
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal(0, await scheduler.RunDueAsync());

            var closed = await rig.Store.AllianceAsync(alliance.Id);
            Assert.True(closed!.Deleted);
            Assert.Null((await rig.Store.GetAsync(executorCorp.Id))!.AllianceId);
            Assert.Empty(await rig.Store.AllianceMembersAsync(alliance.Id));
            Assert.DoesNotContain(await rig.Store.PendingAllianceMaintenanceBillsAsync(),
                entry => entry.BillId == bill.BillId);
            Assert.Equal(1, sovereigntyDrops);
            Assert.Equal(["OnAllianceMemberChanged", "OnAllianceChanged", "OnCorporationChanged"],
                sink.Packets.Select(NotificationName));
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Last_alliance_member_leaves_but_closure_waits_for_next_downtime()
    {
        await using var rig = await Rig.CreateAsync();
        var schedulerSchema = "alliance_empty_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var (actor, corporation) = await Founder(rig, "Final Alliance Member", "ALLYEND");
            var alliance = await rig.Store.CreateAllianceAsync(actor, Request, "alliance-last-member");
            rig.Store.AllianceAffiliationWriter = static (_, _, _, _, _) => Task.CompletedTask;
            var worker = new Eve.Services.Corporations.AllianceMaintenanceBillWorker(
                rig.Store, rig.OfficeStore, scheduler, () => rig.Now);
            worker.NextDowntimeAtMs = at => at + 1000;
            worker.DropSovereigntyClaimsAsync = (_, _, _) => Task.FromResult(true);
            await worker.RegisterAsync();

            var leftAtMs = rig.Now;
            var departure = await rig.Store.RemoveAllianceMemberAsync(
                alliance.Id, corporation.Id, actor, "self-leave", leftAtMs,
                static (_, _, _, _, _) => Task.CompletedTask);
            Assert.False(departure.Duplicate);
            var waiting = await rig.Store.AllianceAsync(alliance.Id);
            Assert.False(waiting!.Deleted);
            Assert.True(waiting.Suspended);
            Assert.Null(waiting.ActiveExecutorId);
            Assert.Empty(await rig.Store.AllianceMembersAsync(alliance.Id));

            rig.Now = leftAtMs + 999;
            Assert.Equal(0, await scheduler.RunDueAsync());
            rig.Now = leftAtMs + 1000;
            Assert.Equal(1, await scheduler.RunDueAsync());
            rig.Now += 1;
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.True((await rig.Store.AllianceAsync(alliance.Id))!.Deleted);
        }
        finally
        {
            await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task A_departing_alliance_member_inherits_the_active_war_and_processes_due_war_bills()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "alliance_inherit_sched_" + suffix;
        var eventsSchema = "alliance_inherit_events_" + suffix;
        var relationsSchema = "alliance_inherit_relations_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            var (executor, executorCorp) = await Founder(rig, "War Executor", "INHEX");
            var (leaver, leaverCorp) = await Founder(rig, "War Leaver", "INHLE");
            var (_, targetCorp) = await Founder(rig, "War Target", "INHTG");
            var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "alliance-war-inheritance");
            var application = await rig.Store.ApplyToAllianceAsync(leaver, alliance.Id, "Join for war");
            var accepted = await AcceptAllianceApplicationAsync(rig.Store, executor, application);
            rig.Now = accepted.DueMs!.Value;
            Assert.Equal([leaverCorp.Id], await ProcessAllianceDueAsync(rig.Store));

            var sessions = new Eve.Services.CharacterSessions();
            var sink = new PacketSink();
            var session = new Eve.Services.ClientSession(sink) { SessionId = executor, CharacterId = executor };
            session.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
            session.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(executor, session);
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                rig.Store, rig.Characters, () => rig.Now, relations: relations, characterSessions: sessions);
            await service.CallAsync(new Eve.Services.ServiceCall
            {
                Session = session,
                Method = "DeclareWarAgainst",
                Args = [new Eve.Marshal.PyInt(targetCorp.Id), Eve.Marshal.PyNone.Instance],
                CallId = new Eve.Marshal.PyInt(71030)
            });
            session.RunAfterResponse();
            sink.Packets.Clear();
            var sourceWar = (await relations.GetAsync("war", alliance.Id, targetCorp.Id))!;
            var sourceAttrs = sourceWar.Attrs<System.Text.Json.JsonElement>();
            var sourceBillId = sourceAttrs.GetProperty("billID").GetInt64();
            var sourceBill = Assert.Single(await rig.Store.AllianceBillsAsync(alliance.Id),
                bill => bill.BillId == sourceBillId);

            var worker = new Eve.Services.Corporations.AllianceWarInheritanceWorker(
                rig.Source, rig.Store, rig.OfficeStore, relations, scheduler, () => rig.Now, sessions);
            await worker.RegisterAsync();
            var departure = await rig.Store.RemoveAllianceMemberAsync(
                alliance.Id, leaverCorp.Id, leaver, "self-leave", rig.Now,
                static (_, _, _, _, _) => Task.CompletedTask);
            Assert.False(departure.Duplicate);
            Assert.Equal(1, await scheduler.RunDueAsync());

            var inherited = (await relations.GetAsync("war", leaverCorp.Id, targetCorp.Id))!;
            var inheritedAttrs = inherited.Attrs<System.Text.Json.JsonElement>();
            Assert.Equal(sourceWar.Id, inheritedAttrs.GetProperty("createdFromWarID").GetInt64());
            var inheritedBillId = inheritedAttrs.GetProperty("billID").GetInt64();
            var inheritedBill = Assert.Single(await rig.Store.CorporationBillsAsync(leaverCorp.Id));
            Assert.Equal(inheritedBillId, inheritedBill.BillId);
            Assert.Equal(4, inheritedBill.BillTypeId);
            Assert.Equal(100_000_000m, inheritedBill.AmountIsk);
            Assert.Equal(sourceBill.DueFiletime, inheritedBill.DueFiletime);
            Assert.Equal(["OnWarChanged"], sink.Packets.Select(NotificationName));

            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await rig.Transactions.TransferAsync(connection, transaction,
                    new Eve.Wallet.WalletAccount(executor, 1000),
                    new Eve.Wallet.WalletAccount(executorCorp.Id, 1000),
                    100_000_000m, 17, "Fund alliance war auto-payment", rig.Now,
                    $"alliance-war-autopay-funding:{suffix}", "test_wallet_transfer");
                await rig.Wallets.OpenAsync(connection, transaction, 1_000_125, 0m, 0);
                await transaction.CommitAsync();
            }
            await rig.OfficeStore.SetAutomaticPaySettingsAsync(new Dictionary<long, Eve.Corporations.CorporationBillAutomaticPaySettings>
            {
                [alliance.Id] = new(1000, new Dictionary<int, bool> { [4] = true }),
                [executorCorp.Id] = new(1000, new Dictionary<int, bool>())
            });
            var payerAccount = new Eve.Wallet.WalletAccount(executorCorp.Id, 1000);
            var balanceBefore = await rig.Transactions.AccountBalanceAsync(payerAccount);
            rig.Now = sourceBill.DueFiletime / 10_000L - 11_644_473_600_000L;
            Assert.Equal(2, await scheduler.RunDueAsync());

            var renewedBill = Assert.Single(await rig.Store.PendingWarBillsAsync(),
                bill => bill.DebtorId == alliance.Id);
            Assert.NotEqual(sourceBill.BillId, renewedBill.BillId);
            Assert.Equal(sourceBill.DueFiletime + 6_048_000_000_000L, renewedBill.DueFiletime);
            var renewedWar = (await relations.GetAsync("war", alliance.Id, targetCorp.Id))!;
            Assert.Equal(renewedBill.BillId,
                renewedWar.Attrs<System.Text.Json.JsonElement>().GetProperty("billID").GetInt64());
            var balanceAfter = await rig.Transactions.AccountBalanceAsync(payerAccount);
            Assert.Equal(100_000_000m, balanceBefore - balanceAfter);
            Assert.DoesNotContain(await rig.Store.PendingWarBillsAsync(),
                bill => bill.BillId == inheritedBill.BillId);

            var finishAtMs = rig.Now + 24L * 60 * 60 * 1000;
            var finishingWar = await relations.GetAsync("war", leaverCorp.Id, targetCorp.Id);
            Assert.NotNull(finishingWar);
            Assert.Equal(finishAtMs, finishingWar.UntilMs);
            var finishingAttrs = finishingWar.Attrs<System.Text.Json.JsonElement>();
            Assert.Equal(10, finishingAttrs.GetProperty("reasonEnded").GetInt32());
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(finishAtMs).ToFileTime().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
                finishingAttrs.GetProperty("timeFinished").GetString());
            Assert.Equal(3, sink.Packets.Count(packet => NotificationName(packet) == "OnWarChanged"));

            rig.Now = finishAtMs;
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Null(await relations.GetAsync("war", leaverCorp.Id, targetCorp.Id));
        }
        finally
        {
            foreach (var schema in new[] { schedulerSchema, eventsSchema, relationsSchema })
            {
                await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Unpaid_alliance_closure_inherits_current_wars_before_ending_the_alliance_wars()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "alliance_closure_wars_sched_" + suffix;
        var eventsSchema = "alliance_closure_wars_events_" + suffix;
        var relationsSchema = "alliance_closure_wars_relations_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            var (executor, executorCorp) = await Founder(rig, "Closing War Executor", "CLSWAR");
            var (_, targetCorp) = await Founder(rig, "Closing War Target", "CLSTGT");
            var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "alliance-closure-war");
            rig.Store.AllianceAffiliationWriter = static (_, _, _, _, _) => Task.CompletedTask;

            var sessions = new Eve.Services.CharacterSessions();
            var sink = new PacketSink();
            var session = new Eve.Services.ClientSession(sink) { SessionId = executor, CharacterId = executor };
            session.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            session.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
            sessions.Enter(executor, session);

            var warInheritance = new Eve.Services.Corporations.AllianceWarInheritanceWorker(
                rig.Source, rig.Store, rig.OfficeStore, relations, scheduler, () => rig.Now, sessions);
            await warInheritance.RegisterAsync();
            var sovereigntyDrops = 0;
            var maintenance = new Eve.Services.Corporations.AllianceMaintenanceBillWorker(
                rig.Store, rig.OfficeStore, scheduler, () => rig.Now);
            maintenance.CharacterSessions = sessions;
            maintenance.NextDowntimeAtMs = at => at + 1000;
            maintenance.DropSovereigntyClaimsAsync = (_, _, _) =>
            {
                sovereigntyDrops++;
                return Task.FromResult(true);
            };
            maintenance.InheritAllianceWarsForClosureAsync = warInheritance.InheritForAllianceClosureAsync;
            maintenance.EndAllianceWarsAtClosureAsync = warInheritance.EndAllianceWarsAsync;
            await maintenance.RegisterAsync();

            var maintenanceBill = Assert.Single(await rig.Store.AllianceMaintenanceBillsAsync(alliance.Id));
            var maintenanceDueMs = maintenanceBill.DueFiletime / 10_000L - 11_644_473_600_000L;
            rig.Now = maintenanceDueMs - 24L * 60 * 60 * 1000 - 2_000;
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                rig.Store, rig.Characters, () => rig.Now, relations: relations, characterSessions: sessions);
            await service.CallAsync(new Eve.Services.ServiceCall
            {
                Session = session,
                Method = "DeclareWarAgainst",
                Args = [new Eve.Marshal.PyInt(targetCorp.Id), Eve.Marshal.PyNone.Instance],
                CallId = new Eve.Marshal.PyInt(73010)
            });
            session.RunAfterResponse();
            sink.Packets.Clear();
            var sourceWar = (await relations.GetAsync("war", alliance.Id, targetCorp.Id))!;
            var sourceBill = Assert.Single(await rig.Store.AllianceBillsAsync(alliance.Id),
                bill => bill.BillTypeId == 4);

            rig.Now = maintenanceDueMs;
            Assert.Equal(1, await scheduler.RunDueAsync());
            rig.Now = maintenanceDueMs + 1000;
            Assert.Equal(1, await scheduler.RunDueAsync());
            rig.Now += 1;
            Assert.Equal(1, await scheduler.RunDueAsync());

            Assert.True((await rig.Store.AllianceAsync(alliance.Id))!.Deleted);
            Assert.Null(await relations.GetAsync("war", alliance.Id, targetCorp.Id));
            var inherited = (await relations.GetAsync("war", executorCorp.Id, targetCorp.Id))!;
            var inheritedAttrs = inherited.Attrs<System.Text.Json.JsonElement>();
            Assert.Equal(sourceWar.Id, inheritedAttrs.GetProperty("createdFromWarID").GetInt64());
            var inheritedBillId = inheritedAttrs.GetProperty("billID").GetInt64();
            var inheritedBill = Assert.Single(await rig.Store.CorporationBillsAsync(executorCorp.Id));
            Assert.Equal(inheritedBillId, inheritedBill.BillId);
            Assert.Equal(sourceBill.DueFiletime, inheritedBill.DueFiletime);
            Assert.Equal(1, sovereigntyDrops);
            Assert.Equal(2, sink.Packets.Count(packet => NotificationName(packet) == "OnWarChanged"));
        }
        finally
        {
            foreach (var schema in new[] { schedulerSchema, eventsSchema, relationsSchema })
            {
                await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Alliance_war_declaration_bills_are_replay_safe_and_pay_from_the_executor_wallet()
    {
        await using var rig = await Rig.CreateAsync();
        var schedulerSchema = "alliance_war_sched_" + Guid.NewGuid().ToString("N")[..12];
        var eventsSchema = "alliance_war_events_" + Guid.NewGuid().ToString("N")[..12];
        var relationsSchema = "alliance_war_relations_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            var (actor, executorCorp) = await Founder(rig, "Alliance War Executor", "ALLYWAR");
            var (_, targetCorp) = await Founder(rig, "Alliance War Target", "ALLYTGT");
            var alliance = await rig.Store.CreateAllianceAsync(actor, Request, "alliance-war");
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await rig.Transactions.TransferAsync(connection, transaction,
                    new Eve.Wallet.WalletAccount(actor,1000), new Eve.Wallet.WalletAccount(executorCorp.Id,1000),
                    200_000_000m, 17, "Test corporation funding", rig.Now,
                    $"alliance-war-funding:{actor}", "test_wallet_transfer");
                await transaction.CommitAsync();
            }
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await rig.Wallets.OpenAsync(connection, transaction, 1_000_125, 0m, 0);
                await transaction.CommitAsync();
            }
            var sessions = new Eve.Services.CharacterSessions();
            var sink = new PacketSink();
            var session = new Eve.Services.ClientSession(sink) { SessionId = actor, CharacterId = actor };
            session.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
            session.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(actor, session);
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                rig.Store, rig.Characters, () => rig.Now, relations: relations, characterSessions: sessions);
            Eve.Services.ServiceCall DeclareCall(long againstId) => new()
            {
                Session = session,
                Method = "DeclareWarAgainst",
                Args = [new Eve.Marshal.PyInt(againstId), Eve.Marshal.PyNone.Instance],
                CallId = new Eve.Marshal.PyInt(71003)
            };

            var declaration = Assert.IsType<Eve.Marshal.PyObject>(await service.CallAsync(DeclareCall(targetCorp.Id)));
            Assert.Empty(sink.Packets);
            session.RunAfterResponse();
            Assert.Equal("OnWarChanged", NotificationName(Assert.Single(sink.Packets)));
            var fields = Assert.IsType<Eve.Marshal.PyDict>(declaration.Args);
            var warId = Assert.IsType<Eve.Marshal.PyInt>(fields.Entries.Single(entry =>
                entry.Key == new Eve.Marshal.PyStr("warID")).Value).Value;
            var billId = Assert.IsType<Eve.Marshal.PyInt>(fields.Entries.Single(entry =>
                entry.Key == new Eve.Marshal.PyStr("billID")).Value).Value;
            Assert.Equal(new Eve.Marshal.PyInt(alliance.Id), fields.Entries.Single(entry =>
                entry.Key == new Eve.Marshal.PyStr("declaredByID")).Value);
            var war = await relations.GetAsync("war", alliance.Id, targetCorp.Id);
            Assert.NotNull(war);
            Assert.Equal(war.Id, warId);
            var bill = Assert.Single(await rig.Store.AllianceBillsAsync(alliance.Id));
            Assert.Equal(billId, bill.BillId);
            Assert.Equal(warId, bill.WarId);
            Assert.Equal(100_000_000m, bill.AmountIsk);
            Assert.Equal(alliance.Id, bill.DebtorId);
            Assert.Equal(1_000_125, bill.CreditorId);

            var replay = Assert.IsType<Eve.Marshal.PyObject>(await service.CallAsync(DeclareCall(targetCorp.Id)));
            var replayFields = Assert.IsType<Eve.Marshal.PyDict>(replay.Args);
            Assert.Equal(new Eve.Marshal.PyInt(warId), replayFields.Entries.Single(entry =>
                entry.Key == new Eve.Marshal.PyStr("warID")).Value);
            session.RunAfterResponse();
            Assert.Single(sink.Packets);
            Assert.Single(await rig.Store.AllianceBillsAsync(alliance.Id));

            var balance = await service.CallAsync(new Eve.Services.ServiceCall
            {
                Session = session, Method = "GetBillBalance", Args = []
            });
            var beforeBalance = await rig.Transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(executorCorp.Id,1000));
            Assert.True(beforeBalance is > 100_000_000m);
            Assert.Equal(new Eve.Marshal.PyInt((long)beforeBalance!.Value),balance);
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(new Eve.Services.ServiceCall
            {
                Session = session, Method = "PayBill", Args = [new Eve.Marshal.PyInt(billId)]
            }));
            Assert.Empty(await rig.Store.AllianceBillsAsync(alliance.Id));
            var afterPayment = await service.CallAsync(new Eve.Services.ServiceCall
            {
                Session = session, Method = "GetBillBalance", Args = []
            });
            var afterBalance = await rig.Transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(executorCorp.Id,1000));
            Assert.Equal(100_000_000m, beforeBalance.Value - afterBalance!.Value);
            Assert.Equal(new Eve.Marshal.PyInt((long)afterBalance.Value),afterPayment);
        }
        finally
        {
            foreach (var schema in new[] { schedulerSchema, eventsSchema, relationsSchema })
            {
                await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Alliance_public_updates_relationships_and_application_decisions_notify_clients()
    {
        await using var rig = await Rig.CreateAsync();
        var schedulerSchema = "alliance_notify_sched_" + Guid.NewGuid().ToString("N")[..12];
        var eventsSchema = "alliance_notify_events_" + Guid.NewGuid().ToString("N")[..12];
        var relationsSchema = "alliance_notify_relations_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            var (executor, executorCorp) = await Founder(rig, "Notify Executor", "NOTIFYEX");
            var (applicant, applicantCorp) = await Founder(rig, "Notify Applicant", "NOTIFYAP");
            var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "notifications");
            var applicationId = await rig.Store.ApplyToAllianceAsync(applicant, alliance.Id, "Please review");

            var sessions = new Eve.Services.CharacterSessions();
            var executorSink = new PacketSink();
            var executorSession = new Eve.Services.ClientSession(executorSink)
            {
                SessionId = executor,
                CharacterId = executor
            };
            executorSession.Set("corpid", new Eve.Marshal.PyInt(executorCorp.Id));
            executorSession.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            sessions.Enter(executor, executorSession);
            var applicantSink = new PacketSink();
            var applicantSession = new Eve.Services.ClientSession(applicantSink)
            {
                SessionId = applicant,
                CharacterId = applicant
            };
            applicantSession.Set("corpid", new Eve.Marshal.PyInt(applicantCorp.Id));
            sessions.Enter(applicant, applicantSession);
            var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
                rig.Store, rig.Characters, () => rig.Now, relations: relations, characterSessions: sessions);
            Eve.Services.ServiceCall Call(string method, params Eve.Marshal.PyValue[] args) => new()
            {
                Session = executorSession,
                Method = method,
                Args = args
            };

            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call("UpdateAlliance",
                new Eve.Marshal.PyWString("Updated alliance description"),
                new Eve.Marshal.PyWString("https://example.invalid/alliance"))));
            executorSession.RunAfterResponse();
            Assert.Equal(["OnAllianceChanged"], executorSink.Packets.Select(NotificationName));
            Assert.Equal(["OnAllianceChanged"], applicantSink.Packets.Select(NotificationName));

            executorSink.Packets.Clear();
            applicantSink.Packets.Clear();
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call("SetRelationship",
                new Eve.Marshal.PyInt(7), new Eve.Marshal.PyInt(applicantCorp.Id))));
            executorSession.RunAfterResponse();
            Assert.Equal(["OnAllianceRelationshipChanged"], executorSink.Packets.Select(NotificationName));
            Assert.Empty(applicantSink.Packets);

            executorSink.Packets.Clear();
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call("DeleteRelationship",
                new Eve.Marshal.PyInt(applicantCorp.Id))));
            executorSession.RunAfterResponse();
            Assert.Equal(["OnAllianceRelationshipChanged"], executorSink.Packets.Select(NotificationName));

            executorSink.Packets.Clear();
            applicantSink.Packets.Clear();
            Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call("UpdateApplication",
                new Eve.Marshal.PyInt(applicantCorp.Id), new Eve.Marshal.PyWString("Not accepted"),
                new Eve.Marshal.PyInt(4))));
            executorSession.RunAfterResponse();
            Assert.Equal(["OnAllianceApplicationChanged"], executorSink.Packets.Select(NotificationName));
            Assert.Equal(["OnAllianceApplicationChanged"], applicantSink.Packets.Select(NotificationName));
            Assert.Equal(4, (await rig.Store.AllianceApplicationsAsync(alliance.Id))
                .Single(item => item.Id == applicationId).Status);
        }
        finally
        {
            foreach (var schema in new[] { schedulerSchema, eventsSchema, relationsSchema })
            {
                await using var cleanup = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Alliance_GMForceAcceptApplication_requires_GML_and_uses_the_durable_due_schedule()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor, _) = await Founder(rig, "GM Alliance Executor", "GMLALLY");
        var (applicant, applicantCorp) = await Founder(rig, "GM Alliance Applicant", "GMLAPPL");
        var alliance = await rig.Store.CreateAllianceAsync(executor, Request, "gm-accept");
        var application = await rig.Store.ApplyToAllianceAsync(applicant, alliance.Id, "Please accept");
        var scheduled = new List<AllianceApplicationDueJob>();
        AllianceApplicationScheduleCallback schedule = (connection, transaction, job, _) =>
        {
            Assert.Same(connection, transaction.Connection);
            scheduled.Add(job);
            return Task.CompletedTask;
        };
        var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
            rig.Store, rig.Characters, () => rig.Now, scheduleAllianceApplication: schedule);
        Eve.Services.ServiceCall Call(long accountRole, long sessionRole = 0)
        {
            var session = new Eve.Services.ClientSession { SessionId = applicant, CharacterId = applicant };
            session.Set("allianceid", new Eve.Marshal.PyInt(alliance.Id));
            session.Set("accountRole", new Eve.Marshal.PyLong(accountRole));
            session.Set("role", new Eve.Marshal.PyLong(sessionRole));
            return new Eve.Services.ServiceCall
            {
                Session = session,
                Method = "GMForceAcceptApplication",
                Args = [new Eve.Marshal.PyInt(applicantCorp.Id)]
            };
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.CallAsync(Call(0, Eve.Services.GameMasterRoles.Gml)).AsTask());
        Assert.Empty(scheduled);
        Assert.Equal(1, (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
        Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(
            Call(Eve.Services.GameMasterRoles.Gml)));
        Assert.Equal(2, (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
        Assert.Equal(rig.Now + Eve.Corporations.AllianceRules.ApplicationDelayMs,
            (await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().DueMs);
        Assert.Equal(application, Assert.Single(scheduled).ApplicationId);
    }

    private sealed class PacketSink : Eve.Services.IPacketSink
    {
        public List<Eve.Marshal.PyValue> Packets { get; } = [];
        public void Send(Eve.Marshal.PyValue packet) => Packets.Add(packet);
    }

    private static Eve.Marshal.PyTuple NotificationArgs(Eve.Marshal.PyValue packet)
    {
        var fields = (Eve.Marshal.PyTuple)((Eve.Marshal.PyObject)packet).Args;
        var envelope = (Eve.Marshal.PyTuple)((Eve.Marshal.PyTuple)fields.Items[4]).Items[0];
        var notification = (Eve.Marshal.PyTuple)Eve.Marshal.MarshalCodec.Decode(((Eve.Marshal.PyBuffer)envelope.Items[1]).Value);
        return (Eve.Marshal.PyTuple)((Eve.Marshal.PyTuple)notification.Items[1]).Items[1];
    }

    private static string? NotificationName(Eve.Marshal.PyValue packet)
    {
        if (packet is not Eve.Marshal.PyObject { Name: Eve.Marshal.PyStr name, Args: Eve.Marshal.PyTuple args } ||
            !name.Value.EndsWith("Notification", StringComparison.Ordinal))
            return null;
        return Eve.Macho.MachoAddress.Decode(args.Items[2]).BroadcastId;
    }

}
