using System.Text.Json;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Npc;
using Eve.Scheduler;
using Eve.Services.Ships;
using Eve.Services.Space.Waves;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Beacons;

/// <summary>What the activation job of a beacon needs: the beacon.</summary>
public sealed record AnalysisBeaconDue(long DeployableId);

/// <summary>
/// The CONCORD Rogue Analysis Beacon (CRAB) of a pilot in low or null security space: deployed from the hold (EVE.js <c>mobileAnalysisBeaconRuntime</c>), active 20 seconds
/// later, linked by a ship (four minutes), and scanning for 10 minutes (16 for the Carrier) while its Rogue Drones are dead. The first wave of Rogue Drones is made when
/// it is active (the owner's figures, <c>analysis-beacon-rules.json</c>); the scan does not move while a drone lives. A completed scan puts its items in the hold of the
/// pilot's ship (once). The beacon leaves when its hour is up, and its drones with it.
/// <para>
/// Each state change is written in one transaction, then told to the simulation (and the client, through its slim item) after the commit. The simulation keeps the ball;
/// this class keeps the rules, the record and the durable jobs (activation, link end, the scan tick) on the shared scheduler.
/// </para>
/// </summary>
public sealed class AnalysisBeaconDirector
{
    /// <summary>The record kind of a beacon in the deployed-record store (the expiry and restore entries of <see cref="SpaceContainers"/>).</summary>
    public const string Kind = "analysis_beacon";

    public const string ActivateJob = "space.analysis_beacon.activate";
    public const string LinkJob = "space.analysis_beacon.link";
    public const string ScanJob = "space.analysis_beacon.scan";
    public const string KillSubscriber = "space.analysis_beacon.kills";
    public const string NpcKilledTopic = "npc.killed";

    /// <summary>The category of a ship (SDE category 6): a beacon links a ship and nothing else.</summary>
    private const int ShipCategory = 6;

    /// <summary>The attribute of a skill requirement is in the static data (<see cref="SpaceContainers"/> reads the same ones for the mobile deployables).</summary>
    private const int RadiusAttribute = 162;

    /// <summary>The Unix time of the FILETIME epoch (1601-01-01) in 100 ns ticks: a FILETIME is the Unix milliseconds times 10,000 plus this.</summary>
    private const long FiletimeEpochTicks = 116_444_736_000_000_000;

    private readonly AnalysisBeaconRules _rules;
    private readonly AnalysisBeaconStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _bus;
    private readonly ISimulation _sim;
    private readonly CharacterShips _ships;
    private readonly PgItemStore _items;
    private readonly DogmaStatic _statics;
    private readonly SpaceSessions _space;
    private readonly PgDeployedStore _deployed;
    private readonly IDungeonNpcSpawner _spawner;
    private readonly NpcSelector _selector;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;
    private readonly NpgsqlDataSource _source;

    public AnalysisBeaconDirector(
        NpgsqlDataSource source, string schema, AnalysisBeaconRules rules, DurableScheduler scheduler, EventBus bus, ISimulation sim, CharacterShips ships,
        PgItemStore items, DogmaStatic statics, SpaceSessions space, PgDeployedStore deployed, IDungeonNpcSpawner spawner, NpcSelector selector, Func<long> nowUnixMs,
        Action<string>? log = null)
    {
        (_source, _rules, _scheduler, _bus, _sim, _ships, _items, _statics, _space, _deployed, _spawner, _selector, _nowUnixMs, _log) =
            (source, rules, scheduler, bus, sim, ships, items, statics, space, deployed, spawner, selector, nowUnixMs, log);
        _store = new AnalysisBeaconStore(source, schema);
    }

    /// <summary>Creates the beacon tables in the space schema (applied once).</summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema, CancellationToken cancellationToken = default) =>
        AnalysisBeaconStore.MigrateAsync(source, schema, cancellationToken);

    /// <summary>
    /// Registers the jobs and the kill subscriber, and starts the scan tick (every <see cref="AnalysisBeaconRules.ScanTick"/>). Call it before the event bus starts and
    /// before the space sessions take pilots.
    /// </summary>
    public async Task AttachAsync()
    {
        _scheduler.Register(ActivateJob, (context, token) => ActivateAsync(context.Payload<AnalysisBeaconDue>()!.DeployableId, token));
        _scheduler.Register(LinkJob, (context, token) => LinkCompleteAsync(context.Payload<AnalysisBeaconDue>()!.DeployableId, token));
        _scheduler.Register(ScanJob, (_, token) => ScanTickAsync(token));
        _bus.Subscribe(KillSubscriber, [NpcKilledTopic], (context, token) => OnKilledAsync(context, token));
        await _scheduler.EnsureRecurringAsync(ScanJob, "scan", AnalysisBeaconRules.ScanTick);
    }

    // ── deploy ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ship.LaunchFromShip</c> of a CRAB in the hold: refused when the pilot lacks the skill, when the system is not low or null security space, or when the item is not in
    /// the hold; otherwise it goes out of the hold 2,500 m ahead of the ship and is active 20 seconds later. Answers the id of the item in space.
    /// </summary>
    public async Task<long?> LaunchAsync(ClientSession session, long itemId, CancellationToken cancellationToken)
    {
        if (await _items.GetAsync(itemId, cancellationToken) is not { } item)
        {
            return null;
        }
        await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (_statics.Type(item.TypeId) is not { } type || type.GroupId != AnalysisBeaconRules.Group)
        {
            return null;
        }
        if (session.CharacterId is not { } character || session.GetInt("shipid") is not { } shipId || shipId <= 0)
        {
            throw Unavailable();
        }
        var system = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        if (system <= 0 || !_space.IsInSpace(session))
        {
            throw Refuse("A CRAB is dropped from a ship in space.");
        }
        if (item.OwnerId != character || item.LocationId != shipId || item.Flag != CharacterShips.CargoFlag)
        {
            throw Refuse("That item is not in your ship's cargo hold.");
        }
        if (await _ships.MissingSkillAsync(character, type.Attributes, cancellationToken) is { } missing)
        {
            await _statics.EnsureTypesAsync([missing.SkillTypeId], cancellationToken);
            throw Refuse($"{type.Name} deployment requires {_statics.Type(missing.SkillTypeId)?.Name ?? "a skill"} {missing.Level}.");
        }
        if (_space.SecurityOfSystem(system) is not { } security || security >= AnalysisBeaconRules.SecurityCutoff)
        {
            throw Refuse("A CRAB can only be deployed in low or null security space.");
        }
        var radius = type.Attributes.GetValueOrDefault(RadiusAttribute);
        if (radius <= 0)
        {
            throw Refuse("That CRAB has no size in the static data.");
        }
        if (await _space.DropPointAsync(session, AnalysisBeaconRules.DeployDistanceMeters, cancellationToken) is not { } point)
        {
            throw Unavailable();
        }
        var position = new[] { point.X, point.Y, point.Z };
        var now = _nowUnixMs();
        var dropped = await _ships.DropDisruptorAsync(character, shipId, itemId, system, cancellationToken)
            ?? throw Refuse("That item is no longer in your ship's cargo hold.");
        var (beacon, changes) = dropped;
        foreach (var change in changes)
        {
            ShipNotifications.SendItemChange(session, change, RowOf);
        }

        var activateAt = now + (long)AnalysisBeaconRules.ActivationDelay.TotalMilliseconds;
        var expiresAt = now + (long)AnalysisBeaconRules.Lifetime.TotalMilliseconds;
        var corporation = (int)(session.GetInt("corpid") ?? 0);
        var alliance = (int)(session.GetInt("allianceid") ?? 0);
        await _deployed.SaveAsync(new DeployedRecord(
            beacon.Id, Kind, system, type.Name, item.TypeId, type.GroupId, position, radius, character, corporation, alliance, shipId, expiresAt, activateAt, 0),
            cancellationToken);
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await _store.InsertAsync(connection, transaction, new BeaconRow(
                beacon.Id, item.TypeId, system, character, shipId, now, activateAt, false, 1, 0, 0, 0, 0, null, false, 0), cancellationToken);
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ActivateJob, activateAt)
            {
                Key = $"beacon:{beacon.Id}",
                Payload = new AnalysisBeaconDue(beacon.Id),
                OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        _sim.PlaceAnalysisBeacon(new SimPlaceAnalysisBeacon(
            beacon.Id, system, type.Name, item.TypeId, type.GroupId, position, radius, character, corporation, alliance, shipId,
            StateOf(new BeaconRow(beacon.Id, item.TypeId, system, character, shipId, now, activateAt, false, 1, 0, 0, 0, 0, null, false, 0))));
        _scheduler.Wake();
        return beacon.Id;
    }

    // ── activation and the wave ─────────────────────────────────────────────

    /// <summary>
    /// The activation time is over (the durable job): the beacon is active, its scan runs from now, and its first wave of Rogue Drones is made (the owner's figures). A
    /// wave that is not made in full ends the scan as failed, and the drones made are taken out.
    /// </summary>
    public async Task ActivateAsync(long deployableId, CancellationToken cancellationToken = default)
    {
        var record = await _deployed.GetAsync(deployableId, cancellationToken);
        if (record is null || record.Kind != Kind)
        {
            return;
        }
        var now = _nowUnixMs();
        BeaconRow row;
        var failure = (string?)null;
        var made = new List<long>();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (await _store.GetAsync(connection, transaction, deployableId, forUpdate: true, cancellationToken) is not { Active: false, Outcome: null } current)
            {
                return;   // already active, ended, or not a beacon row: nothing to do
            }
            await _store.ActivateAsync(connection, transaction, deployableId, now, cancellationToken);
            var rule = _rules.For(current.TypeId)!;
            var (selection, refusal) = WaveComposition.Resolve(_selector, rule.Waves[0].SpawnPool);
            if (selection is null)
            {
                failure = $"the wave's pool could not be used: {refusal}";
            }
            else
            {
                var result = await _spawner.SpawnAsync(record.SolarSystemId, record.Position, selection, rule.Waves[0].Amount, cancellationToken);
                made.AddRange(result.ShipIds);
                if (result.Error is not null || result.ShipIds.Count < rule.Waves[0].Amount)
                {
                    failure = $"{result.ShipIds.Count} of {rule.Waves[0].Amount} Rogue Drones were made" + (result.Error is null ? "" : $": {result.Error}");
                }
                await _store.AddDronesAsync(connection, transaction, deployableId, result.ShipIds, cancellationToken);
            }
            if (failure is not null)
            {
                await _store.EndAsync(connection, transaction, deployableId, "failed", failure, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            row = (await _store.GetAsync(connection, null, deployableId, forUpdate: false, cancellationToken))!;
        }
        if (failure is not null)
        {
            _spawner.Remove(made);
            _log?.Invoke($"the beacon {deployableId} failed at activation: {failure}");
        }
        _sim.SetAnalysisBeaconState(deployableId, StateOf(row));
    }

    /// <summary>
    /// Makes one wave of a beacon's Rogue Drones: they are rows of the beacon (they block its scan until they die), and the wave is counted in the same transaction. A
    /// wave not made in full fails the scan, with the reason; the drones made are returned so the caller takes them out.
    /// </summary>
    private async Task<(string? Failure, IReadOnlyList<long> Made)> MakeWaveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BeaconRow row, BeaconWaveRule wave, CancellationToken cancellationToken)
    {
        var (selection, refusal) = WaveComposition.Resolve(_selector, wave.SpawnPool);
        if (selection is null)
        {
            return ($"the wave's pool could not be used: {refusal}", []);
        }
        var record = await _deployed.GetAsync(row.DeployableId, cancellationToken);
        if (record is null)
        {
            return ("the beacon is no longer deployed", []);
        }
        var result = await _spawner.SpawnAsync(row.SolarSystemId, record.Position, selection, wave.Amount, cancellationToken);
        await _store.AddDronesAsync(connection, transaction, row.DeployableId, result.ShipIds, cancellationToken);
        await _store.SetWavesAsync(connection, transaction, row.DeployableId, row.WavesMade + 1, cancellationToken);
        string? failure = result.Error is not null || result.ShipIds.Count < wave.Amount
            ? $"{result.ShipIds.Count} of {wave.Amount} Rogue Drones were made" + (result.Error is null ? "" : $": {result.Error}")
            : null;
        return (failure, result.ShipIds);
    }

    // ── link ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>CallComponentFromClient(itemID, 'linkWithShip', 'InitiateLink' | 'InitiateLinkQuick')</c> (EVE.js: the two are the same link): a ship in the beacon's system
    /// links it when the beacon is active, idle, not linked, and the ship is one its link list names (SDE 300 standard, 946 Carrier). The link runs four minutes, then is
    /// complete. A refusal is logged (the client shows its own reasons from the same state).
    /// </summary>
    public async Task InitiateLinkAsync(ClientSession session, long deployableId, CancellationToken cancellationToken = default)
    {
        if (session.CharacterId is not { } character || session.GetInt("shipid") is not { } shipId || shipId <= 0
            || (session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid")) is not { } systemId)
        {
            _log?.Invoke($"the link of beacon {deployableId} was refused: no ship in space for the pilot");
            return;
        }
        var now = _nowUnixMs();
        var refusal = (string?)null;
        BeaconRow? row = null;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            row = await _store.GetAsync(connection, transaction, deployableId, forUpdate: true, cancellationToken);
            var ship = await _items.GetAsync(shipId, cancellationToken);
            await _statics.EnsureTypesAsync(ship is null ? Array.Empty<int>() : new[] { ship.TypeId }, cancellationToken);
            if (row is null || row.Outcome is not null || !row.Active)
            {
                refusal = "the beacon is not active";
            }
            else if (row.SolarSystemId != (int)systemId)
            {
                refusal = "the beacon is in another system";
            }
            else if (row.LinkState != 1 || row.LinkedShipId != 0)
            {
                refusal = "the beacon is already linked";
            }
            else if (ship is null || _statics.Type(ship.TypeId) is not { } shipType || shipType.CategoryId != ShipCategory
                     || !await _statics.TypeListNamesAsync(AnalysisBeaconRules.LinkTypeListOf(row.TypeId), ship.TypeId, cancellationToken))
            {
                refusal = "the ship cannot link this beacon";
            }
            else if (!_space.IsInSpace(session))
            {
                refusal = "the ship is not in space";
            }
            else if (!await _store.StartLinkAsync(connection, transaction, deployableId, shipId, now + (long)AnalysisBeaconRules.LinkDuration.TotalMilliseconds, cancellationToken))
            {
                refusal = "the beacon is already linked";
            }
            if (refusal is null)
            {
                await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(LinkJob, now + (long)AnalysisBeaconRules.LinkDuration.TotalMilliseconds)
                {
                    Key = $"link:{deployableId}",
                    Payload = new AnalysisBeaconDue(deployableId),
                    OnDuplicate = OnDuplicate.Replace,
                }, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            if (refusal is null)
            {
                row = await _store.GetAsync(connection, null, deployableId, forUpdate: false, cancellationToken);
            }
        }
        if (refusal is not null)
        {
            _log?.Invoke($"the link of beacon {deployableId} by ship {shipId} was refused: {refusal}");
            return;
        }
        _sim.SetAnalysisBeaconState(deployableId, StateOf(row!));
        _scheduler.Wake();
    }

    /// <summary>The link's time is up (the durable job): the link is complete, and the ship is linked until the beacon leaves.</summary>
    public async Task LinkCompleteAsync(long deployableId, CancellationToken cancellationToken = default)
    {
        BeaconRow? row;
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (await _store.CompleteLinkAsync(connection, transaction, deployableId, cancellationToken) == 0)
            {
                return;   // not running (the beacon left, or the link was never started)
            }
            await transaction.CommitAsync(cancellationToken);
            row = await _store.GetAsync(connection, null, deployableId, forUpdate: false, cancellationToken);
        }
        if (row is not null)
        {
            _sim.SetAnalysisBeaconState(deployableId, StateOf(row));
        }
    }

    // ── scan ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The scan tick: for each active beacon that has not ended, the progress moves by the time since it last moved, but not while a drone of it lives. A scan that reaches its
    /// time (10 minutes, 16 for the Carrier) is completed in the same transaction as its items: they go into the hold of the pilot's ship (once).
    /// </summary>
    public async Task ScanTickAsync(CancellationToken cancellationToken = default)
    {
        var now = _nowUnixMs();
        var completed = new List<(BeaconRow Row, IReadOnlyList<ItemChanged> Changes)>();
        var failed = new List<BeaconRow>();
        var failedDrones = new List<long>();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach (var row in await _store.RunningAsync(connection, transaction, cancellationToken))
            {
                if (!row.Active)
                {
                    continue;
                }
                var drones = await _store.DronesAliveAsync(connection, transaction, row.DeployableId, cancellationToken);
                var duration = (long)AnalysisBeaconRules.ScanOf(row.TypeId).TotalMilliseconds;
                var advance = drones == 0 && row.ScanUpdatedMs > 0 ? Math.Max(0, now - row.ScanUpdatedMs) : 0;
                var progress = Math.Min(duration, row.ScanMs + advance);
                await _store.SetScanAsync(connection, transaction, row.DeployableId, progress, now, cancellationToken);
                // the next wave comes when the scan reaches its progress; its drones block the scan until they are dead
                var rule = _rules.For(row.TypeId)!;
                if (row.WavesMade < rule.Waves.Count && progress >= rule.Waves[row.WavesMade].AtScanProgress * duration)
                {
                    var (failure, made) = await MakeWaveAsync(connection, transaction, row, rule.Waves[row.WavesMade], cancellationToken);
                    if (failure is not null)
                    {
                        await _store.EndAsync(connection, transaction, row.DeployableId, "failed", failure, cancellationToken);
                        failed.Add(row with { Outcome = "failed" });
                        failedDrones.AddRange(made);
                    }
                    continue;
                }
                if (progress < duration)
                {
                    continue;
                }
                var changes = new List<ItemChanged>();
                var rewards = rule.Rewards;
                var delivered = true;
                foreach (var reward in rewards)
                {
                    var granted = await _ships.GrantToShipCargoAsync(connection, transaction, row.OwnerId, row.ShipId, reward.TypeId, reward.Quantity, cancellationToken);
                    if (granted is null)
                    {
                        delivered = false;
                        break;
                    }
                    changes.AddRange(granted);
                }
                if (delivered)
                {
                    await _store.MarkRewardedAsync(connection, transaction, row.DeployableId, cancellationToken);
                    await _store.EndAsync(connection, transaction, row.DeployableId, "completed", "the scan completed and its items were delivered", cancellationToken);
                }
                else
                {
                    // the ship that launched it is gone: nothing is granted, and the run says so
                    await _store.EndAsync(connection, transaction, row.DeployableId, "completed", "the scan completed, but the launching ship is not there to take the items", cancellationToken);
                }
                completed.Add((row, changes));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        if (failedDrones.Count > 0)
        {
            _spawner.Remove(failedDrones);
        }
        foreach (var row in failed)
        {
            _log?.Invoke($"the beacon {row.DeployableId} failed in its scan: a wave was not made in full");
            _sim.SetAnalysisBeaconState(row.DeployableId, StateOf(row));
        }
        foreach (var (row, changes) in completed)
        {
            if (_space.SessionOfShip(row.ShipId) is { } session)
            {
                foreach (var change in changes)
                {
                    ShipNotifications.SendItemChange(session, change, RowOf);
                }
            }
            _sim.SetAnalysisBeaconState(row.DeployableId, StateOf(row with { Outcome = "completed" }));
        }
    }

    // ── kills, expiry and restore ───────────────────────────────────────────

    /// <summary>An NPC was destroyed: when it was a drone of a beacon, it is no longer alive (its scan can go on once none is).</summary>
    private async Task OnKilledAsync(EventContext context, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(context.Event.PayloadJson);
        if (!document.RootElement.TryGetProperty("shipId", out var shipElement) || !shipElement.TryGetInt64(out long shipId))
        {
            return;
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await _store.ForgetDroneAsync(connection, transaction, shipId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The hour of a beacon is up (the sweep of the deployed records): the scan ends as expired (no items), its drones are taken out, and its item is deleted. A scan already
    /// completed keeps its outcome.
    /// </summary>
    public async Task ExpireAsync(DeployedRecord due)
    {
        IReadOnlyList<long> drones;
        await using (var connection = await _source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await _store.EndAsync(connection, transaction, due.DeployableId, "expired", "its hour is up", CancellationToken.None);
            drones = await _store.TakeDronesAsync(connection, transaction, due.DeployableId, CancellationToken.None);
            await transaction.CommitAsync();
        }
        if (drones.Count > 0)
        {
            _spawner.Remove(drones);
        }
        _sim.RemoveDeployed(due.DeployableId);
        await _ships.DeleteDisruptorAsync(due.DeployableId);
    }

    /// <summary>A ship came into the system: a beacon whose hour is not up is put back in space with the state its row keeps (the drones it made are not re-attached).</summary>
    public async Task RestoreAsync(DeployedRecord record, long now)
    {
        BeaconRow? row;
        await using (var connection = await _source.OpenConnectionAsync())
        {
            row = await _store.GetAsync(connection, null, record.DeployableId, forUpdate: false, CancellationToken.None);
        }
        if (row is null)
        {
            return;
        }
        _sim.PlaceAnalysisBeacon(new SimPlaceAnalysisBeacon(
            record.DeployableId, record.SolarSystemId, record.Name, record.TypeId, record.GroupId, record.Position, record.Radius, record.OwnerId, record.CorporationId,
            record.AllianceId, record.ShipId, StateOf(row)));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>What the simulation and the client are told of a beacon: its activation, its link, and when it was deployed (FILETIME).</summary>
    public static SimAnalysisBeaconState StateOf(BeaconRow row) => new(
        row.Active,
        row.Active ? 0 : FiletimeOf(row.ActivateAtMs),
        FiletimeOf(row.DeployedMs),
        (int)AnalysisBeaconRules.ActivationDelay.TotalSeconds,
        row.LinkState,
        row.LinkCompleteMs == 0 ? 0 : FiletimeOf(row.LinkCompleteMs),
        row.LinkedShipId);

    /// <summary>A Unix time in milliseconds as a FILETIME (100 ns ticks since 1601); 0 is kept as 0 (none).</summary>
    public static long FiletimeOf(long unixMs) => unixMs <= 0 ? 0 : unixMs * 10_000 + FiletimeEpochTicks;

    private ItemRow RowOf(Item item)
    {
        var type = _statics.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    private static MachoWrappedException Refuse(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));

    private static MachoWrappedException Unavailable() => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Space is not available right now. Try again in a moment.")));
}
