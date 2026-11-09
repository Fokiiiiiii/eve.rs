using Eve.Corporations;
using Eve.Events;
using Eve.Macho;
using Eve.Marshal;
using Eve.ReadSide;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Corporations;
using Xunit;

namespace Eve.Corporations.Tests;

public sealed class StandingMgrServiceTests
{
    [PgFact]
    public async Task Standing_history_is_scoped_to_the_active_character_and_corporation()
    {
        await using var rig = await Rig.CreateAsync();
        var (owner, corporation) = await CreateCorporationAsync(rig, "Standing Owner", "STND1");
        var (otherOwner, otherCorporation) = await CreateCorporationAsync(rig, "Other Standing Owner", "STND2");

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "standing_sched_" + suffix;
        var eventSchema = "standing_events_" + suffix;
        var relationSchema = "standing_rel_" + suffix;
        await DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
        await EventBus.MigrateAsync(rig.Source, eventSchema);
        await RelationStore.MigrateAsync(rig.Source, relationSchema);
        var scheduler = new DurableScheduler(rig.Source, () => rig.Now,
            new SchedulerOptions { Concurrency = 1 }, schedulerSchema);
        var eventBus = new EventBus(rig.Source, scheduler, null, eventSchema);
        var relations = new RelationStore(rig.Source, scheduler, eventBus, relationSchema);
        var reads = new Eve.ReadSide.ReadSide(rig.Source, new Dictionary<string, string> { ["relations"] = relationSchema });
        var service = new StandingMgrService(reads, rig.Store, () => rig.Now);
        const long npcStandingOwner = 500001;

        await relations.SetAsync("standing", npcStandingOwner, owner, 2.5,
            "{\"eventTypeID\":1,\"modification\":2.5,\"msg\":\"standing history test\"}", null, null);
        await relations.SetAsync("standing", npcStandingOwner, otherOwner, 3.5,
            "{\"eventTypeID\":1,\"modification\":3.5,\"msg\":\"other standing history test\"}", null, null);
        await relations.SetAsync("affiliation.corporation", owner, corporation.Id, 0, "standing composition test", null, null);
        await relations.SetAsync("affiliation.corporation", otherOwner, otherCorporation.Id, 0, "standing composition test", null, null);
        try
        {
            var ownerSession = Session(owner);
            var otherSession = Session(otherOwner);
            var ownHistory = Assert.IsType<PyList>(await ((IService)service).CallAsync(Call("GetStandingTransactions", ownerSession, npcStandingOwner, owner)));
            Assert.Single(ownHistory.Items);
            var otherHistory = Assert.IsType<PyList>(await ((IService)service).CallAsync(Call("GetStandingTransactions", otherSession, npcStandingOwner, owner)));
            Assert.Empty(otherHistory.Items);
            var callerHistory = Assert.IsType<PyList>(await ((IService)service).CallAsync(Call("GetStandingTransactions", ownerSession, npcStandingOwner, otherOwner)));
            Assert.Empty(callerHistory.Items);

            var ownComposition = CompositionRowset(UnwrapCached(await ((IService)service).CallAsync(
                Call("GetStandingCompositions", ownerSession, npcStandingOwner, corporation.Id))));
            var ownCompositionRow = Assert.IsType<PyPackedRow>(Assert.Single(ownComposition.List));
            Assert.Equal([new PyReal(2.5), new PyInt(owner)], ownCompositionRow.Values);
            var foreignComposition = UnwrapCached(await ((IService)service).CallAsync(
                Call("GetStandingCompositions", ownerSession, npcStandingOwner, otherCorporation.Id)));
            Assert.Empty(CompositionRowset(foreignComposition).List);
            var otherCorporationView = UnwrapCached(await ((IService)service).CallAsync(
                Call("GetStandingCompositions", otherSession, npcStandingOwner, corporation.Id)));
            Assert.Empty(CompositionRowset(otherCorporationView).List);
        }
        finally
        {
            foreach (var schema in new[] { schedulerSchema, eventSchema, relationSchema })
            {
                await using var drop = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<(long Ceo, Corporation Corporation)> CreateCorporationAsync(Rig rig, string name, string ticker)
    {
        var ceo = await rig.CharacterAsync(name);
        var corporation = await rig.Store.CreateAsync(ceo,
            new NewCorporation(name, ticker, "Standing test corporation.", "", 0.05m),
            $"standing-{ticker.ToLowerInvariant()}");
        return (ceo, corporation);
    }

    private static ServiceCall Call(string method, ClientSession session, long fromId, long toId) => new()
    {
        Method = method,
        Args = [new PyLong(fromId), new PyLong(toId)],
        Session = session,
    };

    private static ClientSession Session(long characterId) => new() { CharacterId = characterId };

    private static PyObjectEx CompositionRowset(PyValue result)
    {
        var rowset = Assert.IsType<PyObjectEx>(result);
        Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
        Assert.Empty(rowset.Dict);

        var header = Assert.IsType<PyTuple>(rowset.Header).Items;
        Assert.Equal(2, header.Count);
        var classHeader = Assert.IsType<PyTuple>(header[0]).Items;
        Assert.Equal(new PyToken("carbon.common.script.sys.crowset.CRowset"), Assert.Single(classHeader));
        var attributes = Assert.IsType<PyDict>(header[1]);
        var descriptorAttribute = Assert.Single(attributes.Entries);
        Assert.Equal(new PyStr("header"), descriptorAttribute.Key);
        var descriptorHeader = descriptorAttribute.Value;
        Assert.Equal(
            [new PackedColumn("standing", 5), new PackedColumn("ownerID", 3)],
            Descriptor.ColumnsFromHeader(descriptorHeader));

        foreach (var value in rowset.List)
        {
            var row = Assert.IsType<PyPackedRow>(value);
            Assert.Equal(descriptorHeader, row.Header);
        }
        return rowset;
    }

    private static PyValue UnwrapCached(PyValue result)
    {
        var wrapper = Assert.IsType<PyObject>(result);
        var args = Assert.IsType<PyTuple>(wrapper.Args);
        return Assert.IsType<PySubStream>(args.Items[1]).Decoded!;
    }
}
