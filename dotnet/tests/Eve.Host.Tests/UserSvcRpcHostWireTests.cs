using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task User_service_compatibility_replies_are_dispatched_through_host_wire()
    {
        var training = new WireMultipleTraining();
        var userRpcCalls = new List<string>();
        var redeemCallbacks = new WireRedeemCallbacks();
        var service = new RedeemService(
            redeemCallbacks,
            userRpcHandler: (call, _) =>
            {
                userRpcCalls.Add(call.Method);
                return ValueTask.FromResult<PyValue?>(call.Method switch
                {
                    "GetUserToken" => new PyStr("signed-gateway-token"),
                    "UserLogOffCharacter" => PyBool.True,
                    _ => null,
                });
            },
            multipleTraining: training)
        {
            ActivateReSculpt = (userId, itemId, _) => ValueTask.FromResult(userId == 7 && itemId == 99),
        };
        ClientSession? openedSession = null;
        Start(configure: registry => registry.Register(service), strictRpc: true,
            sessionOpened: session =>
            {
                openedSession = session;
                session.UserId = 7;
                session.CharacterId = 42;
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        long callId = 0;
        async Task<Send> SendCall(string method, PyValue[] args, params (string Name, PyValue Value)[] extraKwargs)
        {
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
                .. extraKwargs.Select(entry =>
                    new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Name), entry.Value)),
            ]);
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, ++callId, "userSvc", method, new PyTuple(args), kwargs))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        async Task<PyValue?> Call(string method, PyValue[] args, params (string Name, PyValue Value)[] extraKwargs) =>
            Result(MarshalCodec.Decode((await SendCall(method, args, extraKwargs)).Payload));

        static PyValue Field(PyDict dict, string name) =>
            dict.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

        Assert.Equal(PyBool.True, await Call("ActivateCharacterReSculpt", [new PyLong(99)]));
        Assert.Equal(PyBool.True, await Call("CastUpvote", []));
        Assert.Equal(PyBool.True, await Call("CastDownvote", []));
        Assert.Equal(PyBool.True, await Call("CastDownvote", [new PyStr("feedback")]));

        var itemActivation = Assert.IsType<PyList>(await Call(
            "ActivateMultiTraining", [new PyLong(123)], ("trainingID", new PyInt(2))));
        Assert.Equal(new PyList([new PyTuple([new PyInt(2), new PyLong(132_537_600_000_000_000)])]), itemActivation);
        var defaultItemActivation = Assert.IsType<PyList>(await Call(
            "ActivateMultiTraining", [new PyLong(125)], ("trainingID", PyNone.Instance)));
        Assert.Equal(new PyInt(2), Assert.IsType<PyTuple>(Assert.Single(defaultItemActivation.Items)).Items[0]);
        Assert.Null(training.ItemActivation.Slot);
        var positionalItemActivation = Assert.IsType<PyList>(await Call(
            "ActivateMultiTraining", [new PyLong(124), new PyInt(2)]));
        Assert.Equal(new PyInt(2), Assert.IsType<PyTuple>(Assert.Single(positionalItemActivation.Items)).Items[0]);

        var soulboundTokens = new PyList([new PyTuple([new PyLong(456), PyNone.Instance])]);
        var tokenActivation = Assert.IsType<PyList>(await Call(
            "ActivateSoulboundMultiTraining",
            [new PyLong(42), soulboundTokens],
            ("trainingID", new PyInt(3)),
            ("useHomeStation", PyBool.True)));
        Assert.Equal(new PyList([new PyTuple([new PyInt(3), new PyLong(132_537_600_000_000_000)])]), tokenActivation);
        Assert.Equal(tokenActivation, await Call("GetUpdatedMultiCharacterTraining", [PyBool.False]));
        Assert.Equal(tokenActivation, await Call("GetUpdatedMultiCharacterTraining", [PyBool.True]));

        var activeSlots = Assert.IsType<PyDict>(await Call("GetMultiCharactersTrainingSlots", []));
        Assert.Equal(new PyInt(3), Assert.Single(activeSlots.Entries).Key);
        Assert.Equal(new PyLong(132_537_600_000_000_000), activeSlots.Entries[0].Value);
        Assert.Same(PyNone.Instance, await Call("InvalidateMultiCharacterTrainingCache", [new PyLong(7)]));
        var defaultTokenActivation = Assert.IsType<PyList>(await Call(
            "ActivateSoulboundMultiTraining",
            [new PyLong(42), soulboundTokens],
            ("trainingID", PyNone.Instance),
            ("useHomeStation", PyBool.False)));
        Assert.Equal(new PyInt(2), Assert.IsType<PyTuple>(Assert.Single(defaultTokenActivation.Items)).Items[0]);
        Assert.Null(training.TokenActivation.Slot);
        var positionalTokenActivation = Assert.IsType<PyList>(await Call(
            "ActivateSoulboundMultiTraining", [new PyLong(42), soulboundTokens, new PyInt(2)]));
        Assert.Equal(new PyInt(2), Assert.IsType<PyTuple>(Assert.Single(positionalTokenActivation.Items)).Items[0]);

        var redeemTokens = Assert.IsType<PyObjectEx>(await Call("GetRedeemTokens", []));
        Assert.Equal(ObjectExKind.Ex2, redeemTokens.Kind);
        Assert.Equal(2, redeemTokens.List.Count);
        var tokenRow = Assert.IsType<PyPackedRow>(redeemTokens.List[0]);
        var tokenFields = tokenRow.Fields().ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(14, tokenFields.Count);
        Assert.Equal(new PyInt(100), tokenFields["tokenID"]);
        Assert.Same(PyNone.Instance, tokenFields["massTokenID"]);
        Assert.Equal(new PyInt(34), tokenFields["typeID"]);
        Assert.Equal(new PyInt(2), tokenFields["quantity"]);
        Assert.Equal(new PyInt(7), tokenFields["blueprintRuns"]);
        Assert.Equal(new PyInt(8), tokenFields["blueprintMaterialLevel"]);
        Assert.Equal(new PyInt(9), tokenFields["blueprintProductivityLevel"]);
        Assert.Equal("Blueprint reward", JsSemantics.StringValue(tokenFields["label"]));
        Assert.Equal("wire reward", JsSemantics.StringValue(tokenFields["description"]));
        Assert.Equal(new PyLong(132_000_000_000_000_000), tokenFields["dateTime"]);
        Assert.Equal(new PyLong(200_000_000_000_000_000), tokenFields["expireDateTime"]);
        Assert.Equal(new PyLong(132_000_000_000_000_000), tokenFields["availableDateTime"]);
        Assert.Equal(new PyInt(60_003_760), tokenFields["stationID"]);
        Assert.Equal(new PyInt(1), tokenFields["soulbound"]);
        var multiMassFields = Assert.IsType<PyPackedRow>(redeemTokens.List[1]).Fields()
            .ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(new PyInt(103), multiMassFields["tokenID"]);
        Assert.Equal(new PyInt(999), multiMassFields["massTokenID"]);

        var claimTokens = new PyList([Py.Dict(
            ("tokenID", new PyInt(100)),
            ("massTokenID", PyNone.Instance),
            ("typeID", new PyInt(34)))]);
        var redeemResult = Assert.IsType<PyDict>(await Call(
            "ClaimRedeemTokens", [claimTokens, new PyLong(42)], ("useHomeStation", PyBool.True)));
        Assert.Empty(Assert.IsType<PyList>(Field(redeemResult, "errors")).Items);
        Assert.Single(Assert.IsType<PyList>(Field(redeemResult, "itemsCreated")).Items);
        Assert.Equal(new PyLong(60_003_760), Field(redeemResult, "stationID"));
        Assert.Equal(new PyReal(12.5), Field(redeemResult, "isk_balance"));
        var claimedTokens = Assert.IsType<PyList>(redeemCallbacks.LastClaimTokens);
        var claimedToken = Assert.IsType<PyDict>(Assert.Single(claimedTokens.Items));
        Assert.Equal(new PyInt(100), Field(claimedToken, "tokenID"));
        Assert.Same(PyNone.Instance, Field(claimedToken, "massTokenID"));
        Assert.Equal(new PyInt(34), Field(claimedToken, "typeID"));
        Assert.Equal(42, redeemCallbacks.LastClaimCharacterId);
        Assert.True(redeemCallbacks.LastClaimUseHomeStation);
        _ = await Call("ClaimRedeemTokens", [claimTokens, new PyLong(42)], ("useHomeStation", PyBool.False));
        Assert.False(redeemCallbacks.LastClaimUseHomeStation);
        Assert.Equal(PyBool.True, await Call("ReverseRedeem", [new PyLong(456)]));
        Assert.Equal(PyBool.True, await Call("TrashRedeemTokens", [claimTokens]));
        Assert.Equal(PyBool.True, await Call("ReportBot", [new PyLong(43)]));
        Assert.Equal(new PyLong(132_000_000_000_000_000L), await Call("GetCreateDate", []));
        Assert.Equal(new PyInt(0), await Call("GetSessionPlaytimeMinutes", []));
        Assert.Equal("user-7", System.Text.Encoding.ASCII.GetString(
            Assert.IsType<PyBytes>(await Call("GetUserName", [new PyLong(7)])).Value.Span));
        Assert.Equal("signed-gateway-token", System.Text.Encoding.ASCII.GetString(
            Assert.IsType<PyBytes>(await Call("GetUserToken", [])).Value.Span));
        Assert.Equal(PyBool.True, await Call("UserLogOffCharacter", []));
        Assert.Contains("GetUserToken", userRpcCalls);
        Assert.Contains("UserLogOffCharacter", userRpcCalls);
        Assert.Equal(PyBool.False, await Call("ReportISKSpammer",
            [new PyLong(43), new PyStr("local"), new PyList([new PyStr("[2026-10-28] Pilot > spam")])]));
        Assert.Equal(PyBool.False, await Call("ReportISKSpammer",
            [new PyLong(43), new PyLong(123), new PyList([new PyStr("[2026-10-28] Pilot > spam")])]));

        training.TokenActivationError = "Omega is required";
        var failedActivation = await SendCall(
            "ActivateSoulboundMultiTraining",
            [new PyLong(42), soulboundTokens],
            ("trainingID", new PyInt(3)));
        var failedPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(failedActivation.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, failedPacket.Type);
        var failedPayload = System.Text.Encoding.ASCII.GetString(failedActivation.Payload.Span);
        Assert.Contains("CustomInfo", failedPayload);
        Assert.Contains("info", failedPayload);
        Assert.Contains("Omega is required", failedPayload);

        openedSession!.Set("accountRole", new PyLong(GameMasterRoles.Qa));
        Assert.Equal(PyBool.False, await Call("CreateRedeemTokenQA", [],
            ("userID", new PyLong(7)),
            ("typeID", new PyInt(34)),
            ("quantity", new PyInt(1)),
            ("blueprintRuns", new PyInt(0)),
            ("blueprintMaterialLevel", new PyInt(0)),
            ("blueprintProductivityLevel", new PyInt(0)),
            ("expiry", PyNone.Instance),
            ("label", new PyStr("wire test")),
            ("description", new PyStr("wire test")),
            ("addedByContext", new PyInt(1)),
            ("addedByExtra", PyNone.Instance),
            ("available", PyNone.Instance),
            ("soulbound", PyBool.False)));

        Assert.Equal(7L, training.ItemActivation.UserId);
        Assert.Equal(124L, training.ItemActivation.ItemId);
        Assert.Equal(2, training.ItemActivation.Slot);
        Assert.Equal(7L, training.TokenActivation.UserId);
        Assert.Equal(soulboundTokens, training.TokenActivation.Tokens);
        Assert.Equal(3, training.TokenActivation.Slot);
    }

    [Fact]
    public async Task Soulbound_multi_training_without_provider_returns_a_user_error_over_wire()
    {
        Start(configure: registry => registry.Register(new RedeemService(new WireRedeemCallbacks())));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var request = Packets.CallWithKwargs(
            open,
            1,
            "userSvc",
            "ActivateSoulboundMultiTraining",
            Py.Tuple(new PyLong(42), new PyList([]), new PyInt(3)),
            kwargs);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
        var failure = Assert.IsType<Send>(await front.ReceiveAsync());
        var error = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(failure.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, error.Type);
        Assert.Contains("CustomInfo", System.Text.Encoding.ASCII.GetString(failure.Payload.Span));
    }

    [Fact]
    public async Task User_service_unhandled_errors_are_error_responses_over_wire()
    {
        ClientSession? openedSession = null;
        var problems = new List<(string Message, Exception? Error)>();
        Start(configure: registry => registry.Register(new RedeemService(new WireRedeemCallbacks())),
            onProblem: (message, error) => problems.Add((message, error)),
            sessionOpened: session => openedSession = session);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        long callId = 0;
        async Task<Send> SendCall(string method, PyValue[] args, params (string Name, PyValue Value)[] extraKwargs)
        {
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
                .. extraKwargs.Select(entry =>
                    new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Name), entry.Value)),
            ]);
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, ++callId, "userSvc", method, new PyTuple(args), kwargs))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        static string ErrorText(Send response)
        {
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload)));
            Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
            return System.Text.Encoding.ASCII.GetString(response.Payload.Span);
        }

        openedSession!.UserId = null;
        Assert.Contains("Unhandled error", ErrorText(await SendCall("GetRedeemTokens", [])));
        Assert.IsType<UnauthorizedAccessException>(Assert.Single(problems).Error);
        Assert.Equal("An authenticated account is required to access redeeming items.",
            problems[0].Error!.Message);

        Assert.Contains("Unhandled error", ErrorText(await SendCall("GetUserToken", [])));
        Assert.IsType<InvalidOperationException>(problems[1].Error);
        Assert.Equal("User token issuance is not wired on this host.", problems[1].Error!.Message);

        openedSession.UserId = 7;
        Assert.Contains("Unhandled error", ErrorText(await SendCall(
            "ClaimRedeemTokens",
            [new PyList([]), new PyInt(0)],
            ("useHomeStation", PyBool.False))));
        Assert.IsType<ArgumentException>(problems[2].Error);
        Assert.Equal("A positive character ID is required. (Parameter 'value')", problems[2].Error!.Message);
    }

    private sealed class WireRedeemCallbacks : IRedeemCallbacks
    {
        public PyValue? LastClaimTokens { get; private set; }
        public long LastClaimCharacterId { get; private set; }
        public bool LastClaimUseHomeStation { get; private set; }

        public ValueTask<PyValue> GetRedeemTokensAsync(long userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(new PyList([
                Token(100, true, true, 200_000_000_000_000_000),
                Token(101, false, false, 200_000_000_000_000_000),
                Token(102, true, false, 1),
                Token(103, true, false, 200_000_000_000_000_000, 999),
            ]));

        public ValueTask<PyValue> ClaimRedeemTokensAsync(
            long userId, PyValue tokens, long characterId, bool useHomeStation, CancellationToken cancellationToken)
        {
            LastClaimTokens = tokens;
            LastClaimCharacterId = characterId;
            LastClaimUseHomeStation = useHomeStation;
            return ValueTask.FromResult<PyValue>(Py.Dict(
                ("errors", new PyList([])),
                ("itemsCreated", new PyList([Py.Dict(
                    ("itemID", new PyLong(800)),
                    ("typeID", new PyInt(34)),
                    ("quantity", new PyLong(1)))])),
                ("activated_licenses", new PyList([])),
                ("isk_rewarded", new PyReal(0)),
                ("isk_balance", new PyReal(12.5)),
                ("skillpoints", new PyInt(0)),
                ("stationID", new PyLong(60_003_760)),
                ("charID", new PyLong(characterId))));
        }

        public ValueTask ReverseRedeemAsync(long userId, PyValue itemId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask TrashRedeemTokensAsync(long userId, PyValue tokens, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        private static PyDict Token(long tokenId, bool available, bool soulbound, long expiresAt, long? massTokenId = null) => Py.Dict(
            ("tokenID", new PyLong(tokenId)),
            ("massTokenID", new PyLong(massTokenId ?? tokenId)),
            ("typeID", new PyInt(34)),
            ("quantity", new PyLong(2)),
            ("blueprintRuns", new PyInt(7)),
            ("blueprintMaterialLevel", new PyInt(8)),
            ("blueprintProductivityLevel", new PyInt(9)),
            ("label", new PyStr("Blueprint reward")),
            ("description", new PyStr("wire reward")),
            ("dateTime", new PyLong(132_000_000_000_000_000)),
            ("expireDateTime", new PyLong(expiresAt)),
            ("availableDateTime", new PyLong(132_000_000_000_000_000)),
            ("stationID", new PyLong(60_003_760)),
            ("soulbound", soulbound ? PyBool.True : PyBool.False),
            ("available", available ? PyBool.True : PyBool.False));
    }

    private sealed class WireMultipleTraining : IMultipleTrainingCallbacks
    {
        private IReadOnlyList<(int Slot, long EndsAt)> _slots = [];
        public (long UserId, long ItemId, int? Slot) ItemActivation { get; private set; }
        public (long UserId, PyValue Tokens, int? Slot) TokenActivation { get; private set; }
        public string? TokenActivationError { get; set; }

        public ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> GetActiveSlotsAsync(
            long userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_slots);

        public ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateCertificatesAsync(
            long userId, PyValue tokens, int? trainingId, CancellationToken cancellationToken)
        {
            TokenActivation = (userId, tokens, trainingId);
            if (TokenActivationError is { } error) throw new InvalidOperationException(error);
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