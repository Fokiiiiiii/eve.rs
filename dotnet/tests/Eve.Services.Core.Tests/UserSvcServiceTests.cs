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
        Assert.Equal(new PyDict([]), await Call(service, session, "GetMultiCharactersTrainingSlots"));
        Assert.Equal(new PyList([]), await Call(service, session, "GetUpdatedMultiCharacterTraining"));
        Assert.Equal(new PyList([]), await Call(service, session, "ActivateMultiTraining"));
        Assert.Equal(new PyList([]), await Call(service, session, "ActivateSoulboundMultiTraining"));
        Assert.Same(PyBool.True, await Call(service, session, "ReportBot", new PyInt(90000004)));
        Assert.Same(PyBool.False, await Call(service, session, "CreateRedeemTokenQA"));
    }

    [Fact]
    public async Task User_svc_mct_callbacks_receive_the_original_call_and_control_the_wire_result()
    {
        var calls = new List<ServiceCall>();
        var expected = new Dictionary<string, PyValue>
        {
            ["GetMultiCharactersTrainingSlots"] = new PyDict([new(new PyInt(2), new PyLong(133801234567890123))]),
            ["GetUpdatedMultiCharacterTraining"] = new PyList([Py.Tuple(new PyInt(2), new PyLong(133801234567890123))]),
            ["ActivateMultiTraining"] = new PyList([Py.Tuple(new PyInt(2), new PyLong(133801234567890123))]),
            ["ActivateSoulboundMultiTraining"] = new PyList([Py.Tuple(new PyInt(2), new PyLong(133801234567890123))]),
        };
        var service = (IService)(object)new RedeemService(
            new FakeCallbacks(),
            userRpcHandler: (call, _) =>
            {
                calls.Add(call);
                return ValueTask.FromResult<PyValue?>(expected[call.Method]);
            });
        var session = new ClientSession { UserId = 35, CharacterId = 90000003 };

        foreach (var (method, response) in expected)
        {
            var call = new ServiceCall
            {
                Method = method,
                Args = [new PyStr("first"), new PyList([new PyInt(7)])],
                Kwargs = new PyDict([new(new PyStr("trainingID"), new PyInt(2))]),
                Session = session,
            };

            Assert.Same(response, await service.CallAsync(call));
            Assert.Same(call, calls[^1]);
        }
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
