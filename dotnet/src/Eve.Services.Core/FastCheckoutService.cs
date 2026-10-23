using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>The client-facing PLEX checkout endpoints; purchases stay disabled until a payment settlement provider exists.</summary>
[Service("FastCheckoutService")]
public sealed partial class FastCheckoutService(NewEdenStoreCatalog catalog)
{
    private readonly NewEdenStoreCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    [Method("IsFastCheckoutEnabledForUser")]
    public PyValue IsFastCheckoutEnabledForUser(ServiceCall call)
    {
        RequireNoArguments(call);
        return PyValue.Of(_catalog.FastCheckoutEnabled);
    }

    /// <summary>The server keeps no per-user fast-checkout cache, so there is nothing to clear: the answer is the client's acknowledgement.</summary>
    [Method("ClearFastCheckoutCacheForUser")]
    public PyValue ClearFastCheckoutCacheForUser(ServiceCall call)
    {
        RequireNoArguments(call);
        return PyBool.True;
    }

    [Method("GetTestingConfiguration")]
    public PyValue GetTestingConfiguration(ServiceCall call)
    {
        RequireNoArguments(call);
        return new PyTuple([
            PyValue.Of(_catalog.ChinaFunnelEnabled),
            new PyStr(_catalog.FakeBuyPlexOfferUrl),
            PyValue.Of(_catalog.UseShellExecuteToBuyPlexOffer),
        ]);
    }

    [Method("GetOffersForUser")]
    public PyValue GetOffersForUser(ServiceCall call)
    {
        RequireNoArguments(call);
        return Py.Dict(("plex", new PyList([])));
    }

    [Method("BuyOffer")]
    public ValueTask<PyValue> BuyOffer(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        throw MachoErrors.ThrowUserError("FAST_CHECKOUT_UNAVAILABLE");
    }

    private static void RequireNoArguments(ServiceCall call)
    {
        if (call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("FAST_CHECKOUT_ARGUMENTS_INVALID");
    }
}
