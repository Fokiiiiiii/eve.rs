using Eve.Services.Space.Sites;

namespace Eve.Services.Space.Tests;

/// <summary>The container type names of the static data: a name that two types share resolves to one of them, without an error.</summary>
public sealed class SiteContainerTypesTests
{
    [Fact]
    public void Two_unpublished_types_with_one_name_resolve_to_the_lowest_id_and_the_other_is_reported()
    {
        // the static data's Secure Depot: 33530 and 57436 are both unpublished containers of group 306
        var warnings = new List<string>();
        var byName = SiteContainerTypes.ByName(
        [
            new SiteContainerType("Secure Depot", 57436, 306, false),
            new SiteContainerType("Secure Depot", 33530, 306, false),
        ], warnings.Add);

        Assert.Equal((33530, 306), byName["Secure Depot"]);
        var warning = Assert.Single(warnings);
        Assert.Contains("'Secure Depot'", warning);
        Assert.Contains("57436", warning);
    }

    [Fact]
    public void A_published_type_wins_over_a_lower_unpublished_type_with_the_same_name()
    {
        var warnings = new List<string>();
        var byName = SiteContainerTypes.ByName(
        [
            new SiteContainerType("Research Cache", 100, 306, false),
            new SiteContainerType("Research Cache", 200, 314, true),
        ], warnings.Add);

        Assert.Equal((200, 314), byName["Research Cache"]);
        Assert.Contains("100", Assert.Single(warnings));
    }

    [Fact]
    public void A_name_that_one_type_holds_is_kept_without_a_warning()
    {
        var warnings = new List<string>();
        var byName = SiteContainerTypes.ByName([new SiteContainerType("Human Containment Facility", 48133, 306, false)], warnings.Add);

        Assert.Equal((48133, 306), byName["Human Containment Facility"]);
        Assert.Empty(warnings);
    }
}
