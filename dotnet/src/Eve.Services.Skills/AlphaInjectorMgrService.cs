using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>
/// The Alpha clone's daily injector clock (EveJS <c>alphaInjectorMgr</c>): the filetime of the first downtime after the character's last
/// Daily Alpha Injector use, or 0 when it never used one. The client shows the time left from it.
/// </summary>
[Service("alphaInjectorMgr")]
public sealed partial class AlphaInjectorMgrService(PgSkillStore store, SkillCatalog catalog)
{
    [Method("GetNextAvailableInjection")]
    public async ValueTask<PyValue> GetNextAvailableInjection(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character || catalog.ItemTypeId("Daily Alpha Injector") is not { } typeId
            || await store.DailyAlphaSkillInjectorUsedAtAsync(character, typeId, call.Cancellation) is not { } usedAt)
            return new PyLong(0);
        return new PyLong(Py.FileTimeFromUnixMs(SkillInjectorDowntime.NextAfterMs(usedAt)));
    }
}
