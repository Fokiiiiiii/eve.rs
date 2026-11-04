using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Fighter_manager_maps_and_state_shapes_cross_the_registered_host_rpc()
    {
        var provider = new WireFixtureFighters();
        var fighterManager = new FighterMgrService { Provider = provider };
        Start(configure: registry => registry.Register(fighterManager));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<PyValue?> Call(string method, PyValue[] args, PyValue? kwargs = null, uint correlationId = 1)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, correlationId, "fighterMgr", method, new PyTuple(args), kwargs ?? PyNone.Instance))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        var fighters = Assert.IsType<PyTuple>(await Call("GetFightersForShip", []));
        Assert.Equal(3, fighters.Items.Count);
        Assert.Equal(
            new PyTuple([new PyInt(158), new PyInt(903), new PyInt(32366), new PyInt(9)]),
            Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(fighters.Items[0]).Items)));
        Assert.Equal(
            new PyTuple([new PyInt(159), new PyInt(904), new PyInt(32366), new PyInt(8)]),
            Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(fighters.Items[1]).Items)));
        var abilitiesByFighter = Assert.IsType<PyDict>(fighters.Items[2]);
        var fighterAbilities = Assert.Single(abilitiesByFighter.Entries);
        Assert.Equal(new PyInt(904), fighterAbilities.Key);
        var abilityStates = Assert.IsType<PyTuple>(fighterAbilities.Value);
        Assert.Equal(2, abilityStates.Items.Count);
        Assert.Empty(Assert.IsType<PyDict>(abilityStates.Items[0]).Entries);
        Assert.Empty(Assert.IsType<PyDict>(abilityStates.Items[1]).Entries);

        var machoVersion = Py.Dict(("machoVersion", new PyInt(3396210)));
        var ids = new PyList([new PyInt(903), new PyInt(904)]);
        foreach (var (method, extraArgs, correlationId) in new[]
        {
            ("CmdActivateAbilitySlots", new PyValue[] { new PyInt(1), new PyInt(900001) }, 2u),
            ("CmdDeactivateAbilitySlots", new PyValue[] { new PyInt(1) }, 3u),
            ("LaunchFightersFromTubes", Array.Empty<PyValue>(), 4u),
            ("RecallFightersToTubes", Array.Empty<PyValue>(), 5u),
        })
        {
            var args = new PyValue[] { ids }.Concat(extraArgs).ToArray();
            var errorsById = Assert.IsType<PyDict>(await Call(method, args, machoVersion, correlationId));
            Assert.Equal(new[] { new PyInt(903), new PyInt(904) }, errorsById.Entries.Select(entry => entry.Key));
            // The UI calls iteritems() on this ordinary response dictionary: both successful and refused fighters remain addressable.
            Assert.Same(PyNone.Instance, errorsById.Entries[0].Value);
            Assert.Equal("WIRE_FIXTURE_FIGHTER_REFUSED",
                new MachoError(MachoErrorType.WrappedException, errorsById.Entries[1].Value).UserErrorMessage);
            Assert.Same(PyNone.Instance, provider.Calls[^1].Kwargs);
            Assert.Equal(method, provider.Calls[^1].Method);
        }

        Assert.Same(PyNone.Instance, await Call("ExecuteMovementCommandOnFighters",
            [ids, new PyStr("STOP")], machoVersion, 6));
        Assert.Same(PyNone.Instance, await Call("CmdAbandonFighter", [new PyInt(903)], PyNone.Instance, 7));
        Assert.Same(PyNone.Instance, await Call("CmdScoopAbandonedFighterFromSpace",
            [new PyInt(904), new PyInt(158)], PyNone.Instance, 8));
        Assert.Same(PyNone.Instance, await Call("LoadFightersToTube",
            [new PyInt(903), new PyInt(159)], PyNone.Instance, 9));
        Assert.Same(PyNone.Instance, await Call("UnloadTubeToFighterBay",
            [new PyInt(159)], PyNone.Instance, 10));

        Assert.Equal(10, provider.Calls.Count);
        Assert.Equal("ExecuteMovementCommandOnFighters", provider.Calls[5].Method);
        Assert.Equal(ids, provider.Calls[5].Args[0]);
        Assert.Equal("STOP", T(provider.Calls[5].Args[1]));
    }

    [Fact]
    public async Task Victory_point_states_cross_the_registered_host_rpc_in_client_shapes()
    {
        const int localSystemId = 30_000_142;
        const int remoteSystemId = 30_000_143;
        var stateBySystem = new Dictionary<long, FwVictoryPointState>
        {
            [localSystemId] = new(125, 75_000),
            [remoteSystemId] = new(300.5, 80_000),
        };
        var reads = 0;
        var service = new FwVictoryPointMgrService(new FwVictoryPointRpcCallbacks(_ =>
        {
            reads++;
            return ValueTask.FromResult<IReadOnlyDictionary<long, FwVictoryPointState>>(stateBySystem);
        }));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("solarsystemid2", new PyInt(localSystemId)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<PyValue?> Call(string method, uint correlationId)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, correlationId, "fwVictoryPointMgr", method))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        var all = Assert.IsType<PyDict>(await Call("GetAllVictoryPointStates", 1));
        Assert.Equal(2, all.Entries.Count);
        var localState = all.Entries.Single(entry => entry.Key == new PyInt(localSystemId)).Value;
        Assert.Equal(ObjectExKind.Ex1, Assert.IsType<PyObjectEx>(localState).Kind);
        var remoteState = all.Entries.Single(entry => entry.Key == new PyInt(remoteSystemId)).Value;
        Assert.Equal(new PyList([new PyReal(300.5), new PyInt(80_000)]),
            Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(remoteState).Header).Items[1]);

        var local = Assert.IsType<PyList>(await Call("GetLocalVictoryPointState", 2));
        Assert.Equal(new PyInt(localSystemId), local.Items[0]);
        Assert.Equal(localState, local.Items[1]);
        Assert.Equal(2, reads);
    }

    private sealed class WireFixtureFighters : ISpaceFighters
    {
        public List<ServiceCall> Calls { get; } = [];

        public ValueTask<PyValue> GetFightersForShipAsync(ServiceCall call) =>
            Answer(call, Py.Tuple(
                new PyList([Py.Tuple(new PyInt(158), new PyInt(903), new PyInt(32366), new PyInt(9))]),
                new PyList([Py.Tuple(new PyInt(159), new PyInt(904), new PyInt(32366), new PyInt(8))]),
                new PyDict([
                    new KeyValuePair<PyValue, PyValue>(
                        new PyInt(904),
                        Py.Tuple(new PyDict([]), new PyDict([]))),
                ])));

        public ValueTask<PyValue> LoadFightersToTubeAsync(ServiceCall call) => Answer(call, PyNone.Instance);
        public ValueTask<PyValue> UnloadTubeToFighterBayAsync(ServiceCall call) => Answer(call, PyNone.Instance);
        public ValueTask<PyValue> ExecuteMovementCommandOnFightersAsync(ServiceCall call) => Answer(call, PyNone.Instance);
        public ValueTask<PyValue> CmdAbandonFighterAsync(ServiceCall call) => Answer(call, PyNone.Instance);
        public ValueTask<PyValue> CmdScoopAbandonedFighterFromSpaceAsync(ServiceCall call) => Answer(call, PyNone.Instance);

        public ValueTask<PyValue> LaunchFightersFromTubesAsync(ServiceCall call) => Answer(call, PerId(call));
        public ValueTask<PyValue> RecallFightersToTubesAsync(ServiceCall call) => Answer(call, PerId(call));
        public ValueTask<PyValue> CmdActivateAbilitySlotsAsync(ServiceCall call) => Answer(call, PerId(call));
        public ValueTask<PyValue> CmdDeactivateAbilitySlotsAsync(ServiceCall call) => Answer(call, PerId(call));

        private ValueTask<PyValue> Answer(ServiceCall call, PyValue answer)
        {
            Calls.Add(call);
            return ValueTask.FromResult(answer);
        }

        private static PyDict PerId(ServiceCall call)
        {
            var values = call.Arg(0) switch
            {
                PyList list => list.Items,
                PyTuple tuple => tuple.Items,
                _ => [],
            };
            return new PyDict(values.Select(id => new KeyValuePair<PyValue, PyValue>(id,
                id == new PyInt(903)
                    ? PyNone.Instance
                    : MachoErrors.UserErrorPayload("WIRE_FIXTURE_FIGHTER_REFUSED"))).ToArray());
        }
    }
}
