using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills.Tests;

public sealed class SkillQueueContractTests
{
    [Fact]
    public async Task Save_abort_and_level_completion_keep_the_reference_notification_order()
    {
        await using var source = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Database=unused;Username=unused").Build();
        var catalog = Rig.Catalog;
        var skills = new PgSkillStore(source, "skill_contract");
        var training = new CharacterTraining(source, catalog, skills, new PgTrainingStore(source, "skill_contract"), new TrainingSettings(), () => Rig.ClockMs);
        var sessions = new CharacterSessions();
        var announcer = new TrainingAnnouncer(training, catalog, sessions, null, () => Rig.ClockMs, schedule: false);
        var (session, sink) = Rig.Session(140_000_038);
        sessions.Enter(140_000_038, session);
        var line = new ProjectedEntry(0, 3300, 1, 0, 250, null, null, 30);
        var queued = new TrainingView(new QueueState([new QueueEntry(3300, 1)], false, null), [line], [], 0);
        var paused = new TrainingUpdate(140_000_038, 34, [], [], null, false, queued);

        await announcer.AnnounceSavedAsync(session, new QueueSaved(paused, Paused: true));
        Assert.Equal(
            ["OnNewSkillQueueSaved", "OnSkillQueuePausedServer", "OnMultipleCharactersTrainingUpdated"],
            Notes.Of(sink).Select(note => note.Name));

        Notes.Of(sink);
        await announcer.AnnounceStoppedAsync(session, paused);
        Assert.Equal(
            ["OnSkillQueuePausedServer", "OnMultipleCharactersTrainingUpdated"],
            Notes.Of(sink).Select(note => note.Name));

        Notes.Of(sink);
        var finished = new TrainingUpdate(
            140_000_038, 34, [new CharacterSkill(3300, 0, 0)], [new CharacterSkill(3300, 1, 250)], null, false,
            new TrainingView(QueueState.Empty, [], [new CharacterSkill(3300, 1, 250)], 250));
        await announcer.OnSettledAsync(finished);
        Assert.Equal(
            ["OnServerSkillsChanged", "OnSkillLevelsTrained", "OnSkillsChanged", "OnNewSkillQueueSaved", "OnSkillQueuePausedServer"],
            Notes.Of(sink).Select(note => note.Name));
    }
}
