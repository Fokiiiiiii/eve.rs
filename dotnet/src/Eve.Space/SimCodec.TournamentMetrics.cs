namespace Eve.Space;

public static partial class SimCodec
{
    private static bool ValidMetricsRoster(IReadOnlyList<SimTournamentMetricsPilot> roster) =>
        roster.Count is > 0 and <= 256 && roster.All(x => x.CharacterId > 0 && x.ShipId > 0 && x.Team <= 1) &&
        roster.Select(x => x.CharacterId).Distinct().Count() == roster.Count &&
        roster.Select(x => x.ShipId).Distinct().Count() == roster.Count &&
        roster.SequenceEqual(roster.OrderBy(x => x.CharacterId).ThenBy(x => x.ShipId).ThenBy(x => x.Team));

    private static bool ValidMetricsIdentity(ulong id, SimSceneKey scene, long match, long generation, long start, IReadOnlyList<SimTournamentMetricsPilot> roster) =>
        id != 0 && scene.IsValid && scene.InstanceId == 0 && match > 0 && generation > 0 && start > 0 && ValidMetricsRoster(roster);

    private static bool ValidMetricsStatus(SimTournamentMetricsStatus status, string error) =>
        error is not null && Strict.GetByteCount(error) <= 1024 &&
        (status == SimTournamentMetricsStatus.Error ? error.Length > 0 : status is SimTournamentMetricsStatus.Success or SimTournamentMetricsStatus.Unavailable && error.Length == 0);

    private static SimMessage ReadMetrics(ref Reader r, byte kind)
    {
        var id=r.U64(); var scene=ReadSceneKey(ref r); var match=r.I64(); var generation=r.I64(); var start=r.I64();
        var n=r.U16(); if (n is 0 or > 256 || n > r.Remaining / 17) throw new SimLinkException(SimLinkError.BadValue,"bad metrics roster count");
        var roster=new SimTournamentMetricsPilot[n];
        for (var i=0;i<n;i++) roster[i]=new(r.I64(),r.I64(),r.U8());
        if (!ValidMetricsIdentity(id,scene,match,generation,start,roster)) throw new SimLinkException(SimLinkError.BadValue,"bad metrics identity");
        if (kind==148) return new SimRegisterTournamentMetrics(id,scene,match,generation,start,roster);
        if (kind==150) return new SimReadTournamentMetrics(id,scene,match,generation,start,roster);
        if (kind==154) return new SimReadTournamentMetricsV2(id,scene,match,generation,start,roster);
        if (kind==152) return new SimReleaseTournamentMetrics(id,scene,match,generation,start,roster);
        var status=(SimTournamentMetricsStatus)r.U8(); var error=r.BoundedText(1024);
        if (!ValidMetricsStatus(status,error)) throw new SimLinkException(SimLinkError.BadValue,"bad metrics status");
        if (kind==149) return new SimRegisterTournamentMetricsReply(id,scene,match,generation,start,roster,status,error);
        if (kind==153) return new SimReleaseTournamentMetricsReply(id,scene,match,generation,start,roster,status,error);
        SimTournamentMetricsSample? sample=null;
        if (status==SimTournamentMetricsStatus.Success)
        {
            var at=r.I64(); var available=r.U8(); if (at < start || available > 1) throw new SimLinkException(SimLinkError.BadValue,"bad metrics time");
            double[]? damage=available==1 ? [r.F64(),r.F64()] : null;
            if (damage is not null && damage.Any(x => !double.IsFinite(x) || x < 0)) throw new SimLinkException(SimLinkError.BadValue,"bad applied damage");
            var pilots=r.U16(); if (pilots!=n) throw new SimLinkException(SimLinkError.BadValue,"bad metrics pilots");
            var results=new SimTournamentMetricsPilotSample[pilots]; var total=0;
            for (var i=0;i<pilots;i++)
            {
                var pilot=new SimTournamentMetricsPilot(r.I64(),r.I64(),r.U8()); if (pilot!=roster[i]) throw new SimLinkException(SimLinkError.BadValue,"metrics roster changed");
                var pilotState=(SimTournamentMetricsPilotState)r.U8(); if(pilotState>SimTournamentMetricsPilotState.Destroyed) throw new SimLinkException(SimLinkError.BadValue,"bad metrics pilot state");
                var fittedCount=r.U16(); total+=fittedCount; if (fittedCount>256 || total>8192 || fittedCount>r.Remaining/17) throw new SimLinkException(SimLinkError.BadValue,"bad fitted count");
                var fitted=new SimTournamentMetricsFitted[fittedCount];
                for (var j=0;j<fitted.Length;j++) { var item=r.I64(); var type=r.I32(); var flag=r.I32(); var heat=(SimTournamentMetricsHeat)r.U8(); if(item<=0 || type<=0 || heat>SimTournamentMetricsHeat.On) throw new SimLinkException(SimLinkError.BadValue,"bad fitted item"); fitted[j]=new(item,type,flag,heat); }
                var runningCount=r.U16(); total+=runningCount; if(runningCount>256 || total>8192 || runningCount>r.Remaining/22) throw new SimLinkException(SimLinkError.BadValue,"bad running count");
                var running=new SimTournamentMetricsRunning[runningCount];
                for(var j=0;j<running.Length;j++) { var item=r.I64(); var effect=r.I32(); var target=r.I64(); var cp=r.U8(); if(cp>1) throw new SimLinkException(SimLinkError.BadValue,"bad charge flag"); int? charge=cp==1?r.I32():null; var rp=r.U8(); if(rp>1) throw new SimLinkException(SimLinkError.BadValue,"bad rounds flag"); long? rounds=rp==1?r.I64():null; if(item<=0 || effect<=0 || target<0 || charge is <=0 || rounds is <0) throw new SimLinkException(SimLinkError.BadValue,"bad running item"); running[j]=new(item,effect,target,charge,rounds); }
                var companionCount=r.U16(); total+=companionCount; if(companionCount>256 || total>8192 || companionCount>r.Remaining/(kind==155 ? 26 : 25)) throw new SimLinkException(SimLinkError.BadValue,"bad companion count");
                var companions=new SimTournamentMetricsCompanion[companionCount];
                for(var j=0;j<companions.Length;j++)
                {
                    var ship=r.I64(); var type=r.I32(); var controller=r.I64(); var activity=r.I32(); var ck=(SimTournamentCompanionKind)r.U8();
                    int? members=null;
                    if (kind==155) { var present=r.U8(); if(present>1) throw new SimLinkException(SimLinkError.BadValue,"bad members flag"); if(present==1) members=r.I32(); }
                    if(ship<=0 || type<=0 || controller<=0 || ck>SimTournamentCompanionKind.Fighter || members is <0 or >64 || (ck==SimTournamentCompanionKind.Drone && members is not null)) throw new SimLinkException(SimLinkError.BadValue,"bad companion");
                    companions[j]=new(ship,type,controller,activity,ck,members);
                }
                var ehp=(SimTournamentMetricsEhp)r.U8(); if(ehp>SimTournamentMetricsEhp.Infinite) throw new SimLinkException(SimLinkError.BadValue,"bad EHP status"); double? value=ehp==SimTournamentMetricsEhp.Finite?r.F64():null;
                if(value is {} v && (!double.IsFinite(v) || v<0)) throw new SimLinkException(SimLinkError.BadValue,"bad EHP");
                if(fitted.Select(x=>x.ItemId).Distinct().Count()!=fitted.Length || running.Select(x=>x.ItemId).Distinct().Count()!=running.Length || companions.Select(x=>x.ShipId).Distinct().Count()!=companions.Length) throw new SimLinkException(SimLinkError.BadValue,"duplicate metrics item");
                if(pilotState==SimTournamentMetricsPilotState.Destroyed && (fitted.Length!=0 || running.Length!=0 || companions.Length!=0 || ehp!=SimTournamentMetricsEhp.Finite || value!=0)) throw new SimLinkException(SimLinkError.BadValue,"bad destroyed pilot evidence");
                results[i]=new(pilot,fitted,running,companions,ehp,value,pilotState);
            }
            sample=new(at,damage,results);
        }
        return kind == 155
            ? new SimReadTournamentMetricsV2Reply(id,scene,match,generation,start,roster,status,error,sample)
            : new SimReadTournamentMetricsReply(id,scene,match,generation,start,roster,status,error,sample);
    }

    private static void WriteMetrics(Writer output, SimMessage message)
    {
        var (kind,id,scene,match,generation,start,roster,status,error,sample) = message switch
        {
            SimRegisterTournamentMetrics m => ((byte)148,m.RequestId,m.Scene,m.MatchId,m.Generation,m.Start,m.Roster,(SimTournamentMetricsStatus?)null,(string?)null,(SimTournamentMetricsSample?)null),
            SimRegisterTournamentMetricsReply m => ((byte)149,m.RequestId,m.Scene,m.MatchId,m.Generation,m.Start,m.Roster,(SimTournamentMetricsStatus?)m.Status,m.Error,(SimTournamentMetricsSample?)null),
            SimReadTournamentMetrics m => ((byte)150,m.RequestId,m.Scene,m.MatchId,m.Generation,m.Start,m.Roster,(SimTournamentMetricsStatus?)null,(string?)null,(SimTournamentMetricsSample?)null),
            SimReadTournamentMetricsReply m => ((byte)151,m.RequestId,m.Scene,m.MatchId,m.Generation,m.Start,m.Roster,(SimTournamentMetricsStatus?)m.Status,m.Error,m.Sample),
            SimReadTournamentMetricsV2 m => ((byte)154,m.RequestId,m.Scene,m.MatchId,m.Generation,m.Start,m.Roster,(SimTournamentMetricsStatus?)null,(string?)null,(SimTournamentMetricsSample?)null),
            SimReadTournamentMetricsV2Reply m => ((byte)155,m.RequestId,m.Scene,m.MatchId,m.Generation,m.Start,m.Roster,(SimTournamentMetricsStatus?)m.Status,m.Error,m.Sample),
            SimReleaseTournamentMetrics m => ((byte)152,m.RequestId,m.Scene,m.MatchId,m.Generation,m.Start,m.Roster,(SimTournamentMetricsStatus?)null,(string?)null,(SimTournamentMetricsSample?)null),
            SimReleaseTournamentMetricsReply m => ((byte)153,m.RequestId,m.Scene,m.MatchId,m.Generation,m.Start,m.Roster,(SimTournamentMetricsStatus?)m.Status,m.Error,(SimTournamentMetricsSample?)null),
            _ => throw new ArgumentException("Invalid metrics message.",nameof(message))
        };
        if (!ValidMetricsIdentity(id,scene,match,generation,start,roster) || (status is {} st && !ValidMetricsStatus(st,error!)) ||
            (kind is 151 or 155 && ((status==SimTournamentMetricsStatus.Success) != (sample is not null))))
            throw new ArgumentException("Invalid metrics identity or status.",nameof(message));
        var w=new Writer(); w.U8(kind); w.U64(id); WriteSceneKey(w,scene); w.I64(match); w.I64(generation); w.I64(start); w.U16((ushort)roster.Count);
        foreach(var pilot in roster) { w.I64(pilot.CharacterId); w.I64(pilot.ShipId); w.U8(pilot.Team); }
        if(status is {} state) { w.U8((byte)state); w.Text(error!); }
        if(sample is not null)
        {
            if(sample.Pilots.Count!=roster.Count || (sample.AppliedDamage is not null && sample.AppliedDamage.Count!=2) ||
                sample.Pilots.Any(x=>x.Fitted.Count>256 || x.Running.Count>256 || x.Companions.Count>256) ||
                sample.Pilots.Sum(x=>x.Fitted.Count+x.Running.Count+x.Companions.Count)>8192)
                throw new ArgumentException("Invalid metrics sample size.",nameof(message));
            w.I64(sample.SampleAt); w.U8((byte)(sample.AppliedDamage is null ? 0 : 1));
            if(sample.AppliedDamage is {} damage) { w.F64(damage[0]); w.F64(damage[1]); }
            w.U16((ushort)sample.Pilots.Count);
            foreach(var p in sample.Pilots)
            {
                w.I64(p.Pilot.CharacterId); w.I64(p.Pilot.ShipId); w.U8(p.Pilot.Team); w.U8((byte)p.State); w.U16((ushort)p.Fitted.Count);
                foreach(var x in p.Fitted) { w.I64(x.ItemId); w.I32(x.TypeId); w.I32(x.Flag); w.U8((byte)x.Heat); }
                w.U16((ushort)p.Running.Count);
                foreach(var x in p.Running) { w.I64(x.ItemId); w.I32(x.EffectId); w.I64(x.TargetId); w.U8((byte)(x.ChargeTypeId is null ? 0:1)); if(x.ChargeTypeId is {} charge) w.I32(charge); w.U8((byte)(x.Rounds is null ? 0:1)); if(x.Rounds is {} rounds) w.I64(rounds); }
                w.U16((ushort)p.Companions.Count);
                foreach(var x in p.Companions)
                {
                    if (kind != 155 && x.Members is not null) throw new ArgumentException("Legacy metrics cannot encode fighter members.", nameof(message));
                    if (x.Members is < 0 or > 64 || (x.Kind == SimTournamentCompanionKind.Drone && x.Members is not null)) throw new ArgumentException("Invalid companion members.", nameof(message));
                    w.I64(x.ShipId); w.I32(x.TypeId); w.I64(x.ControllerShipId); w.I32(x.Activity); w.U8((byte)x.Kind);
                    if (kind == 155) { w.U8((byte)(x.Members is null ? 0 : 1)); if (x.Members is {} members) w.I32(members); }
                }
                w.U8((byte)p.EhpStatus); if(p.EhpStatus==SimTournamentMetricsEhp.Finite) w.F64(p.Ehp ?? double.NaN); else if(p.Ehp is not null) throw new ArgumentException("Unexpected EHP value.",nameof(message));
            }
        }
        var bytes=w.ToArray();
        if(bytes.Length>MaxFrame) throw new ArgumentException("Metrics frame exceeds the limit.",nameof(message));
        try { _=Decode(bytes); } catch(SimLinkException e) { throw new ArgumentException("Invalid metrics sample.",nameof(message),e); }
        output.Raw(bytes);
    }
}
