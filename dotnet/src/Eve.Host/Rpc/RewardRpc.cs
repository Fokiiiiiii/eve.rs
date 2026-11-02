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
    /// <summary>
    /// A re-sculpt certificate (SDE 34132, with gender, or 89956, without) of one of the account's characters is consumed in the transaction that sets the
    /// character's recustomization state: 3 (full) for the certificate with gender, 1 (resculpting) for the other (the two SDE descriptions).
    /// </summary>
    private async ValueTask<bool> ActivateReSculptAsync(long userId, ServiceCall call, long itemId, CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId ?? 0;
        if (characterId <= 0 || await items.GetAsync(itemId, cancellationToken) is not { } item ||
            item.OwnerId != characterId || item.TypeId is not (34132 or 89956))
            return false;
        if (await characters.GetAsync(characterId, cancellationToken) is not { } character || character.AccountId != userId)
            return false;
        var dockId = call.Session.GetInt("stationid") ?? call.Session.GetInt("stationID") ??
            call.Session.GetInt("structureid") ?? call.Session.GetInt("structureID");
        var characterDockId = character.StructureId ?? character.StationId;
        var shipId = call.Session.GetInt("shipid") ?? call.Session.GetInt("shipID");
        var inHangar = item.Flag == ItemFlags.Hangar && dockId is > 0 && dockId == characterDockId && item.LocationId == dockId;
        var activeShip = item.Flag == ItemFlags.Cargo && shipId is > 0 && character.ShipId == shipId && item.LocationId == shipId
            ? await items.GetAsync(shipId.Value, cancellationToken)
            : null;
        var inCargo = activeShip?.OwnerId == characterId;
        if (!inHangar && !inCargo)
            return false;
        var state = item.TypeId == 34132 ? (short)3 : (short)1;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var lockedItem = await items.LockAndGetAsync(connection, transaction, item.Id, cancellationToken);
        if (lockedItem is null || lockedItem.OwnerId != characterId || lockedItem.TypeId != item.TypeId ||
            lockedItem.LocationId != item.LocationId || lockedItem.Flag != item.Flag || lockedItem.Quantity <= 0 ||
            await characters.LockResolvedPaperdollStateAsync(connection, transaction, characterId, cancellationToken) != 0)
            return false;
        if (lockedItem.Quantity == 1)
            await items.DeleteAsync(connection, transaction, lockedItem.Id, cancellationToken);
        else
            await items.SetQuantityAsync(connection, transaction, lockedItem.Id, lockedItem.Quantity - 1, cancellationToken);
        await characters.SetPaperdollStateAsync(connection, transaction, character.Id, state, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (characterSessions.Of(characterId) is { } session)
        {
            var type = await itemTypes.GetAsync(lockedItem.TypeId, cancellationToken);
            var row = new ItemRow(lockedItem.Id, lockedItem.TypeId, lockedItem.OwnerId, lockedItem.LocationId, lockedItem.Flag,
                lockedItem.Quantity - 1, type?.GroupId ?? 0, type?.CategoryId ?? 0);
            var changes = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(9), InventoryWire.Id(lockedItem.Quantity))]);
            session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), changes, PyNone.Instance);
            session.SendNotification("OnCharacterHandler", "charid");
        }
        return true;
    }

    private IReadOnlyList<IService> RewardRpc() =>
    [
            new RewardsService(async (groupIds, token) => incursionDirector is { } directorOfIncursions
                ? await directorOfIncursions.PendingLoyaltyPointsAsync(groupIds, token)
                : new Dictionary<long, long>()),
            new Eve.Services.Core.LoginCampaignMgrService(storeRuntime),
            new Eve.Services.Core.SeasonalLoginCampaignMgrService(storeRuntime),
            new StoreOffersService(
                _ => ValueTask.FromResult<JsonObject?>(Eve.Services.Core.NewEdenStoreCatalog.Default.Root)),
            new Eve.Services.Core.FastCheckoutService(Eve.Services.Core.NewEdenStoreCatalog.Default),
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
                    if (call.Method == "GetUserToken")
                    {
                        if (call.Session.UserId is not { } userId || userId <= 0)
                            throw new UnauthorizedAccessException("An authenticated account is required for a user token.");
                        return new Eve.Marshal.PyStr(Eve.Host.PublicGateway.GatewayTokens.Issue(userId,
                            call.Session.CharacterId is > 0 ? call.Session.CharacterId : null, DateTimeOffset.UtcNow));
                    }
                    if (call.Method == "CreateRedeemTokenQA")
                        return await storeRuntime.CreateRedeemTokenQAAsync(call, ct).ConfigureAwait(false);
                    return null;
                },
                storeRuntime) { ActivateReSculpt = ActivateReSculptAsync },
            new Eve.Services.Core.VaultManagerService(storeRuntime),
    ];
}
