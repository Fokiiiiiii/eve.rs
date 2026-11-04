using Eve.Data;
using Npgsql;

namespace Eve.Wallet;

/// <summary>Whose loyalty points: a character's wallet, or a corporation's wallet.</summary>
public enum LoyaltyHolderKind
{
    Character,
    Corporation,
}

/// <summary>The holder of a loyalty point balance (a character id or a corporation id).</summary>
public readonly record struct LoyaltyHolder(LoyaltyHolderKind Kind, long Id)
{
    public string KindText => Kind == LoyaltyHolderKind.Character ? "character" : "corporation";
}

/// <summary>One issuing corporation's balance in a holder's wallet.</summary>
public sealed record LoyaltyPointBalance(long IssuerCorpId, long Amount);

/// <summary>A character or corporation loyalty-point transfer with a stable request reference.</summary>
public sealed record LoyaltyPointTransferRequest(LoyaltyHolder Source, LoyaltyHolder Destination, long IssuerCorpId, long Amount, string Reference);

/// <summary>A change that committed: the balance before and after, and why it changed.</summary>
public sealed record LoyaltyPointChange(LoyaltyHolder Holder, long IssuerCorpId, long Before, long After, string Reason);

/// <summary>The debit would take the balance below zero.</summary>
public sealed class InsufficientLoyaltyPointsException(LoyaltyHolder holder, long issuerCorpId)
    : Exception($"{holder.KindText} {holder.Id} holds too few loyalty points of corporation {issuerCorpId}");

/// <summary>
/// Loyalty point wallets in PostgreSQL. The tables live in the wallet schema, next to the ISK and PLEX balances, and are
/// created by <see cref="PgWalletStore.MigrateAsync"/> (migration 006). A change is one row update and one journal row
/// in the caller's transaction, so a coupled change (a mission reward with its ISK, for example) commits or fails as one.
/// Notifications are published only after a commit, through <see cref="BalanceCommitted"/>.
/// </summary>
public sealed class LoyaltyPointStore
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public LoyaltyPointStore(NpgsqlDataSource source, string schema = PgWalletStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>A change committed by <see cref="ApplyAndCommitAsync"/> (or announced by a caller that committed it itself).</summary>
    public event Action<LoyaltyPointChange>? BalanceCommitted;

    public void AnnounceCommitted(LoyaltyPointChange change) => BalanceCommitted?.Invoke(change);

    /// <summary>The holder's balances, one row per issuing corporation with a balance, in issuer order.</summary>
    public async Task<IReadOnlyList<LoyaltyPointBalance>> ReadAsync(LoyaltyHolder holder, CancellationToken cancellationToken = default)
    {
        if (holder.Id <= 0) return [];
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT issuer_corp_id, amount FROM {_schema}.lp_balances WHERE holder_kind = $1 AND holder_id = $2 ORDER BY issuer_corp_id",
            connection)
        {
            Parameters = { new() { Value = holder.KindText }, new() { Value = holder.Id } },
        };
        var rows = new List<LoyaltyPointBalance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LoyaltyPointBalance(reader.GetInt64(0), reader.GetInt64(1)));
        }
        return rows;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> (negative to debit) to one balance inside the caller's transaction. Returns null when
    /// <paramref name="reference"/> has already been applied to this holder and issuer: nothing changes twice.
    /// Throws <see cref="InsufficientLoyaltyPointsException"/> when a debit would take the balance below zero.
    /// </summary>
    public async Task<LoyaltyPointChange?> ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LoyaltyHolder holder,
        long issuerCorpId,
        long delta,
        string reason,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (holder.Id <= 0) throw new ArgumentOutOfRangeException(nameof(holder), "the holder id must be positive");
        if (issuerCorpId <= 0) throw new ArgumentOutOfRangeException(nameof(issuerCorpId), "the issuer corporation must be positive");
        if (delta == 0) throw new ArgumentOutOfRangeException(nameof(delta), "a change must not be zero");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 64) throw new ArgumentException("a reason has 1 to 64 characters", nameof(reason));
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 128) throw new ArgumentException("a reference has 1 to 128 characters", nameof(reference));

        // Lock the balance row first (created at zero when absent), so two changes to one balance run in order.
        await using (var create = new NpgsqlCommand(
            $"INSERT INTO {_schema}.lp_balances (holder_kind, holder_id, issuer_corp_id, amount) VALUES ($1, $2, $3, 0) ON CONFLICT DO NOTHING",
            connection, transaction)
        {
            Parameters = { new() { Value = holder.KindText }, new() { Value = holder.Id }, new() { Value = issuerCorpId } },
        })
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        long before;
        await using (var read = new NpgsqlCommand(
            $"SELECT amount FROM {_schema}.lp_balances WHERE holder_kind = $1 AND holder_id = $2 AND issuer_corp_id = $3 FOR UPDATE",
            connection, transaction)
        {
            Parameters = { new() { Value = holder.KindText }, new() { Value = holder.Id }, new() { Value = issuerCorpId } },
        })
        {
            before = (long)(await read.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("the balance row is missing"));
        }

        long after;
        try { after = checked(before + delta); }
        catch (OverflowException) { throw new InsufficientLoyaltyPointsException(holder, issuerCorpId); }
        if (after < 0) throw new InsufficientLoyaltyPointsException(holder, issuerCorpId);

        await using (var journal = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.lp_journal (holder_kind, holder_id, issuer_corp_id, delta, balance_after, reason, reference)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (reference, holder_kind, holder_id, issuer_corp_id) DO NOTHING
            RETURNING id
            """,
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = holder.KindText }, new() { Value = holder.Id }, new() { Value = issuerCorpId },
                new() { Value = delta }, new() { Value = after }, new() { Value = reason }, new() { Value = reference },
            },
        })
        {
            if (await journal.ExecuteScalarAsync(cancellationToken) is null) return null;
        }

        await using (var update = new NpgsqlCommand(
            $"UPDATE {_schema}.lp_balances SET amount = $4 WHERE holder_kind = $1 AND holder_id = $2 AND issuer_corp_id = $3",
            connection, transaction)
        {
            Parameters = { new() { Value = holder.KindText }, new() { Value = holder.Id }, new() { Value = issuerCorpId }, new() { Value = after } },
        })
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return new LoyaltyPointChange(holder, issuerCorpId, before, after, reason);
    }

    /// <summary>One change in its own transaction; the announcement goes out after the commit, and only for an applied change.</summary>
    /// <summary>
    /// Credits loyalty points earned by a member (a mission, factional warfare or insurgency payout) inside the caller's transaction. The member's
    /// corporation takes its loyalty point tax (<see cref="LoyaltyTax"/>) into its own balance with the same issuer, and the member keeps the rest.
    /// </summary>
    public async Task<LoyaltyMemberCredit> CreditMemberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long corporationId,
        decimal corporationTaxRate, long issuerCorpId, long amount, string reason, string reference, CancellationToken cancellationToken = default)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "a credit is positive");
        var tax = LoyaltyTax.TaxOf(amount, corporationTaxRate, corporationId);
        if (amount - tax > 0)
            await ApplyAsync(connection, transaction, new LoyaltyHolder(LoyaltyHolderKind.Character, characterId), issuerCorpId, amount - tax, reason,
                reference + ":member", cancellationToken);
        if (tax > 0)
            await ApplyAsync(connection, transaction, new LoyaltyHolder(LoyaltyHolderKind.Corporation, corporationId), issuerCorpId, tax, reason,
                reference + ":corporation", cancellationToken);
        return new LoyaltyMemberCredit(amount - tax, tax);
    }

    public async Task<LoyaltyPointChange?> ApplyAndCommitAsync(
        LoyaltyHolder holder,
        long issuerCorpId,
        long delta,
        string reason,
        string reference,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var change = await ApplyAsync(connection, transaction, holder, issuerCorpId, delta, reason, reference, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (change is not null) AnnounceCommitted(change);
        return change;
    }

    /// <summary>Moves points between wallets atomically and announces the committed source then destination changes.</summary>
    public async Task<bool> TransferAndCommitAsync(LoyaltyPointTransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Source.Id <= 0 || request.Destination.Id <= 0 || request.Source == request.Destination)
            throw new ArgumentOutOfRangeException(nameof(request), "source and destination must be distinct positive wallets");
        if (request.IssuerCorpId <= 0 || request.Amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "issuer and amount must be positive");

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sourceFirst = request.Source.Kind < request.Destination.Kind ||
            request.Source.Kind == request.Destination.Kind && request.Source.Id <= request.Destination.Id;
        var first = sourceFirst ? request.Source : request.Destination;
        var second = first == request.Source ? request.Destination : request.Source;
        LoyaltyPointChange? sourceChange = null;
        LoyaltyPointChange? destinationChange = null;
        foreach (var holder in new[] { first, second })
        {
            var isSource = holder == request.Source;
            var change = await ApplyAsync(connection, transaction, holder, request.IssuerCorpId,
                isSource ? -request.Amount : request.Amount,
                isSource ? "transfer_out" : "transfer_in",
                request.Reference + (isSource ? ":out" : ":in"), cancellationToken);
            if (isSource) sourceChange = change;
            else destinationChange = change;
        }

        if ((sourceChange is null) != (destinationChange is null))
            throw new InvalidOperationException("a loyalty point transfer was only partly applied");
        await transaction.CommitAsync(cancellationToken);
        if (sourceChange is null) return false;
        AnnounceCommitted(sourceChange);
        AnnounceCommitted(destinationChange!);
        return true;
    }
}
