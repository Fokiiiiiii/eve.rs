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
    public async Task Faction_warfare_reads_round_trip_from_persisted_state_through_host_registration()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = $"fw_host_{Guid.NewGuid():N}"[..20];
        const long factionId = 500_001;
        const long corporationId = 1_900_000_001;
        const long allianceId = 1_900_000_002;
        const long characterId = 1_900_000_003;
        const long solarSystemId = 3_000_014_2;
        var now = FixedNowMs;
        var today = (int)(now / 86_400_000);
        var store = new FwStore(source, schema);
        try
        {
            await FwStore.MigrateAsync(source, schema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.SeedSystemsAsync(connection, transaction,
                    [new FwSystem(solarSystemId, 1, factionId, 123, 0, now)]);
                await store.SaveEnlistmentAsync(connection, transaction,
                    new FwEnlistment(FwStore.CorporationKind, corporationId, factionId, 0, 0, now - 1_000));
                await store.SaveEnlistmentAsync(connection, transaction,
                    new FwEnlistment(FwStore.AllianceKind, allianceId, factionId, 0, 0, now - 2_000));
                await store.AddPilotDailyAsync(connection, transaction, characterId, today - 1, 3, 30);
                await store.AddPilotDailyAsync(connection, transaction, characterId, today - 4, 2, 20);
                await store.AddPilotDailyAsync(connection, transaction, characterId, today - 10, 7, 70);
                await store.AddCorporationDailyAsync(connection, transaction, corporationId, today - 1, 5, 50);
                await store.AddCorporationDailyAsync(connection, transaction, corporationId, today - 4, 4, 40);
                await transaction.CommitAsync();
            }

            var persistedStore = new FwStore(source, schema);
            var warfare = new FwService(persistedStore, new LoyaltyPointStore(source));
            var callbacks = new FacWarMgrCallbacks(
                ReadCharacterAsync: (_, _) => ValueTask.FromResult<FwCharacterWarfareState?>(null),
                SaveCharacterAsync: (_, _, _, _) => ValueTask.FromResult(false),
                ReadStandingAsync: (_, _, _) => ValueTask.FromResult(0d),
                NowMs: () => now,
                FactionMembersAsync: (faction, enlisted, _) =>
                {
                    if (faction == factionId)
                        Assert.Contains(corporationId, enlisted);
                    return ValueTask.FromResult(faction == factionId
                        ? new FwFactionMembers(9, 2, 5)
                        : new FwFactionMembers(0, 0, 0));
                },
                AffiliationsAsync: _ => ValueTask.FromResult<IReadOnlyList<(long CorporationId, long AllianceId)>>(
                    [(corporationId, allianceId)]));
            var service = new FacWarMgrService(warfare, callbacks);
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.Set("charid", new PyLong(characterId));
                    session.Set("corpid", new PyLong(corporationId));
                    session.Set("allianceid", new PyLong(allianceId));
                    session.Set("solarsystemid2", new PyLong(solarSystemId));
                });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            async Task<PyValue?> Call(long id, string method, params PyValue[] args)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, id, "facWarMgr", method, new PyTuple(args), Py.Dict(("machoVersion", new PyInt(1)))))));
                return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            }

            void AssertStats(PyValue? value, params int[] expected)
            {
                var fields = Assert.IsType<PyDict>(value);
                var names = new[] { "killsY", "killsLW", "killsTotal", "vpY", "vpLW", "vpTotal" };
                Assert.Equal(names.Length, expected.Length);
                foreach (var (name, amount) in names.Zip(expected))
                    Assert.Contains(fields.Entries, entry => T(entry.Key) == name && entry.Value.Equals(new PyInt(amount)));
            }

            Assert.Equal(new PyInt((int)factionId), await Call(1, "GetCorporationWarFactionID", new PyLong(corporationId)));
            Assert.Equal(new PyInt((int)factionId), await Call(2, "GetAllianceWarFactionID", new PyInt(allianceId)));
            Assert.Equal(new PyList([new PyInt((int)corporationId)]), await Call(3, "GetFactionCorporations", new PyLong(factionId)));
            var status = Assert.IsType<PyObject>(await Call(4, "GetCorpFactionalWarStatus"));
            var statusFields = Assert.IsType<PyDict>(status.Args);
            Assert.Contains(statusFields.Entries, entry => T(entry.Key) == "status" && entry.Value.Equals(new PyInt(FwService.ActiveStatus)));
            Assert.Contains(statusFields.Entries, entry => T(entry.Key) == "factionID" && entry.Value.Equals(new PyInt((int)factionId)));
            Assert.Contains(statusFields.Entries, entry => T(entry.Key) == "startDate" && entry.Value.Equals(new PyLong(Py.FileTimeFromUnixMs(now - 1_000))));
            Assert.Equal(new PyInt(123), await Call(5, "GetSolarSystemLPs", new PyLong(solarSystemId)));

            var characterStats = await Call(6, "GetStats_Character");
            AssertStats(characterStats, 3, 5, 12, 30, 50, 120);
            var corporationStats = await Call(7, "GetStats_Corp");
            AssertStats(corporationStats, 5, 9, 9, 50, 90, 90);
            AssertStats(await Call(8, "GetStats_Alliance"), 5, 9, 9, 50, 90, 90);

            var topAndAll = Assert.IsType<PyTuple>(await Call(9, "GetStats_TopAndAllKillsAndVPs"));
            Assert.Equal(2, topAndAll.Items.Count);
            var top = Assert.IsType<PyDict>(topAndAll.Items[0]);
            var all = Assert.IsType<PyDict>(topAndAll.Items[1]);
            AssertStats(top.Entries.Single(entry => entry.Key.Equals(new PyInt(1))).Value, 3, 5, 12, 30, 50, 120);
            AssertStats(all.Entries.Single(entry => entry.Key.Equals(new PyInt(1))).Value, 3, 5, 12, 30, 50, 120);
            AssertStats(top.Entries.Single(entry => entry.Key.Equals(new PyInt(2))).Value, 5, 9, 9, 50, 90, 90);
            AssertStats(all.Entries.Single(entry => entry.Key.Equals(new PyInt(32))).Value, 5, 9, 9, 50, 90, 90);

            var militia = Assert.IsType<PyDict>(await Call(10, "GetStats_Militia"));
            var militiaData = Assert.IsType<PyDict>(militia.Entries.Single(entry => T(entry.Key) == "data").Value);
            var militiaTotal = Assert.IsType<PyDict>(militiaData.Entries.Single(entry => T(entry.Key) == "killsTotal").Value);
            Assert.Equal(new PyInt(9), militiaTotal.Entries.Single(entry => entry.Key.Equals(new PyInt((int)factionId))).Value);

            var factionInfo = Assert.IsType<PyDict>(await Call(11, "GetStats_FactionInfo"));
            var factionSummary = Assert.IsType<PyObject>(
                factionInfo.Entries.Single(entry => entry.Key.Equals(new PyInt((int)factionId))).Value);
            var factionFields = Assert.IsType<PyDict>(factionSummary.Args);
            Assert.Contains(factionFields.Entries, entry => T(entry.Key) == "totalMembersCount" && entry.Value.Equals(new PyInt(9)));
            Assert.Contains(factionFields.Entries, entry => T(entry.Key) == "systemsCount" && entry.Value.Equals(new PyInt(1)));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
