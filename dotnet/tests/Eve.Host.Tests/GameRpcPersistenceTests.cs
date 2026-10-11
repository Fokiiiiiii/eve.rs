using Eve.Data;
using Eve.GameStore;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

public sealed class GameRpcPersistenceTests
{
    [Fact]
    public async Task Shared_settings_interference_and_killmail_use_existing_game_store_tables()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(pgUrl!));
        var schema = "rpc_game_" + Guid.NewGuid().ToString("N");
        var store = new PersistenceStore(source, schema);
        try
        {
            await store.EnsureSchemaAsync();
            await using var owner = await OwnerSession.StartAsync(store, PersistenceRoles.World, "rpc-game-" + Guid.NewGuid().ToString("N"));
            await using var cache = new GameStoreCache(store, owner, new GameStoreCacheOptions
            {
                ProcessRole = PersistenceRoles.World,
                FlushDelay = TimeSpan.FromHours(1),
            });
            await cache.RecoverAsync();
            foreach (var table in new[] { "sharedSettings", "solarSystemInterferenceState", "killmails" })
                await cache.LoadAsync(table);

            const long now = 1_700_000_000_000;
            var nowFiletime = Py.FileTimeFromUnixMs(now);
            cache.Write("solarSystemInterferenceState", "/systems/30000142", new JsonObject
            {
                ["interferenceLevel"] = 8000,
                ["lastDecayTickTimestamp"] = nowFiletime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            cache.Write("killmails", "/records/88", new JsonObject
            {
                ["killID"] = 88,
                ["killTime"] = "133000000000000000",
                ["victimCharacterID"] = 11,
                ["victimShipTypeID"] = 600,
                ["finalCharacterID"] = 22,
                ["killBlob"] = "persisted",
            });
            await cache.FlushAllAsync();

            var callbacks = new GameRpcCallbacks(
                ReadSharedSettings: () => cache.Read("sharedSettings", "/").Data as JsonObject,
                WriteSharedSettingsAsync: async (state, cancellationToken) =>
                {
                    var result = cache.Write("sharedSettings", "/", state);
                    if (!result.Success)
                        return false;
                    await cache.FlushTableAsync("sharedSettings", cancellationToken);
                    return true;
                },
                ReadInterferenceTableAsync: _ => ValueTask.FromResult<JsonObject?>(cache.Read("solarSystemInterferenceState", "/").Data as JsonObject),
                ReadKillmailAsync: (killId, _) => ValueTask.FromResult<JsonObject?>(
                    cache.Read("killmails", $"/records/{killId}").Data as JsonObject));
            var services = CoreServices.Create(new PolicyOmegaAccounts(), () => now,
                gameRpcCallbacks: callbacks);

            var sharedSettings = services.Single(service => service.Name == "sharedSettingsMgr");
            var key = Assert.IsType<PyObject>(await sharedSettings.CallAsync(Call("StoreSettingLinkAndGetID", new PyInt(2), new PyStr("preset"))));
            Assert.True(cache.Read("sharedSettings", "/entries").Success);
            var hash = Field(key, "hashvalue");
            var sqId = Field(key, "sqID");
            var settingTypeId = Field(key, "settingTypeID");
            Assert.Equal(new PyStr("preset"), await sharedSettings.CallAsync(Call("GetStoredSharedSetting",
                new PyTuple([hash, sqId, settingTypeId]))));

            var bands = Assert.IsType<PyDict>(await services.Single(service => service.Name == "solarsystemInterferenceMgr")
                .CallAsync(Call("GetAllInterferenceBands")));
            Assert.Contains(bands.Entries, row => row.Key == new PyInt(30000142) && row.Value == new PyStr("H"));

            var killmail = Assert.IsType<PyObject>(await services.Single(service => service.Name == "warStatisticMgr")
                .CallAsync(Call("GetKillMail", new PyInt(88))));
            Assert.Equal(new PyInt(88), Field(killmail, "killID"));
            Assert.Equal(new PyStr("persisted"), Field(killmail, "killBlob"));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static ServiceCall Call(string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = new ClientSession(),
    };

    private static PyValue Field(PyObject value, string name)
    {
        var dict = Assert.IsType<PyDict>(value.Args);
        return dict.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
    }

    private sealed class PolicyOmegaAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
