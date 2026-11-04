using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;

namespace Eve.Services.Planets;

/// <summary>The client's planetOrbitalRegistryBroker, bound to the orbital's solar system.</summary>
[Service("planetOrbitalRegistryBroker")]
public sealed partial class PlanetOrbitalRegistryService(
    PlanetMgrService planets,
    Func<long> nowMs,
    Func<long, CancellationToken, Task<OrbitalSettings?>> skyhookSettings,
    Func<ServiceCall, long, OrbitalSettings, CancellationToken, Task> updateSkyhookSettings,
    Func<ClientSession, long, CancellationToken, Task<bool>> canSelectAccessGroup)
{
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>MachoBindObject(solarSystemID, (method, args, kwargs)): binds the system registry, then dispatches its nested call.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowMs())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = [.. nested.Items.Count > 1 && nested.Items[1] is PyTuple args ? args.Items : []],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    /// <summary>GetTaxRate(customsOfficeID): the rate the pilot pays at that office, or nothing when it is not one of the server's.</summary>
    [Method("GetTaxRate")]
    public async ValueTask<PyValue> GetTaxRate(ServiceCall call)
    {
        var office = call.Arg(0) switch { PyInt i => i.Value, PyLong l => l.Value, _ => 0L };
        if (call.Session.CharacterId is not { } character) return PyNone.Instance;
        return await planets.TaxRateAsync(office, character, call.Cancellation) is { } rate ? new PyReal(rate) : PyNone.Instance;
    }

    /// <summary>Returns the six values consumed by orbitalConfiguration.py for POCOs and Skyhooks.</summary>
    [Method("GetSettingsInfo")]
    public async ValueTask<PyValue> GetSettingsInfo(ServiceCall call)
    {
        if (call.Args.Count != 1)
            throw InvalidSettings();
        var orbitalId = PositiveInteger(call.Arg(0));
        var settings = Customs.PlanetOf(orbitalId) is not null
            ? await planets.OfficeSettingsAsync(orbitalId, call.Cancellation)
            : await skyhookSettings(orbitalId, call.Cancellation);
        if (settings is null)
            throw MachoErrors.ThrowUserError("CustomsOfficeNotFound");
        return SettingsInfo(settings);
    }

    /// <summary>UpdateSettings(orbitalID, reinforceHour, taxRateValues, standingValue, allowAllianceValue, allowStandingsValue, aclGroupID).</summary>
    [Method("UpdateSettings")]
    public async ValueTask<PyValue> UpdateSettings(ServiceCall call)
    {
        if (call.Args.Count != 7)
            throw InvalidSettings();
        var orbitalId = PositiveInteger(call.Arg(0));
        var settings = new OrbitalSettings(
            ReinforceHour(call.Arg(1)),
            TaxRates(call.Arg(2)),
            StandingLevel(call.Arg(3)),
            Boolean(call.Arg(4)),
            Boolean(call.Arg(5)),
            AccessGroupId(call.Arg(6)));

        if (Customs.PlanetOf(orbitalId) is not null)
        {
            await planets.UpdateOfficeSettingsAsync(call.Session, orbitalId, settings, call.Cancellation);
        }
        else
        {
            if (settings.AccessGroupId is { } groupId &&
                !await canSelectAccessGroup(call.Session, groupId, call.Cancellation))
                throw MachoErrors.ThrowUserError("CustomsOfficeAccessDenied");
            await updateSkyhookSettings(call, orbitalId, settings, call.Cancellation);
        }
        return PyNone.Instance;
    }

    private static PyValue SettingsInfo(OrbitalSettings settings) => Py.Tuple(
        new PyInt(settings.ReinforceHour),
        Py.KeyVal(
            ("corporation", new PyReal(settings.TaxRates.Corporation)),
            ("alliance", new PyReal(settings.TaxRates.Alliance)),
            ("standingHigh", new PyReal(settings.TaxRates.Excellent)),
            ("standingGood", new PyReal(settings.TaxRates.Good)),
            ("standingNeutral", new PyReal(settings.TaxRates.Neutral)),
            ("standingBad", new PyReal(settings.TaxRates.Bad)),
            ("standingHorrible", new PyReal(settings.TaxRates.Terrible))),
        new PyInt(settings.StandingLevel),
        settings.AllowAlliance ? PyBool.True : PyBool.False,
        settings.AllowStandings ? PyBool.True : PyBool.False,
        settings.AccessGroupId is { } groupId ? new PyLong(groupId) : PyNone.Instance);

    private static long PositiveInteger(PyValue value)
    {
        var number = value switch { PyInt i => i.Value, PyLong l => l.Value, _ => 0L };
        return number > 0 ? number : throw InvalidSettings();
    }

    private static int ReinforceHour(PyValue value)
    {
        var hour = value switch { PyInt i => i.Value, PyLong l => l.Value, _ => -1L };
        return hour is >= 0 and <= 23 ? (int)hour : throw InvalidSettings();
    }

    private static int StandingLevel(PyValue value)
    {
        var standing = value switch { PyInt i => i.Value, PyLong l => l.Value, _ => long.MinValue };
        return standing is >= -10 and <= 10 ? (int)standing : throw InvalidSettings();
    }

    private static bool Boolean(PyValue value) =>
        value is PyBool flag ? flag.Value : throw InvalidSettings();

    private static long? AccessGroupId(PyValue value) => value switch
    {
        PyNone => null,
        PyInt { Value: > 0 } group => group.Value,
        PyLong { Value: > 0 } group => group.Value,
        _ => throw InvalidSettings(),
    };

    private static Customs.OwnerTaxRates TaxRates(PyValue value)
    {
        if (value is not PyObject { Args: PyDict rates } keyVal ||
            JsSemantics.StringValue(keyVal.Name) != "util.KeyVal")
            throw InvalidSettings();
        return new Customs.OwnerTaxRates(
            Rate(rates, "corporation"), Rate(rates, "alliance"), Rate(rates, "standingHigh"),
            Rate(rates, "standingGood"), Rate(rates, "standingNeutral"), Rate(rates, "standingBad"),
            Rate(rates, "standingHorrible"));
    }

    private static double Rate(PyDict rates, string key)
    {
        var matches = rates.Entries
            .Where(entry => JsSemantics.StringValue(entry.Key) == key)
            .Select(entry => entry.Value)
            .ToArray();
        var value = matches.Length == 1 ? matches[0] switch
        {
            PyReal real => real.Value,
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => double.NaN,
        } : double.NaN;
        return double.IsFinite(value) ? value : throw InvalidSettings();
    }

    private static Exception InvalidSettings() => MachoErrors.ThrowUserError("CustomsOfficeNotFound");
}
