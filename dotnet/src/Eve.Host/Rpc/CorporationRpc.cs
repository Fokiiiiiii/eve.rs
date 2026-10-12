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
            new Eve.Services.Corporations.CorpRegistryService(corporations, characters, now, characterSessions, items,
                  readRecentCorporationKillmails: ReadRecentCorporationKillmailPageAsync,
                  readCharacterAllyBaseCost: ReadCharacterAllyBaseCostAsync,
                  declareWar: CreateWarDeclarationAsync)
            {
                AllianceAffiliationWriter = allianceDue.WriteAffiliationAsync
            },
            new Eve.Services.Corporations.CorpRecruitmentProxyService(corporations, characters, now, characterSessions),
            officeManager,
            new Eve.Services.Corporations.StandingMgrService(foundations.Reads, corporations, now),
            new Eve.Services.Corporations.WarsInfoMgrService(foundations.Relations, now),
            new Eve.Services.Corporations.VoteManagerService(corporations),
            new Eve.Services.Corporations.ItemLockingService(corporations),
            new Eve.Services.Corporations.CorpStationMgrService(corporations, characterSessions, StandingTowardAsync, StationPlace,
                StationStandingRulesAsync, now),
    ];

      private async ValueTask<Eve.Services.Corporations.CorpWarDeclarationResult> CreateWarDeclarationAsync(
          Eve.Services.Corporations.CorpWarDeclarationRequest request,CancellationToken cancellationToken)
      {
          var created = await corporations.CreateWarDeclarationAsync(
              request.CorporationId,request.DeclaredByCharacterId,request.AgainstId,request.WarHQId,
              request.TimeDeclaredFiletime,request.TimeStartedFiletime,request.DueFiletime,request.BillTypeId,
              request.BillAmount,request.CreditorId,request.OperationKey,
              async (connection,transaction,billId,token) =>
              {
                  if (await foundations.Relations.GetAsync("war",request.CorporationId,request.AgainstId,
                          connection,transaction,token,forUpdate:true) is not null)
                      throw new Eve.Corporations.CorporationError("WAR_ALREADY_DECLARED");
                  var attrs = new
                  {
                      declaredByCharacterID=request.DeclaredByCharacterId,
                      timeDeclared=request.TimeDeclaredFiletime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                      timeStarted=request.TimeStartedFiletime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                      timeFinished=(string?)null,
                      retracted=(string?)null,
                      retractedBy=(long?)null,
                      billID=billId,
                      mutual=0,
                      fightingLegalNotified=0,
                      openForAllies=0,
                      createdFromWarID=(long?)null,
                      warHQID=request.WarHQId,
                      warHQ=request.WarHQId,
                      reasonEnded=0,
                      reasonStarted=0,
                      reward=0
                  };
                  var change = await foundations.Relations.SetAsync(connection,transaction,"war",
                      request.CorporationId,request.AgainstId,1.0,"declared",attrs,cancellationToken:token);
                  if (change is not null)
                      return change.RelationId;
                  return (await foundations.Relations.GetAsync("war",request.CorporationId,request.AgainstId,
                      connection,transaction,token,forUpdate:true))?.Id ?? 0;
              },
              cancellationToken);
          return new Eve.Services.Corporations.CorpWarDeclarationResult(created.WarId,created.BillId);
      }

}
