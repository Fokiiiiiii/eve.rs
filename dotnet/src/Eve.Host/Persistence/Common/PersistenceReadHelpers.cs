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

public static class PersistenceReadHelpers
{
    public static async Task<JsonObject> ReadOptionalTableAsync(
        Func<string, CancellationToken, Task<JsonObject>> readTableAsync,
        string table,
        CancellationToken cancellationToken)
    {
        try
        {
            return await readTableAsync(table, cancellationToken);
        }
        catch (Eve.GameStore.PersistenceException error) when (error.Code == Eve.GameStore.PersistenceCodes.TableNotFound)
        {
            return new JsonObject();
        }
    }

    public static Task<JsonObject> ReadOptionalTableAsync(
        PersistenceStore store,
        string table,
        CancellationToken cancellationToken) =>
        ReadOptionalTableAsync(
            (name, token) => store.LoadTableAsync(name, createIfMissing: false, token),
            table,
            cancellationToken);
}
