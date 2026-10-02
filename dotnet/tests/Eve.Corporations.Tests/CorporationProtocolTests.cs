using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Corporations.Tests;

public class CorporationProtocolTests
{
    private sealed class PacketSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static ServiceCall Call(ClientSession session,string method,params PyValue[] args) => new() { Session=session,Method=method,Args=args };
    private static PyValue Field(PyValue value,string key) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries.Single(e => e.Key == new PyStr(key)).Value;

    private static IReadOnlyList<PyValue> Lines(PyValue rowset) =>
        Assert.IsType<PyList>(Field(rowset, "lines")).Items;

    private static PyValue CachedPayload(PyValue value) =>
        Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(value).Args).Items[1]).Decoded!;

    private static Eve.Macho.MachoAddress NotificationAddress(PyValue packet)
    {
        var envelope = Assert.IsType<PyObject>(packet);
        return Eve.Macho.MachoAddress.Decode(Assert.IsType<PyTuple>(envelope.Args).Items[2]);
    }

    private static string? NotificationName(PyValue packet)
    {
        if (packet is not PyObject { Name: PyStr name, Args: PyTuple args } ||
            !name.Value.EndsWith("Notification",StringComparison.Ordinal)) return null;
        return Eve.Macho.MachoAddress.Decode(args.Items[2]).BroadcastId;
    }

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
    public async Task Corporation_migrations_upgrade_invitations_and_medal_awards()
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
                await Execute($"""
                    CREATE TABLE {schema}.medal_awards(
                        medal_id bigint NOT NULL,character_id bigint NOT NULL,issuer_id bigint NOT NULL,
                        reason text NOT NULL,created_ms bigint NOT NULL,status integer NOT NULL DEFAULT 2,
                        PRIMARY KEY(medal_id,character_id))
                    """);
                await Execute($"INSERT INTO {schema}.medal_awards(medal_id,character_id,issuer_id,reason,created_ms) VALUES (7,8,9,'legacy',10)");
            }

            var applied = await Eve.Data.Migrations.ApplyAsync(rig.Source,schema,CorporationStore.MigrationSet());
            Assert.Equal(new[] { "004_invitation_ids","005_medal_award_occurrences" },applied);
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
            await using (var award = read.CreateCommand())
            {
                award.CommandText = $"SELECT award_id FROM {schema}.medal_awards WHERE medal_id=7 AND character_id=8";
                Assert.True((long)(await award.ExecuteScalarAsync())! > 0);
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


    [PgFact]
    public async Task Corporation_core_and_role_rpcs_preserve_public_fields_and_role_contracts()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Role Founder");
        var member = await rig.CharacterAsync("Role Member");
        var corp = await rig.Store.CreateAsync(founder,new("Role Works","ROLE","","",0.05m),"roles");
        var application = await rig.Store.ApplyAsync(member,corp.Id,"");
        await rig.Store.RespondAsync(founder,application,6,"","");
        await rig.Store.RespondAsync(member,application,2,"","");
        var registry = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        var manager = (IService)new CorpMgrService(rig.Store,rig.Characters);
        var founderSession = new ClientSession();
        founderSession.CharacterId = founder;
        founderSession.Set("corporationID",new PyInt(corp.Id));
        var memberSession = new ClientSession();
        memberSession.CharacterId = member;
        memberSession.Set("corpid",new PyInt(corp.Id));
        ValueTask<PyValue> CorpRpc(string method,params PyValue[] args) => registry.CallAsync(Call(founderSession,method,args));
        ValueTask<PyValue> ManagerRpc(string method,params PyValue[] args) => manager.CallAsync(Call(founderSession,method,args));
        async Task<PyPackedRow> MemberRow() => Assert.IsType<PyPackedRow>(MarshalCodec.Decode(MarshalCodec.Encode(
            await ((IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now)).CallAsync(Call(memberSession,"GetMember",new PyInt(member))))));

        var emptyKick = Assert.IsType<PyDict>(await CorpRpc("KickOutMembers",new PyList([])));
        Assert.Empty(Assert.IsType<PyList>(emptyKick.Entries.Single(e => e.Key == new PyStr("kicked")).Value).Items);
        Assert.Empty(Assert.IsType<PyList>(emptyKick.Entries.Single(e => e.Key == new PyStr("notKicked")).Value).Items);
        Assert.Equal(new PyInt(corp.Id),await ManagerRpc("GetCorporationIDForCharacter",new PyLong(long.MaxValue)));
        Assert.IsType<PyNone>(await CorpRpc("UpdateCorporation",new PyStr("Updated description"),new PyStr("https://corp.example"),
            Py.Number(0.12),new PyInt(1),Py.Number(0.03)));
        var publicInfo = await ManagerRpc("GetPublicInfo",new PyInt(corp.Id));
        Assert.Equal(new PyStr("Updated description"),Field(publicInfo,"description"));
        Assert.Equal(new PyStr("https://corp.example"),Field(publicInfo,"url"));
        Assert.Equal(Py.Number(0.12),Field(publicInfo,"taxRate"));
        Assert.Equal(new PyInt(1),Field(publicInfo,"applicationsEnabled"));
        Assert.Equal(new PyInt(1),Field(publicInfo,"isRecruiting"));
        Assert.IsType<PyNone>(await CorpRpc("UpdateLogo",new PyInt(501),new PyInt(502),new PyInt(503),
            new PyInt(11),new PyInt(12),new PyInt(13),new PyInt(14)));
        var afterLogo = await rig.Store.GetAsync(corp.Id);
        Assert.Equal(501,afterLogo!.Settings.GetProperty("shape1").GetInt32());
        Assert.IsType<PyNone>(await CorpRpc("UpdateDivisionNames",new PyStr("Ship Hangar")));
        var afterDivisions = await rig.Store.GetAsync(corp.Id);
        Assert.Equal("Ship Hangar",afterDivisions!.Settings.GetProperty("divisionNames").GetProperty("1").GetString());
        Assert.IsType<PyNone>(await CorpRpc("UpdateCorporationAbilities",new PyStr("false")));
        publicInfo = await ManagerRpc("GetPublicInfo",new PyInt(corp.Id));
        Assert.Equal(new PyInt(0),Field(publicInfo,"applicationsEnabled"));
        Assert.Equal(new PyInt(0),Field(publicInfo,"isRecruiting"));
        Assert.Equal(PyBool.True,await CorpRpc("SetAccountKey",new PyStr("cash2")));
        Assert.Equal(1001,(await rig.Store.MemberAsync(founder))!.AccountKey);
        Assert.Equal(1001,founderSession.GetInt("corpAccountKey"));

        var memberUpdate = new PyValue[] { new PyInt(member),new PyStr("Quartermaster"),new PyInt(7),new PyInt(8),
            new PyLong(CorporationRoles.PersonnelManager) };
        Assert.IsType<PyNone>(await CorpRpc("UpdateMember",memberUpdate));
        var updated = await MemberRow();
        Assert.Equal(new PyInt(7),updated.Values[2]);
        Assert.Equal(new PyInt(8),updated.Values[3]);
        Assert.Equal(new PyWString("Quartermaster"),updated.Values[4]);
        Assert.Equal(new PyInt(CorporationRoles.PersonnelManager),updated.Values[5]);
        var activeLeave = Assert.IsType<PyTuple>(await registry.CallAsync(Call(memberSession,"CanLeaveCurrentCorporation")));
        Assert.Equal(new PyInt(0),activeLeave.Items[0]);
        Assert.Equal(new PyInt(0),await CorpRpc("CanBeKickedOut",new PyInt(member)));

        var removeRoles = new PyValue[] { new PyInt(member),PyNone.Instance,PyNone.Instance,PyNone.Instance,new PyLong(0) };
        Assert.IsType<PyNone>(await CorpRpc("UpdateMember",removeRoles));
        Assert.True((await rig.Store.MemberAsync(member))!.StasisUntilMs > rig.Now);
        Assert.Equal(new PyInt(1),Assert.IsType<PyTuple>(await registry.CallAsync(Call(memberSession,"CanLeaveCurrentCorporation"))).Items[0]);
        Assert.Equal(new PyInt(0),await CorpRpc("CanBeKickedOut",new PyInt(member)));
        Assert.IsType<PyNone>(await CorpRpc("UpdateMember",memberUpdate));

        var title = new PyValue[] { new PyInt(32768),new PyStr("Recruiter"),new PyLong(0),new PyLong(0),new PyLong(0),
            new PyLong(0),new PyLong(0),new PyLong(0),new PyLong(0),new PyLong(0) };
        Assert.IsType<PyNone>(await CorpRpc("UpdateTitle",title));
        Assert.Contains(await rig.Store.TitlesAsync(corp.Id),t => t.Id == 32768 && t.Name == "Recruiter");
        var titleMaskUpdate = Enumerable.Repeat<PyValue>(PyNone.Instance,14).ToArray();
        titleMaskUpdate[0] = new PyInt(member);
        titleMaskUpdate[13] = new PyInt(32768);
        Assert.IsType<PyNone>(await CorpRpc("UpdateMember",titleMaskUpdate));
        Assert.Equal(new PyInt(32768),(await MemberRow()).Values[15]);

        var bulkPlaceholders = new PyList([new PyList([new PyInt(member),new PyInt(0),new PyInt(0),new PyInt(0)])]);
        Assert.IsType<PyNone>(await CorpRpc("UpdateMembers",bulkPlaceholders));
        updated = await MemberRow();
        Assert.Equal(new PyInt(7),updated.Values[2]);
        Assert.Equal(new PyInt(8),updated.Values[3]);
        Assert.Equal(new PyWString("Quartermaster"),updated.Values[4]);

        var batchTitles = new PyList([
            new PyList([new PyInt(2),new PyStr("Operations"),.. Enumerable.Repeat<PyValue>(new PyLong(0),8)]),
            new PyList([new PyInt(4),new PyStr("Security"),.. Enumerable.Repeat<PyValue>(new PyLong(0),8)])
        ]);
        Assert.IsType<PyNone>(await CorpRpc("UpdateTitles",batchTitles));
        Assert.Contains(await rig.Store.TitlesAsync(corp.Id),t => t.Id == 2 && t.Name == "Operations");
        Assert.Contains(await rig.Store.TitlesAsync(corp.Id),t => t.Id == 4 && t.Name == "Security");
        Assert.IsType<PyNone>(await CorpRpc("DeleteTitle",new PyInt(32768)));
        Assert.Equal(0,(await rig.Store.MemberAsync(member))!.TitleMask);
        Assert.True((await rig.Store.MemberAsync(member))!.StasisUntilMs > rig.Now);
        Assert.Contains(await rig.Store.TitlesAsync(corp.Id),t => t.Id == 32768 && t.Name == "");

        var candidates = Assert.IsType<PyList>(await CorpRpc("ResignFromCEO"));
        Assert.Equal(new PyInt(member),Assert.Single(candidates.Items));
        Assert.IsType<PyNone>(await CorpRpc("ResignFromCEO",new PyInt(member)));
        var afterHandover = await rig.Store.MemberAsync(member);
        Assert.Equal(member,(await rig.Store.GetAsync(corp.Id))!.CeoId);
        Assert.NotEqual(0,afterHandover!.Masks.Roles & CorporationRoles.Director);
    }

    [PgFact]
    public async Task Add_corporation_rpc_uses_the_call_id_as_the_founding_receipt()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("RPC Founder");
        var session = new ClientSession { SessionId = 2048 };
        session.CharacterId = founder;
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        var call = new ServiceCall
        {
            Method = "AddCorporation",Session = session,CallId = new PyInt(73),
            Args = [new PyStr("RPC Founded"),new PyStr("RPCFD"),new PyStr("Founded in protocol"),new PyStr("https://rpc.example"),
                Py.Number(0.05),new PyInt(419),PyNone.Instance,PyNone.Instance,new PyInt(1),new PyInt(2),new PyInt(3),
                new PyStr("Verdana"),new PyInt(1),PyBool.True,Py.Number(0.02)]
        };
        static PyValue RowField(PyValue row,string name)
        {
            var header = Assert.IsType<PyTuple>(Field(row,"header")).Items;
            var values = Assert.IsType<PyTuple>(Field(row,"line")).Items;
            var index = header.Select((value,i) => (value,i)).Single(pair => pair.value == new PyStr(name)).i;
            return values[index];
        }

        var first = await service.CallAsync(call);
        var corporationId = Assert.IsType<PyInt>(RowField(first,"corporationID")).Value;
        Assert.Equal(new PyStr("RPC Founded"),RowField(first,"corporationName"));
        Assert.Equal(corporationId,(await rig.Store.MemberAsync(founder))!.CorporationId);
        var manager = (IService)new CorpMgrService(rig.Store,rig.Characters);
        var publicRow = await manager.CallAsync(Call(session,"GetCorporations",new PyInt(corporationId)));
        Assert.Equal(new PyInt(1),RowField(publicRow,"memberCount"));

        var replay = await service.CallAsync(call);
        Assert.Equal(new PyInt(corporationId),RowField(replay,"corporationID"));
        Assert.Single(await rig.Store.MembersAsync(corporationId));
    }

    [PgFact]
    public async Task Corporation_medal_rpcs_preserve_wire_rows_persist_awards_and_follow_reference_authorization()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Medal Founder");
        var member = await rig.CharacterAsync("Medal Member");
        var corp = await rig.Store.CreateAsync(founder, new("Medal Works", "MEDAL", "", "", 0.05m), "medals");
        var application = await rig.Store.ApplyAsync(member, corp.Id, "");
        await rig.Store.RespondAsync(founder, application, 6, "", "");
        await rig.Store.RespondAsync(member, application, 2, "", "");
        Assert.Equal(0L, (await rig.Store.MemberAsync(member))!.Masks.Roles);

        var service = (IService)new CorporationService(rig.Store, rig.Characters);
        var memberSession = new ClientSession();
        memberSession.CharacterId = member;
        memberSession.Set("corporationID", new PyInt(corp.Id));
        var founderSession = new ClientSession();
        founderSession.CharacterId = founder;
        founderSession.Set("corpid", new PyInt(corp.Id));
        ValueTask<PyValue> MemberRpc(string method, params PyValue[] args) => service.CallAsync(Call(memberSession, method, args));
        ValueTask<PyValue> FounderRpc(string method, params PyValue[] args) => service.CallAsync(Call(founderSession, method, args));

        var graphics = new PyList([Py.Tuple(new PyInt(1), new PyWString("emblem.42"), new PyInt(7))]);
        var medalId = Assert.IsType<PyInt>(await MemberRpc("CreateMedal",
            new PyWString("Service"), new PyWString("For service"), graphics)).Value;
        var all = Assert.IsType<PyTuple>(CachedPayload(await MemberRpc("GetAllCorpMedals", new PyInt(corp.Id))));
        var medalRow = Assert.IsType<PyList>(Assert.Single(Lines(all.Items[0])));
        Assert.Equal(new PyInt(medalId), medalRow.Items[0]);
        Assert.Equal(new PyInt(corp.Id), medalRow.Items[1]);
        Assert.Equal(new PyWString("Service"), medalRow.Items[3]);
        Assert.Equal(new PyWString("For service"), medalRow.Items[4]);
        Assert.IsType<PyLong>(medalRow.Items[5]);
        Assert.Equal(new PyInt(0), medalRow.Items[6]);
        var graphicRow = Assert.IsType<PyList>(Assert.Single(Lines(all.Items[1])));
        Assert.Equal(new PyWString("emblem.42"), graphicRow.Items[2]);
        var emptyDetails = await MemberRpc("GetMedalDetails", new PyInt(medalId));
        var emptyDetail = Assert.Single(Assert.IsType<PyList>(Field(emptyDetails, "info")).Items);
        Assert.Equal(new PyInt(0), Field(emptyDetail, "numberOfRecipients"));

        var status = new PyDict([new(new PyInt(medalId), new PyInt(4))]);
        Assert.IsType<PyNone>(await MemberRpc("SetMedalStatus", status));
        Assert.IsType<PyNone>(await MemberRpc("GiveMedalToCharacters",
            new PyInt(medalId), new PyList([new PyInt(founder)]), new PyWString("Service award")));
        Assert.IsType<PyNone>(await MemberRpc("GiveMedalToCharacters",
            new PyInt(medalId), new PyList([new PyInt(founder)]), new PyWString("Repeat award")));

        var recipientRows = Lines(await MemberRpc("GetRecipientsOfMedal", new PyInt(medalId)));
        Assert.Equal(2, recipientRows.Count);
        var firstRecipient = Assert.IsType<PyList>(recipientRows[0]);
        Assert.Equal(new PyInt(founder), firstRecipient.Items[1]);
        Assert.Equal(new PyInt(member), firstRecipient.Items[2]);
        Assert.Equal(new PyWString("Service award"), firstRecipient.Items[3]);
        Assert.IsType<PyLong>(firstRecipient.Items[4]);
        var secondRecipient = Assert.IsType<PyList>(recipientRows[1]);
        Assert.Equal(new PyWString("Repeat award"), secondRecipient.Items[3]);

        var received = Assert.IsType<PyTuple>(await FounderRpc("GetMedalsReceived"));
        var receivedRows = Lines(received.Items[0]);
        Assert.Equal(2, receivedRows.Count);
        Assert.Equal(new PyWString("Service award"), Assert.IsType<PyList>(receivedRows[0]).Items[4]);
        Assert.Equal(new PyWString("Repeat award"), Assert.IsType<PyList>(receivedRows[1]).Items[4]);
        Assert.Single(Lines(received.Items[1]));

        var details = await MemberRpc("GetMedalDetails", new PyInt(medalId));
        var detail = Assert.Single(Assert.IsType<PyList>(Field(details, "info")).Items);
        Assert.Equal(new PyInt(medalId), Field(detail, "medalID"));
        Assert.Equal(new PyInt(corp.Id), Field(detail, "ownerID"));
        Assert.Equal(new PyInt(member), Field(detail, "creatorID"));
        Assert.Equal(new PyInt(2), Field(detail, "numberOfRecipients"));
        Assert.Single(Lines(Field(details, "graphics")));
        Assert.Empty(Assert.IsType<PyList>(Field(await MemberRpc("GetMedalDetails", new PyInt(long.MaxValue)), "info")).Items);
    }

    [PgFact]
    public async Task Corp_info_returns_reference_activity_rows_for_existing_corporations()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Corp Info Founder");
        var corp = await rig.Store.CreateAsync(founder, new("Info Works", "INFO", "", "", 0.05m), "corp-info");
        var service = (IService)new CorporationService(rig.Store, rig.Characters);
        var session = new ClientSession();
        session.CharacterId = founder;

        var activity = await service.CallAsync(Call(session, "GetCorpInfo", new PyInt(corp.Id)));
        Assert.Empty(Assert.IsType<PyList>(activity).Items);
        Assert.IsType<PyNone>(await service.CallAsync(Call(session, "GetCorpInfo", new PyInt(long.MaxValue))));
    }

    [PgFact]
    public async Task Corporation_membership_role_and_medal_notifications_run_after_commit()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Notice Founder");
        var member = await rig.CharacterAsync("Notice Member");
        var corp = await rig.Store.CreateAsync(founder,new("Notice Works","NOTICE","","",0.05m),"notice-corp");

        var sessions = new CharacterSessions();
        var founderSink = new PacketSink();
        var founderSession = new ClientSession(founderSink);
        founderSession.CharacterId = founder;
        founderSession.Set("corpid",new PyInt(corp.Id));
        var memberSink = new PacketSink();
        var memberSession = new ClientSession(memberSink);
        memberSession.CharacterId = member;
        memberSession.Set("corpid",new PyInt(1000044));
        sessions.Enter(founder,founderSession);
        sessions.Enter(member,memberSession);
        var registry = new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now,sessions);
        var medals = new CorporationService(rig.Store,rig.Characters,sessions);

        var application = await rig.Store.ApplyAsync(member,corp.Id,"Join");
        await registry.UpdateApplicationOffer(Call(founderSession,"UpdateApplicationOffer",new PyInt(application),new PyInt(member),
            new PyInt(corp.Id),new PyWString("Join"),new PyInt(6),new PyWString("Welcome")));
        founderSession.RunAfterResponse();
        var founderApplication = Assert.Single(founderSink.Packets,p => NotificationName(p) == "OnCorporationApplicationChanged");
        var memberApplication = Assert.Single(memberSink.Packets,p => NotificationName(p) == "OnCorporationApplicationChanged");
        Assert.Equal("clientID",NotificationAddress(founderApplication).IdType);
        Assert.Equal("clientID",NotificationAddress(memberApplication).IdType);
        founderSink.Packets.Clear();
        memberSink.Packets.Clear();
        Assert.Equal(1000044,(await rig.Characters.GetAsync(member))!.CorporationId);
        await registry.UpdateApplicationOffer(Call(memberSession,"UpdateApplicationOffer",new PyInt(application),new PyInt(member),
            new PyInt(corp.Id),new PyWString("Join"),new PyInt(2),new PyWString("Accepted")));
        Assert.Equal(corp.Id,(await rig.Characters.GetAsync(member))!.CorporationId);
        Assert.DoesNotContain(founderSink.Packets,p => NotificationName(p) == "OnCorporationApplicationChanged");
        Assert.DoesNotContain(memberSink.Packets,p => NotificationName(p) == "OnCorporationApplicationChanged");
        Assert.DoesNotContain(founderSink.Packets,p => NotificationName(p) == "OnCorporationMemberChanged");
        Assert.DoesNotContain(memberSink.Packets,p => NotificationName(p) == "OnCorporationMemberChanged");
        memberSession.RunAfterResponse();
        var founderApplicationAccepted = Assert.Single(founderSink.Packets,p => NotificationName(p) == "OnCorporationApplicationChanged");
        var memberApplicationAccepted = Assert.Single(memberSink.Packets,p => NotificationName(p) == "OnCorporationApplicationChanged");
        Assert.Equal("clientID",NotificationAddress(founderApplicationAccepted).IdType);
        Assert.Equal("clientID",NotificationAddress(memberApplicationAccepted).IdType);
        var founderJoin = Assert.Single(founderSink.Packets,p => NotificationName(p) == "OnCorporationMemberChanged");
        var memberJoin = Assert.Single(memberSink.Packets,p => NotificationName(p) == "OnCorporationMemberChanged");
        Assert.Equal("corpid",NotificationAddress(founderJoin).IdType);
        Assert.Equal("corpid",NotificationAddress(memberJoin).IdType);

        founderSink.Packets.Clear();
        memberSink.Packets.Clear();
        await registry.UpdateMember(Call(founderSession,"UpdateMember",new PyInt(member),PyNone.Instance,PyNone.Instance,PyNone.Instance,
            new PyLong(CorporationRoles.JuniorAccountant)));
        Assert.Equal(CorporationRoles.JuniorAccountant,(await rig.Store.MemberAsync(member))!.Masks.Roles);
        Assert.DoesNotContain(founderSink.Packets,p => NotificationName(p) == "OnCorporationMemberChanged");
        Assert.DoesNotContain(memberSink.Packets,p => NotificationName(p) == "OnCorporationMemberChanged");
        founderSession.RunAfterResponse();
        var founderRole = Assert.Single(founderSink.Packets,p => NotificationName(p) == "OnCorporationMemberChanged");
        var memberRole = Assert.Single(memberSink.Packets,p => NotificationName(p) == "OnCorporationMemberChanged");
        Assert.Equal("corpid",NotificationAddress(founderRole).IdType);
        Assert.Equal("corpid",NotificationAddress(memberRole).IdType);

        founderSink.Packets.Clear();
        memberSink.Packets.Clear();
        await registry.UpdateTitle(Call(founderSession,"UpdateTitle",new PyInt(1),new PyWString("Quartermaster"),
            new PyLong(0),new PyLong(0),new PyLong(0),new PyLong(0),new PyLong(0),new PyLong(0),new PyLong(0),new PyLong(0)));
        Assert.DoesNotContain(founderSink.Packets,p => NotificationName(p) == "OnTitleChanged");
        Assert.DoesNotContain(memberSink.Packets,p => NotificationName(p) == "OnTitleChanged");
        founderSession.RunAfterResponse();
        var founderTitle = Assert.Single(founderSink.Packets,p => NotificationName(p) == "OnTitleChanged");
        var memberTitle = Assert.Single(memberSink.Packets,p => NotificationName(p) == "OnTitleChanged");
        Assert.Equal("corpid",NotificationAddress(founderTitle).IdType);
        Assert.Equal("corpid",NotificationAddress(memberTitle).IdType);

        founderSink.Packets.Clear();
        memberSink.Packets.Clear();
        var medalId = Assert.IsType<PyInt>(await medals.CreateMedal(Call(founderSession,"CreateMedal",
            new PyWString("Notice"),new PyWString("Award"),new PyList([])))).Value;
        await medals.GiveMedalToCharacters(Call(founderSession,"GiveMedalToCharacters",new PyInt(medalId),
            new PyList([new PyInt(member)]),new PyWString("Awarded")));
        Assert.DoesNotContain(memberSink.Packets,p => NotificationName(p) == "OnMedalsReceived");
        founderSession.RunAfterResponse();
        Assert.DoesNotContain(founderSink.Packets,p => NotificationName(p) == "OnMedalsReceived");
        var medalNotice = Assert.Single(memberSink.Packets,p => NotificationName(p) == "OnMedalsReceived");
        Assert.Equal("clientID",NotificationAddress(medalNotice).IdType);
    }

    [Fact]
    public void Corporation_service_exposes_reference_methods()
    {
        var service = (IService)new CorporationService(null!, null!);
        foreach (var method in new[]
        {
            "CreateMedal", "SetMedalStatus", "GetAllCorpMedals", "GetRecipientsOfMedal",
            "GiveMedalToCharacters", "GetMedalDetails", "GetMedalsReceived", "GetCorpInfo"
        }) Assert.True(service.HasMethod(method), method);
    }
}
