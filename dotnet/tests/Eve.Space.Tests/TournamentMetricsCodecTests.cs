using Eve.Space;
using Xunit;

namespace Eve.Space.Tests;

public sealed class TournamentMetricsCodecTests
{
    [Fact]
    public void Shared_v89_golden_frames_accept_valid_and_reject_invalid()
    {
        using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","simlink","tournament-metrics-v89.json")));
        var valid=document.RootElement.GetProperty("valid"); var invalid=document.RootElement.GetProperty("invalid");
        Assert.Equal(18,valid.GetArrayLength()); Assert.Equal(35,invalid.GetArrayLength());
        foreach(var entry in valid.EnumerateArray())
        {
            var frame=Convert.FromHexString(entry.GetProperty("hex").GetString()!);
            var message=SimCodec.Decode(frame.AsMemory(4));
            Assert.Equal(frame,SimCodec.Encode(message));
        }
        foreach(var entry in invalid.EnumerateArray())
        {
            var frame=Convert.FromHexString(entry.GetProperty("hex").GetString()!);
            Assert.Throws<SimLinkException>(()=>SimCodec.Decode(frame.AsMemory(4)));
        }
    }

    [Fact]
    public void Shared_v90_golden_frames_preserve_optional_fighter_members()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","simlink","tournament-metrics-v90.json")));
        var valid = document.RootElement.GetProperty("valid"); var invalid = document.RootElement.GetProperty("invalid");
        Assert.Equal(5, valid.GetArrayLength()); Assert.Equal(5, invalid.GetArrayLength());
        foreach (var entry in valid.EnumerateArray())
        {
            var frame = Convert.FromHexString(entry.GetProperty("hex").GetString()!);
            Assert.Equal(frame, SimCodec.Encode(SimCodec.Decode(frame.AsMemory(4))));
        }
        foreach (var entry in invalid.EnumerateArray())
        {
            var frame = Convert.FromHexString(entry.GetProperty("hex").GetString()!);
            Assert.Throws<SimLinkException>(() => SimCodec.Decode(frame.AsMemory(4)));
        }
        var scene = SimSceneKey.SolarSystem(30000142);
        SimTournamentMetricsPilot[] roster = [new(101,201,0)];
        SimTournamentMetricsSample Sample(int? members, SimTournamentCompanionKind kind = SimTournamentCompanionKind.Fighter) =>
            new(1001,null,[new(roster[0],[],[],[new(401,801,201,1,kind,members)],SimTournamentMetricsEhp.Unknown,null)]);
        foreach (int? members in new int?[] { null, 0, 64 })
        {
            var reply = new SimReadTournamentMetricsV2Reply(1,scene,3,4,1000,roster,SimTournamentMetricsStatus.Success,"",Sample(members));
            var decoded = Assert.IsType<SimReadTournamentMetricsV2Reply>(SimCodec.Decode(SimCodec.Encode(reply).AsMemory(4)));
            Assert.Equal(members, decoded.Sample!.Pilots[0].Companions[0].Members);
        }
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimReadTournamentMetricsV2Reply(1,scene,3,4,1000,roster,SimTournamentMetricsStatus.Success,"",Sample(65))));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimReadTournamentMetricsV2Reply(1,scene,3,4,1000,roster,SimTournamentMetricsStatus.Success,"",Sample(0, SimTournamentCompanionKind.Drone))));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimReadTournamentMetricsReply(1,scene,3,4,1000,roster,SimTournamentMetricsStatus.Success,"",Sample(0))));
    }

    [Fact]
    public void Metrics_messages_preserve_identity_and_reject_invalid_samples()
    {
        var scene=SimSceneKey.SolarSystem(30000142);
        SimTournamentMetricsPilot[] roster=[new(101,201,0),new(102,202,1)];
        var pilot0=new SimTournamentMetricsPilotSample(roster[0],[new(301,601,11,SimTournamentMetricsHeat.On)],
            [new(301,5,202,701,40)],[new(401,801,201,1,SimTournamentCompanionKind.Drone)],SimTournamentMetricsEhp.Finite,123.5);
        var pilot1=new SimTournamentMetricsPilotSample(roster[1],[],[],[],SimTournamentMetricsEhp.Unknown,null);
        var sample=new SimTournamentMetricsSample(1002,[3.5,7.25],[pilot0,pilot1]);
        SimMessage[] messages=[new SimRegisterTournamentMetrics(1,scene,3,4,1000,roster),
            new SimRegisterTournamentMetricsReply(2,scene,3,4,1000,roster,SimTournamentMetricsStatus.Success,""),
            new SimReadTournamentMetrics(3,scene,3,4,1000,roster),
            new SimReadTournamentMetricsReply(4,scene,3,4,1000,roster,SimTournamentMetricsStatus.Success,"",sample),
            new SimReadTournamentMetricsReply(5,scene,3,4,1000,roster,SimTournamentMetricsStatus.Unavailable,"",null),
            new SimReleaseTournamentMetrics(6,scene,3,4,1000,roster),
            new SimReleaseTournamentMetricsReply(7,scene,3,4,1000,roster,SimTournamentMetricsStatus.Error,"failed")];
        foreach(var message in messages)
        {
            var encoded=SimCodec.Encode(message);
            Assert.Equal(encoded,SimCodec.Encode(SimCodec.Decode(encoded.AsMemory(4))));
        }
        var decoded=Assert.IsType<SimReadTournamentMetricsReply>(SimCodec.Decode(SimCodec.Encode(messages[3]).AsMemory(4)));
        Assert.Equal(roster,decoded.Roster); Assert.NotNull(decoded.Sample);
        Assert.Equal(new[]{3.5,7.25},decoded.Sample.AppliedDamage); Assert.Equal(roster[0],decoded.Sample.Pilots[0].Pilot);
        Assert.Equal(pilot0.Fitted,decoded.Sample.Pilots[0].Fitted); Assert.Equal(pilot0.Running,decoded.Sample.Pilots[0].Running);
        Assert.Equal(pilot0.Companions,decoded.Sample.Pilots[0].Companions); Assert.Equal(123.5,decoded.Sample.Pilots[0].Ehp);
        Assert.Throws<ArgumentException>(()=>SimCodec.Encode(new SimRegisterTournamentMetrics(1,scene,3,4,1000,[roster[1],roster[0]])));
        Assert.Throws<ArgumentException>(()=>SimCodec.Encode(new SimReadTournamentMetricsReply(1,scene,3,4,1000,roster,SimTournamentMetricsStatus.Success,"",sample with { AppliedDamage=[double.NaN,0] })));
        Assert.Throws<ArgumentException>(()=>SimCodec.Encode(new SimReadTournamentMetricsReply(1,scene,3,4,1000,roster,SimTournamentMetricsStatus.Unavailable,"",sample)));
        Assert.Throws<ArgumentException>(()=>SimCodec.Encode(new SimRegisterTournamentMetricsReply(1,scene,3,4,1000,roster,SimTournamentMetricsStatus.Error,new string('x',1025))));
        var truncated=SimCodec.Encode(messages[3])[..^1]; Assert.Throws<SimLinkException>(()=>SimCodec.Decode(truncated.AsMemory(4)));
    }
}
