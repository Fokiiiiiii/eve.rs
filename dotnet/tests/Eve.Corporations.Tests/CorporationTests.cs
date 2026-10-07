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

    [Fact]
    public void Corporate_hangar_query_and_take_roles_map_by_division_and_location()
    {
        var corporation = new Corporation(
            98000000, "Corp", "CORP", 1, 1, null, 10, 100, 0, 0, "", "", 20, false, false, 0,
            JsonSerializer.SerializeToElement(new { }));
        var masks = new RoleMasks(
            AtHq: (1L << 20) | (1L << 13),
            AtBase: 1L << 21,
            AtOther: 1L << 22);
        var member = new Member(3, corporation.Id, 0, "", masks, 30, 0, false, 1000);

        var hq = CorporationRoles.Session(corporation, member, corporation.StationId, []);
        Assert.True(CorporationRoles.HasHangarAccess(hq, corporation.StationId, 115, take: false));
        Assert.True(CorporationRoles.HasHangarAccess(hq, corporation.StationId, 115, take: true));
        Assert.False(CorporationRoles.HasHangarAccess(hq, corporation.StationId, 116, take: true));

        var baseRoles = CorporationRoles.Session(corporation, member, member.BaseId, []);
        Assert.True(CorporationRoles.HasHangarAccess(baseRoles, member.BaseId!.Value, 116, take: false));
        Assert.False(CorporationRoles.HasHangarAccess(baseRoles, member.BaseId.Value, 116, take: true));

        var other = CorporationRoles.Session(corporation, member, 999_999, []);
        Assert.True(CorporationRoles.HasHangarAccess(other, 999_999, 117, take: false));
        Assert.False(CorporationRoles.HasHangarAccess(other, 999_999, 114, take: false));
    }


    [PgFact]
    public async Task Corporation_membership_projects_affiliation_without_an_alliance_zero_sentinel()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "corp_aff_sched_" + suffix;
        var eventsSchema = "corp_aff_events_" + suffix;
        var relationsSchema = "corp_aff_relations_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            var founder = await rig.CharacterAsync("Affiliation Founder");
            var notifications = new List<long>();
            var worker = new Eve.Services.Corporations.AllianceApplicationDueWorker(
                rig.Store, rig.Characters, scheduler, relations, events,
                (characterId, _) => { notifications.Add(characterId); return Task.CompletedTask; });
            rig.Store.AllianceAffiliationWriter = worker.WriteAffiliationAsync;
            await worker.RegisterAsync();
            await events.PumpAsync(); // initialize the subscriber cursor before publishing membership changes
            notifications.Clear();

            var corporation = await rig.Store.CreateAsync(founder, Request(), "affiliation-foundation");

            var membership = Assert.Single(await relations.FromAsync(Eve.Relations.RelationKinds.CorporationMember.Name, founder));
            Assert.Equal(corporation.Id, membership.Object);
            Assert.Empty(await relations.FromAsync(Eve.Relations.RelationKinds.AllianceMember.Name, corporation.Id));
            await events.PumpAsync();
            Assert.Equal([founder], notifications);
            await events.PumpAsync();
            Assert.Single(notifications);
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
    public async Task Character_affiliation_scan_returns_bounded_npc_and_player_pages()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedNpcCorporationAsync(1000044);
        var npcCharacter = await rig.CharacterAsync("NPC Page Member");
        var founder = await rig.CharacterAsync("Player Page Member");
        var corporation = await rig.Store.CreateAsync(founder, Request(), "affiliation-page");

        var npcPage = await rig.Characters.AffiliationsAfterAsync(0, 1);
        var npc = Assert.Single(npcPage);
        Assert.Equal(npcCharacter, npc.CharacterId);
        Assert.Equal(1000044, npc.CorporationId);

        var playerPage = await rig.Characters.AffiliationsAfterAsync(npc.CharacterId, 1);
        var player = Assert.Single(playerPage);
        Assert.Equal(founder, player.CharacterId);
        Assert.Equal(corporation.Id, player.CorporationId);
        Assert.Empty(await rig.Characters.AffiliationsAfterAsync(player.CharacterId, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            rig.Characters.AffiliationsAfterAsync(0, Eve.Characters.PgCharacterStore.CorporationAffiliationPageSize + 1));
    }


    [PgFact]
    public async Task Corporation_membership_and_relation_outbox_roll_back_together()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "corp_rollback_sched_" + suffix;
        var eventsSchema = "corp_rollback_events_" + suffix;
        var relationsSchema = "corp_rollback_relations_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            await Eve.Relations.RelationStore.MigrateAsync(rig.Source, relationsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(rig.Source, scheduler, schema: eventsSchema);
            var relations = new Eve.Relations.RelationStore(rig.Source, scheduler, events, relationsSchema);
            var notifications = new List<long>();
            var worker = new Eve.Services.Corporations.AllianceApplicationDueWorker(
                rig.Store, rig.Characters, scheduler, relations, events,
                (characterId, _) => { notifications.Add(characterId); return Task.CompletedTask; });
            rig.Store.AllianceAffiliationWriter = worker.WriteAffiliationAsync;
            await worker.RegisterAsync();
            await events.PumpAsync();

            var founder = await rig.CharacterAsync("Rollback Founder");
            rig.Store.CorporationMemberAffiliationWriter = async (connection, transaction, characterId, corporationId, token) =>
            {
                await relations.SetAsync((Npgsql.NpgsqlConnection)connection, (Npgsql.NpgsqlTransaction)transaction,
                    Eve.Relations.RelationKinds.CorporationMember.Name, characterId, corporationId, 1,
                    "corporation membership", cancellationToken: token);
                throw new InvalidOperationException("forced failure after relation write");
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rig.Store.CreateAsync(founder, Request(), "rollback-affiliation"));

            Assert.Equal(1000044, (await rig.Characters.GetAsync(founder))!.CorporationId);
            Assert.Equal(5000000m, (await rig.Wallets.GetAsync(founder))!.Isk);
            Assert.Empty(await relations.FromAsync(Eve.Relations.RelationKinds.CorporationMember.Name, founder));
            await events.PumpAsync();
            Assert.Empty(notifications);
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
}


public sealed class CorporationOfficeTests
{
    private static readonly CorporationOfficeStation Station = new(60000001, 1529, 1000125, 30000142, 0.9, 24);

    [PgFact]
    public async Task Rent_quote_charges_once_and_creates_the_next_30_day_bill()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await CreateCorporationAsync(rig);
        await SetOfficeRoleAsync(rig, actor, CorporationRoles.RentOffice);
        var service = CreateService(rig);
        var quote = await QuoteAsync(service, actor, corporation.Id);
        var before = (await rig.Wallets.GetAsync(corporation.Id))!.Isk;
        var call = Call("RentOffice", actor, corporation.Id, new Eve.Marshal.PyReal((double)quote));

        await service.RentOffice(call);
        await service.RentOffice(call);

        var offices = await rig.OfficeStore.ForCorporationAsync(corporation.Id);
        var office = Assert.Single(offices);
        Assert.Equal(quote, office.RentIsk);
        Assert.Equal(CorporationOfficeStore.OfficeFolderTypeId, (await rig.Items.GetAsync(office.OfficeFolderId))!.TypeId);
        Assert.Equal(rig.Now + CorporationOfficeStore.RentalCycleMs, office.ExpiresAtMs);
        Assert.Equal(before - quote, (await rig.Wallets.GetAsync(corporation.Id))!.Isk);

        await using var bill = rig.Source.CreateCommand(
            $"SELECT amount_isk,due_at_ms,status FROM {rig.CorporationSchema}.office_rent_bills WHERE corporation_id=$1");
        bill.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporation.Id });
        await using var reader = await bill.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(quote, reader.GetDecimal(0));
        Assert.Equal(rig.Now + CorporationOfficeStore.RentalCycleMs, reader.GetInt64(1));
        Assert.Equal((short)0, reader.GetInt16(2));
        Assert.False(await reader.ReadAsync());
    }

    [PgFact]
    public async Task Rent_office_requires_the_station_service_role()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await CreateCorporationAsync(rig);
        await SetOfficeRoleAsync(rig, actor, 0);
        var service = CreateService(rig);
        var quote = await QuoteAsync(service, actor, corporation.Id);
        var before = (await rig.Wallets.GetAsync(corporation.Id))!.Isk;

        var error = await Assert.ThrowsAsync<CorporationError>(async () =>
        {
            await service.RentOffice(Call("RentOffice", actor, corporation.Id, new Eve.Marshal.PyReal((double)quote)));
        });

        Assert.Equal("CORPORATION_ACCESS_DENIED", error.Code);
        Assert.Empty(await rig.OfficeStore.ForCorporationAsync(corporation.Id));
        Assert.Equal(before, (await rig.Wallets.GetAsync(corporation.Id))!.Isk);
    }

    [PgFact]
    public async Task Station_office_limit_is_enforced_and_empty_count_matches()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OfficeStore.WriteAsync(async (connection, transaction) =>
        {
            await rig.OfficeStore.LockStationAsync(connection, transaction, Station.Id);
            for (var i = 0; i < Station.OfficeCapacity; i++)
            {
                var corporationId = 80_000_000L + i;
                var officeFolderId = await rig.Items.CreateAsync(connection, transaction,
                    new Eve.Inventory.NewItem(CorporationOfficeStore.OfficeFolderTypeId, corporationId, Station.Id,
                        Eve.Inventory.ItemFlags.Hangar, Singleton: true));
                await rig.OfficeStore.RentAsync(connection, transaction, corporationId, officeFolderId, Station,
                    CorporationOfficeStore.MinimumRentIsk, rig.Now);
            }
            return 0;
        });

        var (actor, corporation) = await CreateCorporationAsync(rig);
        await SetOfficeRoleAsync(rig, actor, CorporationRoles.RentOffice);
        var service = CreateService(rig);
        var empty = await service.GetEmptyOfficeCount(Call("GetEmptyOfficeCount", actor, corporation.Id));

        Assert.Equal(0L, Assert.IsType<Eve.Marshal.PyInt>(empty).Value);
        var error = await Assert.ThrowsAsync<CorporationError>(async () =>
        {
            await service.RentOffice(Call("RentOffice", actor, corporation.Id,
                new Eve.Marshal.PyReal((double)CorporationOfficeStore.MinimumRentIsk)));
        });
        Assert.Equal("OFFICE_SLOTS_FULL", error.Code);
    }

    [PgFact]
    public async Task Unrent_impounds_corp_hangars_and_rerent_restores_them_without_release_fee()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await CreateCorporationAsync(rig);
        var service = CreateService(rig);
        var quote = await QuoteAsync(service, actor, corporation.Id);
        await service.RentOffice(Call("RentOffice", actor, corporation.Id, new Eve.Marshal.PyReal((double)quote)));
        var originalOffice = Assert.Single(await rig.OfficeStore.ForCorporationAsync(corporation.Id));
        Assert.NotNull(await rig.Items.GetAsync(originalOffice.OfficeFolderId));
        var itemId = await CreateItemAsync(rig, corporation.Id, Station.Id, Eve.Inventory.ItemFlags.CorpSAG3);

        await service.UnrentOffice(Call("UnrentOffice", actor, corporation.Id));
        Assert.Null(await rig.Items.GetAsync(originalOffice.OfficeFolderId));

        Assert.Equal(CorporationOfficeStore.ImpoundedFlag, (await rig.Items.GetAsync(itemId))!.Flag);
        Assert.Empty(await rig.OfficeStore.ForCorporationAsync(corporation.Id));

        var nextQuote = await QuoteAsync(service, actor, corporation.Id);
        await service.RentOffice(Call("RentOffice", actor, corporation.Id, new Eve.Marshal.PyReal((double)nextQuote)));
        Assert.Equal(Eve.Inventory.ItemFlags.CorpSAG1, (await rig.Items.GetAsync(itemId))!.Flag);
        var activeOffice = Assert.Single(await rig.OfficeStore.ForCorporationAsync(corporation.Id));
        Assert.NotEqual(originalOffice.OfficeFolderId, activeOffice.OfficeFolderId);
        Assert.NotNull(await rig.Items.GetAsync(activeOffice.OfficeFolderId));
    }

    [PgFact]
    public async Task Impound_release_costs_half_the_current_rent_and_moves_assets_to_division_one()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await CreateCorporationAsync(rig);
        var service = CreateService(rig);
        var itemId = await CreateItemAsync(rig, corporation.Id, Station.Id, CorporationOfficeStore.ImpoundedFlag);
        var expectedFee = decimal.Round((await QuoteAsync(service, actor, corporation.Id)) / 2m, 2,
            MidpointRounding.AwayFromZero);
        var quote = await service.GetImpoundReleasePrice(Call("GetImpoundReleasePrice", actor, corporation.Id));
        Assert.Equal(expectedFee, (decimal)Assert.IsType<Eve.Marshal.PyReal>(quote).Value);
        var before = (await rig.Wallets.GetAsync(corporation.Id))!.Isk;

        await service.GetItemsFromImpound(Call("GetItemsFromImpound", actor, corporation.Id,
            new Eve.Marshal.PyReal((double)expectedFee)));

        Assert.Equal(before - expectedFee, (await rig.Wallets.GetAsync(corporation.Id))!.Isk);
        Assert.Equal(Eve.Inventory.ItemFlags.CorpSAG1, (await rig.Items.GetAsync(itemId))!.Flag);
    }

    [PgFact]
    public async Task Director_can_trash_impounded_assets_at_the_bound_station()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await CreateCorporationAsync(rig);
        var service = CreateService(rig);
        var itemId = await CreateItemAsync(rig, corporation.Id, Station.Id, CorporationOfficeStore.ImpoundedFlag);

        await service.TrashImpoundedOffice(Call("TrashImpoundedOffice", actor, corporation.Id));

        Assert.Null(await rig.Items.GetAsync(itemId));
    }

    private static Eve.Services.Corporations.OfficeManagerService CreateService(Rig rig) =>
        new(rig.OfficeStore, rig.Store, rig.Characters, rig.Items, rig.Transactions,
            id => id == Station.Id ? Station : null, () => rig.Now);

    private static Eve.Services.ServiceCall Call(string method, long actor, long corporationId,
        params Eve.Marshal.PyValue[] args)
    {
        var session = new Eve.Services.ClientSession { CharacterId = actor };
        session.Set("corpid", new Eve.Marshal.PyLong(corporationId));
        session.Set("corporationID", new Eve.Marshal.PyLong(corporationId));
        session.Set("stationid", new Eve.Marshal.PyLong(Station.Id));
        return new Eve.Services.ServiceCall { Method = method, Args = args, Session = session };
    }

    private static async Task<decimal> QuoteAsync(Eve.Services.Corporations.OfficeManagerService service,
        long actor, long corporationId)
    {
        var result = await service.GetPriceQuote(Call("GetPriceQuote", actor, corporationId,
            new Eve.Marshal.PyLong(corporationId)));
        return decimal.Round((decimal)Assert.IsType<Eve.Marshal.PyReal>(result).Value, 2);
    }

    private static async Task<(long Actor, Corporation Corporation)> CreateCorporationAsync(Rig rig)
    {
        var actor = await rig.CharacterAsync("Office Manager CEO");
        var corporation = await rig.Store.CreateAsync(actor,
            new NewCorporation("Office Operations", "OFFOP", "Office test.", "https://example.invalid", 0.05m,
                Settings: JsonSerializer.SerializeToElement(new { shape1 = 1, color1 = 2 })),
            "office-test-founding");
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Transactions.TransferAsync(connection, transaction, actor, corporation.Id, 2_000_000m,
            1, "Office test funding", rig.Now, $"office-test-funding:{corporation.Id}");
        await transaction.CommitAsync();
        return (actor, corporation);
    }

    private static async Task SetOfficeRoleAsync(Rig rig, long characterId, long roleMask)
    {
        await using (var roles = rig.Source.CreateCommand(
            $"UPDATE {rig.CorporationSchema}.members SET roles=$2 WHERE character_id=$1"))
        {
            roles.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
            roles.Parameters.Add(new Npgsql.NpgsqlParameter { Value = roleMask });
            Assert.Equal(1, await roles.ExecuteNonQueryAsync());
        }
        await using var suspended = rig.Source.CreateCommand(
            $"UPDATE {rig.CorporationSchema}.corporations SET ceo_roles_suspended=true WHERE ceo_id=$1");
        suspended.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
        Assert.Equal(1, await suspended.ExecuteNonQueryAsync());
    }

    private static async Task<long> CreateItemAsync(Rig rig, long corporationId, long stationId, int flag)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction,
            new Eve.Inventory.NewItem(34, corporationId, stationId, flag, Singleton: true));
        await transaction.CommitAsync();
        return id;
    }
}
