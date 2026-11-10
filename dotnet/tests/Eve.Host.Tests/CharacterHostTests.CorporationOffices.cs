using Eve.Corporations;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class CorporationOfficeHostWireTests
{
    private const long Corporation = 900_001_001;

    [PgFact]
    public async Task Registered_office_manager_quotes_the_client_station_price_and_rents_it_through_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        await rig.FundAsync(Corporation, 1_000_000m);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.members SET roles = $2 WHERE character_id = $1", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = character });
            command.Parameters.Add(new NpgsqlParameter { Value = CorporationRoles.RentOffice });
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var sink = new Capture();
        var session = new ClientSession(sink)
        {
            SessionId = 71_001,
            ClientId = 71_001,
            CharacterId = character,
            UserId = 1,
        };
        session.Set("charid", new PyLong(character));
        session.Set("corpid", new PyLong(Corporation));
        session.Set("corporationID", new PyLong(Corporation));
        session.Set("stationid", new PyLong(rig.Station));
        rig.Sessions.Enter(character, session);

        var dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);
        var quoteBinding = await DispatchAsync(dispatcher, session, sink,
            ServiceRequest(1, "officeManager", "MachoBindObject",
                new PyLong(rig.Station),
                new PyTuple([
                    new PyStr("GetPriceQuote"),
                    new PyTuple([new PyLong(Corporation)]),
                    PyNone.Instance,
                ])));
        var binding = Assert.IsType<PyTuple>(Response(quoteBinding));
        var officeManager = Assert.IsType<PySubStruct>(binding.Items[0]);
        var boundData = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(officeManager.Value).Decoded!);
        var objectId = Assert.IsType<PyStr>(boundData.Items[0]).Value;
        var quote = Assert.IsType<PyLong>(binding.Items[1]).Value;

        Assert.Equal(10_000, quote);
        var stationRecord = await DispatchAsync(dispatcher, session, sink,
            ServiceRequest(2, "stationSvc", "GetStation", new PyLong(rig.Station)));
        var cacheResult = Assert.IsType<PyObject>(Response(stationRecord));
        var cachedRecord = Assert.IsType<PyObject>(Assert.IsType<PySubStream>(
            Assert.IsType<PyTuple>(cacheResult.Args).Items[1]).Decoded!);
        Assert.Equal(quote, Assert.IsType<PyInt>(Field(cachedRecord, "officeRentalCost")).Value);

        var rentedResponse = await DispatchAsync(dispatcher, session, sink,
            BoundRequest(3, objectId, "RentOffice", new PyLong(quote)));
        Assert.IsType<PyNone>(Response(rentedResponse));

        var notifications = NotificationNames(sink);
        Assert.Equal(
            ["OnItemsChanged", "OnOfficeRentalChange", "OnOfficeRentalChange", "OnBillReceived"],
            notifications);
        Assert.Equal(MachoMessage.CallRsp, PacketType(quoteBinding));
        Assert.Equal(MachoMessage.CallRsp, PacketType(rentedResponse));
        var billNotificationIndex = sink.Sent.FindIndex(packet =>
            PacketType(packet) == MachoMessage.Notification && PacketNotificationName(packet) == "OnBillReceived");
        Assert.True(sink.Sent.IndexOf(rentedResponse) < billNotificationIndex);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"SELECT rent_isk, office_folder_id FROM {rig.CorporationsSchema}.office_rentals " +
            "WHERE corporation_id = $1 AND station_id = $2 AND active", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = Corporation });
            command.Parameters.Add(new NpgsqlParameter { Value = rig.Station });
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(10_000m, reader.GetDecimal(0));
            Assert.True(reader.GetInt64(1) > 0);
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(990_000m, await BalanceAsync(rig, Corporation));
        var station = (await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source))).All
            .Single(station => station.Id == rig.Station);
        Assert.Equal(10_000m, await BalanceAsync(rig, station.OwnerId));

        await rig.RestartAsync();
        Assert.Equal(990_000m, await BalanceAsync(rig, Corporation));
        Assert.Equal(1L, await ScalarAsync(rig,
            $"SELECT count(*) FROM {rig.CorporationsSchema}.office_rentals WHERE corporation_id = {Corporation} AND station_id = {rig.Station} AND active"));

        rig.Sessions.Enter(character, session);
        var officesBinding = await DispatchAsync(new PacketDispatcher(rig.Registry, () => rig.NowUnixMs), session, sink,
            ServiceRequest(4, "officeManager", "MachoBindObject",
                new PyLong(rig.Station),
                new PyTuple([new PyStr("GetMyCorporationsOffices"), new PyTuple([]), PyNone.Instance])));
        var officesResult = Assert.IsType<PyList>(Assert.IsType<PyTuple>(Response(officesBinding)).Items[1]);
        Assert.Single(officesResult.Items);
    }

    private static async Task<PyValue> DispatchAsync(PacketDispatcher dispatcher, ClientSession session,
        Capture sink, PyValue request)
    {
        var before = sink.Sent.Count;
        Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
        Assert.True(sink.Sent.Count > before);
        return sink.Sent.Skip(before).Last(packet =>
            PacketType(packet) is MachoMessage.CallRsp or MachoMessage.ErrorResponse);
    }

    private static PyValue ServiceRequest(long callId, string service, string method, params PyValue[] args) =>
        Request(callId, MachoAddress.Any(service).Encode(),
            new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(args), new PyDict([])]));

    private static PyValue BoundRequest(long callId, string objectId, string method, params PyValue[] args) =>
        Request(callId, MachoAddress.Any("").Encode(),
            new PyTuple([new PyStr(objectId), new PyStr(method), new PyTuple(args), new PyDict([])]));

    private static PyValue Request(long callId, PyValue destination, PyTuple body)
    {
        var source = new MachoAddress(AddressKind.Client,
            CallId: new PyLong(callId), ClientId: new PyInt(71_001));
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), destination, new PyInt(7), payload, new PyDict([]),
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]));
    }

    private static PyValue Response(PyValue packet)
    {
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(fields[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(fields[4]).Items[0]).Decoded!;
    }

    private static long PacketType(PyValue packet) =>
        Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items[0]).Value;

    private static string? PacketNotificationName(PyValue packet)
    {
        var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(state[2]).Args).Items;
        return address[1] is PyStr name ? name.Value : null;
    }

    private static List<string?> NotificationNames(Capture sink) => sink.Sent
        .Where(packet => PacketType(packet) == MachoMessage.Notification)
        .Select(PacketNotificationName)
        .ToList();

    private static PyValue Field(PyValue keyVal, string name)
    {
        var entries = keyVal switch
        {
            PyObject obj => Assert.IsType<PyDict>(obj.Args).Entries,
            PyDict dict => dict.Entries,
            _ => throw new InvalidOperationException($"Expected a key/value record, got {keyVal.GetType().Name}"),
        };
        return entries.Single(entry => entry.Key switch
        {
            PyStr text => text.Value == name,
            PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span) == name,
            _ => false,
        }).Value;
    }

    private static async Task<decimal> BalanceAsync(CharacterHostTests.CharacterHostRig rig, long ownerId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT isk FROM {rig.WalletSchema}.balances WHERE owner_id = $1 AND account_key = 1000", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ScalarAsync(CharacterHostTests.CharacterHostRig rig, string sql)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
