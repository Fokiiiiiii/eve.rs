using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class SovFwServiceTests
{
    [Fact]
    public void Registers_the_EveJS_sov_hub_and_skyhook_endpoints()
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
                "SetSovHubFuelAccessGroup",
                "SaveUpgradeConfiguration",
                "UninstallUpgrade",
                "SaveWorkforceConfiguration",
                "DestroySkyhooks",
                "AcquireSkyhooks",
            ], method => Assert.True(sov.HasMethod(method)));

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

    [Fact]
    public async Task Sov_hub_writes_check_current_owner_and_forward_client_configuration()
    {
        const long systemId = 30000142;
        const long hubId = 1020002;
        const long upgradeTypeId = 23527;
        var hub = new SovHubRecord(hubId, 98000002, 99000002, 16600000);
        var snapshot = Snapshot(hubs: new Dictionary<long, SovHubRecord> { [systemId] = hub });
        var authorizationCalls = 0;
        long? writtenFuelGroup = 17;
        IReadOnlyList<long> writtenItemIds = [];
        IReadOnlyList<SovHubUpgradeState> writtenUpgradeStates = [];
        (long HubId, long TypeId)? uninstalledUpgrade = null;
        PyValue? workforceConfiguration = null;
        var operations = new SovHubWriteOperations(
            (call, authorizedSystem, authorizedHub) =>
            {
                authorizationCalls++;
                Assert.NotNull(call.Session);
                Assert.Equal(systemId, authorizedSystem);
                Assert.Equal(hub, authorizedHub);
                return ValueTask.FromResult(true);
            },
            (_, solarSystemId, groupId) =>
            {
                Assert.Equal(systemId, solarSystemId);
                writtenFuelGroup = groupId;
                return ValueTask.FromResult<PyValue>(PyBool.True);
            },
            (_, configuredHubId, itemIds, states) =>
            {
                Assert.Equal(hubId, configuredHubId);
                writtenItemIds = itemIds;
                writtenUpgradeStates = states;
                return ValueTask.FromResult<PyValue>(new PyStr("upgrades-saved"));
            },
            (_, configuredHubId, typeId) =>
            {
                uninstalledUpgrade = (configuredHubId, typeId);
                return ValueTask.FromResult<PyValue>(PyNone.Instance);
            },
            (_, configuredHubId, configuration) =>
            {
                Assert.Equal(hubId, configuredHubId);
                workforceConfiguration = configuration;
                return ValueTask.FromResult<PyValue>(PyBool.True);
            });
        var (services, _) = CreateServices(snapshot, writeOperations: operations);
        var sov = services.Single(service => service.Name == "sovMgr");

        Assert.Equal(
            PyBool.True,
            await sov.CallAsync(Call("SetSovHubFuelAccessGroup", [new PyInt(systemId), PyNone.Instance])));
        Assert.Null(writtenFuelGroup);
        Assert.Equal(
            PyBool.True,
            await sov.CallAsync(Call("SetSovHubFuelAccessGroup", [new PyInt(systemId), new PyInt(99000077)])));
        Assert.Equal((long?)99000077, writtenFuelGroup);
        Assert.Same(
            PyNone.Instance,
            await sov.CallAsync(Call("SetSovHubFuelAccessGroup", [new PyInt(systemId + 1), new PyInt(99000077)])));

        Assert.Equal(
            new PyStr("upgrades-saved"),
            await sov.CallAsync(Call(
                "SaveUpgradeConfiguration",
                [
                    new PyInt(hubId),
                    new PyList([new PyInt(90001), new PyInt(90002)]),
                    new PyList([
                        new PyTuple([new PyInt(upgradeTypeId), PyBool.True]),
                        new PyTuple([new PyInt(23528), PyBool.False]),
                    ]),
                ])));
        Assert.Equal(new long[] { 90001, 90002 }, writtenItemIds);
        Assert.Equal(
            new[] { new SovHubUpgradeState(upgradeTypeId, true), new SovHubUpgradeState(23528, false) },
            writtenUpgradeStates);

        Assert.Same(
            PyNone.Instance,
            await sov.CallAsync(Call(
                "UninstallUpgrade",
                [new PyTuple([new PyInt(hubId), new PyInt(upgradeTypeId)])])));
        Assert.Equal((hubId, upgradeTypeId), uninstalledUpgrade);

        var workforceValue = new PyObjectEx(
            ObjectExKind.Ex1,
            new PyTuple([new PyToken("sovereignty.workforce.client.data_types.WorkforceConfiguration"), new PyList([])]),
            [],
            []);
        Assert.Equal(
            PyBool.True,
            await sov.CallAsync(Call("SaveWorkforceConfiguration", [new PyInt(hubId), workforceValue])));
        Assert.Same(workforceValue, workforceConfiguration);
        Assert.Equal(5, authorizationCalls);
    }

    [Fact]
    public async Task Sov_hub_write_denial_and_malformed_client_payloads_never_reach_mutations()
    {
        const long systemId = 30000142;
        var hub = new SovHubRecord(1020002, 98000002, 99000002, 16600000);
        var authorizationCalls = 0;
        var mutationCalls = 0;
        var operations = new SovHubWriteOperations(
            (_, _, _) =>
            {
                authorizationCalls++;
                return ValueTask.FromResult(false);
            },
            (_, _, _) =>
            {
                mutationCalls++;
                return ValueTask.FromResult<PyValue>(PyBool.True);
            },
            (_, _, _, _) =>
            {
                mutationCalls++;
                return ValueTask.FromResult<PyValue>(PyBool.True);
            },
            (_, _, _) =>
            {
                mutationCalls++;
                return ValueTask.FromResult<PyValue>(PyBool.True);
            },
            (_, _, _) =>
            {
                mutationCalls++;
                return ValueTask.FromResult<PyValue>(PyBool.True);
            });
        var snapshot = Snapshot(hubs: new Dictionary<long, SovHubRecord> { [systemId] = hub });
        var (services, _) = CreateServices(snapshot, writeOperations: operations);
        var sov = services.Single(service => service.Name == "sovMgr");

        Assert.Equal(
            PyBool.False,
            await sov.CallAsync(Call("SetSovHubFuelAccessGroup", [new PyInt(systemId), new PyInt(99000077)])));
        Assert.Same(
            PyNone.Instance,
            await sov.CallAsync(Call(
                "SaveUpgradeConfiguration",
                [
                    new PyInt(1020002),
                    new PyList([new PyInt(90001)]),
                    new PyList([new PyTuple([new PyInt(23527), new PyStr("online")])]),
                ])));
        Assert.Equal(1, authorizationCalls);
        Assert.Equal(0, mutationCalls);
    }

    [Fact]
    public async Task Skyhook_callbacks_within_claim_window_return_only_processed_ids()
    {
        const long systemId = 30000142;
        const long hubId = 1020002;
        const long accessListId = 99000077;
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt(systemId));
        var hub = new SovHubRecord(
            hubId,
            98000002,
            99000002,
            DateTime.UtcNow.AddHours(-23).AddMinutes(-59).ToFileTimeUtc());
        var snapshot = Snapshot(hubs: new Dictionary<long, SovHubRecord> { [systemId] = hub });

        long[] destroyRequested = [810801, 810802];
        long[] destroyProcessed = [810802];
        long[] acquireRequested = [810803, 810804];
        long[] acquireProcessed = [810803];
        var authorizationCalls = 0;
        var hubWrites = HubWrites((call, solarSystemId, currentHub) =>
        {
            authorizationCalls++;
            Assert.Same(session, call.Session);
            Assert.Equal(systemId, solarSystemId);
            Assert.Equal(hub, currentHub);
            return ValueTask.FromResult(true);
        });
        var skyhookWrites = new SovSkyhookWriteOperations(
            (call, solarSystemId, currentHub, requestedIds) =>
            {
                Assert.Same(session, call.Session);
                Assert.Equal(systemId, solarSystemId);
                Assert.Equal(hub, currentHub);
                Assert.Equal(destroyRequested, requestedIds);
                return ValueTask.FromResult<IReadOnlyList<long>>(destroyProcessed);
            },
            (call, solarSystemId, currentHub, requestedIds, receivedAccessListId) =>
            {
                Assert.Same(session, call.Session);
                Assert.Equal(systemId, solarSystemId);
                Assert.Equal(hub, currentHub);
                Assert.Equal(acquireRequested, requestedIds);
                Assert.Equal(accessListId, receivedAccessListId);
                return ValueTask.FromResult<IReadOnlyList<long>>(acquireProcessed);
            });
        var (services, _) = CreateServices(
            snapshot,
            writeOperations: hubWrites,
            skyhookWriteOperations: skyhookWrites);
        var sov = services.Single(service => service.Name == "sovMgr");

        var destroyResult = Assert.IsType<PyList>(await sov.CallAsync(Call(
            "DestroySkyhooks",
            [new PyList([new PyInt(destroyRequested[0]), new PyInt(destroyRequested[1])])],
            session)));
        Assert.Single(destroyResult.Items);
        Assert.Equal(new PyInt(destroyProcessed[0]), destroyResult.Items[0]);

        var acquireResult = Assert.IsType<PyList>(await sov.CallAsync(Call(
            "AcquireSkyhooks",
            [
                new PyList([new PyInt(acquireRequested[0]), new PyInt(acquireRequested[1])]),
                new PyInt(accessListId),
            ],
            session)));
        Assert.Single(acquireResult.Items);
        Assert.Equal(new PyInt(acquireProcessed[0]), acquireResult.Items[0]);
        Assert.Equal(2, authorizationCalls);
    }

    [Fact]
    public async Task Skyhook_mutation_callbacks_are_not_called_when_hub_management_is_denied()
    {
        const long systemId = 30000142;
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt(systemId));
        var hub = new SovHubRecord(
            1020002,
            98000002,
            99000002,
            DateTime.UtcNow.AddMinutes(-15).ToFileTimeUtc());
        var snapshot = Snapshot(hubs: new Dictionary<long, SovHubRecord> { [systemId] = hub });
        var authorizationCalls = 0;
        var mutationCalls = 0;
        var hubWrites = HubWrites((_, _, _) =>
        {
            authorizationCalls++;
            return ValueTask.FromResult(false);
        });
        var skyhookWrites = new SovSkyhookWriteOperations(
            (_, _, _, _) =>
            {
                mutationCalls++;
                return ValueTask.FromResult<IReadOnlyList<long>>([810801]);
            },
            (_, _, _, _, _) =>
            {
                mutationCalls++;
                return ValueTask.FromResult<IReadOnlyList<long>>([810802]);
            });
        var (services, _) = CreateServices(
            snapshot,
            writeOperations: hubWrites,
            skyhookWriteOperations: skyhookWrites);
        var sov = services.Single(service => service.Name == "sovMgr");

        Assert.Equal(
            PyBool.False,
            await sov.CallAsync(Call(
                "DestroySkyhooks",
                [new PyList([new PyInt(810801)])],
                session)));
        Assert.Equal(
            PyBool.False,
            await sov.CallAsync(Call(
                "AcquireSkyhooks",
                [new PyList([new PyInt(810802)]), new PyInt(99000077)],
                session)));
        Assert.Equal(2, authorizationCalls);
        Assert.Equal(0, mutationCalls);
    }

    [Fact]
    public async Task AcquireSkyhooks_does_not_call_mutation_after_the_claim_window()
    {
        const long systemId = 30000142;
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt(systemId));
        var hub = new SovHubRecord(
            1020002,
            98000002,
            99000002,
            DateTime.UtcNow.AddHours(-25).ToFileTimeUtc());
        var snapshot = Snapshot(hubs: new Dictionary<long, SovHubRecord> { [systemId] = hub });
        var authorizationCalls = 0;
        var acquireCalls = 0;
        var hubWrites = HubWrites((_, _, _) =>
        {
            authorizationCalls++;
            return ValueTask.FromResult(true);
        });
        var skyhookWrites = new SovSkyhookWriteOperations(
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<long>>([]),
            (_, _, _, _, _) =>
            {
                acquireCalls++;
                return ValueTask.FromResult<IReadOnlyList<long>>([810801]);
            });
        var (services, _) = CreateServices(
            snapshot,
            writeOperations: hubWrites,
            skyhookWriteOperations: skyhookWrites);
        var sov = services.Single(service => service.Name == "sovMgr");

        Assert.Equal(
            PyBool.False,
            await sov.CallAsync(Call(
                "AcquireSkyhooks",
                [new PyList([new PyInt(810801)]), new PyInt(99000077)],
                session)));
        Assert.Equal(1, authorizationCalls);
        Assert.Equal(0, acquireCalls);
    }

    private static (IReadOnlyList<IService> Services, Func<int> MembershipCalls) CreateServices(
        SovFwReadSnapshot snapshot,
        bool isOnLocalFuelAccessGroup = false,
        SovHubWriteOperations? writeOperations = null,
        SovSkyhookWriteOperations? skyhookWriteOperations = null)
    {
        var membershipCalls = 0;
        var services = SovFwService.Create(
            _ => ValueTask.FromResult(snapshot),
            _ =>
            {
                membershipCalls++;
                return ValueTask.FromResult(isOnLocalFuelAccessGroup);
            },
            writeOperations,
            skyhookWriteOperations);
        return (services, () => membershipCalls);
    }

    private static SovHubWriteOperations HubWrites(
        Func<ServiceCall, long, SovHubRecord, ValueTask<bool>> canManageHubAsync) =>
        new(
            canManageHubAsync,
            (_, _, _) => ValueTask.FromResult<PyValue>(PyNone.Instance),
            (_, _, _, _) => ValueTask.FromResult<PyValue>(PyNone.Instance),
            (_, _, _) => ValueTask.FromResult<PyValue>(PyNone.Instance),
            (_, _, _) => ValueTask.FromResult<PyValue>(PyNone.Instance));

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
