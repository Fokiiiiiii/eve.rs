using Eve.Identity;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

/// <summary>A new account is Omega until 2100, the stored end time is the answer of every path, and an operator's earlier end lapses it.</summary>
public class SubscriptionTransitionsTests
{
    private const long Now = 1_700_000_000_000L;

    private sealed class Packets : IPacketSink
    {
        public List<string> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(Convert.ToHexStringLower(MarshalCodec.Encode(packet)));

        public bool Announced(string notification) =>
            Sent.Any(packet => packet.Contains(Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes(notification)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_account_is_stored_until_2100_and_an_operator_lapse_is_announced_to_its_session()
    {
        var directory = new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true });
        var login = await directory.LoginAsync(new LoginAttempt("newbie", new byte[] { 1 }, "en", "127.0.0.1"), CancellationToken.None);
        var accountId = Assert.IsType<LoggedIn>(login).AccountId;
        Assert.Equal(SubscriptionPolicy.NewAccountSubscriptionEnd, directory.SubscriptionEndsAt(accountId));

        var store = new AccountOmegaStore(directory);
        var omega = new OmegaLicenses(store, () => Now);
        var subscriptions = CoreServices.Create(store, () => Now).Single(service => service.Name == "subscriptionMgr");
        var sink = new Packets();
        var session = new ClientSession(sink) { SessionId = 9 };
        session.Set("userid", new PyLong(accountId));

        Assert.Equal(new PyInt(1), await subscriptions.CallAsync(new ServiceCall { Method = "GetCloneGrade", Args = [], Session = session }));
        Assert.Empty(sink.Sent);

        SubscriptionTransitions.SetSubscriptionEnd(
            directory, omega, [session], accountId, DateTimeOffset.FromUnixTimeMilliseconds(1_600_000_000_000L));

        Assert.True(sink.Announced("OnSubscriptionChangedServer"));
        Assert.True(sink.Announced("OnSubscriptionTimeUpdateServer"));
        Assert.False(omega.Resolve(new PyLong(accountId)).HasLicense);
        Assert.Equal(new PyInt(0), await subscriptions.CallAsync(new ServiceCall { Method = "GetCloneGrade", Args = [], Session = session }));
    }
}
