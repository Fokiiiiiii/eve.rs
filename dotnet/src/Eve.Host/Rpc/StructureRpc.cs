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
    /// <summary>Structures: directory, moon extractions, travel, control, asset safety, settings and skyhooks.</summary>
    private async Task<JsonObject?> ReadMoonExtractionsAsync() => await moonExtractionStore.ReadLegacyAsync();

    private IReadOnlyList<IService> StructureRpc() =>
    [
            new Eve.Services.Core.StructureDirectoryService(structureDirectoryReadModel),
            // Mutation needs structure-control/drill authority and the space timer/settlement backend.
            new Eve.Services.Core.MoonExtractionsRpcService(
                readState: ReadMoonExtractionsAsync,
                mutationBackend: call => structureMoon.CallAsync(call)),
            new Eve.Services.Space.StructureTravelRpcService(
                "structureJumpBridgeMgr",
                jumpBridgeDestination: structureDirectoryReadModel.GetJbStructureDestinationAsync,
                linkedStructure: structureDirectoryReadModel.GetLinkedStructureAsync),
            new Eve.Services.Core.StructureRpcService("structureCargoDelivery"),
            new Eve.Services.Core.StructureRpcService("structureCompressionMgr", structureRpcCallbacks),
            new Eve.Services.Core.StructureRpcService("structureControl"),
            new Eve.Services.Core.StructureRpcService("structureDeliveries"),
            new Eve.Services.Core.StructureRpcService("structureDeployment"),
            new Eve.Services.Core.StructureRpcService("structureDocking"),
            new Eve.Services.Core.StructureRpcService("structureHangarViewMgr", structureRpcCallbacks),
            new Eve.Services.Core.StructureRpcService("structureProfiles", structureRpcCallbacks),
            new Eve.Services.Core.StructureRpcService("structureVulnerability"),
            new Eve.Services.Core.StructureAssetSafetyService(structureAssetSafetyReadModel),
            structureSettingsService,
            skyhookResourceMgrService,
    ];
}
