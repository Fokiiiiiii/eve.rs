using Eve.Marshal;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Random_jump_activation_consumes_the_filament_once_over_host_wire()
    {
        const long characterId = 90_000_001;
        const long filamentId = 880_001;
        long remaining = 1;
        var activations = new List<long>();
        Start(
            configure: registry => registry.Register(new RandomJumpMgrService(
                readItem: (itemId, _) => ValueTask.FromResult<SpaceRpcItem?>(
                    itemId == filamentId && remaining > 0
                        ? new SpaceRpcItem(filamentId, characterId, 0, 4041, remaining)
                        : null),
                activate: (_, item, _) =>
                {
                    remaining--;
                    activations.Add(item.ItemId);
                    return ValueTask.FromResult<PyValue?>(PyNone.Instance);
                })),
            sessionOpened: session => session.Set("characterID", new PyInt(characterId)));
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 1, "randomJumpMgr", "ActivateRandomJumpFilament", new PyLong(filamentId)));
        Assert.Equal(new[] { filamentId }, activations);
        Assert.Equal(0, remaining);

        var body = await NsHostRpcErrorAsync(
            front, open, 2, "randomJumpMgr", "ActivateRandomJumpFilament", new PyTuple([new PyLong(filamentId)]));
        Assert.Contains("RandomJumpError", body);
        Assert.Single(activations);
    }
}
