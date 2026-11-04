using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Tests;

public class GameMasterRolesTests
{
    [Fact]
    public void Grants_name_roles_by_their_client_bits()
    {
        var grants = GameMasterRoles.ParseGrants("alice=GML+GMH, bob = qa ");
        Assert.Equal(new (string, long)[] { ("alice", (1L << 54) | (1L << 53)), ("bob", 1L << 52) }, grants);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("alice=")]
    [InlineData("=GML")]
    [InlineData("alice=NOPE")]
    public void A_bad_grant_is_refused_at_start_up(string spec)
    {
        Assert.Throws<FormatException>(() => GameMasterRoles.ParseGrants(spec));
    }

    [Fact]
    public void Only_the_account_role_grants_the_gm_tools()
    {
        var player = new ClientSession();
        player.Set("role", new PyLong(GameMasterRoles.Gml)); // the client's own role is not a grant
        Assert.False(GameMasterRoles.Has(player, GameMasterRoles.Staff));
        player.Set("accountRole", new PyLong(GameMasterRoles.Gml));
        Assert.True(GameMasterRoles.Has(player, GameMasterRoles.Gml));
    }
}
