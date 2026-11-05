using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>The in-game browser's lockdown queries: never locked down, no flagged sites.</summary>
[Service("browserLockdownSvc")]
public sealed partial class BrowserLockdownService
{
    private const string DefaultHomePage = "about:blank";
    private const string EmptyFlaggedSitesHash = "d751713988987e9331980363e24189ce";

    [Method("GetDefaultHomePage")]
    public PyValue GetDefaultHomePage(ServiceCall call) => FixedResponse(call, new PyStr(DefaultHomePage));

    [Method("IsBrowserInLockdown")]
    public PyValue IsBrowserInLockdown(ServiceCall call) => FixedResponse(call, PyBool.False);

    [Method("GetFlaggedSitesHash")]
    public PyValue GetFlaggedSitesHash(ServiceCall call) => FixedResponse(call, new PyStr(EmptyFlaggedSitesHash));

    [Method("GetFlaggedSitesList")]
    public PyValue GetFlaggedSitesList(ServiceCall call) => FixedResponse(call, new PyTuple([]));

    private static PyValue FixedResponse(ServiceCall call, PyValue value)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count != 0 || call.Kwargs is not Eve.Marshal.PyNone)
            throw Eve.Services.MachoErrors.ThrowUserError("BROWSER_LOCKDOWN_ARGUMENTS_INVALID");
        return value;
    }
}
