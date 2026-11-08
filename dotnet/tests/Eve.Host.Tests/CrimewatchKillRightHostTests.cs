using System.Reflection;
using System.Runtime.CompilerServices;
using Eve.Crimewatch;
using Eve.Data;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Space;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public sealed partial class CrimewatchKillRightHostTests
{
    private sealed class NotificationSink : IPacketSink
    {
        private readonly Action<string>? _sent;

        public NotificationSink(Action<string>? sent = null) => _sent = sent;

        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet)
        {
            Packets.Add(packet);
            if (packet is PyObject { Args: PyTuple state } && state.Items.Count > 2 &&
                state.Items[0] is PyInt { Value: MachoMessage.Notification })
                _sent?.Invoke(MachoAddress.Decode(state.Items[2]).BroadcastId ?? "");
        }
    }

    private sealed record WireNotification(string Name, string IdType, PyValue[] Arguments);

    [PgFact]
    public async Task A_player_ship_loss_through_the_host_composition_consumes_only_the_matching_right_and_notifies_online_clients()
    {
        const long now = 1_900_000_000_000;
        const long ownerWithoutFinalBlow = 90_000_001;
        const long ownerForFinalBlow = 90_000_005;
        const long target = 90_000_002;
        const long activator = 90_000_003;
        const long finalAttacker = 90_000_004;
        const long ship = 9_800_000_001;
        const long solarSystem = 30_000_142;
        var schema = "cwh_" + Guid.NewGuid().ToString("N")[..12];
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await PgCrimewatchStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgCrimewatchStore(source, schema);
            var rightWithoutFinalBlowId = await store.CreateKillRightAsync(ownerWithoutFinalBlow, target, solarSystem, now);
            Assert.NotNull(rightWithoutFinalBlowId);
            Assert.True(await store.SetKillRightSaleAsync(ownerWithoutFinalBlow, rightWithoutFinalBlowId.Value, 0m, null, now));
            var activationWithoutFinalBlow = await store.BuyKillRightAsync(
                rightWithoutFinalBlowId.Value, activator, [activator], null, now + 1, null);
            Assert.True(activationWithoutFinalBlow.Success);

            var rightForFinalBlowId = await store.CreateKillRightAsync(ownerForFinalBlow, target, solarSystem, now);
            Assert.NotNull(rightForFinalBlowId);
            Assert.True(await store.SetKillRightSaleAsync(ownerForFinalBlow, rightForFinalBlowId.Value, 0m, null, now));
            var activationForFinalBlow = await store.BuyKillRightAsync(
                rightForFinalBlowId.Value, finalAttacker, [finalAttacker], null, now + 2, null);
            Assert.True(activationForFinalBlow.Success);

            var sessions = new CharacterSessions();
            var sinks = new Dictionary<long, NotificationSink>();
            foreach (var characterId in new[] { ownerWithoutFinalBlow, ownerForFinalBlow, target, activator, finalAttacker })
            {
                var sink = new NotificationSink();
                sinks.Add(characterId, sink);
                sessions.Enter(characterId, new ClientSession(sink)
                {
                    CharacterId = characterId,
                    SessionId = characterId,
                    ClientId = characterId + 100,
                });
            }

            List<string> spaceLogs = [];
            using var space = new SpaceSessions(
                DispatchProxy.Create<ISimulation, InertWormholeSimulation>(), null!, null!,
                new StationCatalog([]), new SolarSystemCatalog([]), () => now, spaceLogs.Add);
            var composition = (CharacterServicesComposition)RuntimeHelpers.GetUninitializedObject(typeof(CharacterServicesComposition));
            SetField(composition, "spaceSessions", space);
            SetField(composition, "crimewatchStore", store);
            SetField(composition, "characterSessions", sessions);
            SetField(composition, "now", (Func<long>)(() => now));
            Assert.Same(sessions, typeof(CharacterServicesComposition)
                .GetField("characterSessions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(composition));
            typeof(CharacterServicesComposition).GetMethod("WireKillRightCallbacks", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(composition, null);

            var handlers = (Action<ShipLoss>?)typeof(SpaceSessions)
                .GetField("ShipLost", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(space);
            var loss = new ShipLoss(target, ship, 587, ShipLossCause.Player, (int)solarSystem, finalAttacker);
            handlers?.Invoke(loss);
            handlers?.Invoke(loss);
            Assert.Empty(spaceLogs);

            Assert.Empty(await store.KillRightsOfAsync(ownerForFinalBlow));
            Assert.Single(await store.KillRightsOfAsync(ownerWithoutFinalBlow));
            Assert.Equal(5, sessions.All().Count);
            var consumedReceipt = await store.ConsumeKillRightAfterKillAsync(target, finalAttacker, ship, now + 1);
            Assert.Equal(ownerForFinalBlow, consumedReceipt?.OwnerCharacterId);
            Assert.Equal(target, consumedReceipt?.TargetCharacterId);
            Assert.Equal(finalAttacker, consumedReceipt?.ActivatorCharacterId);
            Assert.Equal([activationWithoutFinalBlow.Activation!.ActivationId],
                (await store.ActiveKillRightActivationsForTargetAsync(target, now + 1))
                .Select(value => value.ActivationId));

            foreach (var characterId in new[] { ownerForFinalBlow, target, finalAttacker })
            {
                Assert.Same(sinks[characterId], sessions.Of(characterId)?.Sink);
                Assert.Equal(2, sinks[characterId].Packets.Count);
                var notes = NotificationsOf(sinks[characterId]);
                Assert.Equal(2, notes.Count);
                Assert.All(notes, note =>
                {
                    Assert.Equal("OnKillRightUsed", note.Name);
                    Assert.Equal("clientID", note.IdType);
                    Assert.Equal([new PyLong(rightForFinalBlowId!.Value), new PyLong(target)], note.Arguments);
                });
            }
            Assert.Empty(NotificationsOf(sinks[activator]));
            Assert.Empty(NotificationsOf(sinks[ownerWithoutFinalBlow]));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task A_pod_loss_through_the_host_composition_consumes_the_target_kill_right()
    {
        const long now = 1_900_000_000_000;
        const long owner = 91_000_001;
        const long target = 91_000_002;
        const long capsule = 9_800_000_002;
        const long solarSystem = 30_000_142;
        var schema = "cwh_" + Guid.NewGuid().ToString("N")[..12];
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await PgCrimewatchStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgCrimewatchStore(source, schema);
            var rightId = await store.CreateKillRightAsync(owner, target, solarSystem, now);
            Assert.NotNull(rightId);
            var activation = await store.ActivateKillRightAsync(rightId.Value, owner, now);
            Assert.True(activation.Success);

            var sessions = new CharacterSessions();
            var ownerSink = new NotificationSink();
            var targetSink = new NotificationSink();
            sessions.Enter(owner, new ClientSession(ownerSink) { CharacterId = owner, SessionId = owner });
            sessions.Enter(target, new ClientSession(targetSink) { CharacterId = target, SessionId = target });
            List<string> spaceLogs = [];
            using var space = new SpaceSessions(
                DispatchProxy.Create<ISimulation, InertWormholeSimulation>(), null!, null!,
                new StationCatalog([]), new SolarSystemCatalog([]), () => now, spaceLogs.Add);
            var composition = (CharacterServicesComposition)RuntimeHelpers.GetUninitializedObject(typeof(CharacterServicesComposition));
            SetField(composition, "spaceSessions", space);
            SetField(composition, "crimewatchStore", store);
            SetField(composition, "characterSessions", sessions);
            SetField(composition, "now", (Func<long>)(() => now));
            typeof(CharacterServicesComposition).GetMethod("WireKillRightCallbacks", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(composition, null);

            var handlers = (Action<PodLoss>?)typeof(SpaceSessions)
                .GetField("PodLost", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(space);
            handlers?.Invoke(new PodLoss(target, capsule, (int)solarSystem, ShipLossCause.Npc, 0));
            Assert.Empty(spaceLogs);

            Assert.Empty(await store.KillRightsOfAsync(owner));
            Assert.Empty(await store.ActiveKillRightActivationsForTargetAsync(target, now + 1));
            foreach (var sink in new[] { ownerSink, targetSink })
            {
                var note = Assert.Single(NotificationsOf(sink));
                Assert.Equal("OnKillRightUsed", note.Name);
                Assert.Equal("clientID", note.IdType);
                Assert.Equal([new PyLong(rightId.Value), new PyLong(target)], note.Arguments);
            }
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static void SetField(CharacterServicesComposition composition, string name, object value) =>
        typeof(CharacterServicesComposition).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(composition, value);

    private static IReadOnlyList<WireNotification> NotificationsOf(NotificationSink sink)
    {
        var notes = new List<WireNotification>();
        foreach (var packet in sink.Packets.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
                continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var arguments = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items;
            notes.Add(new WireNotification(
                JsSemantics.StringValue(address[1])!,
                JsSemantics.StringValue(address[3])!,
                [.. arguments]));
        }
        return notes;
    }
}
