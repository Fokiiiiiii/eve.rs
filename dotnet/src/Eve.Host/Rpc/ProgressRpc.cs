using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    /// <summary>Achievements, milestones, operations, missions, abyssal, void space and the new-player experience.</summary>
    /// <summary>The achievement tracker record as the client's two maps (EveJS <c>legacyTracker</c>): completions by achievement id, counts by event name.</summary>
    private static AchievementTrackerPersistedState ToPersisted(AchievementTrackerRecord record) => new(
        new PyDict([.. record.EventCounts.Select(e => new KeyValuePair<PyValue, PyValue>(new PyStr(e.Name), new PyLong(e.Count)))]),
        new PyDict([.. record.Completed.Select(c => new KeyValuePair<PyValue, PyValue>(new PyInt(c.AchievementId), new PyLong(c.CompletedFileTime)))]));

    /// <summary>What the client sent, as stored: a positive achievement id with its filetime, and a trimmed event name with a count of zero or more.</summary>
    private static (List<(int AchievementId, long CompletedFileTime)> Completed, List<(string Name, long Count)> EventCounts) FromClient(PyDict completed, PyDict eventCounts)
    {
        var done = new Dictionary<int, long>();
        foreach (var entry in completed.Entries)
        {
            var id = entry.Key switch { PyInt i => i.Value, PyLong l => l.Value, _ => 0L };
            var filetime = entry.Value switch { PyLong l => l.Value, PyInt i => i.Value, _ => -1L };
            if (id is > 0 and <= int.MaxValue && filetime >= 0)
                done[(int)id] = filetime;
        }
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in eventCounts.Entries)
        {
            var count = entry.Value switch { PyLong l => l.Value, PyInt i => i.Value, _ => -1L };
            if (Eve.Macho.JsSemantics.StringValue(entry.Key) is { } name && name.Trim() is { Length: > 0 } key && count >= 0)
                counts[key] = count;
        }
        return ([.. done.Select(pair => (pair.Key, pair.Value))], [.. counts.Select(pair => (pair.Key, pair.Value))]);
    }

    private IReadOnlyList<IService> ProgressRpc() =>
    [
            new AchievementTrackerMgrService(
                readState: async (characterId, ct) =>
                    await AchievementTracker.ReadAsync(characterId, ct) is { } record
                        ? ToPersisted(record)
                        : LoginInitializationGameStoreProjection.ReadAchievementState(
                            await ReadCharacterGameStoreRowAsync(characterId, ct)),
                hasEverWarped: async (characterId, ct) =>
                    LoginInitializationGameStoreProjection.ReadHasEverWarped(
                        await ReadCharacterGameStoreRowAsync(characterId, ct)) ?? true,
                writeState: (characterId, completed, eventCounts, ct) =>
                {
                    var (done, counts) = FromClient(completed, eventCounts);
                    return new ValueTask(AchievementTracker.WriteAsync(characterId, done, counts, now(), ct));
                }),
            MilestoneManager,
            new OperationsManagerService(
                readCharacterProgress: async (call, ct) =>
                    LoginInitializationGameStoreProjection.ReadOperationsProgress(
                        await ReadCharacterGameStoreRowAsync(LoginInitializationGameStoreProjection.CharacterId(call), ct)),
                newCharacterExperienceMode: () => newCharacterExperienceMode,
                readTutorialState: ReadAirNpeCharacterStateAsync),
            new MissionTrackerMgrService(
                readMissionInfoItems: agentMissionService.ReadMissionInfoItemsAsync,
                readAllMissionObjectives: async (characterId, agentId, session, ct) =>
                {
                    var context = await ReadMissionTrackerContextAsync(characterId, agentId, session, ct);
                    return MissionTrackerGameStoreProjection.ReadAllObjectives(
                        context.MissionRecord, agentId, context.InActiveDungeon == true, context.DungeonState);
                },
                isInActiveDungeon: async (call, ct) =>
                {
                    var characterId = call.Session.GetInt("charid") ?? call.Session.GetInt("characterID") ?? 0;
                    var agentId = LoginInitializationGameStoreProjection.PositiveId(call.Arg(0));
                    if (characterId <= 0 || agentId <= 0) return false;
                    return (await ReadMissionTrackerContextAsync(characterId, agentId, call.Session, ct)).InActiveDungeon;
                }),
            new AbyssalMgrService(HandleAbyssalRpcAsync),
            new VoidSpaceMgrService(),
            new AirNpeService(
                () => newCharacterExperienceMode,
                ReadAirNpeCharacterStateAsync,
                (characterId, tutorialEnabled, ct) => characters.MarkTutorialSkippedAsync(characterId, tutorialEnabled, ct)),
            new Eve.Services.Core.NesIntroService(nesIntro.ReadAsync, nesIntro.SkipAsync, ReadAirNpeCharacterStateAsync,
                () => newCharacterExperienceMode, characterSessions),
    ];
}
