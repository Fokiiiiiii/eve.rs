using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>What the client is told about a character's skills and their queue, in the shapes it reads.</summary>
public static class SkillNotifications
{
    private const string QueueKeyVal = "utillib.KeyVal";

    private static KeyValuePair<PyValue, PyValue> Field(string name, PyValue value) => new(new PyStr(name), value);

    private static PyValue Time(long? fileTime) => fileTime is { } value ? new PyLong(value) : PyNone.Instance;

    /// <summary>The queue as the client keeps it: a line each, with the points and, when it runs, the times.</summary>
    public static PyValue Queue(IEnumerable<ProjectedEntry> lines) => new PyList([.. lines.Select(line => (PyValue)new PyObject(
        new PyStr(QueueKeyVal),
        new PyDict(
        [
            Field("trainingStartSP", new PyInt(line.StartPoints)),
            Field("queuePosition", new PyInt(line.Position)),
            Field("trainingTypeID", new PyInt(line.TypeId)),
            Field("trainingDestinationSP", new PyInt(line.DestinationPoints)),
            Field("trainingEndTime", Time(line.End)),
            Field("trainingStartTime", Time(line.Start)),
            Field("trainingToLevel", new PyInt(line.ToLevel)),
        ])))]);

    /// <summary>The history the way <c>GetSkillHistory</c> lists it.</summary>
    public static PyValue History(IEnumerable<HistoryEntry> entries) => new PyList([.. entries.Select(entry => Py.KeyVal(
        ("logDate", new PyLong(entry.LogDate)),
        ("eventTypeID", new PyInt(entry.EventType)),
        ("skillTypeID", new PyInt(entry.TypeId)),
        ("absolutePoints", new PyInt(entry.Points)),
        ("level", new PyInt(entry.Level))))]);

    /// <summary>The queue was kept: the client shows it. (<c>charid</c>)</summary>
    public static void QueueSaved(ClientSession session, IEnumerable<ProjectedEntry> lines) =>
        session.SendNotification("OnNewSkillQueueSaved", "charid", Queue(lines));

    /// <summary>Nothing trains any more. (<c>clientID</c>)</summary>
    public static void QueuePaused(ClientSession session) => session.SendNotification("OnSkillQueuePausedServer", "clientID");

    public static void RespecInfoChanged(ClientSession session) =>
        session.SendNotification("OnRespecInfoChanged", "clientID");

    /// <summary>The account's training changed, for every client of the account to look at. (<c>userid</c>)</summary>
    public static void MultipleCharactersTrainingUpdated(ClientSession session) => session.SendNotification("OnMultipleCharactersTrainingUpdated", "userid");

    /// <summary>Skills of the character changed; <paramref name="reason"/> says why when it is not a level finishing. (<c>charid</c>)</summary>
    public static void ServerSkillsChanged(ClientSession session, PyValue skills, string? reason, long fileTime) =>
        session.SendNotification("OnServerSkillsChanged", "charid", skills, reason is null ? PyNone.Instance : new PyStr(reason), new PyLong(fileTime));

    /// <summary>Levels were trained: which skill reached which level. (<c>clientID</c>)</summary>
    public static void LevelsTrained(ClientSession session, IEnumerable<CharacterSkill> skills) =>
        session.SendNotification("OnSkillLevelsTrained", "clientID", new PyList([.. skills.Select(s => (PyValue)Py.Tuple(new PyInt(s.TypeId), new PyInt(s.Level)))]));

    /// <summary>The skills the client has changed. (<c>clientID</c>)</summary>
    public static void SkillsChanged(ClientSession session, PyValue skills) => session.SendNotification("OnSkillsChanged", "clientID", skills);
}
