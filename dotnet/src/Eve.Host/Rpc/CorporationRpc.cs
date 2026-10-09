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
    /// <summary>Corporations: management, registry, standings, wars and votes.</summary>
    private IReadOnlyList<IService> CorporationRpc() =>
    [
            new Eve.Services.Corporations.CorpMgrService(
                corporations, characters, items, itemTypes, corporationOfficeStore,
                resolveAssetLocation: id => stations.Get(id) is { } station
                    ? (station.SolarSystemId, (int?)station.TypeId)
                    : id is >= 0 and <= int.MaxValue && solarSystems.Get((int)id) is { } system
                        ? ((long)system.Id, (int?)null)
                        : null),
            new Eve.Services.Corporations.MutualWarInviteMgrService(),
            new Eve.Services.Corporations.CorporationService(corporations, characters, characterSessions),
            new Eve.Services.Corporations.CorpRegistryService(corporations, characters, now, characterSessions)
            {
                AllianceAffiliationWriter = allianceDue.WriteAffiliationAsync
            },
            officeManager,
            new Eve.Services.Corporations.StandingMgrService(foundations.Reads, corporations, now),
            new Eve.Services.Corporations.WarsInfoMgrService(foundations.Relations, now),
            new Eve.Services.Corporations.VoteManagerService(corporations),
    ];
}
