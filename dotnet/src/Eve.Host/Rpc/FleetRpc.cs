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
    /// <summary>Fleets: objects, manager and proxy.</summary>
    private IReadOnlyList<IService> FleetRpc() =>
    [
            new Eve.Services.Space.FleetObjectService(
                fleetStore, now,
                loadFleetSetup: async (fleetId, characterId, setupName, _, cancellationToken) =>
                {
                    var characters = await ReadGameStoreTableAsync("characters", cancellationToken);
                    var setup = Eve.Host.PersistenceFleetSettings.FindSetup(characters, characterId, setupName)
                        ?? throw new InvalidOperationException("FleetError");
                    if (!await fleetStore.ApplyFleetSetupAsync(fleetId, characterId, setup, cancellationToken))
                        throw new InvalidOperationException("FleetError");
                    return PyNone.Instance;
                },
                createFleetAdvert: (fleetId, characterId, advertData, session, cancellationToken) =>
                    fleetStore.FleetFinderAdvertOperationAsync(
                        "AddFleetFinderAdvert", characterId, [advertData], session,
                        FleetAdvertLeaderProfileForAsync, cancellationToken),
                sessionForCharacter: FleetSessionForCharacter,
                compositionForMembers: FleetCompositionForMembersAsync,
                sameBubble: SameFleetBubble,
                memberSkillsForCharacter: FleetCommandSkillLevelsForCharacterAsync),
            new Eve.Services.Space.FleetManagerService(
                fleetStore,
                sessionOf: FleetSessionForCharacter,
                sameBubble: SameFleetBubble),
            new Eve.Services.Space.FleetProxyService(
                fleetStore,
                fleetAdvertOperation: (operation, characterId, args, session, cancellationToken) =>
                    fleetStore.FleetFinderAdvertOperationAsync(operation, characterId, args, session,
                        FleetAdvertLeaderProfileForAsync, cancellationToken),
                sessionForCharacter: FleetSessionForCharacter),
    ];
}
