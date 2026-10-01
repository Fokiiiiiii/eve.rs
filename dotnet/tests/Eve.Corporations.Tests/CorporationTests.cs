using System.Text.Json;
using Eve.Wallet;

namespace Eve.Corporations.Tests;

public class CorporationTests
{
    private static NewCorporation Request(string name = "Orbital Works",string ticker = "ORBIT") =>
        new(name,ticker,"A corporation.","https://example.invalid",0.05m,Settings:JsonSerializer.SerializeToElement(new { shape1 = 1,color1 = 2 }));

    [PgFact]
    public async Task Founding_and_replaying_it_charge_once_and_keep_affiliation_and_history_consistent()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var corp = await rig.Store.CreateAsync(founder,Request(),"found-once");
        Assert.Equal(98000000,corp.Id);
        Assert.Equal(founder,corp.CeoId);
        Assert.Equal(corp.Id,(await rig.Characters.GetAsync(founder))!.CorporationId);
        Assert.Equal(CorporationRoles.Director,(await rig.Store.MemberAsync(founder))!.Masks.Roles);
        Assert.Equal(3400200m,(await rig.Wallets.GetAsync(founder))!.Isk);
        Assert.Equal(0m,(await rig.Wallets.GetAsync(corp.Id))!.Isk);
        Assert.Equal(JsonSerializer.Serialize(corp),JsonSerializer.Serialize(await rig.Store.GetAsync(corp.Id)));
        Assert.Equal(corp.Id,(await rig.Store.CreateAsync(founder,Request(),"found-once")).Id);
        Assert.Single(await rig.Transactions.JournalAsync(founder));
        var history = await rig.Store.EmploymentAsync(founder);
        Assert.Equal(2,history.Count);
        Assert.Single(history,e => e.EndedMs is null);
        Assert.Equal(corp.Id,history.Single(e => e.EndedMs is null).CorporationId);
        Assert.Equal(1000044,history.Single(e => e.EndedMs is not null).CorporationId);
        Assert.All(history,e => Assert.True(e.EndedMs is null || e.EndedMs >= e.StartedMs));
    }

    [PgFact]
    public async Task Insufficient_funds_leave_no_corporation_membership_or_fee()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Poor Founder",100m);
        await Assert.ThrowsAsync<InsufficientFundsException>(() => rig.Store.CreateAsync(founder,Request(),"poor"));
        Assert.Equal(1000044,(await rig.Characters.GetAsync(founder))!.CorporationId);
        Assert.Equal(100m,(await rig.Wallets.GetAsync(founder))!.Isk);
        Assert.Null(await rig.Store.MemberAsync(founder));
        Assert.Empty(await rig.Store.EmploymentAsync(founder));
        Assert.Null(await rig.Store.GetAsync(98000000));
        Assert.Empty(await rig.Transactions.JournalAsync(founder));
    }

    [PgFact]
    public async Task Concurrent_claims_of_one_name_cannot_both_charge_and_create_it()
    {
        await using var rig = await Rig.CreateAsync();
        var a = await rig.CharacterAsync("Founder A");
        var b = await rig.CharacterAsync("Founder B");
        async Task<bool> Found(long id,string ticker)
        {
            try { await rig.Store.CreateAsync(id,Request("Shared Name",ticker),"found-" + id); return true; }
            catch (CorporationError error) when (error.Code == "CORPORATION_NAME_TAKEN") { return false; }
        }
        Assert.Equal(1,(await Task.WhenAll(Found(a,"AAAAA"),Found(b,"BBBBB"))).Count(x => x));
        Assert.Equal(8400200m,(await rig.Wallets.GetAsync(a))!.Isk + (await rig.Wallets.GetAsync(b))!.Isk);
        Assert.Equal(1,(await rig.Transactions.JournalAsync(a)).Count + (await rig.Transactions.JournalAsync(b)).Count);
    }

    [PgFact]
    public async Task Reusing_a_founding_key_with_other_terms_is_rejected_without_a_second_fee()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        await rig.Store.CreateAsync(founder,Request(),"fixed-key");
        var error = await Assert.ThrowsAsync<CorporationError>(() => rig.Store.CreateAsync(founder,Request("Other Name","OTHER"),"fixed-key"));
        Assert.Equal("FOUNDING_OPERATION_CONFLICT",error.Code);
        Assert.Equal(3400200m,(await rig.Wallets.GetAsync(founder))!.Isk);
        Assert.Single(await rig.Transactions.JournalAsync(founder));
    }

    [Fact]
    public void Directors_and_titles_have_the_references_effective_and_grantable_masks()
    {
        var corp = new Corporation(98000000,"Corp","CORP",1,1,null,10,100,0,0,"","",20,false,false,0,JsonSerializer.SerializeToElement(new {}));
        var ceo = new Member(1,corp.Id,0,"",new RoleMasks(1),20,0,false,1000);
        var roles = CorporationRoles.Session(corp,ceo,10,[]);
        Assert.Equal(CorporationRoles.All,roles.CorpRole);
        Assert.Equal(CorporationRoles.Administrative,roles.AtAll);
        Assert.Equal(CorporationRoles.Locational,roles.AtHq);
        Assert.Equal(CorporationRoles.Administrative,CorporationRoles.GrantableFor(corp,ceo,corp.Id,[]).GrantableRoles);
        var director = ceo with { CharacterId = 2 };
        Assert.Equal(CorporationRoles.Grantable,CorporationRoles.GrantableFor(corp,director,corp.Id,[]).GrantableRoles);
        var member = ceo with { CharacterId = 3,Masks = new RoleMasks(256,256,8192),TitleMask = 1 };
        var titled = CorporationRoles.Session(corp,member,10,[new CorporationTitle(1,"Personnel",new RoleMasks(128))]);
        Assert.Equal(256 | 128 | 8192,titled.CorpRole);
        Assert.Equal(256 | 128,titled.AtAll);
        Assert.Equal(256 | 128,CorporationRoles.Session(corp,member,30,[new CorporationTitle(1,"Personnel",new RoleMasks(128))]).CorpRole);
        Assert.Equal(0,CorporationRoles.Session(corp,member with { Masks = new RoleMasks(16) },10,[]).AtAll);
        Assert.Equal(new RoleMasks(),CorporationRoles.GrantableFor(corp,member,123,[]));
        Assert.Equal(8589934592,CorporationRoles.WalletTakeRole(1006));
        Assert.Equal(0,CorporationRoles.WalletTakeRole(1007));
    }
}
