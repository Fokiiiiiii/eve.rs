using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using Eve.Data;

namespace Eve.Services.Core.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL (see tools/dev/pg18.sh); a skipped test is not a pass.</summary>
public sealed class FittingPgFactAttribute : FactAttribute
{
    public FittingPgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>
/// The fitting managers end to end on the real saved-fitting store: the client's calls and their KeyVal shapes, the refusals the client shows, the
/// access rules of the owner scopes, and the notifications each change sends to the owner's sessions.
/// </summary>
public sealed class FittingMgrServicesTests : IAsyncLifetime
{
    private readonly List<NpgsqlDataSource> _sources = [];
    private readonly List<string> _schemas = [];

    private const long Character = 140_300_001, OtherCharacter = 140_300_002, Corporation = 98_300_001, Alliance = 99_300_001, Executor = 98_300_002;
    private const int Tengu = 29984, Ibis = 601, DamageControl = 2046;

    private sealed class Host : IFittingHost
    {
        public bool FittingManager { get; set; }
        public long? Executor { get; set; } = FittingMgrServicesTests.Executor;
        public List<ClientSession> Sessions { get; } = [];

        public Task<IReadOnlyDictionary<int, int>> CategoriesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(typeIds.Where(id => id is Tengu or Ibis or DamageControl)
                .ToDictionary(id => id, id => id == DamageControl ? 7 : 6));
        public Task<bool> HasFittingManagerRoleAsync(long characterId, CancellationToken cancellationToken) => Task.FromResult(FittingManager);
        public Task<long?> AllianceExecutorAsync(long allianceId, CancellationToken cancellationToken) => Task.FromResult(Executor);
        public IReadOnlyList<ClientSession> SessionsOf(FittingOwnerScope scope, long ownerId) => Sessions;
    }

    /// <summary>Records the packets a session sends, so a notification can be looked for by its name.</summary>
    private sealed class Sink : IPacketSink
    {
        public List<byte[]> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(MarshalCodec.Encode(packet));
        public bool Sent(string name) => Packets.Any(bytes => System.Text.Encoding.ASCII.GetString(bytes).Contains(name, StringComparison.Ordinal));
        public int IndexOf(string name) => Packets.FindIndex(bytes => System.Text.Encoding.ASCII.GetString(bytes).Contains(name, StringComparison.Ordinal));
    }

    private async Task<(PgSavedFittingStore Store, Host Host, FittingMgrCalls Calls)> SetUpAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        _sources.Add(source);
        var schema = "sfsvc_" + Guid.NewGuid().ToString("N")[..12];
        _schemas.Add(schema);
        await PgModuleStateStore.MigrateAsync(source, schema);
        var store = new PgSavedFittingStore(source, schema);
        var host = new Host();
        return (store, host, new FittingMgrCalls(store, host, () => 1_700_000_000_000));
    }

    /// <summary>Drops this test's scratch schemas (a failed drop does not fail the test), then closes its sources.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            try
            {
                await using var drop = _sources[0].CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // Leaving a scratch schema behind is better than failing the test.
            }
        }
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }
    }

    private static ClientSession SessionOf(Sink sink, long character, long corporation = 0, long alliance = 0)
    {
        var session = new ClientSession(sink) { ClientId = character };
        session.Set("charid", new PyInt(character));
        if (corporation > 0) session.Set("corpid", new PyInt(corporation));
        if (alliance > 0) session.Set("allianceid", new PyInt(alliance));
        return session;
    }

    private static PyValue Fitting(string name = "Tengu PvE", int flag = 11) => Py.KeyVal(
        ("name", new PyStr(name)),
        ("description", new PyStr("text")),
        ("shipTypeID", new PyInt(Tengu)),
        ("fitData", new PyList([Py.Tuple(new PyInt(DamageControl), new PyInt(flag), new PyInt(1))])));

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    [FittingPgFact]
    public async Task A_character_saves_lists_renames_and_deletes_its_fittings_and_is_told_of_each_change()
    {
        var (_, host, calls) = await SetUpAsync();
        var sink = new Sink();
        var session = SessionOf(sink, Character);
        host.Sessions.Add(session);
        IService service = new CharFittingMgrService(calls);

        var saved = (PyLong)await service.CallAsync(Call("SaveFitting", session, new PyInt(Character), Fitting()));
        Assert.True(saved.Value > 0);
        Assert.True(sink.Sent("OnFittingAdded"));

        var listed = Assert.IsType<PyDict>(await service.CallAsync(Call("GetFittings", session, new PyInt(Character))));
        Assert.Single(listed.Entries);
        Assert.Equal(saved.Value, ((PyLong)listed.Entries[0].Key).Value);

        await service.CallAsync(Call("UpdateNameAndDescription", session, new PyLong(saved.Value), new PyInt(Character), new PyStr("Renamed"), new PyStr("d")));
        await service.CallAsync(Call("DeleteFitting", session, new PyInt(Character), new PyLong(saved.Value)));
        Assert.True(sink.Sent("OnFittingDeleted"));
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Call("GetFittings", session, new PyInt(Character)))).Entries);
    }

    [FittingPgFact]
    public async Task A_refused_fitting_names_the_reference_user_error_and_saves_nothing()
    {
        var (_, host, calls) = await SetUpAsync();
        var session = SessionOf(new Sink(), Character);
        IService service = new CharFittingMgrService(calls);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Call("SaveFitting", session, new PyInt(Character), Fitting(flag: 2))).AsTask());
        Assert.Equal(MachoErrors.UserErrorClass, error.Error.ClassName);
        Assert.Equal("InvalidFittingDataInvalidFlag", error.Error.UserErrorMessage);
        Assert.Empty(Assert.IsType<PyDict>(await service.CallAsync(Call("GetFittings", session, new PyInt(Character)))).Entries);
    }

    [FittingPgFact]
    public async Task A_character_cannot_read_or_change_another_characters_fittings()
    {
        var (_, _, calls) = await SetUpAsync();
        var session = SessionOf(new Sink(), Character);
        IService service = new CharFittingMgrService(calls);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Call("GetFittings", session, new PyInt(OtherCharacter))).AsTask());
        Assert.Equal(MachoErrors.UserErrorClass, error.Error.ClassName);
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
    }

    [FittingPgFact]
    public async Task A_corporation_fitting_needs_the_fitting_manager_role_to_change_and_is_announced_to_the_corporation()
    {
        var (_, host, calls) = await SetUpAsync();
        var sink = new Sink();
        var member = SessionOf(sink, Character, Corporation);
        host.Sessions.Add(member);
        IService corp = new CorpFittingMgrService(calls);

        await Assert.ThrowsAsync<MachoWrappedException>(() => corp.CallAsync(Call("SaveFitting", member, new PyInt(Corporation), Fitting())).AsTask());

        host.FittingManager = true;
        var id = (PyLong)await corp.CallAsync(Call("SaveFitting", member, new PyInt(Corporation), Fitting()));
        Assert.True(id.Value > 0);
        Assert.True(sink.Sent("OnFittingAdded"));
    }

    [FittingPgFact]
    public async Task An_alliance_fitting_can_be_changed_only_by_the_executor_corporation()
    {
        var (_, host, calls) = await SetUpAsync();
        host.FittingManager = true;
        var notExecutor = SessionOf(new Sink(), Character, Corporation, Alliance);
        IService alliance = new AllianceFittingMgrService(calls);

        await Assert.ThrowsAsync<MachoWrappedException>(() => alliance.CallAsync(Call("SaveFitting", notExecutor, new PyInt(Alliance), Fitting())).AsTask());

        var executor = SessionOf(new Sink(), Character, Executor, Alliance);
        Assert.True(((PyLong)await alliance.CallAsync(Call("SaveFitting", executor, new PyInt(Alliance), Fitting()))).Value > 0);
    }

    [FittingPgFact]
    public async Task Deleting_many_answers_the_ids_that_were_there_and_announces_them()
    {
        var (_, host, calls) = await SetUpAsync();
        var sink = new Sink();
        var session = SessionOf(sink, Character);
        host.Sessions.Add(session);
        IService service = new CharFittingMgrService(calls);
        var first = ((PyLong)await service.CallAsync(Call("SaveFitting", session, new PyInt(Character), Fitting("a")))).Value;
        var second = ((PyLong)await service.CallAsync(Call("SaveFitting", session, new PyInt(Character), Fitting("b")))).Value;

        var deleted = Assert.IsType<PyList>(await service.CallAsync(Call("DeleteManyFittings", session, new PyInt(Character), new PyList([new PyLong(first), new PyLong(second), new PyLong(first)]))));
        Assert.Equal(2, deleted.Items.Count);
        Assert.True(sink.Sent("OnManyFittingsDeleted"));
    }

    [FittingPgFact]
    public async Task A_corporation_list_is_cached_for_fifteen_minutes_and_each_change_drops_the_clients_copy_before_it_is_announced()
    {
        var (_, host, calls) = await SetUpAsync();
        host.FittingManager = true;
        var sink = new Sink();
        var member = SessionOf(sink, Character, Corporation);
        host.Sessions.Add(member);
        IService corp = new CorpFittingMgrService(calls);

        var listed = await corp.CallAsync(Call("GetFittings", member, new PyInt(Corporation)));
        var listedBytes = System.Text.Encoding.ASCII.GetString(MarshalCodec.Encode(listed));
        Assert.Contains("CachedMethodCallResult", listedBytes, StringComparison.Ordinal);
        Assert.Contains("15 minutes", listedBytes, StringComparison.Ordinal);

        sink.Packets.Clear();
        await corp.CallAsync(Call("SaveFitting", member, new PyInt(Corporation), Fitting()));
        var invalidated = sink.IndexOf("InvalidateCachedMethodCall");
        var announced = sink.IndexOf("OnFittingAdded");
        Assert.True(invalidated >= 0, "the client's cached list is dropped");
        Assert.True(announced > invalidated, "the drop comes before the change is announced");
        Assert.Contains("GetFittings", System.Text.Encoding.ASCII.GetString(sink.Packets[invalidated]), StringComparison.Ordinal);
    }

    [FittingPgFact]
    public async Task A_character_list_is_not_cached_and_its_changes_send_no_cache_drop()
    {
        var (_, host, calls) = await SetUpAsync();
        var sink = new Sink();
        var session = SessionOf(sink, Character);
        host.Sessions.Add(session);
        IService service = new CharFittingMgrService(calls);
        var listed = await service.CallAsync(Call("GetFittings", session, new PyInt(Character)));
        Assert.IsType<PyDict>(listed);
        await service.CallAsync(Call("SaveFitting", session, new PyInt(Character), Fitting()));
        Assert.Equal(-1, sink.IndexOf("InvalidateCachedMethodCall"));
    }
}
