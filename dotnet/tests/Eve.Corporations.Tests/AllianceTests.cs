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
}
