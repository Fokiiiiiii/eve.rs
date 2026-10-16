using System.Globalization;
using Eve.Corporations;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;

namespace Eve.Services.Corporations;

public sealed record BillMgrRpcCallbacks(
    Func<long, long, CancellationToken, ValueTask<bool>>? PayCharacterBillAsync = null,
    Func<long, CancellationToken, ValueTask<IReadOnlyList<PyValue>>>? GetCorporationBillsAsync = null,
    Func<long, CancellationToken, ValueTask<IReadOnlyList<PyValue>>>? GetCorporationBillsReceivableAsync = null,
    Func<long, long, long, int, CancellationToken, ValueTask<bool>>? PayCorporationBillAsync = null);

public sealed class BillMgrService(
    CorporationStore corporations,
    CorporationOfficeStore officeStore,
    WalletTransactions wallets,
    Func<long, long?> stationOwnerOf,
    Func<long> nowUnixMs,
    BillMgrRpcCallbacks? callbacks = null) : IService
{
    private const int RentalBillTypeId = 2;
    private const long AccountantOrJuniorRoles = CorporationRoles.Accountant | CorporationRoles.JuniorAccountant;

    private readonly BillMgrRpcCallbacks _callbacks = callbacks ?? new();

    public string Name => "billMgr";

    public bool HasMethod(string method) => method is
        "CharPayBill" or
        "GetAutomaticPaySettings" or
        "GetCorporationBills" or
        "GetCorporationBillsReceivable" or
        "PayCorporationBill" or
        "SendAutomaticPaySettings";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        switch (call.Method)
        {
            case "CharPayBill":
            {
                var characterId = call.Session.CharacterId.GetValueOrDefault();
                var billId = PositiveId(call.Arg(0));
                if (characterId <= 0 || billId <= 0 || _callbacks.PayCharacterBillAsync is not { } payCharacter ||
                    !await payCharacter(billId, characterId, call.Cancellation))
                    throw new CorporationError("BILL_NOT_PAYABLE");
                return PyNone.Instance;
            }

            case "GetAutomaticPaySettings":
                return await GetAutomaticPaySettingsAsync(call);

            case "GetCorporationBills":
                return await GetCorporationBillsAsync(call);

            case "GetCorporationBillsReceivable":
            {
                var corporationId = SessionCorporationId(call.Session);
                if (corporationId <= 0) return new PyList([]);
                IReadOnlyList<PyValue> bills = _callbacks.GetCorporationBillsReceivableAsync is { } readReceivable
                    ? await readReceivable(corporationId, call.Cancellation)
                    : (await officeStore.BillsForCreditorAsync(corporationId, stationOwnerOf, call.Cancellation))
                        .Select(WireBill)
                        .Concat((await corporations.CorporationBillsReceivableAsync(corporationId,call.Cancellation)).Select(WireBill))
                        .ToArray();
                return Py.List(bills.ToArray());
            }

            case "PayCorporationBill":
                return await PayCorporationBillAsync(call);

            case "SendAutomaticPaySettings":
                return await SendAutomaticPaySettingsAsync(call);

            default:
                return PyNone.Instance;
        }
    }

    private async ValueTask<PyValue> GetCorporationBillsAsync(ServiceCall call)
      {
          var corporationId = SessionCorporationId(call.Session);
          if (corporationId <= 0) return new PyList([]);
          if (_callbacks.GetCorporationBillsAsync is { } readBills)
              return Py.List((await readBills(corporationId, call.Cancellation)).ToArray());
          var bills = (await officeStore.BillsForCorporationAsync(corporationId,call.Cancellation)).Select(WireBill)
              .Concat((await corporations.CorporationBillsAsync(corporationId,call.Cancellation)).Select(WireBill));
          return Py.List(bills.ToArray());
      }

    private async ValueTask<PyValue> GetAutomaticPaySettingsAsync(ServiceCall call)
    {
        var authority = await RequireAuthorityAsync(call, AccountantOrJuniorRoles);
        var ownerIds = await AutomaticPayOwnerIdsAsync(authority, call.Cancellation);
        var settings = await officeStore.AutomaticPaySettingsAsync(ownerIds, call.Cancellation);
        return new PyDict(settings.OrderBy(entry => entry.Key).Select(entry =>
            new KeyValuePair<PyValue, PyValue>(Py.Number(entry.Key), WireSettings(entry.Value))).ToArray());
    }

    private async ValueTask<PyValue> SendAutomaticPaySettingsAsync(ServiceCall call)
    {
        var authority = await RequireAuthorityAsync(call, CorporationRoles.Accountant);
        var allowedOwnerIds = await AutomaticPayOwnerIdsAsync(authority, call.Cancellation);
        var submitted = ParseSettings(call.Arg(0));
        if (submitted.Keys.Any(ownerId => !allowedOwnerIds.Contains(ownerId)))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await officeStore.SetAutomaticPaySettingsAsync(submitted, call.Cancellation);
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> PayCorporationBillAsync(ServiceCall call)
      {
          var authority = await RequireAuthorityAsync(call, CorporationRoles.Accountant);
          var billId = PositiveId(call.Arg(0));
          var accountKey = NormalizeWalletKey(call.Kwarg("fromAccountKey") ?? call.Arg(1));
          var paidAtMs = nowUnixMs();
          var paid = await officeStore.PayBillAsync(
              billId,
              authority.Corporation.Id,
              authority.Corporation.Id,
              paidAtMs,
              async (connection, transaction, bill) =>
              {
                  var creditorId = bill.CreditorId ?? stationOwnerOf(bill.StationId);
                  if (creditorId is not > 0) throw new CorporationError("BILL_NOT_PAYABLE");
                  await OfficeRentSettlement.SettleAsync(wallets, connection, transaction, bill,
                      new WalletAccount(authority.Corporation.Id, accountKey), creditorId.Value, paidAtMs, call.Cancellation);
              },
              call.Cancellation);
          if (!paid)
              paid = await corporations.PayCorporationBillAsync(billId,authority.Corporation.Id,
                  call.Session.CharacterId.GetValueOrDefault(),accountKey,paidAtMs,call.Cancellation);
          if (!paid && _callbacks.PayCorporationBillAsync is { } payOtherBill)
              paid = await payOtherBill(billId, authority.Corporation.Id,
                  call.Session.CharacterId.GetValueOrDefault(), accountKey, call.Cancellation);
          if (!paid) throw new CorporationError("BILL_NOT_PAYABLE");
          return PyNone.Instance;
      }

    private async ValueTask<IReadOnlyList<long>> AutomaticPayOwnerIdsAsync(
        CorporationAuthority authority, CancellationToken cancellationToken)
    {
        var ownerIds = new List<long> { authority.Corporation.Id };
        if (authority.Corporation.AllianceId is not { } allianceId || allianceId <= 0) return ownerIds;
        var alliance = await corporations.AllianceAsync(allianceId, cancellationToken);
        if (alliance?.ActiveExecutorId == authority.Corporation.Id) ownerIds.Add(allianceId);
        return ownerIds;
    }

    private async ValueTask<CorporationAuthority> RequireAuthorityAsync(ServiceCall call, long requiredRoles)
    {
        var actorId = call.Session.CharacterId.GetValueOrDefault();
        var corporationId = SessionCorporationId(call.Session);
        if (actorId <= 0 || corporationId <= 0) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var corporation = await corporations.GetAsync(corporationId, call.Cancellation);
        var member = await corporations.MemberAsync(actorId, call.Cancellation);
        if (corporation is null || corporation.IsNpc || member?.CorporationId != corporationId)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var titles = await corporations.TitlesAsync(corporationId, call.Cancellation);
        var roles = CorporationRoles.Session(
            corporation,
            member,
            call.Session.GetInt("stationid"),
            titles);
        if ((roles.AtAll & requiredRoles) == 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return new CorporationAuthority(corporation, roles);
    }

    private PyValue WireBill(CorporationOfficeRentBill bill) => Py.KeyVal(
        ("billID", Py.Number(bill.BillId)),
        ("billTypeID", new PyInt(RentalBillTypeId)),
        ("amount", Py.Number((double)bill.AmountIsk)),
        ("interest", new PyInt(0)),
        ("debtorID", Py.Number(bill.CorporationId)),
        ("creditorID", Py.Number(bill.CreditorId ?? stationOwnerOf(bill.StationId) ?? 0)),
        ("dueDateTime", new PyLong(Py.FileTimeFromUnixMs(bill.DueAtMs))),
        ("paid", bill.Status == 1 ? new PyInt(1) : new PyInt(0)),
        ("paidDateTime", bill.PaidAtMs is { } paidAt ? new PyLong(Py.FileTimeFromUnixMs(paidAt)) : PyNone.Instance),
        ("paidByOwnerID", Py.Number(bill.PaidByOwnerId ?? 0)),
        ("externalID", new PyInt(-1)),
        ("externalID2", new PyInt(-1)));

      private static PyValue WireBill(CorporationBill bill) => Py.KeyVal(
          ("billID",Py.Number(bill.BillId)),
          ("billTypeID",new PyInt(bill.BillTypeId)),
          ("amount",Py.Number((double)bill.AmountIsk)),
          ("interest",Py.Number((double)bill.Interest)),
          ("debtorID",Py.Number(bill.DebtorId)),
          ("creditorID",Py.Number(bill.CreditorId)),
          ("dueDateTime",new PyLong(bill.DueFiletime)),
          ("paid",new PyInt(bill.Paid ? 1 : 0)),
          ("paidDateTime",bill.PaidFiletime is { } paidAt ? new PyLong(paidAt) : PyNone.Instance),
          ("paidByOwnerID",Py.Number(bill.PaidByOwnerId ?? 0)),
          ("externalID",Py.Number(bill.ExternalId)),
          ("externalID2",Py.Number(bill.ExternalId2)));

    private static PyValue WireSettings(CorporationBillAutomaticPaySettings settings) => new PyDict(
    [
        Pair("divisionID", new PyInt(settings.DivisionId)),
        .. settings.EnabledBillTypes.OrderBy(entry => entry.Key).Select(entry =>
            new KeyValuePair<PyValue, PyValue>(new PyInt(entry.Key), entry.Value ? PyBool.True : PyBool.False)),
    ]);

    private static Dictionary<long, CorporationBillAutomaticPaySettings> ParseSettings(PyValue value)
    {
        var settings = new Dictionary<long, CorporationBillAutomaticPaySettings>();
        if (value is not PyDict owners) return settings;
        foreach (var (rawOwnerId, rawSettings) in owners.Entries)
        {
            if (!TryIntegral(rawOwnerId, out var ownerId) || ownerId <= 0 || rawSettings is not PyDict entries)
                continue;
            var divisionId = 1000;
            var enabledBillTypes = new Dictionary<int, bool>();
            foreach (var (rawKey, rawValue) in entries.Entries)
            {
                var key = KeyText(rawKey);
                if (key == "divisionID")
                {
                    if (TryIntegral(rawValue, out var parsedDivision) && parsedDivision is >= 1000 and <= 1006)
                        divisionId = (int)parsedDivision;
                    continue;
                }
                if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var billTypeId))
                    enabledBillTypes[billTypeId] = BooleanValue(rawValue);
            }
            settings[ownerId] = new CorporationBillAutomaticPaySettings(divisionId, enabledBillTypes);
        }
        return settings;
    }

    private static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static string KeyText(PyValue value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyInt integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyLong integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyReal real => real.Value.ToString(CultureInfo.InvariantCulture),
        PyBool boolean => boolean.Value ? "True" : "False",
        _ => string.Empty,
    };

    private static bool TryIntegral(PyValue value, out long result)
    {
        switch (value)
        {
            case PyInt integer:
                result = integer.Value;
                return true;
            case PyLong integer:
                result = integer.Value;
                return true;
            case PyReal real when double.IsFinite(real.Value) && Math.Truncate(real.Value) == real.Value &&
                                  real.Value >= long.MinValue && real.Value <= long.MaxValue:
                result = (long)real.Value;
                return true;
            case var text when Eve.Macho.JsSemantics.StringValue(text) is { } plain && long.TryParse(plain, NumberStyles.Integer, CultureInfo.InvariantCulture, out result):
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool BooleanValue(PyValue value) => value switch
    {
        PyBool boolean => boolean.Value,
        PyInt integer => integer.Value != 0,
        PyLong integer => integer.Value != 0,
        PyReal real => real.Value != 0 && !double.IsNaN(real.Value),
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)! is "1" or "true" or "True",
        _ => false,
    };

    private static long PositiveId(PyValue value) => TryIntegral(value, out var id) && id > 0 ? id : 0;

    private static int NormalizeWalletKey(PyValue value)
    {
        var key = TryIntegral(value, out var parsed) ? parsed : 1000;
        return key is >= 1000 and <= 1006 ? (int)key : 1000;
    }

    private static long SessionCorporationId(ClientSession session)
    {
        var corporationId = session.GetInt("corporationID") ?? 0;
        return corporationId > 0 ? corporationId : Math.Max(0, session.GetInt("corpid") ?? 0);
    }

    private sealed record CorporationAuthority(Corporation Corporation, SessionRoles Roles);
}
