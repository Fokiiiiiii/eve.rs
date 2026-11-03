using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class CorporationSharesCompositionTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public TaskCompletionSource Notification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Send(PyValue packet)
        {
            lock (Packets) Packets.Add(packet);
            if (MachoPacket.Decode(packet)?.Dest.BroadcastId == "OnShareChange") Notification.TrySetResult();
        }
        public int ShareNotifications()
        {
            lock (Packets) return Packets.Count(packet => MachoPacket.Decode(packet)?.Dest.BroadcastId == "OnShareChange");
        }
    }

    [Fact]
    public async Task Host_composition_share_transfer_notifies_owner_once_and_replay_has_no_event()
    {
        var sourceUrl = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL required");
        var db = "share_host_" + Guid.NewGuid().ToString("N")[..12];
        var template = PgConnection.ForTest(sourceUrl);
        var adminUrl = new NpgsqlConnectionStringBuilder(template) { Database = "postgres" }.ConnectionString;
        var databaseUrl = new NpgsqlConnectionStringBuilder(template) { Database = db }.ConnectionString;
        var names = new[] { "EVE_PG_URL", "EVE_SIM", "EVE_MARKET_SEED", "EVE_WORMHOLES" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        HostRuntime? host = null;
        await using var admin = NpgsqlDataSource.Create(PgConnection.ForTest(adminUrl));
        try
        {
            await using (var command = admin.CreateCommand($"CREATE DATABASE {db} TEMPLATE template0")) await command.ExecuteNonQueryAsync();
            await using (var sdeSource = NpgsqlDataSource.Create(PgConnection.ForTest(databaseUrl)))
                await HostTestDatabase.AttachSdeAsync(sdeSource, new NpgsqlConnectionStringBuilder(PgConnection.ForTest(sourceUrl)));
            Environment.SetEnvironmentVariable("EVE_PG_URL", databaseUrl);
            Environment.SetEnvironmentVariable("EVE_SIM", "off");
            Environment.SetEnvironmentVariable("EVE_MARKET_SEED", "skip");
            Environment.SetEnvironmentVariable("EVE_WORMHOLES", "off");
            var settings = new HostSettings("", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" }, new AccountOptions { AutoCreate = true }, StrictRpc: true);
            host = await HostComposition.BuildAsync(settings);
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(databaseUrl));
            var characters = new PgCharacterStore(source);
            var wallets = new PgWalletStore(source);
            var store = new CorporationStore(source, characters, wallets, new WalletTransactions(source), new PgSkillStore(source), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            async Task<long> Character(string name)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var tx = await connection.BeginTransactionAsync();
                var id = await characters.InsertAsync(connection, tx, new NewCharacter(1, name + Guid.NewGuid().ToString("N"), 0, 1, 1, 1, 11, 1376, 1000044, null, 60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, tx, id, 5_000_000m, 10);
                await tx.CommitAsync(); return id;
            }
            var ownerCeo = await Character("Owner "); var issuerCeo = await Character("Issuer ");
            var owner = await store.CreateAsync(ownerCeo, new NewCorporation("Composition Share Owner", "CSOW", "", "", 0m), "owner:" + Guid.NewGuid());
            var issuer = await store.CreateAsync(issuerCeo, new NewCorporation("Composition Share Issuer", "CSIS", "", "", 0m), "issuer:" + Guid.NewGuid());
            var ownerSink = new Sink(); var issuerSink = new Sink();
            ClientSession Session(long character, long corporation, Sink sink, long id)
            {
                var session = new ClientSession(sink) { SessionId = id, CharacterId = character, ClientId = id };
                session.Set("charid", new PyLong(character)); session.Set("corpid", new PyLong(corporation));
                host.CharacterSessions!.Enter(character, session); return session;
            }
            _ = Session(ownerCeo, owner.Id, ownerSink, 11);
            var issuerSession = Session(issuerCeo, issuer.Id, issuerSink, 12);
            var dispatcher = new PacketDispatcher(host.Registry) { StrictRpc = true };
            var src = new MachoAddress(AddressKind.Client, CallId: new PyLong(88), ClientId: new PyInt(1));
            var dest = new MachoAddress(AddressKind.Node, Service: "corpRegistry", CallId: new PyLong(88), NodeId: new PyInt(MachoMessage.ProxyNodeId));
            var body = new PyTuple([PyNone.Instance, new PyStr("MoveCompanyShares"), new PyTuple([new PyLong(issuer.Id), new PyLong(owner.Id), new PyInt(125)]), new PyDict([])]);
            var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
            var request = MarshalCodec.Decode(MarshalCodec.Encode(new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
                new PyInt(MachoMessage.CallReq), src.Encode(), dest.Encode(), new PyInt(7), payload, new PyDict([]), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]))));
            Assert.True(await dispatcher.DispatchAsync(request, issuerSession));
            await ownerSink.Notification.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, ownerSink.ShareNotifications());
            async Task<long> CountEvents()
            {
                await using var command = source.CreateCommand("SELECT count(*) FROM events.outbox WHERE topic='corporation.share.changed'");
                return (long)(await command.ExecuteScalarAsync())!;
            }
            var beforeReplay = await CountEvents();
            Assert.Equal(2, beforeReplay);
            Assert.True(await dispatcher.DispatchAsync(request, issuerSession));
            Assert.Equal(beforeReplay, await CountEvents());
            Assert.Equal(1, ownerSink.ShareNotifications());
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            await using var command = admin.CreateCommand($"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
            await command.ExecuteNonQueryAsync();
        }
    }
}
