# Industry job notification check

Scope: the client-visible change notification for installed, completed, bulk-completed, cancelled, and ready jobs.

## Whole-client contract

The mutation callers are eve/client/script/industry/industrySvc.py:139-161. The client receives OnIndustryJob(jobID, ownerID, blueprintID, installerID, status, successfulRuns) at industrySvc.py:291-301: it updates cached status/run count, refreshes installer slot counts, invalidates stale owner job caches, and scatters OnIndustryJobClient. The other direct consumer, blueprintSvc.py:37-40, updates the owner's blueprint cache. UI listeners consume the scattered event in ui/shared/industry/industryWnd.py:439-449, ui/shared/industry/browserBlueprints.py:169-181, and ui/shared/industry/browserJobs.py:111-116.

EveJS v0.12.9 builds the same six positional fields and calls sendNotification("OnIndustryJob", ...) in /home/user/evejs-v0.12.9/server/src/services/industry/industryNotifications.js:24-39.

## eve.rs path

PersistenceIndustryJobOperations inserts industry.job.changed with the job mutation transaction for install, cancel, and complete (dotnet/src/Eve.Host/Persistence/Industry/PersistenceIndustryJobOperations.Part2.cs:321-323,611-614,727-729). The Host event subscriber is registered in dotnet/src/Eve.Host/Composition/IndustryComposition.cs:628-638; it delegates to IndustryJobClientNotifications, which sends the six fields to the online installer and to online corporation members with Factory Manager, Rent Factory Slot, or Rent Research Slot roles (dotnet/src/Eve.Host/Composition/IndustryJobClientNotifications.cs:8-26). The ready-job scheduler publishes the same event (dotnet/src/Eve.Host/Persistence/Industry/IndustryReadyJobs.cs:89).

IndustryJobNotificationWireTests.cs decodes the actual Macho notification packet and asserts its name, charid destination, six positional values, None successful-runs value, recipient selection, and exclusion of a corporation member without a view role.

## Verification

The focused notification wire test passed (1 passed, 0 failed, 0 skipped). Eve.Host.Tests was also run with EVE_PG_URL unset as required for this task: 605 passed, 100 failed, and 228 skipped. Failures include database-backed fixtures which require the assigned PostgreSQL lane; this shard is not green. No database or graphical client was used.
