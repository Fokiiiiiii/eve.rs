using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Market;
using Eve.Wallet;

namespace Eve.Market.Tests;

/// <summary>
/// M6: the corporation's transactions by division (CorpGetTransactions, EVE.js 6601 and 6616-6631). An Accountant or a Junior Accountant reads
/// every division; any other member reads the division whose Account Take role it holds; a read of any other division, or of every division
/// without the accountant role, is CrpAccessDenied with the reason (101, NoAccessToWalletDivision). The rows are newest first and carry the
/// division as keyID (the client names the division from it, quote.py:442-443).
/// </summary>
public class MarketTransactionReadTests
{
    private const int Tritanium = 34;
    private const long Minute = 60_000;

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private sealed class Outbox : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    /// <summary>A character's session and the market service built as the host builds it, for one viewer.</summary>
    private static async Task<(ClientSession Session, MarketProxyService Proxy)> ViewerAsync(TradeRig rig, long actor, long sessionId)
    {
        var session = new ClientSession(new Outbox()) { SessionId = sessionId };
        session.Set("charid", new PyInt(actor));
        var sessions = new CharacterSessions();
        sessions.Enter(actor, session);
        var catalog = await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        return (session, new MarketProxyService(rig.Book, rig.Trading, rig.Topology, rig.Characters, rig.Corporations, rig.Wallets, catalog,
            () => rig.Now, rig.Expiry, sessions));
    }

    private static PyValue Received(PyValue value)
    {
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(value));
        if (decoded is not PyObject { Name: var name } cached || JsSemantics.PlainText(name) != ObjectCaching.ResultClass) return decoded;
        var stream = Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cached.Args).Items[1]);
        return stream.Decoded ?? MarshalCodec.Decode(stream.Raw);
    }

    private static PyValue DictField(PyDict dict, string name) => dict.Entries.Single(e => JsSemantics.PlainText(e.Key) == name).Value;

    private static double Num(PyValue value) => value switch
    {
        PyInt n => n.Value,
        PyLong n => n.Value,
        PyReal n => n.Value,
        _ => throw new InvalidOperationException($"not a number: {value}"),
    };

    private static PyValue Line(int typeId, long quantity, double price) => Py.KeyVal(
        ("typeID", new PyInt(typeId)), ("quantity", new PyInt(quantity)), ("price", new PyReal(price)), ("minVolume", new PyInt(1)));

    /// <summary>A labelled refusal's name and its arguments, as the client's UserError reads them.</summary>
    private static (string Label, PyDict Arguments) UserError(MachoWrappedException error)
    {
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var args = Assert.IsType<PyTuple>(header.Items[1]);
        return (error.Error.UserErrorMessage, Assert.IsType<PyDict>(args.Items[1]));
    }

    /// <summary>The rows of a CorpGetTransactions answer, each as its fields (keyID, quantity, accountID).</summary>
    private static List<PyDict> Rows(PyValue answer) =>
        Assert.IsType<PyList>(Received(answer)).Items.Select(row => Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args)).ToList();

    private static long Field(PyDict row, string name) => (long)Num(DictField(row, name));

    /// <summary>A corporation with the founder as its chief, funded in both the 1000 and the 1006 divisions.</summary>
    private static async Task<long> FundedCorporationAsync(TradeRig r, long founder)
    {
        var corp = await r.Corporations.CreateAsync(founder, new("Ledger Works", "LEDGR", "", "", 0), "ledger-corp");
        await using (var c = await r.Source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await r.Wallets.TransferAsync(c, tx, new WalletAccount(founder), new WalletAccount(corp.Id, 1006), 1000m, 10, "Deposit", r.Now, "deposit-1006", "test");
            await r.Wallets.TransferAsync(c, tx, new WalletAccount(founder), new WalletAccount(corp.Id, 1000), 1000m, 10, "Deposit", r.Now, "deposit-1000", "test");
            await tx.CommitAsync();
        }
        return corp.Id;
    }

    /// <summary>The founder buys 2 Tritanium through the division 1006, then 3 through the division 1000, one minute later.</summary>
    private static async Task TwoDivisionsOfBuysAsync(TradeRig r, long founder, ClientSession session, MarketProxyService proxy)
    {
        await r.Book.SeedNpcAsync([new("ask", Tritanium, false, 10, 100, 32767, MarketLocation.From(r.Station), r.Now)]);
        await r.Corporations.SetAccountKeyAsync(founder, 1006);
        await proxy.BuyMultipleItems(Call(session, "BuyMultipleItems", 1, new PyInt(r.Station.Id), new PyList([Line(Tritanium, 2, 10)]), PyBool.True));
        r.Now += Minute;
        await r.Corporations.SetAccountKeyAsync(founder, 1000);
        await proxy.BuyMultipleItems(Call(session, "BuyMultipleItems", 2, new PyInt(r.Station.Id), new PyList([Line(Tritanium, 3, 10)]), PyBool.True));
    }

    [PgFact]
    public async Task An_accountant_reads_every_division_newest_first_and_the_division_and_fromDate_filters_apply()
    {
        await using var r = await TradeRig.Create();
        var founder = await r.Actor("Ledger Chief", 10_000_000m);
        await FundedCorporationAsync(r, founder);
        var (session, proxy) = await ViewerAsync(r, founder, 501);
        var firstAt = r.Now;
        await TwoDivisionsOfBuysAsync(r, founder, session, proxy);

        var all = Rows(await proxy.CorpGetTransactions(Call(session, "CorpGetTransactions", 3, PyNone.Instance, PyNone.Instance)));
        Assert.Equal(2, all.Count);
        Assert.Equal(3, Field(all[0], "quantity"));          // newest first
        Assert.Equal(1000, Field(all[0], "keyID"));
        Assert.Equal(2, Field(all[1], "quantity"));
        Assert.Equal(1006, Field(all[1], "keyID"));
        Assert.Equal(founder, Field(all[0], "buyerID"));

        var division = Rows(await proxy.CorpGetTransactions(Call(session, "CorpGetTransactions", 4, PyNone.Instance, new PyInt(1006))));
        Assert.Equal(new[] { 2L }, division.Select(row => Field(row, "quantity")));

        var recent = Rows(await proxy.CorpGetTransactions(Call(session, "CorpGetTransactions", 5,
            new PyLong(Py.FileTimeFromUnixMs(firstAt + Minute / 2)), PyNone.Instance)));
        Assert.Equal(new[] { 3L }, recent.Select(row => Field(row, "quantity")));
    }

    [PgFact]
    public async Task A_member_with_account_take_reads_its_division_and_is_refused_the_others_and_every_division()
    {
        await using var r = await TradeRig.Create();
        var founder = await r.Actor("Ledger Chief", 10_000_000m);
        var corp = await FundedCorporationAsync(r, founder);
        var (founderSession, founderProxy) = await ViewerAsync(r, founder, 511);
        await TwoDivisionsOfBuysAsync(r, founder, founderSession, founderProxy);
        var member = await JoinAsync(r, founder, corp, "Take Member", CorporationRoles.WalletTakeRole(1006));
        var (session, proxy) = await ViewerAsync(r, member, 512);

        var own = Rows(await proxy.CorpGetTransactions(Call(session, "CorpGetTransactions", 6, PyNone.Instance, new PyInt(1006))));
        Assert.Equal(new[] { 1006L }, own.Select(row => Field(row, "keyID")));

        var other = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            proxy.CorpGetTransactions(Call(session, "CorpGetTransactions", 7, PyNone.Instance, new PyInt(1000))).AsTask());
        var (otherLabel, otherArguments) = UserError(other);
        Assert.Equal("CrpAccessDenied", otherLabel);
        var reason = Assert.IsType<PyTuple>(DictField(otherArguments, "reason"));
        Assert.Equal(101, Num(reason.Items[0]));

        var every = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            proxy.CorpGetTransactions(Call(session, "CorpGetTransactions", 8, PyNone.Instance, PyNone.Instance)).AsTask());
        Assert.Equal("CrpAccessDenied", UserError(every).Label);
    }

    [PgFact]
    public async Task A_junior_accountant_reads_every_division_and_a_member_with_no_take_role_reads_none()
    {
        await using var r = await TradeRig.Create();
        var founder = await r.Actor("Ledger Chief", 10_000_000m);
        var corp = await FundedCorporationAsync(r, founder);
        var (founderSession, founderProxy) = await ViewerAsync(r, founder, 521);
        await TwoDivisionsOfBuysAsync(r, founder, founderSession, founderProxy);
        var junior = await JoinAsync(r, founder, corp, "Junior Books", CorporationRoles.JuniorAccountant);
        var plain = await JoinAsync(r, founder, corp, "Plain Member", null);
        var (juniorSession, juniorProxy) = await ViewerAsync(r, junior, 522);
        var (plainSession, plainProxy) = await ViewerAsync(r, plain, 523);

        var everything = Rows(await juniorProxy.CorpGetTransactions(Call(juniorSession, "CorpGetTransactions", 9, PyNone.Instance, PyNone.Instance)));
        Assert.Equal(2, everything.Count);

        var denied = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            plainProxy.CorpGetTransactions(Call(plainSession, "CorpGetTransactions", 10, PyNone.Instance, new PyInt(1006))).AsTask());
        Assert.Equal("CrpAccessDenied", UserError(denied).Label);
    }

    /// <summary>A character who joins the corporation through the application flow, with the given roles (null: none).</summary>
    private static async Task<long> JoinAsync(TradeRig rig, long founder, long corporationId, string name, long? roles)
    {
        var character = await rig.Actor(name);
        var application = await rig.Corporations.ApplyAsync(character, corporationId, "Please accept.");
        await rig.Corporations.RespondAsync(founder, application, 6, "Please accept.", "Welcome");
        await rig.Corporations.RespondAsync(character, application, 2, "Please accept.", "Accepted");
        if (roles is { } masks)
        {
            var joined = (await rig.Corporations.MemberAsync(character))!;
            await rig.Corporations.UpdateMemberAsync(founder, joined with { Masks = new RoleMasks(masks) });
        }
        return character;
    }
}
