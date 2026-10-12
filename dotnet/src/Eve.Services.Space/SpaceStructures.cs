using Eve.Dogma;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// The Upwell structures in the simulation (ST-1 d). The structures of a system are in its scene once a pilot has come into it: each is put in space with
/// its vitals and limits from the static data, and its slim state from the record. A change of the record's state is given to the simulation, and a
/// damage notice of the simulation is a state event of the record, applied in the transaction of the record's own event (FOUNDATIONS rule 1).
/// </summary>
public sealed class SpaceStructures
{
    /// <summary>The bus subscriber's name: a name is a contract, so it is never renamed.</summary>
    public const string SubscriberName = "structure.space";

    // the attributes of a structure's points, resonances, damage limits and repair pauses (SDE dogma attributes)
    private const int ShieldCapacity = 263, ArmorHitPoints = 265, HullPoints = 9, Radius = 162, ShieldRechargeRate = 479;
    private const int ShieldDamageLimit = 2034, ArmorDamageLimit = 2035, StructureDamageLimit = 2036;
    private const int ShieldRepairPause = 3354, ArmorRepairPause = 3355, HullRepairPause = 3356;
    private static readonly int[] ShieldResonances = [271, 274, 273, 272];
    private static readonly int[] ArmorResonances = [267, 270, 269, 268];
    private static readonly int[] HullResonances = [113, 110, 109, 111];

    /// <summary>Unix milliseconds to the client's file time (100 ns since 1601), the timers of a structure.</summary>
    private const long UnixEpochFileTime = 116_444_736_000_000_000L;

    private readonly ISimulation _sim;
    private readonly SpaceSessions _space;
    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _store;
    private readonly StructureTimers _timers;
    private readonly DogmaStatic _statics;
    private readonly Action<string>? _log;
    private readonly object _damageOrder = new();
    private Task _damageQueue = Task.CompletedTask;

    public SpaceStructures(ISimulation sim, SpaceSessions space, NpgsqlDataSource source, StructureStore store, StructureTimers timers, DogmaStatic statics, Action<string>? log = null)
    {
        _sim = sim;
        _space = space;
        _source = source;
        _store = store;
        _timers = timers;
        _statics = statics;
        _log = log;
        _sim.Received += OnReceived;
        _space.SystemEntered += SystemEntered;
    }

    /// <summary>Subscribes to the structures' state changes. Call it once, before the foundations start.</summary>
    public void Subscribe(Eve.Events.EventBus events) =>
        events.Subscribe(SubscriberName, [StructureTimers.StateChangedTopic], (context, _) =>
        {
            OnStateChanged(context.Event.Payload<StructureStateChanged>()!);
            return Task.CompletedTask;
        });

    /// <summary>
    /// The structures of a system that are in space are put in its scene, with their state as the record has it (a structure still being anchored
    /// is in space; one not yet anchored is not). Points restart full: the simulation keeps no damage between two placements (DEVIATIONS "Structures in the simulation").
    /// </summary>
    public async Task RestoreAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            var inSpace = (await _store.ListAsync(connection, null, includeGone: false, cancellationToken))
                .Where(record => record.SolarSystemId == solarSystemId && record.Snapshot.State != StructureState.Unanchored)
                .ToList();
            if (inSpace.Count == 0)
            {
                return;
            }
            await _statics.EnsureTypesAsync(inSpace.Select(record => record.TypeId), cancellationToken);
            foreach (var record in inSpace)
            {
                if (_statics.Type(record.TypeId) is { } type)
                {
                    _sim.PlaceStructure(Placement(record, type));
                }
                else
                {
                    _log?.Invoke($"structure {record.Snapshot.StructureId} of type {record.TypeId} is not in the static data and is not put in space");
                }
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the structures of system {solarSystemId} were not put back: {error.Message}");
        }
    }

    /// <summary>
    /// A structure that was just anchored is put in space when a pilot is in its system (a system without pilots gets it on entry, <see cref="RestoreAsync"/>).
    /// The simulation ignores a second placement of the same id.
    /// </summary>
    public async Task PlaceAsync(long structureId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            var record = await _store.GetAsync(connection, null, structureId, forUpdate: false, cancellationToken);
            if (record is null || record.Snapshot.State == StructureState.Unanchored || record.Snapshot.DestroyedMs > 0 || !_space.HasPilotsIn(record.SolarSystemId))
            {
                return;
            }
            await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
            if (_statics.Type(record.TypeId) is { } type)
            {
                _sim.PlaceStructure(Placement(record, type));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the anchored structure {structureId} was not put in space: {error.Message}");
        }
    }

    /// <summary>
    /// A hit on a structure: the record applies it as a damage event, in one transaction with the event it publishes. A depleted layer is the layer that was
    /// emptied; a hit that only paused the repair lands on the layer the state names.
    /// </summary>
    public async Task DamageAsync(SimStructureDamaged damaged, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var record = await _store.GetAsync(connection, transaction, damaged.StructureId, forUpdate: false, cancellationToken);
            if (record is null)
            {
                return;
            }
            var layer = damaged.Depleted switch
            {
                1 => StructureLayer.Shield,
                2 => StructureLayer.Armor,
                3 => StructureLayer.Hull,
                _ => LayerOf(record.Snapshot.State),
            };
            await _timers.ApplyAsync(connection, transaction, damaged.StructureId, new Damaged(layer, damaged.Depleted != 0, damaged.OverThreshold), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception error)
        {
            _log?.Invoke($"the hit on structure {damaged.StructureId} was not applied: {error.Message}");
        }
    }

    /// <summary>The record's state changed: the simulation shows it, or takes the structure out of space when it has gone.</summary>
    public void OnStateChanged(StructureStateChanged change)
    {
        if (change.Removal is not null)
        {
            _sim.RemoveStructure(change.StructureId);
            return;
        }
        _sim.SetStructureState(new SimSetStructureState(change.SolarSystemId, change.StructureId, StatusOf(change.State, change.Upkeep, change.Timer)));
    }

    private void OnReceived(SimMessage message)
    {
        if (message is SimStructureDamaged damaged)
        {
            // one hit can empty a shield and then an armor, told as two messages: applied one after the other, so the timeline takes the shield first
            lock (_damageOrder)
            {
                _damageQueue = _damageQueue.ContinueWith(_ => DamageAsync(damaged), TaskScheduler.Default).Unwrap();
            }
        }
    }

    private void SystemEntered(int solarSystemId) => _space.Background(() => RestoreAsync(solarSystemId));

    private static SimPlaceStructure Placement(StructureRecord record, Eve.Dogma.TypeDef type)
    {
        var attributes = type.Attributes;
        double Attribute(int id) => attributes.GetValueOrDefault(id, 0d);
        double[] Layer(int[] ids) => [.. ids.Select(id => Math.Max(0, attributes.GetValueOrDefault(id, 1d)))];
        var health = new SimStructureHealth(
            Attribute(ShieldCapacity),
            Attribute(ArmorHitPoints),
            Attribute(HullPoints),
            [.. Layer(ShieldResonances), .. Layer(ArmorResonances), .. Layer(HullResonances)],
            Attribute(ShieldRechargeRate),
            [Attribute(ShieldDamageLimit), Attribute(ArmorDamageLimit), Attribute(StructureDamageLimit)],
            [Attribute(ShieldRepairPause), Attribute(ArmorRepairPause), Attribute(HullRepairPause)]);
        var snapshot = record.Snapshot;
        return new SimPlaceStructure(
            snapshot.StructureId,
            record.SolarSystemId,
            record.Name,
            record.TypeId,
            type.GroupId,
            [record.X, record.Y, record.Z],
            Attribute(Radius),
            record.OwnerCorporationId,
            (int)record.OwnerCorporationId,
            (int)record.AllianceId,
            health,
            StatusOf(snapshot.State, snapshot.Upkeep, snapshot.StateEndsMs > 0
                ? new StructureTimerInfo(snapshot.StateStartedMs, snapshot.StateEndsMs, snapshot.TimerPausedMs)
                : null));
    }

    /// <summary>The slim state the client shows: the state, the upkeep and the timer when one runs (repair and unanchoring are not sent yet).</summary>
    private static SimStructureStatus StatusOf(int state, int upkeep, StructureTimerInfo? timer) => new(
        state,
        upkeep,
        timer is { EndsMs: > 0 }
            ? (FileTime(timer.StartedMs), FileTime(timer.EndsMs), timer.PausedMs > 0 ? FileTime(timer.PausedMs) : null)
            : null,
        null,
        null);

    private static long FileTime(long unixMs) => unixMs * 10_000 + UnixEpochFileTime;

    /// <summary>The layer a hit lands on while the structure is vulnerable in this state: shield, then armor, then hull.</summary>
    private static StructureLayer LayerOf(int state) => state switch
    {
        StructureState.ArmorReinforce or StructureState.ArmorVulnerable => StructureLayer.Armor,
        StructureState.HullReinforce or StructureState.HullVulnerable => StructureLayer.Hull,
        _ => StructureLayer.Shield,
    };
}
