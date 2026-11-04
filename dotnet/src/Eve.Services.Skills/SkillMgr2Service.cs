using Eve.Marshal;

namespace Eve.Services.Skills;

/// <summary>The door to <c>skillHandler</c>: the client asks for its handler once and calls it as a bound service.</summary>
[Service("skillMgr2")]
public sealed partial class SkillMgr2Service
{
    /// <summary>A moniker <c>("skillHandler", node, bindParams, sessionCheck)</c> for the calling character.</summary>
    [Method("GetMySkillHandler")]
    public PyValue GetMySkillHandler(ServiceCall call)
    {
        var owner = call.Session.CharacterId ?? call.Session.UserId;
        return new PyObject(
            new PyStr("carbon.common.script.net.moniker.Moniker"),
            Py.Tuple(new PyStr("skillHandler"), PyNone.Instance, owner is { } id ? new PyInt(id) : PyNone.Instance, PyNone.Instance));
    }
}
