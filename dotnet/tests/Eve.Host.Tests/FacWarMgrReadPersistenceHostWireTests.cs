using Eve.Data;
using Eve.Events;
using Eve.FactionWarfare;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Scheduler;
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
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schema = "fw_host_" + suffix;
        var schedulerSchema = "sch_host_" + suffix;
        var eventSchema = "evt_host_" + suffix;
        var relationSchema = "rel_host_" + suffix;
        const long factionId = 500_001;
        const long corporationId = 1_900_000_001;
        const long allianceId = 1_900_000_002;
        const long characterId = 1_900_000_003;
        const long alliedCorporationId = 1_900_000_004;
        const long unalignedCorporationId = 1_900_000_005;
        const long solarSystemId = 3_000_014_2;
        long now = FixedNowMs;
        ClientSession? hostSession = null;
        var today = (int)(now / 86_400_000);
        var store = new FwStore(source, schema);
        try
        {
            await FwStore.MigrateAsync(source, schema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema);
            await RelationStore.MigrateAsync(source, relationSchema);
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

            // Resolve one complex and one battlefield through their PostgreSQL tickers before reading the
            // corporation statistics over the registered Host RPC route.
            long attackerFactionId = FwFactions.OccupationEnemyOf(factionId);
            var tickerSites = new FactionWarfareTickerSites(solarSystemId, characterId, attackerFactionId, corporationId);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var complexId = await store.InsertComplexAsync(connection, transaction, solarSystemId, ComplexSize.Scout, now - 300_000);
                await store.SetComplexInstanceAsync(connection, transaction, complexId, FactionWarfareTickerSites.ComplexInstanceId);
                await store.SetComplexProgressAsync(connection, transaction, complexId, 300_000, now);
                var battlefieldId = await store.InsertBattlefieldAsync(connection, transaction, solarSystemId, factionId, now);
                await store.SaveBattlefieldAsync(connection, transaction, battlefieldId, 149, now);
                for (int index = 0; index < FwBattlefields.PointCount; index++)
                    await store.SaveBattlefieldPointAsync(connection, transaction, battlefieldId, index, 10_000, attackerFactionId);
                await transaction.CommitAsync();
            }
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                _ = await FwComplexTicker.TickAsync(store, tickerSites, connection, transaction, now + 10_000, CancellationToken.None);
                await transaction.CommitAsync();
            }
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                _ = await FwBattlefieldTicker.TickAsync(store, tickerSites, NoopFwRewards.Instance, connection, transaction, now + 10_000, CancellationToken.None);
                await transaction.CommitAsync();
            }

            var persistedStore = new FwStore(source, schema);
            var scheduler = new DurableScheduler(source, () => now, new SchedulerOptions { Concurrency = 1 }, schedulerSchema);
            var bus = new EventBus(source, scheduler, null, eventSchema);
            var relations = new RelationStore(source, scheduler, bus, relationSchema);
            var warfare = new FwService(persistedStore, new LoyaltyPointStore(source), relations);
            var callbacks = new FacWarMgrCallbacks(
                ReadCharacterAsync: (_, _) => ValueTask.FromResult<FwCharacterWarfareState?>(null),
                SaveCharacterAsync: (_, _, _, _) => ValueTask.FromResult(false),
                ReadStandingAsync: (_, _, _) => ValueTask.FromResult(0d),
                NowMs: () => now,
                FactionMembersAsync: (faction, enlisted, _) =>
                {
                    if (faction == factionId)
                    {
                        Assert.Contains(corporationId, enlisted);
                        Assert.Contains(alliedCorporationId, enlisted);
                    }
                    return ValueTask.FromResult(faction == factionId
                        ? new FwFactionMembers(9, 2, 5)
                        : new FwFactionMembers(0, 0, 0));
                },
                AffiliationsAsync: _ => ValueTask.FromResult<IReadOnlyList<(long CorporationId, long AllianceId)>>(
                    [(corporationId, allianceId), (alliedCorporationId, allianceId)]));
            var service = new FacWarMgrService(warfare, callbacks);
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    hostSession = session;
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
            var factionCorporations = Assert.IsType<PyList>(await Call(3, "GetFactionCorporations", new PyLong(factionId)));
            Assert.Equal(new PyValue[] { new PyInt((int)corporationId), new PyInt((int)alliedCorporationId) }, factionCorporations.Items);
            var status = Assert.IsType<PyObject>(await Call(4, "GetCorpFactionalWarStatus"));
            var statusFields = Assert.IsType<PyDict>(status.Args);
            Assert.Contains(statusFields.Entries, entry => T(entry.Key) == "status" && entry.Value.Equals(new PyInt(FwService.ActiveStatus)));
            Assert.Contains(statusFields.Entries, entry => T(entry.Key) == "factionID" && entry.Value.Equals(new PyInt((int)factionId)));
            Assert.Contains(statusFields.Entries, entry => T(entry.Key) == "startDate" && entry.Value.Equals(new PyLong(Py.FileTimeFromUnixMs(now - 1_000))));
            Assert.Equal(new PyInt(123), await Call(5, "GetSolarSystemLPs", new PyLong(solarSystemId)));

            var characterStats = await Call(6, "GetStats_Character");
            AssertStats(characterStats, 3, 5, 12, 30, 50, 2_170);
            var corporationStats = await Call(7, "GetStats_Corp");
            AssertStats(corporationStats, 5, 9, 9, 50, 90, 2_140);
            AssertStats(await Call(8, "GetStats_Alliance"), 5, 9, 9, 50, 90, 2_140);

            var topAndAll = Assert.IsType<PyTuple>(await Call(9, "GetStats_TopAndAllKillsAndVPs"));
            Assert.Equal(2, topAndAll.Items.Count);
            var top = Assert.IsType<PyDict>(topAndAll.Items[0]);
            var all = Assert.IsType<PyDict>(topAndAll.Items[1]);
            AssertStats(top.Entries.Single(entry => entry.Key.Equals(new PyInt(1))).Value, 3, 5, 12, 30, 50, 2_170);
            AssertStats(all.Entries.Single(entry => entry.Key.Equals(new PyInt(1))).Value, 3, 5, 12, 30, 50, 2_170);
            AssertStats(top.Entries.Single(entry => entry.Key.Equals(new PyInt(2))).Value, 5, 9, 9, 50, 90, 2_140);
            AssertStats(all.Entries.Single(entry => entry.Key.Equals(new PyInt(32))).Value, 5, 9, 9, 50, 90, 2_140);

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

            hostSession!.Set("corpid", new PyLong(alliedCorporationId));
            Assert.Equal(new PyInt((int)factionId), await Call(12, "GetCorporationWarFactionID", new PyLong(alliedCorporationId)));
            var alliedStatus = Assert.IsType<PyObject>(await Call(13, "GetCorpFactionalWarStatus"));
            var alliedFields = Assert.IsType<PyDict>(alliedStatus.Args);
            Assert.Contains(alliedFields.Entries, entry => T(entry.Key) == "status" && entry.Value.Equals(new PyInt(FwService.ActiveStatus)));
            Assert.Contains(alliedFields.Entries, entry => T(entry.Key) == "factionID" && entry.Value.Equals(new PyInt((int)factionId)));
            Assert.Contains(alliedFields.Entries, entry => T(entry.Key) == "startDate" && entry.Value.Equals(new PyLong(Py.FileTimeFromUnixMs(now - 2_000))));

            // The V24.01 enlistment UI reads all three attributes even when this corporation has not applied.
            hostSession.Set("corpid", new PyLong(unalignedCorporationId));
            var unalignedStatus = Assert.IsType<PyObject>(await Call(14, "GetCorpFactionalWarStatus"));
            Assert.Equal("util.KeyVal", T(unalignedStatus.Name));
            var unalignedFields = Assert.IsType<PyDict>(unalignedStatus.Args);
            Assert.Equal(3, unalignedFields.Entries.Count);
            foreach (var field in new[] { "status", "factionID", "startDate" })
                Assert.Same(PyNone.Instance, unalignedFields.Entries.Single(entry => T(entry.Key) == field).Value);

            // A retried pending join must keep its first requested faction and effective date. The pending
            // corporation is not yet in FactionId, so checking only FactionId lets another join replace it.
            hostSession.Set("corprole", new PyInt(1));
            Assert.Same(PyNone.Instance, await Call(15, "JoinFactionAsCorporation", new PyInt((int)factionId)));
            FwEnlistment? firstRequest;
            await using (var connection = await source.OpenConnectionAsync())
                firstRequest = await persistedStore.EnlistmentAsync(connection, null, FwStore.CorporationKind, unalignedCorporationId, false);
            Assert.NotNull(firstRequest);
            Assert.Equal(factionId, firstRequest.NextFactionId);
            now += 30_000;
            Assert.Same(PyNone.Instance, await Call(16, "JoinFactionAsCorporation", new PyInt((int)factionId)));
            var retriedStatus = Assert.IsType<PyObject>(await Call(17, "GetCorpFactionalWarStatus"));
            var retriedFields = Assert.IsType<PyDict>(retriedStatus.Args);
            Assert.Contains(retriedFields.Entries, entry => T(entry.Key) == "startDate" && entry.Value.Equals(new PyLong(Py.FileTimeFromUnixMs(FixedNowMs))));

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 18, "facWarMgr", "JoinFactionAsCorporation", new PyTuple([new PyInt((int)FwFactions.GallenteFederation)]),
                Py.Dict(("machoVersion", new PyInt(1)))))));
            AssertWrappedError(Assert.IsType<Send>(await front.ReceiveAsync()), "eveexceptions.UserError", "AlreadyInMilitia");
            await using (var connection = await source.OpenConnectionAsync())
            {
                var finalRequest = await persistedStore.EnlistmentAsync(connection, null, FwStore.CorporationKind, unalignedCorporationId, false);
                Assert.Equal(firstRequest, finalRequest);
            }
        }
        finally
        {
            await using var command = source.CreateCommand(
                $"DROP SCHEMA IF EXISTS {schema} CASCADE; DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE; " +
                $"DROP SCHEMA IF EXISTS {eventSchema} CASCADE; DROP SCHEMA IF EXISTS {relationSchema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class FactionWarfareTickerSites(long systemId, long characterId, long factionId, long corporationId) : IFwComplexSites
    {
        public const long ComplexInstanceId = 5_100_001;

        public Task<long> StartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, ComplexSize size, long complexId,
            CancellationToken cancellationToken) => throw new NotSupportedException("the Host stats regression starts no complex site");

        public Task<bool> DefendersClearedAsync(long instanceId, CancellationToken cancellationToken) => Task.FromResult(instanceId == ComplexInstanceId);

        public bool PilotsIn(long solarSystemId) => false;

        public Task<IReadOnlyList<FwComplexPilot>> PilotsInsideAsync(long solarSystemId, long instanceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FwComplexPilot>>(instanceId == ComplexInstanceId
                ? [new FwComplexPilot(characterId, factionId, corporationId)]
                : []);

        public Task EndAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<long> StartBattlefieldAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, long battlefieldId,
            CancellationToken cancellationToken) => throw new NotSupportedException("the Host stats regression starts no battlefield site");

        public Task<double[]> OriginAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, CancellationToken cancellationToken) =>
            Task.FromResult<double[]>([0d, 0d, 1_000_000_000d]);

        public Task<IReadOnlyList<FwComplexPilot>> PilotsNearAsync(long solarSystemId, double[] at, double radius, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FwComplexPilot>>(solarSystemId == systemId
                ? [new FwComplexPilot(characterId, factionId, corporationId)]
                : []);
    }

    private sealed class NoopFwRewards : IFwRewards
    {
        public static NoopFwRewards Instance { get; } = new();

        public Task CreditLpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long issuerCorpId, long amount, string reference,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
