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
    private Task CharMgrReadModel()
    {
        charMgrReadModel = new PersistenceCharMgrReadModel(
            (table, cancellationToken) =>
            {
                var cached = mailCache.Read(table);
                return cached.Success && cached.Data is System.Text.Json.Nodes.JsonObject row
                    ? Task.FromResult(row)
                    : ReadGameStoreTableAsync(table, cancellationToken);
            },
            ReadRecentShipKillmailPageAsync,
            ReadCharacterDescriptionAsync,
            WriteCharacterDescriptionAsync,
            ReadMedal1GraphicIdAsync,
            (table, path, data) => mailCache.Write(table, path, data),
            (table, path) => mailCache.Remove(table, path));
        return Task.CompletedTask;
    }
}
