using Eve.Macho;
using Eve.Marshal;
using Eve.Wallet;

namespace Eve.Services.Wallet;

/// <summary>Personal and role-gated corporate balances (<c>account</c>), always encoded as money reals.</summary>
[Service("account")]
public sealed partial class AccountService(PgWalletStore wallets, Eve.Corporations.CorporationStore? corporations = null, WalletTransactions? transactions = null)
{
    /// <summary><c>GetCashBalance(corporation=0)</c>: the ISK the character has, as a real number.</summary>
    [Method("GetCashBalance")]
    public async ValueTask<PyValue> GetCashBalance(ServiceCall call)
    {
        if (JsSemantics.Truthy(call.Kwarg("isCorpWallet") ?? PyNone.Instance) || JsSemantics.Truthy(call.Arg(0)))
        {
            if (call.Session.CharacterId is { } actor && corporations is not null && transactions is not null)
            {
                try
                {
                    var raw = call.Kwarg("accountKey") ?? call.Arg(1);
                    var key = raw switch
                    {
                        PyInt n when n.Value is >= 1000 and <= 1006 => (int)n.Value,
                        PyStr { Value:"cash" } => 1000,
                        PyStr s when s.Value.Length == 5 && s.Value.StartsWith("cash",StringComparison.Ordinal) && s.Value[4] is >= '2' and <= '7' => 999 + s.Value[4] - '0',
                        _ => 1000,
                    };
                    var account = await corporations.WalletAccountAsync(actor,key,false,call.Cancellation);
                    return new PyReal((double)(await transactions.AccountBalanceAsync(account,call.Cancellation) ?? 0m));
                }
                catch (Eve.Corporations.CorporationError) { }
            }
            throw MachoErrors.ThrowUserError(
                "CrpAccessDenied",
                ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))));
        }
        return call.Session.CharacterId is { } id && await wallets.GetAsync(id, call.Cancellation) is { } balance
            ? new PyReal((double)balance.Isk)
            : PyNone.Instance;
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
            return recipientCorporation is null
                ? PyNone.Instance
                : new PyList([new PyReal((double)Balance(settlement, from)), new PyReal((double)Balance(settlement, to))]);
        }
        catch (MoneyLimitException)
        {
            throw MoneyLimitError();
        }
        catch (Exception error) when (error is InsufficientFundsException or WalletNotFoundException or WalletOperationConflictException)
        {
            return PyNone.Instance;
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
        catch (MoneyLimitException)
        {
            throw MoneyLimitError();
        }
        catch (Exception error) when (error is InsufficientFundsException or WalletNotFoundException or WalletOperationConflictException)
        {
            return PyNone.Instance;
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
            PyStr stringValue => stringValue.Value,
            PyBool boolean => boolean.Value ? "1" : "0",
            _ => string.Empty,
        };
        return decimal.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? decimal.Round(amount, 2, MidpointRounding.AwayFromZero)
            : 0m;
    }

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
            PyStr stringValue => stringValue.Value,
            PyNone => string.Empty,
            _ => JsSemantics.PlainText(value),
        };
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
