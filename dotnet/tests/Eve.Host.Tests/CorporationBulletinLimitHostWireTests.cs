using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Corporations;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;
using System.Text;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Corporation_bulletin_limit_is_a_client_user_error_over_the_host()
    {
        var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var corporationSchema = "host_bulletin_c_" + suffix;
        var characterSchema = "host_bulletin_h_" + suffix;
        var walletSchema = "host_bulletin_w_" + suffix;
        var skillSchema = "host_bulletin_s_" + suffix;
        try
        {
            await CorporationStore.MigrateAsync(source, corporationSchema);
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            var characters = new PgCharacterStore(source, characterSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var transactions = new WalletTransactions(source, walletSchema);
            long actor;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                actor = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(1, "Bulletin Host Founder", 0, 1, 1, 1, 11, 1376,
                        1000044, null, 60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, transaction, actor, 5_000_000_000m, 10);
                await transaction.CommitAsync();
            }
            var store = new CorporationStore(source, characters, wallets, transactions, skills, () => FixedNowMs, corporationSchema);
            var corporation = await store.CreateAsync(actor, new("Bulletin Host Corp", "BHC", "", "", 0), "host-bulletin-limit");

            Start(
                configure: registry => registry.Register(new CorpRegistryService(store, characters, () => FixedNowMs)),
                sessionOpened: session =>
                {
                    session.CharacterId = actor;
                    session.Set("charid", new PyInt(actor));
                });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            await front.ReceiveAsync();

            for (var index = 0; index < 10; index++)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, index + 1,
                    "corpRegistry", "AddBulletin", new PyWString($"Bulletin {index}"), new PyWString("Body")))));
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                Assert.IsType<PyInt>(Result(MarshalCodec.Decode(reply.Payload)));
            }

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, 11,
                "corpRegistry", "AddBulletin", new PyWString("Eleventh"), new PyWString("Body")))));
            var error = Assert.IsType<Send>(await front.ReceiveAsync());
            AssertWrappedError(error, "eveexceptions.UserError", "CorpTooManyBulletins");
            Assert.Contains("CorpTooManyBulletins", Encoding.ASCII.GetString(error.Payload.Span));
            Assert.Equal(10, (await store.CorporationBulletinsAsync(corporation.Id)).Count);
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"""
                DROP SCHEMA IF EXISTS {corporationSchema} CASCADE;
                DROP SCHEMA IF EXISTS {characterSchema} CASCADE;
                DROP SCHEMA IF EXISTS {walletSchema} CASCADE;
                DROP SCHEMA IF EXISTS {skillSchema} CASCADE
                """);
            await cleanup.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }
}
