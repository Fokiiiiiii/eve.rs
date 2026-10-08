using Eve.Corporations;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Corporations.Tests;

public sealed class BillMgrServiceTests
{
    [PgFact]
    public async Task Bill_manager_exposes_all_client_methods_and_receivable_bill_callback_shape()
    {
        await using var rig = await Rig.CreateAsync();
        long paidBill = 0;
        long payingCharacter = 0;
        var callbacks = new BillMgrRpcCallbacks(
            PayCharacterBillAsync: (billId, characterId, _) =>
            {
                paidBill = billId;
                payingCharacter = characterId;
                return ValueTask.FromResult(true);
            });
        var service = new BillMgrService(rig.Store, rig.OfficeStore, rig.Transactions, _ => 1000044, () => 0, callbacks);
        var methods = new[]
        {
            "CharPayBill", "GetAutomaticPaySettings", "GetCorporationBills", "GetCorporationBillsReceivable",
            "PayCorporationBill", "SendAutomaticPaySettings",
        };
        Assert.All(methods, method => Assert.True(service.HasMethod(method), method));

        var session = new ClientSession { CharacterId = 9002 };
        session.Set("corpid", new PyInt(9003));
        var receivable = Assert.IsType<PyList>(await service.CallAsync(Call("GetCorporationBillsReceivable", session)));
        Assert.Empty(receivable.Items);

        var result = await service.CallAsync(Call("CharPayBill", session, new PyInt(7001)));
        Assert.Same(PyNone.Instance, result);
        Assert.Equal((7001L, 9002L), (paidBill, payingCharacter));

        var billsWithoutCorporation = Assert.IsType<PyList>(
            await service.CallAsync(Call("GetCorporationBills", new ClientSession())));
        Assert.Empty(billsWithoutCorporation.Items);
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };
}
