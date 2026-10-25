using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task User_service_activation_and_feedback_rpcs_are_dispatched_through_host_wire()
    {
        var training = new WireMultipleTraining();
        var service = new RedeemService(new WireRedeemCallbacks(), multipleTraining: training)
        {
            ActivateReSculpt = (userId, itemId, _) => ValueTask.FromResult(userId == 7 && itemId == 99),
        };
        Start(configure: registry => registry.Register(service), strictRpc: true,
            sessionOpened: session =>
            {
                session.UserId = 7;
                session.CharacterId = 42;
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        long callId = 0;
        async Task<PyValue?> Call(string method, PyValue[] args, params (string Name, PyValue Value)[] extraKwargs)
        {
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
                .. extraKwargs.Select(entry =>
                    new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Name), entry.Value)),
            ]);
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, ++callId, "userSvc", method, new PyTuple(args), kwargs))));
            var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            return Result(response);
        }

        Assert.Equal(PyBool.True, await Call("ActivateCharacterReSculpt", [new PyLong(99)]));
        Assert.Equal(PyBool.True, await Call("CastDownvote", []));
        Assert.Equal(PyBool.True, await Call("CastDownvote", [new PyStr("feedback")]));

        var itemActivation = Assert.IsType<PyList>(await Call(
            "ActivateMultiTraining", [new PyLong(123)], ("trainingID", new PyInt(2))));
        Assert.Equal(new PyList([new PyTuple([new PyInt(2), new PyLong(132_537_600_000_000_000)])]), itemActivation);

        var tokens = new PyList([new PyTuple([new PyLong(456), PyNone.Instance])]);
        var tokenActivation = Assert.IsType<PyList>(await Call(
            "ActivateSoulboundMultiTraining",
            [new PyLong(42), tokens],
            ("trainingID", new PyInt(3)),
            ("useHomeStation", PyBool.True)));
        Assert.Equal(new PyList([new PyTuple([new PyInt(3), new PyLong(132_537_600_000_000_000)])]), tokenActivation);
        Assert.Equal(tokenActivation, await Call("GetUpdatedMultiCharacterTraining", []));
        Assert.Equal(7L, training.ItemActivation.UserId);
        Assert.Equal(123L, training.ItemActivation.ItemId);
        Assert.Equal(2, training.ItemActivation.Slot);
        Assert.Equal(7L, training.TokenActivation.UserId);
        Assert.Equal(tokens, training.TokenActivation.Tokens);
        Assert.Equal(3, training.TokenActivation.Slot);
    }

    private sealed class WireRedeemCallbacks : IRedeemCallbacks
    {
        public ValueTask<PyValue> GetRedeemTokensAsync(long userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(new PyList([]));

        public ValueTask<PyValue> ClaimRedeemTokensAsync(
            long userId, PyValue tokens, long characterId, bool useHomeStation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(PyNone.Instance);

        public ValueTask ReverseRedeemAsync(long userId, PyValue itemId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask TrashRedeemTokensAsync(long userId, PyValue tokens, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class WireMultipleTraining : IMultipleTrainingCallbacks
    {
        private IReadOnlyList<(int Slot, long EndsAt)> _slots = [];
        public (long UserId, long ItemId, int? Slot) ItemActivation { get; private set; }
        public (long UserId, PyValue Tokens, int? Slot) TokenActivation { get; private set; }

        public ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> GetActiveSlotsAsync(
            long userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_slots);

        public ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateCertificatesAsync(
            long userId, PyValue tokens, int? trainingId, CancellationToken cancellationToken)
        {
            TokenActivation = (userId, tokens, trainingId);
            _slots = [(trainingId ?? 2, 132_537_600_000_000_000)];
            return ValueTask.FromResult(_slots);
        }

        public ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateItemAsync(
            long userId, long itemId, int? trainingId, CancellationToken cancellationToken)
        {
            ItemActivation = (userId, itemId, trainingId);
            _slots = [(trainingId ?? 2, 132_537_600_000_000_000)];
            return ValueTask.FromResult(_slots);
        }
    }
}
