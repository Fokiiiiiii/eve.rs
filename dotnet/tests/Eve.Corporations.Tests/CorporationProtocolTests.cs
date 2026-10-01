using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Corporations.Tests;

public class CorporationProtocolTests
{
    private static ServiceCall Call(ClientSession session,string method,params PyValue[] args) => new() { Session=session,Method=method,Args=args };
    private static PyValue Field(PyValue value,string key) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries.Single(e => e.Key == new PyStr(key)).Value;

    [PgFact]
    public async Task Npc_catalog_membership_and_departure_keep_the_character_faction()
    {
        await using var rig = await Rig.CreateAsync();
        var sde = new SdeReader(rig.Source);
        await rig.Store.SeedNpcAsync(sde);
        var npc = (await rig.Store.GetAsync(1000044))!;
        Assert.True(npc.IsNpc);
        Assert.Equal(-1,npc.MemberLimit);
        await rig.Store.SeedNpcAsync(sde); // static refresh is idempotent
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        Assert.Equal(2,(await rig.Store.MembersAsync(npc.Id)).Count);
        Assert.Equal(new RoleMasks(),(await rig.Store.MemberAsync(founder))!.Masks);
        var corp = await rig.Store.CreateAsync(founder,new("Orbital Works","ORBIT","","",0.05m),"first");
        var app = await rig.Store.ApplyAsync(member,corp.Id,"Join");
        await rig.Store.RespondAsync(founder,app,6,"Join","");
        await rig.Store.RespondAsync(member,app,2,"Join","");
        await rig.Store.LeaveAsync(member,npc.Id);
        Assert.Equal(npc.Id,(await rig.Characters.GetAsync(member))!.CorporationId);
        Assert.Equal(npc.Settings.GetProperty("factionID").GetInt64(),(await rig.Characters.GetAsync(member))!.FactionId);
        Assert.Equal(3,(await rig.Store.EmploymentAsync(member)).Count);
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.CreateAsync(member,new("Other Works",npc.Ticker,"","",0),"claimed-npc"));
        Assert.Equal(5000000m,(await rig.Wallets.GetAsync(member))!.Isk);
    }

    [PgFact]
    public async Task Public_rows_packed_members_titles_login_and_bind_are_native_client_shapes()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var corp = await rig.Store.CreateAsync(founder,new("Orbital Works","ORBIT","","",0.05m),"first");
        var session = new ClientSession { SessionId=7 };
        session.Set("charid",new PyInt(founder));
        session.Set("stationid",new PyInt(corp.StationId));
        session.Set("corpid",new PyInt(123)); // canonical database affiliation wins
        var hook = new CorporationLoginHook(rig.Store,rig.Characters);
        await hook.OnCharacterEnteredAsync(session,founder,default);
        Assert.Equal(corp.Id,session.GetInt("corpid"));
        Assert.Equal(new PyLong(CorporationRoles.All),session.Get("corprole"));
        var registry = new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        var row = await registry.GetCorporation(Call(session,"GetCorporation"));
        var header = Assert.IsType<PyTuple>(Field(row,"header")).Items;
        var line = Assert.IsType<PyTuple>(Field(row,"line")).Items;
        Assert.Equal(51,header.Count);
        Assert.Equal(header.Count,line.Count);
        Assert.Equal(new PyStr("corporationID"),header[0]);
        Assert.Equal(new PyInt(corp.Id),line[0]);
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(await registry.GetMember(Call(session,"GetMember",new PyInt(founder)))));
        var packed = Assert.IsType<PyPackedRow>(decoded);
        Assert.Equal(20,packed.Descriptor.Columns.Count);
        Assert.Equal("roles",packed.Descriptor.Columns[5].Name);
        Assert.Equal(64,packed.Descriptor.Columns[5].DbType);
        Assert.Equal(new PyInt(1),packed.Values[5]); // Packed numeric decoding normalizes small integers.
        var titles = Assert.IsType<PyObjectEx>(MarshalCodec.Decode(MarshalCodec.Encode(await registry.GetTitles(Call(session,"GetTitles")))));
        Assert.Equal(16,titles.Dict.Count);
        var local = Assert.IsType<PyList>(registry.GetLocationalRoles(Call(session,"GetLocationalRoles")));
        Assert.Equal(CorporationRoles.Locational,local.Items.Cast<PyLong>().Aggregate(0L,(mask,item) => mask | item.Value));
        var bound = Assert.IsType<PyTuple>(await registry.MachoBindObject(Call(session,"MachoBindObject",
            new PyInt(corp.Id),Py.Tuple(new PyStr("GetCorporation"),Py.Tuple(),PyNone.Instance))));
        Assert.IsType<PySubStruct>(bound.Items[0]);
        Assert.Equal(MarshalCodec.Encode(row),MarshalCodec.Encode(bound.Items[1]));
        var publicInfo = await new CorpMgrService(rig.Store,rig.Characters).GetPublicInfo(Call(session,"GetPublicInfo",new PyInt(corp.Id)));
        Assert.Equal(new PyInt(1),Field(publicInfo,"memberCount"));
        var history = await new CorporationService(rig.Store,rig.Characters).GetEmploymentRecord(Call(session,"GetEmploymentRecord",new PyInt(founder)));
        Assert.Equal(2,Assert.IsType<PyList>(Field(history,"lines")).Items.Count);
    }

    [PgFact]
    public async Task Corporate_balances_use_the_selected_division_and_persisted_read_and_take_roles()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var corp = await rig.Store.CreateAsync(founder,new("Orbital Works","ORBIT","","",0.05m),"first");
        var app = await rig.Store.ApplyAsync(member,corp.Id,"Join");
        await rig.Store.RespondAsync(founder,app,6,"Join","");
        await rig.Store.RespondAsync(member,app,2,"Join","");
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await rig.Transactions.TransferAsync(connection,tx,new Eve.Wallet.WalletAccount(founder),new Eve.Wallet.WalletAccount(corp.Id,1006),
                123.45m,10,"Deposit",rig.Now,"deposit","test");
            await tx.CommitAsync();
        }
        var service = new Eve.Services.Wallet.AccountService(rig.Wallets,rig.Store,rig.Transactions);
        var session = new ClientSession();
        session.Set("charid",new PyInt(founder));
        Assert.Equal(new PyReal(123.45),await service.GetCashBalance(Call(session,"GetCashBalance",new PyInt(1),new PyInt(1006))));
        Assert.Equal(new PyReal(0),await service.GetCashBalance(Call(session,"GetCashBalance",new PyInt(1),new PyInt(1000))));
        session.Set("charid",new PyInt(member));
        session.Set("corpid",new PyInt(corp.Id));
        session.Set("corprole",new PyLong(CorporationRoles.All)); // cached masks cannot confer authority
        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.GetCashBalance(Call(session,"GetCashBalance",new PyInt(1),new PyInt(1006))));
        var original = (await rig.Store.MemberAsync(member))!;
        var junior = await rig.Store.UpdateMemberAsync(founder,original with { Masks=new RoleMasks(CorporationRoles.JuniorAccountant) });
        Assert.Equal(new PyReal(123.45),await service.GetCashBalance(new ServiceCall { Session=session,Method="GetCashBalance",Args=[],
            Kwargs=Py.Dict(("isCorpWallet",PyBool.True),("accountKey",new PyInt(1006))) }));
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.WalletAccountAsync(member,1006,true));
        await rig.Store.UpdateMemberAsync(founder,junior with { Masks=new RoleMasks(CorporationRoles.WalletTakeRole(1006)) });
        Assert.Equal(new Eve.Wallet.WalletAccount(corp.Id,1006),await rig.Store.WalletAccountAsync(member,1006,true));
        Assert.Equal(new PyReal(123.45),await service.GetCashBalance(Call(session,"GetCashBalance",new PyInt(1),new PyStr("cash7"))));
        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.GetCashBalance(Call(session,"GetCashBalance",new PyInt(1),new PyInt(1000))));
    }
}
