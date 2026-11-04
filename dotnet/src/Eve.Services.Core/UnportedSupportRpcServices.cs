using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Core;

public delegate ValueTask SubmitInGameReportCallback(
    string methodName,
    ServiceCall call,
    CancellationToken cancellationToken);

[Service("ingamereport")]
public sealed partial class InGameReportService(SubmitInGameReportCallback? submitReport = null)
{
    [Method("report_inappropriate_language")]
    public ValueTask<PyValue> ReportInappropriateLanguage(ServiceCall call) =>
        SubmitAsync("report_inappropriate_language", call);

    [Method("report_offensive_mail")]
    public ValueTask<PyValue> ReportOffensiveMail(ServiceCall call) =>
        SubmitAsync("report_offensive_mail", call);

    [Method("report_macro_use")]
    public ValueTask<PyValue> ReportMacroUse(ServiceCall call) =>
        SubmitAsync("report_macro_use", call);

    [Method("report_questionable_transaction")]
    public ValueTask<PyValue> ReportQuestionableTransaction(ServiceCall call) =>
        SubmitAsync("report_questionable_transaction", call);

    [Method("report_inappropriate_character_name")]
    public ValueTask<PyValue> ReportInappropriateCharacterName(ServiceCall call) =>
        SubmitAsync("report_inappropriate_character_name", call);

    [Method("report_exploit_abuse")]
    public ValueTask<PyValue> ReportExploitAbuse(ServiceCall call) =>
        SubmitAsync("report_exploit_abuse", call);

    [Method("report_gambling")]
    public ValueTask<PyValue> ReportGambling(ServiceCall call) =>
        SubmitAsync("report_gambling", call);

    [Method("report_other")]
    public ValueTask<PyValue> ReportOther(ServiceCall call) =>
        SubmitAsync("report_other", call);

    private async ValueTask<PyValue> SubmitAsync(string methodName, ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (submitReport is { } submit)
            await submit(methodName, call, call.Cancellation).ConfigureAwait(false);
        return PyNone.Instance;
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
