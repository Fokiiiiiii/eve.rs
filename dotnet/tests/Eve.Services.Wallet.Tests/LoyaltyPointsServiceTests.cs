using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Wallet.Tests;

/// <summary>LPSvc answers the client's balance rowsets and validates loyalty point transfers; the announcer tells the sessions.</summary>
/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class LpPgFactAttribute : FactAttribute
{
    public LpPgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

public class LoyaltyPointsServiceTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");
    private const long PilotId = 140000001;
    private const long CorporationId = 98000000;
    private const long IssuerId = 1000125;

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class Db : IAsyncDisposable
    {
        private readonly string _schema = $"lps_{Guid.NewGuid():N}"[..20];
        public NpgsqlDataSource Source { get; } = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Url!));

        public async Task<LoyaltyPointStore> StoreAsync()
        {
            await Eve.Wallet.PgWalletStore.MigrateAsync(Source, _schema);
            return new LoyaltyPointStore(Source, _schema);
        }

        public async ValueTask DisposeAsync()
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
            await command.ExecuteNonQueryAsync();
            await Source.DisposeAsync();
        }
    }

    private static ServiceCall Call(string method, ClientSession session) =>
        new() { Method = method, Args = [], Session = session, CallId = new PyInt(1), Kwargs = PyNone.Instance };

    private static List<PyTuple> Lines(PyValue rowset)
    {
        var obj = Assert.IsType<PyObject>(rowset);
        var dict = Assert.IsType<PyDict>(obj.Args);
        var lines = dict.Entries.Single(entry => entry.Key is PyStr { Value: "lines" }).Value;
        return Assert.IsType<PyList>(lines).Items.Cast<PyTuple>().ToList();
    }

    private static ClientSession Pilot(Sink sink, long corporation = CorporationId)
    {
        var session = new ClientSession(sink) { UserId = 7, CharacterId = PilotId, SessionId = 71 };
        session.Set("charid", new PyInt(PilotId));
        session.Set("corpid", new PyInt(corporation));
        return session;
    }

    [LpPgFact]
    public async Task The_character_and_corporation_balances_are_rowsets_of_their_real_rows()
    {
        await using var db = new Db();
        var store = await db.StoreAsync();
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId), IssuerId, 240, "mission", "char-1");
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, CorporationId), IssuerId, 880, "donation", "corp-1");
        var service = new LoyaltyPointsService(store,
            (characterId, _) => Task.FromResult<long?>(characterId == PilotId ? CorporationId : null),
            isDirector: (characterId, corporationId, _) => ValueTask.FromResult(characterId == PilotId && corporationId == CorporationId));
        var session = Pilot(new Sink());

        var character = await service.CallAsync(Call("GetAllMyCharacterWalletLPBalances", session));
        var corporation = await service.CallAsync(Call("GetAllMyCorporationWalletLPBalances", session));

        Assert.Equal(new[] { (IssuerId, 240L) }, Lines(character).Select(row => (((PyInt)row.Items[0]).Value, ((PyInt)row.Items[1]).Value)));
        Assert.Equal(new[] { (IssuerId, 880L) }, Lines(corporation).Select(row => (((PyInt)row.Items[0]).Value, ((PyInt)row.Items[1]).Value)));
    }

    [LpPgFact]
    public async Task A_stale_session_corporation_id_reads_only_the_current_directors_corporation_balance()
    {
        await using var db = new Db();
        var store = await db.StoreAsync();
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, CorporationId), IssuerId, 880, "donation", "corp-stale");
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, CorporationId + 1), IssuerId, 660, "donation", "corp-current");
        var service = new LoyaltyPointsService(store,
            (_, _) => Task.FromResult<long?>(CorporationId + 1),
            isDirector: (_, corporationId, _) => ValueTask.FromResult(corporationId == CorporationId + 1));
        var session = Pilot(new Sink());

        Assert.Equal(new[] { (IssuerId, 660L) }, Lines(await service.CallAsync(Call("GetAllMyCorporationWalletLPBalances", session)))
            .Select(row => (((PyInt)row.Items[0]).Value, ((PyInt)row.Items[1]).Value)));
    }

    [LpPgFact]
    public async Task A_non_director_receives_no_corporation_loyalty_balance()
    {
        await using var db = new Db();
        var store = await db.StoreAsync();
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, CorporationId), IssuerId, 880, "donation", "corp-no-director");
        var service = new LoyaltyPointsService(store,
            (_, _) => Task.FromResult<long?>(CorporationId),
            isDirector: (_, _, _) => ValueTask.FromResult(false));
        var session = Pilot(new Sink());

        Assert.Empty(Lines(await service.CallAsync(Call("GetAllMyCorporationWalletLPBalances", session))));
    }

    [LpPgFact]
    public async Task A_session_with_no_character_or_corporation_reads_empty_rowsets()
    {
        await using var db = new Db();
        var service = new LoyaltyPointsService(await db.StoreAsync());
        var session = new ClientSession(new Sink()) { UserId = 7 };

        Assert.Empty(Lines(await service.CallAsync(Call("GetAllMyCharacterWalletLPBalances", session))));
        Assert.Empty(Lines(await service.CallAsync(Call("GetAllMyCorporationWalletLPBalances", session))));
    }

    [LpPgFact]
    public async Task The_concord_exchange_still_refuses_with_the_reason()
    {
        await using var db = new Db();
        var service = new LoyaltyPointsService(await db.StoreAsync());
        var session = Pilot(new Sink());

        await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "ExchangeConcordLP", Args = [new PyInt(IssuerId), new PyInt(10)], Session = session, CallId = new PyInt(1), Kwargs = PyNone.Instance,
        }).AsTask());
    }

    [LpPgFact]
    public async Task A_character_transfer_updates_both_wallets_once_and_invalidates_both_client_caches()
    {
        await using var db = new Db();
        var store = await db.StoreAsync();
        const long receiverCorporationId = 98000002;
        const long receiverCharacterId = 140000002;
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId), IssuerId, 240, "mission", "donation-seed");
        var sink = new Sink();
        var pilot = Pilot(sink);
        var receiverMember = new ClientSession(sink) { UserId = 8, CharacterId = receiverCharacterId };
        receiverMember.Set("corpid", new PyInt(receiverCorporationId));
        var sessions = new CharacterSessions();
        sessions.Enter(PilotId, pilot);
        sessions.Enter(receiverCharacterId, receiverMember);
        using var announcer = new LoyaltyPointAnnouncer(store, sessions);
        var service = new LoyaltyPointsService(store,
            (_, _) => Task.FromResult<long?>(CorporationId),
            isEligibleDestination: (corporationId, _) => ValueTask.FromResult(corporationId == receiverCorporationId));
        var call = new ServiceCall
        {
            Method = "TransferLPFromMyWalletToOtherCorp",
            Args = [new PyLong(receiverCorporationId), new PyLong(IssuerId), new PyLong(75)],
            Session = pilot,
            CallId = new PyInt(42),
            Kwargs = PyNone.Instance,
        };

        Assert.Equal(PyNone.Instance, await service.CallAsync(call));
        Assert.Equal(PyNone.Instance, await service.CallAsync(call));

        Assert.Equal([new LoyaltyPointBalance(IssuerId, 165)], await store.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId)));
        Assert.Equal([new LoyaltyPointBalance(IssuerId, 75)], await store.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, receiverCorporationId)));
        Assert.Equal(2, sink.Packets.Count);
        var expectedCharacter = ClientNotification.Build(7, pilot.NodeId, "OnLPChange", "clientID",
            [new PyStr("set"), new PyInt(IssuerId), new PyInt(240), new PyInt(165)], 0);
        var expectedCorporation = ClientNotification.Build(8, receiverMember.NodeId, "OnCorpLPChange", "clientID",
            [new PyStr("transfer_in"), new PyInt(IssuerId)], 0);
        Assert.Equal(MarshalCodec.Encode(expectedCharacter), MarshalCodec.Encode(sink.Packets[0]));
        Assert.Equal(MarshalCodec.Encode(expectedCorporation), MarshalCodec.Encode(sink.Packets[1]));
    }

    [LpPgFact]
    public async Task A_committed_change_is_told_to_the_character_and_to_every_member_of_the_corporation()
    {
        await using var db = new Db();
        var store = await db.StoreAsync();
        var pilotSink = new Sink();
        var memberSink = new Sink();
        var outsiderSink = new Sink();
        // The pilot works for another corporation, so only the character is told about the character's balance.
        var pilot = Pilot(pilotSink, CorporationId + 1);
        var member = new ClientSession(memberSink) { UserId = 8, CharacterId = 140000002 };
        member.Set("corpid", new PyInt(CorporationId));
        var outsider = new ClientSession(outsiderSink) { UserId = 9, CharacterId = 140000003 };
        outsider.Set("corpid", new PyInt(CorporationId + 2));
        var sessions = new CharacterSessions();
        sessions.Enter(PilotId, pilot);
        sessions.Enter(140000002, member);
        sessions.Enter(140000003, outsider);
        using var announcer = new LoyaltyPointAnnouncer(store, sessions);

        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId), IssuerId, 240, "mission", "char-award");
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, CorporationId), IssuerId, 880, "donation", "corp-award");

        var expectedCharacter = ClientNotification.Build(7, pilot.NodeId, "OnLPChange", "clientID",
            [new PyStr("set"), new PyInt(IssuerId), new PyInt(0), new PyInt(240)], 0);
        var expectedCorporation = ClientNotification.Build(8, member.NodeId, "OnCorpLPChange", "clientID",
            [new PyStr("donation"), new PyInt(IssuerId)], 0);
        Assert.Equal(Eve.Marshal.MarshalCodec.Encode(expectedCharacter), Eve.Marshal.MarshalCodec.Encode(Assert.Single(pilotSink.Packets)));
        Assert.Equal(Eve.Marshal.MarshalCodec.Encode(expectedCorporation), Eve.Marshal.MarshalCodec.Encode(Assert.Single(memberSink.Packets)));
        Assert.Single(pilotSink.Packets);
        Assert.Empty(outsiderSink.Packets);
    }
}
