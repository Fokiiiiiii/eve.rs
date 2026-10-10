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
    private async Task SpaceSimulation()
    {
        sim = null;
        spaceSessions = null;
        spaceContainers = null;
        spaceDrones = null;
        if (Env("EVE_SIM", "on") is not ("off" or "0" or "false"))
        {
            var socket = Env("EVE_SIM_SOCKET", DefaultSimSocket(OperatingSystem.IsWindows()));
            sim = socket.StartsWith("tcp:", StringComparison.Ordinal)
                ? SimClient.ToTcpSocket(socket["tcp:".Length..], message => Console.Error.WriteLine($"eve-host: {message}"))
                : SimClient.ToUnixSocket(socket, message => Console.Error.WriteLine($"eve-host: {message}"));
            sim.Start();
            var npcAuthority = NpcAuthority.Load();
            var securityRecovery = new Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber<Eve.Events.EventContext>(
                profile => npcAuthority.Profile(profile) is { Bounty: > 0 } npc
                    ? new Eve.Services.Crimewatch.NpcSecurityStatusProfile(npc.Bounty, npc.CorporationId, npc.FactionId, npc.ShipTypeId)
                    : null,
                async (context, characterId, token) =>
                    1.0 + ((await skills.LevelAsync(context.Connection, context.Transaction, characterId, 3358, token)) * 0.05),
                (previous, bounty, fastTalk) => Eve.Npc.NpcSecurityStatusRecovery.TryNext(bounty, previous, fastTalk),
                async (context, characterId, token) =>
                    await foundations.Relations.ValueAsync(Eve.Relations.RelationKinds.SecurityStatus.Name,
                        characterId, Eve.Relations.RelationStore.World, 0, context.Connection, context.Transaction, token),
                async (context, change, token) =>
                {
                    await foundations.Relations.SetAsync(context.Connection, context.Transaction,
                        Eve.Relations.RelationKinds.SecurityStatus.Name, change.CharacterId, Eve.Relations.RelationStore.World,
                        change.NextStatus, "npc_bounty_recovery",
                        new Dictionary<string, object?>
                        {
                            ["eventTypeID"] = change.EventTypeID,
                            ["otherOwnerID"] = change.NpcCorporationId,
                            ["otherTypeID"] = change.NpcTypeId,
                            ["locationID"] = change.SolarSystemId,
                            ["modification"] = change.Modification,
                            ["sourceEventID"] = change.SourceEventId,
                            ["npcShipID"] = change.NpcShipId,
                            ["npcFactionID"] = change.NpcFactionId,
                            ["profileID"] = change.ProfileId,
                            ["bounty"] = change.Bounty,
                            ["fastTalkMultiplier"] = change.FastTalkMultiplier,
                        },
                        cancellationToken: token);
                    await context.PublishAsync("npc.security_status.recovered",
                        new { characterId = change.CharacterId, status = change.NextStatus },
                        $"character:{change.CharacterId}", token);
                });
            // a mining cycle's ledger entry: written in the event's transaction, so a replayed event writes nothing (the entry id is the event id)
            // the mining ledger is a table of the space schema (MiningLedgerStore), read through the rowset read side; the rows the game store held before are copied once
            // (the copy adds only the entries that are not there, so a second start adds none)
            await using (var ledgerConnection = await source.OpenConnectionAsync())
            await using (var ledgerTransaction = await ledgerConnection.BeginTransactionAsync())
            {
                await Eve.Ships.MiningLedgerStore.ImportAsync(ledgerConnection, ledgerTransaction, spaceSchema,
                    (await mailStore.LoadTableAsync("miningLedger")) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject(),
                    Eve.Ships.MiningLedgerStore.FileTimeOf(now()));
                await ledgerTransaction.CommitAsync();
            }
            var miningLedger = new MiningLedgerRecorder(spaceSchema);
            foundations.Events.Subscribe("mining.ledger", [Eve.Ships.OreMinedEvent.Topic], (context, token) => miningLedger.RecordAsync(context, token));
            // the daily goals of mining and salvage: content defined before the foundations attach, advanced by one subscriber (DailyGoalRecorder)
            var dailyGoals = new DailyGoalRecorder(foundations.Content, foundations.Scheduler);
            foreach (var definition in DailyGoalRecorder.Definitions)
            {
                foundations.Content.Define(definition);
            }
            foundations.Events.Subscribe(DailyGoalRecorder.SubscriberName, [Eve.Ships.OreMinedEvent.Topic, Eve.Ships.SalvageWreckedEvent.Topic], (context, token) => dailyGoals.RecordAsync(context, token));
            foundations.Events.Subscribe("npc.security-status-recovery", ["npc.killed"],
                async (context, token) => await securityRecovery.HandleAsync(context, context.Event.Id, context.Event.PayloadJson, token));
            foundations.Events.Subscribe("npc.security-status-notifications", ["npc.security_status.recovered"],
                (context, _) =>
                {
                    var notice = context.Event.Json();
                    if (notice.TryGetProperty("characterId", out var character) &&
                        character.TryGetInt64(out var characterId) && characterId > 0 &&
                        notice.TryGetProperty("status", out var status) &&
                        status.TryGetDouble(out var securityStatus) && double.IsFinite(securityStatus))
                    {
                        characterSessions.Of(characterId)?.SendNotification("OnSecurityStatusUpdate", "charid", Py.Number(securityStatus));
                    }
                    return Task.CompletedTask;
                });
            var npcWrecks = new NpcWrecks(npcAuthority, () => NpcLoot.LoadAsync(sde), ships);
            spaceSessions = new SpaceSessions(sim, ships, dogma, stations, solarSystems, now, message => Console.Error.WriteLine($"eve-host: space: {message}"), localTimers: foundations.LocalTimers, spaceItems: spaceItemStore) { RookieShipOfRace = race => catalog.Race(race)?.ShipTypeId, Npcs = new NpcSelector(npcAuthority), NpcWrecks = npcWrecks, BeltRats = new BeltRats(npcAuthority, BeltRatConfig.FromEnvironment(Environment.GetEnvironmentVariable), type => NpcHull.HasHitPoints(dogma.Type(type))), AutoReloadUsers = AutoReloadUserIds(Env("EVE_AUTO_RELOAD_ACCOUNTS", "")) };
            npcWrecks.Warm();
            // the periodic work of the space features is jobs of the shared scheduler, not timers of their own
            spaceSessions.UseBounties(new PgBountyStore(source, new WalletTransactions(source, walletSchema), npcSchema, message => Console.Error.WriteLine($"eve-host: bounty: {message}")));
            spaceSessions.Events = foundations.Events;   // what happens in space is announced on the bus as well
            ships.TransitionEventWriter = spaceSessions.Transitions.WriteEventAsync;
            spaceSessions.SafetyLevelOf = (character, token) => crimewatchStore.SafetyOfAsync(character, token);   // the ship safety the pilot has set
            spaceSessions.Aggression = async (request, token) => await crimewatchRuntime.RecordAggressionAsync(request, token);   // what an attack makes of the attacker
            spaceSessions.SecurityStatusOf = securityStatusOf;   // the ship in space shows it
            spaceSessions.CrimewatchStateOf = crimewatchRuntime.StateOfAsync;
            spaceSessions.CriminalEnteredSystem = crimewatchRuntime.RespondToCriminalEntryAsync;
            spaceSessions.LifecycleBlockers = async (action, session, _, token) =>
            {
                if (session.CharacterId is not { } characterId || characterId <= 0)
                    return Array.Empty<string>();
                var state = await crimewatchRuntime.StateOfAsync(characterId, token);
                var currentTime = now();
                var blockers = new List<string>();
                if (state.HasWeaponsTimer(currentTime))
                    blockers.Add("weapon_timer");
                if (action == Eve.Services.Space.ShipLifecycleAction.SafeLogoff &&
                    (state.PvpUntilMs > currentTime || state.NpcUntilMs > currentTime))
                    blockers.Add("combat_timer");
                if ((action is Eve.Services.Space.ShipLifecycleAction.Board or Eve.Services.Space.ShipLifecycleAction.Eject) &&
                    state.IsCriminal(currentTime) && state.SolarSystemId is > 0 and <= int.MaxValue &&
                    solarSystems.Get((int)state.SolarSystemId)?.Security >= 0.5)
                    blockers.Add("concord_pursuit");
                return blockers;
            };
            spaceSessions.InvalidateInsuranceForBoarding = async (boarded, token) =>
            {
                await insuranceService.InvalidateShipAsync(new Eve.Services.Core.InsuranceInvalidationRequest(
                    boarded.ShipId, Eve.Services.Core.InsuranceInvalidationReason.Boarded,
                    boarded.BoardedAtFileTime, boarded.ActorCharacterId,
                    boarded.ActorCorporationId > 0 ? boarded.ActorCorporationId : null), token);
            };
            spaceSessions.ShipLost += loss =>
            {
                if (loss.ShipTypeId == Eve.Ships.CharacterShips.CapsuleTypeId)
                    return;
                try
                {
                    insuranceService.SettleShipLossAsync(new Eve.Services.Core.InsuranceLossSettlementRequest(
                        loss.ShipId, loss.ShipId,
                        checked(116444736000000000L + now() * 10_000L),
                        loss.Cause == Eve.Services.Space.ShipLossCause.Concord,
                        loss.CharacterId, 0), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"eve-host: insurance loss {loss.ShipId} could not be settled: {error}");
                }
            };
            crimewatchRuntime.ConcordResponseDue += async request =>
            {
                if (spaceSessions is not null)
                    await spaceSessions.RespondToCriminalAsync(
                        request.AttackerCharacterId, request.SolarSystemId, request.AttackerShipId).ConfigureAwait(false);
            };
            crimewatchRuntime.Aggressed += result =>
            {
                if (characterSessions.Of(result.Request.Attacker) is { } activeSession)
                    spaceSessions.CancelSafeLogoff(activeSession);
                return Task.CompletedTask;
            };
            var weaponSafetyReadModel = new PersistenceWeaponTargetContextReadModel(
                AffiliationOfAsync,
                async (attacker, target, at, token) => crimewatchRuntime.Legal is { } legal && await legal(attacker, target, at, token),
                crimewatchRuntime.StateOfAsync,
                securityStatusOf,
                ReadGameStoreTableAsync,
                now);
            spaceSessions.WeaponTargetContextOf = weaponSafetyReadModel.ReadAsync;
            crimewatchRuntime.AttackPolicyFactsOf = async (request, _, token) =>
            {
                var facts = Eve.Services.Space.WeaponSafety.PolicyFactsOf(
                    await weaponSafetyReadModel.ReadAsync(request.Attacker, request.Target, token));
                return facts with { TargetIsCapsule = request.TargetIsCapsule };
            };
            spaceSessions.RemoteAssistancePolicyOf = crimewatchRuntime.EvaluateRemoteAssistanceAsync;
            spaceSessions.RecordRemoteAssistance = crimewatchRuntime.RecordRemoteAssistanceAsync;
            // a crime costs security status: written to the relation, told to the pilot, shown on the ship in space
            new Eve.Services.Crimewatch.SecurityStatusPenalties(
                crimewatchRuntime,
                characterSessions,
                securityStatusOf,
                async (request, token) =>
                    (await items.GetAsync(request.TargetShipId, token))?.TypeId,
                async (change, token) =>
                {
                    await using var connection = await source.OpenConnectionAsync(token);
                    await using var transaction = await connection.BeginTransactionAsync(token);
                    var attrs = new Dictionary<string, object?>
                    {
                        ["eventTypeID"] = change.EventTypeID,
                        ["otherOwnerID"] = change.OtherOwnerID,
                        ["otherTypeID"] = change.OtherTypeID,
                        ["locationID"] = change.LocationID,
                        ["modification"] = change.Modification,
                        ["targetShipItemID"] = change.TargetShipItemId,
                    };
                    await foundations.Relations.SetAsync(
                        connection, transaction,
                        Eve.Relations.RelationKinds.SecurityStatus.Name,
                        change.CharacterId, Eve.Relations.RelationStore.World, change.NextStatus,
                        change.Reason, attrs, cancellationToken: token);
                    await transaction.CommitAsync(token);
                },
                now,
                async (character, token) => await spaceSessions.AffiliationChangedAsync(character, token)).Attach();
            var bounties = spaceSessions;
            foundations.Scheduler.Register("space.bounty.pay", async (_, token) => await bounties.PayBountiesAsync(token));
            await foundations.Scheduler.EnsureRecurringAsync("space.bounty.pay", "pay", TimeSpan.FromSeconds(10));
            spaceContainers = new SpaceContainers(sim, ships, items, new PgSpaceItemStore(source, spaceSchema), new PgWorkCycleLedger(source, spaceSchema), new Eve.Services.Space.EventBusFacts(foundations.Events), dogma, spaceSessions, now, message => Console.Error.WriteLine($"eve-host: cans: {message}"), deployed: new PgDeployedStore(source, spaceSchema));
            var containers = spaceContainers;
            var probes = new Eve.Services.Space.SpaceProbes(ships, dogma, new Eve.Ships.PgProbeStore(source, spaceSchema), spaceSessions, sim, now, message => Console.Error.WriteLine($"eve-host: probes: {message}"));
            spaceSessions.Probes = probes;
            foundations.Scheduler.Register("space.probes.sweep", async (_, _) => await probes.SweepAsync());
            await foundations.Scheduler.EnsureRecurringAsync("space.probes.sweep", "sweep", TimeSpan.FromSeconds(30));
            spaceDrones = new SpaceDrones(sim, ships, dogma, spaceSessions, now, message => Console.Error.WriteLine($"eve-host: {message}"));
            foundations.Scheduler.Register("space.containers.sweep", async (_, _) => await containers.SweepAsync());
            await foundations.Scheduler.EnsureRecurringAsync("space.containers.sweep", "sweep", TimeSpan.FromSeconds(30));
            Console.Error.WriteLine($"eve-host: the space simulation is expected at {socket}");
        }
        await crimewatchRuntime.ResumeAsync();
    }
}
