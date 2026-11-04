using System.Text;
using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Wallet;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class AccountWalletHostRpcTests
{
    [PgFact]
    public async Task Account_wallet_rpcs_route_real_wire_calls_persist_balances_and_notify()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var characterSchema = $"aw_char_{suffix}";
        var walletSchema = $"aw_wallet_{suffix}";
        var skillSchema = $"aw_skill_{suffix}";
        var corporationSchema = $"aw_corp_{suffix}";
        var schemas = new[] { characterSchema, walletSchema, skillSchema, corporationSchema };

        try
        {
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            var walletMigrations = PgWalletStore.MigrationSet();
            await Migrations.ApplyAsync(source, walletSchema,
                walletMigrations.Where(migration => migration.Name != "009_journal_account_time"));
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await CorporationStore.MigrateAsync(source, corporationSchema);

            var characters = new PgCharacterStore(source, characterSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var transactions = new WalletTransactions(source, walletSchema);
            long sender;
            long recipient;
            long walletlessRecipient;
            var historicalMonth = DateTimeOffset.UtcNow.AddDays(-15);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                async Task<long> CreatePilot(string name, decimal initialCash = 5_000m, bool openWallet = true)
                {
                    var id = await characters.InsertAsync(connection, transaction,
                        new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                            60000001, 30000001, 20000001, 10000001, null, null));
                    if (openWallet)
                        await wallets.OpenAsync(connection, transaction, id, initialCash, 0);
                    return id;
                }

                sender = await CreatePilot($"Wallet RPC Sender {suffix}",
                    CorporationStore.CorporationFoundingCost + 4_875m);
                recipient = await CreatePilot($"Wallet RPC Recipient {suffix}");
                walletlessRecipient = await CreatePilot($"Wallet RPC Missing Wallet {suffix}", openWallet: false);
                await transaction.CommitAsync();
            }

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                for (var index = 0; index < 125; index++)
                {
                    await transactions.AdjustAsync(connection, transaction, sender, 1m, recipient, 1,
                        "Historical wallet entry", historicalMonth.ToUnixTimeMilliseconds() + index,
                        $"historical:{index}");
                }
                await transaction.CommitAsync();
            }
            await Migrations.ApplyAsync(source, walletSchema, walletMigrations);

            var corporations = new CorporationStore(source, characters, wallets, transactions, skills,
                () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), corporationSchema);
            var corporation = await corporations.CreateAsync(sender,
                new NewCorporation($"Wallet RPC Corp {suffix}", $"W{suffix[..4]}", "", "", 0m),
                $"wallet-rpc-test:{suffix}");
            var session = new ClientSession(new PacketCapture())
            {
                SessionId = 908,
                ClientId = 909,
                UserId = 910,
                CharacterId = sender,
            };
            session.Set("corpid", new PyLong(corporation.Id));
            session.Set("corporationID", new PyLong(corporation.Id));
            var sessions = new CharacterSessions();
            sessions.Enter(sender, session);
            using var announcer = new WalletAnnouncer(transactions, sessions);
            var changes = new List<WalletBalanceChange>();
            transactions.BalanceCommitted += changes.Add;

            var registry = new ServiceRegistry();
            registry.Register(new AccountService(wallets, corporations, transactions));
            PacketDispatcher dispatcher = new(registry);
            var capture = (PacketCapture)session.Sink!;
            var open = new SessionOpen((ulong)session.SessionId, (ulong)session.ClientId, 910,
                0, "127.0.0.1", "EN", "GB");

            async Task<PyValue?> Call(long callId, string method, params PyValue[] args)
            {
                capture.Sent.Clear();
                var request = Packets.Call(open, callId, "account", method, args);
                var decodedRequest = MarshalCodec.Decode(MarshalCodec.Encode(request));
                Assert.True(await dispatcher.DispatchAsync(decodedRequest, session));
                var replies = capture.Sent.Where(packet =>
                    MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp).ToArray();
                return Result(Assert.Single(replies));
            }

            async Task<string> CallError(long callId, string method, params PyValue[] args)
            {
                capture.Sent.Clear();
                var request = Packets.Call(open, callId, "account", method, args);
                var decodedRequest = MarshalCodec.Decode(MarshalCodec.Encode(request));
                Assert.True(await dispatcher.DispatchAsync(decodedRequest, session));
                var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(capture.Sent)));
                Assert.Equal(MachoMessage.ErrorResponse, response.Type);
                return Encoding.ASCII.GetString(MarshalCodec.Encode(capture.Sent[0]));
            }

            async Task<(decimal? Balance, IReadOnlyList<JournalEntry> Journal)> WalletState(WalletAccount account) =>
                (await transactions.AccountBalanceAsync(account),
                    await transactions.JournalForAccountAsync(account.OwnerId, account.AccountKey));

            async Task AssertWalletStateUnchanged(
                WalletAccount account,
                (decimal? Balance, IReadOnlyList<JournalEntry> Journal) before)
            {
                var after = await WalletState(account);
                Assert.Equal(before.Balance, after.Balance);
                Assert.Equal(before.Journal, after.Journal);
            }

            async Task DeleteWallet(WalletAccount account)
            {
                await using var command = source.CreateCommand(
                    $"DELETE FROM {walletSchema}.balances WHERE owner_id = $1 AND account_key = $2");
                command.Parameters.Add(new NpgsqlParameter { Value = account.OwnerId });
                command.Parameters.Add(new NpgsqlParameter { Value = account.AccountKey });
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            void AssertNotifications(params string[] expectedIdTypes)
            {
                var packets = capture.Sent.Select(packet =>
                    Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
                Assert.Equal(expectedIdTypes.Length + 1, packets.Length);
                for (var index = 0; index < expectedIdTypes.Length; index++)
                {
                    Assert.Equal(MachoMessage.Notification, packets[index].Type);
                    Assert.Equal("OnAccountChange", packets[index].Dest.BroadcastId);
                    Assert.Equal(expectedIdTypes[index], packets[index].Dest.IdType);
                }
                Assert.Equal(MachoMessage.CallRsp, packets[^1].Type);
            }

            Assert.Equal(new PyReal(5_000), await Call(1, "GetCashBalance", new PyInt(0)));
            Assert.Equal(new PyReal(0), await Call(2, "GetCashBalance", new PyInt(1), new PyInt(1000)));

            var senderBeforeRefusal = (await wallets.GetAsync(sender))!.Isk;
            var recipientBeforeRefusal = (await wallets.GetAsync(recipient))!.Isk;
            var insufficientFunds = await CallError(17, "GiveCash", Py.Number(recipient),
                new PyReal((double)(senderBeforeRefusal + 1m)));
            Assert.Contains("NotEnoughMoney", insufficientFunds);
            Assert.Contains("amount", insufficientFunds);
            Assert.Contains("balance", insufficientFunds);
            Assert.Equal(senderBeforeRefusal, (await wallets.GetAsync(sender))!.Isk);
            Assert.Equal(recipientBeforeRefusal, (await wallets.GetAsync(recipient))!.Isk);
            Assert.Empty(changes);

            Assert.Equal(PyBool.True, await Call(3, "GiveCash", Py.Number(recipient),
                new PyReal(125.75), new PyStr("private gift")));
            AssertNotifications("clientID");
            Assert.Equal(new PyReal(4_874.25), await Call(4, "GetCashBalance", new PyInt(0)));

            Assert.Equal(PyNone.Instance, await Call(5, "GiveCash", Py.Number(corporation.Id),
                new PyReal(40.25), new PyStr("corporation deposit")));
            AssertNotifications("clientID", "*corpid&corpAccountKey");
            Assert.Equal(new PyReal(40.25), await Call(6, "GetCashBalance", new PyInt(1), new PyInt(1000)));

            var recipientBeforeCorpRefusal = (await wallets.GetAsync(recipient))!.Isk;
            var refusedCorpTransfer = await CallError(18, "GiveCashFromCorpAccount", Py.Number(recipient),
                new PyReal(40.26), new PyInt(1000), new PyStr("too much"));
            Assert.Contains("NotEnoughMoney", refusedCorpTransfer);
            Assert.Contains("amount", refusedCorpTransfer);
            Assert.Contains("balance", refusedCorpTransfer);
            Assert.Equal(4, changes.Count);
            Assert.Equal(40.25m, await transactions.AccountBalanceAsync(new WalletAccount(corporation.Id, 1000)));
            Assert.Equal(recipientBeforeCorpRefusal, (await wallets.GetAsync(recipient))!.Isk);

            Assert.Equal(PyNone.Instance, await Call(7, "GiveCashFromCorpAccount", Py.Number(recipient),
                new PyReal(12.25), new PyInt(1000), new PyStr("corporation payout")));
            AssertNotifications("*corpid&corpAccountKey");
            Assert.Equal(6, changes.Count);
            Assert.Equal(4_834m, (await wallets.GetAsync(sender))!.Isk);
            Assert.Equal(5_138m, (await wallets.GetAsync(recipient))!.Isk);
            Assert.Equal(28m, await transactions.AccountBalanceAsync(new WalletAccount(corporation.Id, 1000)));

            var restartedRegistry = new ServiceRegistry();
            restartedRegistry.Register(new AccountService(
                new PgWalletStore(source, walletSchema), corporations, new WalletTransactions(source, walletSchema)));
            dispatcher = new PacketDispatcher(restartedRegistry);

            Assert.Equal(new PyReal(4_834), await Call(8, "GetCashBalance", new PyInt(0)));
            Assert.Equal(new PyReal(28), await Call(9, "GetCashBalance", new PyInt(1), new PyInt(1000)));
            var personalJournal = Assert.IsType<PyList>(await Call(10, "GetTransactions", new PyInt(1000),
                PyNone.Instance, PyNone.Instance, PyBool.False));
            Assert.Equal(128, personalJournal.Items.Count);
            Assert.Equal(new PyReal(-40.25), Field(personalJournal.Items[0], "amount"));
            Assert.Equal(new PyReal(4_834), Field(personalJournal.Items[0], "balance"));
            Assert.Equal(new PyReal(-125.75), Field(personalJournal.Items[1], "amount"));
            Assert.Equal(new PyReal(-(double)CorporationStore.CorporationFoundingCost),
                Field(personalJournal.Items[2], "amount"));
            Assert.Equal(new PyReal(5_000), Field(personalJournal.Items[2], "balance"));

            var corporationJournal = Assert.IsType<PyList>(await Call(11, "GetTransactions", new PyInt(1000),
                PyNone.Instance, PyNone.Instance, PyBool.True));
            Assert.Equal(2, corporationJournal.Items.Count);
            Assert.Equal(new PyReal(-12.25), Field(corporationJournal.Items[0], "amount"));
            Assert.Equal(new PyReal(28), Field(corporationJournal.Items[0], "balance"));
            Assert.Equal(new PyReal(40.25), Field(corporationJournal.Items[1], "amount"));

            var month = DateTime.UtcNow;
            var monthJournal = Assert.IsType<PyList>(await Call(12, "GetTransactions", new PyInt(1000),
                new PyInt(month.Year), new PyInt(month.Month), PyBool.False));
            Assert.Equal(3 + (historicalMonth.Year == month.Year && historicalMonth.Month == month.Month ? 125 : 0),
                monthJournal.Items.Count);
            Assert.Equal(2, Assert.IsType<PyList>(await Call(13, "GetTransactions", new PyInt(1000),
                new PyInt(month.Year), new PyInt(month.Month), PyBool.True)).Items.Count);
            var historicalJournal = Assert.IsType<PyList>(await Call(16, "GetTransactions", new PyInt(1000),
                new PyInt(historicalMonth.Year), new PyInt(historicalMonth.Month), PyBool.False));
            Assert.Equal(125 + (historicalMonth.Year == month.Year && historicalMonth.Month == month.Month ? 3 : 0),
                historicalJournal.Items.Count);
            Assert.Equal(new PyReal(1), Field(historicalJournal.Items[0], "amount"));

            var corporationDivisions = Assert.IsType<PyList>(await Call(14, "GetWalletDivisionsInfo"));
            Assert.Contains(corporationDivisions.Items, row =>
                Field(row, "key") is PyInt { Value: 1000 } && Field(row, "balance") is PyReal { Value: 28 });
            session.Set("corpid", PyNone.Instance);
            var personalDivisions = Assert.IsType<PyList>(await Call(15, "GetWalletDivisionsInfo"));
            var personalDivision = Assert.Single(personalDivisions.Items);
            Assert.Equal(new PyInt(1000), Field(personalDivision, "key"));
            Assert.Equal(new PyReal(4_834), Field(personalDivision, "balance"));

            const string personalReason = "idempotent personal transfer";
            Assert.Equal(PyBool.True, await Call(50, "GiveCash", Py.Number(recipient),
                new PyReal(5), new PyStr(personalReason)));
            var personalSource = new WalletAccount(sender);
            var personalDestination = new WalletAccount(recipient);
            var personalSourceBeforeConflict = await WalletState(personalSource);
            var personalDestinationBeforeConflict = await WalletState(personalDestination);
            var changesBeforeConflict = changes.Count;
            var personalConflict = await CallError(50, "GiveCash", Py.Number(recipient),
                new PyReal(6), new PyStr(personalReason));
            Assert.Contains("CustomInfo", personalConflict);
            Assert.Contains("This transfer could not be completed.", personalConflict);
            await AssertWalletStateUnchanged(personalSource, personalSourceBeforeConflict);
            await AssertWalletStateUnchanged(personalDestination, personalDestinationBeforeConflict);
            Assert.Equal(changesBeforeConflict, changes.Count);

            const string corporationReason = "idempotent corporation transfer";
            Assert.Equal(PyNone.Instance, await Call(51, "GiveCashFromCorpAccount", Py.Number(recipient),
                new PyReal(5), new PyInt(1000), new PyStr(corporationReason)));
            var corporationAccount = new WalletAccount(corporation.Id, 1000);
            var corporationSourceBeforeConflict = await WalletState(corporationAccount);
            var corporationDestinationBeforeConflict = await WalletState(personalDestination);
            changesBeforeConflict = changes.Count;
            var corporationConflict = await CallError(51, "GiveCashFromCorpAccount", Py.Number(recipient),
                new PyReal(6), new PyInt(1000), new PyStr(corporationReason));
            Assert.Contains("CustomInfo", corporationConflict);
            Assert.Contains("This transfer could not be completed.", corporationConflict);
            await AssertWalletStateUnchanged(corporationAccount, corporationSourceBeforeConflict);
            await AssertWalletStateUnchanged(personalDestination, corporationDestinationBeforeConflict);
            Assert.Equal(changesBeforeConflict, changes.Count);

            var missingCorporationAccount = new WalletAccount(corporation.Id, 1001);
            await DeleteWallet(missingCorporationAccount);
            var missingCorporationSource = await WalletState(missingCorporationAccount);
            var corporationDestinationBeforeMissing = await WalletState(personalDestination);
            Assert.Null(missingCorporationSource.Balance);
            changesBeforeConflict = changes.Count;
            var missingCorporationWallet = await CallError(52, "GiveCashFromCorpAccount", Py.Number(recipient),
                new PyReal(1), new PyInt(1001), new PyStr("missing source wallet"));
            Assert.Contains("CustomInfo", missingCorporationWallet);
            Assert.Contains("This transfer could not be completed.", missingCorporationWallet);
            await AssertWalletStateUnchanged(missingCorporationAccount, missingCorporationSource);
            await AssertWalletStateUnchanged(personalDestination, corporationDestinationBeforeMissing);
            Assert.Equal(changesBeforeConflict, changes.Count);

            var missingPersonalDestinationAccount = new WalletAccount(walletlessRecipient);
            var missingPersonalDestination = await WalletState(missingPersonalDestinationAccount);
            var personalSourceBeforeMissing = await WalletState(personalSource);
            Assert.Null(missingPersonalDestination.Balance);
            changesBeforeConflict = changes.Count;
            var missingPersonalWallet = await CallError(53, "GiveCash", Py.Number(walletlessRecipient),
                new PyReal(1), new PyStr("missing destination wallet"));
            Assert.Contains("CustomInfo", missingPersonalWallet);
            Assert.Contains("This transfer could not be completed.", missingPersonalWallet);
            await AssertWalletStateUnchanged(missingPersonalDestinationAccount, missingPersonalDestination);
            await AssertWalletStateUnchanged(personalSource, personalSourceBeforeMissing);
            Assert.Equal(changesBeforeConflict, changes.Count);
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private static PyValue? Result(PyValue response)
    {
        var state = ((PyTuple)((PyObject)response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)state[0]).Value);
        var payload = (PyTuple)state[4];
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    private static PyValue Field(PyValue row, string name)
    {
        var fields = Assert.IsType<PyDict>(((PyObject)row).Args);
        return fields.Entries.Single(entry => ((PyStr)entry.Key).Value == name).Value;
    }

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
