# ST-2 durable reinforcement-change inbox notices

## Status and scope

This handoff covers only accepted pending reinforcement-time changes from `UpdateReinforceTimesFromClient`. It uses the existing notification mailbox writer and reader. No new notification store, schema, migration, or reader was added. `StructureStore.cs` and `StructureTimers.cs` were not edited.

## V24.01 wire contract

- `eve-logic/eve/common/script/util/notificationconst.py:194` defines `notificationTypeStructuresReinforcementChanged = 209`; `groupStructures = 7` is at line 650 and the notification alias is at line 1030.
- `eve-logic/notifications/common/formatting/notificationFormatMapping.py:101` maps that type to `StructuresReinforcementChanged`.
- `eve-logic/notifications/common/formatters/structures.py:687-735` requires exactly `numStructures`, `allStructureInfo`, `weekday`, `hour`, and `timestamp`. Each `allStructureInfo` entry is `[itemID, name, typeID]`. The formatter reads `timestamp` as EVE/Windows FILETIME and sorts the rendered structure links by name.
- The local SDE pin is build `3396210` in `sde/lock.json`; its lock metadata records the EVE Ref mirror as the source. The type and group constants come from the matching V24.01 client logic, while each structure's `TypeId` and `Name` come from its authoritative `StructureRecord`. CCP's [Static Data documentation](https://developers.eveonline.com/docs/services/static-data/) describes the SDE as static game data. The connected local `client_sde` schema contains `mission_messages`, not notification-type constants.

EveJS v0.12.9 confirms the behavior in `server/src/services/structure/structureVulnerabilityService.js:117-176,183-261`: one summary notice per accepted batch, sender = owning corporation, recipient = every character in that corporation, type 209, group 7, unprocessed, with the five formatter fields above. Its tuple uses the structure's real item ID, name, and type ID.

## eve.rs transaction and delivery

`StructureDeploymentAuthority.UpdateReinforceTimesAsync` first validates every requested structure and role, then applies each `ReinforceTimeRequested` in the same PostgreSQL transaction. For each transition with `Changed == true` and `Next.PendingEffectiveMs > 0`, it collects the authorized record's corporation, name, and type ID. It writes one `structure.reinforcement_changed` outbox event per corporation before committing the state transaction. The event's single `ApplyAtMs` is taken from the first changed `transition.Next.PendingEffectiveMs`, because the V24 batch formatter has one `timestamp` field; the requested weekday and hour are shared by the batch.

The pending effective time is authoritative: `Eve.Structures.StructureStateMachine.OnReinforceTimeRequested` sets `PendingWeekday`, `PendingHour`, and `PendingEffectiveMs` to `now + StructureTimes.ReinforceChangeDelayMs` (`dotnet/src/Eve.Structures/StructureStateMachine.cs:236-251`). `StructureTimers.ApplyAsync` saves the transition, publishes its existing state event, schedules from `transition.Next`, and returns that transition in the caller's transaction (`dotnet/src/Eve.Structures/StructureTimers.cs:66-86`). No time or type ID is guessed in the notice path.

An unchanged repeated request has `Changed == false`; a request that clears a pending change has `Next.PendingEffectiveMs == 0`; neither emits the notice event. Authorization or structure lookup rejection occurs before any transition is applied. A transition refusal or cancellation before commit rolls back both the state rows and the notice outbox row. Therefore there is no notice row or client callback before the state transaction commits.

`ReinforcementChangedNotifications` subscribes to the new topic, queries actual members with `PgCharacterStore.ForCorporationAsync`, builds the V24 payload, and calls the existing `PersistenceTransactionMailbox.WriteNotificationsAsync` with `EventContext`. The mailbox writes each recipient's `notifications/boxes/{characterID}` entry in the subscriber transaction and registers cache/live `OnNotificationReceived` delivery after commit; its idempotency key includes the stable outbox event ID. `NotificationMgrService.ReadAsync` continues to read the same persisted `notifications` box (`dotnet/src/Eve.Host/Persistence/Mail/PersistenceTransactionMailbox.cs:40-161`, `dotnet/src/Eve.Services.Core/NotificationMgrService.cs:50-75`).

The historical open item in `docs/project/structures-phase/handoff-ST-2.md:28` proposed a separate `Eve.Notifications` store. That statement is stale and superseded by this handoff and the current `PersistenceTransactionMailbox`.

## Changes and tests

- `dotnet/src/Eve.Host/Persistence/Structures/StructureDeploymentAuthority.cs`: publish only accepted pending-change events in the state transaction; constructor now takes `Eve.Events.EventBus`.
- `dotnet/src/Eve.Host/Persistence/Structures/ReinforcementChangedNotifications.cs`: event contract and mailbox subscriber.
- `dotnet/src/Eve.Host/Composition/MailComposition.cs`: parent registration uses the mailbox created after `mailSendWriter.MutationGate` initialization.
- `dotnet/tests/Eve.Host.Tests/StructureCompositionTests.cs`: `Reinforcement_change_notice_is_durable_formatted_and_scoped_to_the_owner_corporation` covers authorization/unknown-structure rejection, batch payload and recipients, no duplicate on repeated request, no notice on clearing the pending time, and persistence across host restart.

Parent wiring is already present in `StructureComposition.cs`:

```csharp
new StructureDeploymentAuthority(source, structureStore, structureTimers, foundations.Scheduler, foundations.Events)
```

A CBM search found no direct `new StructureDeploymentAuthority` test callers.

## Verification boundary

Serena diagnostics returned no C# diagnostics for the changed authority, adapter, mail composition, or test file. A fresh source inventory found 11 `[PgFact]` methods in `StructureCompositionTests.cs`, including `Reinforcement_change_notice_is_durable_formatted_and_scoped_to_the_owner_corporation`.

Parent-reported baseline: Host production build 0 warnings / 0 errors; `StructureCompositionTests` 11 PASS / 0 FAIL / 0 SKIP in 4m32s, with no database lock. The parent flagged a possible timing mismatch between that baseline and the latest test source, so the baseline is not treated here as confirmation that the added method was exercised; rerun the focused suite after this handoff. This worker did not compile or run .NET tests.

Only the required initial read-only `git status --short --branch` was used to preserve existing changes; no Git mutations were run.
