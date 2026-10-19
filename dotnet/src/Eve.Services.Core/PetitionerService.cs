using System.Globalization;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>Allows the host to route petitioner calls to a future ticket backend.</summary>
public delegate ValueTask<PyValue?> PetitionerRpcCallback(ServiceCall call);

/// <summary>

/// client-safe empty and default wire values; a callback may handle any route when wired.
/// </summary>
[Service("petitioner")]
public sealed partial class PetitionerService
{
    private const string HelpCenterUrl = "https://support.eveonline.com/hc/en-us";
    private readonly PetitionerRpcCallback? _callback;

    public PetitionerService() { }

    public PetitionerService(PetitionerRpcCallback callback) =>
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    [Method("AddPetitionRating")] public ValueTask<PyValue> AddPetitionRating(ServiceCall call) => RespondAsync(call);
    [Method("CancelPetition")] public ValueTask<PyValue> CancelPetition(ServiceCall call) => RespondAsync(call);
    [Method("ClaimPetition")] public ValueTask<PyValue> ClaimPetition(ServiceCall call) => RespondAsync(call);
    [Method("ClosePetition")] public ValueTask<PyValue> ClosePetition(ServiceCall call) => RespondAsync(call);
    [Method("CreatePetition")] public ValueTask<PyValue> CreatePetition(ServiceCall call) => RespondAsync(call);
    [Method("DeletePetition")] public ValueTask<PyValue> DeletePetition(ServiceCall call) => RespondAsync(call);
    [Method("EscalatePetition")] public ValueTask<PyValue> EscalatePetition(ServiceCall call) => RespondAsync(call);
    [Method("GetCategories")] public ValueTask<PyValue> GetCategories(ServiceCall call) => RespondAsync(call);
    [Method("GetCategoryHierarchicalInfo")] public ValueTask<PyValue> GetCategoryHierarchicalInfo(ServiceCall call) => RespondAsync(call);
    [Method("GetCategoryProperties")] public ValueTask<PyValue> GetCategoryProperties(ServiceCall call) => RespondAsync(call);
    [Method("GetClaimedPetitions")] public ValueTask<PyValue> GetClaimedPetitions(ServiceCall call) => RespondAsync(call);
    [Method("GetClientPickerInfo")] public ValueTask<PyValue> GetClientPickerInfo(ServiceCall call) => RespondAsync(call);
    [Method("GetLog")] public ValueTask<PyValue> GetLog(ServiceCall call) => RespondAsync(call);
    [Method("GetMyPetitionsEx")] public ValueTask<PyValue> GetMyPetitionsEx(ServiceCall call) => RespondAsync(call);
    [Method("GetPetitionMessages")] public ValueTask<PyValue> GetPetitionMessages(ServiceCall call) => RespondAsync(call);
    [Method("GetPetitionQueue")] public ValueTask<PyValue> GetPetitionQueue(ServiceCall call) => RespondAsync(call);
    [Method("GetQueues")] public ValueTask<PyValue> GetQueues(ServiceCall call) => RespondAsync(call);
    [Method("GetUnreadMessages")] public ValueTask<PyValue> GetUnreadMessages(ServiceCall call) => RespondAsync(call);
    [Method("GetUserCatalogCountry")] public ValueTask<PyValue> GetUserCatalogCountry(ServiceCall call) => RespondAsync(call);
    [Method("GetZendeskJwtLink")] public ValueTask<PyValue> GetZendeskJwtLink(ServiceCall call) => RespondAsync(call);
    [Method("IsZendeskEnabled")] public ValueTask<PyValue> IsZendeskEnabled(ServiceCall call) => RespondAsync(call);
    [Method("MarkAsRead")] public ValueTask<PyValue> MarkAsRead(ServiceCall call) => RespondAsync(call);
    [Method("MayPetition")] public ValueTask<PyValue> MayPetition(ServiceCall call) => RespondAsync(call);
    [Method("PetitioneeChat")] public ValueTask<PyValue> PetitioneeChat(ServiceCall call) => RespondAsync(call);
    [Method("PetitionerChat")] public ValueTask<PyValue> PetitionerChat(ServiceCall call) => RespondAsync(call);
    [Method("PropertyPopulationInfo")] public ValueTask<PyValue> PropertyPopulationInfo(ServiceCall call) => RespondAsync(call);
    [Method("UnClaimPetition")] public ValueTask<PyValue> UnClaimPetition(ServiceCall call) => RespondAsync(call);
    [Method("UpdatePetitionRating")] public ValueTask<PyValue> UpdatePetitionRating(ServiceCall call) => RespondAsync(call);

    // EveLogic also invokes these helpers directly from its support and notification UI.
    [Method("IsSerenity")] public ValueTask<PyValue> IsSerenity(ServiceCall call) => RespondAsync(call);
    [Method("IsZendeskSwapEnabled")] public ValueTask<PyValue> IsZendeskSwapEnabled(ServiceCall call) => RespondAsync(call);
    [Method("HasOpenTickets")] public ValueTask<PyValue> HasOpenTickets(ServiceCall call) => RespondAsync(call);

    private async ValueTask<PyValue> RespondAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (_callback is not null && await _callback(call).ConfigureAwait(false) is { } response)
            return response;
        return DefaultResponse(call);
    }

    private static PyValue DefaultResponse(ServiceCall call) => call.Method switch
    {
        "GetCategories" or "GetCategoryProperties" or "GetClaimedPetitions" or "GetClientPickerInfo" or
        "GetLog" or "GetMyPetitionsEx" or "GetPetitionMessages" or "GetPetitionQueue" or "GetQueues" or
        "GetUnreadMessages" or "PropertyPopulationInfo" => new PyList([]),
        "GetCategoryHierarchicalInfo" => new PyList([EmptyDict(), EmptyDict(), EmptyDict(), EmptyDict()]),
        "GetUserCatalogCountry" => new PyStr("US"),
        "GetZendeskJwtLink" => new PyStr(ZendeskLink(call)),
        "IsZendeskEnabled" or "IsZendeskSwapEnabled" => PyBool.True,
        "IsSerenity" or "HasOpenTickets" => PyBool.False,
        "CreatePetition" => PyBool.False,
        "ClaimPetition" => new PyInt(0),
        "MayPetition" => new PyInt(-4),
        _ => PyNone.Instance,
    };

    private static PyDict EmptyDict() => new([]);

    private static string ZendeskLink(ServiceCall call)
    {
        var ticketId = JsSemantics.NumberValue(call.Kwarg("ticketID") ?? PyNone.Instance);
        if (!double.IsFinite(ticketId) || ticketId <= 0)
            return $"{HelpCenterUrl}/requests/new";
        var id = Math.Truncate(ticketId).ToString("0", CultureInfo.InvariantCulture);
        return $"{HelpCenterUrl}/requests/{id}";
    }
}
