using Eve.Data;
using Eve.Inventory;
using Eve.Wallet;
using Npgsql;

namespace Eve.Trade;

/// <summary>A trade the rules refuse. The message is what the pilots are told (the client shows it as a notification).</summary>
public sealed class TradeRefusedException(string message) : Exception(message);

/// <summary>
/// The trade as it stands: the two traders in the order the client lists them (initiator first), what each offers in ISK, who has
/// accepted, and the items in custody in the trade container.
/// </summary>
public sealed record TradeView(long TradeId, long LocationId, long SolarSystemId, long InitiatorId, long TargetId, string Status,
    decimal InitiatorIsk, decimal TargetIsk, bool InitiatorAccepted, bool TargetAccepted, IReadOnlyList<Item> Items)
{
    public bool IsOpen => Status == "open";
    public bool Involves(long characterId) => characterId == InitiatorId || characterId == TargetId;
    public long OtherOf(long characterId) => characterId == InitiatorId ? TargetId : InitiatorId;
    public decimal MoneyOf(long characterId) => characterId == InitiatorId ? InitiatorIsk : TargetIsk;
    public bool AcceptedBy(long characterId) => characterId == InitiatorId ? InitiatorAccepted : TargetAccepted;
}

/// <summary>
/// <c>OnItemChange</c> reports). <see cref="Removed"/> marks a stack that was merged back into another (its size is now 0 for the client).
/// </summary>
public sealed record TradeItemMove(Item After, long PreviousOwnerId, long PreviousLocationId, int PreviousFlag, long PreviousQuantity, bool Removed = false);

/// <summary>A change to a trade: the view after it and the item moves the pilots must be told about.</summary>
public sealed record TradeChange(TradeView View, IReadOnlyList<TradeItemMove> Moves);

public enum TradeOfferOutcome
{
    /// <summary>The manifest the pilot sent is not the trade's: the acceptance is cleared.</summary>
    Mismatch,
    /// <summary>The pilot accepted; the other has not.</summary>
    Pending,
    /// <summary>Both accepted: the items and the ISK moved in one transaction.</summary>
    Completed,
    /// <summary>Both accepted but the trade could not complete (a pilot no longer has the ISK); acceptance is cleared.</summary>
    Refused,
}

public sealed record TradeOfferResult(TradeOfferOutcome Outcome, TradeChange Change, string? Reason = null);

/// <summary>
/// Direct trades between two pilots docked in the same place. A trade's items sit in its container until the trade completes (both
/// accepted, the items and the manifest unchanged) or is aborted (every item goes back to where it was). Every change is one
/// PostgreSQL transaction that locks the trade first; ISK moves through <see cref="WalletTransactions"/> in the same transaction.
/// </summary>
public sealed class TradeAuthority
{
    public const string DefaultSchema = "trade";

    /// <summary>The ISK account of a character's wallet.</summary>
    private const int IskAccount = 1000;

    /// <summary>The ESI wallet ref type <c>player_trading</c> (CCP eve-glue, <c>JournalRefTypeEnumV2</c>).</summary>
    public const int PlayerTradingRefType = 1;

    private readonly NpgsqlDataSource _source;
    private readonly PgItemStore _items;
    private readonly WalletTransactions _wallets;
    private readonly PgWalletStore _balances;
    private readonly Func<long> _nowMs;
    private readonly string _schema;

    public TradeAuthority(NpgsqlDataSource source, PgItemStore items, WalletTransactions wallets, PgWalletStore balances, Func<long> nowMs, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _items = items;
        _wallets = wallets;
        _balances = balances;
        _nowMs = nowMs;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() => [.. Migrations.Embedded(typeof(TradeAuthority).Assembly, "Migrations")];

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    // ── reads ────────────────────────────────────────────────────────────────

    /// <summary>The trade with this id, or null.</summary>
    public async Task<TradeView?> ViewAsync(long tradeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var view = await ReadViewAsync(connection, transaction, tradeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return view;
    }

    /// <summary>The open trade a pilot is in, or null.</summary>
    public async Task<TradeView?> OpenViewForAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT p.trade_id FROM {_schema}.participants p JOIN {_schema}.sessions s ON s.trade_id = p.trade_id WHERE p.character_id = $1 AND s.status = 'open'",
            connection)
        {
            Parameters = { new() { Value = characterId } },
        };
        if (await command.ExecuteScalarAsync(cancellationToken) is not long tradeId)
            return null;
        return await ViewAsync(tradeId, cancellationToken);
    }

    // ── writes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a trade between two pilots. Each pilot can be in one open trade at a time (the participant key), and an id names the trade
    /// in every later call. The caller has checked that both are docked at <paramref name="locationId"/>.
    /// </summary>
    public async Task<TradeView> InitiateAsync(long initiatorId, long targetId, long locationId, long solarSystemId, CancellationToken cancellationToken = default)
    {
        if (initiatorId <= 0 || targetId <= 0 || initiatorId == targetId) throw new ArgumentOutOfRangeException(nameof(targetId));
        if (locationId <= 0) throw new ArgumentOutOfRangeException(nameof(locationId));
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long tradeId;
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {_schema}.sessions(trade_id, location_id, solar_system_id, initiator_id, target_id, status, created_ms) VALUES (nextval('{_schema}.trade_ids'), $1, $2, $3, $4, 'open', $5) RETURNING trade_id",
            connection, transaction)
        {
            Parameters = { new() { Value = locationId }, new() { Value = solarSystemId }, new() { Value = initiatorId }, new() { Value = targetId }, new() { Value = _nowMs() } },
        })
        {
            tradeId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        try
        {
            foreach (var character in new[] { initiatorId, targetId })
            {
                await using var participant = new NpgsqlCommand(
                    $"INSERT INTO {_schema}.participants(character_id, trade_id) VALUES ($1, $2)", connection, transaction)
                {
                    Parameters = { new() { Value = character }, new() { Value = tradeId } },
                };
                await participant.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == "23505")
        {
            throw new TradeRefusedException("One of those pilots is already in another direct trade.");
        }
        var view = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new InvalidOperationException("the trade was not stored");
        await transaction.CommitAsync(cancellationToken);
        return view;
    }

    /// <summary>
    /// Puts an item of one trader into the trade's container. <paramref name="quantity"/> 0 (or a singleton, or the whole stack) moves the item;
    /// a smaller quantity splits the stack and moves the new part. Any change clears both acceptances.
    /// </summary>
    public async Task<TradeChange> StageAsync(long tradeId, long characterId, long itemId, long quantity, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var view = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new TradeRefusedException("Trade session is no longer available.");
        RequireOpenParticipant(view, characterId);

        var item = await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken)
            ?? throw new TradeRefusedException("That item is no longer there.");
        if (item.OwnerId != characterId || item.LocationId != view.LocationId || item.Flag != ItemFlags.Hangar)
            throw new TradeRefusedException("Only items in your hangar here can be offered.");

        var moves = new List<TradeItemMove>();
        Item staged;
        long? sourceId = null;
        if (item.Singleton || quantity <= 0 || quantity >= item.Quantity)
        {
            await _items.MoveAsync(connection, transaction, item.Id, tradeId, ItemFlags.Hangar, cancellationToken);
            staged = await _items.LockAndGetAsync(connection, transaction, item.Id, cancellationToken) ?? throw new InvalidOperationException("the staged item vanished");
            moves.Add(new TradeItemMove(staged, item.OwnerId, item.LocationId, item.Flag, item.Quantity));
        }
        else
        {
            staged = await _items.MoveStackAsync(connection, transaction, item, quantity, characterId, tradeId, ItemFlags.Hangar, cancellationToken);
            sourceId = item.Id;
            var source = await _items.LockAndGetAsync(connection, transaction, item.Id, cancellationToken)
                ?? throw new InvalidOperationException("the split stack vanished");
            moves.Add(new TradeItemMove(source, source.OwnerId, source.LocationId, source.Flag, item.Quantity));
            moves.Add(new TradeItemMove(staged, characterId, item.LocationId, item.Flag, 0));
        }

        await using (var stage = new NpgsqlCommand(
            $"INSERT INTO {_schema}.staged_items(item_id, trade_id, owner_id, source_item_id, origin_location_id, origin_flag) VALUES ($1, $2, $3, $4, $5, $6)",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = staged.Id }, new() { Value = tradeId }, new() { Value = characterId },
                new() { Value = (object?)sourceId ?? DBNull.Value }, new() { Value = item.LocationId }, new() { Value = item.Flag },
            },
        })
        {
            await stage.ExecuteNonQueryAsync(cancellationToken);
        }
        await ClearAcceptanceAsync(connection, transaction, tradeId, cancellationToken);
        var after = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new InvalidOperationException("the trade vanished");
        await transaction.CommitAsync(cancellationToken);
        return new TradeChange(after, moves);
    }

    /// <summary>Sets what a pilot offers in ISK (it cannot exceed the wallet now). Any change clears both acceptances.</summary>
    public async Task<TradeChange> SetMoneyAsync(long tradeId, long characterId, decimal amount, CancellationToken cancellationToken = default)
    {
        if (amount < 0 || decimal.Round(amount, 2) != amount) throw new TradeRefusedException("ISK offer must be zero or greater.");
        var wallet = await _balances.GetAsync(characterId, cancellationToken);
        if (wallet is null || wallet.Isk < amount) throw new TradeRefusedException("You do not have enough ISK to offer that amount.");

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var view = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new TradeRefusedException("Trade session is no longer available.");
        RequireOpenParticipant(view, characterId);
        var column = characterId == view.InitiatorId ? "initiator_isk" : "target_isk";
        await using (var update = new NpgsqlCommand($"UPDATE {_schema}.sessions SET {column} = $2 WHERE trade_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = tradeId }, new() { Value = amount } },
        })
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await ClearAcceptanceAsync(connection, transaction, tradeId, cancellationToken);
        var after = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new InvalidOperationException("the trade vanished");
        await transaction.CommitAsync(cancellationToken);
        return new TradeChange(after, []);
    }

    /// <summary>
    /// A pilot accepts the manifest it was shown: the ISK of both sides (trader order) and the items in custody. A manifest that is not the
    /// trade's clears both acceptances. When both pilots have accepted the trade completes, or is refused if a pilot no longer has the ISK.
    /// </summary>
    public async Task<TradeOfferResult> OfferAsync(long tradeId, long characterId, IReadOnlyList<decimal> manifestMoney, IReadOnlySet<long> manifestItems, CancellationToken cancellationToken = default)
    {
        try
        {
            return await OfferTransactionAsync(tradeId, characterId, manifestMoney, manifestItems, cancellationToken);
        }
        catch (InsufficientFundsException)
        {
            // the transaction above rolled back: clear the acceptances and report the refusal with the trade as it stands
            var change = await ClearAcceptanceChangeAsync(tradeId, cancellationToken);
            return new TradeOfferResult(TradeOfferOutcome.Refused, change, "One pilot no longer has enough ISK to complete the trade.");
        }
    }

    private async Task<TradeOfferResult> OfferTransactionAsync(long tradeId, long characterId, IReadOnlyList<decimal> manifestMoney, IReadOnlySet<long> manifestItems, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var view = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new TradeRefusedException("Trade session is no longer available.");
        RequireOpenParticipant(view, characterId);

        var stagedIds = view.Items.Select(i => i.Id).ToHashSet();
        var manifestMatches = manifestMoney.Count == 2
            && manifestMoney[0] == view.InitiatorIsk && manifestMoney[1] == view.TargetIsk
            && stagedIds.SetEquals(manifestItems);
        if (!manifestMatches)
        {
            await ClearAcceptanceAsync(connection, transaction, tradeId, cancellationToken);
            var reset = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new InvalidOperationException("the trade vanished");
            await transaction.CommitAsync(cancellationToken);
            return new TradeOfferResult(TradeOfferOutcome.Mismatch, new TradeChange(reset, []));
        }

        await SetAcceptedAsync(connection, transaction, tradeId, characterId == view.InitiatorId, cancellationToken);
        if (!(view.InitiatorAccepted || characterId == view.InitiatorId) || !(view.TargetAccepted || characterId == view.TargetId))
        {
            var pending = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new InvalidOperationException("the trade vanished");
            await transaction.CommitAsync(cancellationToken);
            return new TradeOfferResult(TradeOfferOutcome.Pending, new TradeChange(pending, []));
        }

        // both accepted: complete in this transaction
        var moves = new List<TradeItemMove>();
        foreach (var (itemId, owner) in await StagedOwnersAsync(connection, transaction, tradeId, cancellationToken))
        {
            var receiver = view.OtherOf(owner);
            var before = await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken)
                ?? throw new InvalidOperationException("a staged item vanished");
            await _items.MoveToOwnerAsync(connection, transaction, itemId, receiver, view.LocationId, ItemFlags.Hangar, cancellationToken);
            var after = await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) ?? throw new InvalidOperationException("a moved item vanished");
            moves.Add(new TradeItemMove(after, before.OwnerId, before.LocationId, before.Flag, before.Quantity));
        }

        var now = _nowMs();
        var transfers = new List<(long From, long To, decimal Amount)>();
        if (view.InitiatorIsk > 0) transfers.Add((view.InitiatorId, view.TargetId, view.InitiatorIsk));
        if (view.TargetIsk > 0) transfers.Add((view.TargetId, view.InitiatorId, view.TargetIsk));
        var index = 0;
        foreach (var (from, to, amount) in transfers)
        {
            await _wallets.TransferAsync(connection, transaction, from, to, amount, PlayerTradingRefType,
                $"Trade with {(from == view.InitiatorId ? view.TargetId : view.InitiatorId)}", now, $"trade:{tradeId}:isk:{index++}", cancellationToken);
        }

        await using (var close = new NpgsqlCommand(
            $"UPDATE {_schema}.sessions SET status = 'completed', closed_ms = $2 WHERE trade_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = tradeId }, new() { Value = now } },
        })
        {
            await close.ExecuteNonQueryAsync(cancellationToken);
        }
        await DeleteStagingAsync(connection, transaction, tradeId, cancellationToken);
        var done = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new InvalidOperationException("the trade vanished");
        await transaction.CommitAsync(cancellationToken);

        // the balances that moved: the wallets announce them after the commit
        foreach (var (from, to, _) in transfers)
        {
            foreach (var owner in new[] { from, to })
            {
                if (await _balances.GetAsync(owner, cancellationToken) is { } balance)
                    _wallets.AnnounceCommitted(new WalletBalanceChange(owner, IskAccount, balance.Isk));
            }
        }
        return new TradeOfferResult(TradeOfferOutcome.Completed, new TradeChange(done, moves));
    }

    /// <summary>
    /// Ends an open trade: every item in custody goes back to its origin (a split stack merges into the stack it came from), the trade
    /// is marked aborted and both pilots are free again. A trade that is not open is left as it is.
    /// </summary>
    public async Task<TradeChange?> AbortAsync(long tradeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var view = await ReadViewAsync(connection, transaction, tradeId, cancellationToken);
        if (view is null || !view.IsOpen)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var moves = new List<TradeItemMove>();
        foreach (var staged in await StagedRowsAsync(connection, transaction, tradeId, cancellationToken))
        {
            var item = await _items.LockAndGetAsync(connection, transaction, staged.ItemId, cancellationToken)
                ?? throw new InvalidOperationException("a staged item vanished");
            var merged = staged.SourceItemId is { } source
                ? await _items.MergeAsync(connection, transaction, item.Id, source, item.Quantity, staged.OwnerId, tradeId, staged.OriginLocationId, cancellationToken)
                : null;
            if (merged is not null)
            {
                moves.Add(new TradeItemMove(merged.DestinationAfter, merged.DestinationBefore.OwnerId, merged.DestinationBefore.LocationId, merged.DestinationBefore.Flag, merged.DestinationBefore.Quantity));
                moves.Add(new TradeItemMove(item with { Quantity = 0 }, staged.OwnerId, tradeId, ItemFlags.Hangar, item.Quantity, Removed: true));
                continue;
            }
            await _items.MoveAsync(connection, transaction, item.Id, staged.OriginLocationId, staged.OriginFlag, cancellationToken);
            var returned = await _items.LockAndGetAsync(connection, transaction, item.Id, cancellationToken) ?? throw new InvalidOperationException("a returned item vanished");
            moves.Add(new TradeItemMove(returned, returned.OwnerId, tradeId, ItemFlags.Hangar, item.Quantity));
        }

        await DeleteStagingAsync(connection, transaction, tradeId, cancellationToken);
        await using (var close = new NpgsqlCommand(
            $"UPDATE {_schema}.sessions SET status = 'aborted', closed_ms = $2 WHERE trade_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = tradeId }, new() { Value = _nowMs() } },
        })
        {
            await close.ExecuteNonQueryAsync(cancellationToken);
        }
        var after = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new InvalidOperationException("the trade vanished");
        await transaction.CommitAsync(cancellationToken);
        return new TradeChange(after, moves);
    }

    /// <summary>At start-up: every trade left open by a stopped host is aborted, so its items go back. Returns the trades aborted.</summary>
    public async Task<IReadOnlyList<TradeChange>> RecoverOpenAsync(CancellationToken cancellationToken = default)
    {
        var open = new List<long>();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand($"SELECT trade_id FROM {_schema}.sessions WHERE status = 'open' ORDER BY trade_id", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) open.Add(reader.GetInt64(0));
        }
        var changes = new List<TradeChange>();
        foreach (var tradeId in open)
        {
            if (await AbortAsync(tradeId, cancellationToken) is { } change) changes.Add(change);
        }
        return changes;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void RequireOpenParticipant(TradeView view, long characterId)
    {
        if (!view.IsOpen) throw new TradeRefusedException("Trade session is no longer available.");
        if (!view.Involves(characterId)) throw new TradeRefusedException("You are not a participant in this trade.");
    }

    private async Task<TradeView?> ReadViewAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long tradeId, CancellationToken cancellationToken)
    {
        string? status;
        TradeView? view = null;
        await using (var command = new NpgsqlCommand(
            $"SELECT location_id, solar_system_id, initiator_id, target_id, status, initiator_isk, target_isk, initiator_accepted, target_accepted FROM {_schema}.sessions WHERE trade_id = $1 FOR UPDATE",
            connection, transaction)
        {
            Parameters = { new() { Value = tradeId } },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            status = reader.GetString(4);
            view = new TradeView(tradeId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), status,
                reader.GetDecimal(5), reader.GetDecimal(6), reader.GetBoolean(7), reader.GetBoolean(8), []);
        }
        var items = new List<Item>();
        foreach (var row in await StagedRowsAsync(connection, transaction, tradeId, cancellationToken))
        {
            if (await _items.LockAndGetAsync(connection, transaction, row.ItemId, cancellationToken) is { } item)
                items.Add(item);
        }
        return view with { Items = items };
    }

    private sealed record StagedRow(long ItemId, long OwnerId, long? SourceItemId, long OriginLocationId, int OriginFlag);

    private async Task<IReadOnlyList<StagedRow>> StagedRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long tradeId, CancellationToken cancellationToken)
    {
        var rows = new List<StagedRow>();
        await using var command = new NpgsqlCommand(
            $"SELECT item_id, owner_id, source_item_id, origin_location_id, origin_flag FROM {_schema}.staged_items WHERE trade_id = $1 ORDER BY item_id",
            connection, transaction)
        {
            Parameters = { new() { Value = tradeId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new StagedRow(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4)));
        }
        return rows;
    }

    private async Task<IReadOnlyList<(long ItemId, long Owner)>> StagedOwnersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long tradeId, CancellationToken cancellationToken) =>
        (await StagedRowsAsync(connection, transaction, tradeId, cancellationToken)).Select(r => (r.ItemId, r.OwnerId)).ToList();

    private async Task DeleteStagingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long tradeId, CancellationToken cancellationToken)
    {
        await using (var staged = new NpgsqlCommand($"DELETE FROM {_schema}.staged_items WHERE trade_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = tradeId } },
        })
        {
            await staged.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var participants = new NpgsqlCommand($"DELETE FROM {_schema}.participants WHERE trade_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = tradeId } },
        };
        await participants.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ClearAcceptanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long tradeId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.sessions SET initiator_accepted = false, target_accepted = false WHERE trade_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = tradeId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetAcceptedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long tradeId, bool initiator, CancellationToken cancellationToken)
    {
        var column = initiator ? "initiator_accepted" : "target_accepted";
        await using var command = new NpgsqlCommand($"UPDATE {_schema}.sessions SET {column} = true WHERE trade_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = tradeId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<TradeChange> ClearAcceptanceChangeAsync(long tradeId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ClearAcceptanceAsync(connection, transaction, tradeId, cancellationToken);
        var view = await ReadViewAsync(connection, transaction, tradeId, cancellationToken) ?? throw new InvalidOperationException("the trade vanished");
        await transaction.CommitAsync(cancellationToken);
        return new TradeChange(view, []);
    }
}
