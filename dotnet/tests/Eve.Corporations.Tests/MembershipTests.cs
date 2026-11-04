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
    public async Task Ceo_resignation_changes_both_leadership_and_the_old_director_mask()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var successor = await rig.CharacterAsync("Successor One");
        var corp = await rig.Store.CreateAsync(founder,Request,"first");
        await Join(rig,founder,successor,corp.Id);
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.ResignAsync(successor,founder));
        Assert.Equal(founder,(await rig.Store.GetAsync(corp.Id))!.CeoId);
        await rig.Store.ResignAsync(founder,successor);
        Assert.Equal(successor,(await rig.Store.GetAsync(corp.Id))!.CeoId);
        Assert.Equal(0,(await rig.Store.MemberAsync(founder))!.Masks.Roles & CorporationRoles.Director);
        var current = (await rig.Store.GetAsync(corp.Id))!;
        Assert.Equal(CorporationRoles.All,CorporationRoles.Session(current,(await rig.Store.MemberAsync(successor))!,current.StationId,[]).CorpRole);
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.ResignAsync(founder,successor));
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

    [PgFact]
    public async Task Eve_owners_are_util_rows_in_owner_header_order()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var corp = await rig.Store.CreateAsync(founder,Request,"first");
        await Join(rig,founder,member,corp.Id);

        var service = new Eve.Services.Corporations.CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        var result = Assert.IsType<Eve.Marshal.PyList>(await service.GetEveOwners(new Eve.Services.ServiceCall
        {
            Method = "GetEveOwners",
            Args = [],
            Session = new Eve.Services.ClientSession { CharacterId = founder },
        }));
        var rows = result.Items.Select(value => Assert.IsType<Eve.Marshal.PyObject>(value)).ToArray();
        Assert.Equal(2,rows.Length);
        var actualIDs = new List<long>();
        foreach (var row in rows)
        {
            Assert.Equal("util.Row",Assert.IsType<Eve.Marshal.PyStr>(row.Name).Value);
            var fields = Assert.IsType<Eve.Marshal.PyDict>(row.Args);
            Eve.Marshal.PyValue Field(string name) => fields.Entries.Single(entry =>
                Assert.IsType<Eve.Marshal.PyStr>(entry.Key).Value == name).Value;
            var header = Assert.IsType<Eve.Marshal.PyTuple>(Field("header")).Items
                .Select(value => Assert.IsType<Eve.Marshal.PyStr>(value).Value).ToArray();
            Assert.Equal(new[] { "ownerID","ownerName","typeID","gender","ownerNameID" },header);

            var line = Assert.IsType<Eve.Marshal.PyTuple>(Field("line")).Items;
            var characterID = Assert.IsType<Eve.Marshal.PyInt>(line[0]).Value;
            actualIDs.Add(characterID);
            Assert.Equal(characterID == founder ? "Founder One" : "Member One",
                Assert.IsType<Eve.Marshal.PyWString>(line[1]).Value);
            Assert.Equal(1376L,Assert.IsType<Eve.Marshal.PyInt>(line[2]).Value);
            Assert.Equal(0L,Assert.IsType<Eve.Marshal.PyInt>(line[3]).Value);
            Assert.IsType<Eve.Marshal.PyNone>(line[4]);
        }
        Assert.Equal(new[] { founder,member }.Order().ToArray(),actualIDs.Order().ToArray());
    }

    [PgFact]
    public async Task Execute_actions_apply_role_title_and_base_updates_for_all_targets()
    {
        const long ctvAdd = 1;
        const long ctvRemove = 2;
        const long ctvSet = 3;
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var first = await rig.CharacterAsync("Member One");
        var second = await rig.CharacterAsync("Member Two");
        var corp = await rig.Store.CreateAsync(founder,Request,"first");
        await Join(rig,founder,first,corp.Id);
        await Join(rig,founder,second,corp.Id);
        var service = new Eve.Services.Corporations.CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);

        static Eve.Marshal.PyValue Action(long verb,string property,Eve.Marshal.PyValue value) =>
            new Eve.Marshal.PyList([new Eve.Marshal.PyInt(verb),new Eve.Marshal.PyWString(property),value]);
        var role = Eve.Corporations.CorporationRoles.Trader;
        var extraRole = Eve.Corporations.CorporationRoles.WalletTakeRole(1000);
        var call = new Eve.Services.ServiceCall
        {
            Method = "ExecuteActions",
            Args =
            [
                new Eve.Marshal.PyList([new Eve.Marshal.PyInt(first),new Eve.Marshal.PyInt(second)]),
                new Eve.Marshal.PyList([
                    Action(ctvSet,"roles",new Eve.Marshal.PyLong(role)),
                    Action(ctvAdd,"roles",new Eve.Marshal.PyLong(extraRole)),
                    Action(ctvRemove,"roles",new Eve.Marshal.PyLong(extraRole)),
                    Action(ctvSet,"grantableRoles",new Eve.Marshal.PyLong(extraRole)),
                    Action(ctvSet,"titleMask",new Eve.Marshal.PyInt(1)),
                    Action(ctvAdd,"titleMask",new Eve.Marshal.PyInt(6)),
                    Action(ctvRemove,"titleMask",new Eve.Marshal.PyInt(2)),
                    Action(ctvSet,"baseID",new Eve.Marshal.PyInt(60000001))
                ])
            ],
            Session = new Eve.Services.ClientSession { CharacterId = founder },
        };

        await service.ExecuteActions(call);
        foreach (var id in new[] { first,second })
        {
            var member = (await rig.Store.MemberAsync(id))!;
            Assert.Equal(role,member.Masks.Roles);
            Assert.Equal(extraRole,member.Masks.GrantableRoles);
            Assert.Equal(5,member.TitleMask);
            Assert.Equal(60000001L,member.BaseId);
        }

        var clearBaseCall = new Eve.Services.ServiceCall
        {
            Method = "ExecuteActions",
            Args =
            [
                new Eve.Marshal.PyList([new Eve.Marshal.PyInt(first)]),
                new Eve.Marshal.PyList([Action(ctvSet,"baseID",Eve.Marshal.PyNone.Instance)])
            ],
            Session = new Eve.Services.ClientSession { CharacterId = founder },
        };
        await service.ExecuteActions(clearBaseCall);
        Assert.Null((await rig.Store.MemberAsync(first))!.BaseId);
    }

    [PgFact]
    public async Task Execute_actions_reject_duplicate_foreign_and_give_batches_without_partial_changes()
    {
        const long ctvAdd = 1;
        const long ctvGive = 4;
        const long ctpgCash = 6;
        const long ctpgShares = 7;
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var outsider = await rig.CharacterAsync("Other Founder");
        var corp = await rig.Store.CreateAsync(founder,Request,"first");
        await Join(rig,founder,member,corp.Id);
        await rig.Store.CreateAsync(outsider,new NewCorporation("Other Corp","OTHER","","",0),"other");
        var before = (await rig.Store.MemberAsync(member))!;
        var service = new Eve.Services.Corporations.CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);

        static Eve.Marshal.PyValue Action(long verb,string property,Eve.Marshal.PyValue value) =>
            new Eve.Marshal.PyList([new Eve.Marshal.PyInt(verb),new Eve.Marshal.PyWString(property),value]);
        Eve.Services.ServiceCall Call(Eve.Marshal.PyList targets,Eve.Marshal.PyList actions) => new()
        {
            Method = "ExecuteActions",Args = [targets,actions],
            Session = new Eve.Services.ClientSession { CharacterId = founder },
        };
        var setRole = new Eve.Marshal.PyList([
            Action(ctvAdd,"roles",new Eve.Marshal.PyLong(Eve.Corporations.CorporationRoles.Trader))
        ]);
        var duplicateCall = Call(
            new Eve.Marshal.PyList([new Eve.Marshal.PyInt(member),new Eve.Marshal.PyInt(member)]),setRole);
        var duplicate = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(
            () => service.ExecuteActions(duplicateCall).AsTask());
        Assert.Equal("CORPORATION_ACCESS_DENIED",duplicate.Code);

        var foreignCall = Call(
            new Eve.Marshal.PyList([new Eve.Marshal.PyInt(member),new Eve.Marshal.PyInt(outsider)]),setRole);
        var foreign = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(
            () => service.ExecuteActions(foreignCall).AsTask());
        Assert.Equal("CORPORATION_ACCESS_DENIED",foreign.Code);

        var mixedCall = Call(new Eve.Marshal.PyList([new Eve.Marshal.PyInt(member)]),new Eve.Marshal.PyList([
            Action(ctvAdd,"roles",new Eve.Marshal.PyLong(Eve.Corporations.CorporationRoles.Trader)),
            new Eve.Marshal.PyList([
                new Eve.Marshal.PyInt(ctvGive),new Eve.Marshal.PyInt(ctpgCash),new Eve.Marshal.PyInt(1000)
            ])
        ]));
        var mixed = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(
            () => service.ExecuteActions(mixedCall).AsTask());
        Assert.Equal("CORPORATION_ACCESS_DENIED",mixed.Code);

        var holdingsBeforeShareGive = await rig.Store.ShareholdersAsync(corp.Id);
        var sharesCall = Call(new Eve.Marshal.PyList([new Eve.Marshal.PyInt(member)]),new Eve.Marshal.PyList([
            new Eve.Marshal.PyList([
                new Eve.Marshal.PyInt(ctvGive),new Eve.Marshal.PyInt(ctpgShares),new Eve.Marshal.PyInt(1)
            ])
        ]));
        var shares = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(
            () => service.ExecuteActions(sharesCall).AsTask());
        Assert.Equal("CORPORATION_ACCESS_DENIED",shares.Code);
        Assert.Equal(before,(await rig.Store.MemberAsync(member))!);
        Assert.Equal(holdingsBeforeShareGive,await rig.Store.ShareholdersAsync(corp.Id));
    }

    [PgFact]
public async Task Potential_ceo_query_returns_all_member_character_ids()
{
    await using var rig = await Rig.CreateAsync();
    var founder = await rig.CharacterAsync("Founder One");
    var member = await rig.CharacterAsync("Member One");
    var corp = await rig.Store.CreateAsync(founder, Request, "first");
    await Join(rig, founder, member, corp.Id);

    var service = new Eve.Services.Corporations.CorpRegistryService(rig.Store, rig.Characters, () => rig.Now);
    var call = new Eve.Services.ServiceCall
    {
        Method = "GetNumberOfPotentialCEOs",
        Args = [],
        Session = new Eve.Services.ClientSession { CharacterId = founder },
    };

    var result = Assert.IsType<Eve.Marshal.PyList>(await service.GetNumberOfPotentialCEOs(call));
    var characterIds = result.Items
        .Select(value => Assert.IsType<Eve.Marshal.PyInt>(value).Value)
        .Order()
        .ToArray();
    Assert.Equal(new[] { founder, member }.Order().ToArray(), characterIds);
}
}
