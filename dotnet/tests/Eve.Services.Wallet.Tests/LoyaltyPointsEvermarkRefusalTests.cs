using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Wallet.Tests;

/// <summary>LPSvc refuses a character transfer that names the Evermark issuer before any wallet is touched; a normal issuer still transfers.</summary>
public class LoyaltyPointsEvermarkRefusalTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");
    private const long PilotId = 140000001;
    private const long CorporationId = 98000000;
    private const long ReceiverCorporationId = 98000002;
    private const long EvermarkIssuerId = 1000419; // V24.01 appConst.corpHeraldry, as LoyaltyPointsService.EvermarkIssuerCorporationId
    private const long NormalIssuerId = 1000125;

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class Db : IAsyncDisposable
    {
        private readonly string _schema = $"lpe_{Guid.NewGuid():N}"[..20];
        public NpgsqlDataSource Source { get; } = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Url!));

        public async Task<LoyaltyPointStore> StoreAsync()
        {
            await Eve.Wallet.PgWalletStore.MigrateAsync(Source, _schema);
            return new LoyaltyPointStore(Source, _schema);
        }

        public async Task<long> JournalRowsAsync()
        {
            await using var command = Source.CreateCommand($"SELECT count(*) FROM {_schema}.lp_journal");
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
            await command.ExecuteNonQueryAsync();
            await Source.DisposeAsync();
        }
    }

    private static ClientSession Pilot(Sink sink)
    {
        var session = new ClientSession(sink) { UserId = 7, CharacterId = PilotId, SessionId = 71 };
        session.Set("charid", new PyInt(PilotId));
        session.Set("corpid", new PyInt(CorporationId));
        return session;
    }

    private static ServiceCall Transfer(ClientSession session, long issuerCorporationId, long amount) => new()
    {
        Method = "TransferLPFromMyWalletToOtherCorp",
        Args = [new PyLong(ReceiverCorporationId), new PyLong(issuerCorporationId), new PyLong(amount)],
        Session = session,
        CallId = new PyInt(42),
        Kwargs = PyNone.Instance,
    };

    [LpPgFact]
    public async Task A_character_transfer_naming_the_evermark_issuer_is_refused_with_the_destination_message()
    {
        await using var db = new Db();
        var store = await db.StoreAsync();
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId), EvermarkIssuerId, 240, "mission", "evermark-refusal-seed");
        var service = new LoyaltyPointsService(store,
            (_, _) => Task.FromResult<long?>(CorporationId),
            isEligibleDestination: (_, _) => ValueTask.FromResult(true));

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(Transfer(Pilot(new Sink()), EvermarkIssuerId, 75)).AsTask());

        Assert.True(refusal.Error.IsUserError);
        Assert.Equal("CustomNotify", refusal.Error.UserErrorMessage);
        var expected = MachoErrors.UserError("CustomNotify",
            ("notify", new PyStr("That destination corporation is not valid for this loyalty point transfer."))).Payload;
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(refusal.Error.Payload));
    }

    [LpPgFact]
    public async Task The_refused_evermark_transfer_writes_no_balance_no_journal_row_and_no_announcement()
    {
        await using var db = new Db();
        var store = await db.StoreAsync();
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId), EvermarkIssuerId, 240, "mission", "evermark-refusal-balance");
        var journalBefore = await db.JournalRowsAsync();
        var announced = 0;
        store.BalanceCommitted += _ => announced++;
        var sink = new Sink();
        var service = new LoyaltyPointsService(store,
            (_, _) => Task.FromResult<long?>(CorporationId),
            isEligibleDestination: (_, _) => ValueTask.FromResult(true));

        await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Transfer(Pilot(sink), EvermarkIssuerId, 75)).AsTask());

        Assert.Equal([new LoyaltyPointBalance(EvermarkIssuerId, 240)], await store.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId)));
        Assert.Empty(await store.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, ReceiverCorporationId)));
        Assert.Equal(journalBefore, await db.JournalRowsAsync());
        Assert.Equal(0, announced);
        Assert.Empty(sink.Packets);
    }

    [LpPgFact]
    public async Task A_character_transfer_of_a_normal_issuer_still_succeeds_so_the_refusal_is_specific_to_evermark()
    {
        await using var db = new Db();
        var store = await db.StoreAsync();
        await store.ApplyAndCommitAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId), NormalIssuerId, 240, "mission", "normal-issuer-seed");
        var service = new LoyaltyPointsService(store,
            (_, _) => Task.FromResult<long?>(CorporationId),
            isEligibleDestination: (_, _) => ValueTask.FromResult(true));

        Assert.Equal(PyNone.Instance, await service.CallAsync(Transfer(Pilot(new Sink()), NormalIssuerId, 75)));

        Assert.Equal([new LoyaltyPointBalance(NormalIssuerId, 165)], await store.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, PilotId)));
        Assert.Equal([new LoyaltyPointBalance(NormalIssuerId, 75)], await store.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, ReceiverCorporationId)));
    }
}
