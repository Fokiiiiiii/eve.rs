using Eve.Macho;
using Eve.Marshal;
using System.Text;

namespace Eve.Services.Core;

public sealed record InGameReportSubmission(
    string MethodName,
    string ReportType,
    long ReporterCharacterId,
    long? ReporterAccountId,
    long TargetCharacterId,
    string Comments,
    string? ChatTime = null,
    string? ChatChannel = null,
    string? ChatContent = null);

public delegate ValueTask SubmitInGameReportCallback(
    InGameReportSubmission report,
    CancellationToken cancellationToken);

[Service("ingamereport")]
public sealed partial class InGameReportService(SubmitInGameReportCallback? submitReport = null)
{
    [Method("report_inappropriate_language")]
    public ValueTask<PyValue> ReportInappropriateLanguage(ServiceCall call) =>
        SubmitAsync("report_inappropriate_language", "inappropriate_language", call, hasChatDetails: true);

    [Method("report_offensive_mail")]
    public ValueTask<PyValue> ReportOffensiveMail(ServiceCall call) =>
        SubmitAsync("report_offensive_mail", "offensive_mail", call);

    [Method("report_macro_use")]
    public ValueTask<PyValue> ReportMacroUse(ServiceCall call) =>
        SubmitAsync("report_macro_use", "macro_use", call);

    [Method("report_questionable_transaction")]
    public ValueTask<PyValue> ReportQuestionableTransaction(ServiceCall call) =>
        SubmitAsync("report_questionable_transaction", "questionable_transaction", call);

    [Method("report_inappropriate_character_name")]
    public ValueTask<PyValue> ReportInappropriateCharacterName(ServiceCall call) =>
        SubmitAsync("report_inappropriate_character_name", "inappropriate_character_name", call);

    [Method("report_exploit_abuse")]
    public ValueTask<PyValue> ReportExploitAbuse(ServiceCall call) =>
        SubmitAsync("report_exploit_abuse", "exploit_abuse", call);

    [Method("report_gambling")]
    public ValueTask<PyValue> ReportGambling(ServiceCall call) =>
        SubmitAsync("report_gambling", "gambling", call);

    [Method("report_other")]
    public ValueTask<PyValue> ReportOther(ServiceCall call) =>
        SubmitAsync("report_other", "other", call);

    private async ValueTask<PyValue> SubmitAsync(string methodName, string reportType, ServiceCall call, bool hasChatDetails = false)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var expectedArgumentCount = hasChatDetails ? 5 : 2;
        if (call.Args.Count != expectedArgumentCount || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("INGAME_REPORT_ARGUMENTS_INVALID");

        if (!PositiveInteger(call.Arg(0), out var targetCharacterId))
            throw MachoErrors.ThrowUserError("INGAME_REPORT_TARGET_INVALID");
        if (!TryText(call.Arg(1), out var comments))
            throw MachoErrors.ThrowUserError("INGAME_REPORT_TEXT_INVALID");

        var reporterCharacterId = call.Session.CharacterId;
        if (reporterCharacterId is not > 0)
            throw MachoErrors.ThrowUserError("INGAME_REPORT_REPORTER_REQUIRED");

        string? chatTime = null;
        string? chatChannel = null;
        string? chatContent = null;
        if (hasChatDetails && (!TryText(call.Arg(2), out chatTime) ||
            !TryText(call.Arg(3), out chatChannel) || !TryText(call.Arg(4), out chatContent)))
            throw MachoErrors.ThrowUserError("INGAME_REPORT_TEXT_INVALID");

        if (submitReport is not { } submit)
            throw MachoErrors.ThrowUserError("INGAME_REPORT_STORAGE_UNAVAILABLE");

        var reporterAccountId = call.Session.UserId is > 0 ? call.Session.UserId : null;
        await submit(new InGameReportSubmission(methodName, reportType, reporterCharacterId.Value,
            reporterAccountId, targetCharacterId, comments, chatTime, chatChannel, chatContent), call.Cancellation)
            .ConfigureAwait(false);
        return PyNone.Instance;
    }

    private static bool PositiveInteger(PyValue value, out long result)
    {
        result = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => 0,
        };
        return result > 0;
    }

    private static bool TryText(PyValue value, out string text)
    {
        switch (value)
        {
            case PyStr str:
                text = str.Value;
                return true;
            case PyWString wide:
                text = wide.Value;
                return true;
            case PyBytes bytes:
                return TryDecode(bytes.Value.Span, out text);
            case PyBuffer buffer:
                return TryDecode(buffer.Value.Span, out text);
            default:
                text = "";
                return false;
        }
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }
}

public delegate ValueTask<int> ReadNonDiminishingInjectionCountCallback(
    long characterId,
    CancellationToken cancellationToken);

[Service("nonDiminishingInjectionMgr")]
public sealed partial class NonDiminishingInjectionMgrService(
    ReadNonDiminishingInjectionCountCallback? readRemaining = null)
{
    [Method("GetAvailableNonDiminishingInjections")]
    public async ValueTask<PyValue> GetAvailableNonDiminishingInjections(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        var remaining = characterId > 0 && readRemaining is { } read
            ? await read(characterId, call.Cancellation).ConfigureAwait(false)
            : 0;
        return new PyInt(Math.Max(0, remaining));
    }
}
