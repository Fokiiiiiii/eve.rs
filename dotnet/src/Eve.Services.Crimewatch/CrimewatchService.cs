using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Crimewatch;

/// <summary>
/// The crimewatch service the client binds for the place it is in (<c>crimewatch</c>, bound by <c>(locationID, groupID)</c> like dogma). The client asks it for everything it shows of
/// crimewatch when it enters a place (<c>GetClientStates</c>: the five combat timers, its engagements, who is flagged, its ship safety level) and keeps the rest from the events it is
/// sent. The server keeps one bound object per session and hands it again on every bind (as the reference does). Security status is the character's <c>security_status</c> relation
/// (<c>Eve.Relations</c>); the service takes it as a function so that it knows nothing of where it is kept.
/// </summary>
[Service("crimewatch")]
public sealed partial class CrimewatchService(
    PgCrimewatchStore store, CrimewatchRuntime runtime, Func<long> nowUnixMs,
    Func<long, CancellationToken, Task<double>>? securityStatusOf = null) : IBoundObjectReleaseHandler
{
    private readonly ConcurrentDictionary<long, string> _boundOfSession = new();

    private static PyValue None => PyNone.Instance;

    private static readonly Descriptor SecurityStatusTransactionDescriptor = Descriptor.FromHeader(new PyObjectEx(
        ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(
            Py.Tuple(
                Py.Tuple(new PyStr("eventTypeID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("eventDate"), new PyInt(0x40)),
                Py.Tuple(new PyStr("otherOwnerID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("otherTypeID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("locationID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("modification"), new PyInt(0x06)),
                Py.Tuple(new PyStr("newValue"), new PyInt(0x06))))),
        [], []));

    public void OnBoundObjectReleased(string oid, ClientSession? session)
    {
        if (session is not null)
        {
            _boundOfSession.TryRemove(new KeyValuePair<long, string>(session.SessionId, oid));
        }
    }

    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    private PyValue Bind(ClientSession session)
    {
        var oid = _boundOfSession.GetOrAdd(session.SessionId, _ =>
        {
            session.Closed += () => _boundOfSession.TryRemove(session.SessionId, out string? _);
            return $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        });
        return new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())))));
    }

    /// <summary><c>MachoBindObject((locationID, groupID), (method, args, kwargs))</c>: the bound object of the session, and the answer of the nested call.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var bound = Bind(call.Session);
        var oid = JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)bound).Value).Decoded!).Items[0]);
        PyValue result = None;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = nested.Items.Count > 1 && nested.Items[1] is PyTuple or PyList ? (nested.Items[1] is PyTuple a ? a.Items : ((PyList)nested.Items[1]).Items) : [],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : None,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    private static long? Number(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => (long)r.Value, _ => null };

    /// <summary>
    /// <c>GetClientStates()</c>: <c>((weapons, pvp, npc, criminal, disapproval), engagements, (criminals, suspects), safetyLevel)</c>. Each timer is <c>(state, expiry)</c>, idle with no
    /// expiry while it does not run; the engagements are a dictionary of the other character to the FILETIME when it ends; the flagged are two sets of the character ids flagged in the
    /// system the pilot is in.
    /// </summary>
    [Method("GetClientStates")]
    public async ValueTask<PyValue> GetClientStates(ServiceCall call)
    {
        var now = nowUnixMs();
        var character = call.Session.CharacterId;
        var safety = character is { } id ? await store.SafetyOfAsync(id, call.Cancellation) : SafetyLevel.Full;
        var timers = (character is { } own ? await store.StateOfAsync(own, call.Cancellation) : CombatState.Idle).TimersAt(now);
        var system = call.Session.GetInt("solarsystemid") ?? call.Session.GetInt("solarsystemid2") ?? 0;
        var (criminals, suspects) = system > 0 ? await store.FlaggedInAsync(system, now, call.Cancellation) : ([], []);
        var engagements = character is { } pilot
            ? await runtime.LimitedEngagementsOfAsync(pilot, now, call.Cancellation)
            : new Dictionary<long, long>();
        var engagementWire = new PyDict([.. engagements.OrderBy(entry => entry.Key).Select(entry =>
            new KeyValuePair<PyValue, PyValue>(
                InventoryWire.Id(entry.Key),
                new PyLong(Py.FileTimeFromUnixMs(entry.Value))))]);
        static PyValue Timer(TimerView timer) => Py.Tuple(new PyInt(timer.State), CrimewatchRuntime.Expiry(timer));
        return Py.Tuple(
            Py.Tuple(Timer(timers.Weapons), Timer(timers.Pvp), Timer(timers.Npc), Timer(timers.Criminal), Timer(timers.Disapproval)),
            engagementWire,
            Py.Tuple(InventoryWire.Set(criminals.Select(InventoryWire.Id)), InventoryWire.Set(suspects.Select(InventoryWire.Id))),
            new PyInt(safety));
    }

    /// <summary><c>GetSafetyLevel()</c>: 0 (none), 1 (partial) or 2 (full).</summary>
    [Method("GetSafetyLevel")]
    public async ValueTask<PyValue> GetSafetyLevel(ServiceCall call) =>
        new PyInt(call.Session.CharacterId is { } character ? await store.SafetyOfAsync(character, call.Cancellation) : SafetyLevel.Full);

    /// <summary><c>SetSafetyLevel(level)</c>: keeps the level (brought to 0, 1 or 2) and answers with what was kept.</summary>
    [Method("SetSafetyLevel")]
    public async ValueTask<PyValue> SetSafetyLevel(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character)
        {
            return new PyInt(SafetyLevel.Full);
        }
        double? requested = call.Arg(0) switch { PyReal real => real.Value, var other => Number(other) };
        return new PyInt(await store.SetSafetyAsync(character, requested, nowUnixMs(), call.Cancellation));
    }

    private async ValueTask<PyValue> SecurityStatus(long? character, CancellationToken cancellationToken) =>
        Py.Number(character is { } id && securityStatusOf is not null ? await securityStatusOf(id, cancellationToken) : 0);

    /// <summary><c>GetMySecurityStatus()</c>: the character's security status.</summary>
    [Method("GetMySecurityStatus")]
    public ValueTask<PyValue> GetMySecurityStatus(ServiceCall call) => SecurityStatus(call.Session.CharacterId, call.Cancellation);

    /// <summary><c>GetCharacterSecurityStatus(characterID)</c>: that character's security status (the caller's own when none is named).</summary>
    [Method("GetCharacterSecurityStatus")]
    public ValueTask<PyValue> GetCharacterSecurityStatus(ServiceCall call) => SecurityStatus(Number(call.Arg(0)) is { } named and > 0 ? named : call.Session.CharacterId, call.Cancellation);

    /// <summary><c>GetSecurityStatusTransactions()</c>: the character's persisted security_status relation history as the client's CRowset.</summary>
    /// <summary>Consumes a duel response; an accepted, valid challenge creates the pair LE in the shared crimewatchRuntime store.</summary>
    /// <summary>Requests a Host-validated duel challenge; the Host owns pending state, expiry, and invite delivery.</summary>
    [Method("StartDuelChallenge")]
    public async ValueTask<PyValue> StartDuelChallenge(ServiceCall call)
    {
        if (call.Session.CharacterId is { } challengerCharacterId &&
            Number(call.Arg(0)) is { } targetCharacterId &&
            targetCharacterId > 0 &&
            targetCharacterId != challengerCharacterId)
        {
            await runtime.StartDuelChallengeAsync(
                challengerCharacterId,
                targetCharacterId,
                call.Session.GetInt("corpid") ?? 0,
                call.Session.GetInt("allianceid") ?? 0,
                call.Cancellation);
        }
        return None;
    }

    [Method("RespondToDuelChallenge")]
    public async ValueTask<PyValue> RespondToDuelChallenge(ServiceCall call)
    {
        if (call.Session.CharacterId is { } responderCharacterId &&
            Number(call.Arg(0)) is { } challengerCharacterId &&
            Number(call.Arg(1)) is { } challengeExpiresAtFileTime)
        {
            await runtime.RespondToDuelChallengeAsync(
                responderCharacterId,
                challengerCharacterId,
                challengeExpiresAtFileTime,
                JsSemantics.Truthy(call.Arg(2)),
                call.Cancellation);
        }
        return None;
    }

    private static PyValue SecurityStatusTransactionRow(SecurityStatusTransaction transaction) =>
        new PyPackedRow(SecurityStatusTransactionDescriptor, [
            transaction.EventTypeID is { } eventTypeId ? new PyInt(eventTypeId) : PyNone.Instance,
            new PyLong(Py.FileTimeFromUnixMs(transaction.EventDateUnixMs)),
            transaction.OtherOwnerID is { } ownerId ? InventoryWire.Id(ownerId) : PyNone.Instance,
            transaction.OtherTypeID is { } typeId ? new PyInt(typeId) : PyNone.Instance,
            transaction.LocationID is { } locationId ? InventoryWire.Id(locationId) : PyNone.Instance,
            transaction.Modification is { } modification ? Py.Number(modification) : PyNone.Instance,
            transaction.NewValue is { } newValue ? Py.Number(newValue) : PyNone.Instance,
        ]);

    [Method("GetSecurityStatusTransactions")]
    public async ValueTask<PyValue> GetSecurityStatusTransactions(ServiceCall call)
    {
        var transactions = call.Session.CharacterId is { } characterId
            ? await runtime.SecurityStatusTransactionsOfAsync(characterId, call.Cancellation)
            : [];
        var rows = transactions.Select(SecurityStatusTransactionRow).ToArray();
        return new PyObjectEx(
            ObjectExKind.Ex2,
            Py.Tuple(
                Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CRowset")),
                Py.Dict(("header", SecurityStatusTransactionDescriptor.Header))),
            rows,
            []);
    }

    /// <summary><c>GetKillRights(toIDs)</c>: the caller's active kill rights against the requested characters.</summary>
}

[Service("killRightMgr")]
public sealed partial class KillRightMgrService(
    PgCrimewatchStore store,
    CrimewatchRuntime? runtime = null,
    Eve.Wallet.WalletTransactions? walletTransactions = null,
    Eve.Wallet.PgWalletStore? walletStore = null,
    Func<long>? nowUnixMs = null,
    Action<string>? log = null)
{
    public Func<KillRightActivation, CancellationToken, Task>? KillRightUsedNotification { get; set; }

    /// <summary>
    /// <c>GetKillRights(toIDs)</c>: active rights owned by the caller for the requested targets.
    /// Client 1dbb5711a072a146e1d228365668ac05655eefe1 caches records by <c>toID</c>
    /// and reads <c>killRightID</c> and <c>restrictedTo</c>.
    /// </summary>
    [Method("GetKillRights")]
    public async ValueTask<PyValue> GetKillRights(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } ownerCharacterId || ownerCharacterId <= 0)
        {
            return new PyList([]);
        }

        var targetIds = call.Arg(0) switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => [],
        };
        var requestedTargets = targetIds
            .Select(Number)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .ToHashSet();
        if (requestedTargets.Count == 0)
        {
            return new PyList([]);
        }

        var rights = await store.KillRightsOfAsync(ownerCharacterId, call.Cancellation);
        return new PyList([.. rights
            .Where(right => requestedTargets.Contains(right.TargetCharacterId))
            .OrderBy(right => right.TargetCharacterId)
            .ThenBy(right => right.KillRightId)
            .Select(right => Py.KeyVal(
                ("killRightID", new PyLong(right.KillRightId)),
                ("fromID", InventoryWire.Id(right.OwnerCharacterId)),
                ("toID", InventoryWire.Id(right.TargetCharacterId)),
                ("restrictedTo", right.SalePrice is null
                    ? InventoryWire.Id(right.OwnerCharacterId)
                    : right.RestrictedToOwnerId is { } restrictedTo
                        ? InventoryWire.Id(restrictedTo)
                        : PyNone.Instance),
                ("price", right.SalePrice is { } price
                    ? new PyReal((double)price)
                    : PyNone.Instance),
                ("solarSystemID", InventoryWire.Id(right.SolarSystemId))))]);
    }

    [Method("ActivateKillRight")]
    public async ValueTask<PyValue> ActivateKillRight(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } activatorCharacterId || activatorCharacterId <= 0
            || Number(call.Arg(0)) is not { } killRightId || killRightId <= 0)
        {
            throw Eve.Services.MachoErrors.ThrowUserError("NoValidKillRight");
        }
        if (runtime is null)
        {
            throw Eve.Services.MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyStr("Unable to activate kill right.")));
        }

        var result = await store.ActivateKillRightAsync(
            killRightId, activatorCharacterId, NowUnixMs, call.Cancellation);
        if (!result.Success)
        {
            return await ErrorAsync(result.ErrorKey, activatorCharacterId, result.RequiredAmount, call.Cancellation);
        }

        await runtime.ApplyKillRightActivationAsync(
            result.Activation!,
            call.Session.GetInt("solarsystemid") ?? call.Session.GetInt("solarsystemid2") ?? 0,
            call.Cancellation);
        await NotifyOwnerAsync(result.Activation!, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("BuyKillRight")]
    public async ValueTask<PyValue> BuyKillRight(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } buyerCharacterId || buyerCharacterId <= 0
            || Number(call.Arg(0)) is not { } killRightId || killRightId <= 0)
        {
            throw Eve.Services.MachoErrors.ThrowUserError("NoValidKillRight");
        }
        if (runtime is null)
        {
            throw Eve.Services.MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyStr("Unable to buy kill right.")));
        }

        decimal? expectedPrice = null;
        if (call.Arg(1) is not PyNone)
        {
            expectedPrice = Money(call.Arg(1));
            if (expectedPrice is null)
            {
                throw Eve.Services.MachoErrors.ThrowUserError("KillRightNotForSale");
            }
        }

        var buyerOwnerIds = new HashSet<long> { buyerCharacterId };
        if (call.Session.GetInt("corpid") is > 0 and { } corporationId)
        {
            buyerOwnerIds.Add(corporationId);
        }
        if (call.Session.GetInt("allianceid") is > 0 and { } allianceId)
        {
            buyerOwnerIds.Add(allianceId);
        }

        Eve.Crimewatch.KillRightPayment? payment = walletTransactions is null ? null : PayKillRightAsync;
        var result = await store.BuyKillRightAsync(
            killRightId, buyerCharacterId, buyerOwnerIds, expectedPrice,
            NowUnixMs, payment, call.Cancellation);
        if (!result.Success)
        {
            return await ErrorAsync(result.ErrorKey, buyerCharacterId, result.RequiredAmount ?? expectedPrice, call.Cancellation);
        }

        await runtime.ApplyKillRightActivationAsync(
            result.Activation!,
            call.Session.GetInt("solarsystemid") ?? call.Session.GetInt("solarsystemid2") ?? 0,
            call.Cancellation);
        await NotifyOwnerAsync(result.Activation!, call.Cancellation);
        return PyNone.Instance;
    }

    private async Task<Eve.Crimewatch.KillRightPaymentResult> PayKillRightAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long activationId,
        long buyerCharacterId,
        long sellerCharacterId,
        decimal amount,
        long timeUnixMs,
        CancellationToken cancellationToken)
    {
        if (walletTransactions is null)
        {
            return Eve.Crimewatch.KillRightPaymentResult.Failed;
        }
        try
        {
            await walletTransactions.TransferAsync(
                connection, transaction, buyerCharacterId, sellerCharacterId, amount,
                116, "Kill right fee", timeUnixMs, $"killright-fee:{activationId}", cancellationToken);
            return Eve.Crimewatch.KillRightPaymentResult.Paid;
        }
        catch (Eve.Wallet.InsufficientFundsException)
        {
            return Eve.Crimewatch.KillRightPaymentResult.InsufficientFunds;
        }
        catch (Exception error) when (error is Eve.Wallet.WalletNotFoundException or Eve.Wallet.MoneyLimitException)
        {
            return Eve.Crimewatch.KillRightPaymentResult.Failed;
        }
    }

    private async ValueTask<PyValue> ErrorAsync(
        string? errorKey,
        long characterId,
        decimal? requiredAmount,
        CancellationToken cancellationToken)
    {
        switch (errorKey)
        {
            case "KillRightExpired":
                throw Eve.Services.MachoErrors.ThrowUserError("KillRightExpired");
            case "KillRightNotForSale":
                throw Eve.Services.MachoErrors.ThrowUserError("KillRightNotForSale");
            case "NotEnoughMoney":
            {
                var balance = walletStore is null
                    ? 0m
                    : (await walletStore.GetAsync(characterId, cancellationToken))?.Isk ?? 0m;
                throw Eve.Services.MachoErrors.ThrowUserError(
                    "NotEnoughMoney",
                    ("amount", new PyReal((double)(requiredAmount ?? 0m))),
                    ("balance", new PyReal((double)balance)));
            }
            case "PaymentFailed":
                throw Eve.Services.MachoErrors.ThrowUserError(
                    "CustomNotify", ("notify", new PyStr("Unable to buy kill right.")));
            default:
                throw Eve.Services.MachoErrors.ThrowUserError("NoValidKillRight");
        }
    }

    private async Task NotifyOwnerAsync(KillRightActivation activation, CancellationToken cancellationToken)
    {
        if (activation.OwnerCharacterId == activation.ActivatorCharacterId
            || KillRightUsedNotification is not { } notifyOwner)
        {
            return;
        }
        try
        {
            await notifyOwner(activation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            log?.Invoke($"kill right {activation.KillRightId} activation notification failed: {error.Message}");
        }
    }

    private long NowUnixMs => nowUnixMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static decimal? Money(PyValue value)
    {
        if (value is PyInt integer && integer.Value >= 0)
        {
            return integer.Value;
        }
        if (value is PyLong largeInteger && largeInteger.Value >= 0)
        {
            return largeInteger.Value;
        }
        if (value is PyReal real && double.IsFinite(real.Value)
            && decimal.TryParse(real.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            && amount >= 0 && decimal.Round(amount, 2) == amount)
        {
            return amount;
        }
        return null;
    }

    private static long? Number(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value == Math.Truncate(real.Value) => (long)real.Value,
        _ => null,
    };
}
