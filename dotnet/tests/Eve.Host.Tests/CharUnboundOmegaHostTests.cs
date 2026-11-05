using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.CharacterSelect;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Char_unbound_omega_downgrade_status_tracks_account_expiry_through_the_registered_route()
    {
        var endsAt = DateTimeOffset.UtcNow.AddDays(1);
        var service = new CharUnboundMgrService(
            accounts: new AccountLookup(() => new AccountInfo(42, "pilot", DateTimeOffset.UnixEpoch, endsAt)),
            characters: null!,
            items: null!,
            training: null!,
            wallets: null!,
            creation: null!,
            catalog: null!,
            online: null!,
            entryHooks: [],
            random: Random.Shared);

        Start(configure: registry => registry.Register(service));
        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<PyValue> CallAsync(uint correlationId)
        {
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, correlationId, "charUnboundMgr", "GetCharOmegaDowngradeStatus",
                    new PyTuple([]), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        Assert.Same(PyBool.False, await CallAsync(1));
        endsAt = DateTimeOffset.UtcNow.AddDays(-1);
        Assert.Same(PyBool.True, await CallAsync(2));
    }

    private sealed class AccountLookup(Func<AccountInfo?> read) : IAccountLookup
    {
        public ValueTask<AccountInfo?> FindAsync(uint accountId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(read());
    }
}
