using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;

namespace Eve.Services.Wallet;

/// <summary>
/// <c>LPSvc</c>: reads and transfers the character's and corporation's loyalty point balances. Transfers validate the
/// destination and corporation authority, then debit and credit the two wallets in one transaction.
/// </summary>
public sealed class LoyaltyPointsService(
    LoyaltyPointStore store,
    Func<long, CancellationToken, Task<long?>>? corporationOfCharacter = null,
    Func<LoyaltyHolder, CancellationToken, Task<IReadOnlyList<LoyaltyPointBalance>>>? readBalances = null,
    Func<long, long, CancellationToken, ValueTask<bool>>? isDirector = null,
    Func<long, CancellationToken, ValueTask<bool>>? isEligibleDestination = null,
    Func<LoyaltyPointTransferRequest, CancellationToken, Task>? transfer = null) : IService
{
    private static readonly string[] BalanceColumns = ["issuerCorpID", "loyaltyPoints"];
    private const long EvermarkIssuerCorporationId = 1_000_419; // V24.01 appConst.corpHeraldry

    private const string ExchangeNotAvailable = "This loyalty point operation is not available on this server yet.";

    public string Name => "LPSvc";

    public bool HasMethod(string method) => method is
        "GetAllMyCharacterWalletLPBalances" or
        "GetAllMyCorporationWalletLPBalances" or
        "ExchangeConcordLP" or
        "TransferLPFromMyWalletToOtherCorp" or
        "TransferLPFromMyCorpWalletToOtherCorp";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetAllMyCharacterWalletLPBalances" => CharacterBalancesAsync(call),
        "GetAllMyCorporationWalletLPBalances" => CorporationBalancesAsync(call),
        "ExchangeConcordLP" => throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(ExchangeNotAvailable))),
        "TransferLPFromMyWalletToOtherCorp" => TransferAsync(call, corporationWallet: false),
        "TransferLPFromMyCorpWalletToOtherCorp" => TransferAsync(call, corporationWallet: true),
        _ => throw new InvalidOperationException($"LPSvc has no method {call.Method}"),
    };

    private async ValueTask<PyValue> TransferAsync(ServiceCall call, bool corporationWallet)
    {
        var characterId = call.Session.CharacterId ?? Positive(call.Session.Get("charid"));
        var destinationCorporationId = Positive(call.Arg(0));
        var issuerCorporationId = Positive(call.Arg(1));
        var amount = Positive(call.Arg(2));
        if (characterId <= 0 || destinationCorporationId <= 0 || issuerCorporationId <= 0 || amount <= 0)
            throw TransferRefusal("INVALID_TRANSFER");
        if (issuerCorporationId == EvermarkIssuerCorporationId)
            throw TransferRefusal("DESTINATION_NOT_ALLOWED");

        LoyaltyHolder source;
        if (corporationWallet)
        {
            var sourceCorporationId = corporationOfCharacter is null
                ? 0
                : await corporationOfCharacter(characterId, call.Cancellation) ?? 0;
            if (sourceCorporationId <= 0)
                throw TransferRefusal("ACCESS_DENIED");
            if (isDirector is null || !await isDirector(characterId, sourceCorporationId, call.Cancellation))
                throw TransferRefusal("ACCESS_DENIED");
            if (sourceCorporationId == destinationCorporationId)
                throw TransferRefusal("DESTINATION_NOT_ALLOWED");
            source = new LoyaltyHolder(LoyaltyHolderKind.Corporation, sourceCorporationId);
        }
        else
        {
            source = new LoyaltyHolder(LoyaltyHolderKind.Character, characterId);
        }

        if (isEligibleDestination is null || !await isEligibleDestination(destinationCorporationId, call.Cancellation))
            throw TransferRefusal("DESTINATION_CORPORATION_INVALID");

        var destination = new LoyaltyHolder(LoyaltyHolderKind.Corporation, destinationCorporationId);
        var reference = call.Session.SessionId > 0 && Positive(call.CallId) > 0
            ? $"lpsvc-transfer:{call.Session.SessionId}:{Positive(call.CallId)}"
            : $"lpsvc-transfer:{Guid.NewGuid():N}";
        try
        {
            var request = new LoyaltyPointTransferRequest(source, destination, issuerCorporationId, amount, reference);
            if (transfer is null)
                await store.TransferAndCommitAsync(request, call.Cancellation);
            else
                await transfer(request, call.Cancellation);
        }
        catch (InsufficientLoyaltyPointsException)
        {
            throw TransferRefusal("INSUFFICIENT_FUNDS");
        }

        return PyNone.Instance;
    }

    private static Exception TransferRefusal(string code)
    {
        var message = code switch
        {
            "INSUFFICIENT_FUNDS" => "Loyalty point transfer failed: insufficient balance.",
            "ACCESS_DENIED" => "Loyalty point transfer failed: insufficient corporation roles.",
            "DESTINATION_NOT_ALLOWED" => "That destination corporation is not valid for this loyalty point transfer.",
            "DESTINATION_CORPORATION_INVALID" or "DESTINATION_CORPORATION_EMPTY" => "Loyalty point transfer failed: destination corporation is not eligible.",
            "INVALID_TRANSFER" => "Loyalty point transfer failed: invalid transfer arguments.",
            _ => "Loyalty point transfer failed.",
        };
        return MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
    }

    private async ValueTask<PyValue> CharacterBalancesAsync(ServiceCall call)
    {
        var characterId = Positive(call.Session.Get("charid"));
        var holder = new LoyaltyHolder(LoyaltyHolderKind.Character, characterId);
        return await BalanceRowsAsync(holder, call.Cancellation);
    }

    private async ValueTask<PyValue> CorporationBalancesAsync(ServiceCall call)
    {
        var characterId = call.Session.CharacterId ?? Positive(call.Session.Get("charid"));
        var corporationId = characterId > 0 && corporationOfCharacter is not null
            ? await corporationOfCharacter(characterId, call.Cancellation) ?? 0
            : 0;
        if (characterId <= 0 || corporationId <= 0 || isDirector is null ||
            !await isDirector(characterId, corporationId, call.Cancellation))
            return Py.Rowset(BalanceColumns, Array.Empty<PyValue>());
        var holder = new LoyaltyHolder(LoyaltyHolderKind.Corporation, corporationId);
        return await BalanceRowsAsync(holder, call.Cancellation);
    }

    private async ValueTask<PyValue> BalanceRowsAsync(LoyaltyHolder holder, CancellationToken cancellationToken)
    {
        var balances = readBalances is null
            ? await store.ReadAsync(holder, cancellationToken)
            : await readBalances(holder, cancellationToken);
        return Py.Rowset(BalanceColumns, balances.Select(balance => new PyTuple(
        [
            new PyInt(balance.IssuerCorpId),
            new PyInt(balance.Amount),
        ])));
    }

    private static long Positive(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };
}

public sealed class LoyaltyPointAnnouncer : IDisposable
{
    private readonly LoyaltyPointStore _store;
    private readonly CharacterSessions _sessions;

    public LoyaltyPointAnnouncer(LoyaltyPointStore store, CharacterSessions sessions)
    {
        _store = store;
        _sessions = sessions;
        store.BalanceCommitted += Changed;
    }

    private void Changed(LoyaltyPointChange change)
    {
        if (change.Holder.Kind == LoyaltyHolderKind.Character)
        {
            _sessions.Of(change.Holder.Id)?.SendNotification(
                "OnLPChange",
                "clientID",
                new PyStr("set"),
                new PyInt(change.IssuerCorpId),
                new PyInt(change.Before),
                new PyInt(change.After));
            return;
        }

        foreach (var session in _sessions.OfCorporation(change.Holder.Id))
        {
            session.SendNotification("OnCorpLPChange", "clientID", new PyStr(change.Reason), new PyInt(change.IssuerCorpId));
        }
    }

    public void Dispose() => _store.BalanceCommitted -= Changed;
}
