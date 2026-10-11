using Eve.Dogma;
using Eve.Marshal;
using Eve.Services;
using Eve.Space;
using Eve.Ships;

namespace Eve.Services.Space.Scanning;

/// <summary>
/// The directional scan (EVE.js <c>scanMgrService</c> <c>Handle_ConeScan</c>, the wiki's "Directional scanning"): from the ship, along the scan direction, the cone of
/// the scan angle out to the scan range shows the objects in it by name and type, and only that (the rows are the client's: the item id, the type and the group).
/// The scene says what there is (<see cref="SimDirectionalTargets"/>); this decides what a scan shows.
/// </summary>
public sealed class SpaceDirectionalScans
{
    /// <summary>The range the scanner reaches at most when the ship says none (EVE.js <c>DSCAN_FALLBACK_MAX_RANGE_METERS</c>; the wiki: "up to 14.3AU").</summary>
    private const double FallbackMaxRangeMeters = 14.3 * ProbeScanMath.AuMeters;

    /// <summary>The shortest range a scan can be asked for (EVE.js <c>DSCAN_MIN_RANGE_METERS</c>).</summary>
    private const double MinRangeMeters = 10_000;

    /// <summary>The widest cone, a sphere (EVE.js <c>DSCAN_FULL_SWEEP_RADIANS</c>).</summary>
    private const double FullSweepRadians = Math.PI * 2;

    /// <summary>The ship's largest directional range (SDE attribute 5796, maxDirectionalScanRange); a type that gives none has the fallback.</summary>
    private const int AttrMaxDirectionalScanRange = 5796;

    /// <summary>Whether a type is out of directional scans (SDE attribute 1958, dscanImmune).</summary>
    private const int AttrDirectionalScanImmune = 1958;

    /// <summary>The radius of a type (SDE attribute 162): a ship's radius for the cone (EVE.js: an entity's radius is its type's).</summary>
    private const int AttrRadius = 162;

    /// <summary>The groups the wiki's list shows: the wrecks (not corpses), the planets, the moons, the asteroid belts, the stargates and the stations.</summary>
    private const int GroupWreck = 186;
    private const int GroupCombatRecon = 906;
    private static readonly HashSet<int> CelestialGroups = [7, 8, 9, 10, 15];

    /// <summary>The kinds of directional target (<see cref="SimDirectionalTarget"/>, the simulation's numbering).</summary>
    private const byte KindShip = 1;
    private const byte KindDrone = 3;
    private const byte KindProbe = 5;
    private const byte KindWreck = 6;
    private const byte KindCelestial = 7;

    /// <summary>The directional scan query on the simulation (command 28, protocol 54).</summary>
    private const byte DirectionalScanCommand = 28;

    private readonly SpaceSessions _space;
    private readonly ISimulation _sim;
    private readonly DogmaStatic _statics;

    public SpaceDirectionalScans(SpaceSessions space, ISimulation sim, DogmaStatic statics)
    {
        (_space, _sim, _statics) = (space, sim, statics);
    }

    /// <summary>
    /// <c>ConeScan(angle, range, dx, dy, dz)</c>: what is in the cone, as the client's rows. An empty list when the pilot is not in space or the direction is none
    /// (the reference's answers too). The angle and the range are clamped to what the scanner does (a sphere at most, 10 km at least, the ship's range at most).
    /// </summary>
    public async Task<PyValue> ConeScanAsync(ClientSession session, PyValue angle, PyValue range, PyValue dx, PyValue dy, PyValue dz, CancellationToken cancellationToken)
    {
        if (_space.ShipIdOf(session) is not { } shipId || _space.ShipTypeIdOf(session) is not { } shipType
            || await _space.PositionOfAsync(session, shipId, cancellationToken) is not { } ego)
        {
            return new PyList([]);
        }
        var direction = Normalize([Number(dx) ?? 0, Number(dy) ?? 0, Number(dz) ?? 0]);
        if (direction is null)
        {
            return new PyList([]);
        }
        await _statics.EnsureTypesAsync([shipType], cancellationToken);
        var shipAttributes = _statics.Type(shipType)?.Attributes ?? new Dictionary<int, double>();
        var egoRadius = shipAttributes.GetValueOrDefault(AttrRadius);
        var shipRange = shipAttributes.TryGetValue(AttrMaxDirectionalScanRange, out var reach) && reach > 0 ? reach : FallbackMaxRangeMeters;
        var rangeCap = Math.Max(MinRangeMeters, shipRange);
        var requestedRange = Number(range) ?? FallbackMaxRangeMeters;
        var effectiveRange = Math.Clamp(double.IsFinite(requestedRange) ? requestedRange : FallbackMaxRangeMeters, MinRangeMeters, rangeCap);
        var requestedAngle = Number(angle) ?? FullSweepRadians;
        var effectiveAngle = Math.Clamp(double.IsFinite(requestedAngle) ? requestedAngle : FullSweepRadians, 0, FullSweepRadians);
        var fullSweep = effectiveAngle >= FullSweepRadians - 0.0001;
        var halfAngleCosine = fullSweep ? -1 : Math.Cos(Math.Max(0, effectiveAngle) / 2);

        var reply = await _sim.CommandAsync((ulong)session.SessionId, DirectionalScanCommand, [], cancellationToken);
        await _statics.EnsureTypesAsync(reply.DirectionalTargets.Select(target => target.TypeId).Distinct().ToArray(), cancellationToken);

        var found = new List<(double Surface, long Id, int TypeId, int GroupId)>();
        foreach (var target in reply.DirectionalTargets)
        {
            if (_statics.Type(target.TypeId) is not { } type || !Visible(target, type))
            {
                continue;
            }
            var offset = new[] { target.Position[0] - ego.X, target.Position[1] - ego.Y, target.Position[2] - ego.Z };
            var surface = Math.Max(0, Length(offset) - egoRadius - target.Radius);
            if (surface > effectiveRange)
            {
                continue;
            }
            if (!fullSweep)
            {
                var toward = Normalize(offset);
                var inCone = toward is null ? surface <= 0 : Dot(toward, direction) >= halfAngleCosine;
                if (!inCone)
                {
                    continue;
                }
            }
            found.Add((surface, target.Id, target.TypeId, type.GroupId));
        }
        return new PyList(found
            .OrderBy(entry => entry.Surface)
            .ThenBy(entry => entry.Id)
            .Select(entry => (PyValue)Py.KeyVal(("id", new PyLong(entry.Id)), ("typeID", new PyInt(entry.TypeId)), ("groupID", new PyInt(entry.GroupId))))
            .ToList());
    }

    /// <summary>
    /// What a scan shows of a target (the wiki's list): a ship that is not cloaked, not in warp and not a combat recon ship; a drone; a scanner probe; a wreck (not a
    /// corpse); the planets, moons, asteroid belts, stargates and stations; and nothing that is out of directional scans. Not shown: the NPCs (the wiki shows only
    /// those named with a diamond, and the simulation keeps no names of its NPCs), the fighter squadrons, the containers, the cosmic signatures and the beacons.
    /// </summary>
    private static bool Visible(SimDirectionalTarget target, TypeDef type)
    {
        if (type.Attributes.GetValueOrDefault(AttrDirectionalScanImmune) > 0)
        {
            return false;
        }
        return target.Kind switch
        {
            KindShip => !target.Cloaked && !target.Warping && type.GroupId != GroupCombatRecon,
            KindDrone => true,
            KindProbe => true,
            KindWreck => type.GroupId == GroupWreck,
            KindCelestial => CelestialGroups.Contains(type.GroupId),
            _ => false,
        };
    }

    private static double? Number(PyValue value) => value switch
    {
        PyReal r => r.Value,
        PyInt i => i.Value,
        PyLong l => l.Value,
        _ => null,
    };

    private static double Length(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    private static double[]? Normalize(double[] v)
    {
        var length = Length(v);
        return double.IsFinite(length) && length > 0 ? [v[0] / length, v[1] / length, v[2] / length] : null;
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}
