using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

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
