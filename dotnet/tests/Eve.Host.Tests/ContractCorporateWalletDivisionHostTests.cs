using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class ContractCorporateWalletDivisionHostTests
{
    private const long ContractManagerRole = 1L << 56;

    [PgFact]
    public async Task Corporate_contract_uses_selected_wallet_division_and_requires_its_take_permission()
    {
        var contractSchema = $"cx5_contract_{Guid.NewGuid():N}";
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync(contractsSchema: contractSchema);
        var characterId = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(characterId);
        var corporationId = (await rig.Characters.GetAsync(characterId))!.CorporationId;
        const int selectedWalletKey = 1002;
        const int itemHangarDivision = 118;
        var roles = ContractManagerRole | Eve.Corporations.CorporationRoles.WalletTakeRole(selectedWalletKey);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.members SET roles = $2 WHERE character_id = $1", connection))
        {
            command.Parameters.AddWithValue(characterId);
            command.Parameters.AddWithValue(roles);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        const decimal openingBalance = 1_000_000m;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {rig.WalletSchema}.balances (owner_id, account_key, isk, plex)
            VALUES ($1, $2, $3, 0)
            ON CONFLICT (owner_id, account_key) DO UPDATE SET isk = EXCLUDED.isk
            """, connection))
        {
            command.Parameters.AddWithValue(corporationId);
            command.Parameters.AddWithValue(selectedWalletKey);
            command.Parameters.AddWithValue(openingBalance);
            await command.ExecuteNonQueryAsync();
        }

        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var firstItemId = await CreateOfferedItemAsync(items, rig, corporationId, itemHangarDivision);
        var secondItemId = await CreateOfferedItemAsync(items, rig, corporationId, itemHangarDivision);
        var capture = new Capture();
        var session = new ClientSession(capture)
        {
            UserId = 1,
            SessionId = characterId + 100_000,
            ClientId = characterId,
            CharacterId = characterId,
        };
        session.Set("charid", new PyLong(characterId));
        session.Set("corpid", new PyLong(corporationId));
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("solarsystemid2", new PyLong(30_000_142));
        session.Set("corprole", new PyLong(roles));
        session.Set("corpAccountKey", new PyInt(selectedWalletKey));
        rig.Sessions.Enter(characterId, session);

        var dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);
        var createResponse = await DispatchAsync(capture, dispatcher, session, 1, ContractInfo(rig.Station, firstItemId));
        var createPacket = MachoPacket.Decode(createResponse);
        var createError = createPacket?.Type == MachoMessage.ErrorResponse
            ? new Eve.Services.MachoError(MachoErrorType.WrappedException,
                Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(createPacket.Payload).Items[2]).Items[0]).Decoded!).UserErrorMessage
            : "";
        Assert.True(createPacket?.Type == MachoMessage.CallRsp, $"CreateContract returned {createPacket?.TypeName} ({createError}).");
        var contractId = await ScalarLongAsync(rig.Source,
            $"SELECT max(contract_id) FROM {contractSchema}.contract_records WHERE issuer_id = {characterId} AND is_corporation_issued");
        Assert.True(contractId > 0);

        await rig.Events.PumpAsync();
        Assert.Contains(capture.Snapshot(),
            packet => MachoPacket.Decode(packet) is { Type: MachoMessage.Notification, Dest.BroadcastId: "OnContractCreated" });

        await using (var created = rig.Source.CreateCommand($"""
            SELECT issuer_wallet_account_key, issuer_item_flag
            FROM {contractSchema}.contract_records WHERE contract_id = $1
            """))
        {
            created.Parameters.AddWithValue(contractId);
            await using var reader = await created.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(selectedWalletKey, reader.GetInt32(0));
            Assert.Equal(itemHangarDivision, reader.GetInt32(1));
        }

        var walletTransactions = new Eve.Wallet.WalletTransactions(rig.Source, rig.WalletSchema);
        Assert.Equal(openingBalance - 240_000m,
            await walletTransactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(corporationId, selectedWalletKey), default));
        var firstItem = await items.GetAsync(firstItemId);
        Assert.Equal((1L, 0), (firstItem!.OwnerId, firstItem.Flag));

        session.Set("corpAccountKey", new PyInt(selectedWalletKey + 1));
        var unauthorizedResponse = await DispatchAsync(capture, dispatcher, session, 2, ContractInfo(rig.Station, secondItemId));
        Assert.Equal(MachoMessage.ErrorResponse, MachoPacket.Decode(unauthorizedResponse)?.Type);
        Assert.Equal(1L, await ScalarLongAsync(rig.Source,
            $"SELECT count(*) FROM {contractSchema}.contract_records WHERE issuer_id = {characterId} AND is_corporation_issued"));
        Assert.Equal(openingBalance - 240_000m,
            await walletTransactions.AccountBalanceAsync(new Eve.Wallet.WalletAccount(corporationId, selectedWalletKey), default));
        Assert.Equal((corporationId, rig.Station, itemHangarDivision),
            ((await items.GetAsync(secondItemId))!.OwnerId,
                (await items.GetAsync(secondItemId))!.LocationId,
                (await items.GetAsync(secondItemId))!.Flag));
    }

    private static async Task<long> CreateOfferedItemAsync(
        PgItemStore items, CharacterHostTests.CharacterHostRig rig, long corporationId, int itemHangarDivision)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = await items.CreateAsync(connection, transaction,
            new NewItem(34, corporationId, rig.Station, itemHangarDivision, Quantity: 1));
        await transaction.CommitAsync();
        return itemId;
    }

    private static PyValue ContractInfo(long stationId, long itemId) => new PyObject(
        new PyStr("contracts.contractStruct.ContractStruct"),
        PyTuple.Of(
            new PyInt(1), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0),
            new PyLong(stationId), new PyInt(0), new PyInt(10_000_000), new PyInt(0), new PyInt(0),
            new PyStr("Corporate wallet division"), new PyStr(""),
            new PyList([PyTuple.Of(new PyLong(itemId), new PyInt(1))]),
            new PyInt(118), new PyList([]), PyBool.True, PyBool.False));

    private static async Task<PyValue> DispatchAsync(
        Capture capture, PacketDispatcher dispatcher, ClientSession session, int callId, PyValue contractInfo)
    {
        capture.Reset();
        Assert.True(await dispatcher.DispatchAsync(Request(session, callId, contractInfo), session));
        return Assert.Single(capture.Snapshot(),
            packet => MachoPacket.Decode(packet)?.Type is not MachoMessage.Notification);
    }

    private static PyObject Request(ClientSession session, int callId, PyValue contractInfo)
    {
        var source = new PyTuple([
            new PyInt(MachoAddress.TypeClient), new PyLong(session.SessionId),
            new PyLong(session.ClientId), PyNone.Instance,
        ]);
        var body = new PyTuple([
            new PyInt(callId), new PyStr("CreateContract"), new PyTuple([contractInfo]), PyNone.Instance,
        ]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[]
        {
            new PyInt(6), source, MachoAddress.Any("contractProxy").Encode(),
            new PyLong(session.UserId!.Value), payload, PyNone.Instance,
        };
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private static async Task<long> ScalarLongAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class Capture : IPacketSink
    {
        private readonly List<PyValue> _packets = [];

        public PyValue[] Snapshot()
        {
            lock (_packets)
                return [.. _packets];
        }

        public void Reset()
        {
            lock (_packets)
                _packets.Clear();
        }

        public void Send(PyValue packet)
        {
            lock (_packets)
                _packets.Add(packet);
        }
    }
}
