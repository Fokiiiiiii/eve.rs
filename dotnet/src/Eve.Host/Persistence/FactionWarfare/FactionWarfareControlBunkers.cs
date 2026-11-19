using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.FactionWarfare;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services.Space;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Persistence.FactionWarfare;

/// <summary>Publishes persisted FW Control Bunkers to loaded solar-system scenes and applies next-downtime captures.</summary>
internal sealed class FactionWarfareControlBunkers
{
    public const string CaptureJobKind = "factionwarfare.control-bunker-capture";
    public const string CaptureCompletedTopic = "factionwarfare.control-bunker-capture-completed";
    private const int InfrastructureHubGroupId = 925;
    private const int FactionDogmaAttributeId = 1341;
    private const int ShieldCapacityAttributeId = 263;
    private const int ArmorHitPointsAttributeId = 265;
    private const int HullHitPointsAttributeId = 9;
    private const int RadiusAttributeId = 162;
    private static readonly int[] ShieldResonanceAttributes = [271, 274, 273, 272];
    private static readonly int[] ArmorResonanceAttributes = [267, 270, 269, 268];
    private static readonly int[] HullResonanceAttributes = [113, 110, 109, 111];

    private readonly NpgsqlDataSource _source;
    private readonly FwStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _events;
    private readonly ISimulation _sim;
    private readonly SpaceSessions _space;
    private readonly DogmaStatic _statics;
    private readonly SdeReader _sde;
    private readonly Action<string>? _log;
    private readonly object _damageOrder = new();
    private Task _damageQueue = Task.CompletedTask;

    private const double SunWarpInClearanceMeters = 100_000d;
    private const double InfrastructureHubWarpInOffsetMeters = 100_000d;

    public FactionWarfareControlBunkers(NpgsqlDataSource source, FwStore store, DurableScheduler scheduler, EventBus events,
        ISimulation sim, SpaceSessions space, DogmaStatic statics, SdeReader sde, Action<string>? log = null)
    {
        (_source, _store, _scheduler, _events, _sim, _space, _statics, _sde, _log) =
            (source, store, scheduler, events, sim, space, statics, sde, log);
        _scheduler.Register(CaptureJobKind, CaptureDueAsync);
        _events.Subscribe("factionwarfare.control-bunker-captures", [CaptureCompletedTopic], CaptureCompletedAsync);
        _sim.Received += OnReceived;
        _space.SystemEntered += SystemEntered;
    }

    private void SystemEntered(int solarSystemId) => _ = Task.Run(() => RestoreAsync(solarSystemId));

    private void OnReceived(SimMessage message)
    {
        if (message is not SimStructureDamaged damaged || damaged.Depleted != 3)
            return;
        lock (_damageOrder)
            _damageQueue = _damageQueue.ContinueWith(_ => ScheduleCaptureAsync(damaged.StructureId), TaskScheduler.Default).Unwrap();
    }

    private async Task ScheduleCaptureAsync(long bunkerId)
    {
        try
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var system = await _store.SystemOfControlBunkerAsync(connection, transaction, bunkerId, forUpdate: true);
            if (system is null)
                return;
            var scheduled = await _store.ScheduleBunkerCaptureAsync(connection, transaction, system.SolarSystemId, _scheduler.Now);
            if (scheduled is null)
                return;
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(CaptureJobKind, scheduled.PendingCaptureMs)
            {
                Key = $"system:{scheduled.SolarSystemId}",
                Payload = new CaptureDue(scheduled.SolarSystemId),
                OnDuplicate = OnDuplicate.Replace,
            });
            await transaction.CommitAsync();
            _scheduler.Wake();
            _sim.RemoveStructure(bunkerId);
        }
        catch (Exception error)
        {
            _log?.Invoke($"FW Control Bunker {bunkerId} destruction could not be recorded: {error.Message}");
            try
            {
                await using var connection = await _source.OpenConnectionAsync();
                var system = await _store.SystemOfControlBunkerAsync(connection, null, bunkerId, forUpdate: false);
                if (system is { } current && current.PendingCaptureMs == 0 && _space.HasPilotsIn((int)current.SolarSystemId))
                {
                    _sim.RemoveStructure(bunkerId);
                    await RestoreAsync((int)current.SolarSystemId);
                }
            }
            catch (Exception recoveryError)
            {
                _log?.Invoke($"FW Control Bunker {bunkerId} could not be restored after a failed destruction record: {recoveryError.Message}");
            }
        }
    }

    private async Task CaptureDueAsync(JobContext context, CancellationToken cancellationToken)
    {
        var due = context.Payload<CaptureDue>() ?? throw new InvalidOperationException("FW Control Bunker capture job has no payload.");
        var transition = await _store.ApplyBunkerCaptureAsync(context.Connection, context.Transaction, due.SolarSystemId, context.NowMs, cancellationToken);
        if (transition is null)
            return;
        await _events.PublishInTransactionAsync(context.Connection, context.Transaction, CaptureCompletedTopic, transition,
            due.SolarSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        context.AfterCommit(_ => { _events.Wake(); return Task.CompletedTask; });
    }

    private async Task CaptureCompletedAsync(EventContext context, CancellationToken cancellationToken)
    {
        var transition = context.Event.Payload<FwControlBunkerTransition>()
            ?? throw new InvalidOperationException("FW Control Bunker capture event has no payload.");
        if (_space.HasPilotsIn((int)transition.Current.SolarSystemId))
        {
            _sim.RemoveStructure(transition.Current.ControlBunkerId);
            await RestoreAsync((int)transition.Current.SolarSystemId, cancellationToken);
        }
    }

    /// <summary>Reads a live Control Bunker ID for the Agency. A persisted identity without a real group-925 ball is not exposed.</summary>
    public async Task<long?> ReadLiveControlBunkerIdAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        if (solarSystemId <= 0)
            return null;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var bunkerId = await _store.ControlBunkerIdAsync(connection, transaction, solarSystemId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (bunkerId is not > 0)
            return null;
        var body = await _sim.GetSceneBodyAsync(
            new SimGetSceneBody(SimClient.NextRequestId(), SimSceneKey.SolarSystem(solarSystemId), bunkerId.Value), cancellationToken);
        return body.Error is null && body.ItemId == bunkerId && body.TypeId > 0 && body.GroupId == InfrastructureHubGroupId &&
            body.Position.Count == 3 && body.Position.All(double.IsFinite) ? bunkerId : null;
    }

    /// <summary>Publishes the persisted bunker when an authoritative scene ball or verified anchor is available.</summary>
    public async Task RestoreAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var bunkerId = await _store.ControlBunkerIdAsync(connection, transaction, solarSystemId, cancellationToken);
            var system = bunkerId is > 0
                ? await _store.SystemAsync(connection, transaction, solarSystemId, forUpdate: false, cancellationToken)
                : null;
            await transaction.CommitAsync(cancellationToken);
            if (system is null || system.PendingCaptureMs > 0 || bunkerId is not > 0)
                return;

            var type = await BunkerTypeAsync(system.OccupierId, cancellationToken);
            var liveBody = await _sim.GetSceneBodyAsync(
                new SimGetSceneBody(SimClient.NextRequestId(), SimSceneKey.SolarSystem(solarSystemId), bunkerId.Value), cancellationToken);
            var bodyIsLive = liveBody.Error is null && liveBody.ItemId == bunkerId && liveBody.GroupId == InfrastructureHubGroupId &&
                liveBody.Position.Count == 3 && liveBody.Position.All(double.IsFinite);
            IReadOnlyList<double>? point = bodyIsLive
                ? liveBody.Position
                : await SunWarpInPositionAsync(solarSystemId, cancellationToken);
            if (point is not { Count: 3 } || point.Any(position => !double.IsFinite(position)))
            {
                _log?.Invoke($"FW Control Bunker of system {solarSystemId} has no live group-925 ball or SDE sun position; placement was skipped.");
                return;
            }
            var corporationId = FwFactions.MilitiaCorporationOf(system.OccupierId);
            var health = Health(type);
            if (bodyIsLive && liveBody.TypeId == type.TypeId)
            {
                _sim.SetStructureState(new SimSetStructureState(solarSystemId, bunkerId.Value,
                    new SimStructureStatus(StatusOf(system), UpkeepState.FullPower, null, null, null)));
                return;
            }
            if (bodyIsLive)
                _sim.RemoveStructure(bunkerId.Value);
            _sim.PlaceStructure(new SimPlaceStructure(bunkerId.Value, solarSystemId, type.Name, type.TypeId, type.GroupId, point,
                Attribute(type, RadiusAttributeId), corporationId, (int)corporationId, 0, health,
                new SimStructureStatus(StatusOf(system), UpkeepState.FullPower, null, null, null)));
        }
        catch (Exception error)
        {
            _log?.Invoke($"FW Control Bunker of system {solarSystemId} was not put in space: {error}");
        }
    }

    /// <summary>
    /// Derives a stable hub anchor from the pinned system sun. V24's GetSunWarpInPoint uses this radius-based point, and the
    /// EveJS sovereignty-hub placement applies an additional 100 km Y offset to keep a hub clear of the sun warp-in point.
    /// The mapStars row is system-centered, so its sun position is the origin.
    /// </summary>
    private async Task<IReadOnlyList<double>?> SunWarpInPositionAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        var stars = await _sde.ReadByFieldAsync("mapStars", "solarSystemID", [solarSystemId], cancellationToken);
        if (stars.Count != 1 || !stars[0].Doc.TryGetProperty("radius", out var radiusValue) ||
            radiusValue.ValueKind != System.Text.Json.JsonValueKind.Number)
            return null;

        var radius = radiusValue.GetDouble();
        if (!double.IsFinite(radius) || radius <= 0)
            return null;

        var distance = radius + SunWarpInClearanceMeters;
        return [
            distance * Math.Cos(radius),
            radius / 5d + InfrastructureHubWarpInOffsetMeters,
            -distance * Math.Sin(radius),
        ];
    }

    /// <summary>Changes the live slim item's vulnerability in step with the persisted victory-point state.</summary>
    public async Task UpdateVulnerabilityAsync(long solarSystemId, long attackerPoints, CancellationToken cancellationToken = default)
    {
        if (!_space.HasPilotsIn((int)solarSystemId))
            return;
        var bunkerId = await ReadLiveControlBunkerIdAsync((int)solarSystemId, cancellationToken);
        if (bunkerId is > 0)
            _sim.SetStructureState(new SimSetStructureState((int)solarSystemId, bunkerId.Value,
                new SimStructureStatus(FwRules.Contest(attackerPoints) == ContestState.Vulnerable ? StructureState.ShieldVulnerable : StructureState.FobInvulnerable,
                    UpkeepState.FullPower, null, null, null)));
    }

    private async Task<TypeDef> BunkerTypeAsync(long factionId, CancellationToken cancellationToken)
    {
        // FW infrastructure hubs are deliberately unpublished in the SDE, including their group. Read the
        // pinned group/type rows directly and ask DogmaStatic to load the authoritative type attributes.
        var group = await _sde.ReadAsync("groups", [InfrastructureHubGroupId], cancellationToken);
        if (group.Count != 1 || !group[0].Doc.TryGetProperty("name", out var groupName) ||
            !string.Equals(SdeReader.English(groupName), "FW Infrastructure Hub", StringComparison.Ordinal))
            throw new InvalidOperationException("the pinned SDE has no FW Infrastructure Hub group 925");
        var typeRows = await _sde.ReadByFieldAsync("types", "groupID", [InfrastructureHubGroupId], cancellationToken);
        await _statics.EnsureTypesAsync(typeRows.Select(row => checked((int)row.Key)), cancellationToken);
        var type = typeRows.Select(row => _statics.Type(checked((int)row.Key))).OfType<TypeDef>()
            .OrderBy(candidate => candidate.TypeId)
            .FirstOrDefault(candidate => (long)Attribute(candidate, FactionDogmaAttributeId) == factionId);
        return type ?? throw new InvalidOperationException($"the pinned SDE has no Control Bunker type for faction {factionId}");
    }

    private static double Attribute(TypeDef type, int id) => type.Attributes.GetValueOrDefault(id, 0d);

    private static SimStructureHealth Health(TypeDef type)
    {
        double[] Resistances(int[] attributes) => [.. attributes.Select(id => Math.Max(0d, type.Attributes.GetValueOrDefault(id, 1d)))];
        return new SimStructureHealth(Attribute(type, ShieldCapacityAttributeId), Attribute(type, ArmorHitPointsAttributeId),
            Attribute(type, HullHitPointsAttributeId), [.. Resistances(ShieldResonanceAttributes), .. Resistances(ArmorResonanceAttributes),
                .. Resistances(HullResonanceAttributes)], 0d, [0d, 0d, 0d], [0d, 0d, 0d]);
    }

    private static int StatusOf(FwSystem system) =>
        FwRules.Contest(system.AttackerPoints) == ContestState.Vulnerable ? StructureState.ShieldVulnerable : StructureState.FobInvulnerable;

    private sealed record CaptureDue(long SolarSystemId);
}
