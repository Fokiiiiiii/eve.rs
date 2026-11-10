using System.Net;
using System.Net.Sockets;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Abrupt_disconnect_persists_logoff_before_presence_notice_and_clean_logoff_is_not_rewritten()
    {
        const long now = 1_790_646_377_000;
        const long characterSessionId = 5_501_001;
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync(now);
        var character = await rig.CreateCharacterAsync();
        var observer = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(character);
        var corporationId = (await rig.Characters.GetAsync(character))!.CorporationId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var makeCharacterDirector = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.corporations SET ceo_id = $1 WHERE id = $2", connection))
        {
            makeCharacterDirector.Parameters.Add(new NpgsqlParameter { Value = character });
            makeCharacterDirector.Parameters.Add(new NpgsqlParameter { Value = corporationId });
            Assert.Equal(1, await makeCharacterDirector.ExecuteNonQueryAsync());
        }
        var firstLogoff = DateTimeOffset.FromUnixTimeMilliseconds(now + 60_000);
        var explicitLogoff = firstLogoff.AddMinutes(1);
        var afterExplicitLogoff = explicitLogoff.AddHours(1);

        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));
        foreach (var (ownerId, contactId) in new[] { (character, observer), (observer, character) })
        {
            await charMgr.CallAsync(new ServiceCall
            {
                Method = "AddContact",
                Args = [new PyLong(contactId), new PyInt(-10), PyBool.True, PyBool.False, new PyStr("")],
                Session = new ClientSession { CharacterId = ownerId, UserId = 1 },
            });
        }

        var observerSink = new LogoffObservingSink(() =>
            rig.Characters.GetAsync(character).GetAwaiter().GetResult()?.LastLogoffAt);
        rig.Sessions.Enter(observer, new ClientSession
        {
            SessionId = 5_501_002,
            CharacterId = observer,
            UserId = 1,
            Sink = observerSink,
        });
        await rig.OnlineStatusNotifications.FlushAsync();

        _server = rig.Server;
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);

        TestFront? firstFront = null;
        TestFront? secondFront = null;
        try
        {
            firstFront = await Handshaken(await ConnectAsync());
            var firstOpen = Open(characterSessionId, 1);
            await OpenCharacterSessionAsync(firstFront, firstOpen, character);
            Assert.NotNull(rig.Sessions.Of(character));
            await rig.OnlineStatusNotifications.FlushAsync();

            rig.AdvanceTo(firstLogoff.ToUnixTimeMilliseconds());
            firstFront.Dispose();
            await WaitUntilAsync(() => rig.Sessions.Of(character) is null);
            await rig.OnlineStatusNotifications.FlushAsync();

            Assert.Equal(firstLogoff, (await rig.Characters.GetAsync(character))!.LastLogoffAt);
            Assert.Contains(firstLogoff, observerSink.LogoffTimesAtNotice);
            Assert.Equal(new PyInt(0), await ReadLastOnlineAsync(rig, character));

            secondFront = await Handshaken(await ConnectAsync());
            var secondOpen = Open(characterSessionId + 1, 1);
            await OpenCharacterSessionAsync(secondFront, secondOpen, character);
            await rig.OnlineStatusNotifications.FlushAsync();

            rig.AdvanceTo(explicitLogoff.ToUnixTimeMilliseconds());
            Assert.Same(PyBool.True, await CallAsync(secondFront, secondOpen, 2,
                "userSvc", "UserLogOffCharacter"));
            Assert.Equal(explicitLogoff, (await rig.Characters.GetAsync(character))!.LastLogoffAt);

            rig.AdvanceTo(afterExplicitLogoff.ToUnixTimeMilliseconds());
            secondFront.Dispose();
            await WaitUntilAsync(() => rig.Sessions.Of(character) is null);
            await rig.OnlineStatusNotifications.FlushAsync();

            Assert.Equal(explicitLogoff, (await rig.Characters.GetAsync(character))!.LastLogoffAt);
            Assert.Equal([firstLogoff, explicitLogoff], observerSink.LogoffTimesAtNotice);
            Assert.Equal(new[]
            {
                "OnContactLoggedOn", "OnContactLoggedOff", "OnContactLoggedOn", "OnContactLoggedOff",
            }, observerSink.NotificationNames);
        }
        finally
        {
            firstFront?.Dispose();
            secondFront?.Dispose();
            try
            {
                await WaitUntilAsync(() => rig.Sessions.Of(character) is null);
                await rig.OnlineStatusNotifications.FlushAsync();
            }
            catch (TimeoutException)
            {
                // Preserve the test failure; the caller still tears down the Host and its database.
            }
        }
    }

    private static async Task OpenCharacterSessionAsync(TestFront front, SessionOpen open, long characterId)
    {
        await front.SendAsync(open);
        Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Same(PyNone.Instance, await CallAsync(front, open, 1, "charUnboundMgr", "SelectCharacterID", new PyLong(characterId)));
    }

    private static async Task<PyValue?> CallAsync(TestFront front, SessionOpen open, long correlation,
        string service, string method, params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.Call(open, correlation, service, method, args))));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var send = Assert.IsType<Send>(await front.ReceiveAsync(timeout.Token));
            var response = MarshalCodec.Decode(send.Payload);
            var header = ((PyTuple)Assert.IsType<PyObject>(response).Args).Items;
            if (header[0] is PyInt { Value: 7 })
                return Result(response);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The character session did not reach the requested state.");
            await Task.Delay(10);
        }
    }

    private static async Task<PyValue> ReadLastOnlineAsync(CharacterHostTests.CharacterHostRig rig, long characterId)
    {
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("corpRegistry"));
        var response = await service.CallAsync(new ServiceCall
        {
            Method = "GetMemberTrackingInfo",
            Args = [],
            Session = new ClientSession { CharacterId = characterId, UserId = 1 },
        });
        var rowset = Assert.IsType<PyDict>(Assert.IsType<PyObject>(response).Args);
        var lines = Assert.IsType<PyList>(rowset.Entries.Single(entry => entry.Key == new PyStr("lines")).Value);
        var row = lines.Items.Select(item => Assert.IsType<PyPackedRow>(item))
            .Single(item => item.Values[0] == new PyInt(characterId));
        return row.Values[9];
    }

    private sealed class LogoffObservingSink(Func<DateTimeOffset?> readLastLogoffAt) : IPacketSink
    {
        public List<string> NotificationNames { get; } = [];
        public List<DateTimeOffset?> LogoffTimesAtNotice { get; } = [];

        public void Send(PyValue packet)
        {
            if (packet is not PyObject { Args: PyTuple state } || state.Items.Count < 5 ||
                state.Items[0] is not PyInt { Value: MachoMessage.Notification } ||
                state.Items[2] is not PyObject { Args: PyTuple address } || address.Items.Count < 2 ||
                address.Items[1] is not PyStr name)
                return;

            NotificationNames.Add(name.Value);
            if (name.Value == "OnContactLoggedOff")
                LogoffTimesAtNotice.Add(readLastLogoffAt());
        }
    }
}
