using Eve.Corporations;

namespace Eve.Corporations.Tests;

public sealed class CorporationRoleCompatibilityTests
{
    public static TheoryData<string, string, long> ClientRoles => new()
    {
        { "corpRoleDirector", nameof(CorporationRoles.Director), 1L },
        { "corpRoleDeliveriesHangarTake", nameof(CorporationRoles.DeliveriesHangarTake), 2L },
        { "corpRoleDeliveriesHangarQuery", nameof(CorporationRoles.DeliveriesHangarQuery), 4L },
        { "corpRolePersonnelManager", nameof(CorporationRoles.PersonnelManager), 128L },
        { "corpRoleAccountant", nameof(CorporationRoles.Accountant), 256L },
        { "corpRoleFactoryManager", nameof(CorporationRoles.FactoryManager), 1024L },
        { "corpRoleStationManager", nameof(CorporationRoles.StationManager), 2048L },
        { "corpRoleDiplomat", nameof(CorporationRoles.Diplomat), 1L << 34 },
        { "corpRoleBrandManager", nameof(CorporationRoles.BrandManager), 1L << 35 },
        { "corpRoleCanRentOffice", nameof(CorporationRoles.RentOffice), 1L << 49 },
        { "corpRoleJuniorAccountant", nameof(CorporationRoles.JuniorAccountant), 1L << 52 },
        { "corpRoleTrader", nameof(CorporationRoles.Trader), 1L << 54 },
        { "corpRoleChatManager", nameof(CorporationRoles.ChatManager), 1L << 55 },
    };

    [Theory]
    [MemberData(nameof(ClientRoles))]
    public void Named_roles_match_v24_01_client_constants(string clientName, string memberName, long expected)
    {
        Assert.StartsWith("corpRole", clientName);
        var field = typeof(CorporationRoles).GetField(memberName);
        Assert.NotNull(field);
        Assert.Equal(expected, field!.GetRawConstantValue());
    }

    [Theory]
    [InlineData("corpRoleAccountCanTake1", 1000, 1L << 27)]
    [InlineData("corpRoleAccountCanTake2", 1001, 1L << 28)]
    [InlineData("corpRoleAccountCanTake3", 1002, 1L << 29)]
    [InlineData("corpRoleAccountCanTake4", 1003, 1L << 30)]
    [InlineData("corpRoleAccountCanTake5", 1004, 1L << 31)]
    [InlineData("corpRoleAccountCanTake6", 1005, 1L << 32)]
    [InlineData("corpRoleAccountCanTake7", 1006, 1L << 33)]
    public void Wallet_division_roles_match_v24_01_client_constants(string clientName, int accountKey, long expected)
    {
        Assert.StartsWith("corpRoleAccountCanTake", clientName);
        Assert.Equal(expected, CorporationRoles.WalletTakeRole(accountKey));
    }

    [Theory]
    [InlineData(62)]
    public void Delivery_hangar_flags_use_the_client_query_and_take_roles(int flag)
    {
        var query = new SessionRoles(CorporationRoles.DeliveriesHangarQuery, CorporationRoles.DeliveriesHangarQuery,
            0, 0, 0, null, null, 1000);
        var take = new SessionRoles(CorporationRoles.DeliveriesHangarTake, CorporationRoles.DeliveriesHangarTake,
            0, 0, 0, null, null, 1000);

        Assert.True(CorporationRoles.HasHangarAccess(query, 60000001, flag, take: false));
        Assert.False(CorporationRoles.HasHangarAccess(query, 60000001, flag, take: true));
        Assert.False(CorporationRoles.HasHangarAccess(take, 60000001, flag, take: false));
        Assert.True(CorporationRoles.HasHangarAccess(take, 60000001, flag, take: true));
    }
}
