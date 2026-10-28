# Deep check: `activityMgr`

Date: 2026-10-28

## Client discovery and route

The exact service exists. Whole-tree search of `/home/user/eve-logic` found one remote-service lookup at `eve/client/script/ui/services/activitiesSvc.py:36-39`: `sm.RemoteSvc('activityMgr')`. There is no bound-object or moniker variant. Its four direct RPC callers are:

- `GetActivityBadgeStatuses()` — `activitiesSvc.py:46`
- `ActivitySeen(activityID)` — `activitiesSvc.py:147`
- `GetActivitiesPopState()` — `activitiesSvc.py:175`
- `SetActivitiesPopState(state)` — `activitiesSvc.py:179`

All method arguments are positional; the getters pass none. The activity ID comes from `ReleasedActivity.GetID()` (`eve/client/script/ui/shared/activities/activity.py:29-30`) after selection (`activityEntry.py:192`, `activitiesSvc.py:156-169`). The popup checkbox passes `checkbox.checked` (`window.py:63-68`), a Python boolean. GPCS adds `machoVersion` to kwargs, default 1 (`carbon/common/script/net/ServiceCallGPCS.py:220-234)); that is transport metadata, not a method argument. The client has no keyword method-argument call.

Host appends the character-scoped service with `CharacterSettingsActivityMgrStateStore` at `dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:109-112`. The earlier core registration is replaced by this later same-name registration, as specified by `ServiceRegistry.Register` at `dotnet/src/Eve.Services/ServiceRegistry.cs:18-21`.

## Method-by-method results

### `GetActivityBadgeStatuses()`

The client calls it during `LoadActivities` (`activitiesSvc.py:61-76`), converts the iterable to a set (`activitiesSvc.py:45-47`), and uses membership to count unseen items (`activitiesSvc.py:51-59`). Host returns a sorted `PyList` of IDs (`PyInt` through 32-bit range, otherwise `PyLong`), reads persisted state, then sends `OnSystemActivityUpdated` before the RPC reply, with destination type `None` and no arguments. The whole client has one subscriber, `facilitySvc.py:17,36-38`, which invalidates activity modifiers and reloads facilities. EveJS sends the same zero-argument `None)-destination notification and returns the sorted list (`activityMgrService.js:86-93,100-109`).

There is no method-specific `UserError`. Persistence exceptions propagate; missing state raises `InvalidOperationException("The activity state is unavailable for this character.")`; cancellation raises `OperationCanceledException`.

### `ActivitySeen(activityID)`

The client passes the selected ID positionally. Host accepts `PyInt` and `PyLong`; explicit `None` and nonpositive IDs return `PyBool.False`. EveJS normalizes the first argument, returns false for nonpositive IDs, otherwise records it and returns true (`activityMgrService.js:50-53,111-126`). Host returns `PyBool.True` only after the character-settings update succeeds. The client adds the ID to its local set only on a truthy result (`activitiesSvc.py:147-152`). This method sends no server notification; the client emits local `OnActivityUpdated` after success (`activitiesSvc.py:149-152`).

No method-specific `UserError` is defined. Invalid IDs return `PyBool.False`; cancellation and persistence exceptions propagate.

### `GetActivitiesPopState()`

The reply supplies the checkbox's `checked` value (`activitiesSvc.py:174-175`, `eve/client/script/ui/shared/activities/window.py:63-64`). EveJS defaults to 1 and returns normalized 0/1 (`activityMgrService.js:128-135`). Host returns `PyInt(1)` by default or persisted `PyInt(0|1)`. No notification is sent. Missing-state and cancellation errors match the getter above.

### `SetActivitiesPopState(state)`

The client sends the checkbox's Python boolean positionally and reverts the checkbox if the result is false (`window.py:67-68`, `activitiesSvc.py:177-183`). Host normalizes `PyBool`, `PyInt`, `PyLong`, and `None`: zero becomes 0; other booleans/integers become 1; `None` or a missing argument uses default 1. Host persists before returning `PyBool.True`; an unavailable character store returns `PyBool.False`. EveJS uses the same 0/1 normalization and returns true (`activityMgrService.js:55-57,137-145`). There is no server notification or method-specific `UserError`; cancellation and persistence exceptions propagate.

## State and EveJS comparison

Host stores seen IDs and popup state under namespaced keys in `CharMgrPersonalData.CharacterSettings` (`dotnet/src/Eve.Host/CharacterSettingsActivityMgrStateStore.cs:6-53`). `PersistenceCharMgrReadModel` reads that field from the character GameStore row (`dotnet/src/Eve.Host/Persistence/CharMgr/PersistenceCharMgrReadModel.cs:123`) and writes changes to `/{characterID}/characterSettings` (`PersistenceCharMgrReadModel.cs:309-320`). This reuses the character-data owner and survives service recreation.

EveJS 261018 holds the equivalent state in a process-local `Map` keyed by character ID (`server/src/services/activity/activityMgrService.js:13-14,55-76`). Durable Host storage is an intentional internal difference; method names, value types, replies, and notification order match the client-visible contract.

## Checks

- `ActivityMgrArgumentHostRpcTests` (`dotnet/tests/Eve.Host.Tests/ActivityMgrArgumentHostRpcTests.cs:10-49`) covers packet-dispatch wire args, `machoVersion`, `PyLong`, `PyBool`, `None`, reply values, and notification-before-reply. `ActivityMgrHostRpcTests.cs:50-55` also checks destination type and order; `ActivityMgrServiceTests.cs:23` asserts the empty notification args.
- `CharacterSettingsActivityMgrStateStoreTests` (`dotnet/tests/Eve.Host.Tests/CharacterSettingsActivityMgrStateStoreTests.cs:10-58`) verifies state across service recreation plus missing-store failure behavior.
- `ActivityMgrComposedHostWireTests` (`dotnet/tests/Eve.Host.Tests/ActivityMgrComposedHostWireTests.cs:12-60`) covers the composed Host TCP path. The existing combined Host wire test is at `ActivityAlphaBillRpcHostWireTests.cs:16-87`.
- Host.Tests build: passed with zero warnings/errors. Focused Host activity tests: 3 passed, 0 skipped. Focused Services.Core activity tests: 2 passed, 0 skipped.
- The composed TCP fixture and full Host.Tests shard were excluded because they require PostgreSQL and this job forbids database use. No graphical V24.01 client session was used.
