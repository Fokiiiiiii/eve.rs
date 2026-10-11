using System.Text.Json.Nodes;
using Eve.GameStore.Tests;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.GameStore.Import.Tests;

/// <summary>The bridge to the legacy store: a licence written by one process is seen by the next.</summary>
public class LegacyOmegaTests
{
    private const long NowMs = 1_700_000_000_123;

    private static JsonNode J(string json) => JsonNode.Parse(json)!;

    private static async Task<GameStoreCache> Writer(PgScope pg, string instance)
    {
        var session = await OwnerSession.StartAsync(pg.Store, "world", instance, new AcquireOptions(LeaseMs: 600_000), renewEvery: TimeSpan.FromHours(1));
        return new GameStoreCache(pg.Store, session, new GameStoreCacheOptions { ProcessRole = "world", FlushDelay = TimeSpan.FromHours(1) });
    }

    private static async Task<PyValue> Call(IService service, string method, long userId)
    {
        var session = new ClientSession { UserId = userId };
        return await service.CallAsync(new ServiceCall { Method = method, Args = [], Session = session });
    }

    [PgFact]
    public async Task A_licence_flushed_by_one_process_is_read_by_the_next()
    {
        await using var pg = await PgScope.CreateAsync();
        var future = Py.FileTimeFromUnixMs(NowMs) + (9 * 864000000000L);

        var first = await Writer(pg, "first");
        await first.LoadAsync(LegacyOmegaAccounts.Table);
        first.Write(LegacyOmegaAccounts.Table, "/accounts/90000001", J($$"""{"omegaExpiryFileTime":" {{future}} ","updatedAt":"x"}"""));
        await first.FlushAllAsync();
        await first.DisposeAsync();

        var reader = new GameStoreCache(pg.Store, null, new GameStoreCacheOptions { ProcessRole = "reader" });
        await reader.LoadAsync(LegacyOmegaAccounts.Table);
        var accounts = new LegacyOmegaAccounts(reader);
        Assert.Equal(future.ToString(), accounts.GetOmegaExpiry(90000001));
        Assert.Null(accounts.GetOmegaExpiry(90000002));

        var services = CoreServices.Create(accounts, () => NowMs);
        var subscription = services.Single(s => s.Name == "subscriptionMgr");
        var info = (PyObject)await Call(subscription, "GetSubscriptionInfo", 90000001);
        var entries = ((PyDict)info.Args).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value);
        Assert.Equal(PyBool.True, entries["isSubscribed"]);
        Assert.Equal(new PyInt(9), entries["subscriptionDaysRemaining"]);
        Assert.Equal(new PyLong(future), entries["subscriptionEndTime"]);

        // Another account has none: Alpha.
        var other = (PyObject)await Call(subscription, "GetSubscriptionInfo", 90000002);
        Assert.Equal(PyBool.False, ((PyDict)other.Args).Entries.Single(e => ((PyStr)e.Key).Value == "isSubscribed").Value);
    }

    [PgFact]
    public async Task Blank_or_non_string_expiries_and_missing_tables_mean_no_licence()
    {
        await using var pg = await PgScope.CreateAsync();
        var writer = await Writer(pg, "w");
        await writer.LoadAsync(LegacyOmegaAccounts.Table);
        writer.Write(LegacyOmegaAccounts.Table, "/accounts/1", J("""{"omegaExpiryFileTime":"   "}"""));
        writer.Write(LegacyOmegaAccounts.Table, "/accounts/2", J("""{"omegaExpiryFileTime":5}"""));
        writer.Write(LegacyOmegaAccounts.Table, "/accounts/3", J("""{}"""));
        var accounts = new LegacyOmegaAccounts(writer);
        Assert.Null(accounts.GetOmegaExpiry(1));
        Assert.Null(accounts.GetOmegaExpiry(2));
        Assert.Null(accounts.GetOmegaExpiry(3));
        Assert.Null(accounts.GetOmegaExpiry(4));
        await writer.DisposeAsync();
    }
}
