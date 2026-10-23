using Eve.Marshal;
using Eve.Macho;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>Client contract for industry facility discovery. The host callback can wire SDE, stations and deployed structures.</summary>
public sealed class FacilityManagerService(
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? rpcHandler = null) : IService
{
    public string Name => "facilityManager";

    public bool HasMethod(string method) => method is
        "GetFacilities" or
        "GetFacility" or
        "GetFacilitiesByID" or
        "GetMaxActivityModifiers" or
        "GetFacilityTaxes" or
        "SetFacilityTaxes" or
        "GetFacilityLocations";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (rpcHandler is not null && await rpcHandler(call, call.Cancellation) is { } response)
            return response;

        return call.Method switch
        {
            "GetFacilities" or "GetFacilitiesByID" or "GetFacilityLocations" => new PyList([]),
            "GetFacility" => throw MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyStr("That facility could not be found."))),
            "GetFacilityTaxes" => Py.KeyVal(
                ("taxCorporation", PyNone.Instance),
                ("taxAlliance", PyNone.Instance),
                ("taxStandingsHorrible", PyNone.Instance),
                ("taxStandingsBad", PyNone.Instance),
                ("taxStandingsNeutral", PyNone.Instance),
                ("taxStandingsGood", PyNone.Instance),
                ("taxStandingsHigh", PyNone.Instance)),
            "GetMaxActivityModifiers" => new PyDict([]),
            "SetFacilityTaxes" => throw MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyStr("Facility tax settings are not available here."))),
            _ => PyNone.Instance,
        };
    }
}