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
    /// <summary>Encoded items and in-space compression.</summary>
    private IReadOnlyList<IService> ItemRpc() =>
    [
            new Eve.Host.EncodedItemsRpcService(
                async (itemId, cancellationToken) => await items.GetAsync(itemId, cancellationToken),
                async (_, item, cancellationToken) =>
                {
                    var itemType = await itemTypes.GetAsync(item.TypeId, cancellationToken);
                    if (itemType is null)
                        return false;
                    var lists = await sde.ReadAsync("typeLists", [492L], cancellationToken);
                    return lists.FirstOrDefault() is { } list &&
                           Eve.Host.EncodedItemsRpcService.IsInTypeList(list.Doc, itemType);
                }),
            new Eve.Host.InSpaceCompressionMgrRpcService(
                async (itemId, cancellationToken) => await items.GetAsync(itemId, cancellationToken),
                // the space host decides (running compressor, fleet, range, type list) and does the inventory change; read at call time, space is composed after this
                async (call, item, facilityBallId, cancellationToken) =>
                {
                    if (spaceContainers is null)
                        throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new Eve.Marshal.PyStr(Eve.Host.InSpaceCompressionMgrRpcService.UnavailableNotice)));
                    var made = await spaceContainers.CompressInSpaceAsync(call.Session, item, facilityBallId, cancellationToken);
                    return new Eve.Host.SpaceCompressionResult(
                        made.SourceItemId, made.SourceTypeId, made.SourceQuantity, made.OutputItemId, made.OutputTypeId, made.OutputQuantity);
                }),
            .. Eve.Services.Core.SovFwService.Create(
                sovereigntyReadModel.ReadSnapshotAsync,
                sovereigntyReadModel.IsOnLocalFuelAccessGroupAsync,
                new Eve.Services.Core.SovHubWriteOperations(
                    sovWriteOperations.CanManageHubAsync,
                    sovWriteOperations.SetFuelAccessGroupAsync,
                    sovWriteOperations.SaveUpgradeConfigurationAsync,
                    sovWriteOperations.SelfDestructUpgradeAsync,
                    sovWriteOperations.SaveWorkforceConfigurationAsync),
                new Eve.Services.Core.SovSkyhookWriteOperations(
                    sovWriteOperations.DestroySkyhooksAsync,
                    sovWriteOperations.AcquireSkyhooksAsync)),
    ];
}
