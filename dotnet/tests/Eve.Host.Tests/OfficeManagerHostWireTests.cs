using System.Text;
using Eve.Corporations;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task OfficeManager_client_routes_bind_and_return_the_client_office_columns()
    {
        const long stationId = 60_000_130;
        const long corporationId = 98_000_130;
        const long officeId = 10_000_130;
        const long folderId = 12_000_130;
        const long solarSystemId = 30_000_142;
        const int stationTypeId = 1531;
        var office = new CorporationOffice(officeId, folderId, corporationId, stationId, solarSystemId,
            100_000m, FixedNowMs, FixedNowMs + CorporationOfficeStore.RentalCycleMs);
        var service = new OfficeManagerService(
            null!, null!, null!, null!, null!,
            id => id == stationId
                ? new CorporationOfficeStation(stationId, stationTypeId, 98_000_001, solarSystemId, 0.5)
                : null,
            () => FixedNowMs,
            readCurrentCorporationId: _ => ValueTask.FromResult(corporationId),
            readCorporationOffices: (_, _) => ValueTask.FromResult<IReadOnlyList<CorporationOffice>>([office]));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("corpid", new PyLong(corporationId));
                session.Set("corporationID", new PyLong(corporationId));
            });
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(new PyInt(MachoMessage.ProxyNodeId),
            await HostRpcAsync(front, open, 1, "officeManager", "MachoResolveObject"));
        var bind = Assert.IsType<PyTuple>(await HostRpcAsync(
            front, open, 2, "officeManager", "MachoBindObject",
            new PyInt(stationId),
            Py.Tuple(new PyStr("PrimeOfficeItem"), new PyTuple([]), PyNone.Instance)));
        Assert.IsType<PySubStruct>(bind.Items[0]);
        Assert.Same(PyNone.Instance, bind.Items[1]);

        var offices = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 3, "officeManager", "GetMyCorporationsOffices"));
        var row = Assert.IsType<PyObject>(Assert.Single(offices.Items));
        Assert.Equal(new PyLong(officeId), WireField(row, "officeID"));
        Assert.Equal(new PyLong(stationId), WireField(row, "stationID"));
        Assert.Equal(new PyLong(solarSystemId), WireField(row, "solarsystemID"));
        Assert.Equal(new PyInt(stationTypeId), WireField(row, "stationTypeID"));
        Assert.Equal(new PyInt(CorporationOfficeStore.OfficeFolderTypeId), WireField(row, "typeID"));

        var longBind = Assert.IsType<PyTuple>(await HostRpcAsync(
            front, open, 4, "officeManager", "MachoBindObject",
            new PyLong(stationId),
            Py.Tuple(new PyStr("PrimeOfficeItem"), new PyTuple([]), PyNone.Instance)));
        Assert.IsType<PySubStruct>(longBind.Items[0]);
        Assert.Same(PyNone.Instance, longBind.Items[1]);
        var boundReference = Assert.IsType<PyTuple>(
            Assert.IsType<PySubStream>(Assert.IsType<PySubStruct>(longBind.Items[0]).Value).Decoded);
        var boundObjectId = Assert.IsType<PyStr>(boundReference.Items[0]).Value;
        Assert.Same(PyNone.Instance,
            await OfficeBoundRpcAsync(front, open, 5, boundObjectId, "PrimeOfficeItem"));
    }

    [Fact]
    public async Task OfficeManager_integral_price_quote_is_a_Python_long_through_the_bound_host_route()
    {
        const long structureId = 1_000_130;
        const long corporationId = 98_000_130;
        var service = new OfficeManagerService(
            null!, null!, null!, null!, null!,
            _ => null,
            () => FixedNowMs,
            structures: new StructureOffices(structureId, 35_832, 30_000_142, corporationId, 100_113m));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("corpid", new PyInt(corporationId)));
        var (front, open) = await OpenFrontAsync();

        var bind = Assert.IsType<PyTuple>(await HostRpcAsync(
            front, open, 1, "officeManager", "MachoBindObject",
            new PyInt(structureId),
            Py.Tuple(
                new PyStr("GetPriceQuote"),
                Py.Tuple(new PyInt(corporationId)),
                PyNone.Instance)));
        Assert.IsType<PySubStruct>(bind.Items[0]);
        Assert.Equal(new PyLong(100_113), bind.Items[1]);

        var boundReference = Assert.IsType<PyTuple>(
            Assert.IsType<PySubStream>(Assert.IsType<PySubStruct>(bind.Items[0]).Value).Decoded);
        var boundObjectId = Assert.IsType<PyStr>(boundReference.Items[0]).Value;
        Assert.Equal(new PyLong(100_113),
            await OfficeBoundRpcAsync(front, open, 2, boundObjectId, "GetPriceQuote", new PyLong(corporationId)));
    }

    [Fact]
    public async Task OfficeManager_access_denial_is_a_UserError_on_the_bound_Host_route()
    {
        const long structureId = 1_000_131;
        const long corporationId = 98_000_131;
        var service = new OfficeManagerService(
            null!, null!, null!, null!, null!,
            _ => null,
            () => FixedNowMs,
            structures: new StructureOffices(structureId, 35_832, 30_000_142, corporationId, 100_113m));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("corpid", new PyLong(corporationId)));
        var (front, open) = await OpenFrontAsync();
        var args = Py.Tuple(
            new PyInt(structureId),
            Py.Tuple(
                new PyStr("GetPriceQuote"),
                Py.Tuple(new PyLong(corporationId + 1)),
                PyNone.Instance));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "officeManager", "MachoBindObject", args, TransportKwargs()))));

        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type);
        var payload = Encoding.ASCII.GetString(send.Payload.Span);
        Assert.Contains("eveexceptions.UserError", payload);
        Assert.Contains("CrpAccessDenied", payload);
        Assert.DoesNotContain("exceptions.RuntimeError", payload);
    }

    private async Task<PyValue> OfficeBoundRpcAsync(
        TestFront front, SessionOpen open, long callId, string objectId, string method, params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, callId, objectId, method, new PyTuple(args), TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
        return Result(decoded)!;
    }

    private sealed class StructureOffices(long id, int typeId, long solarSystemId, long ownerCorporationId, decimal rent)
        : IStructureOffices
    {
        public Task<StructureOfficeSite?> ReadAsync(long structureId, CancellationToken cancellationToken) =>
            Task.FromResult<StructureOfficeSite?>(structureId == id
                ? new StructureOfficeSite(id, typeId, solarSystemId, ownerCorporationId)
                : null);

        public Task<bool> MayRentAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<decimal> RentForAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
            Task.FromResult(rent);

        public Task<decimal?> RenewalRentAsync(long structureId, CancellationToken cancellationToken) =>
            Task.FromResult<decimal?>(rent);

        public Task<bool> MoveOfficeToSafetyAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction,
            StructureOfficeSite site, long corporationId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ScheduleRentDueAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction,
            long officeId, long dueAtMs, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
