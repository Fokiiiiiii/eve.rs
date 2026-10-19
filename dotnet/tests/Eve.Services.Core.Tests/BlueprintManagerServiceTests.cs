using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The <c>blueprintManager</c> answers: the limit, a blueprint of the caller, the owner's listing with its counts, the job link.</summary>
public sealed class BlueprintManagerServiceTests
{
    private const long Owner = 90000200;
    private const long Corporation = 98000200;
    private const long Station = 60000001;

    private static readonly BlueprintRecord Original = new(1_000_000_000_001, 681, Owner, Station, 1529, 4, false, null, 10, 20, 30000142, false);
    private static readonly BlueprintRecord Copy = new(1_000_000_000_002, 681, Owner, Station, 1529, 4, true, 5, 3, 4, 30000142, false);
    private static readonly BlueprintRecord CorpOwned = new(1_000_000_000_003, 681, Corporation, Station, 1529, 116, false, null, 0, 0, 30000142, false);

    [Fact]
    public async Task The_limit_is_the_reference_number()
    {
        var limit = Assert.IsType<PyDict>(await Service().CallAsync(Call("GetLimits", Session(Owner))));
        Assert.Equal(new PyInt(500), Assert.Single(limit.Entries).Value);
    }

    [Fact]
    public async Task A_blueprint_is_an_original_or_a_copy_by_its_quantity_and_carries_its_job()
    {
        var service = Service(job: (Original.ItemId, 77));

        var original = Fields(await service.CallAsync(Call("GetBlueprintData", Session(Owner), new PyLong(Original.ItemId))));
        Assert.Equal(new PyInt(-1), original["quantity"]);
        Assert.Equal(new PyInt(-1), original["runs"]);
        Assert.Equal(InventoryWire.Id(77), original["jobID"]);
        Assert.Equal(PyValue.Of(true), original["original"]);

        var copy = Fields(await service.CallAsync(Call("GetBlueprintData", Session(Owner), new PyLong(Copy.ItemId))));
        Assert.Equal(new PyInt(-2), copy["quantity"]);
        Assert.Equal(new PyInt(5), copy["runs"]);
        Assert.Equal(PyNone.Instance, copy["jobID"]);
        Assert.Equal(PyValue.Of(false), copy["original"]);
    }

    [Fact]
    public async Task A_blueprint_the_caller_does_not_hold_is_the_clients_own_not_found_error()
    {
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Service().CallAsync(Call("GetBlueprintData", Session(Owner + 1), new PyLong(Original.ItemId))));
        Assert.Equal("IndustryBlueprintNotFound", error.Error.UserErrorMessage);
    }

    [Fact]
    public async Task The_owners_listing_is_a_pair_of_the_list_and_the_counts_and_a_facility_filter_lists_none()
    {
        var service = Service();

        var listing = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetBlueprintDataByOwner", Session(Owner), new PyLong(Owner), PyNone.Instance)));
        Assert.Equal(2, listing.Items.Count);
        Assert.Equal(2, Assert.IsType<PyList>(listing.Items[0]).Items.Count);
        var counts = Assert.IsType<PyDict>(listing.Items[1]);
        Assert.Equal(new PyInt(2), Assert.Single(counts.Entries).Value);

        var inFacility = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetBlueprintDataByOwner", Session(Owner), new PyLong(Owner), new PyLong(Station))));
        Assert.Empty(Assert.IsType<PyList>(inFacility.Items[0]).Items);
    }

    [Fact]
    public async Task The_corporation_sees_its_own_blueprints_and_not_another_corporations()
    {
        var service = Service();
        var session = Session(Owner);
        session.Set("corpid", new PyInt(Corporation));
        session.Set("corprole", new PyLong(Eve.Corporations.CorporationRoles.FactoryManager));
        session.Set("rolesAtOther", new PyLong(1L << 21));

        var own = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetBlueprintDataByOwner", session, new PyLong(Corporation), PyNone.Instance)));
        Assert.Single(Assert.IsType<PyList>(own.Items[0]).Items);

        var other = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetBlueprintDataByOwner", session, new PyLong(Corporation + 1), PyNone.Instance)));
        Assert.Empty(Assert.IsType<PyList>(other.Items[0]).Items);
    }

    [Fact]
    public async Task Corporation_blueprint_reads_require_factory_manager_and_query_access_to_the_hangar()
    {
        var service = Service();
        var session = Session(Owner);
        session.Set("corpid", new PyInt(Corporation));

        var noRole = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetBlueprintDataByOwner", session, new PyLong(Corporation), PyNone.Instance)));
        Assert.Empty(Assert.IsType<PyList>(noRole.Items[0]).Items);
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("GetBlueprintData", session, new PyLong(CorpOwned.ItemId))));

        session.Set("corprole", new PyLong(Eve.Corporations.CorporationRoles.FactoryManager));
        var noHangarAccess = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetBlueprintDataByOwner", session, new PyLong(Corporation), PyNone.Instance)));
        Assert.Empty(Assert.IsType<PyList>(noHangarAccess.Items[0]).Items);
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("GetBlueprintData", session, new PyLong(CorpOwned.ItemId))));

        session.Set("rolesAtOther", new PyLong(1L << 21));
        var accessible = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetBlueprintDataByOwner", session, new PyLong(Corporation), PyNone.Instance)));
        Assert.Single(Assert.IsType<PyList>(accessible.Items[0]).Items);
        Assert.IsType<PyObject>(await service.CallAsync(Call("GetBlueprintData", session, new PyLong(CorpOwned.ItemId))));
    }

    private static IService Service((long Blueprint, long Job)? job = null)
    {
        var all = new[] { Original, Copy, CorpOwned };
        var jobs = job is { } entry ? new Dictionary<long, long> { [entry.Blueprint] = entry.Job } : new Dictionary<long, long>();
        return new BlueprintManagerService(
            (id, _) => ValueTask.FromResult(all.FirstOrDefault(blueprint => blueprint.ItemId == id)),
            (owner, _) => ValueTask.FromResult<IReadOnlyList<BlueprintRecord>>(all.Where(blueprint => blueprint.OwnerId == owner).ToArray()),
            (_, _) => ValueTask.FromResult<IReadOnlyDictionary<long, long>>(jobs));
    }

    private static ClientSession Session(long characterId) => new() { CharacterId = characterId };

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static Dictionary<string, PyValue> Fields(PyValue keyVal)
    {
        var dict = Assert.IsType<PyDict>(Assert.IsType<PyObject>(keyVal).Args);
        return dict.Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
    }
}
