using Eve.Data;
using Eve.Fitting;
using Eve.Host.Persistence;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public Task Referee_from_npc_station_returns_to_same_station_with_same_ship_and_condition() => RefereeDockRoundTrip(false);

    [RealAbyssalSimPgFact]
    public Task Referee_from_real_structure_returns_to_same_structure_with_same_ship_and_condition() => RefereeDockRoundTrip(true);

    private async Task RefereeDockRoundTrip(bool atStructure)
    {
        var start=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
        start.Environment["EVE_SIM_LISTEN"]="127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"]=Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process=System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        var schema="rd_"+Guid.NewGuid().ToString("N")[..10]; var structureSchema="rs_"+Guid.NewGuid().ToString("N")[..10];
        try
        {
            string? endpoint=null; using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while(await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if(line.StartsWith("eve-sim: listening on ",StringComparison.Ordinal)) { endpoint=line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint); _=process.StandardError.ReadToEndAsync(); _=process.StandardOutput.ReadToEndAsync();
            await using var simulation=SimClient.ToTcpSocket(endpoint); simulation.Start();
            using var connected=new CancellationTokenSource(TimeSpan.FromSeconds(15)); while(!simulation.IsConnected) await Task.Delay(20,connected.Token);
            await using var rig=await Rig.Create(simulation); rig.Now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var run=await rig.CreateRun(enter:false); var character=run.Data.CreatorCharacterId; var ship=run.Data.CreatorShipId;
            const long npcStation=60003760; var stationData=rig.Stations.Get(npcStation); Assert.NotNull(stationData);
            long station=npcStation;
            if (atStructure)
            {
                await StructureStore.MigrateAsync(rig.Source,structureSchema);
                var structures=new StructureStore(rig.Source,structureSchema);
                var sdeType=await new Eve.Inventory.ItemTypeCatalog(rig.Sde).GetAsync(35832); Assert.NotNull(sdeType);
                Assert.Equal(65,sdeType.CategoryId); Assert.Equal(1657,sdeType.GroupId);
                var pilot=await rig.Characters.GetAsync(character); Assert.NotNull(pilot);
                await using (var connection=await rig.Source.OpenConnectionAsync()) await using (var tx=await connection.BeginTransactionAsync())
                {
                    station=await rig.Items.CreateAsync(connection,tx,new Eve.Inventory.NewItem(35832,pilot.CorporationId,stationData.SolarSystemId,0,1,true));
                    Assert.True(await structures.CreateAsync(connection,tx,new NewStructure(station,35832,stationData.SolarSystemId,1e11,2e11,3e11,0,pilot.CorporationId,0,"Referee Home","")));
                    await structures.SaveAsync(connection,tx,new StructureSnapshot { StructureId=station,State=StructureState.ShieldVulnerable });
                    await tx.CommitAsync();
                }
                rig.Space.StructureExistsAsync=async (id,ct) => await structures.GetAsync(id,ct) is not null;
            }
            var dock=await rig.Ships.DockAsync(character,station,null); Assert.NotNull(dock.Moved);
            var condition=new ShipCondition(0.1,0.8,0.7,0.2,false);
            await new PgModuleStateStore(rig.Source,rig.Schemas[3]).SetShipConditionAsync(ship,condition);
            var session=new ClientSession(new RecordingSink(rig.Order)) { SessionId=character+500, ClientId=character+600 };
            session.Set("charid",new PyLong(character)); session.Set("shipid",new PyLong(ship));
            session.Set("stationid",atStructure ? PyNone.Instance : new PyLong(station)); session.Set("stationid2",atStructure ? PyNone.Instance : new PyLong(station));
            session.Set("structureid",atStructure ? new PyLong(station) : PyNone.Instance);
            session.Set("solarsystemid",atStructure ? new PyInt(stationData.SolarSystemId) : PyNone.Instance);
            session.Set("solarsystemid2",new PyInt(stationData.SolarSystemId)); session.Set("locationid",new PyLong(atStructure ? stationData.SolarSystemId : station));
            rig.Sessions.Enter(character,session); await rig.Space.OnCharacterEnteredAsync(session,character,CancellationToken.None);
            Assert.False(rig.Space.IsInSpace(session));
            var absent=await simulation.CommandAsync((ulong)session.SessionId,SimCommands.GetCondition,[],CancellationToken.None);
            Assert.False(absent.Accepted);
            Assert.Equal(station,(await rig.Items.GetAsync(ship))!.LocationId);
            var deployed=new PgDeployedStore(rig.Source,rig.Schemas[4]);
            await PersistenceTournamentArenas.MigrateAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRefereeTravelAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRefereeDirectionAsync(rig.Source,schema);
            await PersistenceTournamentMatches.MigrateRefereeMoveIdsAsync(rig.Source,schema);
            const int target=30000144;
            var arenas=new PersistenceTournamentArenas(rig.Source,rig.Sde,rig.Characters,rig.Items,deployed,rig.Foundations.Events,schema);
            await arenas.ImportIfMissingAsync(new TournamentArenaImport(target,character,[new TournamentBeaconImport(10645,"Referee Arena",[1000,2000,3000],5)]));
            var store=new PersistenceTournamentMatches(rig.Source,arenas,rig.Items,deployed,rig.Foundations.Events,simulation,rig.Sde,
                schema,rig.Foundations.Scheduler,rig.Sessions,space:rig.Space);
            var sink=new TournamentWireSink(); session.Sink=sink; session.Set("accountRole",new PyLong(GameMasterRoles.Qa));
            var service=new TourneyMgrService(matches:new TourneyMatchCallbacks(store.CreateAsync,store.ReleaseAsync,store.ActionAsync,RefTravelAsync:store.RefTravelAsync));
            var registry=new ServiceRegistry(); registry.Register(service); var dispatcher=new PacketDispatcher(registry,()=>rig.Now); long call=1;
            async Task<string> Bind()
            {
                var bind=TournamentWireRequest(call++,"tourneyMgr",new PyInt(1),"MachoBindObject",new PyInt(target));
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(bind)),session));
                var result=Assert.IsType<PyTuple>(TournamentWireResult(sink.Packets[^1]));
                return BoundObjects.ExtractName(Assert.IsType<PySubStruct>(result.Items[0]))!;
            }
            async Task Travel(string oid,string method)
            {
                var packet=TournamentWireRequest(call++,null,new PyStr(oid),method);
                Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(packet)),session));
                Assert.IsType<PyNone>(TournamentWireResult(sink.Packets[^1]));
            }
            var arenaOid=await Bind(); await Travel(arenaOid,"TeleportToArena");
            Assert.Equal(target,rig.Space.SolarSystemOf(session));
            await using (var q=rig.Source.CreateCommand($"SELECT COALESCE(original_station_id,original_structure_id),original_system_id,ship_id FROM {schema}.referee_travel WHERE character_id=$1"))
            { q.Parameters.AddWithValue(character); await using var row=await q.ExecuteReaderAsync(); Assert.True(await row.ReadAsync()); Assert.Equal(station,row.GetInt64(0)); Assert.Equal(stationData.SolarSystemId,row.GetInt32(1)); Assert.Equal(ship,row.GetInt64(2)); }
            var returnOid=await Bind(); Assert.NotEqual(arenaOid,returnOid);
            await Travel(returnOid,"TeleportToStation");
            Assert.Equal(station,session.GetInt(atStructure ? "structureid" : "stationid"));
            Assert.Null(session.GetInt(atStructure ? "stationid" : "structureid")); Assert.Equal(ship,session.GetInt("shipid"));
            var item=await rig.Items.GetAsync(ship); Assert.NotNull(item); Assert.Equal(character,item.OwnerId); Assert.Equal(station,item.LocationId);
            var snapshot=await rig.Ships.LoadAsync(character); Assert.NotNull(snapshot); Assert.Equal(station,snapshot.Character.StationId); Assert.Equal(ship,snapshot.Ship.Id);
            var saved=await new PgModuleStateStore(rig.Source,rig.Schemas[3]).ShipConditionAsync(ship);
            Assert.True(saved.ShieldCharge <= condition.ShieldCharge+0.02 && saved.ArmorDamage >= condition.ArmorDamage-0.001 && saved.Damage >= condition.Damage-0.001);
            await using var count=rig.Source.CreateCommand($"SELECT count(*) FROM {schema}.referee_travel"); Assert.Equal(0L,(long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var source=NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            await using var cleanup=source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{structureSchema} CASCADE"); await cleanup.ExecuteNonQueryAsync();
            if(!process.HasExited) process.Kill(entireProcessTree:true); await process.WaitForExitAsync();
        }
    }
}
