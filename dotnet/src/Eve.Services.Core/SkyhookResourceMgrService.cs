namespace Eve.Services.Core;
using Eve.Marshal;
using Eve.Macho;

/// <summary>
/// EVE.js v0.12.9 lookupSvc owner lookup methods, pinned to
/// 828823a75f211fdecb945b087a26c225f111491f.
/// </summary>
public sealed class SkyhookResourceMgrService : IService
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "GetMyCorporationSkyhooks",
    };

    private readonly SkyhookResourceService resources;

    public SkyhookResourceMgrService(SkyhookResourceService resources)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public string Name => "colonyResourcesMgr";

    public bool HasMethod(string method) => Methods.Contains(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (call.Method != "GetMyCorporationSkyhooks")
            return PyNone.Instance;

        var corporationId = call.Session.GetInt("corporationID") ?? 0;
        if (corporationId == 0)
            corporationId = call.Session.GetInt("corpid") ?? 0;
        if (corporationId <= 0)
            return new PyList([]);

        var result = await resources.GetAllByCorporationAsync(call, corporationId);
        if (result.Code != SkyhookResourceResultCode.Ok)
        {
            var message = result.Code switch
            {
                SkyhookResourceResultCode.NotFound => "Skyhook not found.",
                SkyhookResourceResultCode.Forbidden => "You cannot access corporation skyhooks.",
                SkyhookResourceResultCode.PersistenceFailure => "Skyhook data could not be loaded.",
                _ => "Skyhook request failed.",
            };
            throw MachoErrors.ThrowUserError(
                "CustomNotify",
                ("notify", new PyStr(message)));
        }

        var rows = result.Value?
            .OrderBy(skyhook => skyhook.SolarSystemId)
            .ThenBy(skyhook => skyhook.PlanetId)
            .ThenBy(skyhook => skyhook.SkyhookId)
            .Select(SerializeSkyhook)
            .ToArray() ?? [];
        return new PyList(rows);
    }

    private static PyValue SerializeSkyhook(SkyhookResourceSnapshot skyhook)
    {
        // Production activation and theft vulnerability do not define the structure state.
        var state = skyhook.State is >= 210 and <= 214 ? skyhook.State.Value : 0;
        var allianceId = skyhook.AllianceId is > 0 ? skyhook.AllianceId.Value : 0;
        return Py.KeyVal(
            ("skyhookID", new PyInt(skyhook.SkyhookId)),
            ("itemID", new PyInt(skyhook.SkyhookId)),
            ("solarSystemID", new PyInt(skyhook.SolarSystemId)),
            ("planetID", new PyInt(skyhook.PlanetId)),
            ("typeID", new PyInt(skyhook.TypeId)),
            ("state", new PyInt(state)),
            ("corporationID", new PyInt(skyhook.CorporationId)),
            ("ownerCorpID", new PyInt(skyhook.CorporationId)),
            ("allianceID", new PyInt(allianceId)));
    }
}
