using Eve.Data;
using Npgsql;
using System.Data;

namespace Eve.Npc;

/// <summary>A solar system's pinned SDE fields used to determine Dynamic Bounty eligibility.</summary>
public sealed record DynamicBountySystem(int Id, int RegionId, double Security);

/// <summary>The Dynamic Bounty output visible to the client.</summary>
public sealed record DynamicBountyValue(double Output, bool Active);

/// <summary>A system and equilibrium or persisted output for the client's map.</summary>
public sealed record DynamicBountyMapEntry(int SolarSystemId, double Output);

/// <summary>A period's paid bounty and player-loss inputs, in cents.</summary>
public sealed record DynamicBountyPeriod(long Index, long PaidCents, long LossCents);

/// <summary>The durable boundary and activation result of a DBS input.</summary>
public sealed record DynamicBountyWrite(bool Activated, long NextAtMs, bool Recorded);

/// <summary>A committed daily output update.</summary>
public sealed record DynamicBountyUpdate(int SolarSystemId, double Output, long PeriodIndex, long NextAtMs);

/// <summary>
/// DBS eligibility and control policy. Eve University documents the direction of risk and bounty effects; the numeric control terms match EveJS 261028.1.
/// </summary>
public static class DynamicBountyRules
{
    public const int FirstSystemId = 30_000_000;
    public const int EndSystemId = 31_000_000;
    public const int PochvenRegionId = 10_000_070;
    public const long TimeIntervalMs = 86_400_000;
    public const decimal MinOutput = 1m;
    public const decimal MaxOutput = 2m;
    public const decimal Equilibrium = 1.5m;

    private const long MinimumPaidCents = 10_000_000_000L;
    private const decimal EquilibriumStepBonus = 0.01m;
    private const decimal EquilibriumStepPenalty = 0.01m;
    private const decimal RiskRatioBonusStep = 0.02m;
    private const decimal RiskRatioPenaltyStep = 0.02m;
    private const decimal CurrentOutputBonusStep = 0.01m;
    private const decimal CurrentOutputPenaltyStep = 0.01m;

    public static bool IsEligible(DynamicBountySystem system) =>
        system.Id >= FirstSystemId && system.Id < EndSystemId && system.RegionId != PochvenRegionId && system.Security < 0.45;

    /// <summary>Rounds the base bounty to cents, applies the authored and system multipliers, then rounds the gross payout once more.</summary>
    public static decimal? GrossBounty(decimal baseBounty, decimal authoredMultiplier, decimal systemOutput)
    {
        if (authoredMultiplier < 0 || systemOutput < 0)
            throw new ArgumentOutOfRangeException(nameof(authoredMultiplier));
        var amount = NpcBounty.Money(Math.Max(0, NpcBounty.Money(baseBounty)));
        var gross = NpcBounty.Money(amount * authoredMultiplier * systemOutput);
        return gross > 0 ? gross : null;
    }

    public static decimal Idle(decimal output, long periods)
    {
        if (periods <= 0)
            return Clamp(output);
        var difference = Equilibrium - Clamp(output);
        var step = difference >= 0 ? EquilibriumStepBonus : EquilibriumStepPenalty;
        return Clamp(output + Math.Sign(difference) * Math.Min(Math.Abs(difference), step * periods));
    }

    public static decimal Step(decimal output, long paidCents, long lossCents)
    {
        output = Clamp(output);
        if (paidCents < MinimumPaidCents && lossCents <= 0)
            return Idle(output, 1);

        var risk = (lossCents + (decimal)MinimumPaidCents) / Math.Max(1m, paidCents + (decimal)MinimumPaidCents);
        var next = risk >= 1m
            ? output + EquilibriumStepBonus + RiskRatioBonusStep * (risk - 1m) + CurrentOutputBonusStep * Math.Max(0m, Equilibrium - output)
            : output - EquilibriumStepPenalty - RiskRatioPenaltyStep * (1m - risk) - CurrentOutputPenaltyStep * Math.Max(0m, output - Equilibrium);
        return Clamp(next);
    }

    /// <summary>Projects all elapsed periods without writing; inputs are consumed when the scheduled update commits.</summary>
    public static decimal Project(decimal output, long activatedAtMs, long periodIndex, long nowMs, IEnumerable<DynamicBountyPeriod> inputs)
    {
        var target = Math.Max(periodIndex, PeriodAt(activatedAtMs, nowMs));
        var cursor = periodIndex;
        output = Clamp(output);
        foreach (var input in inputs.Where(input => input.Index >= cursor && input.Index < target).OrderBy(input => input.Index))
        {
            output = Idle(output, input.Index - cursor);
            output = Step(output, input.PaidCents, input.LossCents);
            cursor = input.Index + 1;
        }
        output = Idle(output, target - cursor);
        return decimal.Round(output, 12, MidpointRounding.AwayFromZero);
    }

    public static long PeriodAt(long activatedAtMs, long nowMs) =>
        nowMs <= activatedAtMs ? 0 : (nowMs - activatedAtMs) / TimeIntervalMs;

    public static long NextAt(long activatedAtMs, long periodIndex) =>
        checked(activatedAtMs + checked((periodIndex + 1) * TimeIntervalMs));

    private static decimal Clamp(decimal output) => Math.Max(MinOutput, Math.Min(MaxOutput, output));
}

/// <summary>
/// PostgreSQL owner for eligible system output, period inputs and idempotent player losses. A missing row means an eligible system is at equilibrium.
/// </summary>
public sealed class PgDynamicBountyStore
{
    public const string DefaultSchema = PgBountyStore.DefaultSchema;

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly IReadOnlyDictionary<int, DynamicBountySystem> _systems;

    public PgDynamicBountyStore(NpgsqlDataSource source, IEnumerable<DynamicBountySystem> systems, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _schema = schema;
        _systems = systems.ToDictionary(system => system.Id);
    }

    private NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction, params object[] values)
    {
        var command = new NpgsqlCommand(sql.Replace("{schema}", _schema), connection, transaction);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }

    public bool IsEligible(int solarSystemId) =>
        _systems.TryGetValue(solarSystemId, out var system) && DynamicBountyRules.IsEligible(system);

    public async Task<DynamicBountyValue> ReadAsync(int solarSystemId, long nowMs, CancellationToken cancellationToken = default)
    {
        if (!IsEligible(solarSystemId))
            return new(1, false);

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var state = await ReadStateAsync(connection, transaction, solarSystemId, false, cancellationToken);
        if (state is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new((double)DynamicBountyRules.Equilibrium, true);
        }

        var periods = await ReadPeriodsAsync(connection, transaction, solarSystemId, state.PeriodIndex, cancellationToken);
        var output = DynamicBountyRules.Project(state.Output, state.ActivatedAtMs, state.PeriodIndex, nowMs, periods);
        await transaction.CommitAsync(cancellationToken);
        return new((double)output, true);
    }

    public async Task<IReadOnlyList<DynamicBountyMapEntry>> MapAsync(long nowMs, CancellationToken cancellationToken = default)
    {
        var eligible = _systems.Values.Where(DynamicBountyRules.IsEligible).OrderBy(system => system.Id).ToArray();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var states = new Dictionary<int, State>();
        await using (var command = Command(
            "SELECT solar_system_id, activated_at_ms, current_output, period_index, next_at_ms FROM {schema}.dynamic_bounty_systems",
            connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                states.Add(reader.GetInt32(0), new(reader.GetInt64(1), reader.GetDecimal(2), reader.GetInt64(3), reader.GetInt64(4)));
        }

        var inputs = new Dictionary<int, List<DynamicBountyPeriod>>();
        await using (var command = Command(
            "SELECT solar_system_id, period_index, paid_cents, loss_cents FROM {schema}.dynamic_bounty_periods",
            connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var systemId = reader.GetInt32(0);
                if (!inputs.TryGetValue(systemId, out var rows))
                    inputs.Add(systemId, rows = []);
                rows.Add(new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
            }
        }

        var result = (IReadOnlyList<DynamicBountyMapEntry>)[.. eligible.Select(system =>
        {
            if (!states.TryGetValue(system.Id, out var state))
                return new DynamicBountyMapEntry(system.Id, (double)DynamicBountyRules.Equilibrium);
            var periods = inputs.GetValueOrDefault(system.Id) ?? [];
            var output = DynamicBountyRules.Project(state.Output, state.ActivatedAtMs, state.PeriodIndex, nowMs, periods);
            return new DynamicBountyMapEntry(system.Id, (double)output);
        })];
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>Adds the gross bounty, in the same transaction as the pilot bucket and ESS share.</summary>
    public async Task<DynamicBountyWrite?> RecordPaidAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, decimal grossAmount, long nowMs, CancellationToken cancellationToken)
    {
        if (!IsEligible(solarSystemId) || grossAmount <= 0)
            return null;
        var cents = ToCents(grossAmount);
        if (cents <= 0)
            return null;

        var (state, activated) = await EnsureStateAsync(connection, transaction, solarSystemId, nowMs, cancellationToken);
        var period = InputPeriod(state, nowMs);
        await AddInputAsync(connection, transaction, solarSystemId, period, cents, isLoss: false, cancellationToken);
        return new(activated, state.NextAtMs, true);
    }

    /// <summary>Records one qualifying player ship loss once, in the ship-death transaction.</summary>
    public async Task<DynamicBountyWrite?> RecordLossAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, long lossId, decimal iskLost, long nowMs, CancellationToken cancellationToken)
    {
        if (!IsEligible(solarSystemId) || lossId <= 0 || iskLost <= 0)
            return null;
        var cents = ToCents(iskLost);
        if (cents <= 0)
            return null;

        var (state, activated) = await EnsureStateAsync(connection, transaction, solarSystemId, nowMs, cancellationToken);
        var period = InputPeriod(state, nowMs);
        await using var seen = Command(
            "INSERT INTO {schema}.dynamic_bounty_losses (loss_id, solar_system_id, period_index, loss_cents, recorded_at_ms) VALUES ($1, $2, $3, $4, $5) ON CONFLICT (loss_id) DO NOTHING RETURNING loss_id",
            connection, transaction, lossId, solarSystemId, period, cents, nowMs);
        if (await seen.ExecuteScalarAsync(cancellationToken) is null)
            return new(activated, state.NextAtMs, false);

        await AddInputAsync(connection, transaction, solarSystemId, period, cents, isLoss: true, cancellationToken);
        return new(activated, state.NextAtMs, true);
    }

    /// <summary>Projects overdue periods and clears their input rows in the scheduler transaction.</summary>
    public async Task<DynamicBountyUpdate?> AdvanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, long nowMs, CancellationToken cancellationToken)
    {
        if (!IsEligible(solarSystemId))
            return null;

        var state = await ReadStateAsync(connection, transaction, solarSystemId, true, cancellationToken);
        if (state is null)
            return null;
        var target = Math.Max(state.PeriodIndex, DynamicBountyRules.PeriodAt(state.ActivatedAtMs, nowMs));
        if (target <= state.PeriodIndex)
            return null;

        var periods = await ReadPeriodsAsync(connection, transaction, solarSystemId, state.PeriodIndex, cancellationToken);
        var output = DynamicBountyRules.Project(state.Output, state.ActivatedAtMs, state.PeriodIndex, nowMs, periods);
        var nextAt = DynamicBountyRules.NextAt(state.ActivatedAtMs, target);
        await using (var update = Command(
            "UPDATE {schema}.dynamic_bounty_systems SET current_output = $2, period_index = $3, next_at_ms = $4 WHERE solar_system_id = $1",
            connection, transaction, solarSystemId, output, target, nextAt))
            await update.ExecuteNonQueryAsync(cancellationToken);
        await using (var clear = Command(
            "DELETE FROM {schema}.dynamic_bounty_periods WHERE solar_system_id = $1 AND period_index < $2",
            connection, transaction, solarSystemId, target))
            await clear.ExecuteNonQueryAsync(cancellationToken);
        return new(solarSystemId, (double)output, target, nextAt);
    }

    private async Task<(State State, bool Activated)> EnsureStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, long nowMs, CancellationToken cancellationToken)
    {
        var nextAt = DynamicBountyRules.NextAt(nowMs, 0);
        await using var insert = Command(
            "INSERT INTO {schema}.dynamic_bounty_systems (solar_system_id, activated_at_ms, current_output, period_index, next_at_ms) VALUES ($1, $2, $3, 0, $4) ON CONFLICT (solar_system_id) DO NOTHING RETURNING solar_system_id",
            connection, transaction, solarSystemId, nowMs, DynamicBountyRules.Equilibrium, nextAt);
        var activated = await insert.ExecuteScalarAsync(cancellationToken) is not null;
        var state = await ReadStateAsync(connection, transaction, solarSystemId, true, cancellationToken)
            ?? throw new InvalidOperationException($"DBS state for system {solarSystemId} disappeared during its transaction.");
        return (state, activated);
    }

    private async Task<State?> ReadStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int solarSystemId, bool forUpdate, CancellationToken cancellationToken)
    {
        var suffix = forUpdate ? " FOR UPDATE" : "";
        await using var command = Command(
            $"SELECT activated_at_ms, current_output, period_index, next_at_ms FROM {{schema}}.dynamic_bounty_systems WHERE solar_system_id = $1{suffix}",
            connection, transaction, solarSystemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new State(reader.GetInt64(0), reader.GetDecimal(1), reader.GetInt64(2), reader.GetInt64(3))
            : null;
    }

    private async Task<IReadOnlyList<DynamicBountyPeriod>> ReadPeriodsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int solarSystemId, long fromPeriod, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT period_index, paid_cents, loss_cents FROM {schema}.dynamic_bounty_periods WHERE solar_system_id = $1 AND period_index >= $2 ORDER BY period_index",
            connection, transaction, solarSystemId, fromPeriod);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DynamicBountyPeriod>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        return result;
    }

    private async Task AddInputAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, long period, long cents, bool isLoss, CancellationToken cancellationToken)
    {
        var column = isLoss ? "loss_cents" : "paid_cents";
        await using var command = Command(
            $"INSERT INTO {{schema}}.dynamic_bounty_periods AS periods (solar_system_id, period_index, paid_cents, loss_cents) VALUES ($1, $2, $3, $4) ON CONFLICT (solar_system_id, period_index) DO UPDATE SET {column} = periods.{column} + EXCLUDED.{column}",
            connection, transaction, solarSystemId, period, isLoss ? 0L : cents, isLoss ? cents : 0L);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static long InputPeriod(State state, long nowMs) =>
        Math.Max(state.PeriodIndex, DynamicBountyRules.PeriodAt(state.ActivatedAtMs, nowMs));

    private static long ToCents(decimal amount) => checked((long)(NpcBounty.Money(amount) * 100m));

    private sealed record State(long ActivatedAtMs, decimal Output, long PeriodIndex, long NextAtMs);
}
