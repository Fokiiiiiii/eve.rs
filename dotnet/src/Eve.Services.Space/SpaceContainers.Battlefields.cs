using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The balls of the factional warfare battlefields in space (EVE.js: the capture points and the scoreboard of a battlefield). The host calls these on each
/// battlefield tick; the simulation places a ball once and ignores a repeat, and sets a slim field only when its value changes.
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>Puts a battlefield's capture point in its system's space, with its attacker and occupier factions (protocol 77).</summary>
    public void PlaceBattlefieldPoint(long pointId, int solarSystemId, string name, long typeId, long groupId, IReadOnlyList<double> position, double radius,
        long attackerFactionId, long occupierFactionId) =>
        _sim.PlaceCapturePoint(new SimPlaceCapturePoint(pointId, solarSystemId, name, (int)typeId, (int)groupId, position, radius, (int)attackerFactionId,
            (int)occupierFactionId, 0));

    /// <summary>Sets a capture point's next tick time (a FILETIME), or clears it (0): the client's tick countdown.</summary>
    public void SetBattlefieldPointNextTick(long pointId, long fileTime) =>
        _sim.SetSlimField(pointId, "component_fwCapturePoint_nextPointTime", fileTime == 0 ? new SimSlimNone() : new SimSlimInt(fileTime));

    /// <summary>Puts a battlefield's scoreboard in its system's space (protocol 78); its score starts at none.</summary>
    public void PlaceBattlefieldScoreboard(long scoreboardId, int solarSystemId, string name, long typeId, long groupId, IReadOnlyList<double> position) =>
        _sim.PlaceBattlefieldScoreboard(new SimPlaceBattlefieldScoreboard(scoreboardId, solarSystemId, name, (int)typeId, (int)groupId, position, 0));

    /// <summary>Sets a battlefield's score: negative for the occupier's side, positive for the attackers', within the scoreboard's thresholds.</summary>
    public void SetBattlefieldScore(long scoreboardId, long score) =>
        _sim.SetSlimField(scoreboardId, "component_fwScoreboard_score", new SimSlimInt(score));

    /// <summary>Takes a battlefield's ball out of space (its battle is over).</summary>
    public void RemoveBattlefieldBall(long deployableId) => _sim.RemoveDeployed(deployableId);
}
