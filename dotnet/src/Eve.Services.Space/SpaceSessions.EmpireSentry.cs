using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>Reports a local illegal attack to the simulation while its attacker is still in that public solar-system scene.</summary>
    public void ReportEmpireSentryOffense(long characterId, long solarSystemId)
    {
        if (characterId <= 0 || solarSystemId is <= 0 or > int.MaxValue)
            return;

        var pilot = _pilots.Values.FirstOrDefault(candidate =>
            candidate.CharacterId == characterId &&
            candidate.SolarSystemId == solarSystemId &&
            candidate.SimulationScene.InstanceId == 0);
        if (pilot is not null)
            _sim.EmpireSentryOffense((ulong)pilot.Session.SessionId);
    }
}
