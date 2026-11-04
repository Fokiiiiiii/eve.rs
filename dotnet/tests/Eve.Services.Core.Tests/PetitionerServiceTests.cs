using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class PetitionerServiceTests
{
    private static readonly string[] Methods =
    [
        "AddPetitionRating", "CancelPetition", "ClaimPetition", "ClosePetition", "CreatePetition",
        "DeletePetition", "EscalatePetition", "GetCategories", "GetCategoryHierarchicalInfo",
        "GetCategoryProperties", "GetClaimedPetitions", "GetClientPickerInfo", "GetLog",
        "GetMyPetitionsEx", "GetPetitionMessages", "GetPetitionQueue", "GetQueues", "GetUnreadMessages",
        "GetUserCatalogCountry", "GetZendeskJwtLink", "IsZendeskEnabled", "MarkAsRead", "MayPetition",
        "PetitioneeChat", "PetitionerChat", "PropertyPopulationInfo", "UnClaimPetition", "UpdatePetitionRating",
        "IsSerenity", "IsZendeskSwapEnabled", "HasOpenTickets",
    ];

    [Fact]
    public async Task Petitioner_rpcs_match_EveJS_unwired_wire_shapes()
    {
        IService service = new PetitionerService();

        Assert.Equal("petitioner", service.Name);
        Assert.All(Methods, method => Assert.True(service.HasMethod(method), method));

        foreach (var method in new[]
        {
            "GetCategories", "GetCategoryProperties", "GetClaimedPetitions", "GetClientPickerInfo",
            "GetLog", "GetMyPetitionsEx", "GetPetitionMessages", "GetPetitionQueue", "GetQueues",
            "GetUnreadMessages", "PropertyPopulationInfo",
        })
        {
            var rows = Assert.IsType<PyList>(await service.CallAsync(Call(method)));
            Assert.Empty(rows.Items);
        }

        var hierarchy = Assert.IsType<PyList>(await service.CallAsync(Call("GetCategoryHierarchicalInfo")));
        Assert.Equal(4, hierarchy.Items.Count);
        Assert.All(hierarchy.Items, value => Assert.Empty(Assert.IsType<PyDict>(value).Entries));

        Assert.Equal(PyBool.False, await service.CallAsync(Call("CreatePetition")));
        Assert.Equal(new PyInt(0), await service.CallAsync(Call("ClaimPetition")));
        Assert.Equal(new PyInt(-4), await service.CallAsync(Call("MayPetition")));
        Assert.Equal(new PyStr("US"), await service.CallAsync(Call("GetUserCatalogCountry")));
        Assert.Equal(PyBool.True, await service.CallAsync(Call("IsZendeskEnabled")));
        Assert.Equal(PyBool.False, await service.CallAsync(Call("IsSerenity")));
        Assert.Equal(PyBool.True, await service.CallAsync(Call("IsZendeskSwapEnabled")));
        Assert.Equal(PyBool.False, await service.CallAsync(Call("HasOpenTickets")));
        Assert.Equal(new PyStr("https://support.eveonline.com/hc/en-us/requests/new"),
            await service.CallAsync(Call("GetZendeskJwtLink")));
        Assert.Equal(new PyStr("https://support.eveonline.com/hc/en-us/requests/321"),
            await service.CallAsync(Call("GetZendeskJwtLink", kwargs: new PyDict([new(new PyStr("ticketID"), new PyInt(321))]))));

        foreach (var method in new[]
        {
            "AddPetitionRating", "CancelPetition", "ClosePetition", "DeletePetition", "EscalatePetition",
            "MarkAsRead", "PetitioneeChat", "PetitionerChat", "UnClaimPetition", "UpdatePetitionRating",
        })
            Assert.IsType<PyNone>(await service.CallAsync(Call(method)));
    }

    [Fact]
    public async Task Petitioner_callback_can_supply_backend_results_for_future_wiring()
    {
        var expected = new PyStr("wired result");
        ServiceCall? received = null;
        IService service = new PetitionerService(call =>
        {
            received = call;
            return ValueTask.FromResult<PyValue?>(expected);
        });
        var request = Call("GetMyPetitionsEx");

        Assert.Same(expected, await service.CallAsync(request));
        Assert.Same(request, received);
    }

    private static ServiceCall Call(string method, PyValue[]? args = null, PyValue? kwargs = null) => new()
    {
        Method = method,
        Args = args ?? [],
        Kwargs = kwargs ?? PyNone.Instance,
        Session = new ClientSession { CharacterId = 42 },
    };
}
