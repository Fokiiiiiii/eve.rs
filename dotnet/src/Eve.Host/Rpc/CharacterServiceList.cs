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
    async ValueTask<Eve.Services.Space.SpaceRpcItem?> ReadTravelRpcItemAsync(long itemId, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(itemId, cancellationToken);
        if (item is null) return null;
        var type = await itemTypes.GetAsync(item.TypeId, cancellationToken);
        return new Eve.Services.Space.SpaceRpcItem(item.Id, item.OwnerId, item.TypeId, type?.GroupId ?? 0, item.Quantity);
    }

    private Task CharacterServiceList()
    {
        services = [
            new CharUnboundMgrService(
                accounts, characters, items, training, wallets, creation, catalog, online, characterEntryHooks, Random.Shared,
                newCharacterExperienceMode == "tutorial",
                sendWelcomeMailAsync: SendNewCharacterWelcomeMailAsync,
                unreadMailCount: ReadUnreadMailCount,
                securityStatusOf: securityStatusOf),
            new MultiLoginBlockerService(),
            new PopulationCapService(characters),
            new Eve.Services.CharacterInfo.ConfigService(characters, corporations, stations, solarSystems),
            Eve.Services.Core.CoreServices.CreateLookupService(
                new Eve.Host.PersistenceLookupDirectory(characters, corporations, sde, stations)),
            new Eve.Services.Core.SecurityMgrService(),
            new Eve.Services.Core.RWManagerService(),
            new Eve.Services.Core.FwCharacterEnlistmentMgrService(new Eve.Services.Core.FwCharacterEnlistmentRpcCallbacks(
                ReadCharacterStateAsync: async (characterId, cancellationToken) =>
                {
                    var character = await characters.GetAsync(characterId, cancellationToken);
                    return character is null
                        ? null
                        : new Eve.Services.Core.FwCharacterWarfareState(character.DirectFactionId, character.DirectEnlistmentCooldownFileTime);
                },
                ReadCorporationFactionIdAsync: async (corporationId, cancellationToken) =>
                    Eve.Services.Corporations.CorporationWire.FactionId(await corporations.GetAsync(corporationId, cancellationToken)),
                ReadAllowedFactionsAsync: async (corporationId, cancellationToken) =>
                    Eve.Services.Corporations.CorporationWire.AllowedEnlistmentFactions(await corporations.GetAsync(corporationId, cancellationToken)),
                SetAllowedFactionsAsync: async (characterId, corporationId, factions, cancellationToken) =>
                    await corporations.SetFactionWarfareAllowedEnlistmentFactionsAsync(characterId, corporationId, factions, cancellationToken),
                SaveCharacterStateAsync: async (characterId, factionId, cooldown, cancellationToken) =>
                    await characters.SetFactionWarfareStateAsync(characterId, factionId, cooldown, cancellationToken))),
            new Eve.Services.Core.FwVictoryPointMgrService(),
            new Eve.Services.Core.InsurgencySolarsystemService(),
            new Eve.Services.Core.AlertService(),
            new Eve.Services.Core.FighterMgrService { Provider = new Eve.Services.Space.SpaceFighters(sim, ships, dogma, session => spaceSessions?.SolarSystemOf(session), message => Console.Error.WriteLine($"eve-host: fighters: {message}")) },
            new Eve.Services.Space.EssMgrService(),
            new Eve.Services.Space.DynamicResourceCacheMgrService(),
            new Eve.Services.Core.ShipKillCounterService(ct => ReadGameStoreTableAsync("shipKillCounters", ct)),
            // security status is the character's relation of that kind (Eve.Relations); crimewatch only asks for it
            new Eve.Services.Crimewatch.CrimewatchService(
                crimewatchStore, crimewatchRuntime, now,
                securityStatusOf),
            new Eve.Services.Crimewatch.KillRightMgrService(
                crimewatchStore, crimewatchRuntime, economyWallets, wallets, now,
                message => Console.Error.WriteLine($"eve-host: crimewatch: {message}"))
            {
                KillRightUsedNotification = (activation, ct) => mailSendWriter.WriteKillRightNotificationAsync(
                    activation.OwnerCharacterId, activation.TargetCharacterId,
                    Eve.Services.Crimewatch.KillRightNotificationTypes.Used,
                    new JsonObject { ["charID"] = activation.TargetCharacterId },
                    $"kill-right-used:{activation.ActivationId}", ct)
            },
            new Eve.Services.Crimewatch.BountyProxyKillRightService(
                store: crimewatchStore,
                notificationWriter: mailSendWriter.WriteKillRightNotificationAsync,
                nowUnixMs: now),

            new SkillMgr2Service(),
            skillHandler,
            new CharMgrService(
                characters,
                stations,
                charMgrReadModel.ReadRecentShipKillmailPageAsync,
                charMgrReadModel.ReadCharacterDescriptionAsync,
                charMgrReadModel.WriteCharacterDescriptionAsync,
                ReadCharacterSecurityStatusAsync,
                charMgrReadModel.ReadMedal1GraphicIdAsync,
                charMgrReadModel.ReadAsync,
                charMgrReadModel.UpdateAsync,
                charMgrReadModel.WriteActivityStatusAsync,
                charMgrReadModel.WriteLogSettingsAsync,
                async (characterId, token) => (await corporations.MemberAsync(characterId, token))?.StartedMs,
                async (memberCharacters, token) =>
                {
                    var corporationIds = memberCharacters
                        .Select(character => character.CorporationId)
                        .Where(id => id > 0)
                        .Distinct()
                        .ToArray();
                    var corporationsById = (await corporations.GetManyAsync(corporationIds, token))
                        .ToDictionary(corporation => corporation.Id);
                    var characterState = await ReadGameStoreTableAsync("characters", token);
                    return memberCharacters.ToDictionary(character => character.Id, character =>
                    {
                        var corporation = corporationsById.GetValueOrDefault(character.CorporationId);
                        var key = character.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var row = characterState[key] as JsonObject;
                        var warFactionId = CharMgrJsonLong(row?["warFactionID"] ?? row?["militiaFactionID"]);
                        return new Eve.Services.CharacterInfo.CharMgrOrganizationInfo(
                            character.CorporationId,
                            character.AllianceId ?? corporation?.AllianceId ?? 0,
                            Math.Max(0, warFactionId));
                    });
                }),
            new Eve.Services.CharacterInfo.OnlineStatusService(characterSessions, ReadWatchlistAsync),
            new StationService(stations, characters, online, now, sovereigntyReadModel.ReadAllianceSystemsAsync, officeManager: officeManager, seedObserverGuestLedger: stationGuestLedger.SeedObserverGuestLedger, characterSessions: characterSessions),
            new Eve.Services.Station.TradeMgrService(characterSessions),
            new Eve.Host.DevIndexManagerRpcService(sovereigntyReadModel.ReadAllDevelopmentIndicesAsync),
            new Eve.Host.EjectRpcService(),
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
            new Eve.Host.InSpaceCompressionMgrRpcService(async (itemId, cancellationToken) =>
                await items.GetAsync(itemId, cancellationToken)),
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
            new Eve.Services.Core.IncursionAnomalyService(
                "map",
                incursionAnomalyReadModel.ReadIncursionsAsync,
                incursionAnomalyReadModel.ReadAnomaliesAsync,
                new MapService(foundations.Reads, now) { ReadDataHook = ReadMapRpcDataAsync },
                now),
            new Eve.Services.Core.DungeonService(dungeonReadModel.ReadEditorCatalogAsync),
            new Eve.Services.Core.DungeonInstanceCacheMgrService(dungeonReadModel.ReadCacheSnapshotAsync, now),
            new Eve.Services.Core.IncursionAnomalyService(
                "taleMgr",
                incursionAnomalyReadModel.ReadIncursionsAsync,
                incursionAnomalyReadModel.ReadAnomaliesAsync,
                nowUnixMs: now),
            new Eve.Services.Core.IncursionAnomalyService(
                "scanMgr",
                incursionAnomalyReadModel.ReadIncursionsAsync,
                incursionAnomalyReadModel.ReadAnomaliesAsync,
                nowUnixMs: now,
                signalTrackerRegister: signalTrackerRegistrationProvider.RegisterAsync),
              HostInventoryServiceFactory.CreateInvBroker(items, itemTypes, stations, ships, dogma, now, spaceContainers, spaceDrones, corporations, characterEntryHooks),
              new Eve.Services.Core.CrateService(new PersistenceCrateServiceBackend(source, items, itemTypes, skills, skillCatalog, mailCache)),
              new Eve.Host.ShipCosmeticsMgrRpcService(mailCache, mailCache, items, source, skinCosmeticsCatalog, now,
                  (characterId, shipId, skinId) =>
                  {
                      if (characterSessions.Of(characterId) is not { } session) return;
                      session.SendNotification("OnShipCosmeticsChanged", "clientID", InventoryWire.Id(shipId), new PyDict([]));
                      session.SendNotification("OnCurrentShipSkinChange", "clientID", InventoryWire.Id(shipId), new PyInt(skinId ?? 0));
                  },
                  (characterId, skinId) =>
                  {
                      if (characterSessions.Of(characterId) is { } session)
                          session.SendNotification("OnSkinLicenseActivated", "clientID",
                              new PyList([new PyInt(skinId), new PyInt(characterId)]));
                  },
                  inventoryItemConsumed: async (characterId, before, remainingQuantity, ct) =>
                  {
                      if (characterSessions.Of(characterId) is not { } session) return;
                      var type = await itemTypes.GetAsync(before.TypeId, ct);
                      var row = new ItemRow(before.Id, before.TypeId, before.OwnerId, before.LocationId, before.Flag, remainingQuantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
                      var changes = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(9), InventoryWire.Id(before.Quantity))]);
                      session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), changes, PyNone.Instance);
                  }),
              new Eve.Host.DynamicItemRpcService(items, itemMutations, dogma, itemTypes),
              new Eve.Host.CharacterEnergyMgrRpcService(mailCache, now),
              new Eve.Host.PaperDollServerRpcService(characters),
              new Eve.Host.PlanetMgrRpcService(mailCache, sde, now),
              new Eve.Host.IndustryMonitorRpcService(mailCache, items, nowUnixMs: now, itemTypes: itemTypes),
              new DogmaIMService(
                dogma, ships, now,
                skillHandler.TryActivateBoosterAsync,
                sync: dogmaSync,
                space: spaceSessions,
                modules: spaceSessions,
                health: spaceSessions,
                rounds: spaceSessions,
                log: message => Console.Error.WriteLine($"eve-host: ammo: {message}"),
                localTimers: foundations.LocalTimers),
            new AccountService(wallets, corporations, economyWallets),
            new Eve.Services.Market.MarketProxyService(market, marketTrading, marketTopology, characters, economyWallets, marketCatalog, now, characterSessions),
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
            new Eve.Services.Space.FleetObjectService(
                fleetStore, now,
                loadFleetSetup: async (fleetId, characterId, setupName, _, cancellationToken) =>
                {
                    var characters = await ReadGameStoreTableAsync("characters", cancellationToken);
                    var setup = Eve.Host.PersistenceFleetSettings.FindSetup(characters, characterId, setupName)
                        ?? throw new InvalidOperationException("FleetError");
                    if (!await fleetStore.ApplyFleetSetupAsync(fleetId, characterId, setup, cancellationToken))
                        throw new InvalidOperationException("FleetError");
                    return PyNone.Instance;
                },
                createFleetAdvert: (fleetId, characterId, advertData, session, cancellationToken) =>
                    fleetStore.FleetFinderAdvertOperationAsync(
                        "AddFleetFinderAdvert", characterId, [advertData], session,
                        FleetAdvertLeaderProfileForAsync, cancellationToken),
                sessionForCharacter: FleetSessionForCharacter,
                compositionForMembers: FleetCompositionForMembersAsync,
                sameBubble: SameFleetBubble),
            new Eve.Services.Space.FleetManagerService(
                fleetStore,
                sessionOf: FleetSessionForCharacter,
                sameBubble: SameFleetBubble),
            new Eve.Services.Space.FleetProxyService(
                fleetStore,
                fleetAdvertOperation: (operation, characterId, args, session, cancellationToken) =>
                    fleetStore.FleetFinderAdvertOperationAsync(operation, characterId, args, session,
                        FleetAdvertLeaderProfileForAsync, cancellationToken),
                sessionForCharacter: FleetSessionForCharacter),
            insuranceService,
            new MailService(
                ct => ReadGameStoreTableAsync("mail", ct),
                async (characterId, messageId, requestingSession, ct) =>
                {
                    var statusPath = $"/mailboxes/{characterId}/statuses/{messageId}";
                    if (mailCache.Read("mail", statusPath).Data is not JsonObject status)
                        return;
                    var priorMask = long.TryParse(status["statusMask"]?.ToString(),
                        System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                        out var parsedMask) ? parsedMask : 0;
                    if ((priorMask & 1) != 0)
                        return;
                    status["statusMask"] = priorMask | 1L;
                    var write = mailCache.Write("mail", statusPath, status);
                    if (!write.Success)
                        throw new InvalidOperationException($"Could not mark mail {messageId} read: {write.ErrorMsg}");
                    await mailCache.FlushTableAsync("mail", ct);
                    if (characterSessions.Of(characterId) is { } otherSession &&
                        !ReferenceEquals(otherSession, requestingSession))
                    {
                        otherSession.SendNotification("OnMailUpdatedByExternal", "clientID",
                            new PyTuple([new PyList([new PyInt(messageId)]), PyBool.True, PyNone.Instance]));
                    }
                },
                mailSendWriter.SendAsync),
            new NotificationMgrService(
                async (characterId, ct) =>
                {
                    var notifications = await ReadGameStoreTableAsync("notifications", ct);
                    return (notifications["boxes"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
                },
                now),
            new MailingListService(
                async ct =>
                {
                    var mailState = await ReadGameStoreTableAsync("mail", ct);
                    return mailState["mailingLists"] as JsonObject;
                },
                mutateMailingList),
            new RewardsService(),
            new Eve.Services.Core.LoginCampaignMgrService(storeRuntime),
            new Eve.Services.Core.SeasonalLoginCampaignMgrService(storeRuntime),
            new StoreOffersService(
                async ct => await ReadGameStoreTableAsync("newEdenStore", ct),
                storeRuntime.PurchaseAsync),
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
                    {
                        call.Session.AfterResponse(call.Session.Close);
                        return PyBool.True;
                    }
                    if (call.Method == "CreateRedeemTokenQA")
                        return await storeRuntime.CreateRedeemTokenQAAsync(call, ct).ConfigureAwait(false);
                    return null;
                }),
            new Eve.Services.Core.VaultManagerService(storeRuntime),
            new Eve.Services.Core.FacilityManagerService(FacilityManagerRpcAsync),
            new IndustryJobsService(
                industryJobOperations.ReadJobsAsync,
                industryJobOperations,
                currentFileTime: () => checked(116444736000000000L + now() * 10_000L)),
            new Eve.Services.Core.CharacterMiningLedgerService(miningLedgerReads, () => Eve.Ships.MiningLedgerStore.FileTimeOf(now())),
            new Eve.Services.Core.CorpMiningLedgerService(miningLedgerReads, () => Eve.Ships.MiningLedgerStore.FileTimeOf(now())),
            new Eve.Services.Core.StructureDirectoryService(structureDirectoryReadModel),
            // Mutation needs structure-control/drill authority and the space timer/settlement backend.
            new Eve.Services.Core.MoonExtractionsRpcService(
                readState: () => mailCache.Read("moonExtractions", "/").Data as JsonObject ?? new JsonObject(),
                mutationBackend: null),
            new Eve.Services.Space.StructureTravelRpcService(
                "structureJumpBridgeMgr",
                jumpBridgeDestination: structureDirectoryReadModel.GetJbStructureDestinationAsync,
                linkedStructure: structureDirectoryReadModel.GetLinkedStructureAsync),
            new Eve.Services.Core.StructureRpcService("structureCargoDelivery"),
            new Eve.Services.Core.StructureRpcService("structureCompressionMgr"),
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
            new AchievementTrackerMgrService(
                readState: async (characterId, ct) =>
                    LoginInitializationGameStoreProjection.ReadAchievementState(
                        await ReadCharacterGameStoreRowAsync(characterId, ct)),
                hasEverWarped: async (characterId, ct) =>
                    LoginInitializationGameStoreProjection.ReadHasEverWarped(
                        await ReadCharacterGameStoreRowAsync(characterId, ct)) ?? true),
            new MilestoneMgrService(),
            new OperationsManagerService(
                readCharacterProgress: async (call, ct) =>
                    LoginInitializationGameStoreProjection.ReadOperationsProgress(
                        await ReadCharacterGameStoreRowAsync(LoginInitializationGameStoreProjection.CharacterId(call), ct)),
                newCharacterExperienceMode: () => newCharacterExperienceMode,
                readTutorialState: ReadAirNpeCharacterStateAsync),
            new MissionTrackerMgrService(
                readMissionInfoItems: agentMissionService.ReadMissionInfoItemsAsync,
                readAllMissionObjectives: async (characterId, agentId, session, ct) =>
                {
                    var context = await ReadMissionTrackerContextAsync(characterId, agentId, session, ct);
                    return MissionTrackerGameStoreProjection.ReadAllObjectives(
                        context.MissionRecord, agentId, context.InActiveDungeon == true, context.DungeonState);
                },
                isInActiveDungeon: async (call, ct) =>
                {
                    var characterId = call.Session.GetInt("charid") ?? call.Session.GetInt("characterID") ?? 0;
                    var agentId = LoginInitializationGameStoreProjection.PositiveId(call.Arg(0));
                    if (characterId <= 0 || agentId <= 0) return false;
                    return (await ReadMissionTrackerContextAsync(characterId, agentId, call.Session, ct)).InActiveDungeon;
                }),
            new AbyssalMgrService(),
            new VoidSpaceMgrService(),
            new AirNpeService(
                () => newCharacterExperienceMode,
                ReadAirNpeCharacterStateAsync,
                (characterId, tutorialEnabled, ct) => characters.MarkTutorialSkippedAsync(characterId, tutorialEnabled, ct)),
            agentMissionService,
            allianceRegistry,
            publicQaSlashService = new SlashService(new ShipHealthCommands(ships, dogma, now, spaceSessions), spaceSessions is null ? null : new DeathTestCommands(spaceSessions, new ShipTypeNames(sde)), spaceSessions is null ? null : new SuicideCommands(spaceSessions), spaceSessions is null ? null : new NpcCommands(spaceSessions), new ItemCommands(new ItemTypeNames(sde), ships, dogma, now), new SkillCommands(skillCatalog, skills, source, dogmaSync, now)),
            .. spaceSessions is null ? [] : new IService[] { new ShipService(spaceSessions, now, spaceContainers, spaceDrones), new BeyonceService(spaceSessions, now, fleetStore, FleetSessionForCharacter), new StructureTravelRpcService("structureCynoBeaconMgr"), new ShipcasterRpcService("shipCasterLandingPadMgr", solarSystems), new ShipcasterRpcService("shipCasterLauncherMgr", solarSystems), new ShipcasterRpcService("shipcasterTravelMgr", solarSystems), new ShipcasterRpcService("starterShipcasterTravelMgr", solarSystems), new EntityService(spaceDrones!, now, spaceContainers) },
        ];
        services = [.. services,
            new Eve.Services.Space.PvpFilamentMgrService(),
            new Eve.Services.Space.RandomJumpMgrService(ReadTravelRpcItemAsync),
            new Eve.Services.Space.WarpVectorMgrService(ReadTravelRpcItemAsync),
            new Eve.Services.Space.WormholeMgrService(spaceSessions),
            new Eve.Services.Core.StructureGuestsService(
            (structureId, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var guests = characterSessions.InStructure(structureId)
                    .Where(session => session.CharacterId is > 0)
                    .Select(session => new Eve.Services.Core.StructureGuestSnapshot(
                        session.CharacterId!.Value,
                        Math.Max(0, session.GetInt("corpid") ?? 0),
                        Math.Max(0, session.GetInt("allianceid") ?? 0),
                        Math.Max(0, session.GetInt("warfactionid") ?? 0)))
                    .ToArray();
                return ValueTask.FromResult<IReadOnlyList<Eve.Services.Core.StructureGuestSnapshot>>(guests);
            }, seedObserverGuestLedger: stationGuestLedger.SeedObserverGuestLedger)];
        services = [.. services, new RepackagingService(services.OfType<InvBrokerService>().Single())];
          services = [.. services, new Eve.Services.Corporations.BillMgrService(
              corporations,
              corporationOfficeStore,
              economyWallets,
              stationId => stations.Get(stationId)?.OwnerId,
              now)];
        return Task.CompletedTask;
    }
}
