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
    /// <summary>Rewards, login campaigns, store offers, redeem and vault.</summary>
    private IReadOnlyList<IService> RewardRpc() =>
    [
            new RewardsService(),
            new Eve.Services.Core.LoginCampaignMgrService(storeRuntime),
            new Eve.Services.Core.SeasonalLoginCampaignMgrService(storeRuntime),
            new StoreOffersService(
                _ => ValueTask.FromResult<JsonObject?>(Eve.Services.Core.NewEdenStoreCatalog.Default.Root),
                storeRuntime.PurchaseAsync),
            new Eve.Services.Core.FastCheckoutService(Eve.Services.Core.NewEdenStoreCatalog.Default, storeRuntime),
            new Eve.Services.Core.RedeemService(
                storeRuntime,
                async (userId, ct) => (await accounts.FindAsync(checked((uint)userId), ct))?.UserName,
                async (characterId, ct) =>
                {
                    var character = await characters.GetAsync(characterId, ct);
                    return character?.CreatedAt.UtcDateTime.ToFileTimeUtc();
                },
                async (call, ct) =>
                {
                    if (call.Method == "UserLogOffCharacter")
                        return PyValue.Of(await new CharacterLogoff(characters, online, characterEntryHooks, now)
                            .LogOffAsync(call.Session, ct).ConfigureAwait(false));
                    if (call.Method == "CreateRedeemTokenQA")
                        return await storeRuntime.CreateRedeemTokenQAAsync(call, ct).ConfigureAwait(false);
                    return null;
                },
                storeRuntime),
            new Eve.Services.Core.VaultManagerService(storeRuntime),
    ];
}
