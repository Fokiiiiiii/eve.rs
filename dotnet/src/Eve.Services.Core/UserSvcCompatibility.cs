using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed partial class RedeemService
{
    private const long OldAccountCreateFileTime = 132000000000000000L;

    /// <summary>The extra training queues that run now (Multiple Character Training), as slot number to end time (FILETIME).</summary>
    [Method("GetMultiCharactersTrainingSlots")]
    public async ValueTask<PyValue> GetMultiCharactersTrainingSlots(ServiceCall call)
    {
        var slots = await ActiveTrainingSlotsAsync(call);
        return new PyDict([.. slots.Select(slot => new KeyValuePair<PyValue, PyValue>(new PyInt(slot.Slot), Py.FileTimeLong(slot.EndsAt)))]);
    }

    /// <summary>The same queues as a list of (slot, end time) pairs, the form the activation window reads.</summary>
    [Method("GetUpdatedMultiCharacterTraining")]
    public async ValueTask<PyValue> GetUpdatedMultiCharacterTraining(ServiceCall call)
    {
        var slots = await ActiveTrainingSlotsAsync(call);
        return new PyList([.. slots.Select(slot => (PyValue)Py.Tuple(new PyInt(slot.Slot), Py.FileTimeLong(slot.EndsAt)))]);
    }

    /// <summary>
    /// <c>ActivateMultiTraining(itemID, trainingID=...)</c>: uses a certificate item of the account. The item is consumed and the queue gets

    /// </summary>
    [Method("ActivateMultiTraining")]
    public async ValueTask<PyValue> ActivateMultiTraining(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var requestedTraining = call.Args.Count > 1 ? call.Arg(1) : call.Kwarg("trainingID") ?? PyNone.Instance;
        if (_multipleTraining is null)
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Multiple Character Training is not available on this server.")));
        var itemId = call.Arg(0) switch { PyInt i => i.Value, PyLong l => l.Value, _ => 0L };
        try
        {
            var slots = await _multipleTraining.ActivateItemAsync(AuthenticatedUserId(call), itemId, TrainingSlotArgument(requestedTraining), call.Cancellation);
            return new PyList([.. slots.Select(slot => (PyValue)Py.Tuple(new PyInt(slot.Slot), Py.FileTimeLong(slot.EndsAt)))]);
        }
        catch (InvalidOperationException error)
        {
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(error.Message)));
        }
    }

    /// <summary>Activates the soulbound certificates in the tokens (<c>ActivateSoulboundMultiTraining(charID, tokens, trainingID)</c>).</summary>
    [Method("ActivateSoulboundMultiTraining")]
    public async ValueTask<PyValue> ActivateSoulboundMultiTraining(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var requestedTraining = call.Args.Count > 2 ? call.Arg(2) : call.Kwarg("trainingID") ?? PyNone.Instance;
        var slots = _multipleTraining is null
            ? []
            : await _multipleTraining.ActivateCertificatesAsync(AuthenticatedUserId(call), call.Arg(1), TrainingSlotArgument(requestedTraining), call.Cancellation);
        return new PyList([.. slots.Select(slot => (PyValue)Py.Tuple(new PyInt(slot.Slot), Py.FileTimeLong(slot.EndsAt)))]);
    }

    /// <summary>
    /// The queue list is read from the account store on every call (there is no cache of it on this server), so there is nothing to
    /// invalidate: the answer is None, which the client does not read.
    /// </summary>
    [Method("InvalidateMultiCharacterTrainingCache")]
    public async ValueTask<PyValue> InvalidateMultiCharacterTrainingCache(ServiceCall call) =>
        await OverrideUserCallAsync(call, PyNone.Instance);

    private async ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActiveTrainingSlotsAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        return _multipleTraining is null
            ? []
            : await _multipleTraining.GetActiveSlotsAsync(AuthenticatedUserId(call), call.Cancellation);
    }

    /// <summary>The queue the client named (2 or 3), or null when it named none: the server then chooses the queue.</summary>
    private static int? TrainingSlotArgument(PyValue value)
    {
        var number = JsSemantics.ToNumber(value);
        return double.IsFinite(number) && number >= 2 && number <= int.MaxValue ? (int)Math.Truncate(number) : null;
    }

    [Method("CreateRedeemTokenQA")]
    public async ValueTask<PyValue> CreateRedeemTokenQA(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var accountId = PositiveInteger(call.Kwarg("userID") ?? PyNone.Instance);
        if (accountId <= 0) accountId = call.Session.UserId ?? 0;
        if (accountId is <= 0 or > int.MaxValue || PositiveInteger(call.Kwarg("typeID") ?? PyNone.Instance) is <= 0 or > int.MaxValue)
            return PyBool.False;
        return await OverrideUserCallAsync(call, PyBool.False);
    }

    /// <summary>
    /// <c>ActivateCharacterReSculpt(itemID)</c>: a re-sculpt certificate of the account's characters is consumed and the character's recustomization
    /// state is set (see <see cref="ActivateReSculpt"/>). Anything else (a missing or another item) answers False and changes nothing.
    /// </summary>
    [Method("ActivateCharacterReSculpt")]
    public async ValueTask<PyValue> ActivateCharacterReSculpt(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var itemId = call.Arg(0) switch { PyInt i => i.Value, PyLong l => l.Value, _ => 0L };
        var activated = ActivateReSculpt is not null && itemId > 0
            && await ActivateReSculpt(AuthenticatedUserId(call), itemId, call.Cancellation);
        return await OverrideUserCallAsync(call, activated ? PyBool.True : PyBool.False);
    }


    [Method("CastUpvote")]
    public async ValueTask<PyValue> CastUpvote(ServiceCall call) =>
        await OverrideUserCallAsync(call, PyBool.True);


    [Method("CastDownvote")]
    public async ValueTask<PyValue> CastDownvote(ServiceCall call) =>
        await OverrideUserCallAsync(call, PyBool.True);


    [Method("ReportBot")]
    public async ValueTask<PyValue> ReportBot(ServiceCall call) =>
        await OverrideUserCallAsync(call, PyBool.True);


    [Method("ReportISKSpammer")]
    public async ValueTask<PyValue> ReportISKSpammer(ServiceCall call) =>
        await OverrideUserCallAsync(call, PyBool.True);

    /// <summary>
    /// The character logs off in the host (<c>CharacterLogoff</c>). Without that callback no session can be logged off, so the
    /// answer is False, not True.
    /// </summary>
    [Method("UserLogOffCharacter")]
    public async ValueTask<PyValue> UserLogOffCharacter(ServiceCall call) =>
        await OverrideUserCallAsync(call, PyBool.False);

    [Method("GetSessionPlaytimeMinutes")]
    public async ValueTask<PyValue> GetSessionPlaytimeMinutes(ServiceCall call) =>
        await OverrideUserCallAsync(call, new PyInt(0));

    [Method("GetCreateDate")]
    public async ValueTask<PyValue> GetCreateDate(ServiceCall call)
    {
        if (await UserOverrideAsync(call).ConfigureAwait(false) is { } overridden) return overridden;
        var characterId = call.Session.CharacterId ?? 0;
        var fileTime = characterId > 0 && readCharacterCreateFileTime is not null
            ? await readCharacterCreateFileTime(characterId, call.Cancellation).ConfigureAwait(false)
            : null;
        return Py.FileTimeLong(fileTime ?? OldAccountCreateFileTime);
    }

    [Method("GetUserName")]
    public async ValueTask<PyValue> GetUserName(ServiceCall call)
    {
        if (await UserOverrideAsync(call).ConfigureAwait(false) is { } overridden) return overridden;
        var userId = Positive(call.Arg(0));
        if (userId <= 0) userId = call.Session.UserId ?? 0;
        var userName = userId > 0 && readUserName is not null
            ? await readUserName(userId, call.Cancellation).ConfigureAwait(false)
            : null;
        return new PyStr(string.IsNullOrEmpty(userName)
            ? userId > 0 ? $"user-{userId}" : "user-local"
            : userName);
    }

    [Method("GetUserToken")]
    public async ValueTask<PyValue> GetUserToken(ServiceCall call)
    {
        if (await UserOverrideAsync(call).ConfigureAwait(false) is { } overridden) return overridden;
        return new PyStr($"evejs-local-token-{call.Session.UserId ?? 0}-{call.Session.CharacterId ?? 0}");
    }

    private async ValueTask<PyValue> OverrideUserCallAsync(ServiceCall call, PyValue fallback) =>
        await UserOverrideAsync(call).ConfigureAwait(false) ?? fallback;

    private ValueTask<PyValue?> UserOverrideAsync(ServiceCall call) =>
        userRpcHandler is null
            ? ValueTask.FromResult<PyValue?>(null)
            : userRpcHandler(call, call.Cancellation);

    private static long Positive(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        _ => 0,
    };

    private static long PositiveInteger(PyValue value)
    {
        var number = JsSemantics.ToNumber(value);
        if (!double.IsFinite(number)) return 0;
        var integer = Math.Truncate(number);
        if (integer <= 0 || integer != number) return 0;
        return integer >= 9_223_372_036_854_775_808d ? long.MaxValue : (long)integer;
    }
}

internal static class UserSvcCompatibility
{
    private const string CRowsetName = "carbon.common.script.sys.crowset.CRowset";
    private static readonly (string Name, int Type)[] Columns =
    [
        ("tokenID", 0x03), ("massTokenID", 0x03), ("typeID", 0x03), ("quantity", 0x03),
        ("blueprintRuns", 0x03), ("blueprintMaterialLevel", 0x03), ("blueprintProductivityLevel", 0x03),
        ("label", 0x82), ("description", 0x82), ("dateTime", 0x40), ("expireDateTime", 0x40),
        ("availableDateTime", 0x40), ("stationID", 0x14), ("soulbound", 0x03),
    ];
    private static readonly PyObjectEx DescriptorWire = new(
        ObjectExKind.Ex1,
        Py.Tuple(
            new PyToken("blue.DBRowDescriptor"),
            Py.Tuple(Py.Tuple([.. Columns.Select(column =>
                (PyValue)Py.Tuple(new PyStr(column.Name), new PyInt(column.Type)))]))),
        [], []);
    private static readonly Descriptor Descriptor = Descriptor.FromHeader(DescriptorWire);
    private static readonly PyValue RowsetHeader = Py.Tuple(
        Py.Tuple(new PyToken(CRowsetName)),
        Py.Dict(("header", DescriptorWire)));

    public static PyValue RedeemTokenRowset(PyValue value)
    {
        if (value is not PyList rows) return value;
        var packedRows = rows.Items
            .Where(item => item is PyDict or PyObject { Args: PyDict } or PyObjectEx { Dict.Count: > 0 })
            .Select(item => (PyValue)new PyPackedRow(Descriptor, RedeemTokenValues(item)))
            .ToArray();
        return new PyObjectEx(ObjectExKind.Ex2, RowsetHeader, packedRows, []);
    }

    private static IReadOnlyList<PyValue> RedeemTokenValues(PyValue token)
    {
        var tokenId = Integer(Field(token, "tokenID"));
        var massTokenId = Integer(Field(token, "massTokenID"));
        var dateTime = Integer(Field(token, "dateTime"));
        var expireDateTime = Integer(Field(token, "expireDateTime"));
        var availableDateTime = Integer(Field(token, "availableDateTime"));
        return
        [
            Number(tokenId),
            massTokenId > 0 && massTokenId != tokenId ? Number(massTokenId) : PyNone.Instance,
            Number(Integer(Field(token, "typeID"))),
            Number(Integer(Field(token, "quantity"))),
            Number(Integer(Field(token, "blueprintRuns"))),
            Number(Integer(Field(token, "blueprintMaterialLevel"))),
            Number(Integer(Field(token, "blueprintProductivityLevel"))),
            new PyWString(Text(Field(token, "label"))),
            new PyWString(Text(Field(token, "description"))),
            Py.FileTimeLong(dateTime),
            expireDateTime > 0 ? Py.FileTimeLong(expireDateTime) : PyNone.Instance,
            Py.FileTimeLong(availableDateTime > 0 ? availableDateTime : dateTime),
            Integer(Field(token, "stationID")) > 0 ? Number(Integer(Field(token, "stationID"))) : PyNone.Instance,
            Boolean(Field(token, "soulbound")) ? new PyInt(1) : new PyInt(0),
        ];
    }

    private static PyValue? Field(PyValue source, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = source switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict dict } => dict.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => [],
        };
        return entries.FirstOrDefault(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;
    }

    private static long Integer(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value < 9_223_372_036_854_775_808d && number.Value >= long.MinValue => (long)number.Value,
        _ => 0,
    };

    private static PyValue Number(long value) => Py.Number(value);

    private static bool Boolean(PyValue? value) => value switch
    {
        PyBool boolean => boolean == PyBool.True,
        PyInt number => number.Value != 0,
        PyLong number => number.Value != 0,
        PyReal number => double.IsFinite(number.Value) && number.Value != 0,
        _ => false,
    };

    private static string Text(PyValue? value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyBytes text => Encoding.UTF8.GetString(text.Value.Span),
        _ => "",
    };
}
