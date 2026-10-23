using System.Globalization;
using System.Text;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core.Tests;

/// <summary>
/// The Omega state is one stored value per account, its subscription end time. Every answer reads it, and an earlier end
/// time (an operator's) lapses the account to Alpha, announced to its open session the way the client expects.
/// </summary>
public class OmegaStateTests
{
    private const long Now = 1_700_000_000_000L;

    /// <summary>An end time in the past of <see cref="Now"/>: an account that has lapsed.</summary>
    private static readonly string Lapsed = Py.FileTimeFromUnixMs(1_600_000_000_000L).ToString(CultureInfo.InvariantCulture);

    private sealed class Store : IOmegaAccountStore
    {
        public Dictionary<long, string?> Values { get; } = [];

        public string? GetOmegaExpiry(long accountId) => Values.GetValueOrDefault(accountId);
    }

    private sealed class Packets : IPacketSink
    {
        public List<string> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(Convert.ToHexStringLower(MarshalCodec.Encode(packet)));

        public bool Announced(string notification) =>
            Sent.Any(packet => packet.Contains(Convert.ToHexStringLower(Encoding.ASCII.GetBytes(notification)), StringComparison.Ordinal));
    }

    private static IService Subscriptions(IOmegaAccountStore store) =>
        CoreServices.Create(store, () => Now).Single(service => service.Name == "subscriptionMgr");

    private static ClientSession SessionOf(long accountId, IPacketSink? sink = null)
    {
        var session = new ClientSession(sink) { SessionId = 1 };
        session.Set("userid", new PyLong(accountId));
        return session;
    }

    private static async Task<PyValue> Call(IService service, ClientSession session, string method) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = [], Session = session });

    [Fact]
    public async Task A_stored_end_in_2100_is_omega_with_that_end()
    {
        var store = new Store { Values = { [1] = OmegaLicenses.DefaultOmegaExpiryText } };
        var service = Subscriptions(store);
        var session = SessionOf(1);

        Assert.Equal(new PyInt(1), await Call(service, session, "GetCloneGrade"));
        Assert.Equal(Py.FileTimeLong(OmegaLicenses.DefaultOmegaExpiryFileTime), await Call(service, session, "GetSubscriptionTime"));
        Assert.Equal(1, new OmegaLicenses(store, () => Now).CloneGradeOf(new PyLong(1)));
    }

    [Fact]
    public async Task Subscription_and_login_reward_reads_refuse_client_arguments_before_session_changes()
    {
        var services = CoreServices.Create(new Store(), () => Now)
            .Where(service => service.Name is "subscriptionMgr" or "loginRewardFacilities")
            .ToDictionary(service => service.Name, StringComparer.Ordinal);
        var methods = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["subscriptionMgr"] = ["GetSubscriptionStatus", "GetSubscriptionInfo", "GetSubscriptionTime", "GetCloneGrade"],
            ["loginRewardFacilities"] = ["get_my_clone_grade"],
        };

        foreach (var (serviceName, routeNames) in methods)
        foreach (var method in routeNames)
        foreach (var kwargs in new PyValue[] { PyNone.Instance, new PyDict([]) })
        {
            var session = SessionOf(7);
            var call = new ServiceCall
            {
                Method = method,
                Args = kwargs is PyNone ? [new PyInt(9)] : [],
                Kwargs = kwargs,
                Session = session,
            };
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => services[serviceName].CallAsync(call).AsTask());
            Assert.Equal(MachoErrors.UserErrorClass, refusal.Error.ClassName);
            Assert.Equal("SUBSCRIPTION_ARGUMENTS_INVALID", refusal.Error.UserErrorMessage);
            Assert.Null(session.Get(SubscriptionMgrService.AnnouncedCloneGradeKey));
        }
    }

    [Fact]
    public async Task A_stored_end_in_the_past_is_alpha_with_no_end_time_and_has_lapsed()
    {
        var store = new Store { Values = { [2] = Lapsed } };
        var service = Subscriptions(store);
        var session = SessionOf(2);

        Assert.Equal(new PyInt(0), await Call(service, session, "GetCloneGrade"));
        Assert.Equal(PyNone.Instance, await Call(service, session, "GetSubscriptionTime"));
        var licenses = new OmegaLicenses(store, () => Now);
        Assert.True(licenses.StoredExpiry(new PyLong(2)) > 0 && licenses.StoredExpiry(new PyLong(2)) <= licenses.NowFileTime);
        Assert.Equal(0, licenses.CloneGradeOf(new PyLong(2)));
    }

    [Fact]
    public async Task A_stored_end_that_has_passed_the_2100_policy_end_is_alpha()
    {
        // After 2100 the policy end is in the past: the account has lapsed (the reference answers Omega here by its constant fallback).
        var store = new Store { Values = { [6] = OmegaLicenses.DefaultOmegaExpiryText } };
        var service = CoreServices.Create(store, () => 4_200_000_000_000L).Single(candidate => candidate.Name == "subscriptionMgr");

        Assert.Equal(new PyInt(0), await Call(service, SessionOf(6), "GetCloneGrade"));
        Assert.Equal(PyNone.Instance, await Call(service, SessionOf(6), "GetSubscriptionTime"));
    }

    [Fact]
    public void An_account_with_no_stored_end_time_is_alpha_and_has_not_lapsed()
    {
        var licenses = new OmegaLicenses(new Store(), () => Now);

        Assert.False(licenses.Resolve(new PyLong(3)).HasLicense);
        Assert.Equal(0, licenses.StoredExpiry(new PyLong(3)));
    }

    [Fact]
    public async Task An_operator_lapse_reaches_the_open_session_once_as_a_clone_grade_change()
    {
        var store = new Store { Values = { [4] = OmegaLicenses.DefaultOmegaExpiryText } };
        var service = Subscriptions(store);
        var sink = new Packets();
        var session = SessionOf(4, sink);

        Assert.Equal(new PyInt(1), await Call(service, session, "GetCloneGrade"));
        Assert.Empty(sink.Sent); // the first answer only records the grade the client already has

        store.Values[4] = Lapsed;
        Assert.Equal(new PyInt(0), await Call(service, session, "GetCloneGrade"));
        Assert.True(sink.Announced("OnSubscriptionChangedServer"));
        Assert.True(sink.Announced("OnSubscriptionTimeUpdateServer"));

        var sent = sink.Sent.Count;
        Assert.Equal(new PyInt(0), await Call(service, session, "GetCloneGrade"));
        Assert.Equal(sent, sink.Sent.Count);
    }

    [Fact]
    public void The_announcement_carries_the_grade_and_the_end_time()
    {
        var sink = new Packets();

        SubscriptionMgrService.AnnounceCloneGrade(SessionOf(5, sink), 1, OmegaLicenses.DefaultOmegaExpiryFileTime);

        Assert.True(sink.Announced("OnSubscriptionChangedServer"));
        Assert.True(sink.Announced("OnSubscriptionTimeUpdateServer"));
    }
}
