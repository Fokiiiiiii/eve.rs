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
    private Task CrimewatchRuntime()
    {
        crimewatchPersistence = new PersistenceCrimewatchCallbacks(
            foundations.Relations, mailStore, mailCache, characterSessions, AffiliationOfAsync, now);
        crimewatchRuntime = new Eve.Services.Crimewatch.CrimewatchRuntime(
            crimewatchStore, characterSessions, now, crimewatchPersistence,
            message => Console.Error.WriteLine($"eve-host: crimewatch: {message}"),
            localTimers: foundations.LocalTimers);
        relationWars = new Eve.Services.Crimewatch.RelationWars(foundations.Relations);
        crimewatchRuntime.Legal = relationWars.AreAtWarAsync;
        crimewatchRuntime.WarAssistanceFactsOf = relationWars.AssistanceFactsAsync;
        crimewatchRuntime.SecurityOfSystem = (systemId, _) => Task.FromResult(
            systemId is > 0 and <= int.MaxValue ? solarSystems.Get((int)systemId)?.Security : null);
        crimewatchRuntime.CreateKillRight = (request, token) => crimewatchStore.CreateKillRightAsync(
            request.OwnerCharacterId, request.TargetCharacterId, request.SolarSystemId, request.CreatedAtUnixMs, token);
        return Task.CompletedTask;
    }
}
