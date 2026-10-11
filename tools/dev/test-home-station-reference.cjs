// Executes the reference service with isolated state; never writes to the reference repository.
const vm=require("node:vm"),assert=require("node:assert/strict"),path=require("node:path");
const initial=Date.UTC(2026,9,11); let now=initial;
const filetime=()=>BigInt(now)*10000n+116444736000000000n;
let record={homeStationID:60000001,stationID:60000002,corporationID:98000001};
const stations=new Map([60000001,60000002,60000003,60000004].map(id=>[id,{stationID:id,stationTypeID:1531,solarSystemID:30000142}]));
const notices=[],session={characterID:90000001,stationID:60000002,sendNotification:(...x)=>notices.push(x)};
const helpers={buildList:x=>x,buildObjectEx2:(type,fields)=>({type,...Object.fromEntries(fields)}),currentFileTime:filetime,normalizeBigInt:(x,d)=>{try{return x?BigInt(x):d;}catch{return d;}}};
const dependencies={
baseService:class {constructor(name){this.name=name;}},
machoErrors:{throwWrappedObject:(type,args,dict)=>{const e=new Error(type);e.type=type;e.args=args;e.dict=dict;throw e;}},
characterState:{getCharacterRecord:()=>({...record}),resolveHomeStationInfo:row=>({homeStationID:row.homeStationID,isFallback:false}),updateCharacterRecord:(id,f)=>{record=f(record);return{success:true};}},
stationStaticData:{getStationRecord:(s,id)=>stations.get(id)},
serviceHelpers:helpers,
corporationRuntimeState:{getCorporationOffices:()=>[{stationID:60000003},{stationID:60000004},{stationID:60000003}]},
homeStationRuntime:{isHomeStationLost:()=>false,isWormholeHomeStationSystem:()=>false,resolveSchoolHQStationID:()=>60000001}
};
const context={module:{exports:{}},exports:{},__dirname:"/reference",Buffer,Date,require:p=>p==="path"?path:dependencies[path.basename(p).replace(/\.js$/,"")]};
const referenceRoot=process.argv[2];
if(!referenceRoot) throw new Error("Usage: node tools/dev/test-home-station-reference.cjs <EveJS repository root>");
vm.runInNewContext(require("node:fs").readFileSync(path.join(referenceRoot,"server/src/services/character/homeStationService.js"),"utf8"),context);
const svc=new context.module.exports.HomeStationService();
assert.deepEqual(JSON.parse(JSON.stringify(svc.Handle_get_home_station_candidates([],session))).map(x=>x.id),[60000001,60000003,60000004,60000002]);
assert.equal(svc.Handle_get_next_remote_change_time([],session),null);
let error;try{svc.Handle_set_home_station([60000003,false],session);}catch(e){error=e;}assert.equal(error.type,"homestation.error.RemoteChangeNotExpectedError");
svc.Handle_set_home_station([60000003,true],session);
assert.equal(record.homeStationID,60000003);
assert.equal(BigInt(record.nextRemoteHomeStationChangeTime)-filetime(),365n*24n*60n*60n*10000000n);
assert.equal(notices.length,1);assert.equal(notices[0][0],"OnHomeStationChanged");
assert.equal(svc.Handle_get_next_remote_change_time([],session).type,"cpicked");
error=null;try{svc.Handle_set_home_station([60000004,true],session);}catch(e){error=e;}assert.equal(error.type,"homestation.validation.ChangeHomeStationValidationError");
svc.Handle_set_home_station([60000002,false],session);assert.equal(record.homeStationID,60000002);assert.ok(record.nextRemoteHomeStationChangeTime);
svc.Handle_set_home_station([60000001,false],session);assert.equal(record.homeStationID,60000001);
svc.Handle_reset_remote_change_time([],session);assert.equal(svc.Handle_get_next_remote_change_time([],session),null);
svc.Handle_set_home_station([60000004,true],session);now+=366*24*60*60*1000;assert.equal(svc.Handle_get_next_remote_change_time([],session),null);
console.log("EveJS executable service harness: candidate/change/cooldown/reset/expiry scenarios passed (in-memory dependency harness; no live server or reference clone writes).");
