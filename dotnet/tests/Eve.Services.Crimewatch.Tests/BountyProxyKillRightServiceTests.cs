using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Crimewatch;

namespace Eve.Services.Crimewatch.Tests;

public sealed class BountyProxyKillRightServiceTests
{
    [Fact]
    public async Task GetMyKillRights_returns_the_reference_rows_from_the_wired_read_source()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var requested = new KillRight(1, 11, 22, 30000142, now, now + 30 * 24 * 60 * 60 * 1000L, 123.45m, 33);
        var other = requested with { KillRightId = 2, TargetCharacterId = 23 };
        long? readCharacter = null;
        var service = new BountyProxyKillRightService((characterId, _) =>
        {
            readCharacter = characterId;
            return Task.FromResult<IReadOnlyList<KillRight>>([requested, other]);
        });
        var session = new ClientSession { CharacterId = 11 };

        Assert.True(service.HasMethod("GetMyKillRights"));
        Assert.False(service.HasMethod("GetKillRights"));
        var result = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetMyKillRights",
            Args = [],
            Session = session,
        }));

        Assert.Equal(11L, readCharacter);
        Assert.Equal(2, result.Items.Count);
        var row = Assert.IsType<PyObject>(result.Items[0]);
        var fields = Assert.IsType<PyDict>(row.Args);
        Assert.Equal(
            ["killRightID", "fromID", "toID", "expiryTime", "price", "restrictedTo"],
            fields.Entries.Select(entry => Assert.IsType<PyStr>(entry.Key).Value));
        Assert.Contains(fields.Entries, pair => pair.Key == new PyStr("killRightID") && pair.Value == new PyLong(1));
        Assert.Contains(fields.Entries, pair => pair.Key == new PyStr("fromID") && pair.Value == new PyInt(11));
        Assert.Contains(fields.Entries, pair => pair.Key == new PyStr("toID") && pair.Value == new PyInt(22));
        Assert.Contains(fields.Entries, pair => pair.Key == new PyStr("restrictedTo") && pair.Value == new PyInt(33));
        Assert.Contains(fields.Entries, pair => pair.Key == new PyStr("price") && pair.Value == new PyReal(123.45));
    }

    [Fact]
    public async Task Unwired_bounty_proxy_rejects_calls_instead_of_returning_empty_successes()
    {
        var service = new BountyProxyKillRightService();
        var session = new ClientSession { CharacterId = 11 };
        ServiceCall Call(string method, params PyValue[] args) => new()
        {
            Method = method,
            Args = args,
            Session = session,
        };
        string[] methods =
        [
            "AddToBounty", "GetBounties", "GetBountiesAndKillRights", "GetMyBounties",
            "GetMyKillRights", "GetKillRightsOnCharacters", "SellKillRight", "CancelSellKillRight",
            "GetTopPilotBounties", "GetTopCorpBounties", "GetTopAllianceBounties",
            "GetTopPilotBountyHunters", "GetTopCorporationBountyHunters", "GetTopAllianceBountyHunters",
            "SearchCharBounties", "SearchCorpBounties", "SearchAllianceBounties",
            "GMReimburseBounties", "GMClearBountyCache",
        ];

        Assert.All(methods, method => Assert.True(service.HasMethod(method), method));
        Assert.False(service.HasMethod("GetKillRights"));

        async Task AssertCustomNotify(string method, params PyValue[] args)
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Call(method, args)).AsTask());
            Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        }

        await AssertCustomNotify("AddToBounty", new PyInt(90_000_001), new PyLong(1_000_000));
        await AssertCustomNotify("GetBounties", new PyList([new PyLong(90_000_001)]));
        await AssertCustomNotify("GetBountiesAndKillRights",
            new PyList([new PyInt(90_000_001)]), new PyList([new PyLong(90_000_001)]));
        await AssertCustomNotify("GetMyBounties");
        await AssertCustomNotify("GetMyKillRights");
        await AssertCustomNotify("GetKillRightsOnCharacters", new PyList([new PyLong(90_000_001)]));
        await AssertCustomNotify("SellKillRight", new PyLong(1), new PyReal(10), PyNone.Instance);
        await AssertCustomNotify("CancelSellKillRight", new PyLong(1), new PyInt(90_000_001));
        await AssertCustomNotify("GetTopPilotBounties", new PyInt(0));
        await AssertCustomNotify("GetTopCorpBounties", new PyInt(0));
        await AssertCustomNotify("GetTopAllianceBounties", new PyInt(0));
        await AssertCustomNotify("GetTopPilotBountyHunters", new PyInt(0));
        await AssertCustomNotify("GetTopCorporationBountyHunters", new PyInt(0));
        await AssertCustomNotify("GetTopAllianceBountyHunters", new PyInt(0));
        await AssertCustomNotify("SearchCharBounties", new PyLong(90_000_001));
        await AssertCustomNotify("SearchCorpBounties", new PyLong(98_000_001));
        await AssertCustomNotify("SearchAllianceBounties", new PyLong(99_000_001));
        await AssertCustomNotify("GMReimburseBounties");
        await AssertCustomNotify("GMClearBountyCache");
    }
}
