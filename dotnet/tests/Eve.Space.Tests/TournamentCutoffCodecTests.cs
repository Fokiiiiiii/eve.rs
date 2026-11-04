namespace Eve.Space.Tests;

public sealed class TournamentCutoffCodecTests
{
    [Fact]
    public void Cutoff_messages_preserve_identity_and_reject_invalid_evidence()
    {
        var scene = SimSceneKey.SolarSystem(30_000_142);
        var registration = new SimRegisterTournamentCutoff(1, scene, 2, 3, 1000, 2000, [new(101,201),new(102,202)]);
        var copy = Assert.IsType<SimRegisterTournamentCutoff>(SimCodec.Decode(SimCodec.Encode(registration).AsMemory(4)));
        Assert.Equal(registration.Roster, copy.Roster); Assert.Equal(registration.Scene, copy.Scene); Assert.Equal(registration.Start, copy.Start);
        SimMessage[] messages = [new SimRegisterTournamentCutoffReply(1,scene,2,3,1000,2000,SimTournamentCutoffStatus.Pending,""),
            new SimReadTournamentCutoff(2,scene,2,3,1000,2000),
            new SimReleaseTournamentCutoff(3,scene,2,3,1000,2000),
            new SimReleaseTournamentCutoffReply(3,scene,2,3,1000,2000,SimTournamentCutoffStatus.Pending,""),
            new SimReleaseTournamentCutoffReply(4,scene,2,3,1000,2000,SimTournamentCutoffStatus.Error,"CONFLICT"),
            new SimReadTournamentCutoffReply(2,scene,2,3,1000,2000,SimTournamentCutoffStatus.Unavailable,[],""),
            new SimRegisterTournamentCutoffReply(1,scene,2,3,1000,2000,SimTournamentCutoffStatus.Error,"CONFLICT")];
        foreach (var message in messages)
        {
            var decoded = SimCodec.Decode(SimCodec.Encode(message).AsMemory(4));
            if (message is SimReadTournamentCutoffReply expected)
            {
                var actual = Assert.IsType<SimReadTournamentCutoffReply>(decoded);
                Assert.Equal(expected with { Evidence = actual.Evidence }, actual); Assert.Equal(expected.Evidence,actual.Evidence);
            }
            else Assert.Equal(message,decoded);
        }
        var ready = new SimReadTournamentCutoffReply(2,scene,2,3,1000,2000,SimTournamentCutoffStatus.Ready,[new(101,201,SimTournamentCutoffEvidence.Alive),new(102,202,SimTournamentCutoffEvidence.Unknown)],"");
        var readyCopy = Assert.IsType<SimReadTournamentCutoffReply>(SimCodec.Decode(SimCodec.Encode(ready).AsMemory(4)));
        Assert.Equal(ready.Evidence,readyCopy.Evidence);
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(registration with { Roster = [new(101,201),new(101,202)] }));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(ready with { Status = SimTournamentCutoffStatus.Pending }));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(ready with { Evidence = [new(101,201,(SimTournamentCutoffEvidence)3)] }));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(ready with { Error = "unexpected" }));
    }
}
