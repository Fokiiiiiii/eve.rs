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
