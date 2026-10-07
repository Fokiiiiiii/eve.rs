using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>How the subscription (Omega/Alpha) state is configured.</summary>
public sealed class SubscriptionSettings
{
    /// <summary>The raw <c>EVE_CLONE_GRADE</c> value (null when unset): "omega", "alpha", "trial", or 0/1.</summary>
    public string? CloneGradeOverride { get; init; }

    /// <summary>Whether Omega is granted by default (the <c>omegaLicenseEnabled</c> setting).</summary>
    public bool OmegaLicenseEnabled { get; init; } = true;
}

/// <summary>Where a runtime account's Omega expiry is kept (<c>newEdenStoreRuntime.accounts[id].omegaExpiryFileTime</c>).</summary>
public interface IOmegaAccountStore
{
    /// <summary>The account's expiry text, trimmed, or null when it has none.</summary>
    string? GetOmegaExpiry(long accountId);
}

/// <summary>The outcome of looking up an account's Omega licence.</summary>
public sealed record OmegaLicense(bool HasLicense, long? ExpiryFileTime);

/// <summary>Resolves whether an account holds an Omega licence, from settings, its stored expiry and the clock.</summary>
public sealed class OmegaLicenses(SubscriptionSettings settings, IOmegaAccountStore accounts, Func<long> nowUnixMs)
{
    /// <summary>2100-01-01T00:00:00Z as a FILETIME: the licence every account has unless disabled.</summary>
    public const long DefaultOmegaExpiryFileTime = 157469184000000000L;

    public long ConfiguredExpiry => settings.OmegaLicenseEnabled ? DefaultOmegaExpiryFileTime : 0;

    public long NowFileTime => Py.FileTimeFromUnixMs(nowUnixMs());

    /// <summary><c>BigInt(text)</c> for a stored expiry; 0 when JS would throw.</summary>
    private static long ParseExpiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        var parsed = JsNumber.BigIntFromString(text);
        return parsed is { } value && value >= long.MinValue && value <= long.MaxValue ? (long)value : 0;
    }

    public OmegaLicense Resolve(PyValue? userId)
    {
        var accountId = JsSemantics.ToPositiveInteger(userId);
        var runtimeExpiry = accountId > 0 ? ParseExpiry(accounts.GetOmegaExpiry(accountId)) : 0;
        var effective = Math.Max(runtimeExpiry, ConfiguredExpiry);
        return effective > NowFileTime ? new OmegaLicense(true, effective) : new OmegaLicense(false, null);
    }
}

/// <summary>Subscription status queries the client makes while logging in.</summary>
[Service("subscriptionMgr")]
public sealed partial class SubscriptionMgrService(SubscriptionSettings settings, OmegaLicenses licenses)
{
    private const long CloneStateAlpha = 0;
    private const long CloneStateOmega = 1;
    private const long FileTimeTicksPerDay = 864000000000L;

    /// <summary>The clone grade forced by the environment, or null. Only 0/1 and the names alpha, trial and omega count.</summary>
    private long? CloneGradeOverride()
    {
        var raw = settings.CloneGradeOverride;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        var value = JsNumber.TrimJs(raw).ToLowerInvariant();
        switch (value)
        {
            case "omega":
                return CloneStateOmega;
            case "alpha" or "trial":
                return CloneStateAlpha;
        }
        var parsed = ParseInt10(value);
        return parsed is CloneStateAlpha or CloneStateOmega ? parsed : null;
    }

    /// <summary><c>Number.parseInt(text, 10)</c>: the leading integer, null when there is none.</summary>
    private static long? ParseInt10(string text)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
        var negative = false;
        if (index < text.Length && text[index] is '+' or '-')
        {
            negative = text[index] == '-';
            index++;
        }
        var start = index;
        long value = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9' && value < 100_000_000_000_000L)
        {
            value = (value * 10) + (text[index] - '0');
            index++;
        }
        return index == start ? null : negative ? -value : value;
    }

    private long ResolveCloneGrade(PyValue? userId)
    {
        // This client build only cleanly recognises Alpha (0) and Omega (1).
        if (CloneGradeOverride() is { } forced)
        {
            return forced;
        }
        if (licenses.Resolve(userId).HasLicense)
        {
            return CloneStateOmega;
        }
        return settings.OmegaLicenseEnabled ? CloneStateOmega : CloneStateAlpha;
    }

    private (long CloneGrade, bool IsSubscribed, long? EndTime, long DaysRemaining) State(ServiceCall call)
    {
        var userId = call.Session.Get("userid");
        var cloneGrade = ResolveCloneGrade(userId);
        var isSubscribed = cloneGrade == CloneStateOmega;
        var license = licenses.Resolve(userId);
        long? endTime = isSubscribed
            ? license.ExpiryFileTime ?? OmegaLicenses.DefaultOmegaExpiryFileTime
            : null;
        long days = 0;
        if (endTime is { } end)
        {
            var remaining = end - licenses.NowFileTime;
            if (remaining > 0)
            {
                days = (remaining + FileTimeTicksPerDay - 1) / FileTimeTicksPerDay;
            }
        }
        return (cloneGrade, isSubscribed, endTime, days);
    }

    private static PyValue EndTime(long? endTime) => endTime is { } value ? Py.FileTimeLong(value) : PyNone.Instance;

    [Method("GetSubscriptionStatus")]
    public PyValue GetSubscriptionStatus(ServiceCall call)
    {
        var state = State(call);
        return Py.KeyVal(
            ("isSubscribed", PyValue.Of(state.IsSubscribed)),
            ("subscriptionDaysRemaining", new PyInt(state.DaysRemaining)),
            ("subscriptionEndTime", EndTime(state.EndTime)));
    }

    [Method("GetSubscriptionInfo")]
    public PyValue GetSubscriptionInfo(ServiceCall call)
    {
        var state = State(call);
        return Py.KeyVal(
            ("isSubscribed", PyValue.Of(state.IsSubscribed)),
            ("subscriptionDaysRemaining", new PyInt(state.DaysRemaining)),
            ("cloneGrade", new PyInt(state.CloneGrade)),
            ("subscriptionEndTime", EndTime(state.EndTime)));
    }

    [Method("GetSubscriptionTime")]
    public PyValue GetSubscriptionTime(ServiceCall call) => EndTime(State(call).EndTime);

    [Method("GetCloneGrade")]
    public PyValue GetCloneGrade(ServiceCall call) => new PyInt(State(call).CloneGrade);
}


[Service("loginRewardFacilities")]
public sealed partial class LoginRewardFacilitiesService(SubscriptionMgrService subscriptions)
{
    private readonly SubscriptionMgrService _subscriptions =
        subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));

    [Method("get_my_clone_grade")]
    public PyValue GetMyCloneGrade(ServiceCall call) => _subscriptions.GetCloneGrade(call);
}
