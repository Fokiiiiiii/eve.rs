using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eve.Host.Tests;

public sealed class OperationalRpcServiceTests
{
    private static ClientSession Session(long characterId = 20, long shipId = 400, long systemId = 30000142)
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));
        session.Set("corpid", new PyInt(30));
        session.Set("shipid", new PyInt(shipId));
        session.Set("solarsystemid2", new PyInt(systemId));
        return session;
    }

    private static ServiceCall Call(string method, ClientSession? session = null, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session ?? Session(),
    };

    [Fact]
    public async Task Get_all_development_indices_returns_the_sorted_keyval_list()
    {
        IService service = new DevIndexManagerRpcService(_ => ValueTask.FromResult<IReadOnlyList<DevelopmentIndexRecord>>(
        [
            new(30000002, 7, 8, 9),
            new(30000001, 4, 5, 6),
        ]));

        var actual = await service.CallAsync(Call("GetAllDevelopmentIndices"));
        var expected = Py.List([
            Py.KeyVal(
                ("solarSystemID", new PyInt(30000001)),
                ("militaryPoints", new PyInt(4)),
                ("industrialPoints", new PyInt(5)),
                ("claimedFor", new PyInt(6))),
            Py.KeyVal(
                ("solarSystemID", new PyInt(30000002)),
                ("militaryPoints", new PyInt(7)),
                ("industrialPoints", new PyInt(8)),
                ("claimedFor", new PyInt(9))),
        ]);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Sovereignty_read_model_maps_the_persisted_development_indices()
    {
        var state = JsonNode.Parse("""
            {
              "systems": {
                "30000002": {
                  "solarSystemID": 30000002,
                  "devIndices": { "militaryPoints": 7, "industrialPoints": 8, "claimedForDays": 9 }
                },
                "30000001": {
                  "devIndices": { "militaryPoints": 4, "industrialPoints": 5, "claimedFor": 6 }
                }
              }
            }
            """)!.AsObject();
        var readModel = new PersistenceSovFwReadModel(
            (_, _) => Task.FromResult(state),
            (_, _) => throw new InvalidOperationException("affiliation is not used for this read"));

        var records = await readModel.ReadAllDevelopmentIndicesAsync(CancellationToken.None);

        Assert.Equal(
            [
                new DevelopmentIndexRecord(30000001, 4, 5, 6),
                new DevelopmentIndexRecord(30000002, 7, 8, 9),
            ],
            records);
    }

    [Fact]
    public async Task Launch_from_wreck_forwards_source_and_item_then_returns_none()
    {
        long? forwardedSource = null;
        long? forwardedItem = null;
        IService service = new EjectRpcService((_, sourceId, itemId, _) =>
        {
            forwardedSource = sourceId;
            forwardedItem = itemId;
            return ValueTask.CompletedTask;
        });

        var actual = await service.CallAsync(Call(
            "LaunchShipOrContainerFromWreckOrContainer",
            args: [new PyLong(900), new PyLong(901)]));

        Assert.IsType<PyNone>(actual);
        Assert.Equal(900, forwardedSource);
        Assert.Equal(901, forwardedItem);
    }

    [Fact]
    public async Task Launch_from_wreck_without_a_space_backend_returns_none()
    {
        IService service = new EjectRpcService();

        Assert.IsType<PyNone>(await service.CallAsync(Call(
            "LaunchShipOrContainerFromWreckOrContainer",
            args: [new PyLong(900), new PyLong(901)])));
    }

    [Fact]
    public async Task Decode_item_returns_only_an_owned_supported_resource_url()
    {
        var item = new Item(55, 123, 20, 400, 0, 1, true, null,
            CustomInfo: """{"evejsEncodedItemUrl":"https://assets.example/item.png"}""");
        IService service = new EncodedItemsRpcService(
            (_, _) => ValueTask.FromResult<Item?>(item),
            (_, candidate, _) => ValueTask.FromResult(candidate.TypeId == 123));

        var actual = await service.CallAsync(Call("DecodeItem", args: [new PyLong(55)]));

        Assert.Equal(new PyStr("https://assets.example/item.png"), actual);
    }

    [Fact]
    public async Task Decode_item_refuses_other_owners_before_consulting_resource_backends()
    {
        var item = new Item(55, 123, 99, 400, 0, 1, true, null);
        var backendCalled = false;
        IService service = new EncodedItemsRpcService(
            (_, _) => ValueTask.FromResult<Item?>(item),
            (_, _, _) =>
            {
                backendCalled = true;
                return ValueTask.FromResult(true);
            });

        var actual = await service.CallAsync(Call("DecodeItem", args: [new PyLong(55)]));

        Assert.IsType<PyNone>(actual);
        Assert.False(backendCalled);
    }

    [Fact]
    public void Encoded_item_type_list_checks_inclusions_and_exclusions()
    {
        using var document = JsonDocument.Parse(
            """{"includedGroupIDs":[10],"excludedTypeIDs":[123]}""");

        Assert.True(EncodedItemsRpcService.IsInTypeList(
            document.RootElement, new ItemType(456, 10, 1)));
        Assert.False(EncodedItemsRpcService.IsInTypeList(
            document.RootElement, new ItemType(123, 10, 1)));
        Assert.False(EncodedItemsRpcService.IsInTypeList(
            document.RootElement, new ItemType(456, 11, 1)));
    }

    [Fact]
    public async Task Compress_item_in_space_returns_the_six_value_reference_tuple()
    {
        var item = new Item(55, 123, 20, 400, 5, 40, false, null);
        IService service = new InSpaceCompressionMgrRpcService(
            (_, _) => ValueTask.FromResult<Item?>(item),
            (_, current, facilityId, _) =>
            {
                Assert.Equal(item.Id, current.Id);
                Assert.Equal(901, facilityId);
                return ValueTask.FromResult<SpaceCompressionResult?>(new(55, 123, 40, 55, 456, 40));
            });

        var actual = await service.CallAsync(Call(
            "CompressItemInSpace",
            args: [new PyLong(55), new PyLong(901)]));

        Assert.Equal(Py.Tuple(
            new PyLong(55),
            new PyInt(123),
            new PyLong(40),
            new PyLong(55),
            new PyInt(456),
            new PyLong(40)), actual);
    }

    [Fact]
    public async Task Compress_item_in_space_refuses_items_outside_the_active_ship()
    {
        var item = new Item(55, 123, 20, 401, 5, 40, false, null);
        var backendCalled = false;
        IService service = new InSpaceCompressionMgrRpcService(
            (_, _) => ValueTask.FromResult<Item?>(item),
            (_, _, _, _) =>
            {
                backendCalled = true;
                return ValueTask.FromResult<SpaceCompressionResult?>(new(55, 123, 40, 55, 456, 40));
            });

        var notice = await RefusalNoticeAsync(service, [new PyLong(55), new PyLong(901)]);

        Assert.Equal(InSpaceCompressionMgrRpcService.NotInActiveShipNotice, notice);
        Assert.False(backendCalled);
    }

    [Fact]
    public async Task Compress_item_in_space_refuses_a_request_without_a_facility_instead_of_answering_none()
    {
        var item = new Item(55, 123, 20, 400, 5, 40, false, null);
        var backendCalled = false;
        IService service = new InSpaceCompressionMgrRpcService(
            (_, _) => ValueTask.FromResult<Item?>(item),
            (_, _, _, _) =>
            {
                backendCalled = true;
                return ValueTask.FromResult<SpaceCompressionResult?>(new(55, 123, 40, 55, 456, 40));
            });

        var notice = await RefusalNoticeAsync(service, [new PyLong(55), new PyLong(0)]);

        Assert.Equal(InSpaceCompressionMgrRpcService.InvalidRequestNotice, notice);
        Assert.False(backendCalled);
    }

    [Fact]
    public async Task Compress_item_in_space_refuses_when_the_backend_returns_no_result()
    {
        var item = new Item(55, 123, 20, 400, 5, 40, false, null);
        IService service = new InSpaceCompressionMgrRpcService(
            (_, _) => ValueTask.FromResult<Item?>(item),
            (_, _, _, _) => ValueTask.FromResult<SpaceCompressionResult?>(null));

        var notice = await RefusalNoticeAsync(service, [new PyLong(55), new PyLong(901)]);

        Assert.Equal(InSpaceCompressionMgrRpcService.FailedNotice, notice);
    }

    private static async Task<string> RefusalNoticeAsync(IService service, PyValue[] args)
    {
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Call(
            "CompressItemInSpace",
            args: args)).AsTask());
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", ((PyStr)arguments[0]).Value);
        var values = ((PyDict)arguments[1]).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value);
        return ((PyStr)values["notify"]).Value;
    }

    [Fact]
    public async Task Compress_item_in_space_without_a_facility_backend_refuses_by_name()
    {
        var item = new Item(55, 123, 20, 400, 5, 40, false, null);
        IService service = new InSpaceCompressionMgrRpcService(
            (_, _) => ValueTask.FromResult<Item?>(item));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Call(
            "CompressItemInSpace",
            args: [new PyLong(55), new PyLong(901)])).AsTask());

        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", ((PyStr)arguments[0]).Value);
        var values = ((PyDict)arguments[1]).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value);
        var notice = ((PyStr)values["notify"]).Value;
        Assert.Equal("In-space compression is not available right now. Try again in a moment.", notice);
        // a ship's compressor module does this work: no structure and no phase is involved
        Assert.DoesNotContain("structure", notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phase", notice, StringComparison.OrdinalIgnoreCase);
    }
}
