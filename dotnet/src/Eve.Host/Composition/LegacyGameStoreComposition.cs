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

/// <summary>The legacy GameStore the host still builds on (ADR 0003 drift). Disposed in the original order: cache, owner session, store.</summary>
internal sealed class LegacyGameStore(PersistenceStore store, OwnerSession owner, GameStoreCache cache) : IAsyncDisposable
{
    internal PersistenceStore Store { get; } = store;

    internal OwnerSession Owner { get; } = owner;

    internal GameStoreCache Cache { get; } = cache;

    public async ValueTask DisposeAsync()
    {
        await Cache.DisposeAsync();
        await Owner.DisposeAsync();
        await Store.DisposeAsync();
    }
}

/// <summary>Opens the legacy GameStore and loads the tables the host reads, in the original order.</summary>
internal static class LegacyGameStoreComposition
{
    internal static async Task<LegacyGameStore> OpenAsync(NpgsqlDataSource source)
    {
        var mailStore = new PersistenceStore(source, Env("EVE_GAMESTORE_SCHEMA", "gamestore"));
        await mailStore.EnsureSchemaAsync();
        var mailStoreOwner = await OwnerSession.StartAsync(mailStore, PersistenceRoles.World, $"eve-host-{Environment.ProcessId}");
        var mailCache = new GameStoreCache(mailStore, mailStoreOwner, new GameStoreCacheOptions
        {
            ProcessRole = PersistenceRoles.World,
            OnFlushError = (table, error) => Console.Error.WriteLine($"eve-host: gameStore {table} flush failed: {error.Message}"),
        });
        await mailCache.RecoverAsync();
        await mailCache.LoadAsync("mail");
        await mailCache.LoadAsync("notifications");
        await mailCache.LoadAsync("crimewatchRuntime");
        await mailCache.LoadAsync("characters");
        await mailCache.LoadAsync("sovereignty");
        await mailCache.LoadAsync("characterEnergyState");
        await mailCache.LoadAsync("shipCosmetics");
        await mailCache.LoadAsync("shipLogoFittings");
        await mailCache.LoadAsync("planetRuntimeState");
        await mailCache.LoadAsync("industryRuntime");
        await mailCache.LoadAsync("moonExtractions");
        await mailCache.LoadAsync("overviewSharedPresets");
          await mailCache.LoadAsync("sharedSettings");
        return new LegacyGameStore(mailStore, mailStoreOwner, mailCache);
    }
}
