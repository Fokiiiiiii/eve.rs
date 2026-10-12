using Eve.Dogma;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Space.Scanning;
using Eve.Ships;
using Eve.Skills;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The probe scan of the scanner probes in space (EVE.js <c>scanMgrService</c> <c>RequestScans</c>, <c>probeScanRuntime</c>): the probes a pilot has idle are moved
/// to where the client put them, scan for the duration the launcher and the scanning skills give, and the pilot is told what they found. The timed phases are
/// durable jobs (<see cref="StartJob"/> when the probes have arrived, <see cref="StopJob"/> when the scan is over), so a restart keeps them.
/// </summary>
public sealed class SpaceProbeScans
{
    /// <summary>The job that starts the scan once the probes have moved (<c>OnProbeWarpEnd</c>, then <c>OnSystemScanStarted</c>).</summary>
    public const string StartJob = "space.probes.scan.start";

    /// <summary>The job that ends the scan: the targets are read and the results told (<c>OnSystemScanStopped</c>).</summary>
    public const string StopJob = "space.probes.scan.stop";

    /// <summary>How long probes take to move to where the client put them (EVE.js <c>DEFAULT_PROBE_MOVE_DURATION_MS</c>).</summary>
    public const long MoveDurationMs = 1_250;

    /// <summary>The scan duration when the launcher gives none (EVE.js <c>DEFAULT_PROBE_SCAN_DURATION_MS</c>; the SDE gives one to every launcher).</summary>
    public const long DefaultDurationMs = 8_000;

    /// <summary>The scanning skills, whose bonuses apply (EVE.js <c>SCANNING_SKILL_TYPE_IDS</c>: Astrometric Rangefinding, Acquisition, Pinpointing, Astrometrics).</summary>
    private static readonly int[] ScanningSkills = [25739, 25811, 25810, 3412];

    /// <summary>The state of an idle probe and of one that is scanning (the client's <c>probeStateIdle</c> and <c>probeStateScanning</c>).</summary>
    private const int IdleState = 1;
    private const int ScanningState = 4;

    /// <summary>The probe scan group ids the client names results with (the client's <c>probescanning.const</c>).</summary>
    private const int GroupFighters = 2;
    private const int GroupShips = 4;
    private const int GroupDrones = 6;
    private const int GroupNpcs = 10;
    private const int GroupDeployables = 12;

    /// <summary>The kinds of scan target the simulation answers with (<c>crate::scene::scan_targets</c>).</summary>
    private const byte KindShip = 1;
    private const byte KindDrone = 2;
    private const byte KindFighter = 3;

    /// <summary>The scan-target query on the simulation (command 27, protocol 52).</summary>
    private const byte ScanTargetsCommand = 27;

    /// <summary>The SDE attributes a probe scan reads (the numbers of the SDE, the ids of the reference's constants).</summary>
    private const int AttrBaseScanRange = 1370, AttrBaseSensorStrength = 1371, AttrBaseMaxDeviation = 1372, AttrRangeFactor = 1373;
    private const int AttrProbeCanScanShips = 1413, AttrScanStrengthBonus = 846, AttrMaxDeviationModifier = 1156, AttrDurationBonus = 66;
    private const int AttrScanDuration = 73, AttrSignatureRadius = 552, AttrRadius = 162;
    private static readonly int[] SensorStrengthAttributes = [208, 209, 210, 211];

    private readonly SpaceSessions _space;
    private readonly ISimulation _sim;
    private readonly DogmaStatic _statics;
    private readonly PgProbeStore _store;
    private readonly CharacterShips _ships;
    private readonly PgSkillStore _skills;
    private readonly DurableScheduler _scheduler;
    private readonly SpaceProbes _probes;
    private readonly ICosmicSiteSource _sites;
    private readonly Func<long> _nowUnixMs;
    private readonly Random _random;

    public SpaceProbeScans(
        SpaceSessions space, ISimulation sim, DogmaStatic statics, PgProbeStore store, CharacterShips ships, PgSkillStore skills, DurableScheduler scheduler,
        SpaceProbes probes, Func<long> nowUnixMs, ICosmicSiteSource? sites = null, Random? random = null)
    {
        (_space, _sim, _statics, _store, _ships, _skills, _scheduler, _probes, _nowUnixMs) =
            (space, sim, statics, store, ships, skills, scheduler, probes, nowUnixMs);
        _sites = sites ?? NoCosmicSites.Instance;
        _random = random ?? Random.Shared;
    }

    /// <summary>The payload of both jobs: whose scan it is, in which system and ship, the probes it scans, which of them moved, and how long it lasts.</summary>
    public sealed record ScanRequest(long CharacterId, int SolarSystemId, long ShipId, ScanProbe[] Probes, long[] Moved, long DurationMs);

    /// <summary>Registers the two jobs with the scheduler (the composition calls this before the scheduler starts).</summary>
    public void Register()
    {
        _scheduler.Register(StartJob, async (context, token) => await BeginAsync(context.Payload<ScanRequest>()!, token));
        _scheduler.Register(StopJob, async (context, token) => await FinishAsync(context.Payload<ScanRequest>()!, token));
    }

    /// <summary>The scanMgr methods the probe scan answers (the bound object of the system scan manager).</summary>
    public ValueTask<PyValue> HandleAsync(string method, ServiceCall call) => method switch
    {
        "RequestScans" => RequestScansAsync(call.Session, call.Arg(0), call.Cancellation),
        "SetActivityState" => SetActivityStateAsync(call.Session, call.Arg(0), call.Arg(1), call.Cancellation),
        "RecoverProbes" => RecoverProbesAsync(call.Session, call.Arg(0), call.Cancellation),
        "ReconnectToLostProbes" => ReconnectAsync(call.Session, call.Cancellation),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };

    /// <summary><c>RecoverProbes(probeIDs)</c>: the probes that left, as the list the client counts (see <see cref="SpaceProbes.RecoverAsync"/>).</summary>
    private async ValueTask<PyValue> RecoverProbesAsync(ClientSession session, PyValue probes, CancellationToken cancellationToken) =>
        new PyList([.. (await _probes.RecoverAsync(session, ProbeIds(probes), cancellationToken)).Select(id => (PyValue)new PyLong(id))]);

    /// <summary><c>ReconnectToLostProbes()</c>: the pilot's probes are told again (see <see cref="SpaceProbes.ReconnectAsync"/>).</summary>
    private async ValueTask<PyValue> ReconnectAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await _probes.ReconnectAsync(session, cancellationToken);
        return PyNone.Instance;
    }

    /// <summary>
    /// <c>RequestScans(probes)</c>: the probes the pilot has idle in the system (the client's dict of probe id to probe) are moved to where they are and scan. Answers
    /// none, as the reference does; nothing is scanned when no probe is idle or a scan is running (the client does not ask then).
    /// </summary>
    public async ValueTask<PyValue> RequestScansAsync(ClientSession session, PyValue asking, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character || _space.ShipIdOf(session) is not { } shipId || _space.SolarSystemOf(session) is not { } system)
        {
            return PyNone.Instance;
        }
        var asked = ReadProbeRequests(asking);
        var now = _nowUnixMs();
        var own = (await _store.InSystemAsync(system, now, cancellationToken)).Where(record => record.CharacterId == character).ToDictionary(record => record.ProbeId);
        if (own.Values.Any(record => record.State == ScanningState))
        {
            return PyNone.Instance;
        }
        var chosen = asked.Keys.Where(id => own.TryGetValue(id, out var record) && record.State == IdleState).OrderBy(id => id).ToList();
        if (chosen.Count == 0)
        {
            return PyNone.Instance;
        }

        var launcher = await _ships.OnlineProbeLauncherAsync(character, shipId, cancellationToken);
        var terms = await ScanTermsAsync(character, launcher?.TypeId, cancellationToken);
        var probes = new List<ScanProbe>();
        var moved = new List<long>();
        var warps = new List<(long ProbeId, double[] From, double[] To)>();
        foreach (var id in chosen)
        {
            var record = own[id];
            var patch = asked[id];
            var destination = patch.Destination ?? record.Destination;
            var step = patch.RangeStep is >= 1 and <= ProbeScanMath.RangeStepCount ? patch.RangeStep.Value : record.RangeStep;
            var type = await ProbeTypeAsync(record.TypeId, cancellationToken);
            var baseRange = type.Attributes.GetValueOrDefault(AttrBaseScanRange);
            var factor = type.Attributes.GetValueOrDefault(AttrRangeFactor, 2);
            var scanRange = ProbeScanMath.ScanRangeMeters(baseRange, factor, step);
            var strength = ProbeScanMath.StrengthAtStep(type.Attributes.GetValueOrDefault(AttrBaseSensorStrength), factor, step, terms.StrengthBonusPercent);
            var deviation = ProbeScanMath.MaxDeviationMeters(
                type.Attributes.GetValueOrDefault(AttrBaseMaxDeviation), baseRange, scanRange, terms.DeviationFactor);
            var combat = type.Attributes.GetValueOrDefault(AttrProbeCanScanShips) > 0;

            var updated = record with { Position = destination, Destination = destination, ScanRange = scanRange, RangeStep = step, State = ScanningState };
            if (!SamePlace(record.Position, destination))
            {
                moved.Add(id);
                warps.Add((id, record.Position, destination));
                _probes.MoveBall(updated);
            }
            await _store.SaveAsync(updated, cancellationToken);
            probes.Add(new ScanProbe(id, destination, scanRange, strength, deviation, combat));
        }

        var request = new ScanRequest(character, system, shipId, [.. probes], [.. moved], terms.DurationMs);
        if (moved.Count == 0)
        {
            await BeginAsync(request, session, cancellationToken);
            return PyNone.Instance;
        }
        var startTime = Py.FileTimeFromUnixMs(now);
        foreach (var (probeId, from, to) in warps)
        {
            session.SendNotification("OnProbeWarpStart", "clientID", new PyLong(probeId), Vector(from), Vector(to), new PyLong(startTime), new PyInt(MoveDurationMs));
        }
        await _scheduler.ScheduleAsync(new JobSpec(StartJob, now + MoveDurationMs)
        {
            Key = character.ToString(),
            Payload = request,
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
        return PyNone.Instance;
    }

    /// <summary>The probes' activity (<c>SetActivityState(probeIDs, active)</c>): an idle probe is made inactive, an inactive one idle again; a probe that scans is left.</summary>
    private async ValueTask<PyValue> SetActivityStateAsync(ClientSession session, PyValue probes, PyValue activity, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character || _space.SolarSystemOf(session) is not { } system || probes is not PyList and not PyTuple)
        {
            return PyNone.Instance;
        }
        var active = activity is PyBool { Value: true };
        var ids = ProbeIds(probes);
        var own = (await _store.InSystemAsync(system, _nowUnixMs(), cancellationToken)).Where(record => record.CharacterId == character).ToDictionary(record => record.ProbeId);
        foreach (var id in ids.Where(id => own.TryGetValue(id, out var record) && record.State is IdleState or 0))
        {
            await _store.SaveAsync(own[id] with { State = active ? IdleState : 0 }, cancellationToken);
        }
        return PyNone.Instance;
    }

    /// <summary>The start job: the probes that moved have arrived, and the scan starts (<c>OnSystemScanStarted</c>).</summary>
    private async Task BeginAsync(ScanRequest request, CancellationToken cancellationToken) =>
        await BeginAsync(request, _space.SessionOfShip(request.ShipId), cancellationToken);

    private async Task BeginAsync(ScanRequest request, ClientSession? session, CancellationToken cancellationToken)
    {
        var now = _nowUnixMs();
        if (session is not null)
        {
            foreach (var probeId in request.Moved)
            {
                session.SendNotification("OnProbeWarpEnd", "clientID", new PyLong(probeId));
            }
            var probes = new List<KeyValuePair<PyValue, PyValue>>();
            foreach (var probe in request.Probes)
            {
                if (await _store.GetAsync(probe.ProbeId, cancellationToken) is { } record)
                {
                    probes.Add(new(new PyLong(probe.ProbeId), SpaceProbes.ProbeKeyVal(record)));
                }
            }
            session.SendNotification("OnSystemScanStarted", "solarsystemid2", new PyLong(Py.FileTimeFromUnixMs(now)), new PyLong(request.DurationMs), new PyDict(probes));
        }
        await _scheduler.ScheduleAsync(new JobSpec(StopJob, now + request.DurationMs)
        {
            Key = request.CharacterId.ToString(),
            Payload = request,
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
    }

    /// <summary>The stop job: the targets are read from the simulation, the probes are worked out and set idle, and the results are told (<c>OnSystemScanStopped</c>).</summary>
    private async Task FinishAsync(ScanRequest request, CancellationToken cancellationToken)
    {
        // the probes are idle first: when the reading below fails, the job is tried again and the probes are not left scanning for good
        var scanned = new List<ScanProbe>();
        var probeIds = new List<PyValue>();
        foreach (var probe in request.Probes)
        {
            if (await _store.GetAsync(probe.ProbeId, cancellationToken) is not { } record)
            {
                continue;
            }
            scanned.Add(probe);
            probeIds.Add(new PyLong(probe.ProbeId));
            await _store.SaveAsync(record with { State = IdleState }, cancellationToken);
        }
        var session = _space.SessionOfShip(request.ShipId);
        var targets = new List<ScanTargetSite>();
        if (session is not null)
        {
            targets.AddRange(await SimulationTargetsAsync(session, cancellationToken));
        }
        targets.AddRange(await _sites.SitesInAsync(request.SolarSystemId, cancellationToken));

        var outcome = ProbeScanEngine.Compute(scanned, targets, _random);
        if (session is null)
        {
            return;
        }
        _space.WarpAuthority.Record(session, request.SolarSystemId, outcome.Results);
        session.SendNotification(
            "OnSystemScanStopped", "solarsystemid2",
            new PyList(probeIds),
            new PyList([.. outcome.Results.Select(ResultRow)]),
            new PyList([.. outcome.AbsentTargets.Select(id => (PyValue)new PyLong(id))]));
    }

    /// <summary>The targets of the system as the simulation sees them (<c>ScanTargets</c>, command 27), with the signal each needs.</summary>
    private async Task<IReadOnlyList<ScanTargetSite>> SimulationTargetsAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var reply = await _sim.CommandAsync((ulong)session.SessionId, ScanTargetsCommand, [], cancellationToken);
        var sites = new List<ScanTargetSite>();
        foreach (var target in reply.ScanTargets)
        {
            await _statics.EnsureTypesAsync([target.TypeId], cancellationToken);
            if (_statics.Type(target.TypeId) is not { } type)
            {
                continue;
            }
            var group = target.Kind switch
            {
                KindDrone => GroupDrones,
                KindFighter => GroupFighters,
                KindShip when _space.SessionOfShip(target.Id) is null => GroupNpcs,
                KindShip => GroupShips,
                _ => GroupDeployables,
            };
            var sensor = SensorStrengthAttributes.Max(attribute => type.Attributes.GetValueOrDefault(attribute));
            var signature = type.Attributes.GetValueOrDefault(AttrSignatureRadius, type.Attributes.GetValueOrDefault(AttrRadius, 1));
            sites.Add(new ScanTargetSite(
                target.Id, target.TypeId, type.GroupId, group, [.. target.Position],
                ProbeScanMath.RequiredStrength(sensor, signature), IsSignature: false));
        }
        return sites;
    }

    /// <summary>The scan's terms from the pilot's skills and the launcher (SDE): the strength bonus in percent (skill and launcher 846), the deviation factor (skill
    /// 1156, one factor per skill) and the duration (the launcher's 73, times each skill's bonus 66).</summary>
    private async Task<ScanTerms> ScanTermsAsync(long character, int? launcherTypeId, CancellationToken cancellationToken)
    {
        var skills = await _skills.ForCharacterAsync(character, cancellationToken);
        var types = launcherTypeId is { } launcher ? ScanningSkills.Append(launcher).ToArray() : ScanningSkills;
        await _statics.EnsureTypesAsync(types, cancellationToken);
        var strength = launcherTypeId is { } type ? _statics.Type(type)?.Attributes.GetValueOrDefault(AttrScanStrengthBonus) ?? 0 : 0;
        var deviation = 1.0;
        var duration = 1.0;
        foreach (var skillId in ScanningSkills)
        {
            var level = skills.FirstOrDefault(skill => skill.TypeId == skillId)?.Level ?? 0;
            if (level <= 0 || _statics.Type(skillId) is not { } skill)
            {
                continue;
            }
            strength += skill.Attributes.GetValueOrDefault(AttrScanStrengthBonus) * level;
            if (skill.Attributes.TryGetValue(AttrMaxDeviationModifier, out var deviationPerLevel))
            {
                deviation *= ProbeScanMath.DeviationFactor(deviationPerLevel, level);
            }
            if (skill.Attributes.TryGetValue(AttrDurationBonus, out var durationPerLevel))
            {
                duration *= ProbeScanMath.DurationFactor(durationPerLevel, level);
            }
        }
        var baseDuration = launcherTypeId is { } launcherType
            ? _statics.Type(launcherType)?.Attributes.GetValueOrDefault(AttrScanDuration, DefaultDurationMs) ?? DefaultDurationMs
            : DefaultDurationMs;
        return new ScanTerms(strength, deviation, Math.Max(1, (long)Math.Round(baseDuration * duration)));
    }

    private sealed record ScanTerms(double StrengthBonusPercent, double DeviationFactor, long DurationMs);

    private async Task<TypeDef> ProbeTypeAsync(int typeId, CancellationToken cancellationToken)
    {
        await _statics.EnsureTypesAsync([typeId], cancellationToken);
        return _statics.Type(typeId) ?? throw new InvalidOperationException($"the probe type {typeId} is not in the static data");
    }

    /// <summary>A result as the client's row: its id, scan group, group and type (named only as far as the certainty goes), its strength attribute, its item, its
    /// certainty, its data (a position at 100%, else the largest deviation) and its position.</summary>
    private static PyValue ResultRow(ScanResultRow row)
    {
        var certainty = row.Certainty;
        var named = certainty >= ProbeScanMath.ResultGroup;
        var typed = certainty >= ProbeScanMath.ResultName;
        return Py.KeyVal(
            ("id", row.Code is { } code ? new PyStr(code) : new PyLong(row.Id)),
            ("scanGroupID", new PyInt(row.ScanGroupId)),
            ("groupID", named ? new PyInt(row.GroupId) : PyNone.Instance),
            ("typeID", typed ? new PyInt(row.TypeId) : PyNone.Instance),
            // the kind of a cosmic site (data, relic, ...) is known with its group; its dungeon, with its name
            ("strengthAttributeID", row.Code is not null && !named ? PyNone.Instance : new PyInt(row.StrengthAttributeId)),
            ("dungeonID", typed ? Optional(row.DungeonId) : PyNone.Instance),
            ("dungeonNameID", typed ? Optional(row.DungeonNameId) : PyNone.Instance),
            ("archetypeID", named ? Optional(row.ArchetypeId) : PyNone.Instance),
            ("factionID", typed ? Optional(row.FactionId) : PyNone.Instance),
            ("itemID", row.Code is not null ? PyNone.Instance : new PyLong(row.ItemId)),
            ("difficulty", new PyInt(row.Difficulty)),
            ("certainty", new PyReal(certainty)),
            ("data", row.Perfect ? Vector(row.Position) : new PyReal(row.DeviationMeters)),
            ("pos", Vector(row.Position)));
    }

    private static PyValue Optional(int? value) => value is { } number ? new PyInt(number) : PyNone.Instance;

    /// <summary>A position as the client's vector (a tuple of three reals).</summary>
    private static PyValue Vector(double[] v) => new PyTuple([new PyReal(v[0]), new PyReal(v[1]), new PyReal(v[2])]);

    private static bool SamePlace(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) < 0.001 && Math.Abs(a[1] - b[1]) < 0.001 && Math.Abs(a[2] - b[2]) < 0.001;

    /// <summary>The probes a request names, with the position and range step the client gives (its <c>util.KeyVal</c> of probe fields).</summary>
    private static Dictionary<long, ProbeRequest> ReadProbeRequests(PyValue? value)
    {
        var asked = new Dictionary<long, ProbeRequest>();
        if (value is not PyDict dict)
        {
            return asked;
        }
        foreach (var (key, probe) in dict.Entries)
        {
            if (Number(key) is not { } id || probe is not PyObject { Name: PyStr { Value: "util.KeyVal" }, Args: PyDict fields })
            {
                continue;
            }
            var field = fields.Entries.ToDictionary(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) ?? "", entry => entry.Value);
            asked[(long)id] = new ProbeRequest(
                field.TryGetValue("destination", out var destination) ? Vector3(destination) : null,
                field.TryGetValue("rangeStep", out var step) && Number(step) is { } number ? (int)number : null);
        }
        return asked;
    }

    private sealed record ProbeRequest(double[]? Destination, int? RangeStep);

    private static IReadOnlyList<long> ProbeIds(PyValue value) =>
        (value is PyList list ? list.Items : value is PyTuple tuple ? tuple.Items : []).Select(item => Number(item) is { } id ? (long)id : 0).Where(id => id > 0).ToList();

    private static double? Number(PyValue value) => value switch
    {
        PyInt i => i.Value,
        PyLong l => l.Value,
        PyReal r => r.Value,
        _ => null,
    };

    private static double[]? Vector3(PyValue value)
    {
        var items = value switch { PyTuple t => t.Items, PyList l => l.Items, _ => null };
        if (items is null || items.Count != 3 || items.Select(Number).Any(n => n is null))
        {
            return null;
        }
        return [Number(items[0])!.Value, Number(items[1])!.Value, Number(items[2])!.Value];
    }
}
