using Eve.Data;
using Eve.FactionWarfare;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Solar_system_lp_donation_debits_the_wallet_and_persists_the_pool_through_host()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var fwSchema = "fw_" + suffix;
        var walletSchema = "wal_" + suffix;
        const long characterId = 1_900_001_003;
        const long corporationId = 1_900_001_004;
        const long factionId = FwFactions.AmarrEmpire;
        var now = FixedNowMs;
        var systems = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar);
        var systemId = systems[0];
        var fwStore = new FwStore(source, fwSchema);
        var loyalty = new LoyaltyPointStore(source, walletSchema);
        try
        {
            await FwStore.MigrateAsync(source, fwSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await fwStore.SeedSystemsAsync(connection, transaction,
                    systems.Select(id => new FwSystem(id, FwWarzones.AmarrMinmatar, factionId, 0, 0, now)));
                await loyalty.ApplyAsync(connection, transaction,
                    new LoyaltyHolder(LoyaltyHolderKind.Character, characterId),
                    FwFactions.MilitiaCorporationOf(factionId), 1_000, "test.seed", $"fw-seed:{suffix}");
                await transaction.CommitAsync();
            }

            var persistedLoyalty = new LoyaltyPointStore(source, walletSchema);
            var service = new FacWarMgrService(
                new FwService(new FwStore(source, fwSchema), persistedLoyalty),
                new FacWarMgrCallbacks(
                    ReadCharacterAsync: (_, _) => ValueTask.FromResult<FwCharacterWarfareState?>(null),
                    SaveCharacterAsync: (_, _, _, _) => ValueTask.FromResult(false),
                    ReadStandingAsync: (_, _, _) => ValueTask.FromResult(0d),
                    NowMs: () => now));
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.Set("charid", new PyLong(characterId));
                    session.Set("corpid", new PyLong(corporationId));
                    session.Set("warfactionid", new PyInt((int)factionId));
                    session.Set("solarsystemid2", new PyLong(systemId));
                });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            async Task<Send> Donate(long id, long totalLp, long hubLp)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, id, "facWarMgr", "DonateLPsToSolarSystem",
                    new PyTuple([new PyLong(totalLp), new PyLong(hubLp)]),
                    Py.Dict(("machoVersion", new PyInt(1)))))));
                return Assert.IsType<Send>(await front.ReceiveAsync());
            }

            var staleQuote = await Donate(1, 100, 100);
            AssertWrappedError(staleQuote, "eveexceptions.UserError", "FacWarDonationTaxChanged");
            var staleWire = System.Text.Encoding.ASCII.GetString(staleQuote.Payload.Span);
            Assert.Contains("taxNow", staleWire);
            Assert.Equal(1_000, (await persistedLoyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, characterId)))
                .Single().Amount);
            Assert.Equal(0, await new FwService(new FwStore(source, fwSchema), persistedLoyalty).SolarSystemLpsAsync(systemId));

            var accepted = await Donate(2, 100, 17);
            Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(accepted.Payload)));
            Assert.Equal(900, (await persistedLoyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, characterId)))
                .Single().Amount);
            Assert.Equal(17, await new FwService(new FwStore(source, fwSchema), persistedLoyalty).SolarSystemLpsAsync(systemId));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {fwSchema} CASCADE; DROP SCHEMA IF EXISTS {walletSchema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
