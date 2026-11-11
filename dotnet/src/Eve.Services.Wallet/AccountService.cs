using Eve.Macho;
using Eve.Marshal;
using Eve.Wallet;

namespace Eve.Services.Wallet;

/// <summary>Personal and role-gated corporate balances (<c>account</c>), always encoded as money reals.</summary>
[Service("account")]
public sealed partial class AccountService(PgWalletStore wallets, Eve.Corporations.CorporationStore? corporations = null, WalletTransactions? transactions = null)
{
    /// <summary><c>GetCashBalance(corporation=0)</c>: the ISK the character has, as a real number.</summary>
    /// <summary>Lists wallet divisions the persisted corporation membership permits the character to read.</summary>
    /// <summary>Returns the caller's recent or month-filtered wallet journal transactions.</summary>
    // The matching SDE omits this table; GetEntryTypes uses the embedded V24 client metadata snapshot below.

    // Build 3396210's native accountingEntryTypes resource, including the historical types the client can still
    // encounter in persisted journals. The embedded snapshot records the source resource hashes.
    private sealed record EntryTypeMetadataRow(int Id, string Name, int? NameMessageId, int? JournalMessageId);

    private static readonly EntryTypeMetadataRow[] EntryTypeMetadata = LoadEntryTypeMetadata();

    private static EntryTypeMetadataRow[] LoadEntryTypeMetadata()
    {
        using var stream = typeof(AccountService).Assembly.GetManifestResourceStream(
            "Eve.Services.Wallet.accountingEntryTypes.json")
            ?? throw new InvalidOperationException("The V24 accounting entry type metadata resource is missing.");
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        if (document.RootElement.GetProperty("source").GetProperty("build").GetInt32() != 3_396_210)
            throw new InvalidOperationException("Accounting entry type metadata must match V24 client build 3396210.");
        return document.RootElement.GetProperty("entries").EnumerateArray().Select(row =>
        {
            int? ReadOptionalInt(string name) => row.GetProperty(name).ValueKind is System.Text.Json.JsonValueKind.Null
                ? null
                : row.GetProperty(name).GetInt32();

            return new EntryTypeMetadataRow(
                row.GetProperty("entryTypeID").GetInt32(),
                row.GetProperty("entryTypeName").GetString()
                    ?? throw new InvalidOperationException("Accounting entry type name is null."),
                ReadOptionalInt("entryTypeNameID"),
                ReadOptionalInt("entryJournalMessageID"));
        }).ToArray();
    }

    [Method("GetEntryTypes")]
    public ValueTask<PyValue> GetEntryTypes(ServiceCall call)
    {
        var rows = new PyList(EntryTypeMetadata.Select(entry => Py.KeyVal([
            new KeyValuePair<PyValue, PyValue>(new PyStr("entryTypeID"), new PyInt(entry.Id)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("entryTypeNameID"),
                entry.NameMessageId is { } nameId ? new PyInt(nameId) : PyNone.Instance),
            new KeyValuePair<PyValue, PyValue>(new PyStr("entryTypeName"), new PyStr(entry.Name)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("entryJournalMessageID"),
                entry.JournalMessageId is { } journalId ? new PyInt(journalId) : PyNone.Instance),
        ])).ToArray());
        return ValueTask.FromResult(Eve.Services.ObjectCaching.MethodResult(
            rows, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    [Method("GetTransactions")]
    public async ValueTask<PyValue> GetTransactions(ServiceCall call)
    {
        if (transactions is not { } walletTransactions)
            throw new InvalidOperationException("Account transaction storage is not configured.");
        if (call.Session.CharacterId is not { } characterId)
            return new PyList([]);

        var accountKey = WalletKey(call.Kwarg("accountKey") ?? call.Arg(0));
        var ownerId = characterId;
        var isCorpWallet = JsSemantics.Truthy(call.Kwarg("isCorpWallet") ?? call.Arg(3));
        if (isCorpWallet)
        {
            if (corporations is not { } corporationStore)
                throw CorporationWalletAccessDenied();

            try
            {
                var account = await corporationStore.WalletAccountAsync(characterId, accountKey, false, call.Cancellation);
                ownerId = account.OwnerId;
            }
            catch (Eve.Corporations.CorporationError)
            {
                throw CorporationWalletAccessDenied();
            }
        }

        var year = Integral(call.Arg(1));
        var month = Integral(call.Arg(2));
        var filterByMonth = year > 0 && month is >= 1 and <= 12;
        if (filterByMonth && year > 9999)
            return new PyList([]);
        long? monthStartMs = null;
        long? nextMonthStartMs = null;
        if (filterByMonth)
        {
            var monthStart = new DateTimeOffset((int)year, (int)month, 1, 0, 0, 0, TimeSpan.Zero);
            monthStartMs = monthStart.ToUnixTimeMilliseconds();
            nextMonthStartMs = year == 9999 && month == 12
                ? long.MaxValue
                : monthStart.AddMonths(1).ToUnixTimeMilliseconds();
        }
        var recentAfter = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        var entries = await walletTransactions.JournalForAccountAsync(ownerId, accountKey,
            limit: null, cancellationToken: call.Cancellation,
            fromTimeMs: filterByMonth ? monthStartMs : recentAfter, untilTimeMs: nextMonthStartMs);
        var rows = entries
            .Select(entry => WalletTransactionInfo(entry, isCorpWallet))
            .ToArray();
        return new PyList(rows);
    }

    private static PyValue WalletTransactionInfo(Eve.Wallet.JournalEntry entry, bool isCorpWallet)
    {
        var transactionDate = DateTimeOffset.FromUnixTimeMilliseconds(entry.TimeMs).UtcDateTime.ToFileTimeUtc();
        var ownerId1 = entry.OwnerId;
        var ownerId2 = entry.CounterpartyId;
        if (isCorpWallet && (entry.RefTypeId is 96 or 97) && ownerId2 > 0)
        {
            ownerId1 = entry.CounterpartyId;
            ownerId2 = entry.OwnerId;
        }
        return Py.KeyVal([
            new KeyValuePair<PyValue, PyValue>(new PyStr("transactionID"), new PyLong(entry.Id)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("transactionDate"), new PyLong(transactionDate)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("referenceID"), new PyLong(entry.ReferenceId ?? 0)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("entryTypeID"), new PyInt(entry.RefTypeId)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("ownerID1"), new PyLong(ownerId1)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("ownerID2"), new PyLong(ownerId2)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("accountKey"), new PyInt(entry.AccountKey)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("amount"), new PyReal((double)entry.Amount)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("balance"), new PyReal((double)entry.Balance)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("description"), new PyStr(entry.Reason)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("currency"), new PyInt(1)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("sortValue"), new PyInt(1)),
        ]);
    }

    [Method("GetWalletDivisionsInfo")]
    public async ValueTask<PyValue> GetWalletDivisionsInfo(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || corporations is not { } corporationStore ||
            transactions is not { } walletTransactions)
            return PyNone.Instance;

        var divisions = new List<PyValue>();
        if (call.Session.GetInt("corpid") is { } corporationId && corporationId > 0)
        {
            for (var accountKey = 1000; accountKey <= 1006; accountKey++)
            {
                if (!await corporationStore.CanReadWalletDivisionAsync(characterId, accountKey, call.Cancellation))
                    continue;

                var account = await corporationStore.WalletAccountAsync(characterId, accountKey, false, call.Cancellation);
                var balance = await walletTransactions.AccountBalanceAsync(account, call.Cancellation) ?? 0m;
                divisions.Add(WalletDivisionInfo(accountKey, balance));
            }

            return new PyList(divisions);
        }

        var personal = await wallets.GetAsync(characterId, call.Cancellation);
        if (personal is not null)
            divisions.Add(WalletDivisionInfo(1000, personal.Isk));
        return new PyList(divisions);
    }

    private static PyValue WalletDivisionInfo(int accountKey, decimal balance) => Py.KeyVal([
        new KeyValuePair<PyValue, PyValue>(new PyStr("key"), new PyInt(accountKey)),
        new KeyValuePair<PyValue, PyValue>(new PyStr("balance"), new PyReal((double)balance)),
    ]);

    [Method("GetCashBalance")]
    public async ValueTask<PyValue> GetCashBalance(ServiceCall call)
    {
        if (JsSemantics.Truthy(call.Kwarg("isCorpWallet") ?? PyNone.Instance) || JsSemantics.Truthy(call.Arg(0)))
        {
            if (call.Session.CharacterId is { } actor && corporations is not null && transactions is not null)
            {
                try
                {
                    var raw = call.Kwarg("accountKey");
                    if (raw is null or PyNone)
                        raw = call.Arg(1);
                    if (raw is null or PyNone)
                    {
                        var selectedKey = call.Session.GetInt("corpAccountKey") ?? call.Session.GetInt("corpaccountkey");
                        raw = selectedKey is { } value ? new PyLong(value) : PyNone.Instance;
                    }
                    var key = WalletKey(raw);
                    var account = await corporations.WalletAccountAsync(actor,key,false,call.Cancellation);
                    return new PyReal((double)(await transactions.AccountBalanceAsync(account,call.Cancellation) ?? 0m));
                }
                catch (Eve.Corporations.CorporationError) { }
            }
            throw MachoErrors.ThrowUserError(
                "CrpAccessDenied",
                ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))));
        }
        if (call.Session.CharacterId is not { } id)
            return PyNone.Instance;
        var personalBalance = await wallets.GetAsync(id, call.Cancellation);
        return new PyReal((double)(personalBalance?.Isk ?? 0m));
    }

    /// <summary>Transfer ISK from the active character wallet to a character or corporation.</summary>
    [Method("GiveCash")]
    public async ValueTask<PyValue> GiveCash(ServiceCall call)
    {
        var actorId = call.Session.CharacterId.GetValueOrDefault();
        var recipientId = Integral(call.Arg(0));
        var amount = Money(call.Arg(1));
        if (actorId <= 0 || recipientId <= 0 || amount <= 0 || transactions is not { } walletTransactions)
            return PyNone.Instance;

        var recipientCorporation = corporations is null
            ? null
            : await corporations.GetAsync(recipientId, call.Cancellation);
        var from = new WalletAccount(actorId);
        var to = new WalletAccount(recipientId,
            recipientCorporation is null ? 1000 : WalletKey(call.Kwarg("toAccountKey") ?? PyNone.Instance));
        if (from == to) return PyNone.Instance;

        var reason = Reason(call, 2, $"Player donation to {recipientId}");
        try
        {
            var settlement = await walletTransactions.TransferAndCommitAsync(
                from, to, amount, 10, reason, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                OperationKey(call, "GiveCash"), "player_donation", call.Cancellation);
            Announce(walletTransactions, settlement);
            // The client reads truthiness to finish a personal gift and ignores the transfer window's result.
            // Never return the corporation's post-transfer balance to an unrelated donor.
            return recipientCorporation is null ? PyBool.True : PyNone.Instance;
        }
        catch (InsufficientFundsException error)
        {
            throw MachoErrors.ThrowUserError("NotEnoughMoney",
                ("balance", new PyReal((double)(error.AvailableBalance ?? 0m))),
                ("amount", new PyReal((double)amount)));
        }
        catch (MoneyLimitException)
        {
            throw MoneyLimitError();
        }
        catch (Exception error) when (error is WalletNotFoundException or WalletOperationConflictException)
        {
            throw MachoErrors.ThrowUserError("CustomInfo",
                ("info", new PyStr("This transfer could not be completed. Please try again.")));
        }
    }

    /// <summary>Transfer ISK out of the caller's canonically authorized corporation division.</summary>
    [Method("GiveCashFromCorpAccount")]
    public async ValueTask<PyValue> GiveCashFromCorpAccount(ServiceCall call)
    {
        var actorId = call.Session.CharacterId.GetValueOrDefault();
        var recipientId = Integral(call.Arg(0));
        var amount = Money(call.Arg(1));
        if (actorId <= 0 || recipientId <= 0 || amount <= 0 || corporations is not { } corporationStore ||
            transactions is not { } walletTransactions)
            return PyNone.Instance;

        var sourceKeyValue = call.Arg(2);
        if (sourceKeyValue is PyNone)
        {
            var sessionKey = call.Session.GetInt("corpAccountKey") ?? call.Session.GetInt("corpaccountkey");
            sourceKeyValue = sessionKey is { } key ? new PyLong(key) : PyNone.Instance;
        }
        WalletAccount from;
        try
        {
            from = await corporationStore.WalletAccountAsync(actorId, WalletKey(sourceKeyValue), true, call.Cancellation);
        }
        catch (Eve.Corporations.CorporationError)
        {
            throw MachoErrors.ThrowUserError("CrpAccessDenied",
                ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))));
        }

        var recipientCorporation = await corporationStore.GetAsync(recipientId, call.Cancellation);
        var to = new WalletAccount(recipientId,
            recipientCorporation is null ? 1000 : WalletKey(call.Kwarg("toAccountKey") ?? PyNone.Instance));
        if (from == to)
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(
                "That would move ISK from a corporation wallet division into itself. Choose a different destination division.")));

        var reason = Reason(call, 3, $"Corporation wallet transfer to {recipientId}");
        try
        {
            var settlement = await walletTransactions.TransferAndCommitAsync(
                from, to, amount, 10, reason, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                OperationKey(call, "GiveCashFromCorpAccount"), "player_donation", call.Cancellation);
            Announce(walletTransactions, settlement);
            return PyNone.Instance;
        }
        catch (InsufficientFundsException error)
        {
            throw MachoErrors.ThrowUserError("NotEnoughMoney",
                ("balance", new PyReal((double)(error.AvailableBalance ?? 0m))),
                ("amount", new PyReal((double)amount)));
        }
        catch (MoneyLimitException)
        {
            throw MoneyLimitError();
        }
        catch (Exception error) when (error is WalletNotFoundException or WalletOperationConflictException)
        {
            throw MachoErrors.ThrowUserError("CustomInfo",
                ("info", new PyStr("This transfer could not be completed. Please try again.")));
        }
    }

    private static long Integral(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) && Math.Truncate(real.Value) == real.Value &&
                         real.Value >= long.MinValue && real.Value <= long.MaxValue => (long)real.Value,
        PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    private static decimal Money(PyValue value)
    {
        if (value is PyInt integer) return integer.Value;
        if (value is PyLong longInteger) return longInteger.Value;
        var text = value switch
        {
            PyReal real when double.IsFinite(real.Value) => real.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            var stringValue when Eve.Macho.JsSemantics.StringValue(stringValue) is not null => Eve.Macho.JsSemantics.StringValue(stringValue)!,
            PyBool boolean => boolean.Value ? "1" : "0",
            _ => string.Empty,
        };
        return decimal.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? decimal.Round(amount, 2, MidpointRounding.AwayFromZero)
            : 0m;
    }

    private static Exception CorporationWalletAccessDenied() => MachoErrors.ThrowUserError(
        "CrpAccessDenied",
        ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))));

    private static int WalletKey(PyValue value)
    {
        if (value is PyStr text && text.Value == "cash") return 1000;
        if (value is PyStr division && division.Value.Length == 5 && division.Value.StartsWith("cash", StringComparison.Ordinal) &&
            division.Value[4] is >= '2' and <= '7') return 999 + division.Value[4] - '0';
        var key = Integral(value);
        return key is >= 1000 and <= 1006 ? (int)key : 1000;
    }

    private static string Reason(ServiceCall call, int positionalIndex, string fallback)
    {
        var value = call.Kwarg("reason");
        if (value is null or PyNone) value = call.Arg(positionalIndex);
        var text = value switch
        {
            var stringValue when Eve.Macho.JsSemantics.StringValue(stringValue) is not null => Eve.Macho.JsSemantics.StringValue(stringValue)!,
            PyNone => string.Empty,
            _ => JsSemantics.PlainText(value),
        };
        if (text is { Length: > 40 })
            throw MachoErrors.ThrowUserError("WALLET_REASON_LIMIT_EXCEEDED");
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string OperationKey(ServiceCall call, string method) =>
        $"rpc:{call.Session.SessionId}:{Integral(call.CallId)}:{call.Session.CharacterId}:{method}";

    private static decimal Balance(WalletSettlement settlement, WalletAccount account) =>
        settlement.Entries.Single(entry => entry.OwnerId == account.OwnerId && entry.AccountKey == account.AccountKey).Balance;

    private static void Announce(WalletTransactions walletTransactions, WalletSettlement settlement)
    {
        foreach (var entry in settlement.Entries)
            walletTransactions.AnnounceCommitted(new WalletBalanceChange(entry.OwnerId, entry.AccountKey, entry.Balance));
    }

    private static global::Eve.Services.MachoWrappedException MoneyLimitError() =>
        global::Eve.Services.MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(
            $"That transfer was refused: the receiving wallet cannot hold more than {WalletLimits.MaximumIsk.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"))} ISK.")));
}
