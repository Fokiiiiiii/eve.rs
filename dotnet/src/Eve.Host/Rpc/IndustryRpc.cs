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
    /// <summary>Facility manager, industry jobs and the mining ledgers.</summary>
    private IReadOnlyList<IService> IndustryRpc() =>
    [
            new Eve.Services.Core.FacilityManagerService(FacilityManagerRpcAsync),
            new IndustryJobsService(
                industryJobOperations.ReadJobsAsync,
                industryJobOperations,
                currentFileTime: () => checked(116444736000000000L + now() * 10_000L)),
            new Eve.Services.Core.BlueprintManagerService(
                ReadBlueprintAsync,
                ReadOwnedBlueprintsAsync,
                ReadActiveJobsAsync,
                async (characterId, corporationId, stationId, token) =>
                {
                    var member = await corporations.MemberAsync(characterId, token);
                    if (member is null || member.CorporationId != corporationId ||
                        await corporations.GetAsync(corporationId, token) is not { } corporation)
                        return null;
                    return Eve.Corporations.CorporationRoles.Session(
                        corporation, member, stationId, await corporations.TitlesAsync(corporationId, token));
                }),
            new Eve.Services.Core.CharacterMiningLedgerService(miningLedgerReads, () => Eve.Ships.MiningLedgerStore.FileTimeOf(now())),
            new Eve.Services.Core.CorpMiningLedgerService(
                miningLedgerReads,
                async (corporationId, characterId, token) =>
                {
                    var corporation = await corporations.GetAsync(corporationId, token);
                    var member = await corporations.MemberAsync(characterId, token);
                    if (corporation is null || member is null || member.CorporationId != corporationId)
                        return false;
                    var roles = Eve.Corporations.CorporationRoles.Session(
                        corporation, member, null, await corporations.TitlesAsync(corporationId, token));
                    const long ledgerRoleMask = 1L | 256L | (1L << 52);
                    return (roles.AtAll & ledgerRoleMask) != 0;
                },
                () => Eve.Ships.MiningLedgerStore.FileTimeOf(now())),
    ];
}
