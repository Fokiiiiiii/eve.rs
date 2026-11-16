using System.Globalization;
using Eve.Data;
using Eve.Events;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Slash;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Setstanding_sends_the_three_client_arguments_after_persisting_the_relation()
    {
        const long characterId = 900_001;
        const long ownerId = 500_001;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "standing_set_sched_" + suffix;
        var eventSchema = "standing_set_events_" + suffix;
        var relationSchema = "standing_set_relations_" + suffix;
        await DurableScheduler.MigrateAsync(source, schedulerSchema);
        await EventBus.MigrateAsync(source, eventSchema);
        await RelationStore.MigrateAsync(source, relationSchema);
        await using var scheduler = new DurableScheduler(source, () => FixedNowMs, new SchedulerOptions { Concurrency = 1 }, schedulerSchema);
        var events = new EventBus(source, scheduler, schema: eventSchema);
        var relations = new RelationStore(source, scheduler, events, relationSchema);
        ClientSession? hostSession = null;
        var commands = new StandingCommands(
            new TestStandingDirectory(ownerId),
            relations,
            (character, owner, standing) => StandingSetNotification.Send(hostSession, character, owner, standing));
        Start(
            configure: registry => registry.Register(new SlashService(
                new ShipHealthCommands(null!, null!, () => FixedNowMs), standings: commands)),
            sessionOpened: session =>
            {
                hostSession = session;
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
            });
        var (front, open) = await OpenFrontAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "slash", "SlashCmd", [new PyStr("/setstanding 2.5 500001")]))));
        var notice = await ReadStandingNotificationAsync(front);
        var response = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload))).Type);
        var reply = Result(MarshalCodec.Decode(response.Payload));

        Assert.Equal("Set standing with Owner(500001) to 2.50 for character(900001).", WireText(reply));
        Assert.Equal("OnStandingSet", notice.Name);
        Assert.Equal("clientID", notice.IdType);
        Assert.Equal(
            new PyValue[] { new PyInt(ownerId), new PyLong(characterId), new PyReal(2.5) },
            notice.Args);
        Assert.Equal(2.5, await relations.ValueAsync(RelationKinds.Standing.Name, ownerId, characterId, 0));
    }

    private sealed class TestStandingDirectory(long ownerId) : IStandingDirectory
    {
        public Task<StandingOwner?> FindOwnerAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult<StandingOwner?>(token == ownerId.ToString(CultureInfo.InvariantCulture)
                ? new StandingOwner(ownerId, "Owner")
                : null);

        public Task<IReadOnlyList<StandingOwner>> AgentOwnersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StandingOwner>>([]);

        public Task<long?> FindCharacterAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(null);

        public Task<string?> CharacterNameAsync(long characterId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    private static async Task<(string Name, string IdType, PyValue[] Args)> ReadStandingNotificationAsync(TestFront front)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await front.ReceiveAsync(timeout.Token);
            if (message is not Send send) continue;
            var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload)).Args).Items;
            if (Assert.IsType<PyInt>(state[0]).Value != MachoMessage.Notification) continue;
            var address = MachoAddress.Decode(state[2]);
            if (address.BroadcastId != "OnStandingSet") continue;
            var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(state[4]).Items[0]).Items[1]);
            var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray()));
            var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(notification.Items[1]).Items[1]);
            return (address.BroadcastId!, address.IdType!, [.. args.Items]);
        }
    }
}
