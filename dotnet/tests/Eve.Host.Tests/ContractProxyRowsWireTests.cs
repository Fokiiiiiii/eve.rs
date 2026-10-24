using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class HostServerTests
{
    [Fact]
    public async Task Place_bid_forwards_a_positive_integer_amount_through_the_registered_host_service()
    {
        var writer = new RecordingContractWriter();
        StartContractHost(writer);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "contractProxy", "PlaceBid",
                new PyTuple([new PyInt(900), new PyInt(1000), PyBool.False]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());

        Assert.True(Assert.IsType<PyBool>(Result(MarshalCodec.Decode(reply.Payload))).Value);
        Assert.Equal((900L, 42L, 84L, false, 1000, 1000m), writer.LastBid);
    }

    [Fact]
    public async Task Search_contracts_returns_the_fields_consumed_by_the_client_through_the_registered_host_service()
    {
        var writer = new RecordingContractWriter();
        var readModel = new SearchContractReadModel();
        StartContractHost(writer, readModel);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = Eve.Services.Py.Dict(
            ("itemTypes", PyValue.PythonSet([new PyInt(34)])),
            ("itemTypeName", PyNone.Instance),
            ("itemCategoryID", PyNone.Instance),
            ("itemGroupID", PyNone.Instance),
            ("contractType", new PyInt(10)),
            ("securityClasses", PyValue.PythonSet([new PyInt(0), new PyInt(2)])),
            ("locationID", new PyLong(30000001)),
            ("endLocationID", PyNone.Instance),
            ("issuerID", new PyInt(200)),
            ("minPrice", new PyReal(100)),
            ("maxPrice", PyNone.Instance),
            ("minReward", PyNone.Instance),
            ("maxReward", PyNone.Instance),
            ("minCollateral", PyNone.Instance),
            ("maxCollateral", PyNone.Instance),
            ("minVolume", PyNone.Instance),
            ("maxVolume", PyNone.Instance),
            ("excludeTrade", PyBool.False),
            ("excludeMultiple", PyBool.True),
            ("excludeNoBuyout", PyBool.False),
            ("availability", new PyInt(0)),
            ("description", PyNone.Instance),
            ("searchHint", new PyInt(0)),
            ("sortBy", new PyInt(1)),
            ("sortDir", new PyInt(1)),
            ("startNum", new PyInt(25)),
            ("machoVersion", new PyInt(1)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "contractProxy", "SearchContracts", new PyTuple([]), kwargs))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var response = Assert.IsType<PyObject>(Result(MarshalCodec.Decode(reply.Payload)));

        Assert.Empty(Assert.IsType<PyList>(Field(response, "contracts")).Items);
        Assert.Equal(17, Assert.IsType<PyInt>(Field(response, "numFound")).Value);
        Assert.Equal(5_000_000L, Assert.IsType<PyLong>(Field(response, "searchTime")).Value);
        Assert.Equal(1000, Assert.IsType<PyInt>(Field(response, "maxResults")).Value);
        Assert.Equal(42, readModel.Query!.CharacterId);
        Assert.Equal(84, readModel.Query.CorporationId);
        Assert.Equal(10, readModel.Query.ContractType);
        Assert.Equal(25, readModel.Query.StartNum);
        Assert.Equal([34], readModel.Query.ItemTypeIds);
        Assert.Equal([0, 2], readModel.Query.SecurityClasses);
    }

    [Fact]
    public async Task Split_stack_mutates_through_the_registered_host_service()
    {
        var writer = new RecordingContractWriter();
        StartContractHost(writer);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "contractProxy", "SplitStack",
                new PyTuple([new PyInt(3001), new PyLong(200), new PyInt(3), PyBool.False, new PyInt(4)]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());

        Assert.True(Assert.IsType<PyBool>(Result(MarshalCodec.Decode(reply.Payload))).Value);
        Assert.Equal((200L, 3L, 42L, 84L, 3001L, false, 4), writer.LastSplit);
    }

    private static PyValue Field(PyObject keyVal, string name) =>
        Assert.IsType<PyDict>(keyVal.Args).Entries.Single(entry =>
            JsSemantics.StringValue(entry.Key) == name).Value;

    private sealed class SearchContractReadModel : IContractReadModel
    {
        public ContractSearchQuery? Query { get; private set; }

        public ValueTask<ContractLoginInfo> GetLoginInfoAsync(long characterId, long corporationId, long allianceId,
            long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromException<ContractLoginInfo>(new NotSupportedException());

        public ValueTask<ContractEscrowSummary> GetMyContractEscrowAsync(long characterId, long corporationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ContractEscrowSummary>(new NotSupportedException());

        public ValueTask<ContractReadBundle> GetMyBidsAsync(long characterId, long corporationId, bool forCorporation,
            long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromException<ContractReadBundle>(new NotSupportedException());

        public ValueTask<ContractReadBundle> GetContractListForOwnerAsync(long characterId, long corporationId, long ownerId,
            int status, int contractType, long issuedBy, int pageSize, long? startContractId, long nowFileTime,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ContractReadBundle>(new NotSupportedException());

        public ValueTask<ContractSearchResult> SearchContractsAsync(ContractSearchQuery query, long nowFileTime,
            CancellationToken cancellationToken)
        {
            Query = query;
            return ValueTask.FromResult(new ContractSearchResult([], 17, 5_000_000));
        }

        public ValueTask<ContractReadBundle> GetMyCurrentContractListAsync(long characterId, long corporationId,
            bool isAccepted, bool forCorporation, long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromException<ContractReadBundle>(new NotSupportedException());

        public ValueTask<ContractReadBundle> GetMyExpiredContractListAsync(long characterId, long corporationId,
            bool forCorporation, long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromException<ContractReadBundle>(new NotSupportedException());
    }
}
