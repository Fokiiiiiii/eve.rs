using Eve.Data;
using Eve.Events;
using Eve.Marshal;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Corporations;
using Npgsql;

namespace Eve.Corporations.Tests;

/// <summary>The <c>warRegistry</c> reads and the two changes a war's owner makes: open for allies, mutual retraction.</summary>
public sealed class WarRegistryTests
{
    private const long Declarer = 9100001;
    private const long Defender = 9100002;
    private const long Bystander = 9100003;

    [PgFact]
    public async Task The_wars_of_an_owner_are_listed_and_the_negotiation_calls_are_refused_or_empty()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: true);
        var service = rig.Service();
        Assert.True(service.HasMethod("MachoResolveObject"));
        Assert.Equal(new PyInt(Eve.Macho.MachoMessage.ProxyNodeId), await service.CallAsync(new ServiceCall
        {
            Method = "MachoResolveObject", Args = [Py.Tuple(new PyInt(Declarer), new PyInt(1))], Session = Session(Declarer),
        }));

        var wars = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall { Method = "GetWars", Args = [], Session = Session(Declarer) }));
        var entry = Assert.Single(wars.Entries);
        Assert.Equal(InventoryWire.Id(war), entry.Key);
        Assert.Equal(PyNone.Instance, Field(entry.Value, "retracted"));

        var none = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall { Method = "GetWars", Args = [], Session = Session(Bystander) }));
        Assert.Empty(none.Entries);
        Assert.IsType<PyList>(await service.CallAsync(new ServiceCall { Method = "GetNegotiations", Args = [], Session = Session(Declarer) }));
        Assert.Equal(PyNone.Instance, await service.CallAsync(new ServiceCall { Method = "GetWarNegotiation", Args = [new PyInt(1)], Session = Session(Declarer) }));

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "CreateWarAllyOffer", Args = [new PyInt(war), new PyInt(0), new PyInt(Defender), new PyStr("")], Session = Session(Bystander),
        }));
        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task The_defender_opens_its_war_for_allies_and_the_declarer_cannot()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        var service = rig.Service();

        await service.CallAsync(new ServiceCall { Method = "SetOpenForAllies", Args = [new PyInt(war), PyValue.Of(true)], Session = Session(Declarer) });
        Assert.Equal(new PyInt(0), Field(await Payload(service, Declarer, war), "openForAllies"));

        await service.CallAsync(new ServiceCall { Method = "SetOpenForAllies", Args = [new PyInt(war), PyValue.Of(true)], Session = Session(Defender) });
        Assert.Equal(new PyInt(1), Field(await Payload(service, Declarer, war), "openForAllies"));
    }

    [PgFact]
    public async Task A_mutual_war_is_retracted_by_one_of_its_sides_and_a_war_that_is_not_mutual_is_left_alone()
    {
        await using var rig = await WarRig.CreateAsync();
        var mutual = await rig.DeclareAsync(Declarer, Defender, mutual: true);
        var plain = await rig.DeclareAsync(Declarer, Bystander, mutual: false);
        var service = rig.Service();

        await service.CallAsync(new ServiceCall { Method = "RetractMutualWar", Args = [new PyInt(plain)], Session = Session(Declarer) });
        Assert.Equal(PyNone.Instance, Field(await Payload(service, Declarer, plain), "retracted"));

        await service.CallAsync(new ServiceCall { Method = "RetractMutualWar", Args = [new PyInt(mutual)], Session = Session(Defender) });
        Assert.NotEqual(PyNone.Instance, Field(await Payload(service, Declarer, mutual), "retracted"));
        Assert.NotEqual(PyNone.Instance, Field(await Payload(service, Declarer, mutual), "timeFinished"));
    }

    [PgFact]
    public async Task War_changes_refuse_a_session_without_persisted_director_authority()
    {
        await using var rig = await WarRig.CreateAsync();
        var mutual = await rig.DeclareAsync(Declarer, Defender, mutual: true);
        var open = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        var service = rig.Service((_, _, _) => Task.FromResult(false));

        await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "RetractMutualWar", Args = [new PyInt(mutual)], Session = Session(Declarer),
        }).AsTask());
        await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "SetOpenForAllies", Args = [new PyInt(open), PyValue.Of(true)], Session = Session(Defender),
        }).AsTask());

        Assert.Equal(PyNone.Instance, Field(await Payload(service, Declarer, mutual), "retracted"));
        Assert.Equal(new PyInt(0), Field(await Payload(service, Declarer, open), "openForAllies"));
    }

    private static ClientSession Session(long corporationId)
    {
        var session = new ClientSession { CharacterId = corporationId + 1000 };
        session.Set("corpid", new PyInt(corporationId));
        return session;
    }

    private static PyValue Field(PyValue keyVal, string name)
    {
        var fields = Assert.IsType<PyObject>(keyVal).Args;
        return fields is PyDict dict ? dict.Entries.Single(entry => ((PyStr)entry.Key).Value == name).Value : PyNone.Instance;
    }

    private static async Task<PyValue> Payload(IService service, long corporationId, long warId)
    {
        var wars = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall { Method = "GetWars", Args = [], Session = Session(corporationId) }));
        return wars.Entries.Single(entry => entry.Key.Equals(InventoryWire.Id(warId))).Value;
    }

    /// <summary>A relations store with its scheduler and event bus, in schemas of their own.</summary>
    private sealed class WarRig : IAsyncDisposable
    {
        private readonly string[] _schemas;
        private readonly NpgsqlDataSource _source;
        private readonly RelationStore _relations;
        private long _now = 1_800_000_000_000;

        private WarRig(NpgsqlDataSource source, string[] schemas, DurableScheduler scheduler, EventBus bus)
        {
            _source = source;
            _schemas = schemas;
            _relations = new RelationStore(source, scheduler, bus, schemas[2]);
        }

        public static async Task<WarRig> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["wsch_" + id, "wevt_" + id, "wrel_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await RelationStore.MigrateAsync(source, schemas[2]);
            var scheduler = new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            return new WarRig(source, schemas, scheduler, new EventBus(source, scheduler, null, schemas[1]));
        }

        public IService Service(Func<long, long, CancellationToken, Task<bool>>? canManageWar = null) =>
            new WarRegistryService(_source, _relations, null, () => _now,
                canManageWar ?? ((_, _, _) => Task.FromResult(true)));

        /// <summary>Declares a war as the declarer does (the relation the war is kept in), with its attributes.</summary>
        public async Task<long> DeclareAsync(long declarer, long against, bool mutual)
        {
            var filetime = Py.FileTimeFromUnixMs(_now).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await _relations.SetAsync(connection, transaction, "war", declarer, against, 1.0, "declared", new
            {
                timeDeclared = filetime,
                timeStarted = filetime,
                mutual = mutual ? 1 : 0,
                openForAllies = 0,
            }, null, default);
            await transaction.CommitAsync();
            var relation = await _relations.GetAsync("war", declarer, against);
            return relation!.Id;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var command = _source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await _source.DisposeAsync();
        }
    }
}
