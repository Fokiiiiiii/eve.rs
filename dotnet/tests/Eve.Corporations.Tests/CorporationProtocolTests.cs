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

    [PgFact]
    public async Task Corp_registry_membership_rpcs_preserve_application_invitation_and_authority_contracts()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var applicant = await rig.CharacterAsync("Applicant One");
        var invitee = await rig.CharacterAsync("Invitee One");
        var secondInvitee = await rig.CharacterAsync("Invitee Two");
        var applicantDecliner = await rig.CharacterAsync("Applicant Decliner");
        var corporationDecliner = await rig.CharacterAsync("Corporation Decliner");
        var corp = await rig.Store.CreateAsync(founder,new("Orbital Works","ORBIT","","",0.05m),"first");
        var registry = new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        var service = (IService)registry;
        ClientSession Session(long character)
        {
            var session = new ClientSession();
            session.Set("charid",new PyInt(character));
            return session;
        }
        ValueTask<PyValue> Rpc(ClientSession session,string method,params PyValue[] args) =>
            service.CallAsync(Call(session,method,args));
        PyPackedRow GroupedRow(PyValue result,long owner,long application)
        {
            var grouped = Assert.IsType<PyDict>(result);
            var rows = Assert.IsType<PyList>(grouped.Entries.Single(e => e.Key == new PyInt(owner)).Value);
            return Assert.IsType<PyPackedRow>(rows.Items.Select(row => Assert.IsType<PyPackedRow>(
                MarshalCodec.Decode(MarshalCodec.Encode(row)))).Single(row => row.Values[0] == new PyInt(application)));
        }

        var founderSession = Session(founder);
        var applicantSession = Session(applicant);
        var applicationId = Assert.IsType<PyInt>(await Rpc(applicantSession,"InsertApplication",
            new PyInt(corp.Id),new PyStr("Please consider my application."))).Value;

        var ownApplications = await Rpc(applicantSession,"GetMyApplications");
        var application = GroupedRow(ownApplications,corp.Id,applicationId);
        Assert.Equal(new[] { "applicationID","corporationID","characterID","applicationText","status","applicationDateTime","deleted","responseText" },
            application.Descriptor.Columns.Select(column => column.Name));
        Assert.Equal(new long[] { 3,3,3,130,3,64,11,130 },application.Descriptor.Columns.Select(column => column.DbType));
        Assert.Equal(new PyInt(applicationId),application.Values[0]);
        Assert.Equal(new PyInt(corp.Id),application.Values[1]);
        Assert.Equal(new PyInt(applicant),application.Values[2]);
        Assert.Equal(new PyWString("Please consider my application."),application.Values[3]);
        Assert.IsType<PyLong>(application.Values[5]);
        Assert.Equal(new PyInt(0),application.Values[4]);
        Assert.Equal(PyBool.False,application.Values[6]);
        Assert.Equal(PyNone.Instance,application.Values[7]);

        var managedApplications = Assert.IsType<PyDict>(await Rpc(founderSession,"GetApplications"));
        Assert.Equal(applicant,Assert.IsType<PyInt>(managedApplications.Entries.Single().Key).Value);
        Assert.Equal(new PyInt(0),GroupedRow(managedApplications,applicant,applicationId).Values[4]);

        Assert.IsType<PyNone>(await Rpc(applicantSession,"UpdateApplicationOffer",
            new PyInt(applicationId),new PyInt(applicant),new PyInt(corp.Id+1),new PyStr("Wrong corporation"),new PyInt(6),new PyStr("")));
        await Assert.ThrowsAsync<CorporationError>(async () => await Rpc(founderSession,"UpdateApplicationOffer",
            new PyInt(applicationId),new PyInt(invitee),new PyInt(corp.Id),new PyStr("Wrong character"),new PyInt(6),new PyStr("")));
        await Assert.ThrowsAsync<CorporationError>(async () => await Rpc(Session(applicant),"UpdateApplicationOffer",
            new PyInt(applicationId),new PyInt(applicant),new PyInt(corp.Id),new PyStr("Forged manager"),new PyInt(6),new PyStr("")));

        Assert.IsType<PyNone>(await Rpc(founderSession,"UpdateApplicationOffer",
            new PyInt(applicationId),new PyInt(applicant),new PyInt(corp.Id),new PyStr("Let's discuss"),new PyInt(5),new PyStr("Please revise.")));
        Assert.Equal(new PyInt(5),GroupedRow(await Rpc(applicantSession,"GetMyApplications"),corp.Id,applicationId).Values[4]);
        Assert.IsType<PyNone>(await Rpc(applicantSession,"UpdateApplicationOffer",
            new PyInt(applicationId),new PyInt(applicant),new PyInt(corp.Id),new PyStr("Revised application"),new PyInt(1),new PyStr("")));
        Assert.IsType<PyNone>(await Rpc(founderSession,"UpdateApplicationOffer",
            new PyInt(applicationId),new PyInt(applicant),new PyInt(corp.Id),new PyStr("Offer"),new PyInt(6),new PyStr("Welcome.")));
        var openOffer = Assert.IsType<PyList>(await Rpc(applicantSession,"GetMyOpenInvitations"));
        Assert.Equal(new PyInt(6),Field(openOffer.Items.Single(),"status"));
        Assert.IsType<PyNone>(await Rpc(applicantSession,"UpdateApplicationOffer",
            new PyInt(applicationId),new PyInt(applicant),new PyInt(corp.Id),new PyStr("Offer accepted"),new PyInt(2),new PyStr("")));
        Assert.Equal(corp.Id,(await rig.Characters.GetAsync(applicant))!.CorporationId);
        Assert.Equal(corp.Id,applicantSession.GetInt("corpid"));
        var ownHistory = Assert.IsType<PyList>(await Rpc(applicantSession,"GetMyOldApplications"));
        Assert.Equal(new PyInt(2),Assert.IsType<PyPackedRow>(MarshalCodec.Decode(MarshalCodec.Encode(ownHistory.Items.Single()))).Values[4]);

        var invitationReceipt = Assert.IsType<PyInt>(await Rpc(founderSession,"InsertInvitation",new PyInt(invitee)));
        Assert.True(invitationReceipt.Value > 0);
        var inviteeSession = Session(invitee);
        var inviteeApplications = Assert.IsType<PyDict>(await Rpc(inviteeSession,"GetMyApplications"));
        var invitationApplications = Assert.IsType<PyList>(inviteeApplications.Entries.Single(e => e.Key == new PyInt(corp.Id)).Value);
        var invitationApplication = Assert.IsType<PyPackedRow>(MarshalCodec.Decode(MarshalCodec.Encode(invitationApplications.Items.Single())));
        var invitationApplicationId = Assert.IsType<PyInt>(invitationApplication.Values[0]).Value;
        Assert.NotEqual(invitationReceipt.Value,invitationApplicationId); // InsertInvitation returns the distinct invitationID.
        var invitations = Assert.IsType<PyList>(await Rpc(inviteeSession,"GetMyOpenInvitations"));
        Assert.Equal(new PyInt(invitationApplicationId),Field(invitations.Items.Single(),"invitationID"));
        Assert.Equal(new PyInt(corp.Id),Field(invitations.Items.Single(),"corporationID"));
        Assert.Equal(new PyInt(8),Field(invitations.Items.Single(),"status"));
        Assert.IsType<PyLong>(Field(invitations.Items.Single(),"invitationDateTime"));
        Assert.IsType<PyNone>(await Rpc(inviteeSession,"UpdateApplicationOffer",
            new PyInt(invitationApplicationId),new PyInt(invitee),new PyInt(corp.Id),new PyStr(""),new PyInt(2),new PyStr("")));
        Assert.Equal(corp.Id,(await rig.Characters.GetAsync(invitee))!.CorporationId);
        Assert.Equal(corp.Id,inviteeSession.GetInt("corpid"));
        inviteeSession.Set("corpid",new PyInt(corp.Id));
        await Assert.ThrowsAsync<CorporationError>(async () => await Rpc(inviteeSession,"GetApplications"));
        await Assert.ThrowsAsync<CorporationError>(async () => await Rpc(inviteeSession,"InsertInvitation",new PyInt(applicantDecliner)));

        var secondApplicationId = Assert.IsType<PyInt>(await Rpc(Session(secondInvitee),"InsertApplication",
            new PyInt(corp.Id),new PyStr("Application before invite."))).Value;
        var nextInvitation = await rig.ReopenStore().CreateInvitationAsync(secondInvitee,corp.Id,founder);
        Assert.Equal(invitationReceipt.Value+1,nextInvitation.InvitationId);
        Assert.NotEqual(nextInvitation.InvitationId,nextInvitation.ApplicationId);
        var secondInviteeSession = Session(secondInvitee);
        var secondApps = Assert.IsType<PyDict>(await Rpc(secondInviteeSession,"GetMyApplications"));
        var secondRows = Assert.IsType<PyList>(secondApps.Entries.Single(e => e.Key == new PyInt(corp.Id)).Value);
        Assert.Equal(2,secondRows.Items.Count);
        Assert.Contains(secondRows.Items, row => Assert.IsType<PyInt>(Assert.IsType<PyPackedRow>(MarshalCodec.Decode(MarshalCodec.Encode(row))).Values[0]).Value == secondApplicationId);
        Assert.Contains(secondRows.Items, row => Assert.IsType<PyInt>(Assert.IsType<PyPackedRow>(MarshalCodec.Decode(MarshalCodec.Encode(row))).Values[0]).Value == nextInvitation.ApplicationId);
        var nextOpen = Assert.IsType<PyList>(await Rpc(secondInviteeSession,"GetMyOpenInvitations"));
        var nextApplicationId = Assert.IsType<PyInt>(Field(nextOpen.Items.Single(),"invitationID")).Value;
        Assert.Equal(nextInvitation.ApplicationId,nextApplicationId);
        Assert.Equal(new PyInt(8),Field(nextOpen.Items.Single(),"status"));
        Assert.IsType<PyNone>(await Rpc(secondInviteeSession,"UpdateApplicationOffer",
            new PyInt(nextApplicationId),new PyInt(secondInvitee),new PyInt(corp.Id),new PyStr(""),new PyInt(2),new PyStr("")));
        Assert.Equal(corp.Id,(await rig.Characters.GetAsync(secondInvitee))!.CorporationId);
        Assert.Equal(corp.Id,secondInviteeSession.GetInt("corpid"));

        var declineApplicationId = Assert.IsType<PyInt>(await Rpc(Session(applicantDecliner),"InsertApplication",
            new PyInt(corp.Id),new PyStr("I may withdraw."))).Value;
        Assert.IsType<PyNone>(await Rpc(Session(applicantDecliner),"UpdateApplicationOffer",
            new PyInt(declineApplicationId),new PyInt(applicantDecliner),new PyInt(corp.Id),new PyStr(""),new PyInt(3),new PyStr("")));
        var rejectedApplicationId = Assert.IsType<PyInt>(await Rpc(Session(corporationDecliner),"InsertApplication",
            new PyInt(corp.Id),new PyStr("Please review."))).Value;
        Assert.IsType<PyNone>(await Rpc(founderSession,"UpdateApplicationOffer",
            new PyInt(rejectedApplicationId),new PyInt(corporationDecliner),new PyInt(corp.Id),new PyStr(""),new PyInt(4),new PyStr("No vacancy.")));
        var corpHistory = Assert.IsType<PyList>(await Rpc(founderSession,"GetOldApplications"));
        var oldStatuses = corpHistory.Items.Select(item => Assert.IsType<PyInt>(Assert.IsType<PyPackedRow>(
            MarshalCodec.Decode(MarshalCodec.Encode(item))).Values[4]).Value).Order().ToArray();
        Assert.Equal(new long[] { 2,2,2,3,4,7 },oldStatuses);
    }

    [PgFact]
    public async Task Corporation_invitation_migration_backfills_ids_and_advances_the_durable_sequence()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = $"corp_invite_upgrade_{Guid.NewGuid():N}";
        try
        {
            await using (var connection = await rig.Source.OpenConnectionAsync())
            {
                async Task Execute(string sql)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }
                await Execute($"CREATE SCHEMA {schema}");
                await Execute($"CREATE TABLE {schema}.migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
                await Execute($"INSERT INTO {schema}.migrations(name) VALUES ('001_corporations'),('002_npc_corporations'),('003_alliance_governance')");
                await Execute($"""
                    CREATE TABLE {schema}.applications(
                        id bigint PRIMARY KEY,corporation_id bigint NOT NULL,character_id bigint NOT NULL,
                        text text NOT NULL,status integer NOT NULL,created_ms bigint NOT NULL,updated_ms bigint NOT NULL,
                        response text NOT NULL DEFAULT '',archived boolean NOT NULL DEFAULT false)
                    """);
                await Execute($"CREATE UNIQUE INDEX applications_active ON {schema}.applications(corporation_id,character_id) WHERE NOT archived");
                await Execute($"INSERT INTO {schema}.applications(id,corporation_id,character_id,text,status,created_ms,updated_ms,response,archived) VALUES (42,1,2,'',8,1,1,'',false),(99,1,3,'',0,1,1,'',false)");
            }

            var applied = await Eve.Data.Migrations.ApplyAsync(rig.Source,schema,CorporationStore.MigrationSet());
            Assert.Equal(new[] { "004_invitation_ids" },applied);
            await using var read = await rig.Source.OpenConnectionAsync();
            await using (var backfill = read.CreateCommand())
            {
                backfill.CommandText = $"SELECT invitation_id FROM {schema}.applications WHERE id=42";
                Assert.Equal(42L,(long)(await backfill.ExecuteScalarAsync())!);
            }
            await using (var next = read.CreateCommand())
            {
                next.CommandText = $"SELECT nextval('{schema}.invitation_ids')";
                Assert.Equal(100L,(long)(await next.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
