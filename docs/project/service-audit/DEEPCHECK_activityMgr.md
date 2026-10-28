# Deep check: `activityMgr`

Date: 2026-10-28

## Client discovery and route

The exact service exists. Whole-tree search of `/home/user/eve-logic` found one remote-service lookup at `eve/client/script/ui/services/activitiesSvc.py:36-39`: `sm.RemoteSvc('activityMgr')`. No bound-object or moniker variant exists. Its four direct RPC callers are:

- `GetActivityBadgeStatuses()` — `eve/client/script/ui/services/activitiesSvc.py:46`
- `ActivitySeen(activityID)` — `eve/client/script/ui/services/activitiesSvc.py:147`
- `GetActivitiesPopState()` — `eve/client/script/ui/services/activitiesSvc.py:175`
- `SetActivitiesPopState(state)` — `eve/client/script/ui/services/activitiesSvc.py:179`

All method arguments are positional; the getters pass none. The activity ID comes from `ReleasedActivity.GetID()` (`eve/client/script/ui/shared/activities/activity.py:29-30`) after selection (`activityEntry.py:192`, `activitiesSvc.py:156-169`). The popup checkbox passes `checkbox.checked` (`window.py:63-68`), a Python boolean. GPCS adds `machoVersion` to kwargs, default 1 (`carbon/common/script/net/ServiceCallGPCS.py:220-234)); this is transport metadata, not a method argument. No client call supplies a keyword method argument.

Host appends the character-scoped service with `CharacterSettingsActivityMgrStateStore` at `dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:109-112`. The earlier core registration is replaced by this later same-name registration, as specified by `ServiceRegistry.Register` at `dotnet/src/Eve.Services/ServiceRegistry.cs:18-21`.

## Method-by-method results

### `GetActivityBadgeStatuses()`

The client calls it during `LoadActivities` (`activitiesSvc.py:61-76`), converts the iterable to a set (`activitiesSvc.py:45-47`), and uses membership to count unseen items (`activitiesSvc.py:51-59`). Host returns a sorted `PyList` of IDs (`PyInt` through 32-bit range, otherwise `PyLong`), reads persisted state, then sends `OnSystemActivityUpdated` before the RPC reply, with destination type `None` and no arguments. The whole client has one subscriber, `eve/client/script/industry/facilitySvc.py:17,36-38`, which invalidates activity modifiers and reloads facilities. EveJS sends the same zero-argument notification with destination type `None` and returns the sorted list (`src/server/src/services/activity/activityMgrService.js:86-93,100-109`).

There is no method-specific `UserError`. Persistence exceptions propagate; missing state raises `InvalidOperationException("The activity state is unavailable for this character.")`; cancellation raises `OperationCanceledException`.

### `ActivitySeen(activityID)`

The client passes the selected ID positionally. Host accepts `PyInt` and `PyLong`; explicit `None` and nonpositive IDs return `PyBool.False`. EveJS normalizes the first argument, returns false for nonpositive IDs, otherwise records it and returns true (`src/server/src/services/activity/activityMgrService.js:50-53,111-126`). Host returns `PyBool.True` only after the character-settings update succeeds. The client adds the ID to its local set only on a truthy result (`activitiesSvc.py:147-152`). This method sends no server notification; the client emits local `OnActivityUpdated` after success (`activitiesSvc.py:149-152`).

No method-specific `UserError` is defined. Invalid IDs return `PyBool.False`; cancellation and persistence exceptions propagate.

### `GetActivitiesPopState()`

The reply supplies the checkbox's `checked` value (`activitiesSvc.py:174-175`, `eve/client/script/ui/shared/activities/window.py:63-64`). EveJS defaults to 1 and returns normalized 0/1 (`src/server/src/services/activity/activityMgrService.js:128-135`). Host returns `PyInt(1)` by default or persisted `PyInt(0|1)`. No notification is sent. Missing-state and cancellation errors match the first getter.

### `SetActivitiesPopState(state)`

The client sends the checkbox's Python boolean positionally and reverts the checkbox if the result is false (`window.py:67-68`, `activitiesSvc.py:177-183`). Host normalizes `PyBool`, `PyInt`, `PyLong`, and `None`: zero becomes 0; nonzero booleans/integers become 1; `None` or a missing argument uses default 1. Host persists before returning `PyBool.True`; an unavailable character store returns `PyBool.False`. EveJS uses the same 0/1 normalization and returns true (`src/server/src/services/activity/activityMgrService.js:55-57,137-145`). There is no server notification or method-specific `UserError`; cancellation and persistence exceptions propagate.

## State and EveJS comparison

Host stores seen IDs and popup state under namespaced keys in `CharMgrPersonalData.CharacterSettings` (`dotnet/src/Eve.Host/CharacterSettingsActivityMgrStateStore.cs:6-53`). `PersistenceCharMgrReadModel` reads that field from the character GameStore row (`dotnet/src/Eve.Host/Persistence/CharMgr/PersistenceCharMgrReadModel.cs:123`) and writes changes to `/{characterID}/characterSettings` (`PersistenceCharMgrReadModel.cs:309-320`). Its `UpdateAsync` serializes read-modify-write updates with an instance semaphore (`PersistenceCharMgrReadModel.cs:138-148`); other character settings are preserved. State survives service recreation.

EveJS 261018 holds the equivalent state in a process-local `Map` keyed by character ID (`src/server/src/services/activity/activityMgrService.js:13-14,55-76`). Durable Host storage is an intentional internal difference; method names, value types, replies, and notification order match the client-visible contract.

## Checks

- `ActivityMgrArgumentHostRpcTests` (`dotnet/tests/Eve.Host.Tests/ActivityMgrArgumentHostRpcTests.cs:10-49`) covers packet-dispatch wire args, `machoVersion`, `PyLong`, `PyBool`, `None`, reply values, and notification-before-reply. `ActivityMgrHostRpcTests.cs:50-55` also checks destination type and order; `ActivityMgrServiceTests.cs:23` asserts empty notification args.
- `CharacterSettingsActivityMgrStateStoreTests` verifies restart persistence and duplicate retry idempotence (`dotnet/tests/Eve.Host.Tests/CharacterSettingsActivityMgrStateStoreTests.cs:10-39`), 32 concurrent seen updates (`CharacterSettingsActivityMgrStateStoreTests.cs:42-76`), and missing-store behavior (`CharacterSettingsActivityMgrStateStoreTests.cs:79-94`). The concurrent case exercises the serialized update contract at `PersistenceCharMgrReadModel.cs:138-148`.
- `ActivityMgrComposedHostWireTests` (`dotnet/tests/Eve.Host.Tests/ActivityMgrComposedHostWireTests.cs:12-60`) covers the composed Host TCP path. The existing combined Host wire test is at `ActivityAlphaBillRpcHostWireTests.cs:16-87`.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- Focused `Eve.Host.Tests` filter for `ActivityMgrArgumentHostRpcTests` and `CharacterSettingsActivityMgrStateStoreTests`: 4 passed, 0 failed, 0 skipped.
- Focused `Eve.Services.Core.Tests` activity tests: 2 passed, 0 skipped.
- The composed TCP fixture and full `Eve.Host.Tests` shard were excluded because they require PostgreSQL, which this job forbids. No graphical V24.01 client session was used.
