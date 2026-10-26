using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Sovereignty_reads_and_fuel_access_mutation_use_the_registered_host_routes()
    {
        const long systemId = 30_000_142;
        const long hubId = 88_000_014;
        const long groupId = 77_000_006;
        var fuelGroups = new Dictionary<long, long?> { [systemId] = 77_000_005 };
        var snapshot = new SovFwReadSnapshot(
            new Dictionary<long, SovClaimRecord>
            {
                [systemId] = new(91_000_001, 98_000_001, 99_000_001),
            },
            new Dictionary<long, SovHubRecord>
            {
                [systemId] = new(hubId, 98_000_001, 99_000_001, 133_801_234_567_890_123),
            },
            new Dictionary<long, IReadOnlyList<SovStructureRecord>>
            {
                [systemId] = [new(91_000_001, 32226, 98_000_001, 98_000_001, 99_000_001, systemId)],
            },
            [],
            fuelGroups);
        var hubWrites = new SovHubWriteOperations(
            (_, requestedSystem, hub) => ValueTask.FromResult(requestedSystem == systemId && hub.HubId == hubId),
            (_, requestedSystem, requestedGroup) =>
            {
                fuelGroups[requestedSystem] = requestedGroup;
                return ValueTask.FromResult<PyValue>(PyNone.Instance);
            },
            (_, _, _, _) => throw new NotSupportedException(),
            (_, _, _) => throw new NotSupportedException(),
            (_, _, _) => throw new NotSupportedException());
        var services = SovFwService.Create(
            _ => ValueTask.FromResult(snapshot),
            call => ValueTask.FromResult(call.Session.GetInt("fuelGroupMember") == 1),
            hubWrites,
            new SovSkyhookWriteOperations(
                (_, _, _, _) => throw new NotSupportedException(),
                (_, _, _, _, _) => throw new NotSupportedException()));
        Start(
            configure: registry =>
            {
                foreach (var service in services)
                    registry.Register(service);
            },
            sessionOpened: session =>
            {
                session.Set("solarsystemid2", new PyInt(systemId));
                session.Set("fuelGroupMember", new PyInt(1));
            });
        var (front, open) = await OpenFrontAsync();

        var structures = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 1, "sovMgr", "GetSovStructuresInfoForSolarSystem", new PyLong(systemId)));
        var structure = Assert.IsType<PyObject>(Assert.Single(structures.Items));
        Assert.Equal(new PyInt(91_000_001), WireField(structure, "itemID"));
        Assert.Equal(new PyInt(32226), WireField(structure, "typeID"));
        Assert.Equal(new PyInt(systemId), WireField(structure, "solarSystemID"));

        var localStructures = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 2, "sovMgr", "GetSovStructuresInfoForLocalSolarSystem"));
        Assert.Single(localStructures.Items);

        var claim = Assert.IsType<PyObjectEx>(
            await HostRpcAsync(front, open, 3, "sovMgr", "GetSystemSovereigntyInfo", new PyLong(systemId)));
        Assert.Equal(ObjectExKind.Ex1, claim.Kind);
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 4, "sovMgr", "GetSystemSovereigntyInfo", new PyLong(systemId + 1)));

        Assert.Same(PyBool.True,
            await HostRpcAsync(front, open, 5, "sovMgr", "IsOnLocalSovHubFuelAccessGroup"));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 6, "sovMgr", "SetSovHubFuelAccessGroup", new PyLong(systemId), new PyLong(groupId)));
        Assert.Equal(new PyInt(groupId),
            await HostRpcAsync(front, open, 7, "sovMgr", "GetSovHubFuelAccessGroup", new PyLong(systemId)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 8, "sovMgr", "SetSovHubFuelAccessGroup", new PyLong(systemId), PyNone.Instance));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 9, "sovMgr", "GetSovHubFuelAccessGroup", new PyLong(systemId)));
    }
}
