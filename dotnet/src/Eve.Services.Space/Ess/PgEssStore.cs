using System.Globalization;
using Eve.Data;
using Eve.Marshal;
using Eve.Npc;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>A thief's link to the Main Bank: the link's id, who holds it, the ship it was made from, when it began and when it completes.</summary>
public sealed record EssLinkRecord(string Id, long CharacterId, long ShipId, long StartedMs, long CompletesMs);

/// <summary>
/// A pilot as the space simulation sees it, for a link: whether the pilot is in the system, whether the ship is one that may link (type list 231), where the ship
/// is in metres from the ESS ball, and which ship it is. Null from the holder callback when the character is not in the system.
/// </summary>
public sealed record EssHolder(bool InSystem, bool LinkableShip, double DistanceMeters, long ShipId);

/// <summary>One taken amount: a Main Bank theft (<c>AtMs</c> the moment) or the reserve payouts of a character (summed, the last one's moment).</summary>
public sealed record EssTheftRecord(long CharacterId, decimal Amount, long AtMs);

/// <summary>The ESS of a solar system as the rules see it.</summary>
public sealed record EssState(
    int SolarSystemId,
    long EssId,
    long BeaconId,
    int TypeId,
    long? OwnerId,
    double CurrentOutput,
    decimal MainValue,
    decimal ReserveValue,
    EssLinkRecord? MainLink,
    long NextPayoutMs,
    long? ReserveKeyTypeId,
    int PulsesTotal,
    int PulsesRemaining,
    long? ReserveOpenedMs,
    long? NextPulseMs,
    IReadOnlySet<long> ReserveLinked,
    IReadOnlyList<EssTheftRecord> MainThefts,
    IReadOnlyList<EssTheftRecord> ReserveThefts);

public enum EssEventKind
{
    /// <summary>A link completed while its thief was still in the system: the bank was taken.</summary>
    Theft,
    /// <summary>A link ended without a theft (the thief was no longer in the system).</summary>
    LinkEnded,

    Changed,
}

/// <summary>What the clock did to an ESS, for whoever tells the pilots of the system.</summary>
public sealed record EssEvent(EssEventKind Kind, int SolarSystemId, string LinkId, long CharacterId, decimal Amount, int Reason);

/// <summary>
/// The Encounter Surveillance Systems in PostgreSQL. A bounty earned in an ESS system pays <see cref="EssRules.MainBankShare"/> into its Main Bank in the same
/// clock; a Reserve Bank opened by a key pays its pulses on the clock. Every change is one transaction under the row lock of its system.
/// </summary>
public sealed class PgEssStore
{
    public const string DefaultSchema = "ess";

    private readonly NpgsqlDataSource _source;
    private readonly WalletTransactions _wallet;
    private readonly string _schema;
    private readonly Action<string>? _log;
    private readonly Func<int, long, long, CancellationToken, ValueTask<EssHolder?>>? _holderOf;
    private readonly Func<NpgsqlConnection, NpgsqlTransaction, long, long, CancellationToken, ValueTask<bool>>? _consumeKey;
    private readonly Func<int, string?>? _quadrantOf;
    private readonly Func<int, CancellationToken, ValueTask<long?>>? _sovereigntyOwnerOf;
    private readonly Func<long, CancellationToken, ValueTask<long?>>? _corporationOf;
    private readonly double _reserveMaxPayout;

    /// <param name="holderOf">
    /// The pilot of a character in the system, as the space simulation sees it (null when the character is not in it). Without it no link can be checked, so none is
    /// made, and a link already made breaks.
    /// </param>
    /// <param name="consumeKey">
    /// Takes one key of the type out of the character's inventory, inside the given transaction (the bank opens in the same one, so a failure takes the key back);
    /// false when the character has none.
    /// </param>
    /// <param name="quadrantOf">The quadrant of a system, or null when it is in none.</param>
    /// <param name="reserveMaxPayout">The largest payout of one reserve pulse (<see cref="EssRules.ReserveBankMaxPayout"/> unless a test sets another).</param>
    public PgEssStore(
        NpgsqlDataSource source,
        WalletTransactions wallet,
        string schema = DefaultSchema,
        Action<string>? log = null,
        Func<int, long, long, CancellationToken, ValueTask<EssHolder?>>? holderOf = null,
        Func<NpgsqlConnection, NpgsqlTransaction, long, long, CancellationToken, ValueTask<bool>>? consumeKey = null,
        Func<int, string?>? quadrantOf = null,
        Func<int, CancellationToken, ValueTask<long?>>? sovereigntyOwnerOf = null,
        double reserveMaxPayout = EssRules.ReserveBankMaxPayout,
        Func<long, CancellationToken, ValueTask<long?>>? corporationOf = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _wallet = wallet;
        _schema = schema;
        _log = log;
        _holderOf = holderOf;
        _consumeKey = consumeKey;
        _quadrantOf = quadrantOf;
        _sovereigntyOwnerOf = sovereigntyOwnerOf;
        _corporationOf = corporationOf;
        _reserveMaxPayout = reserveMaxPayout;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgEssStore).Assembly, "EssMigrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private const string Columns =
        "solar_system_id, ess_id, beacon_id, type_id, owner_id, current_output, main_value, reserve_value, main_link_id, main_link_character_id, " +
        "main_link_started_ms, main_link_completes_ms, next_payout_ms, reserve_key_type_id, reserve_pulses_total, reserve_pulses_remaining, reserve_opened_ms, next_pulse_ms, " +
        "main_link_ship_id";

    private NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction, params object?[] values)
    {
        var command = new NpgsqlCommand(sql.Replace("{schema}", _schema), connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
        return command;
    }

    public async Task RegisterAsync(int solarSystemId, long essId, long? ownerId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(
            """
            INSERT INTO {schema}.systems (solar_system_id, ess_id, beacon_id, type_id, owner_id, current_output, next_payout_ms)
            VALUES ($1, $2, $2, $3, $4, $5, $6)
            ON CONFLICT (solar_system_id) DO UPDATE SET ess_id = EXCLUDED.ess_id, beacon_id = EXCLUDED.beacon_id, owner_id = EXCLUDED.owner_id
            """,
            connection, null, solarSystemId, essId, EssRules.TypeEncounterSurveillanceSystem, ownerId, EssRules.EquilibriumOutput, nowMs + EssRules.AutoPayIntervalMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The ESS of a system, or null when it has none.</summary>
    public async Task<EssState?> ReadAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await LoadAsync(connection, null, solarSystemId, lockRow: false, cancellationToken);
    }

    /// <summary>Whether the system has an ESS now.</summary>
    public async Task<bool> IsOnlineAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command("SELECT EXISTS (SELECT 1 FROM {schema}.systems WHERE solar_system_id = $1)", connection, null, solarSystemId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Every ESS, for the agency's list.</summary>
    public async Task<IReadOnlyList<EssState>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command($"SELECT solar_system_id FROM {{schema}}.systems ORDER BY solar_system_id", connection, null);
        var ids = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt32(0));
            }
        }
        var states = new List<EssState>();
        foreach (var id in ids)
        {
            if (await LoadAsync(connection, null, id, lockRow: false, cancellationToken) is { } state)
            {
                states.Add(state);
            }
        }
        return states;
    }

    /// <summary>
    /// The ESS share of a bounty earned in this system: the Main Bank's and the Reserve Bank's, in the transaction of the bounty (the caller passes it through
    /// <c>PgBountyStore.RecordAsync</c>). Throws when the system has no ESS, so that nothing is recorded that cannot be paid.
    /// </summary>
    public async Task CreditBountyShareAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int solarSystemId, decimal mainBank, decimal reserveBank, CancellationToken cancellationToken = default)
    {
        if (mainBank <= 0 && reserveBank <= 0)
        {
            return;
        }
        await using var command = Command(
            "UPDATE {schema}.systems SET main_value = main_value + $2, reserve_value = reserve_value + $3 WHERE solar_system_id = $1",
            connection, transaction, solarSystemId, EssRules.Money(mainBank), EssRules.Money(reserveBank));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"no ESS in solar system {solarSystemId}");
        }
    }

    /// <summary>Pays a bounty's ESS shares into a system on their own (a bounty whose pilot share is nothing has no bucket to commit with).</summary>
    public async Task CreditAsync(int solarSystemId, decimal mainBank, decimal reserveBank, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await CreditBountyShareAsync(connection, transaction, solarSystemId, mainBank, reserveBank, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Runs one pilot's request against the ESS of their system (the rules of the client's essMgr calls).</summary>
    public async Task<EssMgrOperationResult> ExecuteAsync(EssMgrOperationRequest request, long nowMs, CancellationToken cancellationToken = default)
    {
        if (request.SolarSystemId is < 1 or > int.MaxValue)
        {
            return new EssMgrOperationResult(false);
        }
        var systemId = (int)request.SolarSystemId;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await LoadAsync(connection, transaction, systemId, lockRow: true, cancellationToken) is not { } state)
        {
            return new EssMgrOperationResult(false);
        }

        var character = request.CharacterId;
        EssMgrOperationResult result;
        switch (request.Operation)
        {
            case EssMgrOperation.AttemptLinkToMainBank:
                if (character <= 0 || state.ReserveLinked.Contains(character) || state.MainLink is not null ||
                    await LinkHolderAsync(systemId, character, state.EssId, cancellationToken) is not { } holder)
                {
                    return new EssMgrOperationResult(false, ToClient(state));
                }
                var link = new EssLinkRecord(Guid.NewGuid().ToString("D"), character, holder.ShipId, nowMs, nowMs + EssRules.LinkDurationMs);
                state = state with { MainLink = link };
                await SaveLinkAsync(connection, transaction, systemId, link, cancellationToken);
                result = new EssMgrOperationResult(true, ToClient(state));
                break;

            case EssMgrOperation.AttemptLinkToReserveBank:
                if (character <= 0 || state.PulsesRemaining <= 0 || state.ReserveLinked.Contains(character) ||
                    state.MainLink?.CharacterId == character || await LinkHolderAsync(systemId, character, state.EssId, cancellationToken) is null)
                {
                    return new EssMgrOperationResult(false, ToClient(state));
                }
                await using (var insert = Command(
                    "INSERT INTO {schema}.reserve_links (solar_system_id, character_id, linked_ms) VALUES ($1, $2, $3)",
                    connection, transaction, systemId, character, nowMs))
                {
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
                state = state with { ReserveLinked = new HashSet<long>(state.ReserveLinked) { character } };
                result = new EssMgrOperationResult(true, ToClient(state));
                break;

            case EssMgrOperation.RequestMainBankUnlink:
                // only the thief who holds the link lets it go; the client offers the button to that pilot alone
                if (state.MainLink is not { } held || held.CharacterId != character)
                {
                    return new EssMgrOperationResult(false, ToClient(state));
                }
                await ClearLinkAsync(connection, transaction, systemId, cancellationToken);
                state = state with { MainLink = null };
                result = new EssMgrOperationResult(true, ToClient(state), ToClientLink(held));
                break;

            case EssMgrOperation.RequestReserveBankUnlink:
                await using (var delete = Command(
                    "DELETE FROM {schema}.reserve_links WHERE solar_system_id = $1 AND character_id = $2", connection, transaction, systemId, character))
                {
                    if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
                    {
                        return new EssMgrOperationResult(false, ToClient(state));
                    }
                }
                state = state with { ReserveLinked = state.ReserveLinked.Where(id => id != character).ToHashSet() };
                result = new EssMgrOperationResult(true, ToClient(state));
                break;

            case EssMgrOperation.RequestUnlockReserveBank:
                // a key opens a bank only in its own quadrant, and only when the character holds one: the key is taken out of the inventory
                if (EssReserveKeys.Find(request.KeyTypeId) is not { } key || state.PulsesRemaining > 0 ||
                    _quadrantOf?.Invoke(systemId) != key.Quadrant || _consumeKey is null ||
                    !await _consumeKey(connection, transaction, character, key.KeyTypeId, cancellationToken))
                {
                    return new EssMgrOperationResult(false, ToClient(state));
                }
                state = state with
                {
                    ReserveKeyTypeId = key.KeyTypeId,
                    PulsesTotal = key.TotalPulses,
                    PulsesRemaining = key.TotalPulses,
                    ReserveOpenedMs = nowMs,
                    NextPulseMs = nowMs + EssRules.PulseIntervalMs,
                };
                await using (var open = Command(
                    "UPDATE {schema}.systems SET reserve_key_type_id = $2, reserve_pulses_total = $3, reserve_pulses_remaining = $3, reserve_opened_ms = $4, next_pulse_ms = $5 WHERE solar_system_id = $1",
                    connection, transaction, systemId, key.KeyTypeId, key.TotalPulses, nowMs, state.NextPulseMs))
                {
                    await open.ExecuteNonQueryAsync(cancellationToken);
                }
                result = new EssMgrOperationResult(true, ToClient(state));
                break;

            default:
                return new EssMgrOperationResult(false, ToClient(state));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>The pilot of a character in the system when it may link now (in the system, a linkable ship, within range); null otherwise.</summary>
    private async ValueTask<EssHolder?> LinkHolderAsync(int systemId, long characterId, long essId, CancellationToken cancellationToken) =>
        await HolderAsync(systemId, characterId, essId, cancellationToken) is { InSystem: true, LinkableShip: true } holder
        && holder.DistanceMeters <= EssRules.MaxLinkDistanceMeters
            ? holder
            : null;

    private async ValueTask<EssHolder?> HolderAsync(int systemId, long characterId, long essId, CancellationToken cancellationToken) =>
        _holderOf is null ? null : await _holderOf(systemId, characterId, essId, cancellationToken);

    /// <summary>
    /// What the clock does now. A link is checked on every tick: a thief who has left the system, changed ship, or gone beyond the link range breaks it (the
    /// client's reasons 1 and 2); a link that completes while its thief is still linked is a theft (the thief's wallet gets <see cref="EssRules.ThiefPayout"/>,
    /// and each Reserve Bank pulse that is due is paid to the linked characters. Systems are worked one at a time; a system whose wallet cannot take the money is
    /// logged and retried at the next tick.
    /// </summary>
    public async Task<IReadOnlyList<EssEvent>> TickAsync(long nowMs, CancellationToken cancellationToken = default)
    {
        var events = new List<EssEvent>();
        foreach (var systemId in await DueSystemsAsync(nowMs, cancellationToken))
        {
            try
            {
                events.AddRange(await TickSystemAsync(systemId, nowMs, cancellationToken));
            }
            catch (Exception error) when (error is WalletNotFoundException or MoneyLimitException or InsufficientFundsException or WalletOperationConflictException or Npgsql.NpgsqlException)
            {
                _log?.Invoke($"the ESS of solar system {systemId} could not be worked and is retried: {error.Message}");
            }
        }
        return events;
    }

    private async Task<IReadOnlyList<int>> DueSystemsAsync(long nowMs, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(
            """
            SELECT solar_system_id FROM {schema}.systems
            WHERE main_link_id IS NOT NULL OR next_payout_ms <= $1 OR (reserve_pulses_remaining > 0 AND next_pulse_ms <= $1)
            ORDER BY solar_system_id
            """,
            connection, null, nowMs);
        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }

    private async Task<IReadOnlyList<EssEvent>> TickSystemAsync(int systemId, long nowMs, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await LoadAsync(connection, transaction, systemId, lockRow: true, cancellationToken) is not { } state)
        {
            return [];
        }
        var events = new List<EssEvent>();

        if (state.MainLink is { } link)
        {
            var holder = await HolderAsync(systemId, link.CharacterId, state.EssId, cancellationToken);
            var breakReason = holder is not { InSystem: true }
                ? EssRules.UnlinkedProximity
                : holder.ShipId != link.ShipId
                    ? EssRules.UnlinkedShipChange
                    : holder.DistanceMeters > EssRules.MaxLinkDistanceMeters
                        ? EssRules.UnlinkedProximity
                        : (int?)null;
            if (breakReason is { } reason)
            {
                state = state with { MainLink = null };
                events.Add(new EssEvent(EssEventKind.LinkEnded, systemId, link.Id, link.CharacterId, 0, reason));
            }
            else if (link.CompletesMs <= nowMs)
            {
                var taken = state.MainValue;
                var thiefPayout = EssRules.ThiefPayout(taken);
                if (thiefPayout > 0)
                {
                    await _wallet.AdjustAsync(connection, transaction, link.CharacterId, thiefPayout, state.OwnerId ?? 0, EssRules.EssRefType,
                        "ESS main bank theft", nowMs, $"ess:theft:{link.Id}", cancellationToken);
                    await InsertTheftAsync(connection, transaction, systemId, "main", link.CharacterId, thiefPayout, nowMs, cancellationToken);
                }
                state = state with { MainValue = 0, MainLink = null, MainThefts = [.. state.MainThefts, new(link.CharacterId, thiefPayout, nowMs)] };
                events.Add(new EssEvent(EssEventKind.Theft, systemId, link.Id, link.CharacterId, thiefPayout, 0));
            }
        }

        if (state.NextPayoutMs <= nowMs)
        {
            var owner = _sovereigntyOwnerOf is null ? null : await _sovereigntyOwnerOf(systemId, cancellationToken);
            owner ??= state.OwnerId;
            if (owner is { } payee && state.MainValue > 0)
            {
                await _wallet.AdjustAsync(connection, transaction, payee, state.MainValue, 0, EssRules.EssRefType, "ESS main bank autopayment",
                    nowMs, $"ess:autopay:{systemId}:{state.NextPayoutMs}", cancellationToken);
                events.Add(new EssEvent(EssEventKind.Changed, systemId, string.Empty, payee, state.MainValue, 0));
                state = state with { MainValue = 0 };
            }
            var next = state.NextPayoutMs;
            while (next <= nowMs)
            {
                next += EssRules.AutoPayIntervalMs;
            }
            state = state with { NextPayoutMs = next };
        }

        var pulsed = false;
        while (state.PulsesRemaining > 0 && state.NextPulseMs is { } due && due <= nowMs)
        {
            var pulse = state.PulsesTotal - state.PulsesRemaining + 1;
            var payout = Math.Min(EssRules.Money((decimal)EssRules.ReservePulsePayout(pulse, state.PulsesTotal, _reserveMaxPayout)), state.ReserveValue);
            var linked = state.ReserveLinked.Order().ToList();
            if (payout > 0 && linked.Count > 0)
            {
                var share = EssRules.Money(payout / linked.Count);
                if (share > 0)
                {
                    var sharesByCorporation = new Dictionary<long, decimal>();
                    foreach (var character in linked)
                    {
                        var corporationId = _corporationOf is null
                            ? throw new InvalidOperationException("ESS reserve payouts require the linked character's corporation")
                            : await _corporationOf(character, cancellationToken);
                        if (corporationId is not > 0)
                            throw new InvalidOperationException($"ESS reserve recipient {character} has no corporation");
                        sharesByCorporation[corporationId.Value] = sharesByCorporation.GetValueOrDefault(corporationId.Value) + share;
                        await InsertTheftAsync(connection, transaction, systemId, "reserve", character, share, due, cancellationToken);
                    }
                    foreach (var (corporationId, amount) in sharesByCorporation.OrderBy(pair => pair.Key))
                    {
                        await _wallet.OpenCorporationDivisionsAsync(connection, transaction, corporationId, cancellationToken);
                        await _wallet.AdjustAsync(connection, transaction, new WalletAccount(corporationId, 1001), amount, 0, EssRules.EssRefType,
                            "ESS reserve bank payout", due, $"ess:reserve:{systemId}:{state.ReserveOpenedMs}:{pulse}:{corporationId}",
                            "ess-reserve", cancellationToken);
                    }
                    state = state with { ReserveValue = state.ReserveValue - share * linked.Count };
                }
            }
            state = state with
            {
                PulsesRemaining = state.PulsesRemaining - 1,
                NextPulseMs = state.PulsesRemaining - 1 > 0 ? due + EssRules.PulseIntervalMs : null,
            };
            pulsed = true;
        }
        if (pulsed)
        {
            events.Add(new EssEvent(EssEventKind.Changed, systemId, string.Empty, 0, 0, 0));
        }

        await using (var save = Command(
            """
            UPDATE {schema}.systems SET main_value = $2, reserve_value = $3, main_link_id = $4, main_link_character_id = $5, main_link_started_ms = $6,
                main_link_completes_ms = $7, next_payout_ms = $8, reserve_pulses_remaining = $9, next_pulse_ms = $10, main_link_ship_id = $11
            WHERE solar_system_id = $1
            """,
            connection, transaction, systemId, state.MainValue, state.ReserveValue, state.MainLink?.Id, state.MainLink?.CharacterId,
            state.MainLink?.StartedMs, state.MainLink?.CompletesMs, state.NextPayoutMs, state.PulsesRemaining, state.NextPulseMs, state.MainLink?.ShipId))
        {
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return events;
    }

    private async Task InsertTheftAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int systemId, string bank, long characterId, decimal amount, long atMs, CancellationToken cancellationToken)
    {
        await using var insert = Command(
            "INSERT INTO {schema}.thefts (solar_system_id, bank, character_id, amount, at_ms) VALUES ($1, $2, $3, $4, $5)",
            connection, transaction, systemId, bank, characterId, amount, atMs);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveLinkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int systemId, EssLinkRecord link, CancellationToken cancellationToken)
    {
        await using var save = Command(
            "UPDATE {schema}.systems SET main_link_id = $2, main_link_character_id = $3, main_link_started_ms = $4, main_link_completes_ms = $5, main_link_ship_id = $6 WHERE solar_system_id = $1",
            connection, transaction, systemId, link.Id, link.CharacterId, link.StartedMs, link.CompletesMs, link.ShipId);
        await save.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ClearLinkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int systemId, CancellationToken cancellationToken)
    {
        await using var clear = Command(
            "UPDATE {schema}.systems SET main_link_id = NULL, main_link_character_id = NULL, main_link_started_ms = NULL, main_link_completes_ms = NULL, main_link_ship_id = NULL WHERE solar_system_id = $1",
            connection, transaction, systemId);
        await clear.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<EssState?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, int systemId, bool lockRow, CancellationToken cancellationToken)
    {
        EssState? state = null;
        await using (var command = Command(
            $"SELECT {Columns} FROM {{schema}}.systems WHERE solar_system_id = $1" + (lockRow ? " FOR UPDATE" : ""), connection, transaction, systemId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            state = new EssState(
                SolarSystemId: reader.GetInt32(0),
                EssId: reader.GetInt64(1),
                BeaconId: reader.GetInt64(2),
                TypeId: reader.GetInt32(3),
                OwnerId: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                CurrentOutput: reader.GetDouble(5),
                MainValue: reader.GetDecimal(6),
                ReserveValue: reader.GetDecimal(7),
                MainLink: reader.IsDBNull(8) ? null : new EssLinkRecord(reader.GetString(8), reader.GetInt64(9), reader.GetInt64(18), reader.GetInt64(10), reader.GetInt64(11)),
                NextPayoutMs: reader.GetInt64(12),
                ReserveKeyTypeId: reader.IsDBNull(13) ? null : reader.GetInt64(13),
                PulsesTotal: reader.GetInt32(14),
                PulsesRemaining: reader.GetInt32(15),
                ReserveOpenedMs: reader.IsDBNull(16) ? null : reader.GetInt64(16),
                NextPulseMs: reader.IsDBNull(17) ? null : reader.GetInt64(17),
                ReserveLinked: new HashSet<long>(),
                MainThefts: [],
                ReserveThefts: []);
        }

        var linked = new HashSet<long>();
        await using (var command = Command("SELECT character_id FROM {schema}.reserve_links WHERE solar_system_id = $1", connection, transaction, systemId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                linked.Add(reader.GetInt64(0));
            }
        }

        var mainThefts = new List<EssTheftRecord>();
        await using (var command = Command(
            "SELECT character_id, amount, at_ms FROM {schema}.thefts WHERE solar_system_id = $1 AND bank = 'main' ORDER BY id DESC LIMIT 50", connection, transaction, systemId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                mainThefts.Add(new EssTheftRecord(reader.GetInt64(0), reader.GetDecimal(1), reader.GetInt64(2)));
            }
        }
        mainThefts.Reverse();

        var reserveThefts = new List<EssTheftRecord>();
        await using (var command = Command(
            "SELECT character_id, SUM(amount), MAX(at_ms) FROM {schema}.thefts WHERE solar_system_id = $1 AND bank = 'reserve' GROUP BY character_id ORDER BY MAX(at_ms)",
            connection, transaction, systemId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                reserveThefts.Add(new EssTheftRecord(reader.GetInt64(0), reader.GetDecimal(1), reader.GetInt64(2)));
            }
        }

        return state with { ReserveLinked = linked, MainThefts = mainThefts, ReserveThefts = reserveThefts };
    }

    /// <summary>The state as the client's essMgr reads it (the payload of <c>GetDataForClientSolarSystem</c> and of the change notifications).</summary>
    public static EssMgrSystemData ToClient(EssState state) => new(
        EssId: state.EssId,
        SolarSystemId: state.SolarSystemId,
        CurrentOutput: state.CurrentOutput,
        MainValue: (double)state.MainValue,
        ReserveValue: (double)state.ReserveValue,
        BeaconId: state.BeaconId,
        TypeId: state.TypeId,
        MainBankLink: state.MainLink is null ? null : ToClientLink(state.MainLink),
        ReserveBankLastPulseInitiatedFileTime: state.ReserveOpenedMs is { } opened ? NpcBounty.Filetime(opened) : null,
        ReserveBankPulsesRemaining: state.PulsesRemaining,
        ReserveBankPulsesTotal: state.PulsesTotal,
        ReserveLinkedCharacterIds: state.ReserveLinked,
        MainBankThefts: [.. state.MainThefts.Select(theft => (PyValue)Py.Dict(
            ("characterID", Py.Number(theft.CharacterId)),
            ("amount", Py.Number((double)theft.Amount))))],
        ReserveBankThefts: [.. state.ReserveThefts.Select(theft => (PyValue)Py.Dict(
            ("characterID", Py.Number(theft.CharacterId)),
            ("amount", Py.Number((double)theft.Amount)),
            ("lastTheft", Py.FileTimeLong(NpcBounty.Filetime(theft.AtMs)))))]);

    private static EssMgrMainBankLink ToClientLink(EssLinkRecord link) =>
        new(link.Id, link.CharacterId, NpcBounty.Filetime(link.StartedMs), NpcBounty.Filetime(link.CompletesMs));
}
