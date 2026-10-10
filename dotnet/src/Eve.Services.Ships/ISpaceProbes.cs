using Eve.Macho;
using Eve.Ships;

namespace Eve.Services.Ships;

/// <summary>
/// What the scanner probes ask of space (EVE.js <c>dogmaIM.LaunchProbes</c> and <c>scanMgr.DestroyProbe</c>): a launch from a probe launcher of the ship the session
/// flies, and the recall of a probe. Implemented where the ships are in space (<c>SpaceSessions</c>).
/// </summary>
public interface ISpaceProbes
{
    /// <summary>Launches <paramref name="count"/> probes from the launcher <paramref name="moduleId"/>. Throws the reference's user error when it cannot.</summary>
    Task LaunchAsync(ClientSession session, long moduleId, int count, CancellationToken cancellationToken);

    /// <summary>Recalls a probe the pilot controls: it leaves space and the pilot is told (<c>OnRemoveProbe</c>). Anything else is left alone.</summary>
    Task RecallAsync(ClientSession session, long probeId, CancellationToken cancellationToken);
}
