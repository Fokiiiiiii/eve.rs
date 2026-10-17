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
    private Task IncursionsDungeonsKeeper()
    {
        incursionAnomalyReadModel = new PersistenceIncursionAnomalyReadModel(
            async (kind, owner, token) =>
            {
                // the anomalies of a system the system scanner asks about exist by then (the sites are made when a system is first looked at)
                if (kind == "anomaly" && cosmicSites is { } director && owner is > 0 and <= int.MaxValue)
                    await director.EnsureAsync((int)owner, token);
                return await foundations.Content.ActiveOfAsync(kind, owner, token);
            },
            ReadGameStoreTableAsync,
            now,
            activeComplexes: (system, token) => ComplexAnomaliesAsync(system, token),
            activeIncursions: async token => incursionDirector is { } directorOfIncursions
                ? await directorOfIncursions.ReadActiveAsync(token)
                : Array.Empty<JsonObject>());
        dungeonReadModel = new Eve.Host.PersistenceDungeonReadModel(
            (table, token) => sde.ReadAsync(table, cancellationToken: token),
            foundations.Content.ActiveByKindAsync,
            ReadGameStoreTableAsync,
            now);
        keeperRpcCallbacks = new Eve.Services.Core.KeeperRpcCallbacks(
            ReadCurrentDungeonAsync: CurrentDungeonPayloadAsync,
            ActivateAccelerationGateAsync: async (session, gate, cancellationToken) =>
            {
                if (accelerationGates is null)
                {
                    throw Eve.Services.MachoErrors.ThrowUserError("DeniedShipChanged");
                }
                var gateId = gate switch
                {
                    PyInt asInt => asInt.Value,
                    PyLong asLong => asLong.Value,
                    _ => throw Eve.Services.MachoErrors.ThrowUserError("DeniedTargetAttemptFailed"),
                };
                return await accelerationGates.ActivateAsync(session, gateId, cancellationToken);
            });
        return Task.CompletedTask;
    }

    /// <summary>The acceleration gates of the dungeon rooms: set once the space and the dungeons are composed (see the space composition).</summary>
    private Eve.Services.Space.Gates.SpaceAccelerationGates? accelerationGates;

    /// <summary>The client's current dungeon for the session (the keeper's answer), or null when it is in none.</summary>
    private async ValueTask<PyValue?> CurrentDungeonPayloadAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (spaceSessions is null || session.CharacterId is not { } characterId)
            return null;
        var shipId = spaceSessions.ShipIdOf(session);
        if (shipId is not > 0)
            return null;
        var position = await spaceSessions.PositionOfAsync(session, shipId.Value, cancellationToken).ConfigureAwait(false);
        var currentSystemId = position?.SolarSystemId ??
            session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
        return await dungeonReadModel.ReadCurrentDungeonAsync(
            characterId, shipId.Value, currentSystemId,
            position?.X, position?.Y, position?.Z,
            spaceSessions.IsWarping(session), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The instance of the dungeon the session is in, or null (the instance id is the third item of the keeper's answer).</summary>
    private async Task<long?> CurrentDungeonInstanceAsync(ClientSession session, CancellationToken cancellationToken) =>
        await CurrentDungeonPayloadAsync(session, cancellationToken).ConfigureAwait(false) is PyTuple { Items.Count: >= 3 } payload
            ? payload.Items[2] switch
            {
                PyInt asInt => asInt.Value,
                PyLong asLong => asLong.Value,
                _ => null,
            }
            : null;
}
