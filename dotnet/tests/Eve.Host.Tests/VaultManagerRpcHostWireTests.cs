using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Vault_manager_balance_uses_the_client_wire_shape_and_authenticated_account()
    {
        var callbacks = new VaultBalanceCallbacks(12_345);
        Start(
            configure: registry => registry.Register(new VaultManagerService(callbacks)),
            sessionOpened: session => session.UserId = 9001);

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<Send> Invoke(uint id, PyValue currency)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, id, "vaultManager", "get_account_balance", new PyTuple([currency]), MachoVersion()))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        var balance = await Invoke(1, new PyBytes(Encoding.UTF8.GetBytes("PLX")));
        var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(balance.Payload)));
        Assert.Equal(MachoMessage.CallRsp, response.Type);
        Assert.Equal(new PyLong(12_345), Result(MarshalCodec.Decode(balance.Payload)));
        Assert.Equal(9001, callbacks.AuthenticatedUserId);
        Assert.Equal(1, callbacks.Calls);

        callbacks.Balance = 0;
        var empty = await Invoke(2, new PyBytes(Encoding.UTF8.GetBytes("PLX")));
        var emptyResponse = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(empty.Payload)));
        Assert.Equal(MachoMessage.CallRsp, emptyResponse.Type);
        Assert.Equal(new PyLong(0), Result(MarshalCodec.Decode(empty.Payload)));

        var unsupported = await Invoke(3, new PyBytes(Encoding.UTF8.GetBytes("ISK")));
        var unsupportedResponse = Assert.IsType<MachoPacket>(
            MachoPacket.Decode(MarshalCodec.Decode(unsupported.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, unsupportedResponse.Type);
        Assert.Equal(2, callbacks.Calls);
    }

    private sealed class VaultBalanceCallbacks(long balance) : IVaultManagerCallbacks
    {
        public long Balance { get; set; } = balance;
        public long AuthenticatedUserId { get; private set; }
        public int Calls { get; private set; }

        public ValueTask<long> GetPlexBalanceAsync(long authenticatedUserId, CancellationToken cancellationToken)
        {
            AuthenticatedUserId = authenticatedUserId;
            Calls++;
            return ValueTask.FromResult(Balance);
        }
    }
}
