using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class StructureRpcServiceTests
{
    [Theory]
    [InlineData("structureCargoDelivery", "DropOffItems")]
    [InlineData("structureCompressionMgr", "CompressItemInStructure")]
    [InlineData("structureCompressionMgr", "DecompressGasInStructure")]
    [InlineData("structureCompressionMgr", "GetMyGasDecompressionEfficiency")]
    [InlineData("structureControl", "GetStructurePilot")]
    [InlineData("structureControl", "TakeControl")]
    [InlineData("structureControl", "BoardStructure")]
    [InlineData("structureControl", "EjectFromStructure")]
    [InlineData("structureControl", "ReleaseControl")]
    [InlineData("structureDeliveries", "Deliver")]
    [InlineData("structureDeployment", "Anchor")]
    [InlineData("structureDeployment", "Unanchor")]
    [InlineData("structureDeployment", "CancelUnanchor")]
    [InlineData("structureDeployment", "RenameStructure")]
    [InlineData("structureDocking", "Dock")]
    [InlineData("structureDocking", "Undock")]
    [InlineData("structureHangarViewMgr", "GetMyHangarViewState")]
    [InlineData("structureProfiles", "GetProfiles")]
    [InlineData("structureProfiles", "CreateProfile")]
    [InlineData("structureProfiles", "UpdateProfile")]
    [InlineData("structureProfiles", "GetProfileSettings")]
    [InlineData("structureProfiles", "SaveProfileSettings")]
    [InlineData("structureProfiles", "SetDefaultProfile")]
    [InlineData("structureProfiles", "ChangeProfiles")]
    [InlineData("structureProfiles", "DeleteProfile")]
    [InlineData("structureProfiles", "DuplicateProfile")]
    [InlineData("structureVulnerability", "UpdateReinforceTimesFromClient")]
    public void Evejs_structure_rpcs_are_advertised(string serviceName, string method)
    {
        Assert.True(new StructureRpcService(serviceName).HasMethod(method));
    }

    [Fact]
    public async Task Unwired_handlers_keep_reference_empty_and_none_shapes()
    {
        var cargo = await Call(new StructureRpcService("structureCargoDelivery"), "DropOffItems",
            [new PyList([]), new PyInt(10), new PyInt(20)]);
        Assert.Empty(Assert.IsType<PyList>(cargo).Items);

        var efficiency = Assert.IsType<PyList>(await Call(
            new StructureRpcService("structureCompressionMgr"), "GetMyGasDecompressionEfficiency"));
        Assert.Equal(new PyReal(0), efficiency.Items[0]);
        Assert.Equal(new PyReal(0), efficiency.Items[1]);

        Assert.Same(PyNone.Instance, await Call(new StructureRpcService("structureControl"), "GetStructurePilot", [new PyInt(20)]));
        Assert.Same(PyNone.Instance, await Call(new StructureRpcService("structureHangarViewMgr"), "GetMyHangarViewState"));

        var session = new ClientSession();
        session.Set("corpid", new PyInt(98000001));
        session.Set("corprole", new PyInt(2048));
        var settings = Assert.IsType<PyDict>(await Call(
            new StructureRpcService("structureProfiles"), "GetProfileSettings", [new PyInt(1)], session));
        Assert.Empty(settings.Entries);
        Assert.Same(PyNone.Instance, await Call(new StructureRpcService("structureDeliveries"), "Deliver",
            [new PyInt(20), new PyInt(30), new PyList([])]));
    }

    [Fact]
    public async Task Cargo_and_profile_callbacks_receive_typed_arguments_and_emit_keyvals()
    {
        (IReadOnlyList<long> Ids, long ShipId, long StructureId)? observedCargo = null;
        var callbacks = new StructureRpcCallbacks
        {
            DropOffItemsAsync = (_, ids, shipId, structureId) =>
            {
                observedCargo = (ids, shipId, structureId);
                return ValueTask.FromResult<IReadOnlyList<long>>([701, 702]);
            },
            GetProfilesAsync = (corporationId, _) =>
            {
                Assert.Equal(98000001, corporationId);
                return ValueTask.FromResult<IReadOnlyList<StructureProfileRecord>>(
                    [new StructureProfileRecord(51, "Home", "Dock access", true)]);
            },
        };

        var cargoService = new StructureRpcService("structureCargoDelivery", callbacks);
        var moved = Assert.IsType<PyList>(await Call(cargoService, "DropOffItems",
            [new PyList([new PyInt(7), new PyInt(8), new PyInt(7), new PyInt(0)]), new PyInt(12), new PyLong(34)]));
        Assert.Equal((new long[] { 7, 8 }, 12L, 34L), observedCargo);
        Assert.Equal(new PyInt(701), moved.Items[0]);
        Assert.Equal(new PyInt(702), moved.Items[1]);

        var session = new ClientSession();
        session.Set("corpid", new PyInt(98000001));
        session.Set("corprole", new PyInt(2048));
        var profiles = Assert.IsType<PyList>(await Call(
            new StructureRpcService("structureProfiles", callbacks), "GetProfiles", session: session));
        var profile = Assert.IsType<PyObjectEx>(Assert.Single(profiles.Items));
        Assert.Equal(ObjectExKind.Ex1, profile.Kind);
        Assert.Equal(new PyStr("util.KeyVal"), profile.Header);
        Assert.Contains(profile.Dict, pair => pair.Key == new PyStr("profileID") && pair.Value == new PyInt(51));
        Assert.Contains(profile.Dict, pair => pair.Key == new PyStr("isDefault") && pair.Value == PyBool.True);
    }

    [Fact]
    public async Task Profile_reads_require_the_station_manager_role()
    {
        var session = new ClientSession();
        session.Set("corpid", new PyInt(98000001));
        session.Set("corprole", new PyInt(0));
        session.Set("corpRole", new PyInt(2048));
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Call(new StructureRpcService("structureProfiles"), "GetProfiles", session: session));
    }

    [Fact]
    public async Task Vulnerability_rpc_preserves_the_reference_no_day_sentinel()
    {
        (IReadOnlyList<long> StructureIds, int Weekday, int Hour)? observed = null;
        var service = new StructureRpcService("structureVulnerability", new StructureRpcCallbacks
        {
            UpdateReinforceTimesFromClientAsync = (_, ids, weekday, hour) =>
            {
                observed = (ids, weekday, hour);
                return ValueTask.CompletedTask;
            },
        });

        Assert.Same(PyNone.Instance, await Call(service, "UpdateReinforceTimesFromClient",
            [new PyList([new PyInt(1001)]), new PyInt(255), new PyInt(18)]));
        Assert.Equal(new long[] { 1001 }, observed?.StructureIds);
        Assert.Equal(255, observed?.Weekday);
        Assert.Equal(18, observed?.Hour);
    }

    [Fact]
    public async Task Structure_undock_only_accepts_the_clients_boolean_confirmation()
    {
        bool? observed = null;
        var service = new StructureRpcService("structureDocking", new StructureRpcCallbacks
        {
            UndockAsync = (_, structure, ship, ignoreContraband) =>
            {
                Assert.Equal(1_000_000_000_051, structure);
                Assert.Equal(1_000_000_000_052, ship);
                observed = ignoreContraband;
                return ValueTask.CompletedTask;
            },
        });
        foreach (var keyword in new PyValue[] { PyNone.Instance, PyBool.False, PyBool.True, new PyStr("true"), new PyInt(1) })
        {
            PyValue kwargs = keyword is PyNone ? PyNone.Instance : Py.Dict(("ignoreContraband", keyword));
            await service.CallAsync(new ServiceCall
            {
                Method = "Undock",
                Args = [new PyLong(1_000_000_000_051), new PyLong(1_000_000_000_052)],
                Kwargs = kwargs,
                Session = new ClientSession(),
            });
            Assert.Equal(PyBool.True.Equals(keyword), observed);
        }
    }

    private static ValueTask<PyValue> Call(
        StructureRpcService service,
        string method,
        IReadOnlyList<PyValue>? args = null,
        ClientSession? session = null) =>
        service.CallAsync(new ServiceCall
        {
            Method = method,
            Args = args ?? [],
            Session = session ?? new ClientSession(),
        });
}
