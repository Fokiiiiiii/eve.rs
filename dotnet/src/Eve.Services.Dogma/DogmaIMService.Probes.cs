using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;

namespace Eve.Services.Dogma;

/// <summary>
/// The scanner probe launch (<c>LaunchProbes(moduleID, count)</c>). The probes and the checks are the ships' and space's
/// (<see cref="ISpaceProbes"/>); this puts the call to them.
/// </summary>
public sealed partial class DogmaIMService
{
    [Method("LaunchProbes")]
    public async ValueTask<PyValue> LaunchProbes(ServiceCall call)
    {
        if (BoundContextOf(call) is null || call.Session.CharacterId is null || IdOf(call.Arg(0)) is not { } moduleId)
        {
            return None;
        }
        var count = (int)Math.Clamp(IdOf(call.Arg(1)) ?? 1, 1, 1000);
        if (probes is null)
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Scanner probes are not wired on this server.")));
        }
        await probes.LaunchAsync(call.Session, moduleId, count, call.Cancellation);
        return None;
    }
}
