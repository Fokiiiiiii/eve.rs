using System.Text.Json;
using Eve.Corporations;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Wallet;
using Eve.Wallet;

namespace Eve.Corporations.Tests;

public sealed class AccountRpcTests
{
    [PgFact]
    public async Task GiveCash_transfers_between_character_wallets_and_returns_none()
    {
        await using var rig = await Rig.CreateAsync();
        var sender = await rig.CharacterAsync("GiveCash Sender");
        var recipient = await rig.CharacterAsync("GiveCash Recipient");
        var service = new AccountService(rig.Wallets, rig.Store, rig.Transactions);
        var senderBefore = (await rig.Wallets.GetAsync(sender))!.Isk;
        var recipientBefore = (await rig.Wallets.GetAsync(recipient))!.Isk;
        var changes = new List<WalletBalanceChange>();
        rig.Transactions.BalanceCommitted += changes.Add;

        var result = await service.GiveCash(new ServiceCall
        {
            Method = "GiveCash",
            Args = [Py.Number(recipient), new PyReal(125.75), new PyStr("private gift")],
            Session = new ClientSession { CharacterId = sender },
        });

        Assert.Equal(PyBool.True, result);
        Assert.Equal(senderBefore - 125.75m, (await rig.Wallets.GetAsync(sender))!.Isk);
        Assert.Equal(recipientBefore + 125.75m, (await rig.Wallets.GetAsync(recipient))!.Isk);
        Assert.Equal("private gift", Assert.Single(await rig.Transactions.JournalAsync(sender)).Reason);
        Assert.Equal(10, Assert.Single(await rig.Transactions.JournalAsync(sender)).RefTypeId);
        Assert.Equal(2, changes.Count);
    }

    [PgFact]
    public async Task GiveCash_to_a_corporation_does_not_return_private_balances_and_corp_transfer_uses_the_selected_division()
    {
        await using var rig = await Rig.CreateAsync();
        var actor = await rig.CharacterAsync("GiveCash Corporation CEO");
        var recipient = await rig.CharacterAsync("GiveCash Corp Recipient");
        var corporation = await rig.Store.CreateAsync(actor,
            new NewCorporation("GiveCash Test Corporation", "GCTST", "RPC test", "https://example.invalid", 0.05m,
                Settings: JsonSerializer.SerializeToElement(new { shape1 = 1, color1 = 2 })),
            $"givecash-rpc:{Guid.NewGuid():N}");
        var service = new AccountService(rig.Wallets, rig.Store, rig.Transactions);
        var session = new ClientSession { CharacterId = actor };
        session.Set("corporationID", Py.Number(corporation.Id));
        session.Set("corpid", Py.Number(corporation.Id));

        var personalBefore = (await rig.Wallets.GetAsync(actor))!.Isk;
        var longReason = await Assert.ThrowsAsync<MachoWrappedException>(() => service.GiveCash(new ServiceCall
        {
            Method = "GiveCash",
            Args = [Py.Number(corporation.Id), new PyReal(1), new PyStr(new string('x', 41))],
            Session = session,
        }).AsTask());
        Assert.Equal("WALLET_REASON_LIMIT_EXCEEDED", longReason.Error.UserErrorMessage);
        Assert.Equal(personalBefore, (await rig.Wallets.GetAsync(actor))!.Isk);
        var deposit = await service.GiveCash(new ServiceCall
        {
            Method = "GiveCash",
            Args = [Py.Number(corporation.Id), new PyReal(40.25)],
            Kwargs = new PyDict([
                new(new PyStr("toAccountKey"), new PyInt(1001)),
                new(new PyStr("reason"), new PyStr("corporation deposit")),
            ]),
            Session = session,
        });

        Assert.Equal(PyNone.Instance, deposit);
        Assert.Equal(personalBefore - 40.25m, (await rig.Wallets.GetAsync(actor))!.Isk);
        Assert.Equal(40.25m, await rig.Transactions.AccountBalanceAsync(new WalletAccount(corporation.Id, 1001)));

        var zeroDeposit = await service.GiveCash(new ServiceCall
        {
            Method = "GiveCash",
            Args = [Py.Number(corporation.Id), Py.Number(0)],
            Session = session,
        });
        Assert.Equal(PyNone.Instance, zeroDeposit);
        Assert.Equal(40.25m, await rig.Transactions.AccountBalanceAsync(new WalletAccount(corporation.Id, 1001)));

        var recipientBefore = (await rig.Wallets.GetAsync(recipient))!.Isk;
        var withdrawal = await service.GiveCashFromCorpAccount(new ServiceCall
        {
            Method = "GiveCashFromCorpAccount",
            Args = [Py.Number(recipient), new PyReal(12.25), new PyInt(1001), new PyStr("division transfer")],
            Session = session,
        });

        Assert.Equal(PyNone.Instance, withdrawal);
        Assert.Equal(28m, await rig.Transactions.AccountBalanceAsync(new WalletAccount(corporation.Id, 1001)));
        Assert.Equal(recipientBefore + 12.25m, (await rig.Wallets.GetAsync(recipient))!.Isk);
    }
}
