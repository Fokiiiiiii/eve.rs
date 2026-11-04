using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class BrowserLockdownServiceTests
{
    [Fact]
    public async Task Browser_lockdown_fixed_reads_refuse_client_arguments()
    {
        var service = (IService)new BrowserLockdownService();
        var methods = new[]
        {
            "GetDefaultHomePage", "IsBrowserInLockdown", "GetFlaggedSitesHash", "GetFlaggedSitesList",
        };

        foreach (var method in methods)
        {
            var call = new ServiceCall
            {
                Method = method,
                Args = [new PyLong(42)],
                Session = new ClientSession { CharacterId = 99 },
            };
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal(MachoErrors.UserErrorClass, refusal.Error.ClassName);
            Assert.Equal("BROWSER_LOCKDOWN_ARGUMENTS_INVALID", refusal.Error.UserErrorMessage);

            call = new ServiceCall
            {
                Method = method,
                Args = [],
                Kwargs = new PyDict([]),
                Session = new ClientSession(),
            };
            refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal("BROWSER_LOCKDOWN_ARGUMENTS_INVALID", refusal.Error.UserErrorMessage);
        }
    }
}
