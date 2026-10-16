using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class RedeemUserSvcCompatibilityTests
{
    [Fact]
    public async Task User_svc_exposes_the_complete_reference_surface_and_uses_account_reads()
    {
        var service = (IService)(object)new RedeemService(
            new FakeCallbacks(),
            (userId, _) => ValueTask.FromResult<string?>(userId == 35 ? "capsuleer" : null),
            (characterId, _) => ValueTask.FromResult<long?>(characterId == 90000003 ? 133801234567890123 : null));
        string[] methods =
        [
            "GetRedeemTokens", "ClaimRedeemTokens", "ReverseRedeem", "TrashRedeemTokens",
            "GetMultiCharactersTrainingSlots", "GetUpdatedMultiCharacterTraining", "ActivateMultiTraining",
            "ActivateSoulboundMultiTraining", "InvalidateMultiCharacterTrainingCache", "CreateRedeemTokenQA",
            "ActivateCharacterReSculpt", "CastUpvote", "CastDownvote", "ReportBot", "ReportISKSpammer",
            "GetCreateDate", "GetSessionPlaytimeMinutes", "GetUserName", "GetUserToken", "UserLogOffCharacter",
        ];
        Assert.All(methods, method => Assert.True(service.HasMethod(method), method));
        var session = new ClientSession { UserId = 35, CharacterId = 90000003 };

        Assert.Equal(new PyLong(133801234567890123), await Call(service, session, "GetCreateDate"));
        Assert.Equal(new PyInt(0), await Call(service, session, "GetSessionPlaytimeMinutes"));
        Assert.Equal(new PyStr("capsuleer"), await Call(service, session, "GetUserName"));
        Assert.Equal(new PyStr("evejs-local-token-35-90000003"), await Call(service, session, "GetUserToken"));

        // Without a Multiple Character Training store the account has no extra queues, and no activation grants one.
        Assert.Empty(Assert.IsType<PyDict>(await Call(service, session, "GetMultiCharactersTrainingSlots")).Entries);
        Assert.Empty(Assert.IsType<PyList>(await Call(service, session, "GetUpdatedMultiCharacterTraining")).Items);
        Assert.Empty(Assert.IsType<PyList>(await Call(service, session, "ActivateSoulboundMultiTraining")).Items);
        // the refusal is the reference's CustomInfo text (UserSvcCompatibility.ActivateMultiTraining), not a missing method
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(service, session, "ActivateMultiTraining"));
        Assert.Equal("CustomInfo", refused.Error.UserErrorMessage);
        Assert.Empty(Assert.IsType<PyDict>(await Call(service, session, "GetMultiCharactersTrainingSlots")).Entries);

        // GATE-C: the votes and reports answer true and keep nothing (EveJS userService.js 1104-1127)
        Assert.Same(PyBool.True, await Call(service, session, "CastUpvote"));
        Assert.Same(PyBool.True, await Call(service, session, "CastDownvote", new PyStr("feedback")));
        Assert.Same(PyBool.True, await Call(service, session, "ReportBot", new PyInt(90000004)));
        Assert.Same(PyBool.True, await Call(service, session, "ReportISKSpammer", new PyInt(90000004)));
        Assert.Same(PyBool.False, await Call(service, session, "CreateRedeemTokenQA"));
    }

    [Fact]
    public async Task User_svc_mct_queues_are_read_and_activated_through_the_account_store()
    {
        var store = new InMemoryMultipleTraining();
        var userRpcCalls = new List<string>();
        var service = (IService)(object)new RedeemService(
            new FakeCallbacks(),
            userRpcHandler: (call, _) =>
            {
                userRpcCalls.Add(call.Method);
                return ValueTask.FromResult<PyValue?>(null);
            },
            multipleTraining: store);
        var session = new ClientSession { UserId = 35, CharacterId = 90000003 };

        AssertSlots(Assert.IsType<PyDict>(await Call(service, session, "GetMultiCharactersTrainingSlots")));
        AssertSlotList(Assert.IsType<PyList>(await Call(service, session, "GetUpdatedMultiCharacterTraining")));

        // Positional form: ActivateSoulboundMultiTraining(charID, tokens, trainingID) extends queue 3 and returns the queues that run.
        var positional = Assert.IsType<PyList>(await Call(
            service, session, "ActivateSoulboundMultiTraining", new PyLong(90000003), new PyList([new PyInt(7)]), new PyInt(3)));
        AssertSlotList(positional, (3, SlotEndsAt));
        var first = Assert.Single(store.Activations);
        Assert.Equal(35, first.UserId);
        Assert.Equal(new PyInt(7), Assert.Single(Assert.IsType<PyList>(first.Tokens).Items));
        Assert.Equal(3, first.TrainingId);
        AssertSlots(Assert.IsType<PyDict>(await Call(service, session, "GetMultiCharactersTrainingSlots")), (3, SlotEndsAt));

        // Keyword form: the trainingID kwarg names queue 2, which then runs beside queue 3.
        var keyword = await service.CallAsync(new ServiceCall
        {
            Method = "ActivateSoulboundMultiTraining",
            Args = [new PyLong(90000003), new PyList([new PyInt(8)])],
            Kwargs = new PyDict([new(new PyStr("trainingID"), new PyInt(2))]),
            Session = session,
        });
        AssertSlotList(Assert.IsType<PyList>(keyword), (2, SlotEndsAt), (3, SlotEndsAt));
        Assert.Equal(2, store.Activations[1].TrainingId);

        // No trainingID: the server chooses the queue (the fake store picks queue 2 for it).
        await Call(service, session, "ActivateSoulboundMultiTraining", new PyLong(90000003), new PyList([new PyInt(9)]));
        Assert.Null(store.Activations[2].TrainingId);
        AssertSlots(Assert.IsType<PyDict>(await Call(service, session, "GetMultiCharactersTrainingSlots")), (2, SlotEndsAt), (3, SlotEndsAt));

        // The item activation is refused: nothing is consumed and no queue is granted.
        await Assert.ThrowsAsync<NotSupportedException>(() => Call(service, session, "ActivateMultiTraining", new PyLong(90000003), new PyInt(34)));
        Assert.Equal(3, store.Activations.Count);
        AssertSlots(Assert.IsType<PyDict>(await Call(service, session, "GetMultiCharactersTrainingSlots")), (2, SlotEndsAt), (3, SlotEndsAt));

        // The queue read is served by the store, never by the user RPC override: only the invalidation reaches the override.
        Assert.Same(PyNone.Instance, await Call(service, session, "InvalidateMultiCharacterTrainingCache"));
        Assert.Equal(["InvalidateMultiCharacterTrainingCache"], userRpcCalls);
    }

    [Fact]
    public async Task Create_redeem_token_qa_rejects_invalid_input_before_provider_dispatch()
    {
        var dispatched = false;
        var service = (IService)(object)new RedeemService(
            new FakeCallbacks(),
            userRpcHandler: (_, _) =>
            {
                dispatched = true;
                return ValueTask.FromResult<PyValue?>(new PyDict([]));
            });
        var call = new ServiceCall
        {
            Method = "CreateRedeemTokenQA",
            Args = [],
            Kwargs = new PyDict([new(new PyStr("typeID"), new PyInt(0))]),
            Session = new ClientSession { UserId = 35 },
        };

        Assert.Same(PyBool.False, await service.CallAsync(call));
        Assert.False(dispatched);

        var fractionalTypeIdCall = new ServiceCall
        {
            Method = "CreateRedeemTokenQA",
            Args = [],
            Kwargs = new PyDict([new(new PyStr("typeID"), new PyReal(34.5))]),
            Session = new ClientSession { UserId = 35 },
        };
        Assert.Same(PyBool.False, await service.CallAsync(fractionalTypeIdCall));
        Assert.False(dispatched);
    }

    [Fact]
    public async Task Create_redeem_token_qa_dispatches_valid_type_ids()
    {
        var dispatched = false;
        var payload = new PyDict([]);
        var service = (IService)(object)new RedeemService(
            new FakeCallbacks(),
            userRpcHandler: (_, _) =>
            {
                dispatched = true;
                return ValueTask.FromResult<PyValue?>(payload);
            });
        var call = new ServiceCall
        {
            Method = "CreateRedeemTokenQA",
            Args = [],
            Kwargs = new PyDict([new(new PyStr("typeID"), new PyStr("34"))]),
            Session = new ClientSession { UserId = 35 },
        };

        Assert.Same(payload, await service.CallAsync(call));
        Assert.True(dispatched);
    }

    [Fact]
    public async Task Create_redeem_token_qa_forwards_valid_input_to_provider_and_returns_its_payload()
    {
        ServiceCall? received = null;
        var payload = new PyDict([new(new PyStr("tokenID"), new PyLong(700000001))]);
        var service = (IService)(object)new RedeemService(
            new FakeCallbacks(),
            userRpcHandler: (call, _) =>
            {
                received = call;
                return ValueTask.FromResult<PyValue?>(payload);
            });
        var call = new ServiceCall
        {
            Method = "CreateRedeemTokenQA",
            Args = [],
            Kwargs = new PyDict(
            [
                new(new PyStr("userID"), new PyStr("35")),
                new(new PyStr("typeID"), new PyInt(34)),
                new(new PyStr("quantity"), new PyInt(2)),
            ]),
            Session = new ClientSession { UserId = 35 },
        };

        Assert.Same(payload, await service.CallAsync(call));
        Assert.Same(call, received);
    }

    private const long SlotEndsAt = 133801234567890123;

    private static void AssertSlots(PyDict slots, params (int Slot, long EndsAt)[] expected)
    {
        Assert.Equal(expected.Length, slots.Entries.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(new PyInt(expected[i].Slot), slots.Entries[i].Key);
            Assert.Equal(new PyLong(expected[i].EndsAt), slots.Entries[i].Value);
        }
    }

    private static void AssertSlotList(PyList slots, params (int Slot, long EndsAt)[] expected)
    {
        Assert.Equal(expected.Length, slots.Items.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var pair = Assert.IsType<PyTuple>(slots.Items[i]);
            Assert.Equal(new PyInt(expected[i].Slot), pair.Items[0]);
            Assert.Equal(new PyLong(expected[i].EndsAt), pair.Items[1]);
        }
    }

    /// <summary>An in-memory account store: activations add or replace a queue and are recorded as the store received them.</summary>
    private sealed class InMemoryMultipleTraining : IMultipleTrainingCallbacks
    {
        private readonly List<(int Slot, long EndsAt)> _slots = [];

        public List<(long UserId, PyValue Tokens, int? TrainingId)> Activations { get; } = [];

        public ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> GetActiveSlotsAsync(long authenticatedUserId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<(int Slot, long EndsAt)>>(_slots.OrderBy(slot => slot.Slot).ToList());

        public ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateCertificatesAsync(
            long authenticatedUserId, PyValue tokens, int? trainingId, CancellationToken cancellationToken)
        {
            Activations.Add((authenticatedUserId, tokens, trainingId));
            var slot = trainingId ?? 2;
            _slots.RemoveAll(existing => existing.Slot == slot);
            _slots.Add((slot, SlotEndsAt));
            return GetActiveSlotsAsync(authenticatedUserId, cancellationToken);
        }

        // this fake answers the token path only; the item path is the Host's (CharacterHostTests)
        public ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateItemAsync(
            long authenticatedUserId, long itemId, int? trainingId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session }).AsTask();

    private sealed class FakeCallbacks : IRedeemCallbacks
    {
        public ValueTask<PyValue> GetRedeemTokensAsync(long userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(new PyList([]));

        public ValueTask<PyValue> ClaimRedeemTokensAsync(long userId, PyValue tokens, long characterId, bool useHomeStation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(PyNone.Instance);

        public ValueTask ReverseRedeemAsync(long userId, PyValue itemId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask TrashRedeemTokensAsync(long userId, PyValue tokens, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
