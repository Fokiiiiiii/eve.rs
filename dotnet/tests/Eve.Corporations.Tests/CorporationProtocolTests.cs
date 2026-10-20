using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Corporations.Tests;

public sealed class WarsInfoMgrServiceTests
{
    private const string WarRowsetName = "carbon.common.script.sys.crowset.CRowset";
    private const string CachedMethodCallName = "carbon.common.script.net.objectCaching.CachedMethodCallResult";

    [Eve.Corporations.Tests.PgFact]
    public async Task Owner_lookups_and_public_info_preserve_the_reference_wire_shapes()
    {
        await using var rig = await WarRig.CreateAsync();
        await rig.SetAsync("war", 1001, 1002, attrs: new
        {
            mutual = false,
            openForAllies = true,
            warHQID = 4001L,
            timeDeclared = 1_700_000_000_000L,
            timeStarted = 1_700_000_086_400L,
            reasonStarted = 7,
            reward = 123.5
        });
        var war = Assert.Single(await rig.Relations.ListAsync("war"));
        await rig.SetAsync("war.ally", 1003, war.Id, attrs: new { side = "defender" });

        var service = new Eve.Services.Corporations.WarsInfoMgrService(rig.Relations, () => rig.Now);
        var byOwner = AssertRowset(UnwrapCached(await service.GetWarsByOwnerID(Call("GetWarsByOwnerID", new Eve.Marshal.PyLong(1003)))));
        var row = Assert.IsType<Eve.Marshal.PyPackedRow>(Assert.Single(byOwner.List));
        Assert.Equal(17, row.Values.Count);
        Assert.Equal(war.Id, Number(row.Values[0]));
        Assert.Equal(1001, Number(row.Values[1]));
        Assert.Equal(1002, Number(row.Values[2]));
        Assert.Equal(Eve.Services.Py.FileTimeFromUnixMs(1_700_000_000_000L), Number(row.Values[3]));
        Assert.Equal(Eve.Services.Py.FileTimeFromUnixMs(1_700_000_086_400L), Number(row.Values[7]));
        Assert.Equal(1, Number(row.Values[15]));

        var byOwners = Assert.IsType<Eve.Marshal.PyDict>(await service.GetWarsByOwners(Call(
            "GetWarsByOwners",
            new Eve.Marshal.PyList([new Eve.Marshal.PyLong(1001), new Eve.Marshal.PyLong(1003), new Eve.Marshal.PyLong(9999)]))));
        Assert.Collection(byOwners.Entries, _ => { }, _ => { }, _ => { });
        Assert.Contains(byOwners.Entries, entry => Number(entry.Key) == 1003 && Assert.IsType<Eve.Marshal.PyDict>(entry.Value).Entries.Count == 1);

        var publicInfo = await service.GetPublicWarInfo(Call("GetPublicWarInfo", new Eve.Marshal.PyLong(war.Id)));
        var fields = KeyValFields(publicInfo);
        Assert.Equal(war.Id, Number(fields["warID"]));
        Assert.Equal(4001, Number(fields["warHQ"]));
        Assert.Single(Assert.IsType<Eve.Marshal.PyDict>(fields["allies"]).Entries);
        Assert.IsType<Eve.Marshal.PyNone>(await service.GetPublicWarInfo(Call("GetPublicWarInfo", new Eve.Marshal.PyLong(long.MaxValue))));

        var structureWars = Assert.IsType<Eve.Marshal.PyList>(await service.GetWarsForStructure(Call("GetWarsForStructure", new Eve.Marshal.PyLong(4001))));
        Assert.Equal(war.Id, Number(KeyValFields(Assert.Single(structureWars.Items))["warID"]));
    }

    [Eve.Corporations.Tests.PgFact]
    public async Task Assistance_lookup_is_defender_only_and_uses_session_owner_defaults()
    {
        await using var rig = await WarRig.CreateAsync();
        await rig.SetAsync("war", 1101, 1201, attrs: new { openForAllies = true });
        await rig.SetAsync("war", 1102, 1201, attrs: new { openForAllies = false });
        await rig.SetAsync("war", 1201, 1301, attrs: new { openForAllies = true });
        var service = new Eve.Services.Corporations.WarsInfoMgrService(rig.Relations, () => rig.Now);

        var session = new Eve.Services.ClientSession();
        session.Set("allianceID", new Eve.Marshal.PyLong(1201));
        var bySession = Assert.IsType<Eve.Marshal.PyList>(await service.GetWarsRequiringAssistance(Call("GetWarsRequiringAssistance", session: session)));
        Assert.Single(bySession.Items);
        Assert.Equal(1101, Number(KeyValFields(bySession.Items[0])["declaredByID"]));

        var byDefender = Assert.IsType<Eve.Marshal.PyList>(await service.GetWarsRequiringAssistance(Call("GetWarsRequiringAssistance", new Eve.Marshal.PyLong(1201))));
        Assert.Single(byDefender.Items);
        var byAttacker = Assert.IsType<Eve.Marshal.PyList>(await service.GetWarsRequiringAssistance(Call("GetWarsRequiringAssistance", new Eve.Marshal.PyLong(1101))));
        Assert.Empty(byAttacker.Items);
    }

    [Eve.Corporations.Tests.PgFact]
    public async Task Top50_uses_descending_exclusive_cursor_and_structure_rows_sort_by_declaration_time()
    {
        await using var rig = await WarRig.CreateAsync();
        for (var index = 0; index < 51; index++)
        {
            await rig.SetAsync("war", 20000 + index, 30000 + index);
        }
        await rig.SetAsync("war", 41001, 41002, attrs: new { warHQID = 7000L, timeDeclared = 2_000L });
        await rig.SetAsync("war", 42001, 42002, attrs: new { warHQID = 7000L, timeDeclared = 1_000L });

        var service = new Eve.Services.Corporations.WarsInfoMgrService(rig.Relations, () => rig.Now);
        var top50 = AssertRowset(UnwrapCached(await service.GetTop50(Call("GetTop50"))));
        Assert.Equal(50, top50.List.Count);
        var ids = top50.List.Cast<Eve.Marshal.PyPackedRow>().Select(row => Number(row.Values[0])).ToArray();
        Assert.Equal(ids.OrderByDescending(id => id), ids);
        Assert.Equal(17, ((Eve.Marshal.PyPackedRow)top50.List[0]).Values.Count);
        var cursor = ids[0];
        var nextPage = AssertRowset(UnwrapCached(await service.GetTop50(Call("GetTop50", new Eve.Marshal.PyLong(cursor)))));
        Assert.True(Number(((Eve.Marshal.PyPackedRow)nextPage.List[0]).Values[0]) < cursor);

        var structureWars = Assert.IsType<Eve.Marshal.PyList>(await service.GetWarsForStructure(Call("GetWarsForStructure", new Eve.Marshal.PyLong(7000))));
        Assert.Equal(2, structureWars.Items.Count);
        var declaredTimes = structureWars.Items.Select(item => Number(KeyValFields(item)["timeDeclared"])).ToArray();
        Assert.True(declaredTimes[0] > declaredTimes[1]);
    }

    [Eve.Corporations.Tests.PgFact]
    public async Task War_declaration_storage_is_visible_to_wars_info_with_its_bill_and_spoolup()
    {
        await using var corporationRig = await Rig.CreateAsync();
        await using var relationRig = await WarRig.CreateAsync();
        var founder = await corporationRig.CharacterAsync("War Persistence Founder");
        var defender = await corporationRig.CharacterAsync("War Persistence Defender");
        var corporation = await corporationRig.Store.CreateAsync(founder,new("War Persistence Works","WPR","","",0.05m),"corp-war-persistence");
        var target = await corporationRig.Store.CreateAsync(defender,new("War Persistence Target","WPT","","",0.05m),"corp-war-persistence-target");
        var declaredAt = Assert.IsType<PyLong>(CorporationWire.Time(corporationRig.Now)).Value;
        var startedAt = declaredAt+864_000_000_000L;
        var created = await corporationRig.Store.CreateWarDeclarationAsync(
            corporation.Id,founder,target.Id,null,declaredAt,startedAt,declaredAt+6_048_000_000_000L,
            4,100_000_000m,1000125,"war-persistence",
            async (connection,transaction,billId,token) =>
            {
                if (await relationRig.Relations.GetAsync("war",corporation.Id,target.Id,connection,transaction,token,forUpdate:true) is not null)
                    throw new Eve.Corporations.CorporationError("WAR_ALREADY_DECLARED");
                var attrs = new
                {
                    declaredByCharacterID=founder,
                    timeDeclared=declaredAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    timeStarted=startedAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    timeFinished=(string?)null,
                    retracted=(string?)null,
                    retractedBy=(long?)null,
                    billID=billId,
                    mutual=0,
                    fightingLegalNotified=0,
                    openForAllies=0,
                    createdFromWarID=(long?)null,
                    warHQID=(long?)null,
                    warHQ=(long?)null,
                    reasonEnded=0,
                    reasonStarted=0,
                    reward=0
                };
                var change = await relationRig.Relations.SetAsync(connection,transaction,"war",corporation.Id,target.Id,
                    1.0,"declared",attrs,cancellationToken:token);
                return change?.RelationId ?? 0;
            });

        var persisted = await relationRig.Relations.GetAsync("war",corporation.Id,target.Id);
        Assert.Equal(created.WarId,persisted!.Id);
        using var attributes = System.Text.Json.JsonDocument.Parse(persisted.AttrsJson);
        Assert.Equal(created.BillId,attributes.RootElement.GetProperty("billID").GetInt64());
        Assert.Equal(startedAt.ToString(System.Globalization.CultureInfo.InvariantCulture),attributes.RootElement.GetProperty("timeStarted").GetString());
        var wars = new Eve.Services.Corporations.WarsInfoMgrService(relationRig.Relations,() => relationRig.Now);
        var result = AssertRowset(UnwrapCached(await wars.GetWarsByOwnerID(Call("GetWarsByOwnerID",new Eve.Marshal.PyInt(corporation.Id)))));
        var war = Assert.IsType<Eve.Marshal.PyPackedRow>(Assert.Single(result.List));
        Assert.Equal(created.WarId,Number(war.Values[0]));
    }

    private static Eve.Services.ServiceCall Call(string method, params Eve.Marshal.PyValue[] args) =>
        Call(method, args, new Eve.Services.ClientSession());

    private static Eve.Services.ServiceCall Call(string method, Eve.Services.ClientSession session, params Eve.Marshal.PyValue[] args) =>
        Call(method, args, session);

    private static Eve.Services.ServiceCall Call(string method, Eve.Marshal.PyValue[] args, Eve.Services.ClientSession session) =>
        new() { Method = method, Args = args, Session = session };

    private static Eve.Marshal.PyValue UnwrapCached(Eve.Marshal.PyValue result)
    {
        var wrapper = Assert.IsType<Eve.Marshal.PyObject>(result);
        Assert.Equal(CachedMethodCallName, Assert.IsType<Eve.Marshal.PyStr>(wrapper.Name).Value);
        var args = Assert.IsType<Eve.Marshal.PyTuple>(wrapper.Args);
        return Assert.IsType<Eve.Marshal.PySubStream>(args.Items[1]).Decoded!;
    }

    private static Eve.Marshal.PyObjectEx AssertRowset(Eve.Marshal.PyValue value)
    {
        var rowset = Assert.IsType<Eve.Marshal.PyObjectEx>(value);
        Assert.Equal(Eve.Marshal.ObjectExKind.Ex2, rowset.Kind);
        var header = Assert.IsType<Eve.Marshal.PyTuple>(rowset.Header);
        var classHeader = Assert.IsType<Eve.Marshal.PyTuple>(header.Items[0]);
        Assert.Equal(WarRowsetName, Assert.IsType<Eve.Marshal.PyToken>(classHeader.Items[0]).Value);
        return rowset;
    }

    private static IReadOnlyDictionary<string, Eve.Marshal.PyValue> KeyValFields(Eve.Marshal.PyValue value)
    {
        var keyVal = Assert.IsType<Eve.Marshal.PyObject>(value);
        Assert.Equal("util.KeyVal", Assert.IsType<Eve.Marshal.PyStr>(keyVal.Name).Value);
        var dict = Assert.IsType<Eve.Marshal.PyDict>(keyVal.Args);
        return dict.Entries.ToDictionary(entry => Assert.IsType<Eve.Marshal.PyStr>(entry.Key).Value, entry => entry.Value);
    }

    private static long Number(Eve.Marshal.PyValue value) => value switch
    {
        Eve.Marshal.PyInt integer => integer.Value,
        Eve.Marshal.PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected a marshal integer but received {value.GetType().Name}.")
    };

    private sealed class WarRig : IAsyncDisposable
    {
        private readonly string[] _schemas;
        private WarRig(Npgsql.NpgsqlDataSource source, string[] schemas, long now)
        {
            Source = source;
            _schemas = schemas;
            Now = now;
            Scheduler = new Eve.Scheduler.DurableScheduler(source, () => Now, new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, schemas[0]);
            Bus = new Eve.Events.EventBus(source, Scheduler, null, schemas[1]);
            Relations = new Eve.Relations.RelationStore(source, Scheduler, Bus, schemas[2]);
        }

        public Npgsql.NpgsqlDataSource Source { get; }
        public Eve.Scheduler.DurableScheduler Scheduler { get; }
        public Eve.Events.EventBus Bus { get; }
        public Eve.Relations.RelationStore Relations { get; }
        public long Now { get; }

        public static async Task<WarRig> CreateAsync()
        {
            var source = Npgsql.NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Eve.Corporations.Tests.Rig.Url!));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["war_sched_" + id, "war_events_" + id, "war_rel_" + id];
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000;
            await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schemas[0]);
            await Eve.Events.EventBus.MigrateAsync(source, schemas[1]);
            await Eve.Relations.RelationStore.MigrateAsync(source, schemas[2]);
            return new WarRig(source, schemas, now);
        }

        public async Task SetAsync(string kind, long subject, long obj, object? attrs = null)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Relations.SetAsync(connection, transaction, kind, subject, obj, 0, "wars read test", attrs);
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }
}

public class CorporationProtocolTests
{
    private sealed class PacketSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static ServiceCall Call(ClientSession session,string method,params PyValue[] args) => new() { Session=session,Method=method,Args=args };
    private static PyValue Field(PyValue value,string key) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries.Single(e => e.Key == new PyStr(key)).Value;

private static PyValue RowField(PyValue value,string key)
{
    var row = Assert.IsType<PyObject>(value);
    Assert.Equal("util.Row",Assert.IsType<PyStr>(row.Name).Value);
    var header = Assert.IsType<PyTuple>(Field(value,"header")).Items;
    var values = Assert.IsType<PyTuple>(Field(value,"line")).Items;
    var index = header.Select((field,index) => (field,index))
        .Single(pair => pair.field == new PyStr(key)).index;
    return values[index];
}

    private static IReadOnlyList<PyValue> Lines(PyValue rowset) =>
        Assert.IsType<PyList>(Field(rowset, "lines")).Items;

    private static IReadOnlyList<PyValue> RowValues(PyValue row) => row switch
    {
        PyList list => list.Items,
        PyPackedRow packed => packed.Values,
        _ => Assert.IsType<PyPackedRow>(MarshalCodec.Decode(MarshalCodec.Encode(row))).Values
    };

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
    public async Task Get_entry_types_returns_the_cached_v24_metadata_rows()
    {
        await using var rig = await Rig.CreateAsync();
        var service = new Eve.Services.Wallet.AccountService(rig.Wallets, rig.Store, rig.Transactions);
        var session = new ClientSession();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cached = Assert.IsType<PyObject>(await service.GetEntryTypes(Call(session, "GetEntryTypes")));
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.Equal(new PyStr("carbon.common.script.net.objectCaching.CachedMethodCallResult"), cached.Name);
        var args = Assert.IsType<PyTuple>(cached.Args).Items;
        Assert.Equal(3, args.Count);
        var details = Assert.IsType<PyDict>(args[0]);
        var detail = Assert.Single(details.Entries);
        Assert.Equal("versionCheck", Eve.Macho.JsSemantics.StringValue(detail.Key));
        Assert.Equal("run", Eve.Macho.JsSemantics.StringValue(detail.Value));
        var rows = Assert.IsType<PyList>(Assert.IsType<PySubStream>(args[1]).Decoded);
        Assert.Equal(26, rows.Items.Count);
        var version = Assert.IsType<PyList>(args[2]);
        Assert.InRange(Assert.IsType<PyLong>(version.Items[0]).Value,
            Py.FileTimeFromUnixMs(before), Py.FileTimeFromUnixMs(after));
        Assert.Equal(new PyInt(Eve.Services.Checksums.SignedAdler32(MarshalCodec.Encode(rows))),
            version.Items[1]);

        var donation = Assert.IsType<PyObject>(Assert.Single(rows.Items,
            row => row is PyObject item && HasTransactionField(item, "entryTypeID", new PyInt(10))));
        AssertTransactionField(donation, "entryTypeName", new PyStr("PlayerDonation"));
        AssertTransactionField(donation, "entryTypeNameID", new PyInt(59802));
        AssertTransactionField(donation, "entryJournalMessageID", new PyInt(234508));

        var officeRent = Assert.IsType<PyObject>(Assert.Single(rows.Items,
            row => row is PyObject item && HasTransactionField(item, "entryTypeID", new PyInt(13))));
        AssertTransactionField(officeRent, "entryTypeNameID", PyNone.Instance);
        AssertTransactionField(officeRent, "entryJournalMessageID", PyNone.Instance);
    }

    [PgFact]
    public async Task Get_transactions_returns_persisted_rows_and_enforces_division_read_access()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var corp = await rig.Store.CreateAsync(founder, new("Orbital Works", "ORBIT", "", "", 0.05m), "first");
        var application = await rig.Store.ApplyAsync(member, corp.Id, "Join");
        await rig.Store.RespondAsync(founder, application, 6, "Join", "");
        await rig.Store.RespondAsync(member, application, 2, "Join", "");

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Transactions.TransferAsync(connection, transaction, new Eve.Wallet.WalletAccount(founder),
                new Eve.Wallet.WalletAccount(corp.Id, 1006), 123.45m, 10, "Deposit", rig.Now, "deposit", "test");
            await transaction.CommitAsync();
        }

        var service = new Eve.Services.Wallet.AccountService(rig.Wallets, rig.Store, rig.Transactions);
        var session = new ClientSession();
        session.Set("charid", new PyInt(founder));
        session.Set("corpid", new PyInt(corp.Id));
        var date = DateTimeOffset.FromUnixTimeMilliseconds(rig.Now);
        var corporateCall = Call(session, "GetTransactions", new PyInt(1006), new PyInt(date.Year), new PyInt(date.Month), PyBool.True);
        var corporateRows = Assert.IsType<PyList>(await service.GetTransactions(corporateCall));
        var corporateRow = Assert.IsType<PyObject>(Assert.Single(corporateRows.Items));
        AssertTransactionField(corporateRow, "accountKey", new PyInt(1006));
        AssertTransactionField(corporateRow, "entryTypeID", new PyInt(10));
        AssertTransactionField(corporateRow, "ownerID1", new PyLong(corp.Id));
        AssertTransactionField(corporateRow, "ownerID2", new PyLong(founder));
        AssertTransactionField(corporateRow, "amount", new PyReal(123.45));
        AssertTransactionField(corporateRow, "description", new PyStr("Deposit"));
        AssertTransactionField(corporateRow, "currency", new PyInt(1));
        AssertTransactionField(corporateRow, "sortValue", new PyInt(1));
        AssertTransactionField(corporateRow, "transactionDate", new PyLong(date.UtcDateTime.ToFileTimeUtc()));

        var personalCall = Call(session, "GetTransactions", new PyInt(1000), new PyInt(date.Year), new PyInt(date.Month), PyBool.False);
        var personalRows = Assert.IsType<PyList>(await service.GetTransactions(personalCall));
        var personalRow = Assert.IsType<PyObject>(Assert.Single(personalRows.Items, item => item is PyObject row && HasTransactionField(row, "amount", new PyReal(-123.45))));
        AssertTransactionField(personalRow, "accountKey", new PyInt(1000));
        AssertTransactionField(personalRow, "ownerID1", new PyLong(founder));
        AssertTransactionField(personalRow, "ownerID2", new PyLong(corp.Id));
        AssertTransactionField(personalRow, "amount", new PyReal(-123.45));

        session.Set("charid", new PyInt(member));
        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.GetTransactions(
            Call(session, "GetTransactions", new PyInt(1006), new PyInt(date.Year), new PyInt(date.Month), PyBool.True)));

        var persisted = (await rig.Store.MemberAsync(member))!;
        await rig.Store.UpdateMemberAsync(founder, persisted with { Masks = new RoleMasks(CorporationRoles.WalletTakeRole(1006)) });
        var memberRows = Assert.IsType<PyList>(await service.GetTransactions(
            Call(session, "GetTransactions", new PyInt(1006), new PyInt(date.Year), new PyInt(date.Month), PyBool.True)));
        Assert.Single(memberRows.Items);
    }

    [PgFact]
    public async Task Dividend_payout_requires_master_wallet_take_role()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var corp = await rig.Store.CreateAsync(founder, new("Orbital Works", "ORBIT", "", "", 0.05m), "first");
        var application = await rig.Store.ApplyAsync(member, corp.Id, "Join");
        await rig.Store.RespondAsync(founder, application, 6, "Join", "");
        await rig.Store.RespondAsync(member, application, 2, "Join", "");

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Transactions.TransferAsync(connection, transaction, new Eve.Wallet.WalletAccount(founder),
                new Eve.Wallet.WalletAccount(corp.Id, 1000), 1000m, 10, "Deposit", rig.Now, "dividend-deposit", "test");
            await transaction.CommitAsync();
        }

        var service = new CorpRegistryService(rig.Store, rig.Characters, () => rig.Now);
        var session = new ClientSession();
        session.Set("charid", new PyInt(member));
        session.Set("corpid", new PyInt(corp.Id));
        var before = (await rig.Wallets.GetAsync(corp.Id))!.Isk;
        var memberBefore = (await rig.Wallets.GetAsync(member))!.Isk;
        Assert.IsType<PyNone>(await service.PayoutDividend(Call(session, "PayoutDividend", PyBool.False, new PyInt(100))));
        Assert.Equal(before, (await rig.Wallets.GetAsync(corp.Id))!.Isk);
        Assert.Equal(memberBefore, (await rig.Wallets.GetAsync(member))!.Isk);

        var persisted = (await rig.Store.MemberAsync(member))!;
        await rig.Store.UpdateMemberAsync(founder, persisted with { Masks = new RoleMasks(CorporationRoles.WalletTakeRole(1000)) });
        Assert.IsType<PyNone>(await service.PayoutDividend(Call(session, "PayoutDividend", PyBool.False, new PyInt(100))));
        Assert.Equal(before - 100m, (await rig.Wallets.GetAsync(corp.Id))!.Isk);
        Assert.Equal(memberBefore + 50m, (await rig.Wallets.GetAsync(member))!.Isk);
    }

    private static void AssertTransactionField(PyObject row, string name, PyValue expected) =>
        Assert.Contains(Assert.IsType<PyDict>(row.Args).Entries,
            entry => entry.Key.Equals(new PyStr(name)) && entry.Value.Equals(expected));

    private static bool HasTransactionField(PyObject row, string name, PyValue expected) =>
        Assert.IsType<PyDict>(row.Args).Entries.Any(entry =>
            entry.Key.Equals(new PyStr(name)) && entry.Value.Equals(expected));

    [PgFact]
    public async Task Wallet_division_info_lists_persisted_readable_balances()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Founder One");
        var member = await rig.CharacterAsync("Member One");
        var corp = await rig.Store.CreateAsync(founder, new("Orbital Works", "ORBIT", "", "", 0.05m), "first");
        var application = await rig.Store.ApplyAsync(member, corp.Id, "Join");
        await rig.Store.RespondAsync(founder, application, 6, "Join", "");
        await rig.Store.RespondAsync(member, application, 2, "Join", "");

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Transactions.TransferAsync(connection, transaction, new Eve.Wallet.WalletAccount(founder),
                new Eve.Wallet.WalletAccount(corp.Id, 1006), 123.45m, 10, "Deposit", rig.Now, "deposit", "test");
            await transaction.CommitAsync();
        }

        var service = new Eve.Services.Wallet.AccountService(rig.Wallets, rig.Store, rig.Transactions);
        var session = new ClientSession();
        session.Set("charid", new PyInt(founder));
        session.Set("corpid", new PyInt(corp.Id));
        var founderRows = Assert.IsType<PyList>(await service.GetWalletDivisionsInfo(Call(session, "GetWalletDivisionsInfo")));
        Assert.Equal(7, founderRows.Items.Count);
        Assert.Equal(WalletDivisionRow(1006, 123.45), founderRows.Items[6]);

        session.Set("charid", new PyInt(member));
        var memberRows = Assert.IsType<PyList>(await service.GetWalletDivisionsInfo(Call(session, "GetWalletDivisionsInfo")));
        Assert.Empty(memberRows.Items);

        var persisted = (await rig.Store.MemberAsync(member))!;
        await rig.Store.UpdateMemberAsync(founder, persisted with { Masks = new RoleMasks(CorporationRoles.WalletTakeRole(1006)) });
        memberRows = Assert.IsType<PyList>(await service.GetWalletDivisionsInfo(Call(session, "GetWalletDivisionsInfo")));
        Assert.Single(memberRows.Items);
        Assert.Equal(WalletDivisionRow(1006, 123.45), memberRows.Items[0]);
    }

    private static PyValue WalletDivisionRow(int key, double balance) => Py.KeyVal([
        new KeyValuePair<PyValue, PyValue>(new PyStr("key"), new PyInt(key)),
        new KeyValuePair<PyValue, PyValue>(new PyStr("balance"), new PyReal(balance)),
    ]);

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
            var legacyMigrations = CorporationStore.MigrationSet().Take(3).ToArray();
            var initial = await Eve.Data.Migrations.ApplyAsync(rig.Source,schema,legacyMigrations);
            Assert.Equal(new[] { "001_corporations","002_npc_corporations","003_alliance_governance" },initial);

            await using (var connection = await rig.Source.OpenConnectionAsync())
            {
                async Task Execute(string sql)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }

                await Execute($"INSERT INTO {schema}.owner_names(owner_id,name_key,ticker_key) VALUES (1,'legacy corporation','legacy')");
                await Execute($"""
                    INSERT INTO {schema}.corporations(
                        id,name,ticker,ceo_id,creator_id,station_id,solar_system_id,
                        tax_rate,lp_tax_rate,description,url,member_limit,created_ms)
                    VALUES (1,'Legacy Corporation','LEG',2,2,60003760,30000142,0,0,'','',20,1)
                    """);
                await Execute($"INSERT INTO {schema}.owner_names(owner_id,name_key,ticker_key) VALUES (2,'legacy alliance','la')");
                await Execute($"ALTER TABLE {schema}.alliances ADD COLUMN current_prime_hour smallint NOT NULL DEFAULT 17 CHECK (current_prime_hour BETWEEN 0 AND 23)");
                await Execute($"""
                    INSERT INTO {schema}.alliances(
                        id,name,short_name,description,url,executor_corporation_id,
                        creator_corporation_id,creator_character_id,created_ms,founding_key)
                    VALUES (2,'Legacy Alliance','LA','legacy','',1,1,2,1,'legacy-alliance')
                    """);
                await Execute($"""
                    INSERT INTO {schema}.applications(
                        id,corporation_id,character_id,text,status,created_ms,updated_ms,response,archived)
                    OVERRIDING SYSTEM VALUE
                    VALUES (42,1,2,'',8,1,1,'',false),(99,1,3,'',0,1,1,'',false)
                    """);
                await Execute($"""
                    INSERT INTO {schema}.medals(
                        id,corporation_id,creator_id,title,description,data,created_ms)
                    OVERRIDING SYSTEM VALUE
                    VALUES (7,1,9,'Legacy medal','',jsonb_build_object(),10)
                    """);
                await Execute($"INSERT INTO {schema}.medal_awards(medal_id,character_id,issuer_id,reason,created_ms) VALUES (7,8,9,'legacy',10)");
            }

            var applied = await Eve.Data.Migrations.ApplyAsync(rig.Source,schema,CorporationStore.MigrationSet());
            Assert.Equal(new[] {
                "004_invitation_ids",
                "005_medal_award_occurrences",
                "006_alliance_shares_votes",
                "007_corporation_welcome_mail",
                "008_corporation_recruitment_ads",
                "009_corporation_social",
                "010_corporation_war_bills",
                "011_alliance_registry_state",
                "012_alliance_bill_lifecycle",
                "013_item_locks"
            },applied);

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
            await using (var ceoState = read.CreateCommand())
            {
                ceoState.CommandText = $"SELECT ceo_roles_suspended FROM {schema}.corporations WHERE id=1";
                Assert.False((bool)(await ceoState.ExecuteScalarAsync())!);
            }
            await using (var allianceState = read.CreateCommand())
            {
                allianceState.CommandText = $"SELECT current_prime_hour,current_capital_system_id,new_capital_system_id,new_capital_system_valid_after_filetime FROM {schema}.alliances WHERE id=2";
                await using var state = await allianceState.ExecuteReaderAsync();
                Assert.True(await state.ReadAsync());
                Assert.Equal((short)17,state.GetInt16(0));
                Assert.True(state.IsDBNull(1));
                Assert.True(state.IsDBNull(2));
                Assert.Equal(0L,state.GetInt64(3));
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

        var tooManyMemberIds = new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyInt(id)).ToArray());
        var limitError = await Assert.ThrowsAsync<CorporationError>(async () => await CorpRpc("GetMembersByIds", tooManyMemberIds));
        Assert.Equal("CORPORATION_BULK_LIMIT_EXCEEDED", limitError.Code);
        var tooManyMemberUpdates = new PyList(Enumerable.Range(0, 501).Select(_ => (PyValue)new PyList([])).ToArray());
        var updateLimitError = await Assert.ThrowsAsync<CorporationError>(async () => await CorpRpc("UpdateMembers", tooManyMemberUpdates));
        Assert.Equal("CORPORATION_BULK_LIMIT_EXCEEDED", updateLimitError.Code);
        var oversizedActionTargets = new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyInt(id)).ToArray());
        var actionTargetLimitError = await Assert.ThrowsAsync<CorporationError>(async () =>
            await CorpRpc("ExecuteActions", oversizedActionTargets, new PyList([])));
        Assert.Equal("CORPORATION_BULK_LIMIT_EXCEEDED", actionTargetLimitError.Code);
        var oversizedActions = new PyList(Enumerable.Range(0, 501).Select(_ => (PyValue)new PyList([])).ToArray());
        var actionLimitError = await Assert.ThrowsAsync<CorporationError>(async () =>
            await CorpRpc("ExecuteActions", new PyList([new PyInt(member)]), oversizedActions));
        Assert.Equal("CORPORATION_BULK_LIMIT_EXCEEDED", actionLimitError.Code);
        var oversizedMemberQuery = new PyList(Enumerable.Range(0, 101).Select(_ => (PyValue)new PyList([])).ToArray());
        var memberQueryLimitError = await Assert.ThrowsAsync<CorporationError>(async () =>
            await CorpRpc("GetMemberIDsByQuery", oversizedMemberQuery));
        Assert.Equal("CORPORATION_QUERY_LIMIT_EXCEEDED", memberQueryLimitError.Code);
        var longMemberQuery = new PyList([new PyList([new PyStr("characterID"), new PyInt(9), new PyStr(new string('x', 257))])]);
        var memberQueryTextLimitError = await Assert.ThrowsAsync<CorporationError>(async () =>
            await CorpRpc("GetMemberIDsByQuery", longMemberQuery));
        Assert.Equal("CORPORATION_QUERY_LIMIT_EXCEEDED", memberQueryTextLimitError.Code);
        var duplicateMemberUpdates = new PyList([
            new PyList([new PyInt(member)]), new PyList([new PyInt(member)])
        ]);
        var duplicateUpdateError = await Assert.ThrowsAsync<CorporationError>(async () => await CorpRpc("UpdateMembers", duplicateMemberUpdates));
        Assert.Equal("CORPORATION_ACCESS_DENIED", duplicateUpdateError.Code);

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
    public async Task Corporation_medal_rpcs_enforce_manager_roles_and_member_recipients()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Medal Founder");
        var member = await rig.CharacterAsync("Medal Member");
        var outsider = await rig.CharacterAsync("Medal Outsider");
        var corp = await rig.Store.CreateAsync(founder, new("Medal Works", "MEDAL", "", "", 0.05m), "medals");
        var application = await rig.Store.ApplyAsync(member, corp.Id, "");
        await rig.Store.RespondAsync(founder, application, 6, "", "");
        await rig.Store.RespondAsync(member, application, 2, "", "");
        Assert.Equal(0L, (await rig.Store.MemberAsync(member))!.Masks.Roles);

        var service = (IService)new CorporationService(rig.Store, rig.Characters);
        var memberSession = new ClientSession();
        memberSession.CharacterId = member;
        memberSession.Set("corporationID", new PyInt(corp.Id));
        memberSession.Set("role", new PyLong(CorporationRoles.PersonnelManager));
        memberSession.Set("rolesAtAll", new PyLong(CorporationRoles.PersonnelManager));
        var founderSession = new ClientSession();
        founderSession.CharacterId = founder;
        founderSession.Set("corpid", new PyInt(corp.Id));
        ValueTask<PyValue> MemberRpc(string method, params PyValue[] args) => service.CallAsync(Call(memberSession, method, args));
        ValueTask<PyValue> FounderRpc(string method, params PyValue[] args) => service.CallAsync(Call(founderSession, method, args));

        var graphics = new PyList([Py.Tuple(new PyInt(1), new PyWString("emblem.42"), new PyInt(7))]);
        await Assert.ThrowsAsync<CorporationError>(async () => await MemberRpc("CreateMedal",
            new PyWString("Denied"), new PyWString("Forged role"), graphics));
        var founderMedalId = Assert.IsType<PyInt>(await FounderRpc("CreateMedal",
            new PyWString("Founder"), new PyWString("For service"), graphics)).Value;
        var forgedStatus = new PyDict([new(new PyInt(founderMedalId), new PyInt(2))]);
        await Assert.ThrowsAsync<CorporationError>(async () => await MemberRpc("SetMedalStatus", forgedStatus));
        await Assert.ThrowsAsync<CorporationError>(async () => await MemberRpc("GiveMedalToCharacters",
            new PyInt(founderMedalId), new PyList([new PyInt(founder)]), new PyWString("Unauthorized")));

        var memberRecord = await rig.Store.MemberAsync(member);
        await rig.Store.UpdateMemberAsync(founder, memberRecord! with { Masks = new RoleMasks(CorporationRoles.PersonnelManager) });
        var medalId = Assert.IsType<PyInt>(await MemberRpc("CreateMedal",
            new PyWString("Service"), new PyWString("For service"), graphics)).Value;
        var all = Assert.IsType<PyTuple>(CachedPayload(await MemberRpc("GetAllCorpMedals", new PyInt(corp.Id))));
        var medalRows = Lines(all.Items[0]).Select(value => Assert.IsType<PyList>(value)).ToArray();
        Assert.Equal(2, medalRows.Length);
        var medalRow = Assert.Single(medalRows, row => Assert.IsType<PyInt>(row.Items[0]).Value == medalId);
        Assert.Equal(new PyInt(corp.Id), medalRow.Items[1]);
        Assert.Equal(new PyWString("Service"), medalRow.Items[3]);
        Assert.Equal(new PyWString("For service"), medalRow.Items[4]);
        Assert.IsType<PyLong>(medalRow.Items[5]);
        Assert.Equal(new PyInt(0), medalRow.Items[6]);
        var graphicRow = Assert.Single(Lines(all.Items[1]).Select(value => Assert.IsType<PyList>(value)),
            row => Assert.IsType<PyInt>(row.Items[0]).Value == medalId);
        Assert.Equal(new PyWString("emblem.42"), graphicRow.Items[2]);
        var emptyDetails = await MemberRpc("GetMedalDetails", new PyInt(medalId));
        var emptyDetail = Assert.Single(Assert.IsType<PyList>(Field(emptyDetails, "info")).Items);
        Assert.Equal(new PyInt(0), Field(emptyDetail, "numberOfRecipients"));

        var status = new PyDict([new(new PyInt(medalId), new PyInt(2))]);
        Assert.IsType<PyNone>(await MemberRpc("SetMedalStatus", status));
        await Assert.ThrowsAsync<CorporationError>(async () => await MemberRpc("GiveMedalToCharacters",
            new PyInt(medalId), new PyList([new PyInt(founder), new PyInt(outsider)]), new PyWString("Partial award")));
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
        var crossCharacterReceived = Assert.IsType<PyTuple>(await MemberRpc("GetMedalsReceived", new PyInt(founder)));
        Assert.Empty(Lines(crossCharacterReceived.Items[0]));

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
        var affiliationWrites = new List<(long CorporationId,long? AllianceId,bool SameTransaction)>();
        var memberAffiliationWrites = new List<(long CharacterId,long CorporationId,bool SameTransaction)>();
        rig.Store.CorporationMemberAffiliationWriter = (connection,transaction,characterId,corporationId,token) =>
        {
            memberAffiliationWrites.Add((characterId,corporationId,ReferenceEquals(connection,transaction.Connection)));
            return Task.CompletedTask;
        };
        registry.AllianceAffiliationWriter = (connection,transaction,corporationId,allianceId,token) =>
        {
            affiliationWrites.Add((corporationId,allianceId,ReferenceEquals(connection,transaction.Connection)));
            return Task.CompletedTask;
        };

        var application = await rig.Store.ApplyAsync(member,corp.Id,"Join");
        await registry.UpdateApplicationOffer(Call(founderSession,"UpdateApplicationOffer",new PyInt(application),new PyInt(member),
            new PyInt(corp.Id),new PyWString("Join"),new PyInt(6),new PyWString("Welcome")));
        founderSession.RunAfterResponse();
        Assert.Empty(affiliationWrites);
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
        Assert.Empty(affiliationWrites);
        Assert.Equal((member,corp.Id,true),Assert.Single(memberAffiliationWrites));
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
        await registry.ResignFromCEO(Call(founderSession,"ResignFromCEO",new PyInt(member)));
        Assert.Empty(affiliationWrites);
        Assert.Single(memberAffiliationWrites);
        founderSession.RunAfterResponse();
    }

    [Fact]
    public async Task Standing_manager_reads_character_rows_corporation_averages_history_and_npc_relations()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "standing_" + Guid.NewGuid().ToString("N")[..12];
        var relationSchema = schema + "_relations";
        var schedulerSchema = schema + "_scheduler";
        var eventsSchema = schema + "_events";
        var staticSchema = schema + "_sde";
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationSchema);
            await using (var seedSde = rig.Source.CreateCommand($"""
                CREATE SCHEMA {staticSchema};
                CREATE TABLE {staticSchema}."npcCorporations" (key text PRIMARY KEY, doc jsonb NOT NULL);
                CREATE TABLE {staticSchema}.factions (key text PRIMARY KEY, doc jsonb NOT NULL);
                INSERT INTO {staticSchema}."npcCorporations"(key, doc)
                    VALUES ('1000001', jsonb_build_object('factionID', 500001, 'enemyID', 1000002, 'friendID', 1000002)),
                           ('1000002', jsonb_build_object('factionID', 500002, 'enemyID', 1000001));
                INSERT INTO {staticSchema}.factions(key, doc)
                    VALUES ('500001', jsonb_build_object('corporationID', 1000001)),
                           ('500002', jsonb_build_object('corporationID', 1000002));
                """))
            {
                await seedSde.ExecuteNonQueryAsync();
            }

            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationSchema);
            var reads = new Eve.ReadSide.ReadSide(rig.Source, new Dictionary<string, string>
            {
                ["relations"] = relationSchema,
                ["sde"] = staticSchema,
            });
            var founder = await rig.CharacterAsync("Standing Founder");
            var member = await rig.CharacterAsync("Standing Member");
            var corporation = await rig.Store.CreateAsync(
                founder, new("Standing Works", "STAND", "", "", 0.05m), "standing-corp");
            await relations.SetAsync(Eve.Relations.RelationKinds.CorporationMember.Name, founder, corporation.Id, 0, "test");
            await relations.SetAsync(Eve.Relations.RelationKinds.CorporationMember.Name, member, corporation.Id, 0, "test");
            await relations.SetAsync(Eve.Relations.RelationKinds.Standing.Name, 500001, founder, 1.25,
                """{"eventTypeID":65,"eventDateTime":"134000000000000000","modification":0.125,"msg":"standing changed"}""");
            await relations.SetAsync(Eve.Relations.RelationKinds.Standing.Name, 500002, founder, -0.5, "test");
            await relations.SetAsync(Eve.Relations.RelationKinds.Standing.Name, 500001, member, 2.75, "test");

            var service = new StandingMgrService(reads, rig.Store, () => rig.Now);
            var characterSession = new ClientSession { CharacterId = founder };
            var characterRows = Lines(await service.GetCharStandings(Call(characterSession, "GetCharStandings")));
            Assert.Equal(2, characterRows.Count);
            var firstCharacterRow = Assert.IsType<PyList>(characterRows[0]).Items;
            Assert.Equal(new PyInt(500001), firstCharacterRow[0]);
            Assert.Equal(new PyReal(1.25), firstCharacterRow[1]);

            var corporationSession = new ClientSession();
            corporationSession.Set("corporationID", new PyInt(corporation.Id));
            var corporationRows = Lines(await service.GetCorpStandings(Call(corporationSession, "GetCorpStandings")));
            Assert.Equal(2, corporationRows.Count);
            var firstCorporationRow = Assert.IsType<PyList>(corporationRows[0]).Items;
            Assert.Equal(new PyInt(500001), firstCorporationRow[0]);
            Assert.Equal(new PyReal(2.0), firstCorporationRow[1]);
            await relations.SetAsync(Eve.Relations.RelationKinds.Standing.Name, 500001, corporation.Id, 4.5, "corporation override");
            var explicitCorporationRows = Lines(await service.GetCorpStandings(Call(corporationSession, "GetCorpStandings")));
            Assert.Single(explicitCorporationRows);
            var explicitCorporationRow = Assert.IsType<PyList>(explicitCorporationRows[0]).Items;
            Assert.Equal(new PyReal(4.5), explicitCorporationRow[1]);

            var npcRows = Lines(await service.GetNPCNPCStandings(Call(characterSession, "GetNPCNPCStandings")));
            Assert.Equal(8, npcRows.Count);
            var combinedNpcRelation = npcRows
                .Select(row => Assert.IsType<PyList>(row).Items)
                .Single(values => values[0] == new PyInt(1000001) && values[1] == new PyInt(1000002));
            Assert.Equal(new PyReal(1.0), combinedNpcRelation[2]);

            var transactions = Assert.IsType<PyList>(await service.GetStandingTransactions(
                Call(characterSession, "GetStandingTransactions", new PyInt(500001), new PyInt(founder))));
            Assert.Single(transactions.Items);
            Assert.Equal(new PyInt(65), Field(transactions.Items[0], "eventTypeID"));
            Assert.Equal(new PyLong(134000000000000000), Field(transactions.Items[0], "eventDateTime"));
            Assert.Equal(new PyReal(0.125), Field(transactions.Items[0], "modification"));

            var compositionPayload = CachedPayload(await service.GetStandingCompositions(
                Call(characterSession, "GetStandingCompositions", new PyInt(500001), new PyInt(corporation.Id))));
            Assert.Equal(new PyStr("carbon.common.script.sys.crowset.CRowset"), Assert.IsType<PyObject>(compositionPayload).Name);
            Assert.Equal(2, Lines(compositionPayload).Count);
        }
        finally
        {
            foreach (var cleanupSchema in new[] { staticSchema, eventsSchema, schedulerSchema, relationSchema })
            {
                await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {cleanupSchema} CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public void Standing_manager_service_exposes_the_reference_read_methods()
    {
        var type = typeof(CorporationService).Assembly.GetType("Eve.Services.Corporations.StandingMgrService");
        Assert.NotNull(type);
        var methods = type!.GetMethods()
            .Where(method => Attribute.IsDefined(method, typeof(MethodAttribute)))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(methods, new HashSet<string>
        {
            "GetNPCNPCStandings", "GetCharStandings", "GetCorpStandings",
            "GetStandingTransactions", "GetStandingCompositions"
        });
        Assert.Contains("GetCharStandings", methods);
    }

    [Fact]
    public void Corp_mgr_exposes_every_reference_rpc()
    {
        var service = (IService)new CorpMgrService(null!, null!);
        foreach (var method in new[]
        {
            "AuditMember", "GetAggressionSettings", "GetAggressionSettingsForCorps",
            "GetAssetInventory", "GetAssetInventoryForLocation", "GetCorporationIDForCharacter", "SearchAssets"
        }) Assert.True(service.HasMethod(method), method);
    }

    [PgFact]
    public async Task Corp_mgr_aggression_and_asset_rpcs_use_login_corporation_and_inventory_data()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Corp RPC Founder");
        var corporation = await rig.Store.CreateAsync(
            founder, new NewCorporation("RPC Assets", "RPCAS", "", "", 0.05m),
            "corp-rpc-assets-" + Guid.NewGuid().ToString("N"));
        var sde = new SdeReader(rig.Source);
        Assert.NotNull(await sde.BuildAsync());
        var itemTypes = new Eve.Inventory.ItemTypeCatalog(sde);
        var tritanium = await itemTypes.GetAsync(34)
            ?? throw new InvalidOperationException("The SDE does not contain Tritanium.");
        const long stationId = 60000001;
        long itemId;
        long deliveryItemId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            itemId = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(34, corporation.Id, stationId, Eve.Inventory.ItemFlags.CorpSAG3,
                    5, CustomName: "corporate stack"));
            deliveryItemId = await rig.Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(34, corporation.Id, stationId, 62, 2, CustomName: "delivery"));
            await transaction.CommitAsync();
        }

        var service = new CorpMgrService(
            rig.Store, rig.Characters, rig.Items, itemTypes, rig.OfficeStore,
            resolveAssetLocation: id => id == stationId ? (30000142L, (int?)1531) : null,
            readAggressionSettings: (id, _) => ValueTask.FromResult<CorpMgrAggressionSettings?>(
                id == corporation.Id ? new CorpMgrAggressionSettings(987654321, null) : null));
        var session = new ClientSession { CharacterId = founder };
        await new CorporationLoginHook(rig.Store, rig.Characters)
            .OnCharacterEnteredAsync(session, founder, default);
        Assert.Equal(corporation.Id, session.GetInt("corpid"));
        Assert.NotEqual(0, (session.GetInt("corprole") ?? 0) & CorporationRoles.Director);
        Assert.Equal(new PyInt(corporation.Id), await service.GetCorporationIDForCharacter(Call(
            session, "GetCorporationIDForCharacter", new PyInt(founder))));

        var aggression = await service.GetAggressionSettings(Call(
            session, "GetAggressionSettings", new PyInt(corporation.Id)));
        Assert.Equal(new PyLong(987654321), Field(aggression, "_enableAfter"));
        Assert.IsType<PyNone>(Field(aggression, "_disableAfter"));
        var aggressionByCorporation = Assert.IsType<PyDict>(await service.GetAggressionSettingsForCorps(Call(
            session, "GetAggressionSettingsForCorps", new PyList([new PyInt(corporation.Id)]))));
        Assert.Equal(new PyLong(987654321), Field(Assert.Single(aggressionByCorporation.Entries).Value, "_enableAfter"));

        var locations = Assert.IsType<PyObject>(CachedPayload(await service.GetAssetInventory(Call(
            session, "GetAssetInventory", new PyInt(corporation.Id), new PyStr("property")))));
        var locationValues = RowValues(Assert.Single(Lines(locations)));
        Assert.Equal(new PyLong(stationId), locationValues[0]);
        Assert.Equal(new PyLong(30000142), locationValues[1]);
        Assert.Equal(new PyInt(1531), locationValues[2]);

        var inventory = Assert.IsType<PyObject>(CachedPayload(await service.GetAssetInventoryForLocation(Call(
            session, "GetAssetInventoryForLocation",
            new PyInt(corporation.Id), new PyLong(stationId), new PyStr("property")))));
        var itemValues = RowValues(Assert.Single(Lines(inventory)));
        Assert.Equal(new PyLong(itemId), itemValues[0]);
        Assert.Equal(new PyInt(34), itemValues[1]);
        Assert.Equal(new PyInt(corporation.Id), itemValues[2]);
        Assert.Equal(new PyLong(stationId), itemValues[3]);
        Assert.Equal(new PyInt(Eve.Inventory.ItemFlags.CorpSAG3), itemValues[4]);
        Assert.Equal(new PyInt(5), itemValues[5]);
        Assert.Equal(new PyInt(tritanium.GroupId), itemValues[6]);
        Assert.Equal(new PyInt(tritanium.CategoryId), itemValues[7]);
        Assert.Equal(new PyWString("corporate stack"), itemValues[8]);

        var search = Assert.IsType<PyObject>(CachedPayload(await service.SearchAssets(Call(
            session, "SearchAssets", new PyStr("property"), new PyInt(0), new PyInt(0),
            new PyInt(34), new PyInt(3)))));
        Assert.Equal(new PyLong(stationId), RowValues(Assert.Single(Lines(search)))[0]);

        var deliveries = Assert.IsType<PyObject>(CachedPayload(await service.GetAssetInventory(Call(
            session, "GetAssetInventory", new PyInt(corporation.Id), new PyStr("deliveries")))));
        var deliveryLocation = RowValues(Assert.Single(Lines(deliveries)));
        Assert.Equal(new PyLong(stationId), deliveryLocation[0]);
        Assert.Equal(new PyInt(1), deliveryLocation[3]);
        var deliveryItems = Assert.IsType<PyObject>(CachedPayload(await service.GetAssetInventoryForLocation(Call(
            session, "GetAssetInventoryForLocation",
            new PyInt(corporation.Id), new PyLong(stationId), new PyStr("deliveries")))));
        Assert.Equal(new PyLong(deliveryItemId), RowValues(Assert.Single(Lines(deliveryItems)))[0]);

        var attacker = await rig.CharacterAsync("Corp RPC Attacker");
        var forgedRoles = new ClientSession { CharacterId = attacker };
        forgedRoles.Set("corpid", new PyInt(corporation.Id));
        forgedRoles.Set("corprole", new PyInt(CorporationRoles.Accountant));
        await Assert.ThrowsAsync<CorporationError>(async () => await service.GetAssetInventory(Call(
            forgedRoles, "GetAssetInventory", new PyInt(corporation.Id), new PyStr("property"))));

        var mismatched = new ClientSession { CharacterId = founder };
        mismatched.Set("corpid", new PyInt(corporation.Id + 1));
        await Assert.ThrowsAsync<CorporationError>(async () => await service.GetAssetInventory(Call(
            mismatched, "GetAssetInventory", new PyInt(corporation.Id), new PyStr("property"))));

        var member = await rig.CharacterAsync("Corp RPC Member");
        var registry = new CorpRegistryService(rig.Store, rig.Characters, () => rig.Now);
        var memberSession = new ClientSession { CharacterId = member };
        var applicationId = Assert.IsType<PyInt>(await registry.InsertApplication(Call(
            memberSession, "InsertApplication", new PyInt(corporation.Id), new PyStr("Please consider my application.")))).Value;
        await registry.UpdateApplicationOffer(Call(
            session, "UpdateApplicationOffer", new PyInt(applicationId), new PyInt(member),
            new PyInt(corporation.Id), new PyStr(""), new PyInt(6), new PyStr("")));
        await registry.UpdateApplicationOffer(Call(
            memberSession, "UpdateApplicationOffer", new PyInt(applicationId), new PyInt(member),
            new PyInt(corporation.Id), new PyStr(""), new PyInt(2), new PyStr("")));
        await new CorporationLoginHook(rig.Store, rig.Characters)
            .OnCharacterEnteredAsync(memberSession, member, default);
        Assert.Equal(corporation.Id, memberSession.GetInt("corpid"));
        await Assert.ThrowsAsync<CorporationError>(async () => await service.GetAssetInventory(Call(
            memberSession, "GetAssetInventory", new PyInt(corporation.Id), new PyStr("property"))));
    }

    [PgFact]
    public async Task Corp_mgr_audit_member_uses_login_roles_and_stored_role_history()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Audit RPC Founder");
        var memberId = await rig.CharacterAsync("Audit RPC Member");
        var corporation = await rig.Store.CreateAsync(
            founder, new NewCorporation("RPC Audit", "RPCAU", "", "", 0.05m),
            "corp-rpc-audit-" + Guid.NewGuid().ToString("N"));
        var application = await rig.Store.ApplyAsync(memberId, corporation.Id, "Join");
        await rig.Store.RespondAsync(founder, application, 6, "Join", "");
        await rig.Store.RespondAsync(memberId, application, 2, "Join", "");

        var member = (await rig.Store.MemberAsync(memberId))!;
        await rig.Store.UpdateMembersAsync(founder,
            [member with { Masks = member.Masks with { Roles = CorporationRoles.Accountant } }]);

        var auditReads = 0;
        var service = new CorpMgrService(
            rig.Store, rig.Characters,
            readAuditEvents: (corpId, targetId, _, _, _, _) =>
            {
                auditReads++;
                IReadOnlyList<CorpMgrAuditEvent> events =
                [
                    new CorpMgrAuditEvent(77, Py.FileTimeFromUnixMs(rig.Now), 3, targetId, corpId)
                ];
                return ValueTask.FromResult(events);
            });
        var login = new CorporationLoginHook(rig.Store, rig.Characters);
        var founderSession = new ClientSession { CharacterId = founder };
        await login.OnCharacterEnteredAsync(founderSession, founder, default);

        var response = Assert.IsType<PyTuple>(await service.AuditMember(Call(
            founderSession, "AuditMember", new PyInt(memberId), new PyLong(0), new PyLong(0), new PyInt(10))));
        var eventsRowset = response.Items[0];
        var eventValues = RowValues(Assert.Single(Lines(eventsRowset)));
        Assert.Equal(new PyLong(77), eventValues[0]);
        Assert.Equal(new PyInt(3), eventValues[2]);
        Assert.Equal(new PyInt(memberId), eventValues[3]);
        Assert.Equal(new PyInt(corporation.Id), eventValues[4]);

        var roleValues = RowValues(Assert.Single(Lines(response.Items[1])));
        Assert.Equal(new PyInt(memberId), roleValues[0]);
        Assert.Equal(new PyInt(corporation.Id), roleValues[1]);
        Assert.Equal(PyBool.False, roleValues[3]);
        Assert.Equal(new PyLong(0), roleValues[4]);
        Assert.Equal(new PyLong(CorporationRoles.Accountant), roleValues[5]);
        Assert.Equal(new PyInt(founder), roleValues[6]);

        var memberSession = new ClientSession { CharacterId = memberId };
        await login.OnCharacterEnteredAsync(memberSession, memberId, default);
        var denied = Assert.IsType<PyTuple>(await service.AuditMember(Call(
            memberSession, "AuditMember", new PyInt(memberId), new PyLong(0), new PyLong(0), new PyInt(10))));
        Assert.Empty(Lines(denied.Items[0]));
        Assert.Empty(Lines(denied.Items[1]));
        Assert.Equal(1, auditReads);
    }

    [Fact]
    public async Task Mutual_war_invite_mgr_exposes_reference_routes_and_callback_shapes()
    {
        long pendingOwner = 0;
        (long From, long To)? sent = null;
        (long From, long To)? withdrawn = null;
        (long From, long To, bool Accepts)? response = null;
        (long Owner, bool Blocked)? blockChange = null;
        var service = new MutualWarInviteMgrService(
            readPendingInvites: (owner, _) =>
            {
                pendingOwner = owner;
                IReadOnlyList<MutualWarInvite> invites =
                [
                    new MutualWarInvite(8001, owner, 10),
                    new MutualWarInvite(8002, owner, 20)
                ];
                return ValueTask.FromResult(invites);
            },
            readInvitesBlocked: (owner, _) => ValueTask.FromResult(owner is 7001 or 8002),
            sendInvite: (_, from, to, _) =>
            {
                sent = (from, to);
                return ValueTask.CompletedTask;
            },
            withdrawInvite: (_, from, to, _) =>
            {
                withdrawn = (from, to);
                return ValueTask.CompletedTask;
            },
            respondToInvite: (_, from, to, accepts, _) =>
            {
                response = (from, to, accepts);
                return ValueTask.CompletedTask;
            },
            setInvitesBlocked: (_, owner, blocked, _) =>
            {
                blockChange = (owner, blocked);
                return ValueTask.CompletedTask;
            });
        var rpc = (IService)service;
        foreach (var method in new[]
        {
            "GetPendingInvitesForSession", "IsCorpInvitesBlockedPlayer", "RespondToInviteByPlayer",
            "SendInviteByPlayer", "SetInvitesBlockedByPlayer", "WithdrawInviteByPlayer"
        }) Assert.True(rpc.HasMethod(method), method);

        var session = new ClientSession();
        session.Set("allianceID", new PyLong(7001));
        session.Set("allianceid", new PyLong(7002));
        session.Set("corpid", new PyLong(7003));

        var invites = Assert.IsType<PyList>(await service.GetPendingInvitesForSession(
            Call(session, "GetPendingInvitesForSession")));
        Assert.Equal(7001, pendingOwner);
        Assert.Equal(2, invites.Items.Count);
        Assert.Equal(new PyInt(8002), Field(invites.Items[0], "fromOwnerID"));
        Assert.Equal(new PyLong(20), Field(invites.Items[0], "sentDate"));
        Assert.Equal(new PyInt(1), await service.IsCorpInvitesBlockedPlayer(
            Call(session, "IsCorpInvitesBlockedPlayer")));

        Assert.IsType<PyNone>(await service.SendInviteByPlayer(
            Call(session, "SendInviteByPlayer", new PyInt(8002))));
        Assert.Null(sent);
        Assert.IsType<PyNone>(await service.SendInviteByPlayer(
            Call(session, "SendInviteByPlayer", new PyInt(8003))));
        Assert.Equal((7001L, 8003L), sent);

        Assert.IsType<PyNone>(await service.WithdrawInviteByPlayer(
            Call(session, "WithdrawInviteByPlayer", new PyInt(8004))));
        Assert.Equal((7001L, 8004L), withdrawn);
        Assert.IsType<PyNone>(await service.RespondToInviteByPlayer(
            Call(session, "RespondToInviteByPlayer", new PyInt(8005), PyBool.True)));
        Assert.Equal((8005L, 7001L, true), response);
        Assert.IsType<PyNone>(await service.SetInvitesBlockedByPlayer(
            Call(session, "SetInvitesBlockedByPlayer", PyBool.True)));
        Assert.Equal((7001L, true), blockChange);

        var unconfigured = new MutualWarInviteMgrService();
        Assert.Empty(Assert.IsType<PyList>(await unconfigured.GetPendingInvitesForSession(
            Call(session, "GetPendingInvitesForSession"))).Items);
        Assert.Equal(new PyInt(0), await unconfigured.IsCorpInvitesBlockedPlayer(
            Call(session, "IsCorpInvitesBlockedPlayer")));
        foreach (var method in new[] { "SendInviteByPlayer", "WithdrawInviteByPlayer", "RespondToInviteByPlayer", "SetInvitesBlockedByPlayer" })
        {
            var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await ((IService)unconfigured).CallAsync(
                Call(session, method, new PyInt(8002), PyBool.True)));
            Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        }
    }

    [Fact]
    public void Corporation_service_exposes_reference_methods()
    {
        var service = (IService)new CorporationService(null!, null!);
        foreach (var method in new[]
        {
            "CreateMedal", "SetMedalStatus", "GetAllCorpMedals", "GetRecipientsOfMedal",
            "GiveMedalToCharacters", "GetMedalDetails", "GetMedalsReceived", "GetCorpInfo",
            "GetEmployementRecordAndCharacterTransfers", "GetEmploymentRecord"
        }) Assert.True(service.HasMethod(method), method);
    }

    [PgFact]
    public async Task Corporation_employment_and_transfer_rpc_returns_the_history_rowset_and_empty_transfers()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = await rig.CharacterAsync("Employment RPC Character");
        var session = new ClientSession { CharacterId = characterId };
        var service = new CorporationService(rig.Store, rig.Characters);

        var result = Assert.IsType<PyTuple>(await service.GetEmployementRecordAndCharacterTransfers(
            Call(session, "GetEmployementRecordAndCharacterTransfers", new PyInt(characterId))));

        Assert.Single(Lines(result.Items[0]));
        Assert.Empty(Assert.IsType<PyList>(result.Items[1]).Items);
    }

    [Fact]
    public async Task Wars_info_manager_returns_reference_wire_shapes_from_relations()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "wars_" + Guid.NewGuid().ToString("N")[..12];
        var relationSchema = schema + "_relations";
        var schedulerSchema = schema + "_scheduler";
        var eventsSchema = schema + "_events";
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationSchema);

            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationSchema);

            const long attacker = 990001;
            const long defender = 990002;
            const long secondDefender = 990003;
            const long ally = 990004;
            const long ownerWithoutWars = 990005;
            const long structure = 990006;
            const long legacyOwner = 990007;
            const long legacyDefender = 990008;
            const long legacyStructure = 990009;
            var now = rig.Now;
            var firstDeclared = now - 20_000;
            var firstStarted = now - 10_000;
            var secondDeclared = now - 5_000;

            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var first = await relations.SetAsync(connection, transaction, Eve.Relations.RelationKinds.War.Name,
                    attacker, defender, 25, "test",
                    attrs: new
                    {
                        timeDeclared = firstDeclared,
                        timeStarted = firstStarted,
                        mutual = false,
                        openForAllies = true,
                        warHQID = structure,
                        billID = 77,
                        reasonStarted = 1,
                    });
                Assert.NotNull(first);

                var second = await relations.SetAsync(connection, transaction, Eve.Relations.RelationKinds.War.Name,
                    attacker, secondDefender, 50, "test",
                    attrs: new
                    {
                        timeDeclared = secondDeclared,
                        timeStarted = now,
                        timeFinished = now + 1_000,
                        mutual = true,
                        openForAllies = false,
                        warHQID = structure,
                        reasonEnded = 4,
                    });
                Assert.NotNull(second);

                await relations.SetAsync(connection, transaction, Eve.Relations.RelationKinds.WarAlly.Name,
                    ally, first!.RelationId, 0, "test",
                    attrs: new { side = "defender" });

                var legacy = await relations.SetAsync(connection, transaction, Eve.Relations.RelationKinds.War.Name,
                    legacyOwner, legacyDefender, 0, "test",
                    attrs: new { warHQ = legacyStructure, timeDeclared = now - 2_000, timeStarted = now });
                Assert.NotNull(legacy);

                await transaction.CommitAsync();
            }

            var service = new WarsInfoMgrService(relations, () => rig.Now);
            var attackerSession = new ClientSession();
            var ownerResult = CachedPayload(await service.GetWarsByOwnerID(
                Call(attackerSession, "GetWarsByOwnerID", new PyInt(attacker))));
            var rowset = Assert.IsType<PyObjectEx>(ownerResult);
            Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
            var ownerRows = rowset.List.Select(row => Assert.IsType<PyPackedRow>(row)).ToArray();
            Assert.Equal(2, ownerRows.Length);
            Assert.Equal(17, ownerRows[0].Values.Count);
            Assert.Equal(new PyInt(0), ownerRows[0].Values[9]);
            Assert.Equal(new PyInt(1), ownerRows[0].Values[11]);
            Assert.Equal(new PyInt(1), ownerRows[0].Values[12]);
            Assert.Equal(new PyInt(1), ownerRows[0].Values[15]);
            Assert.Equal(PyValue.None, ownerRows[0].Values[4]);
            Assert.Equal(new PyInt(1), ownerRows[1].Values[9]);
            Assert.Equal(new PyInt(0), ownerRows[1].Values[11]);
            Assert.Equal(new PyInt(0), ownerRows[1].Values[12]);
            Assert.Equal(new PyLong(Eve.Services.Py.FileTimeFromUnixMs(firstDeclared)), ownerRows[0].Values[3]);
            Assert.Equal(new PyLong(Eve.Services.Py.FileTimeFromUnixMs(firstStarted)), ownerRows[0].Values[7]);

            var alliesByOwner = Assert.IsType<PyDict>(await service.GetWarsByOwners(
                Call(attackerSession, "GetWarsByOwners",
                    new PyList([new PyInt(attacker), new PyInt(ally), new PyInt(ownerWithoutWars)]))));
            var attackerWars = Assert.IsType<PyDict>(alliesByOwner.Entries.Single(entry => entry.Key == new PyInt(attacker)).Value);
            Assert.Equal(2, attackerWars.Entries.Count);
            var allyWars = Assert.IsType<PyDict>(alliesByOwner.Entries.Single(entry => entry.Key == new PyInt(ally)).Value);
            Assert.Single(allyWars.Entries);
            Assert.Empty(Assert.IsType<PyDict>(alliesByOwner.Entries.Single(entry => entry.Key == new PyInt(ownerWithoutWars)).Value).Entries);
            var negativeOwnerWars = Assert.IsType<PyDict>(await service.GetWarsByOwners(
                Call(attackerSession, "GetWarsByOwners", new PyList([new PyInt(-1)]))));
            Assert.Empty(Assert.IsType<PyDict>(Assert.Single(negativeOwnerWars.Entries).Value).Entries);

            var defenderSession = new ClientSession();
            defenderSession.Set("corporationID", new PyInt(defender));
            var assistance = Assert.IsType<PyList>(await service.GetWarsRequiringAssistance(
                Call(defenderSession, "GetWarsRequiringAssistance")));
            var assistanceWar = Assert.Single(assistance.Items);
            Assert.Equal(new PyInt(1), Field(assistanceWar, "openForAllies"));
            Assert.Empty(Assert.IsType<PyList>(await service.GetWarsRequiringAssistance(
                Call(defenderSession, "GetWarsRequiringAssistance", new PyInt(-1)))).Items);

            long Id(PyValue value) => value switch
            {
                PyInt integer => integer.Value,
                PyLong integer => integer.Value,
                _ => throw new Xunit.Sdk.XunitException($"Unexpected ID value type: {value.GetType().Name}"),
            };
            var top50 = CachedPayload(await service.GetTop50(
                Call(attackerSession, "GetTop50", new PyInt(Id(ownerRows[1].Values[0]) + 1))));
            var topRows = Assert.IsType<PyObjectEx>(top50).List.Select(row => Assert.IsType<PyPackedRow>(row)).ToArray();
            Assert.Equal(2, topRows.Length);
            Assert.Equal(ownerRows[1].Values[0], topRows[0].Values[0]);
            var negativeTop50 = CachedPayload(await service.GetTop50(
                Call(attackerSession, "GetTop50", new PyInt(-1))));
            Assert.Empty(Assert.IsType<PyObjectEx>(negativeTop50).List);

            var publicInfo = await service.GetPublicWarInfo(
                Call(attackerSession, "GetPublicWarInfo", ownerRows[0].Values[0]));
            Assert.Equal(new PyStr("util.KeyVal"), Assert.IsType<PyObject>(publicInfo).Name);
            Assert.Equal(PyValue.None, await service.GetPublicWarInfo(
                Call(attackerSession, "GetPublicWarInfo", new PyInt(long.MaxValue))));

            var structureWars = Assert.IsType<PyList>(await service.GetWarsForStructure(
                Call(attackerSession, "GetWarsForStructure", new PyInt(structure))));
            Assert.Equal(2, structureWars.Items.Count);
            Assert.Equal(ownerRows[1].Values[0], Field(structureWars.Items[0], "warID"));

            var legacyRows = Assert.IsType<PyObjectEx>(CachedPayload(await service.GetWarsByOwnerID(
                Call(attackerSession, "GetWarsByOwnerID", new PyInt(legacyOwner)))));
            var legacyRow = Assert.IsType<PyPackedRow>(Assert.Single(legacyRows.List));
            Assert.Equal(new PyInt(legacyStructure), legacyRow.Values[14]);
            var legacyInfo = await service.GetPublicWarInfo(
                Call(attackerSession, "GetPublicWarInfo", legacyRow.Values[0]));
            Assert.Equal(PyValue.None, Field(legacyInfo, "warHQID"));
            Assert.Equal(PyValue.None, Field(legacyInfo, "warHQ"));
            Assert.Empty(Assert.IsType<PyList>(await service.GetWarsForStructure(
                Call(attackerSession, "GetWarsForStructure", new PyInt(legacyStructure)))).Items);
        }
        finally
        {
            foreach (var cleanupSchema in new[] { eventsSchema, schedulerSchema, relationSchema })
            {
                await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {cleanupSchema} CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public void Wars_info_manager_exposes_the_six_reference_methods()
    {
        var type = typeof(CorporationService).Assembly.GetType("Eve.Services.Corporations.WarsInfoMgrService");
        Assert.NotNull(type);
        var methods = type!.GetMethods()
            .Where(method => Attribute.IsDefined(method, typeof(MethodAttribute)))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>
        {
            "GetWarsByOwnerID", "GetWarsByOwners", "GetWarsRequiringAssistance",
            "GetTop50", "GetPublicWarInfo", "GetWarsForStructure",
        }, methods);
    }

    [PgFact]
    public async Task Vote_manager_replaces_ballots_and_uses_live_share_weight()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "vote_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);

            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            var founder = await rig.CharacterAsync("Vote Founder");
            var member = await rig.CharacterAsync("Vote Member");
            var corporation = await store.CreateAsync(founder,new("Vote Works","VOTE","","",0m),"vote-manager");
            var application = await store.ApplyAsync(member,corporation.Id,"");
            await store.RespondAsync(founder,application,6,"","");
            await store.RespondAsync(member,application,2,"","");

            var service = new VoteManagerService(store);
            var founderSession = new ClientSession { CharacterId = founder };
            founderSession.Set("corporationID",new PyInt(corporation.Id));
            founderSession.Set("corpid",new PyInt(corporation.Id));
            var memberSession = new ClientSession { CharacterId = member };
            memberSession.Set("corporationID",new PyInt(corporation.Id));
            memberSession.Set("corpid",new PyInt(corporation.Id));
            ValueTask<PyValue> Rpc(ClientSession session,string method,params PyValue[] args)
            {
                var call = Call(session,method,args);
                return method switch
                {
                    "GetVoteCasesByCorporation" => service.GetVoteCasesByCorporation(call),
                    "GetVoteCase" => service.GetVoteCase(call),
                    "GetVoteCaseOptions" => service.GetVoteCaseOptions(call),
                    "GetVotes" => service.GetVotes(call),
                    "CanViewVotes" => service.CanViewVotes(call),
                    "CanRunForCEO" => service.CanRunForCEO(call),
                    "InsertVoteCase" => service.InsertVoteCase(call),
                    "InsertVote" => service.InsertVote(call),
                    "GetCorpSanctionedActions" => service.GetCorpSanctionedActions(call),
                    "ActivateSanctionedAction" => service.ActivateSanctionedAction(call),
                    _ => throw new Xunit.Sdk.XunitException($"Unexpected vote RPC: {method}"),
                };
            }
            ValueTask<PyValue> FounderRpc(string method,params PyValue[] args) => Rpc(founderSession,method,args);
            ValueTask<PyValue> MemberRpc(string method,params PyValue[] args) => Rpc(memberSession,method,args);
            var registry = (IService)new CorpRegistryService(store,rig.Characters,() => rig.Now);
            ValueTask<PyValue> SharesRpc(ClientSession session,string method,params PyValue[] args) =>
                registry.CallAsync(Call(session,method,args));
            var initialCompanyShares = Assert.Single(Lines(await SharesRpc(founderSession,"GetSharesByShareholder",new PyInt(1))));
            Assert.Equal(new PyInt(1000),RowValues(initialCompanyShares)[2]);
            Assert.IsType<PyNone>(await SharesRpc(founderSession,"MoveCompanyShares",
                new PyInt(corporation.Id),new PyInt(founder),new PyInt(1000)));

            var caseId = Assert.IsType<PyInt>(await FounderRpc("InsertVoteCase",
                new PyWString("General vote"),new PyWString("Choose"),new PyInt(4),
                new PyList([new PyWString("For"),new PyWString("Against")]),new PyInt(1))).Value;
            Assert.IsType<PyNone>(await SharesRpc(founderSession,"MovePrivateShares",
                new PyInt(corporation.Id),new PyInt(member),new PyInt(400)));
            var memberShares = Assert.Single(Lines(await SharesRpc(memberSession,"GetSharesByShareholder",new PyInt(0))));
            Assert.Equal(new PyInt(member),RowValues(memberShares)[0]);
            Assert.Equal(new PyInt(400),RowValues(memberShares)[2]);
            var shareholderRows = Lines(await SharesRpc(founderSession,"GetShareholders",new PyInt(corporation.Id)));
            var shareholderBalances = shareholderRows.Select(RowValues).ToDictionary(
                row => Assert.IsType<PyInt>(row[0]).Value,
                row => Assert.IsType<PyInt>(row[2]).Value);
            Assert.Equal(1000,shareholderBalances.Values.Sum());
            Assert.Equal(600,shareholderBalances[founder]);
            Assert.Equal(400,shareholderBalances[member]);
            Assert.Equal(PyBool.False,await MemberRpc("CanRunForCEO"));
            await using (var skillConnection = await rig.Source.OpenConnectionAsync())
            await using (var skillTransaction = await skillConnection.BeginTransactionAsync())
            {
                await rig.Skills.GrantAsync(skillConnection,skillTransaction,member,
                    [new Eve.Skills.CharacterSkill(3363,1,250)]);
                await skillTransaction.CommitAsync();
            }
            Assert.Equal(PyBool.True,await MemberRpc("CanViewVotes",new PyInt(corporation.Id)));
            Assert.Equal(PyBool.True,await MemberRpc("CanRunForCEO"));
            Assert.Equal(PyBool.False,await FounderRpc("CanRunForCEO"));
            var memberCaseId = Assert.IsType<PyInt>(await MemberRpc("InsertVoteCase",
                new PyWString("Member vote"),new PyWString("Shareholder can create"),new PyInt(4),
                new PyList([new PyWString("For"),new PyWString("Against")]),new PyInt(1))).Value;
            Assert.True(memberCaseId > 0);
            var cases = Assert.IsType<PyDict>(await MemberRpc("GetVoteCasesByCorporation",new PyInt(corporation.Id)));
            Assert.Equal(2,cases.Entries.Count);
            var openByStatus = Assert.IsType<PyDict>(await MemberRpc("GetVoteCasesByCorporation",new PyInt(corporation.Id),new PyInt(2)));
            var openEntry = Assert.Single(openByStatus.Entries);
            Assert.Equal(new PyInt(2),openEntry.Key);
            Assert.Equal(2,Lines(openEntry.Value).Count);
            var closedByStatus = Assert.IsType<PyDict>(await MemberRpc("GetVoteCasesByCorporation",new PyInt(corporation.Id),new PyInt(1)));
            var closedEntry = Assert.Single(closedByStatus.Entries);
            Assert.Equal(new PyInt(1),closedEntry.Key);
            Assert.Empty(Lines(closedEntry.Value));
            Assert.Equal(new PyInt(caseId),RowField(await MemberRpc("GetVoteCase",new PyInt(corporation.Id),new PyInt(caseId)),"voteCaseID"));
            var generalOptions = Lines(await MemberRpc("GetVoteCaseOptions",new PyInt(corporation.Id),new PyInt(caseId)));
            Assert.Equal(2,generalOptions.Count);
            Assert.Equal(new PyInt(0),Assert.IsType<PyList>(generalOptions[0]).Items[1]);
            Assert.Equal(new PyInt(1),Assert.IsType<PyList>(generalOptions[1]).Items[1]);

            Assert.IsType<PyNone>(await MemberRpc("InsertVote",new PyInt(corporation.Id),new PyInt(caseId),new PyInt(0)));
            Assert.IsType<PyNone>(await SharesRpc(memberSession,"MovePrivateShares",
                new PyInt(corporation.Id),new PyInt(founder),new PyInt(100)));
            Assert.IsType<PyNone>(await MemberRpc("InsertVote",new PyInt(corporation.Id),new PyInt(caseId),new PyInt(1)));
            Assert.Equal((1L,300L),await ReadBallot(member));
            Assert.IsType<PyNone>(await FounderRpc("InsertVote",new PyInt(corporation.Id),new PyInt(caseId),new PyInt(0)));
            Assert.Equal((0L,700L),await ReadBallot(founder));
            var personalBallot = Assert.IsType<PyList>(Assert.Single(Lines(await MemberRpc("GetVotes",new PyInt(caseId)))));
            Assert.Equal(new PyInt(1),personalBallot.Items[0]);

            async Task<(long OptionId,long VotingPower)> ReadBallot(long shareholderId)
            {
                await using var query = rig.Source.CreateCommand($"SELECT option_id,voting_power FROM {schema}.vote_ballots WHERE vote_case_id=$1 AND shareholder_id=$2");
                query.Parameters.AddWithValue(caseId);
                query.Parameters.AddWithValue(shareholderId);
                await using var reader = await query.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                return (reader.GetInt64(0),reader.GetInt64(1));
            }

            var itemId = 987654321L;
            const long typeId = 15001;
            var unlockId = Assert.IsType<PyInt>(await FounderRpc("InsertVoteCase",
                new PyWString("Unlock blueprint"),new PyWString("Unlock"),new PyInt(6),
                Py.Tuple(
                    Py.Tuple(new PyWString("UI/Corporations/CorporationWindow/Politics/UnlockItem"),
                        Py.Dict(("item",new PyInt(typeId)))),
                    Py.Tuple(new PyWString("UI/Corporations/CorporationWindow/Politics/DontUnlockItem"),
                        Py.Dict(("item",new PyInt(typeId)))),
                    new PyInt(itemId),new PyInt(typeId),new PyInt(60000001)),
                new PyInt(1))).Value;
            var optionRows = Lines(await MemberRpc("GetVoteCaseOptions",new PyInt(corporation.Id),new PyInt(unlockId)));
            Assert.Equal(2,optionRows.Count);
            Assert.Equal(new PyInt(itemId),Assert.IsType<PyList>(optionRows[0]).Items[3]);
            Assert.Equal(new PyInt(0),Assert.IsType<PyList>(optionRows[1]).Items[3]);
            Assert.IsType<PyNone>(await MemberRpc("InsertVote",new PyInt(corporation.Id),new PyInt(unlockId),new PyInt(0)));

            rig.Now += 86_400_001L;
            var pending = Assert.IsType<PyDict>(await MemberRpc("GetCorpSanctionedActions"));
            var action = Assert.Single(pending.Entries,entry => entry.Key.Equals(new PyInt(unlockId))).Value;
            Assert.Equal(new PyInt(6),Field(action,"voteType"));
            Assert.Equal(new PyInt(itemId),Field(action,"parameter"));
            Assert.Equal(new PyInt(0),Field(action,"inEffect"));
            var unapplied = Assert.Single(
                Assert.IsType<PyDict>(await FounderRpc("GetCorpSanctionedActions")).Entries,
                entry => entry.Key.Equals(new PyInt(unlockId))).Value;
            Assert.Equal(new PyInt(0),Field(unapplied,"inEffect"));

            await using (var seedClosed = rig.Source.CreateCommand($"""
                INSERT INTO {schema}.vote_cases
                    (corporation_id,creator_id,vote_case_text,description,vote_type,options_payload,option_count,
                     start_filetime,end_filetime,sanction_in_effect,sanction_expires_filetime)
                SELECT $1,$2,'Closed test case','',4,$3,2,0,1,false,0 FROM generate_series(1,21)
                """))
            {
                seedClosed.Parameters.AddWithValue(corporation.Id);
                seedClosed.Parameters.AddWithValue(founder);
                seedClosed.Parameters.AddWithValue(new byte[] { 0 });
                await seedClosed.ExecuteNonQueryAsync();
            }
            var cappedClosed = Assert.IsType<PyDict>(await MemberRpc(
                "GetVoteCasesByCorporation",new PyInt(corporation.Id),new PyInt(1)));
            Assert.Equal(20,Lines(Assert.Single(cappedClosed.Entries).Value).Count);
        }
        finally
        {
            await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Vote_manager_exposes_the_fixed_client_rpc_names()
    {
        var type = typeof(CorporationService).Assembly.GetType("Eve.Services.Corporations.VoteManagerService");
        Assert.NotNull(type);
        var expectedMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetVoteCasesByCorporation","GetVoteCase","GetVoteCaseOptions","GetVotes","CanViewVotes",
            "CanRunForCEO","InsertVoteCase","InsertVote","GetCorpSanctionedActions","ActivateSanctionedAction"
        };
        Assert.Equal(expectedMethods,type!.GetMethods().Where(method => Attribute.IsDefined(method,typeof(MethodAttribute)))
            .Select(method => method.Name).ToHashSet(StringComparer.Ordinal));

        var service = (IService)new VoteManagerService(null!);
        Assert.All(new[]
        {
            "GetVoteCasesByCorporation","GetVoteCaseOptions","GetVotes",
            "CanViewVotes","CanRunForCEO","InsertVoteCase",
        },method => Assert.True(service.HasMethod(method),method));
    }

    [PgFact]
    public async Task Corporation_share_rpcs_move_company_and_private_shares_without_changing_supply()
    {
        await using var rig = await Rig.CreateAsync();
        var ceo = await rig.CharacterAsync("Share CEO");
        var recipient = await rig.CharacterAsync("Share Recipient");
        var otherCeo = await rig.CharacterAsync("Other Share CEO");
        var nonManager = await rig.CharacterAsync("Share Non Manager");
        var corporation = await rig.Store.CreateAsync(ceo,new("Share Works","SHARE","","",0m),"share-rpc-" + Guid.NewGuid().ToString("N"));
        var otherCorporation = await rig.Store.CreateAsync(otherCeo,new("Other Shares","OSHARE","","",0m),"other-share-rpc-" + Guid.NewGuid().ToString("N"));
        var memberApplication = await rig.Store.ApplyAsync(nonManager,corporation.Id,"");
        await rig.Store.RespondAsync(ceo,memberApplication,6,"","");
        await rig.Store.RespondAsync(nonManager,memberApplication,2,"","");
        var registry = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        var registryMethods = typeof(CorpRegistryService).GetMethods()
            .Where(method => Attribute.IsDefined(method,typeof(MethodAttribute)))
            .Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("GetSharesByShareholder",registryMethods);
        Assert.Contains("GetShareholders",registryMethods);
        Assert.Contains("MoveCompanyShares",registryMethods);
        Assert.Contains("MovePrivateShares",registryMethods);
        var session = new ClientSession { CharacterId=ceo };
        session.Set("corporationID",new PyInt(corporation.Id));
        session.Set("corpid",new PyInt(corporation.Id));
        ValueTask<PyValue> Rpc(string method,params PyValue[] args) => registry.CallAsync(Call(session,method,args));
        var otherSession = new ClientSession { CharacterId=otherCeo };
        otherSession.Set("corporationID",new PyInt(otherCorporation.Id));
        otherSession.Set("corpid",new PyInt(otherCorporation.Id));
        ValueTask<PyValue> OtherCorpRpc(string method,params PyValue[] args) => registry.CallAsync(Call(otherSession,method,args));

        var companyShares = Assert.Single(Lines(await Rpc("GetSharesByShareholder",new PyInt(1))));
        Assert.Equal(new PyInt(corporation.Id),RowValues(companyShares)[0]);
        Assert.Equal(new PyInt(corporation.Id),RowValues(companyShares)[1]);
        Assert.Equal(new PyInt(1000),RowValues(companyShares)[2]);
        Assert.Single(Lines(await Rpc("GetShareholders",new PyInt(corporation.Id))));

        Assert.IsType<PyNone>(await Rpc("MoveCompanyShares",new PyInt(corporation.Id),new PyInt(ceo),new PyInt(300)));
        Assert.IsType<PyNone>(await Rpc("MovePrivateShares",new PyInt(corporation.Id),new PyInt(recipient),new PyInt(100)));
        Assert.IsType<PyNone>(await OtherCorpRpc("MoveCompanyShares",new PyInt(otherCorporation.Id),new PyInt(ceo),new PyInt(150)));
        var personalShares = Lines(await Rpc("GetSharesByShareholder",new PyInt(0)));
        var personalByCorporation = personalShares.Select(RowValues).ToDictionary(
            row => Assert.IsType<PyInt>(row[1]).Value,
            row => Assert.IsType<PyInt>(row[2]).Value);
        Assert.Equal(2,personalByCorporation.Count);
        Assert.Equal(200,personalByCorporation[corporation.Id]);
        Assert.Equal(150,personalByCorporation[otherCorporation.Id]);
        var remainingCompanyShares = Assert.Single(Lines(await Rpc("GetSharesByShareholder",new PyInt(1))));
        Assert.Equal(new PyInt(corporation.Id),RowValues(remainingCompanyShares)[1]);
        Assert.Equal(new PyInt(700),RowValues(remainingCompanyShares)[2]);
        var nonManagerSession = new ClientSession { CharacterId=nonManager };
        nonManagerSession.Set("corporationID",new PyInt(corporation.Id));
        nonManagerSession.Set("corpid",new PyInt(corporation.Id));
        await Assert.ThrowsAnyAsync<Exception>(async () => await registry.CallAsync(
            Call(nonManagerSession,"GetSharesByShareholder",new PyInt(1))));

        var rows = Lines(await Rpc("GetShareholders",new PyInt(corporation.Id)));
        var balances = rows.Select(RowValues).ToDictionary(
            row => Assert.IsType<PyInt>(row[0]).Value,
            row => Assert.IsType<PyInt>(row[2]).Value);
        Assert.Equal(1000,balances.Values.Sum());
        Assert.Equal(700,balances[corporation.Id]);
        Assert.Equal(200,balances[ceo]);
        Assert.Equal(100,balances[recipient]);
    }

    [PgFact]
    public async Task Private_share_rpc_retry_is_idempotent_and_changed_arguments_conflict()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "share_retry_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);
            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            var owner = await rig.CharacterAsync("Retry Share Owner");
            var recipient = await rig.CharacterAsync("Retry Share Recipient");
            var corporation = await store.CreateAsync(owner,new("Retry Shares","RTRY","","",0m),
                "share-retry-" + Guid.NewGuid().ToString("N"));
            await store.MoveCompanySharesAsync(corporation.Id,owner,owner,1000,"share-retry-seed");

            var registry = (IService)new CorpRegistryService(store,rig.Characters,() => rig.Now);
            var session = new ClientSession { CharacterId=owner };
            session.Set("corporationID",new PyInt(corporation.Id));
            session.Set("corpid",new PyInt(corporation.Id));
            ServiceCall Retry(long amount) => new()
            {
                Session=session,
                Method="MovePrivateShares",
                Args=[new PyInt(corporation.Id),new PyInt(recipient),new PyInt(amount)],
                CallId=new PyInt(812345)
            };
            var ownerBefore = Assert.Single(await store.SharesByShareholderAsync(owner,corporation.Id)).Shares;

            Assert.IsType<PyNone>(await registry.CallAsync(Retry(25)));
            Assert.IsType<PyNone>(await registry.CallAsync(Retry(25)));

            var ownerAfter = Assert.Single(await store.SharesByShareholderAsync(owner,corporation.Id)).Shares;
            var recipientAfter = Assert.Single(await store.SharesByShareholderAsync(recipient,corporation.Id)).Shares;
            Assert.Equal(ownerBefore - 25,ownerAfter);
            Assert.Equal(25,recipientAfter);

            var conflict = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(async () =>
                await registry.CallAsync(Retry(26)));
            Assert.Equal("CORPORATION_OPERATION_CONFLICT",conflict.Code);
            Assert.Equal(ownerAfter,Assert.Single(await store.SharesByShareholderAsync(owner,corporation.Id)).Shares);
            Assert.Equal(recipientAfter,Assert.Single(await store.SharesByShareholderAsync(recipient,corporation.Id)).Shares);
        }
        finally
        {
            await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Execute_actions_give_cash_and_move_shares_through_the_share_rpc_with_short_wallet_rollback()
    {
        await using var rig = await Rig.CreateAsync();
        var ceo = await rig.CharacterAsync("Grant CEO");
        var first = await rig.CharacterAsync("Grant First");
        var second = await rig.CharacterAsync("Grant Second");
        var corporation = await rig.Store.CreateAsync(ceo,new("Grant Works","GRANT","","",0m),"grant-" + Guid.NewGuid().ToString("N"));
        foreach (var member in new[] { first,second })
        {
            var application = await rig.Store.ApplyAsync(member,corporation.Id,"");
            await rig.Store.RespondAsync(ceo,application,6,"","");
            await rig.Store.RespondAsync(member,application,2,"","");
        }

        var registry = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        var session = new ClientSession { CharacterId=ceo };
        session.Set("corporationID",new PyInt(corporation.Id));
        session.Set("corpid",new PyInt(corporation.Id));
        session.Set("corpAccountKey",new PyInt(1000));
        ValueTask<PyValue> Rpc(string method,params PyValue[] args) => registry.CallAsync(Call(session,method,args));
        var fromCorpWallet = new Eve.Wallet.WalletAccount(corporation.Id,1000);
        var firstWallet = new Eve.Wallet.WalletAccount(first);
        var secondWallet = new Eve.Wallet.WalletAccount(second);
        async Task FundAsync(decimal amount,string operationKey)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Transactions.TransferAsync(connection,transaction,new Eve.Wallet.WalletAccount(ceo),fromCorpWallet,
                amount,10,"Share grant test",rig.Now,operationKey,"test");
            await transaction.CommitAsync();
        }
        async Task<Dictionary<long,long>> ShareBalancesAsync()
        {
            var rows = Lines(await Rpc("GetShareholders",new PyInt(corporation.Id)));
            return rows.Select(RowValues).ToDictionary(
                row => Assert.IsType<PyInt>(row[0]).Value,
                row => Assert.IsType<PyInt>(row[2]).Value);
        }

        var cashActions = new PyList([
            Py.Tuple(new PyInt(4),new PyInt(6),new PyInt(10))
        ]);
        var mixedActions = new PyList([
            Py.Tuple(new PyInt(4),new PyInt(6),new PyInt(10)),
            Py.Tuple(new PyInt(4),new PyInt(7),new PyInt(100))
        ]);
        await FundAsync(15m,"share-grant-short-" + Guid.NewGuid().ToString("N"));
        var beforeCorp = await rig.Transactions.AccountBalanceAsync(fromCorpWallet);
        var beforeFirst = await rig.Transactions.AccountBalanceAsync(firstWallet);
        var beforeSecond = await rig.Transactions.AccountBalanceAsync(secondWallet);
        var targets = new PyList([new PyInt(first),new PyInt(second)]);

        var emptyTargetError = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(async () =>
            await Rpc("ExecuteActions",new PyList([]),cashActions));
        Assert.Equal("CORPORATION_INVALID_MEMBER_GRANT",emptyTargetError.Code);
        Assert.Equal(beforeCorp,await rig.Transactions.AccountBalanceAsync(fromCorpWallet));
        Assert.Equal(beforeFirst,await rig.Transactions.AccountBalanceAsync(firstWallet));
        Assert.Equal(beforeSecond,await rig.Transactions.AccountBalanceAsync(secondWallet));

        var mixedShareError = await Assert.ThrowsAsync<Eve.Corporations.CorporationError>(async () =>
            await Rpc("ExecuteActions",targets,mixedActions));
        Assert.Equal("CORPORATION_ACCESS_DENIED",mixedShareError.Code);
        Assert.Equal(beforeCorp,await rig.Transactions.AccountBalanceAsync(fromCorpWallet));
        Assert.Equal(beforeFirst,await rig.Transactions.AccountBalanceAsync(firstWallet));
        Assert.Equal(beforeSecond,await rig.Transactions.AccountBalanceAsync(secondWallet));
        var beforeRejectedShares = await ShareBalancesAsync();
        Assert.Equal(1000,beforeRejectedShares.Values.Sum());
        Assert.Equal(1000,beforeRejectedShares[corporation.Id]);
        Assert.DoesNotContain(first,beforeRejectedShares.Keys);
        Assert.DoesNotContain(second,beforeRejectedShares.Keys);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Rpc("ExecuteActions",targets,cashActions));
        Assert.Equal(beforeCorp,await rig.Transactions.AccountBalanceAsync(fromCorpWallet));
        Assert.Equal(beforeFirst,await rig.Transactions.AccountBalanceAsync(firstWallet));
        Assert.Equal(beforeSecond,await rig.Transactions.AccountBalanceAsync(secondWallet));
        var afterRollback = await ShareBalancesAsync();
        Assert.Equal(1000,afterRollback.Values.Sum());
        Assert.Equal(1000,afterRollback[corporation.Id]);
        Assert.DoesNotContain(first,afterRollback.Keys);
        Assert.DoesNotContain(second,afterRollback.Keys);

        await FundAsync(100m,"share-grant-fund-" + Guid.NewGuid().ToString("N"));
        var afterFundingCorp = await rig.Transactions.AccountBalanceAsync(fromCorpWallet);
        var afterFundingFirst = await rig.Transactions.AccountBalanceAsync(firstWallet);
        var afterFundingSecond = await rig.Transactions.AccountBalanceAsync(secondWallet);
        Assert.IsType<PyNone>(await Rpc("ExecuteActions",targets,cashActions));
        Assert.Equal(beforeCorp + 80m,await rig.Transactions.AccountBalanceAsync(fromCorpWallet));
        Assert.Equal(beforeFirst + 10m,await rig.Transactions.AccountBalanceAsync(firstWallet));
        Assert.Equal(beforeSecond + 10m,await rig.Transactions.AccountBalanceAsync(secondWallet));
        Assert.Equal(afterFundingCorp - 20m,await rig.Transactions.AccountBalanceAsync(fromCorpWallet));
        Assert.Equal(afterFundingFirst + 10m,await rig.Transactions.AccountBalanceAsync(firstWallet));
        Assert.Equal(afterFundingSecond + 10m,await rig.Transactions.AccountBalanceAsync(secondWallet));

        Assert.IsType<PyNone>(await Rpc("MoveCompanyShares",
            new PyInt(corporation.Id),new PyInt(first),new PyInt(100)));
        Assert.IsType<PyNone>(await Rpc("MoveCompanyShares",
            new PyInt(corporation.Id),new PyInt(second),new PyInt(100)));
        var afterGrant = await ShareBalancesAsync();
        Assert.Equal(1000,afterGrant.Values.Sum());
        Assert.Equal(800,afterGrant[corporation.Id]);
        Assert.Equal(100,afterGrant[first]);
        Assert.Equal(100,afterGrant[second]);
    }

    [PgFact]
    public async Task Corporation_votes_apply_official_sanctions_and_restore_roles_after_failed_votes()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "vote_effects_" + Guid.NewGuid().ToString("N")[..12];
        var blueprintEffects = new List<(long CorporationId,long ItemId,long TypeId,long LocationId,bool Locked)>();
        Task RecordBlueprintLock(Npgsql.NpgsqlConnection connection,Npgsql.NpgsqlTransaction transaction,
            long corporationId,long itemId,long typeId,long locationId,bool locked,CancellationToken cancellationToken)
        {
            Assert.NotNull(connection);
            Assert.NotNull(transaction);
            cancellationToken.ThrowIfCancellationRequested();
            blueprintEffects.Add((corporationId,itemId,typeId,locationId,locked));
            return Task.CompletedTask;
        }

        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);
            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,
                () => rig.Now,schema,RecordBlueprintLock);
            await store.SeedNpcAsync(new Eve.Sde.SdeReader(rig.Source));
            var ceo = await rig.CharacterAsync("Vote CEO");
            var candidate = await rig.CharacterAsync("Vote Candidate");
            var target = await rig.CharacterAsync("Vote Target");
            var targetCharacter = (await rig.Characters.GetAsync(target))!;
            var expectedNpcCorporationId = Eve.Characters.CreationCatalog
                .ResolveSchool(targetCharacter.SchoolId,targetCharacter.RaceId)!.CorporationId;
            var corporation = await store.CreateAsync(ceo,new("Vote Effects","VEFX","","",0m),"vote-effects-" + Guid.NewGuid().ToString("N"));
            foreach (var member in new[] { candidate,target })
            {
                var application = await store.ApplyAsync(member,corporation.Id,"");
                await store.RespondAsync(ceo,application,6,"","");
                await store.RespondAsync(member,application,2,"","");
            }
            var targetRecord = (await store.MemberAsync(target))!;
            await store.UpdateMemberAsync(ceo,targetRecord with { Masks=new RoleMasks(CorporationRoles.PersonnelManager) });
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await rig.Skills.GrantAsync(connection,transaction,candidate,[new Eve.Skills.CharacterSkill(3363,1,250)]);
                await transaction.CommitAsync();
            }

            var registry = (IService)new CorpRegistryService(store,rig.Characters,() => rig.Now);
            var votes = (IService)new VoteManagerService(store);
            ClientSession Session(long characterId)
            {
                var session = new ClientSession { CharacterId=characterId };
                session.Set("corporationID",new PyInt(corporation.Id));
                session.Set("corpid",new PyInt(corporation.Id));
                return session;
            }
            var ceoSession = Session(ceo);
            var candidateSession = Session(candidate);
            var targetSession = Session(target);
            ValueTask<PyValue> ShareRpc(ClientSession session,string method,params PyValue[] args) =>
                registry.CallAsync(Call(session,method,args));
            ValueTask<PyValue> VoteRpc(ClientSession session,string method,params PyValue[] args) =>
                votes.CallAsync(Call(session,method,args));
            async Task<long> CreateVote(ClientSession session,string title,int voteType,PyValue options)
            {
                var result = await VoteRpc(session,"InsertVoteCase",new PyWString(title),new PyWString(title),
                    new PyInt(voteType),options,new PyInt(1));
                return Assert.IsType<PyInt>(result).Value;
            }
            async Task CastVote(ClientSession session,long voteCaseId,long optionId)
            {
                Assert.IsType<PyNone>(await VoteRpc(session,"InsertVote",new PyInt(corporation.Id),
                    new PyInt(voteCaseId),new PyInt(optionId)));
            }
            async Task CloseVote(ClientSession session)
            {
                rig.Now += 86_400_001L;
                _ = await VoteRpc(session,"GetVoteCasesByCorporation",new PyInt(corporation.Id));
            }
            async Task<Dictionary<long,long>> SharesAsync(ClientSession session)
            {
                var rows = Lines(await ShareRpc(session,"GetShareholders",new PyInt(corporation.Id)));
                return rows.Select(RowValues).ToDictionary(
                    row => Assert.IsType<PyInt>(row[0]).Value,
                    row => Assert.IsType<PyInt>(row[2]).Value);
            }

            var createShares = await CreateVote(ceoSession,"Create shares",2,Py.Tuple(
                Py.Tuple(new PyWString("UI/Corporations/CorporationWindow/Politics/CreateShares"),
                    Py.Dict(("shares",new PyInt(200)))),
                Py.Tuple(new PyWString("UI/Corporations/CorporationWindow/Politics/DoNotCreateShares")),
                new PyInt(200),PyNone.Instance,PyNone.Instance));
            await CastVote(ceoSession,createShares,0);
            await CloseVote(ceoSession);
            var pendingShares = Assert.IsType<PyDict>(await VoteRpc(ceoSession,"GetCorpSanctionedActions"));
            Assert.Contains(pendingShares.Entries,entry => entry.Key.Equals(new PyInt(createShares)));
            var pendingCreateShares = Assert.Single(pendingShares.Entries,
                entry => entry.Key.Equals(new PyInt(createShares))).Value;
            Assert.Equal(new PyInt(2),Field(pendingCreateShares,"voteType"));
            Assert.IsType<PyNone>(await VoteRpc(ceoSession,"ActivateSanctionedAction",new PyInt(createShares)));
            var afterIssue = await SharesAsync(ceoSession);
            Assert.Equal(1200,afterIssue.Values.Sum());
            Assert.Equal(1200,afterIssue[corporation.Id]);

            Assert.IsType<PyNone>(await ShareRpc(ceoSession,"MoveCompanyShares",new PyInt(corporation.Id),new PyInt(candidate),new PyInt(100)));
            Assert.IsType<PyNone>(await ShareRpc(ceoSession,"MoveCompanyShares",new PyInt(corporation.Id),new PyInt(target),new PyInt(700)));
            Assert.IsType<PyNone>(await ShareRpc(ceoSession,"MoveCompanyShares",new PyInt(corporation.Id),new PyInt(ceo),new PyInt(100)));
            Assert.Equal(100L,(await store.ShareholdersAsync(corporation.Id)).Single(share => share.ShareholderId==ceo).Shares);
            Assert.Equal(PyBool.True,await VoteRpc(candidateSession,"CanRunForCEO"));
            var ceoRolesBefore = (await store.MemberAsync(ceo))!.Masks;
            async Task AssertCeoVoteSuspendsIncumbent()
            {
                var duringVote = (await store.GetAsync(corporation.Id))!;
                Assert.Equal(ceo,duringVote.CeoId);
                Assert.True(duringVote.CeoRolesSuspended);
                var incumbent = (await store.MemberAsync(ceo))!;
                Assert.Equal(0L,incumbent.Masks.Roles);
                Assert.Equal(0L,incumbent.Masks.GrantableRoles);
                Assert.Equal(0L,incumbent.Masks.AtHq);
                Assert.Equal(0L,incumbent.Masks.GrantableHq);
                Assert.Equal(0L,incumbent.Masks.AtBase);
                Assert.Equal(0L,incumbent.Masks.GrantableBase);
                Assert.Equal(0L,incumbent.Masks.AtOther);
                Assert.Equal(0L,incumbent.Masks.GrantableOther);
                Assert.Equal(0,incumbent.TitleMask);
                await Assert.ThrowsAnyAsync<Exception>(async () => await ShareRpc(ceoSession,"UpdateCorporation",
                    new PyWString("Suspended CEO must not edit"),new PyWString(""),Py.Number(0.05),new PyInt(1),Py.Number(0)));
            }
            PyValue CEOOptions() => Py.Tuple(
                Py.Tuple(new PyWString("UI/Corporations/CorporationWindow/Politics/SomeoneForCEO"),
                    Py.Dict(("char",new PyInt(candidate)))),
                Py.Tuple(new PyWString("UI/Corporations/CorporationWindow/Politics/DontChangeCEO")),
                new PyInt(candidate),PyNone.Instance,PyNone.Instance);
            var failedCeoVote = await CreateVote(candidateSession,"Failed CEO vote",0,CEOOptions());
            await AssertCeoVoteSuspendsIncumbent();
            Assert.Equal(0,(await store.MemberAsync(ceo))!.Masks.Roles);
            await CastVote(candidateSession,failedCeoVote,0);
            await CastVote(ceoSession,failedCeoVote,1);
            await CloseVote(candidateSession);
            var afterFailedCeoVote = (await store.GetAsync(corporation.Id))!;
            Assert.Equal(ceo,afterFailedCeoVote.CeoId);
            Assert.False(afterFailedCeoVote.CeoRolesSuspended);
            Assert.Equal(ceoRolesBefore,(await store.MemberAsync(ceo))!.Masks);
            Assert.IsType<PyNone>(await ShareRpc(ceoSession,"UpdateCorporation",
                new PyWString("CEO restored after failed vote"),new PyWString(""),Py.Number(0.05),new PyInt(1),Py.Number(0)));

            var passedCeoVote = await CreateVote(candidateSession,"Successful CEO vote",0,CEOOptions());
            await AssertCeoVoteSuspendsIncumbent();
            await CastVote(candidateSession,passedCeoVote,0);
            await CloseVote(candidateSession);
            var afterPassedCeoVote = (await store.GetAsync(corporation.Id))!;
            Assert.Equal(candidate,afterPassedCeoVote.CeoId);
            Assert.False(afterPassedCeoVote.CeoRolesSuspended);
            await Assert.ThrowsAnyAsync<Exception>(async () => await ShareRpc(ceoSession,"UpdateCorporation",
                new PyWString("Former CEO must not retain authority"),new PyWString(""),Py.Number(0.05),new PyInt(1),Py.Number(0)));
            Assert.IsType<PyNone>(await ShareRpc(candidateSession,"UpdateCorporation",
                new PyWString("New CEO has authority"),new PyWString(""),Py.Number(0.05),new PyInt(1),Py.Number(0)));

            const long blueprintId = 987654321;
            const long blueprintTypeId = 15001;
            const long officeId = 60000001;
            PyValue BlueprintOptions(bool unlock) => Py.Tuple(
                Py.Tuple(new PyWString(unlock
                    ? "UI/Corporations/CorporationWindow/Politics/UnlockItem"
                    : "UI/Corporations/CorporationWindow/Politics/LockdownItem"),
                    Py.Dict(("item",new PyInt(blueprintTypeId)))),
                Py.Tuple(new PyWString(unlock
                    ? "UI/Corporations/CorporationWindow/Politics/DontUnlockItem"
                    : "UI/Corporations/CorporationWindow/Politics/DontLockdownItem"),
                    Py.Dict(("item",new PyInt(blueprintTypeId)))),
                new PyInt(blueprintId),new PyInt(blueprintTypeId),new PyInt(officeId));
            var lockVote = await CreateVote(candidateSession,"Lock blueprint",5,BlueprintOptions(false));
            Assert.Equal((corporation.Id,blueprintId,blueprintTypeId,officeId,true),Assert.Single(blueprintEffects));
            await CastVote(candidateSession,lockVote,0);
            await CloseVote(candidateSession);
            Assert.Equal((corporation.Id,blueprintId,blueprintTypeId,officeId,true),Assert.Single(blueprintEffects));

            var unlockVote = await CreateVote(candidateSession,"Unlock blueprint",6,BlueprintOptions(true));
            await CastVote(candidateSession,unlockVote,0);
            await CloseVote(candidateSession);
            var pendingUnlock = Assert.IsType<PyDict>(await VoteRpc(candidateSession,"GetCorpSanctionedActions"));
            Assert.Contains(pendingUnlock.Entries,entry => entry.Key.Equals(new PyInt(unlockVote)));
            Assert.IsType<PyNone>(await VoteRpc(candidateSession,"ActivateSanctionedAction",new PyInt(unlockVote)));
            Assert.Equal(new[] { (corporation.Id,blueprintId,blueprintTypeId,officeId,true),(corporation.Id,blueprintId,blueprintTypeId,officeId,false) },blueprintEffects);

            var expelOptions = Py.Tuple(
                Py.Tuple(new PyWString("UI/Corporations/CorporationWindow/Politics/ExpelFromCorporation"),
                    Py.Dict(("char",new PyInt(target)))),
                Py.Tuple(new PyWString("UI/Corporations/CorporationWindow/Politics/DontExpelFromCorporation"),
                    Py.Dict(("memberName",new PyWString("Vote Target")))),
                new PyInt(target),PyNone.Instance,PyNone.Instance);
            var targetRoles = (await store.MemberAsync(target))!.Masks;
            var failedExpel = await CreateVote(candidateSession,"Failed expel",3,expelOptions);
            Assert.Equal(0,(await store.MemberAsync(target))!.Masks.Roles);
            await CastVote(candidateSession,failedExpel,0);
            await CastVote(targetSession,failedExpel,1);
            await CloseVote(candidateSession);
            Assert.Equal(targetRoles,(await store.MemberAsync(target))!.Masks);

            var passedExpel = await CreateVote(candidateSession,"Passed expel",3,expelOptions);
            await CastVote(candidateSession,passedExpel,0);
            await CloseVote(candidateSession);
            Assert.IsType<PyNone>(await VoteRpc(candidateSession,"ActivateSanctionedAction",new PyInt(passedExpel)));
            var expelled = await store.MemberAsync(target);
            Assert.NotNull(expelled);
            Assert.Equal(expectedNpcCorporationId,expelled.CorporationId);
            Assert.True((await store.GetAsync(expectedNpcCorporationId))!.IsNpc);
            Assert.Equal(700,(await SharesAsync(candidateSession))[target]);
        }
        finally
        {
            await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Outside_corporation_shareholders_can_read_and_vote_using_the_case_corporation_ledger()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "external_vote_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);
            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            var founder = await rig.CharacterAsync("Issuer CEO");
            var outsideShareholder = await rig.CharacterAsync("Outside Shareholder");
            var issuer = await store.CreateAsync(founder,new("Issuer Corp","ISSUE","","",0m),"issuer-" + Guid.NewGuid().ToString("N"));
            var shareholderCorp = await store.CreateAsync(outsideShareholder,new("Shareholder Corp","SHOLD","","",0m),"holder-" + Guid.NewGuid().ToString("N"));

            var registry = (IService)new CorpRegistryService(store,rig.Characters,() => rig.Now);
            var votes = (IService)new VoteManagerService(store);
            var founderSession = new ClientSession { CharacterId=founder };
            founderSession.Set("corporationID",new PyInt(issuer.Id));
            founderSession.Set("corpid",new PyInt(issuer.Id));
            var founderVotes = votes.CallAsync(Call(founderSession,"InsertVoteCase",new PyWString("External holder vote"),
                new PyWString("All shareholders may vote"),new PyInt(4),
                new PyList([new PyWString("For"),new PyWString("Against")]),new PyInt(1)));
            var voteCaseId = Assert.IsType<PyInt>(await founderVotes).Value;
            Assert.IsType<PyNone>(await registry.CallAsync(Call(founderSession,"MoveCompanyShares",
                new PyInt(issuer.Id),new PyInt(outsideShareholder),new PyInt(1000))));

            var outsideSession = new ClientSession { CharacterId=outsideShareholder };
            outsideSession.Set("corporationID",new PyInt(shareholderCorp.Id));
            outsideSession.Set("corpid",new PyInt(shareholderCorp.Id));
            ValueTask<PyValue> VoteRpc(string method,params PyValue[] args) => votes.CallAsync(Call(outsideSession,method,args));
            var nonShareholder = await rig.CharacterAsync("Non Shareholder");
            var nonShareholderSession = new ClientSession { CharacterId=nonShareholder };
            nonShareholderSession.Set("corporationID",new PyInt(shareholderCorp.Id));
            nonShareholderSession.Set("corpid",new PyInt(shareholderCorp.Id));
            Assert.Equal(PyBool.False,await votes.CallAsync(Call(nonShareholderSession,"CanViewVotes",new PyInt(issuer.Id))));
            Assert.Equal(PyBool.True,await VoteRpc("CanViewVotes",new PyInt(issuer.Id)));
            var openCases = Assert.IsType<PyDict>(await VoteRpc("GetVoteCasesByCorporation",new PyInt(issuer.Id),new PyInt(2)));
            Assert.NotEmpty(Lines(Assert.Single(openCases.Entries).Value));
            Assert.Equal(new PyInt(voteCaseId),RowField(await VoteRpc("GetVoteCase",new PyInt(issuer.Id),new PyInt(voteCaseId)),"voteCaseID"));
            Assert.Equal(2,Lines(await VoteRpc("GetVoteCaseOptions",new PyInt(issuer.Id),new PyInt(voteCaseId))).Count);

            Assert.IsType<PyNone>(await VoteRpc("InsertVote",new PyInt(issuer.Id),new PyInt(voteCaseId),new PyInt(0)));
            var ownVotes = Lines(await VoteRpc("GetVotes",new PyInt(voteCaseId)));
            Assert.Single(ownVotes);
            await using var query = rig.Source.CreateCommand($"SELECT shareholder_id,voting_power FROM {schema}.vote_ballots WHERE vote_case_id=$1");
            query.Parameters.AddWithValue(voteCaseId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(outsideShareholder,reader.GetInt64(0));
            Assert.Equal(1000,reader.GetInt64(1));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Corporation_store_persists_welcome_mail_per_corporation()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "corp_welcome_mail_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);
            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            var founder = await rig.CharacterAsync("Welcome Mail Founder");
            var otherFounder = await rig.CharacterAsync("Other Welcome Mail Founder");
            var corporation = await store.CreateAsync(founder,new("Welcome Mail","WMAIL","","",0m),"welcome-" + Guid.NewGuid().ToString("N"));
            var otherCorporation = await store.CreateAsync(otherFounder,new("Other Welcome Mail","OWML","","",0m),"welcome-other-" + Guid.NewGuid().ToString("N"));
            const string welcomeMail = "Welcome, capsuleer! <b>Fly safe.</b>";

            await store.SetCorpWelcomeMailAsync(founder,corporation.Id,welcomeMail);

            Assert.Equal(welcomeMail,await store.GetCorpWelcomeMailAsync(corporation.Id));
            Assert.Equal(string.Empty,await store.GetCorpWelcomeMailAsync(otherCorporation.Id));
            var reopened = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            Assert.Equal(welcomeMail,await reopened.GetCorpWelcomeMailAsync(corporation.Id));

            var outsider = await rig.CharacterAsync("Welcome Mail Outsider");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                store.SetCorpWelcomeMailAsync(outsider,corporation.Id,"unauthorized"));
            Assert.Equal(welcomeMail,await store.GetCorpWelcomeMailAsync(corporation.Id));
        }
        finally
        {
            await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Corp_registry_welcome_mail_rpc_returns_raw_text_and_requires_personnel_manager_to_set()
    {
        await using var rig = await Rig.CreateAsync();
        var schema = "corp_welcome_rpc_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await CorporationStore.MigrateAsync(rig.Source,schema);
            var store = new CorporationStore(rig.Source,rig.Characters,rig.Wallets,rig.Transactions,rig.Skills,() => rig.Now,schema);
            var ceo = await rig.CharacterAsync("Welcome RPC CEO");
            var personnelManager = await rig.CharacterAsync("Welcome RPC Manager");
            var ordinaryMember = await rig.CharacterAsync("Welcome RPC Member");
            var corporation = await store.CreateAsync(ceo,new("Welcome RPC","WRPC","","",0m),"welcome-rpc-" + Guid.NewGuid().ToString("N"));
            foreach (var character in new[] { personnelManager,ordinaryMember })
            {
                var application = await store.ApplyAsync(character,corporation.Id,"");
                await store.RespondAsync(ceo,application,6,"","");
                await store.RespondAsync(character,application,2,"","");
            }
            var manager = (await store.MemberAsync(personnelManager))!;
            await store.UpdateMemberAsync(ceo,manager with { Masks=new RoleMasks(CorporationRoles.PersonnelManager) });

            var registry = (IService)new CorpRegistryService(store,rig.Characters,() => rig.Now);
            Assert.True(registry.HasMethod("GetCorpWelcomeMail"));
            Assert.True(registry.HasMethod("SetCorpWelcomeMail"));
            ClientSession Session(long characterId)
            {
                var session = new ClientSession { CharacterId=characterId };
                session.Set("corporationID",new PyInt(corporation.Id));
                session.Set("corpid",new PyInt(corporation.Id));
                return session;
            }

            var managerSession = Session(personnelManager);
            Assert.Equal("",Assert.IsType<PyWString>(await registry.CallAsync(Call(managerSession,"GetCorpWelcomeMail"))).Value);
            const string welcomeMail = "Welcome to <i>New Eden</i>, capsuleer!";
            Assert.IsType<PyNone>(await registry.CallAsync(Call(managerSession,"SetCorpWelcomeMail",new PyWString(welcomeMail))));
            Assert.Equal(welcomeMail,Assert.IsType<PyWString>(await registry.CallAsync(Call(managerSession,"GetCorpWelcomeMail"))).Value);

            var ordinarySession = Session(ordinaryMember);
            Assert.Equal(welcomeMail,Assert.IsType<PyWString>(await registry.CallAsync(Call(ordinarySession,"GetCorpWelcomeMail"))).Value);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await registry.CallAsync(Call(ordinarySession,"SetCorpWelcomeMail",new PyWString("unauthorized"))));
            Assert.Equal(welcomeMail,Assert.IsType<PyWString>(await registry.CallAsync(Call(managerSession,"GetCorpWelcomeMail"))).Value);
        }
        finally
        {
            await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Corp_registry_member_query_returns_the_corporations_persisted_members()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Query Founder");
        var member = await rig.CharacterAsync("Query Member");
        var corp = await rig.Store.CreateAsync(founder,new("Query Works","QUERY","","",0.05m),"member-query");
        var application = await rig.Store.ApplyAsync(member,corp.Id,"");
        await rig.Store.RespondAsync(founder,application,6,"","");
        await rig.Store.RespondAsync(member,application,2,"","");

        var session = new ClientSession();
        session.CharacterId = founder;
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        async Task<long[]> Query(PyValue criteria) => Assert.IsType<PyList>(await service.CallAsync(
            Call(session,"GetMemberIDsByQuery",criteria,PyBool.False,PyBool.False))).Items
            .Select(value => Assert.IsType<PyInt>(value).Value)
            .Order()
            .ToArray();

        Assert.Equal(new[] { founder,member }.Order().ToArray(),await Query(new PyList([])));
        var search = new PyList([
            new PyList([PyNone.Instance,new PyStr("characterID"),new PyInt(10),new PyStr("Query M*")]),
            new PyList([new PyInt(2),new PyStr("corporationID"),new PyInt(1),new PyInt(corp.Id)])
        ]);
        Assert.Equal(new[] { member },await Query(search));

        var simple = Assert.IsType<PyList>(await service.CallAsync(Call(session,"GetMemberTrackingInfoSimple")));
        var simpleRows = simple.Items.Select(row => Assert.IsType<PyList>(row)).ToArray();
        Assert.Equal(2,simpleRows.Length);
        Assert.All(simpleRows,row => Assert.Equal(13,row.Items.Count));
        Assert.Contains(simpleRows,row => row.Items[0] == new PyInt(member));
        var detailed = await service.CallAsync(Call(session,"GetMemberTrackingInfo"));
        Assert.IsType<PyObject>(detailed);
        Assert.NotEmpty(MarshalCodec.Encode(detailed));

        await rig.Store.SetVoteShareholderAsync(corp.Id,corp.Id,6);
        await rig.Store.SetVoteShareholderAsync(corp.Id,member,4);
        var aboveAverage = Assert.IsType<PyList>(await service.CallAsync(Call(session,"GetMemberIDsWithMoreThanAvgShares")));
        Assert.Equal(new[] { new PyInt(member) },aboveAverage.Items);

        await rig.Store.UpdateTitleAsync(founder,corp.Id,new CorporationTitle(1,"Private title scheme",new RoleMasks()));
        var info = await service.CallAsync(Call(session,"GetInfoWindowDataForChar",new PyInt(member)));
        Assert.Equal(new PyInt(corp.Id),Field(info,"corpID"));
        Assert.Equal(new PyWString(""),Field(info,"title"));
        Assert.Same(PyNone.Instance,Field(info,"title1"));
        Assert.All(Enumerable.Range(2,15),index => Assert.Same(PyNone.Instance,Field(info,$"title{index}")));
        var suggestions = Assert.IsType<PyList>(await service.CallAsync(Call(session,"GetSuggestedTickerNames")));
        var ticker = Assert.IsType<PyStr>(Field(suggestions.Items.Single(),"tickerName")).Value;
        Assert.Equal(4,ticker.Length);
        Assert.All(ticker,character => Assert.InRange(character,'A','Z'));
    }

    [PgFact]
    public async Task Corp_registry_structure_and_mail_settings_are_role_gated_and_persisted()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Settings Founder");
        var outsider = await rig.CharacterAsync("Settings Outsider");
        var corp = await rig.Store.CreateAsync(founder,new("Settings Works","SET","","",0.05m),"corp-settings");
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        var session = new ClientSession();
        session.CharacterId = founder;
        ValueTask<PyValue> Rpc(ClientSession actor,string method,params PyValue[] args) =>
            service.CallAsync(Call(actor,method,args));

        Assert.Equal(new PyInt(0),await Rpc(session,"DoesMyCorpAcceptStructures"));
        Assert.Equal(new PyInt(0),await Rpc(session,"DoesCorpRestrictCorpMails"));
        var defaultReinforce = Assert.IsType<PyTuple>(await Rpc(session,"GetStructureReinforceDefault"));
        Assert.Equal(new PyInt(255),defaultReinforce.Items[0]);
        Assert.Equal(new PyInt(20),defaultReinforce.Items[1]);

        Assert.IsType<PyNone>(await Rpc(session,"RegisterNewAcceptStructureSettings",PyBool.True));
        Assert.IsType<PyNone>(await Rpc(session,"RegisterNewCorpMailRestrictionSettings",PyBool.True));
        Assert.IsType<PyNone>(await Rpc(session,"SetStructureReinforceDefault",new PyInt(7)));
        Assert.Equal(new PyInt(1),await Rpc(session,"DoesMyCorpAcceptStructures"));
        Assert.Equal(new PyInt(1),await Rpc(session,"DoesCorpRestrictCorpMails"));
        var updated = Assert.IsType<PyTuple>(await Rpc(session,"GetStructureReinforceDefault"));
        Assert.Equal(new PyInt(255),updated.Items[0]);
        Assert.Equal(new PyInt(7),updated.Items[1]);

        var persisted = await rig.ReopenStore().GetAsync(corp.Id);
        Assert.True(persisted!.Settings.GetProperty("acceptStructures").GetBoolean());
        Assert.True(persisted.Settings.GetProperty("restrictCorpMails").GetBoolean());
        Assert.Equal(7,persisted.Settings.GetProperty("structureReinforceDefault").GetInt32());
        await Assert.ThrowsAsync<CorporationError>(async () => await Rpc(session,"SetStructureReinforceDefault",new PyInt(24)));

        var outsiderSession = new ClientSession();
        outsiderSession.CharacterId = outsider;
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Rpc(outsiderSession,"RegisterNewAcceptStructureSettings",PyBool.False));
    }

    [PgFact]
    public async Task Corp_recruitment_ads_persist_filter_notify_and_replay_by_call_id()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Recruit Founder");
        var corp = await rig.Store.CreateAsync(founder,new("Recruit Works","RECRUIT","","",0.05m),"recruit-corp");
        var sessions = new CharacterSessions();
        var sink = new PacketSink();
        var session = new ClientSession(sink);
        session.CharacterId = founder;
        session.Set("corpid",new PyInt(corp.Id));
        sessions.Enter(founder,session);
        var service = (IService)new CorpRecruitmentProxyService(rig.Store,rig.Characters,() => rig.Now,sessions);
        var createArgs = new PyValue[]
        {
            new PyInt(7),new PyInt(3),new PyInt(4),new PyWString("New pilots welcome"),
            new PyList([new PyInt(founder)]),new PyWString("Join Recruit Works"),
            new PyInt(8),new PyInt(100),new PyInt(16)
        };
        ServiceCall CreateCall() => new()
        {
            Session=session,Method="CreateRecruitmentAd",Args=createArgs,CallId=new PyInt(776)
        };

        var adId = Assert.IsType<PyInt>(await service.CallAsync(CreateCall())).Value;
        Assert.Empty(sink.Packets);
        var persisted = await rig.ReopenStore().RecruitmentAdsAsync(corp.Id);
        var stored = Assert.Single(persisted);
        Assert.Equal(adId,stored.Id);
        Assert.Equal("Join Recruit Works",stored.Title);
        Assert.Equal(new[] { founder },stored.Recruiters);
        session.RunAfterResponse();
        var createNotice = Assert.Single(sink.Packets);
        Assert.Equal("OnCorporationRecruitmentAdChanged",NotificationName(createNotice));
        Assert.Equal("ownerid",NotificationAddress(createNotice).IdType);

        var replayId = Assert.IsType<PyInt>(await service.CallAsync(CreateCall())).Value;
        Assert.Equal(adId,replayId);
        Assert.Single(await rig.ReopenStore().RecruitmentAdsAsync(corp.Id));

        var ads = Assert.IsType<PyList>(await service.CallAsync(Call(session,"GetRecruitmentAdsForCorporation")));
        var row = Assert.IsType<PyObject>(Assert.Single(ads.Items));
        var header = Assert.IsType<PyList>(Field(row,"header"));
        var line = Assert.IsType<PyList>(Field(row,"line"));
        Assert.Equal(14,header.Items.Count);
        Assert.Equal(new PyStr("adID"),header.Items[0]);
        Assert.Equal(new PyInt(adId),line.Items[0]);
        Assert.Equal(new PyWString("Join Recruit Works"),line.Items[7]);
        Assert.Equal(new PyInt(1),line.Items[13]);

        var searchArgs = new PyValue[]
        {
            new PyInt(2),new PyInt(4),PyBool.False,PyBool.False,new PyInt(0),new PyInt(1),
            new PyInt(10),Py.Number(0.1),Py.Number(0.1),new PyInt(8),new PyInt(16)
        };
        var matches = Assert.IsType<PyList>(await service.CallAsync(Call(session,"SearchCorpAds",searchArgs)));
        Assert.Single(matches.Items);
        searchArgs[0] = new PyInt(8);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call(session,"SearchCorpAds",searchArgs))).Items);

        var beforeExpiry = stored.ExpiryFiletime;
        Assert.IsType<PyNone>(await service.CallAsync(Call(session,"UpdateRecruitmentAd",
            new PyInt(adId),new PyInt(4),new PyInt(8),new PyWString("Updated details"),
            new PyList([new PyInt(founder)]),new PyWString("Updated title"),new PyInt(3),
            new PyInt(4),new PyInt(200),new PyInt(32))));
        var updated = Assert.Single(await rig.ReopenStore().RecruitmentAdsAsync(corp.Id));
        Assert.Equal(4,updated.TypeMask);
        Assert.Equal(8,updated.LangMask);
        Assert.Equal("Updated details",updated.Description);
        Assert.Equal("Updated title",updated.Title);
        Assert.Equal(200,updated.MinSP);
        Assert.Equal(32,updated.OtherMask);
        Assert.Equal(beforeExpiry+3*864_000_000_000L,updated.ExpiryFiletime);

        Assert.IsType<PyNone>(await service.CallAsync(Call(session,"DeleteRecruitmentAd",new PyInt(adId))));
        Assert.Empty(await rig.ReopenStore().RecruitmentAdsAsync(corp.Id));
        Assert.IsType<PyNone>(await service.CallAsync(Call(session,"GetRecruitmentAd",new PyInt(corp.Id),new PyInt(adId))));
    }

    [PgFact]
    public async Task Corp_registry_contacts_labels_and_bulletins_persist_and_enforce_roles()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Social Founder");
        var contactId = await rig.CharacterAsync("Social Contact");
        var memberId = await rig.CharacterAsync("Social Member");
        var outsider = await rig.CharacterAsync("Social Outsider");
        var corp = await rig.Store.CreateAsync(founder,new("Social Works","SOCIAL","","",0.05m),"corp-social");
        var application = await rig.Store.ApplyAsync(memberId,corp.Id,"Join");
        await rig.Store.RespondAsync(founder,application,6,"","");
        await rig.Store.RespondAsync(memberId,application,2,"","");

        var session = new ClientSession();
        session.CharacterId = founder;
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        ValueTask<PyValue> Rpc(ClientSession actor,string method,params PyValue[] args) =>
            service.CallAsync(Call(actor,method,args));
        long Integer(PyValue value) => value switch
        {
            PyInt number => number.Value,
            PyLong number => number.Value,
            _ => throw new Xunit.Sdk.XunitException($"expected integer, got {value.GetType().Name}")
        };

        Assert.Empty(Assert.IsType<PyDict>(await Rpc(session,"GetCorporateContacts")).Entries);
        Assert.IsType<PyNone>(await Rpc(session,"AddCorporateContact",new PyInt(contactId),new PyInt(-5)));
        var contacts = Assert.IsType<PyDict>(await Rpc(session,"GetContactList"));
        var contact = contacts.Entries.Single(entry => Integer(entry.Key) == contactId).Value;
        Assert.Equal(new PyInt(contactId),Field(contact,"contactID"));
        Assert.Equal(new PyInt(-5),Field(contact,"relationshipID"));
        Assert.Equal(new PyInt(0),Field(contact,"inWatchlist"));

        var labelId = Assert.IsType<PyInt>(await Rpc(session,"CreateLabel",new PyWString("Diplomats"),new PyInt(0x123456)));
        Assert.Equal(1,labelId.Value);
        var secondLabelId = Assert.IsType<PyInt>(await Rpc(session,"CreateLabel",new PyWString("Executives"),new PyInt(0x654321)));
        Assert.Equal(2,secondLabelId.Value);
        Assert.IsType<PyNone>(await Rpc(session,"AssignLabels",new PyList([new PyInt(contactId)]),labelId));
        Assert.IsType<PyNone>(await Rpc(session,"AssignLabels",new PyList([new PyInt(contactId)]),secondLabelId));
        var labels = Assert.IsType<PyDict>(await Rpc(session,"GetLabels"));
        Assert.Equal(2,labels.Entries.Count);
        var diplomat = labels.Entries.Single(entry => Integer(entry.Key) == labelId.Value).Value;
        Assert.Equal(new PyWString("Diplomats"),Field(diplomat,"name"));
        Assert.Equal(new PyInt(0x123456),Field(diplomat,"color"));
        Assert.IsType<PyNone>(await Rpc(session,"EditLabel",labelId,new PyWString("Diplomacy"),new PyInt(0x111111)));
        var editedDiplomat = Assert.IsType<PyDict>(await Rpc(session,"GetLabels")).Entries.Single(entry => Integer(entry.Key) == labelId.Value).Value;
        Assert.Equal(new PyWString("Diplomacy"),Field(editedDiplomat,"name"));
        Assert.Equal(new PyInt(0x111111),Field(editedDiplomat,"color"));
        Assert.Equal(3,Integer(Field(Assert.IsType<PyDict>(await Rpc(session,"GetCorporateContacts"))
            .Entries.Single().Value,"labelMask")));

        var bulletinId = Assert.IsType<PyInt>(await Rpc(session,"AddBulletin",new PyWString("Ops"),new PyWString("Docking window")));
        var bulletinRows = Assert.IsType<PyObject>(await Rpc(session,"GetBulletins"));
        Assert.NotEmpty(MarshalCodec.Encode(bulletinRows));
        Assert.Single(Assert.IsType<PyList>(Field(bulletinRows,"lines")).Items);
        Assert.IsType<PyPackedRow>(await Rpc(session,"GetBulletin",bulletinId));
        Assert.Equal(MarshalCodec.Encode(bulletinRows),MarshalCodec.Encode(await Rpc(session,"GetBulletinEntries")));

        var reopened = (IService)new CorpRegistryService(rig.ReopenStore(),rig.Characters,() => rig.Now);
        Assert.Single(Assert.IsType<PyDict>(await reopened.CallAsync(Call(session,"GetCorporateContacts"))).Entries);
        Assert.Single(Assert.IsType<PyList>(Field(await reopened.CallAsync(Call(session,"GetBulletins")),"lines")).Items);

        Assert.IsType<PyNone>(await Rpc(session,"EditCorporateContact",new PyInt(contactId),new PyInt(2)));
        var directContact = Assert.Single(await rig.Store.CorporationContactsAsync(corp.Id));
        Assert.Equal(2,directContact.RelationshipId);
        Assert.Equal(new PyInt(2),Field(Assert.IsType<PyDict>(await Rpc(session,"GetCorporateContacts"))
            .Entries.Single().Value,"relationshipID"));
        Assert.IsType<PyNone>(await Rpc(session,"EditContactsRelationshipID",new PyList([new PyInt(contactId)]),new PyInt(10)));
        Assert.Equal(new PyInt(10),Field(Assert.IsType<PyDict>(await Rpc(session,"GetCorporateContacts"))
            .Entries.Single().Value,"relationshipID"));
        Assert.IsType<PyNone>(await Rpc(session,"AssignLabels",new PyList([new PyInt(contactId)]),labelId));
        Assert.IsType<PyNone>(await Rpc(session,"AssignLabels",new PyList([new PyInt(contactId)]),secondLabelId));
        Assert.Equal(3,Integer(Field(Assert.IsType<PyDict>(await Rpc(session,"GetCorporateContacts"))
            .Entries.Single().Value,"labelMask")));
        Assert.IsType<PyNone>(await Rpc(session,"RemoveLabels",new PyList([new PyInt(contactId)]),labelId));
        Assert.Equal(new PyInt(2),Field(Assert.IsType<PyDict>(await Rpc(session,"GetCorporateContacts"))
            .Entries.Single().Value,"labelMask"));
        Assert.IsType<PyNone>(await Rpc(session,"DeleteLabel",secondLabelId));
        Assert.Equal(new PyInt(0),Field(Assert.IsType<PyDict>(await Rpc(session,"GetCorporateContacts"))
            .Entries.Single().Value,"labelMask"));
        Assert.IsType<PyNone>(await Rpc(session,"DeleteLabel",labelId));
        Assert.Empty(Assert.IsType<PyDict>(await Rpc(session,"GetLabels")).Entries);
        Assert.IsType<PyInt>(await Rpc(session,"UpdateBulletin",bulletinId,new PyWString("Ops updated"),new PyWString("Complete")));
        Assert.IsType<PyNone>(await Rpc(session,"UpdateBulletinOrder",new PyList([bulletinId])));
        Assert.IsType<PyNone>(await Rpc(session,"DeleteBulletin",bulletinId));
        Assert.Empty(Assert.IsType<PyList>(Field(await Rpc(session,"GetBulletins"),"lines")).Items);
        Assert.IsType<PyNone>(await Rpc(session,"RemoveCorporateContacts",new PyList([new PyInt(contactId)])));
        Assert.Empty(Assert.IsType<PyDict>(await Rpc(session,"GetCorporateContacts")).Entries);
        Assert.Empty(Assert.IsType<PyList>(await Rpc(session,"GetPendingAutoKicks")).Items);

        var memberSession = new ClientSession();
        memberSession.CharacterId = memberId;
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Rpc(memberSession,"AddCorporateContact",new PyInt(contactId),new PyInt(1)));
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Rpc(memberSession,"AddBulletin",new PyWString("Denied"),new PyWString("No role")));
        var memberRecord = await rig.Store.MemberAsync(memberId);
        await rig.Store.UpdateMemberAsync(founder,memberRecord! with
        {
            Masks=memberRecord.Masks with { Roles=CorporationRoles.Diplomat }
        });
        Assert.IsType<PyNone>(await Rpc(memberSession,"AddCorporateContact",new PyInt(contactId),new PyInt(1)));
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Rpc(memberSession,"AddBulletin",new PyWString("Denied"),new PyWString("Still no chat role")));
        memberRecord = await rig.Store.MemberAsync(memberId);
        await rig.Store.UpdateMemberAsync(founder,memberRecord! with
        {
            Masks=memberRecord.Masks with { Roles=CorporationRoles.Diplomat | CorporationRoles.ChatManager }
        });
        Assert.IsType<PyInt>(await Rpc(memberSession,"AddBulletin",new PyWString("Manager"),new PyWString("Can post")));

        var outsiderSession = new ClientSession();
        outsiderSession.CharacterId = outsider;
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Rpc(outsiderSession,"AddCorporateContact",new PyInt(contactId),new PyInt(1)));
        Assert.Equal(corp.Id,(await rig.Store.GetAsync(corp.Id))?.Id);
    }

    [PgFact]
    public async Task Corp_registry_dividends_settle_once_for_shareholders_and_members()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Dividend Founder");
        var member = await rig.CharacterAsync("Dividend Member");
        var corp = await rig.Store.CreateAsync(founder,new("Dividend Works","DIV","","",0.05m),"corp-dividend");
        var application = await rig.Store.ApplyAsync(member,corp.Id,"");
        await rig.Store.RespondAsync(founder,application,6,"","");
        await rig.Store.RespondAsync(member,application,2,"","");
        await rig.Store.SetVoteShareholderAsync(corp.Id,corp.Id,0);
        await rig.Store.SetVoteShareholderAsync(corp.Id,member,100);

        var session = new ClientSession();
        session.CharacterId = founder;
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        await rig.Transactions.TransferAndCommitAsync(new Eve.Wallet.WalletAccount(founder),
            new Eve.Wallet.WalletAccount(corp.Id,1000),100m,10,"Dividend funding",rig.Now,"dividend-fund","fund");
        var memberBefore = (await rig.Wallets.GetAsync(member))!.Isk;

        ServiceCall Dividend(bool shareholders,long amount,int callId) => new()
        {
            Session=session,Method="PayoutDividend",
            Args=[shareholders ? PyBool.True : PyBool.False,new PyReal(amount)],
            CallId=new PyInt(callId)
        };
        Assert.IsType<PyNone>(await service.CallAsync(Dividend(true,100,9001)));
        Assert.Equal(0m,await rig.Transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(corp.Id,1000)));
        Assert.Equal(memberBefore+100m,(await rig.Wallets.GetAsync(member))!.Isk);
        Assert.IsType<PyNone>(await service.CallAsync(Dividend(true,100,9001)));
        Assert.Equal(memberBefore+100m,(await rig.Wallets.GetAsync(member))!.Isk);

        await rig.Transactions.TransferAndCommitAsync(new Eve.Wallet.WalletAccount(founder),
            new Eve.Wallet.WalletAccount(corp.Id,1000),20m,10,"Dividend funding",rig.Now,"dividend-fund-members","fund");
        var founderBefore = (await rig.Wallets.GetAsync(founder))!.Isk;
        memberBefore = (await rig.Wallets.GetAsync(member))!.Isk;
        Assert.IsType<PyNone>(await service.CallAsync(Dividend(false,20,9002)));
        Assert.Equal(founderBefore+10m,(await rig.Wallets.GetAsync(founder))!.Isk);
        Assert.Equal(memberBefore+10m,(await rig.Wallets.GetAsync(member))!.Isk);
    }

    [PgFact]
    public async Task Corp_registry_recent_kill_and_loss_routes_return_crowsets()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Kill Report Founder");
        await rig.Store.CreateAsync(founder,new("Kill Report Works","KILL","","",0.05m),"corp-kill-report");
        var session = new ClientSession();
        session.CharacterId = founder;
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);

        var kills = Assert.IsType<PyObject>(await service.CallAsync(Call(session,"GetRecentKills",new PyInt(10))));
        Assert.Empty(Assert.IsType<PyList>(Field(kills,"lines")).Items);
        Assert.NotEmpty(MarshalCodec.Encode(kills));
        var losses = Assert.IsType<PyObject>(await service.CallAsync(Call(session,"GetRecentLosses",new PyInt(10))));
        Assert.Empty(Assert.IsType<PyList>(Field(losses,"lines")).Items);
        Assert.NotEmpty(MarshalCodec.Encode(losses));
    }

    [PgFact]
    public async Task Corp_registry_recent_kill_rows_keep_the_fixed_client_columns_and_cursor()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Killmail Founder");
        var corp = await rig.Store.CreateAsync(founder,new("Killmail Works","KMAIL","","",0.05m),"corp-killmail");
        var session = new ClientSession();
        session.CharacterId = founder;
        var requests = new List<(long CorporationId,bool Losses,int Limit,long? StartKillId)>();
        var record = new PyDict([
            new(new PyStr("killID"),new PyLong(7001)),
            new(new PyStr("solarSystemID"),new PyInt(30000142)),
            new(new PyStr("victimCharacterID"),new PyInt(90000001)),
            new(new PyStr("victimCorporationID"),new PyInt(98000001)),
            new(new PyStr("finalCharacterID"),new PyInt(founder)),
            new(new PyStr("finalCorporationID"),new PyInt(corp.Id)),
            new(new PyStr("killBlob"),new PyStr("serialized")),
            new(new PyStr("killTime"),new PyLong(133700000000000000L)),
            new(new PyStr("finalSecurityStatus"),new PyReal(0.5)),
            new(new PyStr("iskLost"),new PyReal(1234.5))
        ]);
        ValueTask<IReadOnlyList<PyValue>> Read(long corporationId,bool losses,int limit,long? startKillId,CancellationToken token)
        {
            requests.Add((corporationId,losses,limit,startKillId));
            return ValueTask.FromResult<IReadOnlyList<PyValue>>([record]);
        }
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now,
            readRecentCorporationKillmails:Read);

        var kills = Assert.IsType<PyObject>(await service.CallAsync(Call(session,"GetRecentKills",new PyInt(10),new PyInt(8000))));
        var killRows = Assert.IsType<PyList>(Field(kills,"lines"));
        var kill = Assert.IsType<PyPackedRow>(Assert.Single(killRows.Items));
        Assert.Equal(25,kill.Values.Count);
        Assert.Equal(new PyInt(7001),kill.Values[0]);
        Assert.Equal(new PyInt(corp.Id),kill.Values[8]);
        Assert.Equal(new PyWString("serialized"),kill.Values[13]);
        Assert.Equal(new PyLong(133700000000000000L),kill.Values[14]);

        var losses = Assert.IsType<PyObject>(await service.CallAsync(Call(session,"GetRecentLosses",new PyInt(5),new PyInt(7000))));
        Assert.Single(Assert.IsType<PyList>(Field(losses,"lines")).Items);
        Assert.Equal(new[]
        {
            (corp.Id,false,10,(long?)8000),
            (corp.Id,true,5,(long?)7000)
        },requests);
    }

    [PgFact]
    public async Task Corp_registry_aggression_settings_schedule_and_persist()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Aggression Founder");
        var outsider = await rig.CharacterAsync("Aggression Outsider");
        var corp = await rig.Store.CreateAsync(founder,new("Aggression Works","AGGR","","",0.05m),"corp-aggression");
        var session = new ClientSession();
        session.CharacterId = founder;
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);
        ValueTask<PyValue> Rpc(ClientSession actor,string method,params PyValue[] args) =>
            service.CallAsync(Call(actor,method,args));

        var defaults = await Rpc(session,"GetAggressionSettings");
        Assert.Equal(new PyLong(0),Field(defaults,"_enableAfter"));
        Assert.IsType<PyNone>(Field(defaults,"_disableAfter"));
        var scheduled = await Rpc(session,"RegisterNewAggressionSettings",PyBool.False);
        Assert.Equal(new PyLong(0),Field(scheduled,"_enableAfter"));
        var due = Assert.IsType<PyLong>(Field(scheduled,"_disableAfter")).Value;
        Assert.Equal(864_000_000_000L+Assert.IsType<PyLong>(CorporationWire.Time(rig.Now)).Value,due);
        Assert.True((await rig.Store.GetAsync(corp.Id))!.FriendlyFire);
        var reopened = (IService)new CorpRegistryService(rig.ReopenStore(),rig.Characters,() => rig.Now);
        Assert.Equal(MarshalCodec.Encode(scheduled),MarshalCodec.Encode(await reopened.CallAsync(Call(session,"GetAggressionSettings"))));

        var outsiderSession = new ClientSession();
        outsiderSession.CharacterId = outsider;
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Rpc(outsiderSession,"RegisterNewAggressionSettings",PyBool.True));
    }

    [PgFact]
    public async Task Corp_registry_ally_base_cost_uses_the_reference_fallback()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("Ally Cost Founder");
        await rig.Store.CreateAsync(founder,new("Ally Cost Works","ALLY","","",0.05m),"corp-ally-cost");
        var session = new ClientSession();
        session.CharacterId = founder;
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now);

        Assert.Equal(new PyInt(10_000_000),await service.CallAsync(Call(session,"CharGetAllyBaseCost")));
        long? measuredCharacter = null;
        var configured = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now,
            readCharacterAllyBaseCost:(characterId,token) =>
            {
                measuredCharacter=characterId;
                return ValueTask.FromResult(12_345_678L);
            });
        Assert.Equal(new PyInt(12_345_678),await configured.CallAsync(Call(session,"CharGetAllyBaseCost")));
        Assert.Equal(founder,measuredCharacter);
    }

    [PgFact]
    public async Task Corp_registry_declare_war_persists_a_bill_and_replays_by_call_id()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("War Founder");
        var defender = await rig.CharacterAsync("War Defender");
        var corp = await rig.Store.CreateAsync(founder,new("War Works","WAR","","",0.05m),"corp-war");
        var target = await rig.Store.CreateAsync(defender,new("Defender Works","DEF","","",0.05m),"corp-war-target");
        var sessions = new CharacterSessions();
        var sink = new PacketSink();
        var session = new ClientSession(sink);
        session.CharacterId = founder;
        session.Set("corpid",new PyInt(corp.Id));
        sessions.Enter(founder,session);

        CorpWarDeclarationRequest? received = null;
        ValueTask<CorpWarDeclarationResult> Declare(CorpWarDeclarationRequest request,CancellationToken token)
        {
            received=request;
            return ValueTask.FromResult(new CorpWarDeclarationResult(7001,8001));
        }
        var service = (IService)new CorpRegistryService(rig.Store,rig.Characters,() => rig.Now,
            characterSessions:sessions,declareWar:Declare);
        ServiceCall DeclareCall() => new()
        {
            Session=session,Method="DeclareWarAgainst",Args=[new PyInt(target.Id),new PyInt(60000001)],CallId=new PyInt(9003)
        };
        var declaration = Assert.IsType<PyObject>(await service.CallAsync(DeclareCall()));
        Assert.Empty(sink.Packets);
        session.RunAfterResponse();
        var warChanged = Assert.Single(sink.Packets,p => NotificationName(p) == "OnWarChanged");
        Assert.Equal("ownerid",NotificationAddress(warChanged).IdType);
        Assert.Equal(new PyInt(7001),Field(declaration,"warID"));
        Assert.Equal(new PyInt(corp.Id),Field(declaration,"declaredByID"));
        Assert.Equal(new PyInt(target.Id),Field(declaration,"againstID"));
        Assert.Equal(new PyInt(60000001),Field(declaration,"warHQID"));
        Assert.Equal(new PyInt(8001),Field(declaration,"billID"));
        Assert.Equal(corp.Id,received!.CorporationId);
        Assert.Equal(founder,received.DeclaredByCharacterId);
        Assert.Equal(target.Id,received.AgainstId);
        Assert.Equal(4,received.BillTypeId);
        Assert.Equal(100_000_000m,received.BillAmount);
        Assert.Equal(1000125,received.CreditorId);

        var declaredAt = 1_337_000_000_000_000_000L;
        var requestKey = "war-declare-pg-retry";
        var first = await rig.Store.CreateWarDeclarationAsync(corp.Id,founder,target.Id,60000001,declaredAt,
            declaredAt+864_000_000_000L,declaredAt+6_048_000_000_000L,4,100_000_000m,1000125,requestKey,
            static (_,_,_,_) => Task.FromResult(9001L));
        var replay = await rig.Store.CreateWarDeclarationAsync(corp.Id,founder,target.Id,60000001,declaredAt,
            declaredAt+864_000_000_000L,declaredAt+6_048_000_000_000L,4,100_000_000m,1000125,requestKey,
            static (_,_,_,_) => throw new Xunit.Sdk.XunitException("a replay must not create another relation"));
        Assert.Equal(first,replay);
        var bill = Assert.Single(await rig.Store.CorporationBillsAsync(corp.Id));
        Assert.Equal(first.BillId,bill.BillId);
        Assert.Equal(first.WarId,bill.WarId);
        Assert.Equal(100_000_000m,bill.AmountIsk);
        await Assert.ThrowsAsync<CorporationError>(() => rig.Store.CreateWarDeclarationAsync(corp.Id,founder,target.Id,60000001,
            declaredAt,declaredAt+864_000_000_000L,declaredAt+6_048_000_000_000L,4,90_000_000m,1000125,requestKey,
            static (_,_,_,_) => Task.FromResult(9002L)));
    }

    [PgFact]
    public async Task Corporation_war_bills_are_listed_and_paid_once_through_bill_manager()
    {
        await using var rig = await Rig.CreateAsync();
        var founder = await rig.CharacterAsync("War Bill Founder",200_000_000m);
        var corp = await rig.Store.CreateAsync(founder,new("War Bill Works","WBILL","","",0.05m),"corp-war-bill");
        var nowFiletime = Assert.IsType<PyLong>(CorporationWire.Time(rig.Now)).Value;
        var declaration = await rig.Store.CreateWarDeclarationAsync(corp.Id,founder,98000002,null,nowFiletime,
            nowFiletime+864_000_000_000L,nowFiletime+6_048_000_000_000L,4,100_000_000m,1000125,"war-bill-payment",
            static (_,_,_,_) => Task.FromResult(9001L));
        var bill = Assert.Single(await rig.Store.CorporationBillsAsync(corp.Id));
        Assert.Equal(declaration.BillId,bill.BillId);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Wallets.OpenAsync(connection,transaction,1000125,0m,0);
            await transaction.CommitAsync();
        }
        await rig.Transactions.TransferAndCommitAsync(new Eve.Wallet.WalletAccount(founder),
            new Eve.Wallet.WalletAccount(corp.Id,1000),100_000_000m,10,"War bill funding",rig.Now,"war-bill-fund","fund");

        var billService = new BillMgrService(rig.Store,rig.OfficeStore,rig.Transactions,_ => null,() => rig.Now);
        var session = new ClientSession();
        session.CharacterId = founder;
        session.Set("corpid",new PyInt(corp.Id));
        session.Set("corpAccountKey",new PyInt(1000));
        var payable = Assert.IsType<PyList>(await billService.CallAsync(Call(session,"GetCorporationBills")));
        var billRow = Assert.IsType<PyObject>(Assert.Single(payable.Items));
        Assert.Equal(new PyInt(4),Field(billRow,"billTypeID"));
        Assert.Equal(new PyInt(corp.Id),Field(billRow,"debtorID"));
        Assert.Equal(new PyInt(1000125),Field(billRow,"creditorID"));

        var concordSession = new ClientSession();
        concordSession.Set("corpid",new PyInt(1000125));
        Assert.Single(Assert.IsType<PyList>(await billService.CallAsync(Call(concordSession,"GetCorporationBillsReceivable"))).Items);
        Assert.IsType<PyNone>(await billService.CallAsync(new ServiceCall
        {
            Session=session,Method="PayCorporationBill",Args=[new PyLong(bill.BillId),new PyInt(1000)]
        }));
        Assert.Empty(await rig.Store.CorporationBillsAsync(corp.Id));
        Assert.Equal(0m,await rig.Transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(corp.Id,1000)));
        Assert.Equal(100_000_000m,await rig.Transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(1000125,1000)));
    }
}
