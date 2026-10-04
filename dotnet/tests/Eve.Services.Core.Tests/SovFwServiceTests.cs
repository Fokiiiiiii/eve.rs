using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class SovFwServiceTests
{
    [Fact]
    public void Registers_the_EveJS_read_endpoints_without_write_methods()
    {
        var (services, _) = CreateServices(SovFwReadSnapshot.Empty);
        Assert.Equal(["fwWarzoneSolarsystem", "sovMgr"], services.Select(service => service.Name).Order());

        var sov = services.Single(service => service.Name == "sovMgr");
        Assert.All(
            [
                "GetSovStructuresInfoForLocalSolarSystem",
                "GetSovStructuresInfoForSolarSystem",
                "GetSystemSovereigntyInfo",
                "GetInfrastructureHubInfo",
                "GetSovHubFuelAccessGroup",
                "IsOnLocalSovHubFuelAccessGroup",
            ], method => Assert.True(sov.HasMethod(method)));
        Assert.False(sov.HasMethod("SetSovHubFuelAccessGroup"));
        Assert.False(sov.HasMethod("DestroySkyhooks"));
        Assert.False(sov.HasMethod("AcquireSkyhooks"));

        var fw = services.Single(service => service.Name == "fwWarzoneSolarsystem");
        Assert.All(
            [
                "GetAllWarzonesOccupationStates",
                "GetAllWarzonesOccupationStatesUncached",
                "GetAllWarzones",
                "GetHQSystemIDs",
                "GetLocalOccupationState",
            ], method => Assert.True(fw.HasMethod(method)));
        Assert.False(fw.HasMethod("SetOccupationState"));
    }

    [Fact]
    public async Task SovMgr_returns_persisted_claim_hub_and_structure_values_in_EVE_wire_types()
    {
        const long systemId = 30000142;
        var structure = new SovStructureRecord(
            ItemId: 1020001,
            TypeId: 32226,
            OwnerId: 0,
            CorporationId: 98000001,
            AllianceId: 99000001,
            SolarSystemId: systemId,
            CampaignEventType: 4,
            CampaignStartTimeFileTime: 13370000,
            CampaignScoresByTeam: new Dictionary<long, double> { [1] = 4.5, [2] = 2 },
            VulnerableStartTimeFileTime: 14440000,
            VulnerableEndTimeFileTime: 15550000,
            DefenseMultiplier: 1.25,
            IsCapital: true);
        var snapshot = Snapshot(
            claims: new Dictionary<long, SovClaimRecord>
            {
                [systemId] = new(1020001, 98000001, 99000001),
            },
            hubs: new Dictionary<long, SovHubRecord>
            {
                [systemId] = new(1020002, 98000002, 99000002, 16600000),
            },
            structures: new Dictionary<long, IReadOnlyList<SovStructureRecord>>
            {
                [systemId] = [structure],
            });
        var (services, _) = CreateServices(snapshot);
        var sov = services.Single(service => service.Name == "sovMgr");
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt(systemId));

        var claim = Assert.IsType<PyObjectEx>(await sov.CallAsync(Call("GetSystemSovereigntyInfo", [new PyInt(systemId)])));
        Assert.Equal(ObjectExKind.Ex1, claim.Kind);
        var claimHeader = Assert.IsType<PyTuple>(claim.Header);
        Assert.Equal(new PyToken("sovereignty.data_types.SovClaimInfo"), claimHeader.Items[0]);
        Assert.Equal(
            new PyList([new PyInt(1020001), new PyInt(98000001), new PyInt(99000001)]),
            claimHeader.Items[1]);

        var hub = Assert.IsType<PyObjectEx>(await sov.CallAsync(Call("GetInfrastructureHubInfo", [new PyInt(systemId)])));
        var hubHeader = Assert.IsType<PyTuple>(hub.Header);
        Assert.Equal(new PyToken("sovereignty.data_types.SovHubInfo"), hubHeader.Items[0]);
        Assert.Equal(
            new PyList([new PyInt(1020002), new PyInt(98000002), new PyInt(99000002), new PyLong(16600000)]),
            hubHeader.Items[1]);

        var structures = Assert.IsType<PyList>(await sov.CallAsync(Call("GetSovStructuresInfoForLocalSolarSystem", session: session)));
        var keyVal = Assert.IsType<PyObject>(Assert.Single(structures.Items));
        Assert.Equal(new PyStr("util.KeyVal"), keyVal.Name);
        var fields = Assert.IsType<PyDict>(keyVal.Args);
        Assert.Equal(PyNone.Instance, Get(fields, "ownerID"));
        Assert.Equal(new PyInt(1020001), Get(fields, "itemID"));
        Assert.Equal(new PyInt(32226), Get(fields, "typeID"));
        Assert.Equal(new PyInt(systemId), Get(fields, "solarSystemID"));
        Assert.Equal(new PyReal(1.25), Get(fields, "defenseMultiplier"));
        Assert.Equal(PyBool.True, Get(fields, "isCapital"));

        var campaign = Assert.IsType<PyList>(Get(fields, "campaignState"));
        Assert.Equal(new PyInt(4), campaign.Items[0]);
        Assert.Equal(new PyInt(99000001), campaign.Items[1]);
        Assert.Equal(new PyLong(13370000), campaign.Items[2]);
        var scores = Assert.IsType<PyDict>(campaign.Items[3]);
        Assert.Equal(new PyReal(4.5), Get(scores, new PyInt(1)));
        Assert.Equal(new PyInt(2), Get(scores, new PyInt(2)));
        Assert.Equal(
            new PyList([new PyLong(14440000), new PyLong(15550000)]),
            Get(fields, "vulnerabilityState"));
    }

    [Fact]
    public async Task Missing_Sov_state_uses_the_client_expected_None_and_empty_list_shapes()
    {
        var (services, _) = CreateServices(SovFwReadSnapshot.Empty);
        var sov = services.Single(service => service.Name == "sovMgr");
        var call = Call("GetSystemSovereigntyInfo", [new PyInt(30000142)]);

        Assert.Same(PyNone.Instance, await sov.CallAsync(call));
        Assert.Same(PyNone.Instance, await sov.CallAsync(Call("GetInfrastructureHubInfo", [new PyInt(30000142)])));
        Assert.Same(PyNone.Instance, await sov.CallAsync(Call("GetSovHubFuelAccessGroup", [new PyInt(30000142)])));
        Assert.Equal(new PyList([]), await sov.CallAsync(Call("GetSovStructuresInfoForSolarSystem", [new PyInt(30000142)])));
    }

    [Fact]
    public async Task FW_read_endpoints_return_static_warzones_and_only_valid_persisted_occupiers()
    {
        var snapshot = Snapshot(occupations:
        [
            new(1, 30000142, 500003),
            new(1, 30000142, 500002), // duplicate system: first persisted entry wins, as in EVE.js
            new(1, 30000143, 500001), // faction is not a member of warzone 1
            new(99, 30000144, 500003), // unknown warzone
            new(2, 30000145, 500004),
        ]);
        var (services, _) = CreateServices(snapshot);
        var fw = services.Single(service => service.Name == "fwWarzoneSolarsystem");

        var warzones = Assert.IsType<PyDict>(await fw.CallAsync(Call("GetAllWarzones")));
        Assert.Equal(new PyList([new PyInt(500003), new PyInt(500002)]), Get(warzones, new PyInt(1)));
        Assert.Equal(new PyList([new PyInt(500001), new PyInt(500004)]), Get(warzones, new PyInt(2)));

        var headquarters = Assert.IsType<PyDict>(await fw.CallAsync(Call("GetHQSystemIDs")));
        Assert.Equal(new PyInt(30002974), Get(headquarters, new PyInt(500003)));
        Assert.Equal(new PyInt(30045324), Get(headquarters, new PyInt(500001)));
        Assert.Equal(new PyInt(30003788), Get(headquarters, new PyInt(500004)));
        Assert.Equal(new PyInt(30002055), Get(headquarters, new PyInt(500002)));

        var all = Assert.IsType<PyDict>(await fw.CallAsync(Call("GetAllWarzonesOccupationStates")));
        Assert.Equal(2, all.Entries.Count);
        var amarrMinmatar = Assert.IsType<PyDict>(Get(all, new PyInt(1)));
        Assert.Single(amarrMinmatar.Entries);
        Assert.Equal(new PyInt(500003), Get(KeyVal(amarrMinmatar.Entries[0].Value), "occupierID"));
        Assert.Equal(new PyInt(30000142), Get(KeyVal(amarrMinmatar.Entries[0].Value), "solarSystemID"));
        Assert.Equal(new PyInt(1), Get(KeyVal(amarrMinmatar.Entries[0].Value), "warzoneID"));
        Assert.Equal(new PyInt(0), Get(KeyVal(amarrMinmatar.Entries[0].Value), "adjacencyState"));
        Assert.Single(Assert.IsType<PyDict>(Get(all, new PyInt(2))).Entries);
    }

    [Fact]
    public async Task Local_occupation_uses_argument_then_session_and_returns_a_two_item_tuple()
    {
        var snapshot = Snapshot(occupations: [new(2, 30000145, 500004)]);
        var (services, _) = CreateServices(snapshot);
        var fw = services.Single(service => service.Name == "fwWarzoneSolarsystem");
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt(30000145));

        var fromSession = Assert.IsType<PyTuple>(await fw.CallAsync(Call("GetLocalOccupationState", session: session)));
        Assert.Equal(new PyInt(30000145), fromSession.Items[0]);
        Assert.Equal(new PyInt(500004), Get(KeyVal(fromSession.Items[1]), "occupierID"));

        var fromArgument = Assert.IsType<PyTuple>(await fw.CallAsync(
            Call("GetLocalOccupationState", [new PyInt(30000999)], session)));
        Assert.Equal(new PyInt(30000999), fromArgument.Items[0]);
        Assert.Same(PyNone.Instance, fromArgument.Items[1]);
    }

    [Fact]
    public async Task Fuel_access_group_reads_snapshot_and_session_membership_callbacks()
    {
        const long systemId = 30000142;
        var snapshot = Snapshot(fuelGroups: new Dictionary<long, long?> { [systemId] = 99000077 });
        var (services, membershipCalls) = CreateServices(snapshot, true);
        var sov = services.Single(service => service.Name == "sovMgr");

        Assert.Equal(new PyInt(99000077), await sov.CallAsync(Call("GetSovHubFuelAccessGroup", [new PyInt(systemId)])));
        Assert.Equal(PyBool.True, await sov.CallAsync(Call("IsOnLocalSovHubFuelAccessGroup")));
        Assert.Equal(1, membershipCalls());
    }

    private static (IReadOnlyList<IService> Services, Func<int> MembershipCalls) CreateServices(
        SovFwReadSnapshot snapshot,
        bool isOnLocalFuelAccessGroup = false)
    {
        var membershipCalls = 0;
        var services = SovFwService.Create(
            _ => ValueTask.FromResult(snapshot),
            _ =>
            {
                membershipCalls++;
                return ValueTask.FromResult(isOnLocalFuelAccessGroup);
            });
        return (services, () => membershipCalls);
    }

    private static SovFwReadSnapshot Snapshot(
        IReadOnlyDictionary<long, SovClaimRecord>? claims = null,
        IReadOnlyDictionary<long, SovHubRecord>? hubs = null,
        IReadOnlyDictionary<long, IReadOnlyList<SovStructureRecord>>? structures = null,
        IReadOnlyList<FwOccupationRecord>? occupations = null,
        IReadOnlyDictionary<long, long?>? fuelGroups = null) => new(
            claims ?? new Dictionary<long, SovClaimRecord>(),
            hubs ?? new Dictionary<long, SovHubRecord>(),
            structures ?? new Dictionary<long, IReadOnlyList<SovStructureRecord>>(),
            occupations ?? [],
            fuelGroups ?? new Dictionary<long, long?>());

    private static ServiceCall Call(string method, PyValue[]? args = null, ClientSession? session = null) => new()
    {
        Method = method,
        Args = args ?? [],
        Session = session ?? new ClientSession(),
    };

    private static PyValue Get(PyDict dictionary, string key) => Get(dictionary, new PyStr(key));

    private static PyValue Get(PyDict dictionary, PyValue key) =>
        dictionary.Entries.Single(entry => entry.Key.Equals(key)).Value;

    private static PyDict KeyVal(PyValue value)
    {
        var obj = Assert.IsType<PyObject>(value);
        Assert.Equal(new PyStr("util.KeyVal"), obj.Name);
        return Assert.IsType<PyDict>(obj.Args);
    }
}
