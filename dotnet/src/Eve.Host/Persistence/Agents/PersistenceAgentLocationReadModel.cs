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

public sealed class PersistenceAgentLocationReadModel(
    Func<string, CancellationToken, Task<IReadOnlyList<SdeRow>>> readTableAsync)
{
    public async Task<IReadOnlyDictionary<long, long>> ReadAsync(CancellationToken cancellationToken)
    {
        var locations = new Dictionary<long, long>();
        foreach (var row in await readTableAsync("npcCharacters", cancellationToken))
        {
            if (row.Key > 0 &&
                row.Doc.TryGetProperty("locationID", out var location) &&
                location.TryGetInt64(out var locationId) &&
                locationId > 0)
            {
                locations[row.Key] = locationId;
            }
        }

        return locations;
    }
}
