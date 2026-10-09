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
    private IReadOnlyList<IService> ProgressRpc() =>
    [
            new AchievementTrackerMgrService(
                readState: async (characterId, ct) =>
                    LoginInitializationGameStoreProjection.ReadAchievementState(
                        await ReadCharacterGameStoreRowAsync(characterId, ct)),
                hasEverWarped: async (characterId, ct) =>
                    LoginInitializationGameStoreProjection.ReadHasEverWarped(
                        await ReadCharacterGameStoreRowAsync(characterId, ct)) ?? true),
            new MilestoneMgrService(),
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
            new AbyssalMgrService(),
            new VoidSpaceMgrService(),
            new AirNpeService(
                () => newCharacterExperienceMode,
                ReadAirNpeCharacterStateAsync,
                (characterId, tutorialEnabled, ct) => characters.MarkTutorialSkippedAsync(characterId, tutorialEnabled, ct)),
    ];
}
