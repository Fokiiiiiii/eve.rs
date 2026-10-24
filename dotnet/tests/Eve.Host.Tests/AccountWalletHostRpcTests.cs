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
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await CorporationStore.MigrateAsync(source, corporationSchema);

            var characters = new PgCharacterStore(source, characterSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var transactions = new WalletTransactions(source, walletSchema);
            long sender;
            long recipient;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                async Task<long> CreatePilot(string name)
                {
                    var id = await characters.InsertAsync(connection, transaction,
                        new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                            60000001, 30000001, 20000001, 10000001, null, null));
                    await wallets.OpenAsync(connection, transaction, id, CorporationStore.CorporationFoundingCost + 5_000m, 0);
                    return id;
                }

                sender = await CreatePilot($"Wallet RPC Sender {suffix}");
                recipient = await CreatePilot($"Wallet RPC Recipient {suffix}");
                await transaction.CommitAsync();
            }

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
            var dispatcher = new PacketDispatcher(registry);
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

            void AssertNotifications(int expectedCount)
            {
                var packets = capture.Sent.Select(packet =>
                    Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
                Assert.Equal(expectedCount + 1, packets.Length);
                Assert.All(packets[..expectedCount], packet =>
                {
                    Assert.Equal(MachoMessage.Notification, packet.Type);
                    Assert.Equal("OnAccountChange", packet.Dest.BroadcastId);
                    Assert.Equal("clientID", packet.Dest.IdType);
                });
                Assert.Equal(MachoMessage.CallRsp, packets[^1].Type);
            }

            Assert.Equal(new PyReal(5_000), await Call(1, "GetCashBalance", new PyInt(0)));
            Assert.Equal(new PyReal(0), await Call(2, "GetCashBalance", new PyInt(1), new PyInt(1000)));

            Assert.Equal(PyBool.True, await Call(3, "GiveCash", Py.Number(recipient),
                new PyReal(125.75), new PyStr("private gift")));
            AssertNotifications(1);
            Assert.Equal(new PyReal(4_874.25), await Call(4, "GetCashBalance", new PyInt(0)));

            Assert.Equal(PyNone.Instance, await Call(5, "GiveCash", Py.Number(corporation.Id),
                new PyReal(40.25), new PyStr("corporation deposit")));
            AssertNotifications(2);
            Assert.Equal(new PyReal(40.25), await Call(6, "GetCashBalance", new PyInt(1), new PyInt(1000)));

            Assert.Equal(PyNone.Instance, await Call(7, "GiveCashFromCorpAccount", Py.Number(recipient),
                new PyReal(12.25), new PyInt(1000), new PyStr("corporation payout")));
            AssertNotifications(1);
            Assert.Equal(6, changes.Count);
            Assert.Equal(4_834m, (await wallets.GetAsync(sender))!.Isk);
            Assert.Equal(5_138m, (await wallets.GetAsync(recipient))!.Isk);
            Assert.Equal(28m, await transactions.AccountBalanceAsync(new WalletAccount(corporation.Id, 1000)));
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

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
