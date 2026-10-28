using Eve.Services;
using Eve.Services.Core;
using Eve.Marshal;

namespace Eve.Host;

internal static class IndustryJobClientNotifications
{
    internal const long ViewCorporationJobsRoles = 1024L | 1125899906842624L | 2251799813685248L;

    internal static void Send(CharacterSessions characterSessions, IndustryJobChangedEvent change)
    {
        var recipients = new List<ClientSession>();
        foreach (var characterId in new[] { change.OwnerId, change.InstallerId }.Distinct())
            if (characterSessions.Of(characterId) is { } owned)
                recipients.Add(owned);
        foreach (var member in characterSessions.OfCorporation(change.OwnerId))
            if (((member.GetInt("corprole") ?? 0) & ViewCorporationJobsRoles) != 0)
                recipients.Add(member);

        foreach (var session in recipients.Distinct())
            session.SendNotification(
                "OnIndustryJob", "charid",
                new PyInt(change.JobId), new PyInt(change.OwnerId), new PyInt(change.BlueprintId),
                new PyInt(change.InstallerId), new PyInt(change.Status),
                change.SuccessfulRuns is { } runs ? new PyInt(runs) : PyNone.Instance);
    }
}
