using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Fitting;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Skills;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class TournamentRosterTests
{
    [PgFact]
    public async Task Consented_fleet_roster_revalidates_fits_and_manual_changes()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tm = "rt_" + suffix; var fleet = "rf_" + suffix; var ch = "rc_" + suffix; var inv = "ri_" + suffix;
        var sk = "rk_" + suffix; var mod = "rm_" + suffix; var sched = "rs_" + suffix; var evt = "re_" + suffix;
        try
        {
            await PgCharacterStore.MigrateAsync(source, ch); await PgItemStore.MigrateAsync(source, inv);
            await PgSkillStore.MigrateAsync(source, sk); await PgModuleStateStore.MigrateAsync(source, mod);
            await DurableScheduler.MigrateAsync(source, sched); await EventBus.MigrateAsync(source, evt);
            await using (var seed = source.CreateCommand($"""
                CREATE SCHEMA {tm}; CREATE SCHEMA {fleet};
                CREATE TABLE {tm}.tournaments(id bigint PRIMARY KEY,ship_types integer[] NOT NULL);
                CREATE TABLE {tm}.series(tournament_id bigint NOT NULL REFERENCES {tm}.tournaments(id),id bigint NOT NULL,PRIMARY KEY(tournament_id,id));
                INSERT INTO {tm}.tournaments VALUES(1,ARRAY[587]); INSERT INTO {tm}.series VALUES(1,0);
                CREATE TABLE {fleet}.fleets(fleet_id bigint PRIMARY KEY,auto_join_squad_id bigint,max_member_count smallint NOT NULL DEFAULT 256,
                    is_free_move boolean NOT NULL DEFAULT false,advert_active boolean NOT NULL DEFAULT false,motd text NOT NULL DEFAULT '');
                CREATE TABLE {fleet}.fleet_members(fleet_id bigint NOT NULL REFERENCES {fleet}.fleets,character_id bigint PRIMARY KEY,wing_id bigint NOT NULL DEFAULT -1,
                    squad_id bigint NOT NULL DEFAULT -1,role smallint NOT NULL DEFAULT 4,job smallint NOT NULL DEFAULT 0,
                    accepts_fleet_warp boolean NOT NULL DEFAULT true,accepts_conduit_jumps boolean NOT NULL DEFAULT true,
                    accepts_fleet_regroups boolean NOT NULL DEFAULT true,ship_type_id bigint,joined_at timestamptz NOT NULL DEFAULT now());
                CREATE TABLE {fleet}.fleet_wings(fleet_id bigint,wing_id bigint,name text,position integer);
                CREATE TABLE {fleet}.fleet_squads(fleet_id bigint,squad_id bigint,wing_id bigint,name text,position integer);
                CREATE TABLE {fleet}.fleet_join_requests(fleet_id bigint,character_id bigint,corporation_id bigint,alliance_id bigint,war_faction_id bigint,security_status double precision,created_at timestamptz);
                INSERT INTO {fleet}.fleets(fleet_id) VALUES(10);
                """)) await seed.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateAsync(source, tm); await PersistenceTournamentMatches.MigrateFleetAsync(source, tm);
            await PersistenceTournamentMatches.MigrateSpaceAsync(source, tm); await PersistenceTournamentMatches.MigrateRulesAsync(source, tm);
            await PersistenceTournamentMatches.MigrateRosterAsync(source, tm);
            var characters = new PgCharacterStore(source, ch); var items = new PgItemStore(source, inv);
            var statics = new DogmaStatic([], [], [new TypeDef(587,25,6,new Dictionary<int,double>(),[],"Rifter"),
                new TypeDef(2048,60,7,new Dictionary<int,double>(),[],"Damage Control I"),
                new TypeDef(1376,1,1,new Dictionary<int,double>(),[],"Character")]);
            var ships = new CharacterShips(source, statics, characters, items, new PgSkillStore(source, sk), new PgModuleStateStore(source, mod));
            long pilot, extra, shipId;
            await using (var c = await source.OpenConnectionAsync())
            await using (var tx = await c.BeginTransactionAsync())
            {
                async Task<long> Character(string name) => await characters.InsertAsync(c, tx, new NewCharacter(1, name, 0,1,1,1,11,1376,0,null,60000001,30000142,20000142,10000001,null,null));
                pilot = await Character("Roster Pilot " + suffix); extra = await Character("Extra Pilot " + suffix);
                shipId = await items.CreateAsync(c, tx, new NewItem(587,pilot,60000001,ItemFlags.Hangar,1,Singleton:true));
                var extraShip = await items.CreateAsync(c, tx, new NewItem(587,extra,60000001,ItemFlags.Hangar,1,Singleton:true));
                await characters.SetShipAsync(c, tx, pilot, shipId); await characters.SetShipAsync(c, tx, extra, extraShip);
                await items.CreateAsync(c, tx, new NewItem(2048,pilot,shipId,ItemFlags.HiSlot0,1,Singleton:true));
                await tx.CommitAsync();
            }
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {fleet}.fleet_members(fleet_id,character_id) VALUES(10,{pilot});
                INSERT INTO {tm}.tournament_rules VALUES(1,ARRAY[2048]); INSERT INTO {tm}.tournament_ship_points VALUES(1,587,5);
                INSERT INTO {tm}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state,red_captain_id,red_fleet_id) VALUES(1,0,30000142,100,600,0,1,{pilot},10);
                INSERT INTO {tm}.match_referees VALUES('N=1:1',1,10,100);
                """)) await seed.ExecuteNonQueryAsync();
            await using var scheduler = new DurableScheduler(source, () => 1_700_000_000_000, schema:sched);
            var events = new EventBus(source, scheduler, schema:evt);
            var store = new PersistenceTournamentMatches(source, null!, items, null!, events, null, new SdeReader(source), tm, scheduler, null, fleet, null, ships, characters);
            var referee = new ClientSession { SessionId = 10, CharacterId = 100 };
            referee.Set("accountRole",new PyLong(GameMasterRoles.Staff)); referee.Set("solarsystemid",new PyInt(30000142));
            TourneyMatchActionRequest Request(TourneyMatchAction action, params PyValue[] tail) => new(action,1,[new PyInt(1),..tail],PyNone.Instance,30000142,"N=1:1",referee);
            async Task Run(TourneyMatchAction action, params PyValue[] tail) => await store.RosterActionAsync(Request(action,tail),CancellationToken.None);
            async Task<string[]> Errors()
            { await using var command=source.CreateCommand($"SELECT errors FROM {tm}.match_roster_details WHERE character_id={pilot}"); return (string[])(await command.ExecuteScalarAsync())!; }
            await Run(TourneyMatchAction.UpdateFleetDetails,new PyInt(0));
            Assert.Equal(["Prohibited fitted type 2048"],await Errors());
            await Run(TourneyMatchAction.OverrideError,new PyLong(pilot),new PyStr("Prohibited fitted type 2048"));
            Assert.Empty(await Errors());
            await using (var c = await source.OpenConnectionAsync())
            await using (var tx = await c.BeginTransactionAsync())
            { await items.CreateAsync(c,tx,new NewItem(587,pilot,shipId,ItemFlags.Cargo,1,Singleton:true)); await tx.CommitAsync(); }
            await Run(TourneyMatchAction.UpdateFleetDetails,new PyInt(0));
            Assert.Equal(["Prohibited fitted type 2048"],await Errors());
            await Run(TourneyMatchAction.LockMemberList,new PyInt(0),new PyList([new PyLong(pilot)]));
            await Run(TourneyMatchAction.AddPlayer,new PyInt(0),new PyLong(extra));
            await using (var count = source.CreateCommand($"SELECT count(*) FROM {tm}.match_members WHERE match_id=1 AND team=0")) Assert.Equal(2L,(long)(await count.ExecuteScalarAsync())!);
            await Run(TourneyMatchAction.RemovePlayer,new PyInt(0),new PyLong(extra));
            await using (var count = source.CreateCommand($"SELECT count(*) FROM {tm}.match_members WHERE match_id=1 AND team=0")) Assert.Equal(1L,(long)(await count.ExecuteScalarAsync())!);
        }
        finally
        { await using var cleanup=source.CreateCommand($"DROP SCHEMA IF EXISTS {tm},{fleet},{ch},{inv},{sk},{mod},{sched},{evt} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
    }
}
