using System.Text.Json;
using Eve.Data;
using Eve.Inventory;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>What an undock or a stargate jump costs a pilot who is caught with contraband: the fine charged to its wallet up to its balance, and the contraband confiscated.</summary>
public sealed record ContrabandPenalty(decimal Fine, decimal Charged, IReadOnlyList<Item> Confiscated);

/// <summary>One penalty to settle: the fine and the standing loss are fixed at the inspection, the items are the cargo it covers.</summary>
public sealed record ContrabandSettlement(string OperationId, long CharacterId, long ShipId, int SolarSystemId, int FactionId, IReadOnlyList<ContrabandSettlementLine> Lines);

/// <summary>One item of a penalty: its fine (quantity x base price x fineByValue) and the standing it costs (the SDE standingLoss of its type and faction).</summary>
public sealed record ContrabandSettlementLine(long ItemId, int TypeId, long Quantity, decimal Fine, double StandingLoss);

/// <summary>
/// Contraband in the ship's cargo at an undock or a stargate jump (EVE.js inspectCharacterContraband and contrabandPenaltyState). The rules are the SDE contrabandTypes: per type
/// and faction, the confiscation threshold (the system's security must reach it), the fine factor and the standing loss. An inspection that detects the contraband rolls
/// against the pilot's smuggling chance; a detected load is settled by the durable job <see cref="SettleJobKind"/>, in its own transaction, so a restart finishes it.
/// </summary>
public sealed class UndockContraband
{
    /// <summary>The durable job that settles a penalty (EVE.js DURABLE_SETTLEMENT_JOB_TYPE).</summary>
    public const string SettleJobKind = "contraband.penalty-settle";

    public const string DefaultSchema = "contraband";

    /// <summary>The wallet reason of the fine (EVE.js "Contraband fine").</summary>
    public const string FineReason = "Contraband fine";

    /// <summary>The journal ref type of the fine: eve-logic appConst.refContrabandFine (EVE.js names the admin adjustment instead, DEVIATIONS, ST-18).</summary>
    public const int FineRefType = 51;

    /// <summary>The standing event of the loss: eve-logic appLogConst.eventStandingContrabandTrafficking.</summary>
    public const int ContrabandTraffickingEventTypeId = 126;

    private const string Standing = "standing";
    private const double MinStanding = -10;

    private readonly PgItemStore _items;
    private readonly SolarSystemCatalog _systems;
    private readonly SdeReader _sde;
    private readonly NpgsqlDataSource? _source;
    private readonly WalletTransactions? _wallets;
    private readonly DurableScheduler? _scheduler;
    private readonly RelationStore? _relations;
    private readonly Func<long, CancellationToken, Task<double>>? _smugglingChance;
    private readonly Func<long> _nowMs;
    private readonly string _schema;
    private readonly Dictionary<(int TypeId, int FactionId), ContrabandRule> _rules;

    private sealed record ContrabandRule(double ConfiscateMinSecurity, double FineByValue, double StandingLoss);

    private UndockContraband(
        PgItemStore items,
        SolarSystemCatalog systems,
        SdeReader sde,
        NpgsqlDataSource? source,
        WalletTransactions? wallets,
        DurableScheduler? scheduler,
        RelationStore? relations,
        Func<long, CancellationToken, Task<double>>? smugglingChance,
        Func<long> nowMs,
        string schema,
        Dictionary<(int TypeId, int FactionId), ContrabandRule> rules)
    {
        _items = items;
        _systems = systems;
        _sde = sde;
        _source = source;
        _wallets = wallets;
        _scheduler = scheduler;
        _relations = relations;
        _smugglingChance = smugglingChance;
        _nowMs = nowMs;
        _schema = schema;
        _rules = rules;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(UndockContraband).Assembly, "ContrabandMigrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>
    /// Loads the contraband rules. The penalty needs the database, the wallets, the relations and the scheduler (its handler is registered with the scheduler here, as the
    /// Host does at start-up); the smuggling chance (0 when none is given) and the clock are injected for tests.
    /// </summary>
    public static async Task<UndockContraband> LoadAsync(
        SdeReader sde,
        PgItemStore items,
        SolarSystemCatalog systems,
        NpgsqlDataSource? source = null,
        WalletTransactions? wallets = null,
        DurableScheduler? scheduler = null,
        RelationStore? relations = null,
        Func<long, CancellationToken, Task<double>>? smugglingChance = null,
        Func<long>? nowMs = null,
        string schema = DefaultSchema,
        CancellationToken ct = default)
    {
        if (scheduler is not null && (source is null || wallets is null || relations is null))
        {
            throw new ArgumentException("The penalty job needs the database, the wallets and the relations.", nameof(scheduler));
        }

        var rules = new Dictionary<(int TypeId, int FactionId), ContrabandRule>();
        foreach (var row in await sde.ReadAsync("contrabandTypes", cancellationToken: ct))
        {
            if (row.Key is <= 0 or > int.MaxValue
                || row.Doc.ValueKind != JsonValueKind.Object
                || !row.Doc.TryGetProperty("factions", out var factions)
                || factions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var faction in factions.EnumerateArray())
            {
                if (faction.ValueKind != JsonValueKind.Object
                    || !faction.TryGetProperty("_key", out var factionIdValue)
                    || factionIdValue.ValueKind != JsonValueKind.Number
                    || !factionIdValue.TryGetInt32(out var factionId)
                    || factionId <= 0
                    || !faction.TryGetProperty("confiscateMinSec", out var minSecurityValue)
                    || minSecurityValue.ValueKind != JsonValueKind.Number
                    || !minSecurityValue.TryGetDouble(out var minSecurity)
                    || !double.IsFinite(minSecurity))
                {
                    continue;
                }

                var fineByValue = faction.TryGetProperty("fineByValue", out var fineValue)
                    && fineValue.ValueKind == JsonValueKind.Number && fineValue.TryGetDouble(out var fine) && double.IsFinite(fine) && fine >= 0
                        ? fine
                        : 0d;
                var standingLoss = faction.TryGetProperty("standingLoss", out var lossValue)
                    && lossValue.ValueKind == JsonValueKind.Number && lossValue.TryGetDouble(out var loss) && double.IsFinite(loss) && loss >= 0
                        ? loss
                        : 0d;
                rules[((int)row.Key, factionId)] = new ContrabandRule(minSecurity, fineByValue, standingLoss);
            }
        }

        var inspector = new UndockContraband(
            items, systems, sde, source, wallets, scheduler, relations, smugglingChance,
            nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), Sql.CheckSchema(schema), rules);
        scheduler?.Register(SettleJobKind, (job, token) => inspector.SettleAsync(job, token));
        return inspector;
    }

    public async Task<IReadOnlyList<int>> InspectAsync(
        long characterId,
        long shipId,
        int solarSystemId,
        CancellationToken ct = default)
    {
        var system = _systems.Get(solarSystemId);
        if (characterId <= 0
            || shipId <= 0
            || system is null
            || system.FactionId <= 0
            || !double.IsFinite(system.Security))
        {
            return Array.Empty<int>();
        }

        var ship = await _items.GetAsync(shipId, ct);
        if (ship is null || ship.OwnerId != characterId)
        {
            return Array.Empty<int>();
        }

        var contraband = new List<int>();
        var seen = new HashSet<int>();
        foreach (var item in await _items.InLocationAsync(shipId, characterId, ct))
        {
            if (item.Flag != ItemFlags.Cargo
                || item.TypeId <= 0
                || !_rules.TryGetValue((item.TypeId, system.FactionId), out var rule)
                || system.Security < rule.ConfiscateMinSecurity
                || !seen.Add(item.TypeId))
            {
                continue;
            }

            contraband.Add(item.TypeId);
        }

        return contraband;
    }

    /// <summary>
    /// Applies the penalty of the contraband in the ship's cargo at <paramref name="solarSystemId"/> (EVE.js applyContrabandPenalties). Null when there is nothing to
    /// penalise or the inspection evades: the roll of the inspection is below the pilot's smuggling chance. A detected load is scheduled as a durable job, run now; the
    /// result is what the job settled. A job that has not settled (it failed; the runner retries it) gives null here and is settled later.
    /// </summary>
    public async Task<ContrabandPenalty?> ApplyAsync(long characterId, long shipId, int solarSystemId, CancellationToken ct = default)
    {
        if (_source is null || _wallets is null || _scheduler is null || _relations is null)
        {
            throw new InvalidOperationException("The contraband penalty needs the database, the wallets, the relations and the scheduler.");
        }

        var (operationId, entries) = await InspectDetectedAsync(characterId, shipId, solarSystemId, ct);
        if (operationId is null)
        {
            return null;
        }
        await _scheduler.RunNowAsync(SettleJobKind, JobKey(operationId), ct);
        return await ReadSettledAsync(operationId, entries, ct);
    }

    /// <summary>
    /// The inspection and the scheduling only: a detected load is written as a settlement job in its own transaction and nothing is charged or removed. Returns the
    /// operation id, or null when nothing is detected. The job runs when the runner (or <see cref="ApplyAsync"/>) reaches it.
    /// </summary>
    internal async Task<string?> ScheduleSettlementAsync(long characterId, long shipId, int solarSystemId, CancellationToken ct = default)
    {
        if (_source is null || _scheduler is null)
        {
            throw new InvalidOperationException("The contraband penalty needs the database and the scheduler.");
        }
        return (await InspectDetectedAsync(characterId, shipId, solarSystemId, ct)).OperationId;
    }

    /// <summary>
    /// The inspection of the cargo: the contraband that the system confiscates, and whether the roll detects it. The scheduling of the detected load is in the
    /// inspection's own transaction, so the job exists exactly when the detection does.
    /// </summary>
    private async Task<(string? OperationId, List<(Item Item, double StandingLoss)> Entries)> InspectDetectedAsync(
        long characterId, long shipId, int solarSystemId, CancellationToken ct)
    {
        var none = (OperationId: (string?)null, Entries: new List<(Item Item, double StandingLoss)>());
        var system = _systems.Get(solarSystemId);
        if (characterId <= 0 || shipId <= 0 || system is null || system.FactionId <= 0 || !double.IsFinite(system.Security))
        {
            return none;
        }
        var ship = await _items.GetAsync(shipId, ct);
        if (ship is null || ship.OwnerId != characterId)
        {
            return none;
        }

        var entries = new List<(Item Item, ContrabandRule Rule)>();
        foreach (var item in await _items.InLocationAsync(shipId, characterId, ct))
        {
            if (item.Flag != ItemFlags.Cargo
                || item.TypeId <= 0
                || !_rules.TryGetValue((item.TypeId, system.FactionId), out var rule)
                || system.Security < rule.ConfiscateMinSecurity)
            {
                continue;
            }
            entries.Add((item, rule));
        }
        if (entries.Count == 0)
        {
            return none;
        }

        // the inspection rolls against the smuggling chance (EVE.js: evaded when the roll is below the chance)
        var now = _nowMs();
        var chance = _smugglingChance is null ? 0d : Math.Clamp(await _smugglingChance(characterId, ct), 0d, 1d);
        if (InspectionRoll(characterId, shipId, solarSystemId, now) < chance)
        {
            return none;
        }

        // the base prices of the contraband types (EVE.js getItemBasePrice, the SDE types)
        var prices = new Dictionary<long, decimal>();
        foreach (var row in await _sde.ReadAsync("types", entries.Select(entry => (long)entry.Item.TypeId).Distinct(), ct))
        {
            if (row.Doc.ValueKind == JsonValueKind.Object
                && row.Doc.TryGetProperty("basePrice", out var price)
                && price.TryGetDecimal(out var value))
            {
                prices[row.Key] = value;
            }
        }

        var lines = entries.Select(entry => new ContrabandSettlementLine(
            entry.Item.Id, entry.Item.TypeId, entry.Item.Quantity,
            Math.Round(entry.Item.Quantity * prices.GetValueOrDefault(entry.Item.TypeId) * (decimal)entry.Rule.FineByValue, 2, MidpointRounding.AwayFromZero),
            entry.Rule.StandingLoss)).ToList();
        var operationId = Guid.NewGuid().ToString("N");
        var settlement = new ContrabandSettlement(operationId, characterId, shipId, solarSystemId, system.FactionId, lines);

        await using var connection = await _source!.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await _scheduler!.ScheduleAsync(connection, transaction, new JobSpec(SettleJobKind, _scheduler.Now) { Key = JobKey(operationId), Payload = settlement }, ct);
        await transaction.CommitAsync(ct);
        return (operationId, entries.Select(entry => (entry.Item, entry.Rule.StandingLoss)).ToList());
    }

    /// <summary>The job's handler: settles one penalty in the job's transaction, so that its fine, standing loss, confiscation and record commit together or not at all.</summary>
    private async Task SettleAsync(JobContext job, CancellationToken ct)
    {
        var settlement = job.Payload<ContrabandSettlement>()
            ?? throw new InvalidOperationException("The contraband penalty job has no settlement.");
        var connection = job.Connection;
        var transaction = job.Transaction;
        var character = new WalletAccount(settlement.CharacterId);

        // a settled penalty is not settled again
        await using (var seen = Sql.Command(connection, transaction, Q("SELECT 1 FROM {schema}.contraband_penalties WHERE operation_id = $1"), settlement.OperationId))
        {
            if (await seen.ExecuteScalarAsync(ct) is not null)
            {
                return;
            }
        }

        // the items still in the hold are the ones confiscated (an item moved since the inspection is left alone, and not fined)
        var inHold = (await _items.InLocationAsync(settlement.ShipId, settlement.CharacterId, ct))
            .Where(item => item.Flag == ItemFlags.Cargo)
            .Select(item => item.Id)
            .ToHashSet();
        var taken = settlement.Lines.Where(line => inHold.Contains(line.ItemId)).ToArray();

        var fine = taken.Sum(line => line.Fine);
        var balance = await _wallets!.AccountBalanceAsync(character, ct) ?? 0m;
        var charged = Math.Min(balance, fine);
        if (charged > 0)
        {
            // a fine is a journal line against the faction (EVE.js ownerID2 and referenceID); no faction wallet is credited
            await _wallets.AdjustAsync(connection, transaction, character, -charged, settlement.FactionId, FineRefType, FineReason, job.NowMs,
                $"contraband-penalty:{settlement.OperationId}:wallet", $"contraband-penalty:{settlement.OperationId}", ct, referenceId: settlement.FactionId);
        }

        var loss = taken.Sum(line => line.StandingLoss);
        if (loss > 0)
        {
            var standing = await _relations!.ValueAsync(Standing, settlement.CharacterId, settlement.FactionId, 0, connection, transaction, ct);
            var next = StandingAfter(standing, -loss);
            if (next != standing)
            {
                await _relations.SetAsync(connection, transaction, Standing, settlement.CharacterId, settlement.FactionId, next,
                    StandingReason(settlement, taken, -loss), cancellationToken: ct);
            }
        }

        await using (var record = Sql.Command(connection, transaction, Q("""
            INSERT INTO {schema}.contraband_penalties (operation_id, character_id, ship_id, solar_system_id, faction_id, total_fine, charged_fine, standing_loss,
                                                       confiscated_item_ids, settled_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """), settlement.OperationId, settlement.CharacterId, settlement.ShipId, settlement.SolarSystemId, settlement.FactionId, fine, charged, loss,
            taken.Select(line => line.ItemId).ToArray(), job.NowMs))
        {
            await record.ExecuteNonQueryAsync(ct);
        }
        foreach (var line in taken)
        {
            await _items.DeleteAsync(connection, transaction, line.ItemId, ct);
        }
    }

    /// <summary>What the settled job wrote: the fine, what was charged, and the cargo that was confiscated (from the inspection's snapshot of the items).</summary>
    private async Task<ContrabandPenalty?> ReadSettledAsync(string operationId, IReadOnlyList<(Item Item, double StandingLoss)> entries, CancellationToken ct)
    {
        await using var connection = await _source!.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, null, Q("SELECT total_fine, charged_fine, confiscated_item_ids FROM {schema}.contraband_penalties WHERE operation_id = $1"), operationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var confiscated = reader.GetFieldValue<long[]>(2);
        return new ContrabandPenalty(
            reader.GetDecimal(0),
            reader.GetDecimal(1),
            entries.Where(entry => confiscated.Contains(entry.Item.Id)).Select(entry => entry.Item).ToArray());
    }

    /// <summary>The roll of an inspection in [0, 1): a hash of the pilot, its ship, the system and the inspection's time, so the same inspection rolls the same number.</summary>
    public static double InspectionRoll(long characterId, long shipId, int solarSystemId, long nowMs)
    {
        var hash = Mix(unchecked((ulong)characterId));
        hash = Mix(hash ^ unchecked((ulong)shipId));
        hash = Mix(hash ^ unchecked((ulong)solarSystemId));
        hash = Mix(hash ^ unchecked((ulong)nowMs));
        return (hash >> 11) * (1d / (1UL << 53));
    }

    /// <summary>The standing after a raw change (EVE.js calculateStandingsByRawChange): it moves by the change times (1 + standing / 10), rounded to thousandths; at -10 it stays.</summary>
    public static double StandingAfter(double standing, double rawChange)
    {
        if (standing <= MinStanding)
        {
            return standing;
        }
        var next = Math.Max(MinStanding, 10 * (standing / 10 + (1 + standing / 10) * rawChange));
        return Math.Floor(next * 1000 + 0.5) / 1000;
    }

    private static string StandingReason(ContrabandSettlement settlement, IReadOnlyList<ContrabandSettlementLine> taken, double modification) =>
        JsonSerializer.Serialize(new
        {
            eventTypeID = ContrabandTraffickingEventTypeId,
            msg = "Contraband trafficking",
            modification,
            int_1 = settlement.SolarSystemId,
            int_2 = taken.Count == 1 ? (long?)taken[0].TypeId : null,
            int_3 = taken.Sum(line => line.Quantity),
        });

    private static string JobKey(string operationId) => $"contraband-penalty:{operationId}";

    private static ulong Mix(ulong z)
    {
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    private string Q(string text) => Sql.In(_schema, text);
}
