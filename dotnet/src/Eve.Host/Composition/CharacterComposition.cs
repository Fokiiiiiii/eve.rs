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
    private async Task ItemMutationsOmegaTraining()
    {
        // the abyssal mutation authority (ADR 0016): the mutaplasmid catalog of the static data, the item store and its provenance, the event bus
        itemMutations = new Eve.Inventory.Mutation.ItemMutationService(
            source, items, items.Mutations, await Eve.Inventory.Mutation.MutatorCatalog.LoadAsync(sde), dogma, new BusItemMutationEvents(foundations.Events), now);
        training = new CharacterTraining(
            source, skillCatalog, skills, new PgTrainingStore(source, skillsSchema),
            new TrainingSettings
            {
                Speed = double.Parse(Env("EVE_SKILL_TRAINING_SPEED", "1"), System.Globalization.CultureInfo.InvariantCulture),
                HasOmega = account => omega.Resolve(new PyInt(account)).HasLicense,
                // each running extra queue (Multiple Character Training) lets one more of the account's characters train
                AdditionalTrainingSlots = account => accounts.ActiveTrainingSlotCount(checked((uint)account), DateTimeOffset.FromUnixTimeMilliseconds(now())),
                Attribute = skills.LearningAttribute,
            },
            now);
    }

        static bool ContactWatchlisted(JsonNode? node)
        {
            if (node is not JsonValue value)
                return false;
            return value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number != 0,
                JsonValueKind.String => value.GetValue<string>() is "1" or "true",
                _ => false,
            };
        }

        async ValueTask<IReadOnlyCollection<long>> ReadWatchlistAsync(long characterId, CancellationToken cancellationToken)
        {
            var state = await ReadGameStoreTableAsync("characters", cancellationToken);
            var contacts = (state[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject)?["personalContacts"];
            var ids = new HashSet<long>();
            if (contacts is JsonObject byId)
            {
                foreach (var (key, value) in byId)
                {
                    if (value is JsonObject row && ContactWatchlisted(row["inWatchlist"]))
                    {
                        var id = long.TryParse(row["contactID"]?.ToString(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var storedId) ? storedId :
                            long.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var keyedId) ? keyedId : 0;
                        if (id > 0) ids.Add(id);
                    }
                }
            }
            else if (contacts is JsonArray rows)
            {
                foreach (var row in rows.OfType<JsonObject>())
                {
                    if (ContactWatchlisted(row["inWatchlist"]) &&
                        long.TryParse(row["contactID"]?.ToString(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
                        ids.Add(id);
                }
            }
            return ids;
        }

    private Task PrepareCharacterHelpers()
    {
            // The service re-reads saved contacts for both sides of its mutual-watchlist check.
        // the combat timers and flags: kept in crimewatch's schema, told to the clients as they change (Eve.Services.Crimewatch)
        securityStatusOf = (id, token) => foundations.Relations.ValueAsync(Eve.Relations.RelationKinds.SecurityStatus.Name, id, Eve.Relations.RelationStore.World, 0, null, null, token);
        return Task.CompletedTask;
    }

    private Task TrainingSkillsAndCreation()
    {
        announcer = new TrainingAnnouncer(training, skillCatalog, characterSessions, dogmaSync, now, localTimers: foundations.LocalTimers);
        announcer.Attach();
        readAugmentations = SkillHandlerService.CreateAugmentationReader(items, dogma, skills, now);
        skillHandler = new SkillHandlerService(
            skills, skillCatalog, training, characters, items, dogma, announcer, now, readAugmentations,
            walletTransactions: economyWallets,
            isAlphaClone: accountId => !omega.Resolve(new PyInt(accountId)).HasLicense);
        startingWallet = new StartingWallet(
            decimal.Parse(Env("EVE_START_ISK", "100000"), System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(Env("EVE_START_PLEX", "2222"), System.Globalization.CultureInfo.InvariantCulture));
        Console.Error.WriteLine($"eve-host: characters in PostgreSQL, schemas {characterSchema}, {inventorySchema}, {skillsSchema} and {walletSchema}; {stations.Count} stations; dogma of {dogma.AttributeCount} attributes and {dogma.EffectCount} effects; static data build {build}; new characters start at {(startStation is { } id ? $"station {id}" : "their school's headquarters")}");
        creation = new CharacterCreation(
            source, characters, items, skills, skillCatalog, wallets, startingWallet, catalog, modules,
            newCharacterExperienceMode == "tutorial", newCharacterExperienceMode,
            (connection, transaction, characterId, corporationId, token) => foundations.Relations.SetAsync(
                connection, transaction, Eve.Relations.RelationKinds.CorporationMember.Name, characterId, corporationId, 1,
                "corporation membership", cancellationToken: token));
        return Task.CompletedTask;
    }

    /// <summary>The login milestone: its progress in the characters schema, its timer in this process. One instance serves the entry hook and the service.</summary>
    private MilestoneMgrService? milestoneMgr;
    private MilestoneMgrService MilestoneManager => milestoneMgr ??= new MilestoneMgrService(
        new Eve.Characters.LoginMilestoneStore(source, characterSchema, economyWallets), foundations.LocalTimers, now);

    /// <summary>The achievement tracker of each character (the characters schema).</summary>
    private AchievementTrackerStore? achievementTracker;
    private AchievementTrackerStore AchievementTracker => achievementTracker ??= new AchievementTrackerStore(source, characterSchema);

    private Task EconomyAndInsurance()
    {
        economyWallets = new WalletTransactions(source, walletSchema);
        economyAnnouncer = new WalletAnnouncer(economyWallets, characterSessions);
        loyaltyPoints = new LoyaltyPointStore(source, walletSchema);
        // the loyalty store offers: ESI's official offers, refreshed daily; EVE_LP_STORE_SEED=skip answers what is stored and calls nothing
        lpStoreCache = new Eve.Wallet.LpStoreOfferCache(
            new Eve.Wallet.LpStoreOfferStore(source, walletSchema),
            Env("EVE_LP_STORE_SEED", "auto") == "skip" ? null : new Eve.Wallet.EsiLpStoreOffers(lpStoreHttp, Eve.Wallet.EsiLpStoreOffers.DefaultBase),
            () => DateTimeOffset.UtcNow,
            message => Console.Error.WriteLine($"eve-host: lp store: {message}"));
        lpStorePurchases = new Eve.Services.Wallet.LpStorePurchases(source, walletSchema, economyWallets, loyaltyPoints, items,
            (typeId, token) => itemTypes.GetAsync(typeId, token), lpStoreCache);
        loyaltyAnnouncer = new LoyaltyPointAnnouncer(loyaltyPoints, characterSessions);
        insuranceReadModel = new PersistenceInsuranceReadModel(mailStore);
        insuranceWriteModel = new PersistenceInsuranceWriteModel(
            source, items, mailStore, mailStoreOwner.Fence, economyWallets, insuranceReadModel, now);
        items.ShipInsuranceInvalidator = insuranceWriteModel;
        insuranceService = new Eve.Services.Core.InsuranceService(insuranceReadModel, now, insuranceWriteModel);
        storeRuntime = new PersistenceNewEdenStoreRuntime(
            source, mailStore, mailStoreOwner.Fence, wallets, characters, items, itemTypes,
            characterSessions, now, _ => Task.FromResult(Eve.Services.Core.NewEdenStoreCatalog.Default.Root), accounts);
        return Task.CompletedTask;
    }

    private Task CharacterEntryHooks()
    {
        publicQaSlashService = null;
        gameRpcCallbacks = new Eve.Services.Core.GameRpcCallbacks(
            ReadSharedSettings: () => mailCache.Read("sharedSettings", "/").Data as JsonObject,
                    ReadOverviewPresets: () => mailCache.Read("overviewSharedPresets", "/").Data as JsonObject,
                    WriteOverviewPresets: state => mailCache.Write("overviewSharedPresets", "/", state).Success,
            WriteSharedSettingsAsync: async (state, cancellationToken) =>
            {
                var write = mailCache.Write("sharedSettings", "/", state);
                if (!write.Success)
                    return false;
                await mailCache.FlushTableAsync("sharedSettings", cancellationToken);
                return true;
            },
            SetShipStanceAsync: SetShipStanceAsync,
            ReadInterferenceTableAsync: async cancellationToken =>
                await ReadGameStoreTableAsync("solarSystemInterferenceState", cancellationToken),
            ReadKillmailAsync: ReadKillmailAsync,
            ReadMiningScanResultsAsync: spaceSessions is null
                ? null
                : new Eve.Host.MiningScanRpcReadModel(spaceSessions, items, spaceItemStore, dogma).ReadAsync,
            ReadPeaceTreaties: async (ownerId, cancellationToken) =>
            {
                var state = await ReadGameStoreTableAsync("corporationRuntime", cancellationToken);
                var nowFiletime = DateTimeOffset.FromUnixTimeMilliseconds(now()).ToFileTime();
                return PersistencePeaceTreatyReadModel.Read(state, ownerId, nowFiletime);
            },
            PublicQaTools: new Eve.Services.Core.PublicQaToolsRpcCallbacks(
                IsStaff: Eve.Services.Core.PublicQaToolsServerService.HasStaffAccountRole,
                SlashCmdAsync: async (request, cancellationToken) =>
                {
                    if (publicQaSlashService is null)
                        return null;
                    return await publicQaSlashService.SlashCmd(new ServiceCall
                    {
                        Method = "SlashCmd",
                        Args = request.Arguments,
                        Kwargs = request.KeywordArguments,
                        Session = request.Session,
                        CallId = request.CallId,
                        Cancellation = cancellationToken,
                    });
                }),
            JumpClones: new HostJumpCloneStore(new Eve.Characters.PgJumpCloneStore(source, characterSchema), source, skills));

        stationGuestLedger = new Eve.Services.Station.StationGuestLedger();
        characterEntryHooks = new List<ICharacterEntryHook>();
        if (spaceSessions is not null)
            characterEntryHooks.Add(spaceSessions);
        characterEntryHooks.Add(new SkillsLoginHook(training, skillCatalog, characterSessions, announcer, now));
        characterEntryHooks.Add(MilestoneManager);
        characterEntryHooks.Add(corporationLogin);
        characterEntryHooks.Add(new FleetLoginHook(fleetStore.MemberForCharacterAsync));
        characterEntryHooks.Add(dogmaSync);
        var guests = new Eve.Services.Station.GuestLoginHook(characterSessions, stationGuestLedger);
        characterEntryHooks.Add(guests);
        if (spaceSessions is not null)
            spaceSessions.DockedLocationChanged = guests.OnLocationChanged;
        return Task.CompletedTask;
    }
}
