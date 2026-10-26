using System.Text;
using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Dogma;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Module_activation_uses_the_registered_bound_host_route_for_docked_and_in_space_client_contexts()
    {
        const long stationId = 600;
        const long solarSystemId = 30000142;
        const int groupStation = 15;
        const int groupSolarSystem = 5;

        var modules = new RecordingModules();
        var service = new DogmaIMService(
            new DogmaStatic([], [], []), null!, () => FixedNowMs,
            (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null),
            space: modules, modules: modules);
        ClientSession? client = null;
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                client = session;
                session.Set("charid", new PyInt(42));
                session.Set("stationid", new PyInt(stationId));
                session.Set("solarsystemid", new PyLong(solarSystemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<Send> Bind(uint callId, long locationId, int groupId)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, callId, "dogmaIM", "MachoBindObject",
                new PyTuple([
                    new PyTuple([new PyLong(locationId), new PyInt(groupId)]),
                    new PyTuple([new PyStr("GetRequiredSkillLevels"), new PyTuple([new PyInt(21857)]), PyNone.Instance]),
                ]),
                MachoVersion()))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        static string BoundId(Send reply)
        {
            var result = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
            var bound = Assert.IsType<PySubStruct>(result.Items[0]);
            var boundTuple = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundTuple.Items[0]).Value.Span);
        }

        async Task<Send> Activate(uint callId, string oid, PyValue target, PyValue repeat)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, callId, oid, "Activate",
                new PyTuple([new PyLong(9988400002220), new PyStr("targetAttack"), target, repeat]),
                MachoVersion()))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        var dockedBind = await Bind(1, stationId, groupStation);
        Assert.Equal(MachoMessage.CallRsp, Envelope(dockedBind).Type);
        var dockedOid = BoundId(dockedBind);
        var noTarget = await Activate(2, dockedOid, PyNone.Instance, new PyInt(1));
        Assert.Equal(MachoMessage.CallRsp, Envelope(noTarget).Type);
        Assert.Equal(new PyInt(1), Result(MarshalCodec.Decode(noTarget.Payload)));
        Assert.Equal((9988400002220L, "targetAttack", 0L, 1), Assert.Single(modules.Activations));

        var target = await Activate(3, dockedOid, new PyLong(9988400002215), new PyInt(5));
        Assert.Equal(MachoMessage.CallRsp, Envelope(target).Type);
        Assert.Equal(new PyInt(1), Result(MarshalCodec.Decode(target.Payload)));
        Assert.Equal((9988400002220L, "targetAttack", 9988400002215L, 5), modules.Activations[1]);

        modules.Answer = ModuleResult.Refusal("MODULE_ALREADY_ACTIVE", 21857, "1MN Civilian Afterburner");
        var refused = await Activate(4, dockedOid, PyNone.Instance, new PyInt(1));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(refused).Type);
        Assert.Equal(3, modules.Activations.Count);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 5, dockedOid, "Deactivate",
            new PyTuple([new PyLong(9988400002220), new PyStr("targetAttack")]),
            MachoVersion()))));
        var stopped = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Envelope(stopped).Type);
        Assert.Equal(new PyInt(1), Result(MarshalCodec.Decode(stopped.Payload)));
        Assert.Equal((9988400002220L, "targetAttack"), Assert.Single(modules.Deactivations));

        client!.Set("stationid", PyNone.Instance);
        Assert.Null(client.DockedLocationId);
        var inSpaceBind = await Bind(6, solarSystemId, groupSolarSystem);
        Assert.Equal(MachoMessage.CallRsp, Envelope(inSpaceBind).Type);
        var inSpaceOid = BoundId(inSpaceBind);
        modules.Answer = ModuleResult.Done;
        var inSpace = await Activate(7, inSpaceOid, PyNone.Instance, new PyInt(1));
        Assert.Equal(MachoMessage.CallRsp, Envelope(inSpace).Type);
        Assert.Equal(new PyInt(1), Result(MarshalCodec.Decode(inSpace.Payload)));
        Assert.Equal((9988400002220L, "targetAttack", 0L, 1), modules.Activations[3]);
    }

    private static MachoPacket Envelope(Send reply) =>
        Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload)));

    private sealed class RecordingModules : ISpaceModules, ISpaceTargeting
    {
        public ModuleResult? Answer { get; set; } = ModuleResult.Done;
        public List<(long ModuleId, string Effect, long Target, int Repeat)> Activations { get; } = [];
        public List<(long ModuleId, string Effect)> Deactivations { get; } = [];

        public Task<ModuleResult?> ActivateAsync(ClientSession session, long moduleId, string effectName, long targetId, int repeat, CancellationToken cancellationToken, IReadOnlyList<double>? aim = null, IReadOnlyList<double>? aimEnd = null)
        {
            Activations.Add((moduleId, effectName, targetId, repeat));
            return Task.FromResult(Answer);
        }

        public Task<ModuleResult?> DeactivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken)
        {
            Deactivations.Add((moduleId, effectName));
            return Task.FromResult<ModuleResult?>(ModuleResult.Done);
        }

        public Task<TargetingResult?> OrderAsync(ClientSession session, TargetingOrder order, IReadOnlyList<long> targets, CancellationToken cancellationToken) =>
            Task.FromResult<TargetingResult?>(null);

        public Task<ModuleResult?> OverloadAsync(ClientSession session, long moduleId, int effectId, bool active, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleResult?>(ModuleResult.Done);

        public Task<ModuleResult?> OfflineAsync(ClientSession session, long moduleId, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleResult?>(ModuleResult.Done);

        public Task<ModuleResult?> OverloadRackAsync(ClientSession session, long moduleId, bool active, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleResult?>(ModuleResult.Done);

        public Task RefreshShipAsync(ClientSession session, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ModuleRepairStart?> StartRepairAsync(ClientSession session, IReadOnlyList<long> moduleIds, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleRepairStart?>(null);

        public Task<ModuleRepairRecord?> EndRepairAsync(ClientSession session, long moduleId, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleRepairRecord?>(null);
    }
}
