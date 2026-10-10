using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The scanner probes of the ships in space (EVE.js <c>probeRuntimeState</c> and <c>Handle_LaunchProbes</c>): a launch from a probe launcher that the pilot has online
/// puts up to eight probes around the ship, each as a record and a ball in space; a probe is recalled, or leaves when its time is up, and the pilot is told.
/// </summary>
public sealed class SpaceProbes : ISpaceProbes
{
    /// <summary>The most probes a character controls in a system (EVE.js <c>MAX_ACTIVE_PROBES</c>).</summary>
    public const int MaxActiveProbes = 8;

    /// <summary>The ring a launch spreads its probes on, in metres (EVE.js <c>DEFAULT_LAUNCH_RING_RADIUS_METERS</c>).</summary>
    public const double LaunchRingRadiusMeters = 10_000;

    /// <summary>A probe's scan range step when it is launched (the client sets its own from here).</summary>
    private const int LaunchRangeStep = 7;

    /// <summary>The state of an idle probe (EVE.js <c>probeStateIdle</c>, the client's <c>probeStateIdle</c>).</summary>
    private const int IdleState = 1;

    private readonly CharacterShips _ships;
    private readonly DogmaStatic _statics;
    private readonly PgProbeStore _store;
    private readonly SpaceSessions _space;
    private readonly ISimulation _sim;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;

    public SpaceProbes(
        CharacterShips ships, DogmaStatic statics, PgProbeStore store, SpaceSessions space, ISimulation sim, Func<long> nowUnixMs, Action<string>? log = null)
    {
        (_ships, _statics, _store, _space, _sim, _nowUnixMs, _log) = (ships, statics, store, space, sim, nowUnixMs, log);
    }

    /// <summary>The offset of the <paramref name="index"/>th probe on the ring of <paramref name="count"/> (EVE.js <c>buildLaunchOffset</c>).</summary>
    internal static double[] RingOffset(int index, int count, double radius)
    {
        var angle = 2 * Math.PI * Math.Max(0, index) / Math.Max(1, count);
        return [Math.Cos(angle) * radius, 0, Math.Sin(angle) * radius];
    }

    public async Task LaunchAsync(ClientSession session, long moduleId, int count, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character || session.GetInt("shipid") is not { } shipId || shipId <= 0)
        {
            throw Refusal("DeniedShipChanged");
        }
        var system = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        // the reference's order: in space first, then the launcher, its online state, its charge, the rounds asked for, and the cap
        if (system <= 0 || await _space.PositionOfAsync(session, shipId, cancellationToken) is not { } origin)
        {
            throw Refusal("DeniedShipChanged");
        }
        var state = await _ships.ProbeLaunchStateAsync(character, shipId, moduleId, cancellationToken);
        if (state.Refusal is { } refusal)
        {
            throw RefusalOf(refusal, state.ModuleTypeId);
        }
        if (count > state.Quantity)
        {
            throw Notify("You do not have enough loaded scanner probes.");
        }
        var active = await _store.ActiveCountAsync(character, system, cancellationToken);
        if (active + count > MaxActiveProbes)
        {
            throw Notify("You cannot control more than eight active probes.");
        }
        if (!await _ships.SpendProbesAsync(character, state.ChargeId, count, cancellationToken))
        {
            throw Notify("You do not have enough loaded scanner probes.");
        }

        var type = _statics.Type(state.ProbeTypeId);
        var now = _nowUnixMs();
        var corporation = (int)(session.GetInt("corpid") ?? 0);
        var alliance = (int)(session.GetInt("allianceid") ?? 0);
        for (var index = 0; index < count; index++)
        {
            var offset = RingOffset(active + index, MaxActiveProbes, LaunchRingRadiusMeters);
            var position = new[] { origin.X + offset[0], origin.Y + offset[1], origin.Z + offset[2] };
            var record = new ScanProbeRecord(
                await _store.NextIdAsync(cancellationToken), character, system, state.ProbeTypeId, CharacterShips.ScanProbeGroup, moduleId, shipId,
                position, [.. position], 0, LaunchRangeStep, IdleState, now + state.LifetimeMs);
            await _store.SaveAsync(record, cancellationToken);
            _sim.PlaceProbe(new SimPlaceProbe(
                record.ProbeId, system, type?.Name ?? "Scanner Probe", record.TypeId, record.GroupId, position, RadiusOf(type),
                character, corporation, alliance, shipId));
            session.SendNotification("OnNewProbe", "clientID", ProbeKeyVal(record));
        }
    }

    /// <summary>The ball radius of a probe: its radius attribute (162), or a fallback when its type gives none (the Core probes of the SDE have none).</summary>
    private const double ProbeRadiusFallback = 40;

    private const int RadiusAttribute = 162;

    private static double RadiusOf(TypeDef? type) =>
        type is not null && type.Attributes.TryGetValue(RadiusAttribute, out var radius) && radius > 0 ? radius : ProbeRadiusFallback;

    public async Task RecallAsync(ClientSession session, long probeId, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character || await _store.GetAsync(probeId, cancellationToken) is not { } record || record.CharacterId != character)
        {
            return;
        }
        await Forget(record, session, cancellationToken);
    }

    /// <summary>The probes whose time is up leave space, and their pilots (when they are in space) are told (the sweep's job).</summary>
    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var record in await _store.DueAsync(_nowUnixMs(), cancellationToken))
            {
                await Forget(record, _space.SessionOfShip(record.ShipId), cancellationToken);
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the scanner probes whose time ran out were not removed: {error.Message}");
        }
    }

    /// <summary>A probe leaves: its record goes, its ball is taken out of space and its pilot is told (<c>OnRemoveProbe</c>).</summary>
    private async Task Forget(ScanProbeRecord record, ClientSession? session, CancellationToken cancellationToken)
    {
        await _store.RemoveAsync(record.ProbeId, cancellationToken);
        _sim.RemoveProbe(record.ProbeId);
        session?.SendNotification("OnRemoveProbe", "clientID", new PyLong(record.ProbeId));
    }

    private static MachoWrappedException Refusal(string name) => MachoErrors.ThrowUserError(name);

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));

    /// <summary>
    /// A refusal of the launch by its name (EVE.js <c>_throwProbeLaunchUserError</c>). shortcut: a launcher with no charges is named by its type id; the reference
    /// names it by its type value, whose rendering the client does not depend on.
    /// </summary>
    private MachoWrappedException RefusalOf(string reason, int launcherTypeId) => reason switch
    {
        "NOT_IN_SPACE" or "MODULE_NOT_FOUND" or "SHIP_NOT_FOUND" => Refusal("DeniedShipChanged"),
        "MODULE_NOT_ONLINE" => Notify($"{_statics.Type(launcherTypeId)?.Name ?? "That module"} is offline."),
        "INVALID_LAUNCHER" => Notify("That module cannot launch scanner probes."),
        "NO_CHARGES" => MachoErrors.ThrowUserError("NoCharges", ("launcher", new PyInt(launcherTypeId))),
        "INVALID_CHARGE" => Notify("The loaded charge is not a valid scanner probe."),
        _ => Notify("That scanner probe does not fit in this probe launcher."),
    };

    /// <summary>The probe as the client's <c>util.KeyVal</c> (EVE.js <c>buildProbeKeyVal</c>): its id, type, place, range, state and the scan bonuses (none yet).</summary>
    internal static PyValue ProbeKeyVal(ScanProbeRecord probe) => Py.KeyVal(
        ("probeID", new PyLong(probe.ProbeId)),
        ("typeID", new PyInt(probe.TypeId)),
        ("pos", Reals(probe.Position)),
        ("destination", Reals(probe.Destination)),
        ("scanRange", new PyReal(probe.ScanRange)),
        ("rangeStep", new PyInt(probe.RangeStep)),
        ("state", new PyInt(probe.State)),
        ("scanBonuses", ZeroScanBonuses()),
        ("expiry", new PyLong(Py.FileTimeFromUnixMs(probe.ExpiresAtMs))));

    private static PyValue Reals(IEnumerable<double> values) => new PyList([.. values.Select(v => (PyValue)new PyReal(v))]);

    /// <summary>The probe's scan bonuses (EVE.js <c>buildScanBonusesDict</c>): strength, deviation and duration, each by modules, ship, skills, implants, boosters.</summary>
    private static PyValue ZeroScanBonuses()
    {
        PyValue Origins() => new PyDict([.. new[] { "modules", "ship", "skills", "implants", "boosters" }.Select(o => new KeyValuePair<PyValue, PyValue>(new PyStr(o), new PyReal(0)))]);
        return new PyDict([.. new[] { "strength", "deviation", "duration" }.Select(b => new KeyValuePair<PyValue, PyValue>(new PyStr(b), Origins()))]);
    }
}
