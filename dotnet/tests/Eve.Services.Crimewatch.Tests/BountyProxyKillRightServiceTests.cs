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
    public async Task Unwired_bounty_proxy_exposes_all_reference_methods_and_empty_shapes()
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

        var bounties = Assert.IsType<PyList>(await service.CallAsync(Call("GetBounties", new PyList([new PyInt(22)]))));
        var bountyEntry = Assert.IsType<PyTuple>(Assert.Single(bounties.Items));
        Assert.Equal(new PyInt(22), bountyEntry.Items[0]);
        var bounty = Assert.IsType<PyObject>(bountyEntry.Items[1]);
        var bountyFields = Assert.IsType<PyDict>(bounty.Args);
        Assert.Equal([new PyStr("targetID"), new PyStr("bounty")], bountyFields.Entries.Select(entry => entry.Key));
        Assert.Equal(new PyInt(22), bountyFields.Entries[0].Value);
        Assert.Equal(new PyInt(0), bountyFields.Entries[1].Value);

        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call("GetMyBounties"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call("GetMyKillRights"))).Items);
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call("GetKillRightsOnCharacters", new PyList([new PyInt(22)])))).Items);

        var combined = Assert.IsType<PyTuple>(await service.CallAsync(Call(
            "GetBountiesAndKillRights",
            new PyList([new PyInt(22)]),
            new PyList([new PyInt(22)]))));
        Assert.Single(Assert.IsType<PyList>(combined.Items[0]).Items);
        Assert.Empty(Assert.IsType<PyList>(combined.Items[1]).Items);

        foreach (var method in new[]
        {
            "GetTopPilotBounties", "GetTopCorpBounties", "GetTopAllianceBounties",
            "GetTopPilotBountyHunters", "GetTopCorporationBountyHunters", "GetTopAllianceBountyHunters",
        })
        {
            var ranked = Assert.IsType<PyTuple>(await service.CallAsync(Call(method)));
            Assert.Empty(Assert.IsType<PyList>(ranked.Items[0]).Items);
            Assert.IsType<PyLong>(ranked.Items[1]);
        }

        foreach (var method in new[] { "SearchCharBounties", "SearchCorpBounties", "SearchAllianceBounties" })
            Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call(method, new PyInt(22)))).Items);
        foreach (var method in new[] { "GMReimburseBounties", "GMClearBountyCache" })
            Assert.Same(PyNone.Instance, await service.CallAsync(Call(method)));
        await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(Call("AddToBounty", new PyInt(22), new PyInt(1))));
    }
}
