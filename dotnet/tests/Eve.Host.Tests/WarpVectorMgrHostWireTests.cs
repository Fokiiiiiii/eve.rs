using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    // warpVectorMgr is registered by the production space composition (CharacterServiceList.cs -> SpaceRpc.cs,
    // WarpVectorMgrService(ReadTravelRpcItemAsync)); the test registers the same service type with a stored-item reader.
    [Fact]
    public async Task Warp_vector_use_reaches_the_registered_host_route_and_stops_at_the_missing_destination()
    {
        Start(
            configure: registry => registry.Register(new WarpVectorMgrService(
                (id, _) => ValueTask.FromResult<SpaceRpcItem?>(new SpaceRpcItem(id, 123, 700, 4599, 1)))),
            sessionOpened: session => session.CharacterId = 123);
        var (front, open) = await OpenFrontAsync();

        var body = await NsHostRpcErrorAsync(
            front, open, 1, "warpVectorMgr", "UseVector", new PyTuple([new PyInt(70)]));

        Assert.Contains("CustomNotify", body);
        Assert.Contains("No verified warp vector destination is available.", body);
    }

    [Fact]
    public async Task Warp_vector_use_reads_the_stored_item_over_the_host_route_before_validating_its_group()
    {
        Start(
            configure: registry => registry.Register(new WarpVectorMgrService(
                (id, _) => ValueTask.FromResult<SpaceRpcItem?>(new SpaceRpcItem(id, 123, 700, 9, 1)))),
            sessionOpened: session => session.CharacterId = 123);
        var (front, open) = await OpenFrontAsync();

        var body = await NsHostRpcErrorAsync(
            front, open, 1, "warpVectorMgr", "UseVector", new PyTuple([new PyInt(70)]));

        Assert.Contains("The selected item is not warp vector data.", body);
    }
}
