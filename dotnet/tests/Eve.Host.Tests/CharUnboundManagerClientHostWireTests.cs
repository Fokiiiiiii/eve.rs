using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Character_count_is_the_accounts_integer_count_over_registered_host_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        _ = await rig.CreateCharacterAsync();
        _ = await rig.CreateCharacterAsync();
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.UserId = 1);
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(new PyInt(2),
            await HostRpcAsync(front, open, 1, "charUnboundMgr", "GetNumCharacters"));
    }

    [PgFact]
    public async Task Character_delete_cancel_clears_the_queue_over_registered_host_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        var session = new ClientSession { UserId = 1, CharacterId = characterId };

        _ = await service.CallAsync(new ServiceCall
        {
            Method = "PrepareCharacterForDelete",
            Args = [new PyLong(characterId)],
            Session = session,
        });
        Assert.True((await rig.Characters.GetAsync(characterId))!.IsQueuedForDeletion);

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: openedSession =>
            {
                openedSession.UserId = 1;
                openedSession.CharacterId = characterId;
            });
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 1,
            "charUnboundMgr", "CancelCharacterDeletePrepare", new PyLong(characterId)));
        Assert.False((await rig.Characters.GetAsync(characterId))!.IsQueuedForDeletion);
    }

    [PgFact]
    public async Task A_corporation_ceo_cannot_be_queued_or_deleted_over_registered_host_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var ceo = await rig.CreateCharacterAsync();
        var otherPilot = await rig.CreateCharacterAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var owner = new NpgsqlCommand(
                $"INSERT INTO {rig.CorporationsSchema}.owner_names(owner_id,name_key,ticker_key) VALUES (900001001,'test corporation','TEST')", connection);
            await owner.ExecuteNonQueryAsync();
            await using var corporation = new NpgsqlCommand(
                $"INSERT INTO {rig.CorporationsSchema}.corporations(id,name,ticker,ceo_id,creator_id,station_id,solar_system_id,tax_rate,lp_tax_rate,description,url,member_limit,created_ms) " +
                "VALUES (900001001,'Test Corporation','TEST',$1,$1,$2,30000142,0,0,'','',100,0)", connection);
            corporation.Parameters.Add(new NpgsqlParameter { Value = ceo });
            corporation.Parameters.Add(new NpgsqlParameter { Value = rig.Station });
            await corporation.ExecuteNonQueryAsync();
        }

        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.UserId = 1);
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(MachoMessage.ErrorResponse,
            (await HostErrorPacketAsync(front, open, 1, "PrepareCharacterForDelete", ceo)).Type);
        var refusedQueue = await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(new ServiceCall
        {
            Method = "PrepareCharacterForDelete",
            Args = [new PyLong(ceo)],
            Session = new ClientSession { UserId = 1 },
        }));
        Assert.Equal("CustomInfo", refusedQueue.Error.UserErrorMessage);
        Assert.Equal("You must resign as CEO or close the corporation before deleting this character.", InfoText(refusedQueue));
        Assert.False((await rig.Characters.GetAsync(ceo))!.IsQueuedForDeletion);

        Assert.IsType<PyLong>(await HostRpcAsync(front, open, 2,
            "charUnboundMgr", "PrepareCharacterForDelete", new PyLong(otherPilot)));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var promote = new NpgsqlCommand(
                $"UPDATE {rig.CorporationsSchema}.corporations SET ceo_id=$1 WHERE id=900001001", connection);
            promote.Parameters.Add(new NpgsqlParameter { Value = otherPilot });
            await promote.ExecuteNonQueryAsync();
            await using var expire = new NpgsqlCommand(
                $"UPDATE {rig.CharactersSchema}.characters SET delete_prepare_at=now()-interval '1 minute' WHERE id=$1", connection);
            expire.Parameters.Add(new NpgsqlParameter { Value = otherPilot });
            await expire.ExecuteNonQueryAsync();
        }
        Assert.Equal(MachoMessage.ErrorResponse,
            (await HostErrorPacketAsync(front, open, 3, "DeleteCharacter", otherPilot)).Type);
        var refusedDeletion = await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(new ServiceCall
        {
            Method = "DeleteCharacter",
            Args = [new PyLong(otherPilot)],
            Session = new ClientSession { UserId = 1 },
        }));
        Assert.Equal("CustomInfo", refusedDeletion.Error.UserErrorMessage);
        Assert.Equal("You must resign as CEO or close the corporation before deleting this character.", InfoText(refusedDeletion));
        Assert.False((await rig.Characters.GetAsync(otherPilot))!.IsDeleted);
    }

    private async Task<MachoPacket> HostErrorPacketAsync(TestFront front, SessionOpen open, long callId, string method, long characterId)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, callId, "charUnboundMgr", method, new PyTuple([new PyLong(characterId)]),
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        return Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
    }

    private static string InfoText(MachoWrappedException error)
    {
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var args = Assert.IsType<PyDict>(Assert.IsType<PyTuple>(header.Items[1]).Items[1]);
        return Assert.IsType<PyStr>(args.Entries.Single(entry => ((PyStr)entry.Key).Value == "info").Value).Value;
    }
}
