namespace Eve.Corporations.Tests;

public class MembershipTests
{
    private static readonly NewCorporation Request = new("Orbital Works","ORBIT","","",0.05m);
    private static async Task Join(Rig rig,long founder,long member,long corp)
    {
        var application = await rig.Store.ApplyAsync(member,corp,"Please accept.");
        Assert.Equal(application,await rig.Store.ApplyAsync(member,corp,"Please accept."));
        await rig.Store.RespondAsync(founder,application,6,"Please accept.","Welcome");
        Assert.Equal(1000044,(await rig.Characters.GetAsync(member))!.CorporationId);
        await rig.Store.RespondAsync(member,application,2,"Please accept.","Accepted");
    }

    [PgFact]
    public async Task An_offer_is_accepted_by_the_applicant_and_membership_and_other_applications_change_atomically()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var corp = await rig.Store.CreateAsync(founder,Request,"first");
        await Join(rig,founder,member,corp.Id);
        Assert.Equal(corp.Id,(await rig.Characters.GetAsync(member))!.CorporationId);
        Assert.Equal(2,(await rig.Store.MembersAsync(corp.Id)).Count);
        Assert.Empty(await rig.Store.ApplicationsAsync(corporation:corp.Id));
        Assert.Single(await rig.Store.ApplicationsAsync(corporation:corp.Id,archived:true));
        Assert.Equal(corp.Id,(await rig.Store.EmploymentAsync(member)).First().CorporationId);
        var app = (await rig.Store.ApplicationsAsync(corporation:corp.Id,archived:true)).Single();
        await rig.Store.RespondAsync(member,app.Id,2,app.Text,app.Response);
        Assert.Equal(2,(await rig.Store.EmploymentAsync(member)).Count);
    }

    [PgFact]
    public async Task Roles_are_exact_longs_director_promotion_is_canonical_and_self_resignation_keeps_stasis()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var corp = await rig.Store.CreateAsync(founder,Request,"first");
        await Join(rig,founder,member,corp.Id);
        Assert.Equal(16,(await rig.Store.TitlesAsync(corp.Id)).Count);
        var old = (await rig.Store.MemberAsync(member))!;
        var granted = await rig.Store.UpdateMemberAsync(founder,old with { Masks = new RoleMasks(CorporationRoles.Trader | CorporationRoles.WalletTakeRole(1000)) });
        Assert.Equal(18014398643699712,granted.Masks.Roles);
        await rig.Store.SetAccountKeyAsync(member,1000);
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.SetAccountKeyAsync(member,1006));
        var denied = await Assert.ThrowsAsync<CorporationError>(() => rig.Store.UpdateMemberAsync(member,granted with { Masks = new RoleMasks(1) }));
        Assert.Equal("CORPORATION_ACCESS_DENIED",denied.Code);
        var promoted = await rig.Store.UpdateMemberAsync(founder,granted with { Masks = new RoleMasks(granted.Masks.Roles | 1),TitleMask = 1 });
        Assert.Equal(new RoleMasks(1),promoted.Masks);
        Assert.Equal(0,promoted.TitleMask);
        var demoted = await rig.Store.UpdateMemberAsync(founder,promoted with { Masks = new RoleMasks() });
        Assert.Equal(rig.Now + 86400000,demoted.StasisUntilMs);
        var resigned = await rig.Store.UpdateMemberAsync(member,demoted with { BlockRoles = true });
        Assert.True(resigned.BlockRoles);
        var before = resigned.StasisUntilMs;
        var replay = await rig.Store.UpdateMemberAsync(member,resigned);
        Assert.Equal(before,replay.StasisUntilMs);
    }

    [PgFact]
    public async Task A_failed_bulk_role_update_rolls_back_the_earlier_member()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var outsider = await rig.CharacterAsync("Other Founder");
        var corp = await rig.Store.CreateAsync(founder,Request,"first");
        await Join(rig,founder,member,corp.Id);
        await rig.Store.CreateAsync(outsider,new NewCorporation("Other Corp","OTHER","","",0),"other");
        var old = (await rig.Store.MemberAsync(member))!;
        var foreign = (await rig.Store.MemberAsync(outsider))!;
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.UpdateMembersAsync(founder,[old with { Masks = new RoleMasks(256) },foreign]));
        Assert.Equal(old,(await rig.Store.MemberAsync(member))!);
    }

    [PgFact]
    public async Task An_unrelated_character_cannot_accept_or_manage_somebody_elses_application()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var outsider = await rig.CharacterAsync("Outsider");
        var corp = await rig.Store.CreateAsync(founder,Request,"first");
        var app = await rig.Store.ApplyAsync(member,corp.Id,"Hello");
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.RespondAsync(outsider,app,6,"Hello",""));
        await rig.Store.RespondAsync(founder,app,6,"Hello","");
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.RespondAsync(outsider,app,2,"Hello",""));
        Assert.Equal(1000044,(await rig.Characters.GetAsync(member))!.CorporationId);
        Assert.Single(await rig.Store.MembersAsync(corp.Id));
    }
}
