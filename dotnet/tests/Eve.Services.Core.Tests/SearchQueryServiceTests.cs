using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class SearchQueryServiceTests
{
    [Fact]
    public async Task Query_returns_category_to_id_lists_for_the_client_search_categories()
    {
        var locations = new Dictionary<int, IReadOnlyList<LookupLocation>>
        {
            [3] = [new(801, "Alpha Region", 3, 3)],
            [4] = [new(601, "Alpha Constellation", 4, 4)],
            [5] = [new(701, "Alpha Solar System", 5, 5)],
            [15] = [new(901, "Alpha Station", 1531, 15)],
        };
        var directory = new LookupSearchSources(
            Characters: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(201, "Alpha Pilot"), new(202, "Alpha")]),
            Corporations: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(301, "Alpha Corporation")]),
            Alliances: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Factions: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Locations: (groupId, _) => Task.FromResult(locations.GetValueOrDefault(groupId, [])),
            Types: _ => Task.FromResult<IReadOnlyList<LookupType>>([new(1001, "Alpha Item")]));
        var service = new SearchQueryService(directory);
        var groups = new PyList([new PyInt(2), new PyInt(3), new PyInt(6), new PyInt(7), new PyInt(8), new PyInt(9), new PyInt(10)]);

        var response = Assert.IsType<PyDict>(await service.CallAsync(Request("alpha", groups)));
        var roundTrip = Assert.IsType<PyDict>(MarshalCodec.Decode(MarshalCodec.Encode(response)));

        Assert.Equal([202L, 201L], Ids(roundTrip, 2));
        Assert.Equal([301L], Ids(roundTrip, 3));
        Assert.Equal([601L], Ids(roundTrip, 6));
        Assert.Equal([701L], Ids(roundTrip, 7));
        Assert.Equal([801L], Ids(roundTrip, 8));
        Assert.Equal([901L], Ids(roundTrip, 9));
        Assert.Equal([1001L], Ids(roundTrip, 10));

        var exact = Assert.IsType<PyDict>(await service.CallAsync(Request("alpha", groups, exact: 2)));
        Assert.Equal([202L], Ids(exact, 2));
        Assert.Empty(Ids(exact, 3));
    }

    [Fact]
    public async Task QuickQuery_returns_a_unique_flat_list_and_honors_alias_and_npc_filters()
    {
        var directory = new LookupSearchSources(
            Characters: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Corporations: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([
                new(301, "Alpha Corporation", TickerName: "ALP"),
                new(302, "Alpha NPC Corporation", TickerName: "NPC", IsNpc: true),
            ]),
            Alliances: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
            Factions: _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]));
        var service = new SearchQueryService(directory);
        var request = new ServiceCall
        {
            Method = "QuickQuery",
            Args = [new PyStr("alpha"), new PyList([new PyInt(3), new PyInt(3)])],
            Kwargs = Py.Dict(("hideNPC", PyBool.True)),
            Session = new ClientSession(),
        };

        var result = Assert.IsType<PyList>(await service.CallAsync(request));
        Assert.Equal([301L], result.Items.Select(Id));

        var aliasRequest = new ServiceCall
        {
            Method = "QuickQuery",
            Args = [new PyStr("ALP"), new PyList([new PyInt(3)])],
            Kwargs = Py.Dict(("exact", new PyInt(-1)), ("onlyAltName", PyBool.True)),
            Session = new ClientSession(),
        };
        result = Assert.IsType<PyList>(await service.CallAsync(aliasRequest));
        Assert.Equal([301L], result.Items.Select(Id));

        static long Id(PyValue value) => Assert.IsType<PyInt>(value).Value;
    }

    private static ServiceCall Request(string search, PyList groups, int exact = 0) => new()
    {
        Method = "Query",
        Args = [new PyStr(search), groups],
        Kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("exact"), new PyInt(exact)),
        ]),
        Session = new ClientSession(),
    };

    private static long[] Ids(PyDict response, long groupId) =>
        Assert.IsType<PyList>(response.Entries.Single(entry => entry.Key == new PyInt(groupId)).Value)
            .Items.Select(value => value switch
            {
                PyInt number => number.Value,
                PyLong number => number.Value,
                _ => throw new InvalidOperationException("query result IDs must be integers"),
            })
            .ToArray();
}
