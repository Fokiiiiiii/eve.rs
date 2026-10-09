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
    private async Task ShipsAndDogma()
    {
        ships = new CharacterShips(source, dogma, characters, items, skills, modules, training);
        // the simulation starts empty with this host: a ship that a host which stopped without closing its sessions left in space is put back where its character last was
        stranded = await ships.RecoverAllFromSpaceAsync(
            id => stations.Get(id) is { } station ? (station.SolarSystemId, station.ConstellationId, station.RegionId) : null,
            shouldRecover: async (characterId, cancellationToken) =>
            {
                var character = await characters.GetAsync(characterId, cancellationToken);
                return character?.ShipId is not { } shipId ||
                       await spaceItemStore.GetAsync(shipId, cancellationToken) is null;
            });
        if (stranded > 0)
        {
            Console.Error.WriteLine($"eve-host: {stranded} ship(s) left in space by the last host were put back in their stations");
        }
        dogmaSync = new CharacterDogmaSync(dogma, ships, now);
            characterSessions = new CharacterSessions();
            signalTrackerRegistrationProvider = new Eve.Host.SignalTrackerRegistrationProvider(characterSessions);
    }
}
