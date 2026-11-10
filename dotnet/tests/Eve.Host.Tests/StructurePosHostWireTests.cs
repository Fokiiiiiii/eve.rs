using Eve.Macho;
using Eve.Marshal;
using Eve.Host.Persistence;
using Eve.Ships;
using Eve.Data;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task PosMgr_AnchorStructure_accepts_the_client_item_and_position_arguments()
    {
        long anchoredItemId = 0;
        IReadOnlyList<double>? anchoredPosition = null;
        Start(strictRpc: true, anchorControlTower: (session, itemId, position, _) =>
        {
            anchoredItemId = itemId;
            anchoredPosition = position;
            return ValueTask.CompletedTask;
        });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 2, "posMgr", "AnchorStructure",
            new PyTuple([new PyLong(9_871_005_101), new PyTuple([new PyReal(0), new PyReal(0), new PyReal(0)])]), MachoVersion()))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());

        Assert.Equal(MachoMessage.CallRsp, Envelope(reply).Type);
        Assert.Equal(9_871_005_101, anchoredItemId);
        Assert.Equal(new[] { 0d, 0d, 0d }, anchoredPosition);
    }

    [StructureControlPgFact]
    public async Task PosMgr_GetControlTowers_returns_the_corporations_persisted_towers_over_macho()
    {
        const int ownerCorporationId = 98_710_051;
        const int otherCorporationId = 98_710_052;
        const int solarSystemId = 30_000_142;
        const int towerGroupId = 365;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required");
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var schema = "pos_towers_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgSpaceItemStore.MigrateAsync(source, schema);
            var deployed = new PgDeployedStore(source, schema);
            await deployed.SaveAsync(new DeployedRecord(
                9_871_005_101, "control_tower", solarSystemId, "Anchored", 16_213, towerGroupId,
                [1, 2, 3], 1_000, ownerCorporationId, ownerCorporationId, 0, 20_000_001, long.MaxValue, FixedNowMs - 1));
            await deployed.SaveAsync(new DeployedRecord(
                9_871_005_102, "control_tower", solarSystemId + 1, "Anchoring", 16_214, towerGroupId,
                [4, 5, 6], 1_000, ownerCorporationId, ownerCorporationId, 0, 20_000_002, long.MaxValue, FixedNowMs + 60_000));
            await deployed.SaveAsync(new DeployedRecord(
                9_871_005_104, "control_tower", solarSystemId, "Unanchored", 16_216, towerGroupId,
                [10, 11, 12], 1_000, ownerCorporationId, ownerCorporationId, 0, 20_000_004, long.MaxValue, long.MaxValue));
            await deployed.SaveAsync(new DeployedRecord(
                9_871_005_103, "control_tower", solarSystemId, "Other corporation", 16_215, towerGroupId,
                [7, 8, 9], 1_000, otherCorporationId, otherCorporationId, 0, 20_000_003, long.MaxValue, 1));

            var readModel = new ControlTowerReadModel(deployed);
            Start(
                sessionOpened: session => session.Set("corpid", new PyLong(ownerCorporationId)),
                readControlTowerRows: readModel.ReadAsync);
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "posMgr", "GetControlTowers", new PyTuple([]), MachoVersion()))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Envelope(reply).Type);
            var rows = Assert.IsType<PyList>(Result(MarshalCodec.Decode(reply.Payload)));
            Assert.Equal(
                [
                    new PyList([new PyInt(16_213), new PyLong(9_871_005_101), new PyInt(solarSystemId)]),
                    new PyList([new PyInt(16_214), new PyLong(9_871_005_102), new PyInt(solarSystemId + 1)]),
                ],
                rows.Items);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
