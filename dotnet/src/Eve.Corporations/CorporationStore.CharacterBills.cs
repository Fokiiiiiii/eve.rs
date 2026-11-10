using System.Globalization;
using Eve.Events;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    private const string CharacterBillColumns =
        "bill_id,bill_type_id,amount_isk,interest,debtor_character_id,creditor_id,due_filetime,external_id,external_id2,paid,paid_filetime,paid_by_owner_id,operation_key";

    public async Task<CharacterBill> CreateCharacterBillAsync(
        long debtorCharacterId, int billTypeId, decimal amountIsk, long creditorId, long dueFiletime,
        string operationKey, decimal interest = 0m, long externalId = -1, long externalId2 = -1,
        CancellationToken token = default)
    {
        if (debtorCharacterId <= 0 || billTypeId <= 0 || amountIsk <= 0 || decimal.Round(amountIsk, 2) != amountIsk ||
            creditorId <= 0 || dueFiletime <= 0 || interest < 0 || decimal.Round(interest, 2) != interest)
            throw new CorporationError("BILL_INVALID");
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var fingerprint = string.Join("|", billTypeId,
            amountIsk.ToString("0.00", CultureInfo.InvariantCulture),
            interest.ToString("0.00", CultureInfo.InvariantCulture), debtorCharacterId, creditorId,
            dueFiletime, externalId, externalId2);
        var bill = await WriteAsync(async (connection, transaction) =>
        {
            await using (var operationLock = Command(connection, transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"character-bill:{_schema}:{operationKey}"))
                await operationLock.ExecuteNonQueryAsync(token);
            CharacterBill? existing = null;
            string? existingFingerprint = null;
            await using (var read = Command(connection, transaction,
                $"SELECT {CharacterBillColumns},request_fingerprint FROM {_schema}.character_bills WHERE operation_key=$1 FOR UPDATE", operationKey))
            await using (var reader = await read.ExecuteReaderAsync(token))
                if (await reader.ReadAsync(token))
                {
                    existing = ReadCharacterBill(reader);
                    existingFingerprint = reader.GetString(13);
                }
            if (existing is not null)
            {
                if (existingFingerprint != fingerprint) throw new CorporationError("BILL_OPERATION_MISMATCH");
                return existing;
            }

            CharacterBill created;
            await using (var insert = Command(connection, transaction, $"""
                INSERT INTO {_schema}.character_bills
                    (bill_type_id,amount_isk,interest,debtor_character_id,creditor_id,due_filetime,external_id,external_id2,operation_key,request_fingerprint)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                RETURNING {CharacterBillColumns}
                """, billTypeId, amountIsk, interest, debtorCharacterId, creditorId, dueFiletime,
                externalId, externalId2, operationKey, fingerprint))
            await using (var reader = await insert.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Character bill insert returned no row.");
                created = ReadCharacterBill(reader);
            }
            if (_events is not null)
                await _events.PublishAsync(connection, transaction, CharacterBillEvents.IssuedTopic,
                    created, $"bill:{created.BillId}:issued", token);
            return created;
        }, token);
        _events?.Wake();
        return bill;
    }

    public async Task<CharacterBillPaymentResult> PayCharacterBillAsync(
        long billId, long characterId, long paidAtMs, CancellationToken token = default)
    {
        if (billId <= 0 || characterId <= 0 || paidAtMs < 0)
            return new(false, "BILL_NOT_PAYABLE");

        await using var connection = await _source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        CharacterBill? bill = null;
        await using (var command = Command(connection, transaction,
            $"SELECT {CharacterBillColumns} FROM {_schema}.character_bills WHERE bill_id=$1 AND debtor_character_id=$2 AND NOT paid FOR UPDATE",
            billId, characterId))
        await using (var reader = await command.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token)) bill = ReadCharacterBill(reader);
        if (bill is null) return new(false, "BILL_NOT_PAYABLE");

        bool isCorporationCreditor;
        await using (var creditor = Command(connection, transaction,
            $"SELECT EXISTS(SELECT 1 FROM {_schema}.corporations WHERE id=$1 AND NOT deleted)", bill.CreditorId))
            isCorporationCreditor = (bool)(await creditor.ExecuteScalarAsync(token) ?? false);
        WalletSettlement settlement;
        try
        {
            var operationKey = $"character-bill:{bill.BillId}:payment";
            settlement = isCorporationCreditor
                ? await _wallets.TransferAsync(connection, transaction, new WalletAccount(characterId),
                    new WalletAccount(bill.CreditorId), bill.AmountIsk, 17, "Bill payment", paidAtMs,
                    operationKey, "bill_payment", token)
                : await _wallets.AdjustAsync(connection, transaction, new WalletAccount(characterId),
                    -bill.AmountIsk, bill.CreditorId, 17, "Bill payment", paidAtMs,
                    operationKey, "bill_payment", token);
        }
        catch (InsufficientFundsException error)
        {
            await transaction.RollbackAsync(token);
            return new(false, "INSUFFICIENT_FUNDS", bill.AmountIsk, error.AvailableBalance);
        }
        catch (Exception error) when (error is MoneyLimitException or WalletNotFoundException or WalletOperationConflictException)
        {
            await transaction.RollbackAsync(token);
            return new(false, "WALLET_DEBIT_FAILED", bill.AmountIsk);
        }

        var paidFiletime = checked((paidAtMs + 11_644_473_600_000L) * 10_000L);
        await using (var mark = Command(connection, transaction,
            $"UPDATE {_schema}.character_bills SET paid=true,paid_filetime=$2,paid_by_owner_id=$3 WHERE bill_id=$1 AND NOT paid",
            billId, paidFiletime, characterId))
            if (await mark.ExecuteNonQueryAsync(token) != 1)
                throw new CorporationError("BILL_NOT_PAYABLE");

        if (_events is not null)
            await _events.PublishAsync(connection, transaction, CharacterBillEvents.PaidTopic,
                bill with { Paid = true, PaidFiletime = paidFiletime, PaidByOwnerId = characterId },
                $"bill:{bill.BillId}:paid", token);
        await transaction.CommitAsync(token);
        _events?.Wake();
        if (!settlement.Replayed)
            foreach (var entry in settlement.Entries.GroupBy(entry => (entry.OwnerId, entry.AccountKey)).Select(group => group.Last()))
                _wallets.AnnounceCommitted(new WalletBalanceChange(entry.OwnerId, entry.AccountKey, entry.Balance));
        return new(true, null, bill.AmountIsk);
    }

    private static CharacterBill ReadCharacterBill(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt32(1), reader.GetDecimal(2), reader.GetDecimal(3),
        reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
        reader.GetBoolean(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.GetString(12));
}

public sealed record CharacterBill(
    long BillId, int BillTypeId, decimal AmountIsk, decimal Interest, long DebtorCharacterId,
    long CreditorId, long DueFiletime, long ExternalId, long ExternalId2, bool Paid,
    long? PaidFiletime, long? PaidByOwnerId, string OperationKey);

public sealed record CharacterBillPaymentResult(bool Paid, string? Error, decimal Amount = 0m, decimal? Balance = null);

public static class CharacterBillEvents
{
    public const string IssuedTopic = "bill.issued";
    public const string PaidTopic = "bill.paid";
}
