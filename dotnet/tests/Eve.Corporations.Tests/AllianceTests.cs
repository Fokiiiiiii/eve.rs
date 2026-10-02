using Eve.Skills;
using Eve.Wallet;

namespace Eve.Corporations.Tests;

public class AllianceTests
{
    private static readonly NewAlliance Request = new("Orbital Alliance","ORB","An alliance.","");
    private static async Task<(long Actor,Corporation Corp)> Founder(Rig rig,string name,string ticker,decimal money = 2000000000m,bool skill = true)
    {
        var actor = await rig.CharacterAsync(name,money);
        var corp = await rig.Store.CreateAsync(actor,new(name+" Corporation",ticker,"","",0),"corp-"+actor);
        if (skill)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection,tx,actor,[new CharacterSkill(3732,5,1024000)]);
            await tx.CommitAsync();
        }
        return (actor,corp);
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
        var (a,corpA) = await Founder(rig,"Founder A","AAAAA");
        var (b,corpB) = await Founder(rig,"Founder B","BBBBB");
        var alliance = await rig.Store.CreateAllianceAsync(a,Request,"first");
        var app = await rig.Store.ApplyToAllianceAsync(b,alliance.Id,"Join us");
        Assert.Equal(app,await rig.Store.ApplyToAllianceAsync(b,alliance.Id,"Join us"));
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.RespondToAllianceAsync(b,app,2));
        var accepted = await rig.Store.RespondToAllianceAsync(a,app,2);
        rig.Now += 86399999;
        Assert.Empty(await rig.Store.ActivateDueAllianceApplicationsAsync());
        Assert.Null((await rig.Characters.GetAsync(b))!.AllianceId);
        Assert.Equal(accepted.DueMs,(await rig.Store.RespondToAllianceAsync(a,app,2)).DueMs);
        rig.Now++;
        Assert.Equal([corpB.Id],await rig.ReopenStore().ActivateDueAllianceApplicationsAsync());
        Assert.Empty(await rig.Store.ActivateDueAllianceApplicationsAsync());
        Assert.Equal(alliance.Id,(await rig.Characters.GetAsync(b))!.AllianceId);
        Assert.Equal(alliance.Id,(await rig.Store.GetAsync(corpB.Id))!.AllianceId);
        Assert.Equal(2,(await rig.Store.AllianceMembersAsync(alliance.Id)).Count);
        Assert.Equal(3,(await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
        await rig.Store.ChooseExecutorAsync(b,corpB.Id); // source ceil(2/2) and tied votes retain the incumbent
        Assert.Equal(corpA.Id,(await rig.Store.AllianceAsync(alliance.Id))!.ActiveExecutorId);
        await rig.Store.ChooseExecutorAsync(a,corpB.Id);
        Assert.Equal(corpB.Id,(await rig.Store.AllianceAsync(alliance.Id))!.ActiveExecutorId);
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

        var approved = await rig.Store.RespondToAllianceAsync(executor,approvedId,"Updated approved text",2);
        var approvedReplay = await rig.Store.RespondToAllianceAsync(executor,approvedId,"Updated approved text",2);
        Assert.Equal(approved,approvedReplay);
        Assert.Equal("Updated approved text",approved.Text);
        await Assert.ThrowsAsync<CorporationError>(() =>
            rig.Store.RespondToAllianceAsync(executor,approvedId,"Conflicting approved text",2));

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
        var (actor,corp) = await Founder(rig,"Founder One","FIRST");
        var alliance = await rig.Store.CreateAllianceAsync(actor,Request,"first");
        var session = new Eve.Services.ClientSession { SessionId=77 };
        session.Set("charid",new Eve.Marshal.PyInt(actor));
        var service = new Eve.Services.Corporations.AllianceRegistryService(rig.Store,rig.Characters,() => rig.Now);
        Eve.Services.ServiceCall Call(string name,params Eve.Marshal.PyValue[] args) => new() { Session=session,Method=name,Args=args };
        var info = await service.GetAlliance(Call("GetAlliance",new Eve.Marshal.PyInt(alliance.Id)));
        var fields = Assert.IsType<Eve.Marshal.PyDict>(Assert.IsType<Eve.Marshal.PyObject>(info).Args);
        Assert.Equal(new Eve.Marshal.PyInt(1),fields.Entries.Single(e => e.Key == new Eve.Marshal.PyStr("memberCount")).Value);
        Assert.Equal(new Eve.Marshal.PyInt(corp.Id),fields.Entries.Single(e => e.Key == new Eve.Marshal.PyStr("executorCorpID")).Value);
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
        Assert.True(corpRegistry.HasMethod("ApplyToJoinAlliance"));
        Assert.True(corpRegistry.HasMethod("DeleteAllianceApplication"));
    }

    [PgFact]
    public async Task Alliance_registry_lists_pending_requests_and_executor_can_accept_or_reject_them()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,_) = await Founder(rig,"Executor","EXE");
        var (member,memberCorp) = await Founder(rig,"Member","MEM");
        var (applicant,applicantCorp) = await Founder(rig,"Applicant","APP");
        var (rejectedApplicant,rejectedCorp) = await Founder(rig,"Rejected","REJ");
        var alliance = await rig.Store.CreateAllianceAsync(executor,Request,"registry");
        var memberApplication = await rig.Store.ApplyToAllianceAsync(member,alliance.Id,"Join as member");
        await rig.Store.RespondToAllianceAsync(executor,memberApplication,2);
        rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs;
        Assert.Equal([memberCorp.Id],await rig.Store.ActivateDueAllianceApplicationsAsync());

        var application = await rig.Store.ApplyToAllianceAsync(applicant,alliance.Id,"Please accept");
        var rejected = await rig.Store.ApplyToAllianceAsync(rejectedApplicant,alliance.Id,"Please reject");
        var service = new Eve.Services.Corporations.AllianceRegistryService(rig.Store,rig.Characters,() => rig.Now);
        Eve.Services.ClientSession Session(long actor)
        {
            var session = new Eve.Services.ClientSession { SessionId=actor };
            session.Set("charid",new Eve.Marshal.PyInt(actor));
            return session;
        }
        Eve.Services.ServiceCall Call(long actor,string method,params Eve.Marshal.PyValue[] args) =>
            new() { Session=Session(actor),Method=method,Args=args };

        var executorCall = Call(executor,"GetApplications");
        var indexed = Assert.IsType<Eve.Marshal.PyObjectEx>(
            await ((Eve.Services.IService)service).CallAsync(executorCall));
        var applicantRow = Assert.IsType<Eve.Marshal.PyPackedRow>(
            indexed.Dict.Single(row => row.Key == new Eve.Marshal.PyInt(applicantCorp.Id)).Value);
        Assert.Equal(new Eve.Marshal.PyInt(alliance.Id),applicantRow.Values[0]);
        Assert.Equal(new Eve.Marshal.PyInt(applicantCorp.Id),applicantRow.Values[1]);
        Assert.Equal(new Eve.Marshal.PyWString("Please accept"),applicantRow.Values[2]);
        Assert.Equal(new Eve.Marshal.PyInt(1),applicantRow.Values[3]);

        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(member,"GetApplications")).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(member,"UpdateApplication",new Eve.Marshal.PyInt(applicantCorp.Id),
                new Eve.Marshal.PyWString("Please accept"),new Eve.Marshal.PyInt(4))).AsTask());

        var acceptCall = Call(executor,"UpdateApplication",new Eve.Marshal.PyInt(applicantCorp.Id),
            new Eve.Marshal.PyWString("Updated acceptance text"),new Eve.Marshal.PyInt(2));
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(acceptCall));
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(acceptCall));
        await Assert.ThrowsAsync<CorporationError>(() => ((Eve.Services.IService)service).CallAsync(
            Call(executor,"UpdateApplication",new Eve.Marshal.PyInt(applicantCorp.Id),
                new Eve.Marshal.PyWString("Conflicting acceptance text"),new Eve.Marshal.PyInt(2))).AsTask());

        var rejectCall = Call(executor,"UpdateApplication",new Eve.Marshal.PyInt(rejectedCorp.Id),
            new Eve.Marshal.PyWString("Updated rejection text"),new Eve.Marshal.PyInt(4));
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(rejectCall));
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(rejectCall));
        await Assert.ThrowsAsync<CorporationError>(() => ((Eve.Services.IService)service).CallAsync(
            Call(executor,"UpdateApplication",new Eve.Marshal.PyInt(rejectedCorp.Id),
                new Eve.Marshal.PyWString("Conflicting rejection text"),new Eve.Marshal.PyInt(4))).AsTask());

        var applications = await rig.Store.AllianceApplicationsAsync(alliance.Id);
        Assert.Equal(2,applications.Single(item => item.Id == application).Status);
        Assert.Equal("Updated acceptance text",applications.Single(item => item.Id == application).Text);
        Assert.Equal(rig.Now + Eve.Corporations.AllianceRules.ApplicationDelayMs,
            applications.Single(item => item.Id == application).DueMs);
        Assert.Equal(4,applications.Single(item => item.Id == rejected).Status);
        Assert.Equal("Updated rejection text",applications.Single(item => item.Id == rejected).Text);
        Assert.True(applications.Single(item => item.Id == rejected).Archived);
        Assert.Null((await rig.Characters.GetAsync(applicant))!.AllianceId);
    }

    [PgFact]
    public async Task Alliance_registry_requests_activate_due_applications_and_notify_after_commit()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,_) = await Founder(rig,"Due Executor","DUEEX");
        var (applicant,applicantCorp) = await Founder(rig,"Due Applicant","DUEAP");
        var alliance = await rig.Store.CreateAllianceAsync(executor,Request,"due");
        var application = await rig.Store.ApplyToAllianceAsync(applicant,alliance.Id,"Join after a day");
        await rig.Store.RespondToAllianceAsync(executor,application,2);

        var changedCharacters = new List<long>();
        var service = (Eve.Services.IService)new Eve.Services.Corporations.AllianceRegistryService(
            rig.Store,rig.Characters,() => rig.Now,async (characterId,token) =>
            {
                Assert.Equal(alliance.Id,(await rig.Characters.GetAsync(characterId,token))!.AllianceId);
                changedCharacters.Add(characterId);
            });
        Eve.Services.ServiceCall Call(long actor,string method,params Eve.Marshal.PyValue[] args)
        {
            var session = new Eve.Services.ClientSession { SessionId=actor };
            session.Set("charid",new Eve.Marshal.PyInt(actor));
            return new() { Session=session,Method=method,Args=args };
        }

        rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs - 1;
        await service.CallAsync(Call(executor,"GetAllianceMembers",new Eve.Marshal.PyInt(alliance.Id)));
        Assert.Null((await rig.Characters.GetAsync(applicant))!.AllianceId);
        Assert.Single(await rig.Store.AllianceMembersAsync(alliance.Id));
        Assert.Equal(2,(await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
        Assert.Empty(changedCharacters);

        rig.Now++;
        var applications = Assert.IsType<Eve.Marshal.PyObjectEx>(
            await service.CallAsync(Call(executor,"GetApplications")));
        var effectiveApplication = Assert.IsType<Eve.Marshal.PyPackedRow>(
            applications.Dict.Single(row => row.Key == new Eve.Marshal.PyInt(applicantCorp.Id)).Value);
        Assert.Equal(new Eve.Marshal.PyInt(3),effectiveApplication.Values[3]);
        Assert.Equal([applicant],changedCharacters);
        Assert.Equal(alliance.Id,(await rig.Characters.GetAsync(applicant))!.AllianceId);
        Assert.Equal(alliance.Id,(await rig.Store.GetAsync(applicantCorp.Id))!.AllianceId);
        Assert.Equal(3,(await rig.Store.AllianceApplicationsAsync(alliance.Id)).Single().Status);
        Assert.Equal(2,(await rig.Store.AllianceMembersAsync(alliance.Id)).Count);
    }

    [PgFact]
    public async Task Corp_registry_application_calls_use_session_corporation_for_create_and_withdrawal()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,executorCorp) = await Founder(rig,"Executor","EXE");
        var (applicant,applicantCorp) = await Founder(rig,"Applicant","APP");
        var outsider = await rig.CharacterAsync("Alliance outsider");
        var alliance = await rig.Store.CreateAllianceAsync(executor,Request,"corp-registry");
        var service = new Eve.Services.Corporations.CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);

        Eve.Services.ClientSession Session(long actor)
        {
            var session = new Eve.Services.ClientSession { SessionId=actor };
            session.Set("charid",new Eve.Marshal.PyInt(actor));
            return session;
        }
        Eve.Services.ServiceCall Call(long actor,string method,params Eve.Marshal.PyValue[] args) =>
            new() { Session=Session(actor),Method=method,Args=args };

        Eve.Marshal.PyValue[] applicationArgs =
        [
            new Eve.Marshal.PyInt(alliance.Id),
            new Eve.Marshal.PyWString("Please join"),
            new Eve.Marshal.PyInt(executorCorp.Id)
        ];
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicant,"ApplyToJoinAlliance",applicationArgs)));
        var applications = await rig.Store.AllianceApplicationsAsync(alliance.Id);
        var application = Assert.Single(applications,item => item.CorporationId == applicantCorp.Id);
        Assert.Equal("Please join",application.Text);

        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(outsider,"ApplyToJoinAlliance",new Eve.Marshal.PyInt(alliance.Id),
                new Eve.Marshal.PyWString("Impersonated request"))).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(applicant,"ApplyToJoinAlliance",new Eve.Marshal.PyInt(alliance.Id),
                new Eve.Marshal.PyWString("Please join again"))).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => ((Eve.Services.IService)service).CallAsync(
            Call(outsider,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id))).AsTask());

        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(executor,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id),
                new Eve.Marshal.PyInt(applicantCorp.Id))));
        Assert.Contains(await rig.Store.AllianceApplicationsAsync(alliance.Id),
            item => item.CorporationId == applicantCorp.Id);

        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicant,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id))));
        Assert.DoesNotContain(await rig.Store.AllianceApplicationsAsync(alliance.Id),
            item => item.CorporationId == applicantCorp.Id);
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicant,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id))));

        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicant,"ApplyToJoinAlliance",new Eve.Marshal.PyInt(alliance.Id),
                new Eve.Marshal.PyWString("Please join again"))));
        var rejected = Assert.Single(await rig.Store.AllianceApplicationsAsync(alliance.Id),
            item => item.CorporationId == applicantCorp.Id);
        await rig.Store.RespondToAllianceAsync(executor,rejected.Id,"Rejected request",4);
        Assert.True((await rig.Store.AllianceApplicationsAsync(alliance.Id))
            .Single(item => item.Id == rejected.Id).Archived);
        Assert.IsType<Eve.Marshal.PyNone>(await ((Eve.Services.IService)service).CallAsync(
            Call(applicant,"DeleteAllianceApplication",new Eve.Marshal.PyInt(alliance.Id))));
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
            "GetAllianceMembersOlderThan","UpdateAlliance","GetAlliancePublicInfo"
        }) Assert.True(alliance.HasMethod(method),method);

        var corporation = (Eve.Services.IService)new Eve.Services.Corporations.CorpRegistryService(null!,null!,() => 0);
        foreach (var method in new[] { "CreateAlliance","GetAllianceApplications","GetSuggestedAllianceShortNames" })
            Assert.True(corporation.HasMethod(method),method);
    }

    [PgFact]
    public async Task Alliance_membership_reads_and_public_info_updates_match_reference_shapes()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,_) = await Founder(rig,"Read Executor","RDEXE");
        var (member,memberCorp) = await Founder(rig,"Read Member","RDMEM");
        var alliance = await rig.Store.CreateAllianceAsync(executor,Request,"reads");
        var application = await rig.Store.ApplyToAllianceAsync(member,alliance.Id,"Read-side join");
        await rig.Store.RespondToAllianceAsync(executor,application,2);
        rig.Now += Eve.Corporations.AllianceRules.ApplicationDelayMs;
        Assert.Equal([memberCorp.Id],await rig.Store.ActivateDueAllianceApplicationsAsync());
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

        var ranked = Assert.IsType<Eve.Marshal.PyList>(await service.CallAsync(Call(executor,"GetRankedAlliances",new Eve.Marshal.PyInt(10))));
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
    public async Task Corp_registry_creates_alliance_idempotently_and_lists_its_applications()
    {
        await using var rig = await Rig.CreateAsync();
        var (executor,_) = await Founder(rig,"RPC Executor","RPCEX");
        var (applicant,applicantCorp) = await Founder(rig,"RPC Applicant","RPCAP");
        var service = (Eve.Services.IService)new Eve.Services.Corporations.CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        Eve.Services.ClientSession Session(long actor)
        {
            var session = new Eve.Services.ClientSession { SessionId=actor };
            session.Set("charid",new Eve.Marshal.PyInt(actor));
            return session;
        }
        Eve.Services.ServiceCall Call(long actor,string method,params Eve.Marshal.PyValue[] args) =>
            new() { Session=Session(actor),Method=method,Args=args,CallId=new Eve.Marshal.PyInt(51) };

        var before = (await rig.Wallets.GetAsync(executor))!.Isk;
        var create = Call(executor,"CreateAlliance",new Eve.Marshal.PyWString("RPC Alliance"),new Eve.Marshal.PyWString("RPCA"),
            new Eve.Marshal.PyWString("Created by RPC"),new Eve.Marshal.PyWString(""));
        var allianceId = Assert.IsType<Eve.Marshal.PyInt>(await service.CallAsync(create));
        Assert.Equal(allianceId,await service.CallAsync(create));
        Assert.Equal(before-1000000000m,(await rig.Wallets.GetAsync(executor))!.Isk);

        Assert.IsType<Eve.Marshal.PyNone>(await service.CallAsync(Call(applicant,"ApplyToJoinAlliance",allianceId,
            new Eve.Marshal.PyWString("Application row"))));
        var applications = Assert.IsType<Eve.Marshal.PyObjectEx>(await service.CallAsync(Call(applicant,"GetAllianceApplications")));
        var application = Assert.IsType<Eve.Marshal.PyPackedRow>(applications.Dict.Single(entry => entry.Key == allianceId).Value);
        Assert.Equal(allianceId,application.Values[0]);
        Assert.Equal(new Eve.Marshal.PyInt(applicantCorp.Id),application.Values[1]);
        Assert.Equal(new Eve.Marshal.PyWString("Application row"),application.Values[2]);
        Assert.Equal(new Eve.Marshal.PyInt(1),application.Values[3]);
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
}
