using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

[Service("alphaInjectorMgr")]
public sealed partial class AlphaInjectorMgrService(PgSkillStore store, SkillCatalog catalog)
{
    [Method("GetNextAvailableInjection")]
    public async ValueTask<PyValue> GetNextAvailableInjection(ServiceCall call)
    {
        var validKwargs = call.Kwargs is PyNone || call.Kwargs is PyDict kwargs &&
            kwargs.Entries.All(entry => entry.Key is PyStr { Value: "machoVersion" } && entry.Value is (PyInt or PyLong));
        if (call.Args.Count != 0 || !validKwargs)
            throw MachoErrors.ThrowUserError("ALPHA_INJECTOR_ARGUMENTS_INVALID");
        if (call.Session.CharacterId is not { } character || catalog.ItemTypeId("Daily Alpha Injector") is not { } typeId
            || await store.DailyAlphaSkillInjectorUsedAtAsync(character, typeId, call.Cancellation) is not { } usedAt)
            return new PyLong(0);
        return new PyLong(Py.FileTimeFromUnixMs(SkillInjectorDowntime.NextAfterMs(usedAt)));
    }
}
