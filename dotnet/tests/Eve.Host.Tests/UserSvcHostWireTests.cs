using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class UserSvcHostWireTests
{
    [PgFact]
    public async Task Registered_user_service_replies_match_the_v24_consumers()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var userId = await rig.CreateAccountAsync();
        Assert.Equal(1, userId);
        var characterId = await rig.CreateCharacterAsync();
        var session = new ClientSession { UserId = userId, CharacterId = characterId };
        session.Set("stationid", new PyLong(rig.Station));
        rig.Sessions.Enter(characterId, session);
        var userSvc = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("userSvc"));

        foreach (var method in new[]
        {
            "CastUpvote", "ClaimRedeemTokens", "GetMultiCharactersTrainingSlots", "GetRedeemTokens",
            "GetUpdatedMultiCharacterTraining", "GetUserToken", "InvalidateMultiCharacterTrainingCache",
            "ReportBot", "ReportISKSpammer", "ReverseRedeem", "TrashRedeemTokens", "UserLogOffCharacter",
        })
            Assert.True(userSvc.HasMethod(method), method);

        Assert.Equal(PyBool.True, await Call(userSvc, session, "CastUpvote"));
        Assert.Empty(Assert.IsType<PyDict>(await Call(userSvc, session, "GetMultiCharactersTrainingSlots")).Entries);
        Assert.Empty(Assert.IsType<PyList>(await Call(userSvc, session, "GetUpdatedMultiCharacterTraining", PyBool.False)).Items);
        Assert.IsType<PyNone>(await Call(userSvc, session, "InvalidateMultiCharacterTrainingCache", new PyLong(userId)));

        var tokenId = await CreateRedeemTokenAsync(userSvc, session, 34);
        var tokens = Assert.IsType<PyObjectEx>(await Call(userSvc, session, "GetRedeemTokens"));
        Assert.Equal(ObjectExKind.Ex2, tokens.Kind);
        Assert.Single(tokens.List);

        var redeemResult = Assert.IsType<PyDict>(await Call(userSvc, session, "ClaimRedeemTokens",
            [new PyList([Py.Dict(("tokenID", new PyLong(tokenId)), ("massTokenID", PyNone.Instance), ("typeID", new PyInt(34)))]),
                new PyLong(characterId)],
            Py.Dict(("useHomeStation", PyBool.False))));
        Assert.Empty(Assert.IsType<PyList>(Field(redeemResult, "errors")).Items);
        var itemsCreated = Assert.IsType<PyList>(Field(redeemResult, "itemsCreated"));
        Assert.Single(itemsCreated.Items);
        Assert.Empty(Assert.IsType<PyList>(Field(redeemResult, "activated_licenses")).Items);
        Assert.Equal(new PyReal(0), Field(redeemResult, "isk_rewarded"));
        Assert.True(Field(redeemResult, "skillpoints") is PyInt { Value: 0 });
        Assert.Equal(new PyLong(characterId), Field(redeemResult, "charID"));
        Assert.Equal(new PyLong(rig.Station), Field(redeemResult, "stationID"));
        Assert.True(Field(redeemResult, "isk_balance") is PyNone or PyReal);
        Assert.Empty(Assert.IsType<PyObjectEx>(await Call(userSvc, session, "GetRedeemTokens")).List);
        Assert.Contains(await new PgItemStore(rig.Source, rig.InventorySchema).OwnedByAsync(characterId),
            item => item.TypeId == 34 && item.Quantity == 2);

        var trashToken = await CreateRedeemTokenAsync(userSvc, session, 35);
        Assert.Single(Assert.IsType<PyObjectEx>(await Call(userSvc, session, "GetRedeemTokens")).List);
        Assert.Equal(PyBool.True, await Call(userSvc, session, "TrashRedeemTokens",
            new PyList([Py.Tuple(new PyLong(trashToken), PyNone.Instance)])));
        Assert.Empty(Assert.IsType<PyObjectEx>(await Call(userSvc, session, "GetRedeemTokens")).List);

        var reverseToken = await CreateRedeemTokenAsync(userSvc, session, 36);
        Assert.Equal(PyBool.True, await Call(userSvc, session, "ReverseRedeem", new PyLong(reverseToken)));
        Assert.Empty(Assert.IsType<PyObjectEx>(await Call(userSvc, session, "GetRedeemTokens")).List);

        var token = Assert.IsType<PyStr>(await Call(userSvc, session, "GetUserToken"));
        Assert.NotEmpty(token.Value);
        Assert.Equal(PyBool.True, await Call(userSvc, session, "ReportBot", new PyLong(characterId + 1)));
        Assert.Equal(PyBool.True, await Call(userSvc, session, "ReportISKSpammer",
            new PyLong(characterId + 1), new PyStr("local"), new PyList([])));

        Assert.Equal(PyBool.True, await Call(userSvc, session, "UserLogOffCharacter"));
    }

    private static async Task<long> CreateRedeemTokenAsync(IService userSvc, ClientSession session, int typeId)
    {
        session.Set("accountRole", new PyLong(GameMasterRoles.Qa));
        var created = Assert.IsType<PyObject>(await Call(userSvc, session, "CreateRedeemTokenQA", [],
            Py.Dict(("typeID", new PyInt(typeId)), ("quantity", new PyInt(2)), ("label", new PyStr("wire test")))));
        return Number(Field(Assert.IsType<PyDict>(created.Args), "tokenID"));
    }

    private static long _callId;

    private static ValueTask<PyValue> Call(
        IService service,
        ClientSession session,
        string method,
        params PyValue[] args) =>
        service.CallAsync(new ServiceCall
        {
            Session = session,
            Method = method,
            Args = args,
            Kwargs = new PyDict([]),
            CallId = new PyInt(Interlocked.Increment(ref _callId)),
        });

    private static ValueTask<PyValue> Call(
        IService service,
        ClientSession session,
        string method,
        PyValue[] args,
        PyDict kwargs) =>
        service.CallAsync(new ServiceCall
        {
            Session = session,
            Method = method,
            Args = args,
            Kwargs = kwargs,
            CallId = new PyInt(Interlocked.Increment(ref _callId)),
        });

    private static PyValue Field(PyDict fields, string name) =>
        fields.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException("Expected a numeric token ID."),
    };
}
