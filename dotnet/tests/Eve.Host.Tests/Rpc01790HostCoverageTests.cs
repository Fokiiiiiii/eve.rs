using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using System.Text.Json.Nodes;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Lookup_machoNet_and_moon_read_calls_use_registered_host_routes_with_macho_version_kwargs()
    {
        static Task<IReadOnlyList<LookupEntity>> Entities(params LookupEntity[] rows) =>
            Task.FromResult<IReadOnlyList<LookupEntity>>(rows);
        var lookup = new LookupSearchSources(
            _ => Entities(new LookupEntity(900001, "Alpha Pilot", CorporationId: 980001)),
            _ => Entities(new LookupEntity(980001, "Beta Corp")),
            _ => Entities(),
            _ => Entities(),
            (group, _) => Task.FromResult<IReadOnlyList<LookupLocation>>(
                [new LookupLocation(30000001, "Alpha", 5, group)]));

        var moonState = JsonNode.Parse("""
            {
              "extractions": {
                "1": {
                  "extractionID": 1,
                  "structureID": 30000142,
                  "moonID": 40000001,
                  "solarSystemID": 30000142,
                  "ownerID": 980001,
                  "startMoveTimeMs": 1700000000000,
                  "chunkAvailableTimeMs": 1700518460000,
                  "naturalDecayTimeMs": 1700529260000,
                  "durationSeconds": 518460,
                  "yieldMultiplier": 1.0,
                  "calendarEventID": 42,
                  "state": "active"
                },
                "2": {
                  "extractionID": 2,
                  "structureID": 30000143,
                  "moonID": 40000002,
                  "solarSystemID": 30000143,
                  "ownerID": 980002,
                  "startMoveTimeMs": 1700000000000,
                  "chunkAvailableTimeMs": 1700518461000,
                  "naturalDecayTimeMs": 1700529261000,
                  "durationSeconds": 518460,
                  "yieldMultiplier": 0.75,
                  "calendarEventID": 43,
                  "state": "active"
                },
                "3": {
                  "extractionID": 3,
                  "structureID": 30000144,
                  "moonID": 40000003,
                  "solarSystemID": 30000144,
                  "ownerID": 980001,
                  "startMoveTimeMs": 1700000000000,
                  "chunkAvailableTimeMs": 1700518462000,
                  "naturalDecayTimeMs": 1700529262000,
                  "durationSeconds": 518460,
                  "yieldMultiplier": 1.0,
                  "calendarEventID": 44,
                  "state": "completed"
                }
              },
              "resourcesByStructureID": {
                "30000142": { "resources": { "34": 0.75 } },
                "30000143": { "resources": { "35": 0.5 } }
              }
            }
            """)!.AsObject();

        Start(configure: registry =>
        {
            registry.Register(CoreServices.CreateLookupService(lookup));
            registry.Register(new MoonExtractionsRpcService(() => Task.FromResult<JsonObject?>(moonState)));
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        var machoVersion = new PyTuple([new PyInt(0), new PyInt(3396210)]);
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), machoVersion),
        ]);
        async Task<PyValue?> Call(long id, string service, string method, params PyValue[] args)
        {
            var packet = Packets.CallWithKwargs(
                open, id, service, method, new PyTuple(args), kwargs);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(packet)));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(response.Payload));
        }

        var characters = Assert.IsType<PyList>(await Call(
            1, "lookupSvc", "LookupEvePlayerCharacters", new PyStr("Alpha"), new PyInt(0)));
        var character = Assert.IsType<PyObject>(Assert.Single(characters.Items));
        Assert.Equal("util.KeyVal", Assert.IsType<PyStr>(character.Name).Value);

        var locations = Assert.IsType<PyList>(await Call(
            2, "lookupSvc", "LookupKnownLocationsByGroup", new PyInt(29), new PyStr("Alpha")));
        Assert.Equal("util.KeyVal", Assert.IsType<PyStr>(Assert.IsType<PyObject>(Assert.Single(locations.Items)).Name).Value);

        var accountOwners = Assert.IsType<PyList>(await Call(
            3, "lookupSvc", "LookupNoneNPCAccountOwners", new PyStr("Beta"), new PyInt(0)));
        Assert.Single(accountOwners.Items);
        Assert.Equal("util.KeyVal", Assert.IsType<PyStr>(Assert.IsType<PyObject>(accountOwners.Items[0]).Name).Value);

        var globalConfig = Assert.IsType<PyDict>(await Call(4, "machoNet", "GetGlobalConfig"));
        Assert.Contains(globalConfig.Entries, entry =>
            T(entry.Key) == "imageserverurl" && T(entry.Value) == "http://127.0.0.1:26001/");
        Assert.Contains(globalConfig.Entries, entry =>
            T(entry.Key) == "defaultPortraitSaveSize" && entry.Value is PyInt { Value: 1024 });

        var statistics = Assert.IsType<PyTuple>(await Call(
            5, "machoNet", "GetClusterGameStatisticsForClient",
            new PyStr("EVE"), new PyTuple([new PyDict([]), new PyDict([]), new PyInt(0)])));
        Assert.Equal(3, statistics.Items.Count);
        Assert.Empty(Assert.IsType<PyDict>(statistics.Items[0]).Entries);
        Assert.Empty(Assert.IsType<PyDict>(statistics.Items[1]).Entries);
        Assert.Equal(new PyInt(1), statistics.Items[2]);

        var fallback = new PyTuple([new PyDict([]), new PyDict([]), new PyInt(0)]);
        Assert.Equal(fallback, await Call(
            6, "machoNet", "GetClusterGameStatisticsForClient", new PyStr("OTHER"), fallback));

        var extractions = Assert.IsType<PyList>(await Call(
            7, "moonExtractions", "GetExtractionsForCorp", new PyInt(980001)));
        Assert.Single(extractions.Items);
        Assert.Single(Assert.IsType<PyList>(await Call(
            8, "moonExtractions", "GetExtractionsForCorp", new PyInt(980002))).Items);
        Assert.Empty(Assert.IsType<PyList>(await Call(
            9, "moonExtractions", "GetExtractionsForCorp", new PyInt(980003))).Items);

        var resources = Assert.IsType<PyDict>(await Call(
            10, "moonExtractions", "GetMoonResourcesForStructure", new PyInt(30000142)));
        Assert.Single(resources.Entries);
        Assert.Equal(new PyInt(34), resources.Entries[0].Key);
        var otherResources = Assert.IsType<PyDict>(await Call(
            11, "moonExtractions", "GetMoonResourcesForStructure", new PyInt(30000143)));
        Assert.Equal(new PyInt(35), Assert.Single(otherResources.Entries).Key);
    }
}
