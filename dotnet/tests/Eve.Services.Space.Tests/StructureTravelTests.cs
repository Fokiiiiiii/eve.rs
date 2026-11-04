using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The RPC service's fallback when no structure travel handler is supplied. The live direct jump, ACL, and group/bridge boundary are covered by
/// <see cref="StructureBeaconTravelTests"/>.
/// </summary>
public sealed class StructureTravelTests
{
    private const string Unwired = "A Pharolux or Upwell cynosural beacon cannot be jumped to: the structure service that keeps the beacon's state (the Upwell structure phase) is not built on this server.";

    public static TheoryData<string> CynoBeaconMethods { get; } =
    [
        "CmdBridgeToStructureBeacon",
        "CmdGroupJumpToStructureBeacon",
        "CmdJumpToStructureBeacon",
    ];

    /// <summary>Unwired, every advertised call throws the user error; a PyNone answer would be read by the client as a jump that went through.</summary>
    [Theory]
    [MemberData(nameof(CynoBeaconMethods))]
    public async Task An_unwired_cyno_beacon_call_is_a_user_error_with_its_text(string method)
    {
        var service = new StructureTravelRpcService("structureCynoBeaconMgr");
        Assert.True(service.HasMethod(method));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Call(method)).AsTask());

        Assert.Equal(Unwired, NotifyText(error));
    }

    [Fact]
    public async Task A_wired_cyno_beacon_call_is_answered_by_its_backend_and_not_by_the_unwired_error()
    {
        var expected = new PyInt(1);
        var service = new StructureTravelRpcService("structureCynoBeaconMgr", _ => ValueTask.FromResult<PyValue>(expected));

        Assert.Same(expected, await service.CallAsync(Call("CmdJumpToStructureBeacon")));
    }

    [Fact]
    public void The_cyno_beacon_service_name_is_the_one_the_client_calls()
    {
        Assert.Equal("structureCynoBeaconMgr", new StructureTravelRpcService("structureCynoBeaconMgr").Name);
    }

    private static ServiceCall Call(string method) => new()
    {
        Method = method,
        Args = [new PyLong(123), new PyLong(30_000_142)],
        Session = new ClientSession(),
    };

    private static string? NotifyText(MachoWrappedException error) =>
        error.Error.Payload is PyObjectEx { Header: PyTuple { Items: [_, PyTuple { Items: [_, PyDict arguments] }, ..] } }
            && arguments.Entries.FirstOrDefault(entry => entry.Key is PyStr { Value: "notify" }).Value is PyStr text
                ? text.Value
                : null;
}
