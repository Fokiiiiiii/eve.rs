using System.Text;
using System.Net;
using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Skills;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public partial class HostServerTests
{
    [PgFact]
    public async Task Activity_alpha_injector_and_bill_rpcs_keep_client_wire_contracts_on_the_composed_host()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var characterData = Assert.IsType<Eve.Characters.Character>(await rig.Characters.GetAsync(character));
        var activityMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("activityMgr"));
        var alphaInjector = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("alphaInjectorMgr"));
        var billMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("billMgr"));

        StartComposed(registry =>
        {
            registry.Register(activityMgr);
            registry.Register(alphaInjector);
            registry.Register(billMgr);
        }, session =>
        {
            session.CharacterId = character;
            session.UserId = 1;
            session.Set("charid", new PyLong(character));
            session.Set("corpid", new PyLong(characterData.CorporationId));
            session.Set("stationid", new PyLong(rig.Station));
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var notifications = new List<string>();
        long callId = 0;
        async Task<PyValue> Exchange(string service, string method, PyValue[] args, PyDict kwargs)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, service, method, new PyTuple(args), kwargs))));
            while (true)
            {
                var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var packet = MachoPacket.Decode(response);
                if (packet?.Type == MachoMessage.Notification)
                {
                    notifications.Add(packet.Dest.BroadcastId ?? string.Empty);
                    continue;
                }
                return response;
            }
        }

        PyDict TransportKwargs() => new([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(string service, string method, params PyValue[] args) =>
            Result(await Exchange(service, method, args, TransportKwargs()))!;

        var initialBadges = Assert.IsType<PyList>(await Call("activityMgr", "GetActivityBadgeStatuses"));
        Assert.Empty(initialBadges.Items);
        Assert.Equal(["OnSystemActivityUpdated"], notifications);
        notifications.Clear();

        Assert.Equal(PyBool.True, await Call("activityMgr", "ActivitySeen", new PyInt(42)));
        Assert.Equal(PyBool.True, await Call("activityMgr", "ActivitySeen", new PyLong(4_294_967_297)));
        Assert.Equal(PyBool.False, await Call("activityMgr", "ActivitySeen", new PyInt(0)));
        var badges = Assert.IsType<PyList>(await Call("activityMgr", "GetActivityBadgeStatuses"));
        Assert.Equal(new PyList([new PyInt(42), new PyLong(4_294_967_297)]), badges);
        Assert.Equal(["OnSystemActivityUpdated"], notifications);
        notifications.Clear();

        Assert.Equal(new PyInt(1), await Call("activityMgr", "GetActivitiesPopState"));
        Assert.Equal(PyBool.True, await Call("activityMgr", "SetActivitiesPopState", new PyInt(0)));
        Assert.Equal(new PyInt(0), await Call("activityMgr", "GetActivitiesPopState"));
        Assert.Equal(PyBool.True, await Call("activityMgr", "SetActivitiesPopState", new PyInt(1)));
        Assert.Equal(new PyInt(1), await Call("activityMgr", "GetActivitiesPopState"));

        // Rows 897 and 898 are duplicate observations of this same, zero-argument client RPC.
        Assert.Equal(new PyLong(0), await Call("alphaInjectorMgr", "GetNextAvailableInjection"));
        Assert.Equal(new PyLong(0), await Call("alphaInjectorMgr", "GetNextAvailableInjection"));
        var malformedAlphaCall = await Exchange("alphaInjectorMgr", "GetNextAvailableInjection", [],
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("unexpected"), new PyInt(1))]));
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(malformedAlphaCall)).Type);

        Assert.Empty(Assert.IsType<PyList>(await Call("billMgr", "GetCorporationBills")).Items);
        Assert.Empty(Assert.IsType<PyList>(await Call("billMgr", "GetCorporationBillsReceivable")).Items);

        foreach (var (method, args) in new[]
        {
            ("GetAutomaticPaySettings", Array.Empty<PyValue>()),
            ("SendAutomaticPaySettings", [new PyDict([])]),
            ("CharPayBill", [new PyLong(1)]),
            ("PayCorporationBill", [new PyLong(1)]),
        })
        {
            var response = await Exchange("billMgr", method, args, TransportKwargs());
            Assert.Equal(MachoMessage.ErrorResponse,
                Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
        }
    }

    [Fact]
    public async Task Char_pay_bill_returns_none_through_the_registered_host_route()
    {
        const long characterId = 90_000_201;
        const long billId = 7_001;
        (long BillId, long CharacterId)? payment = null;
        var service = new Eve.Services.Corporations.BillMgrService(
            null!, null!, null!, _ => null, () => 0,
            new Eve.Services.Corporations.BillMgrRpcCallbacks(
                PayCharacterBillAsync: (paidBillId, paidCharacterId, _) =>
                {
                    payment = (paidBillId, paidCharacterId);
                    return ValueTask.FromResult(true);
                }));

        StartComposed(
            registry => registry.Register(service),
            session => session.CharacterId = characterId);
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 1, "billMgr", "CharPayBill", new PyInt(billId)));
        Assert.Equal((billId, characterId), payment);
    }

    [PgFact]
    public async Task Character_bill_payment_settles_through_the_registered_host_route()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        await rig.FundAsync(character, 1_000m);
        await rig.AddCorporationMemberAsync(character);
        var corporationId = (await rig.Characters.GetAsync(character))!.CorporationId;
        const decimal amount = 125.25m;
        long billId;

        var walletStore = new Eve.Wallet.PgWalletStore(rig.Source, rig.WalletSchema);
        var transactions = new Eve.Wallet.WalletTransactions(rig.Source, rig.WalletSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await walletStore.OpenAsync(connection, transaction, corporationId, 0m, 0);
            await transactions.OpenCorporationDivisionsAsync(connection, transaction, corporationId);
            await transaction.CommitAsync();
        }

        var corporationStore = new Eve.Corporations.CorporationStore(
            rig.Source, rig.Characters, walletStore, transactions,
            new Eve.Skills.PgSkillStore(rig.Source, rig.SkillsSchema), () => rig.NowUnixMs,
            rig.CorporationsSchema, events: rig.Events);
        const long externalId = 98_000_002;
        var issuedBill = await corporationStore.CreateCharacterBillAsync(
            character, 4, amount, corporationId, DateTimeOffset.UtcNow.AddDays(30).ToFileTime(),
            "host-character-bill", externalId: externalId);
        Assert.Equal(issuedBill, await corporationStore.CreateCharacterBillAsync(
            character, 4, amount, corporationId, issuedBill.DueFiletime,
            "host-character-bill", externalId: externalId));
        billId = issuedBill.BillId;
        await rig.Events.PumpAsync();

        var billMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("billMgr"));
        StartComposed(registry => registry.Register(billMgr), session =>
        {
            session.CharacterId = character;
            session.UserId = 1;
            session.Set("charid", new PyLong(character));
            session.Set("corpid", new PyLong(corporationId));
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 1, "billMgr", "CharPayBill", new PyLong(billId)));

        Assert.Equal(874.75m, await transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(character)));
        Assert.Equal(amount, await transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(corporationId)));
        await using var verify = await rig.Source.OpenConnectionAsync();
        await using var bill = new NpgsqlCommand(
            $"SELECT paid FROM {rig.CorporationsSchema}.character_bills WHERE bill_id = $1", verify);
        bill.Parameters.Add(new NpgsqlParameter { Value = billId });
        Assert.True((bool)(await bill.ExecuteScalarAsync())!);

        await rig.Events.PumpAsync();
        await using var persistence = new Eve.GameStore.PersistenceStore(rig.Source, rig.GameStoreSchema);
        await persistence.EnsureSchemaAsync();
        var noticeTable = await persistence.LoadTableAsync("notifications", createIfMissing: false);
        var notices = noticeTable["boxes"]![character.ToString()]!["byID"]!.AsObject()
            .Select(pair => pair.Value!.AsObject()).ToArray();
        Assert.Collection(notices.OrderBy(value => value["notificationID"]!.GetValue<long>()),
            issued =>
            {
                Assert.Equal(9, issued["typeID"]!.GetValue<int>());
                Assert.Equal(2, issued["groupID"]!.GetValue<int>());
                Assert.Equal(billId, issued["data"]!["billID"]!.GetValue<long>());
                Assert.Equal(externalId, issued["data"]!["externalID"]!.GetValue<long>());
            },
            paid =>
            {
                Assert.Equal(12, paid["typeID"]!.GetValue<int>());
                Assert.Equal(2, paid["groupID"]!.GetValue<int>());
                Assert.Equal(billId, paid["data"]!["billID"]!.GetValue<long>());
                Assert.Equal(externalId, paid["data"]!["externalID"]!.GetValue<long>());
                Assert.Equal("long", paid["data"]!["paidDateTime"]!["type"]!.GetValue<string>());
                Assert.Equal(character, paid["data"]!["paidByOwnerID"]!.GetValue<long>());
            });
    }

    [PgFact]
    public async Task Character_bill_payment_refuses_short_balance_and_serializes_paid_retries()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        await rig.FundAsync(character, 0m);
        await rig.AddCorporationMemberAsync(character);
        var corporationId = (await rig.Characters.GetAsync(character))!.CorporationId;
        var walletStore = new Eve.Wallet.PgWalletStore(rig.Source, rig.WalletSchema);
        var transactions = new Eve.Wallet.WalletTransactions(rig.Source, rig.WalletSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await walletStore.OpenAsync(connection, transaction, corporationId, 0m, 0);
            await transactions.OpenCorporationDivisionsAsync(connection, transaction, corporationId);
            await transaction.CommitAsync();
        }
        var corporationStore = new Eve.Corporations.CorporationStore(
            rig.Source, rig.Characters, walletStore, transactions,
            new Eve.Skills.PgSkillStore(rig.Source, rig.SkillsSchema), () => rig.NowUnixMs,
            rig.CorporationsSchema, events: rig.Events);
        const decimal amount = 125.25m;
        var bill = await corporationStore.CreateCharacterBillAsync(
            character, 2, amount, corporationId, DateTimeOffset.UtcNow.AddDays(30).ToFileTime(),
            "host-character-bill-short-balance");
        await rig.Events.PumpAsync();

        var billMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("billMgr"));
        StartComposed(registry => registry.Register(billMgr), session =>
        {
            session.CharacterId = character;
            session.UserId = 1;
            session.Set("charid", new PyLong(character));
            session.Set("corpid", new PyLong(corporationId));
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<Send> FailedCallAsync(long callId)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, callId, "billMgr", "CharPayBill", new PyTuple([new PyLong(bill.BillId)]), TransportKwargs()))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type);
            return send;
        }

        var insufficient = await FailedCallAsync(1);
        Assert.Equal("NotEnoughMoney", UserErrorKey(insufficient));
        var errorState = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(insufficient.Payload)).Args);
        var errorPayload = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(errorState.Items[4]).Items[2]);
        var errorObject = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(errorPayload.Items[0]).Decoded);
        var errorHeader = Assert.IsType<PyTuple>(errorObject.Header);
        var substitutions = Assert.IsType<PyDict>(Assert.IsType<PyTuple>(errorHeader.Items[1]).Items[1]);
        PyValue Substitution(string name) => substitutions.Entries.Single(entry => entry.Key switch
        {
            PyStr text => text.Value == name,
            PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span) == name,
            _ => false,
        }).Value;
        Assert.Equal(new PyReal(125.25d), Substitution("amount"));
        Assert.Equal(new PyReal(0), Substitution("balance"));
        Assert.Equal(0m, await transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(character)));
        await using (var verify = await rig.Source.OpenConnectionAsync())
        await using (var unpaid = new NpgsqlCommand(
            $"SELECT paid FROM {rig.CorporationsSchema}.character_bills WHERE bill_id=$1", verify))
        {
            unpaid.Parameters.Add(new NpgsqlParameter { Value = bill.BillId });
            Assert.False((bool)(await unpaid.ExecuteScalarAsync())!);
        }

        await rig.FundAsync(character, 200m);
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 2, "billMgr", "CharPayBill", new PyLong(bill.BillId)));
        await rig.Events.PumpAsync();
        Assert.Equal(74.75m, await transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(character)));
        Assert.Equal(amount, await transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(corporationId)));

        var duplicate = await FailedCallAsync(3);
        Assert.Equal("CustomNotify", UserErrorKey(duplicate));
        Assert.Contains("BILL_NOT_PAYABLE", Encoding.ASCII.GetString(duplicate.Payload.Span));
        Assert.Equal(74.75m, await transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(character)));
        Assert.Single(await transactions.JournalAsync(character),
            entry => entry.OperationKey.StartsWith($"character-bill:{bill.BillId}:payment", StringComparison.Ordinal));

        var concurrentBill = await corporationStore.CreateCharacterBillAsync(
            character, 2, 20m, corporationId, DateTimeOffset.UtcNow.AddDays(30).ToFileTime(),
            "host-character-bill-concurrent-payment");
        await rig.Events.PumpAsync();
        var simultaneous = await Task.WhenAll(
            corporationStore.PayCharacterBillAsync(concurrentBill.BillId, character, rig.NowUnixMs),
            corporationStore.PayCharacterBillAsync(concurrentBill.BillId, character, rig.NowUnixMs));
        Assert.Single(simultaneous, payment => payment.Paid);
        Assert.Single(simultaneous, payment => !payment.Paid && payment.Error == "BILL_NOT_PAYABLE");
        Assert.Equal(54.75m, await transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(character)));
        Assert.Equal(145.25m, await transactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(corporationId)));
        await rig.Events.PumpAsync();
    }

    private void StartComposed(Action<ServiceRegistry> configure, Action<ClientSession>? sessionOpened = null)
    {
        var registry = new ServiceRegistry();
        configure(registry);
        Func<long> now = () => FixedNowMs;
        var config = GlobalConfig.Build(Options);
        registry.Register(new MachoNetService(new Info(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now) { StrictRpc = true };
        var encoded = MarshalCodec.Encode(config);
        _server = new HostServer(
            new HostOptions
            {
                MaxFrontConnections = 1_024,
                MaxPendingPacketsPerSession = 10_000,
                MaxPendingBytesPerSession = 16 * 1024 * 1024,
                MaxQueuedOutboundBytesPerSession = 8 * 1024 * 1024,
                Log = message => { lock (_log) _log.Add(message); },
                SessionOpened = sessionOpened,
            },
            dispatcher,
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => encoded);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);
    }
}
