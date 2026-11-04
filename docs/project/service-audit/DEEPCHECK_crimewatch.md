# Crimewatch Host deep check

## Scope and route

The V24.01 client gets this service as the **bound moniker** `crimewatch`: `eve/common/script/net/eveMoniker.py:134-137`. Moniker resolution and binding call `MachoResolveObject` and `MachoBindObject` through the generic path in `carbon/common/script/net/moniker.py:143-165, 235`. The Host registers `CrimewatchRpc()` in the character service list (`dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:50-54`); that creates the `[Service("crimewatch")]` object (`dotnet/src/Eve.Host/Rpc/CrimewatchRpc.cs:212-217`, `dotnet/src/Eve.Services.Crimewatch/CrimewatchService.cs:14-16`). The service returns a per-session bound object and dispatches the nested method, args and kwargs (`CrimewatchService.cs:44-78`).

A whole-tree search of `/home/user/eve-logic` found eight gameplay RPC methods called on this moniker. All eight use positional method arguments; no callsite passes method-level keyword arguments. The RPC envelope's `machoVersion` is metadata and is covered by the Host wire test; `PacketDispatcher` strips it before method dispatch (`dotnet/src/Eve.Services/PacketDispatcher.cs:314-326`). The client sends floating-point security statuses for tag exchange, IDs as Python integers, FILETIME for duel expiry, and actual booleans for duel answers. Host ID parsing accepts `PyInt`, `PyLong`, and integral `PyReal`; the tag handler accepts finite `PyInt`, `PyLong`, and `PyReal` values.

No crimewatch gameplay method calls `MachoErrors.UserError`. With `StrictRpc` disabled and no service exception mapper, a thrown exception becomes an `ERRORRESPONSE` carrying `exceptions.RuntimeError("Unhandled error {errorId} in crimewatch.<Method>")`; the Host log retains the original CLR exception type and message (`dotnet/src/Eve.Services/PacketDispatcher.cs:265-288`, `dotnet/src/Eve.Services/MachoErrors.cs:68-91`).

## Client-called gameplay methods

| RPC | Whole-client callsite | Reply consumed by the client |
| --- | --- | --- |
| `GetClientStates()` | `eve/client/script/ui/services/crimewatchSvc.py:88,95,119` | Four-tuple: five timer pairs, engagement dict, criminal/suspect sets, numeric safety level. |
| `SetSafetyLevel(level)` | `crimewatchSvc.py:342` | Client ignores the reply; it updates local state and scatters `OnSafetyLevelChanged`. |
| `RespondToDuelChallenge(fromCharID, expiryTime, accept)` | `crimewatchSvc.py:576` | `None`; the caller ignores the reply. |
| `StartDuelChallenge(charID)` | `crimewatchSvc.py:586` | `None`; the caller ignores the reply. |
| `GetMySecurityStatus()` | `crimewatchSvc.py:591` | Numeric status, cached and used in comparisons and UI. |
| `GetCharacterSecurityStatus(charID)` | `crimewatchSvc.py:595` | Numeric status, used by slim items, character information and corporation member UI. |
| `GetSecurityStatusTransactions()` | `crimewatchSvc.py:602` | Iterable history rows consumed by the security status panel. |
| `ApplyTagsForSecStatus(targetStatus, currentStatus)` | `eve/client/script/ui/station/securityOfficeWindow.py:124` | `None`; the caller refreshes the tag bar in `finally`. |

The Host also exposes `GetSafetyLevel()`, but a whole-client search found no moniker call for it. The client calls its local cache getter at `crimewatchSvc.py:336`; for example, the safety button reads that cache at `eve/client/script/ui/inflight/shipSafetyButton.py:210,226,311,469`. The service endpoint is extra compatibility surface.

## Per-method verification

### `GetClientStates`

- **Client use:** `crimewatchSvc.py:88-89` reads the fourth member as safety; `:95-110` destructures timers, engagements and flags, updates caches, and scatters `OnCombatTimersUpdated`; `:119-124` reads safety after a session transition.
- **Wire shape:** `CrimewatchService.cs:102-124` returns `((five (state, expiry) pairs), engagement dict, (criminal set, suspect set), safety)`. States and safety are `PyInt`; expiry is `None` or FILETIME `PyLong`; engagement keys are character IDs and values are FILETIME `PyLong`. The existing wire test checks the outer tuple, timer count/pair shape, empty dict, two flag sets and safety integer.
- **State/errors:** Reads the persisted safety and combat state, current system flags and runtime engagements. With no character it returns full safety, idle timers and no engagements; store/runtime failures propagate. It does not send notifications as a side effect.
- **Subsequent client consumers:** Timer notifications update the cached timer fields in `crimewatchSvc.py:221-250`; the timer UI reads them in `eve/client/script/ui/crimewatch/crimewatchTimers.py:347-407`. Related timer consumers include `voidspace/client/void_space_activation_controller.py:201`, `pvpFilaments/client/activation_window/pvpFilamentController.py:262`, `eve/client/script/ui/shared/abyss/baseActivationController.py:148`, and `randomJump/client/randomJumpController.py:192`. Flag updates are applied at `crimewatchSvc.py:251-253`; engagement updates are applied and scattered at `:261-273`, with nodegraph listeners in `nodegraph/client/events/crimewatch.py:11-32`.
- **EveJS:** Uses its `crimewatchState.buildClientStatesForSession` helper. Host uses the same client-visible four-part structure.

### `SetSafetyLevel`

- **Client use:** `crimewatchSvc.py:341-345` sends one positional level, then stores that level locally and scatters `OnSafetyLevelChanged`; it does not inspect the RPC return. Local listeners are `eve/client/script/ui/inflight/shipSafetyButton.py:265-266`, `voidspace/client/void_space_activation_controller.py:218-219`, `pvpFilaments/client/activation_window/pvpFilamentController.py:279-280`, `eve/client/script/ui/shared/abyss/baseActivationController.py:165-166`, and `randomJump/client/randomJumpController.py:209-210`.
- **Wire/state:** Host returns the stored normalized level as `PyInt` and persists by character through the safety store (`CrimewatchService.cs:132-143`). Normalization truncates finite values and clamps to 0–2; missing, non-finite or `None` becomes full safety (`dotnet/src/Eve.Crimewatch/SafetyLevel.cs:13-14`). An unauthenticated session returns full safety without a write.
- **Errors/notifications:** No server notification is sent; the client performs its local state transition. Persistence errors propagate; no `UserError` is raised by this method.
- **EveJS:** Also persists a normalized level and returns it; its missing/invalid argument default is full safety.

### `StartDuelChallenge`

- **Client use:** `crimewatchSvc.py:582-587` sends one positional character ID unless it already has an engagement; the return is ignored.
- **Wire/state:** The Host accepts an integer/long/integral real ID, and silently returns `None` for absent, invalid, non-positive, or self IDs (`CrimewatchService.cs:157-173`). Production validation also requires both characters online and the caller's corporation/alliance session facts to match persisted affiliation. It stores a 120-second pending challenge in the shared `crimewatchRuntime` game-store table and sends `OnDuelChallenge` to the target with challenger character/corporation/alliance IDs and an expiry FILETIME (`PersistenceCrimewatchCallbacks.cs:74-125`).
- **Next client action:** `crimewatchSvc.py:561-578` opens the dialog. Accept/decline sends `RespondToDuelChallenge` with the sender ID, same expiry and a bool; the “block” choice blocks locally and does not send an RPC. The local challenge window is cleared in `finally`.
- **Errors/EveJS:** Invalid or unavailable challenges are no-ops returning `None`; storage failures propagate. No `UserError` is produced. EveJS implements neither this method nor its response method.

### `RespondToDuelChallenge`

- **Client use:** `crimewatchSvc.py:561-578` sends three positional args only for accept/decline; it never sends the dialog's `accept=None` block choice.
- **Wire/state:** Host accepts integer/long/integral-real sender and FILETIME values and applies truthiness to the third argument (`CrimewatchService.cs:175-190`). Invalid IDs/expiry are no-ops returning `None`. A valid decline removes the matching challenge with no notification. A valid, unexpired acceptance stores a five-minute limited engagement.
- **Notifications:** On accepted engagement, runtime sends `OnCrimewatchEngagementCreated(otherCharId, timeoutFileTime)` to both participants (`CrimewatchRuntime.cs:95-125,172-177`). The client updates `myEngagements`, scatters `OnCrimewatchEngagementUpdated`, and closes the matching invite at `crimewatchSvc.py:261-268`. Expiry sends `OnCrimewatchEngagementEnded`; the client removes the entry and scatters at `:270-273`.
- **Errors/EveJS:** Invalid, stale or declined challenges return `None` at the RPC boundary; persistence errors propagate. EveJS has no implementation.

### `GetMySecurityStatus`

- **Client use/caching:** `crimewatchSvc.py:589-592` calls once while the cache is empty; `:597-598` replaces it on `OnSecurityStatusUpdate`. Consumers include `clientDogmaLocation.py:749`, `characterOverviewPanel.py:152-153`, `fittingDogmaLocation.py:787`, `crimewatchSvc.py:643-644`, `securityOfficeWindow.py:90,171,338,344`, and `agentDialogueUtil.py:174-175,423-424`.
- **Wire/state:** Returns the persisted `security_status` relation as a number (`PyInt` for integral values, `PyReal` for fractional values; `CrimewatchService.cs:144-148`). Missing character or an absent test callback yields 0; production Host supplies the shared relation reader. Read/storage failures propagate.
- **Notifications/EveJS:** It is read-only. Status changes use `OnSecurityStatusUpdate`, which refreshes the cache and security office UI (`crimewatchSvc.py:597-598`, `securityOfficeWindow.py:145-146`). EveJS reads its character record's `securityStatus`/fallback `securityRating`, rather than the Host relation store.

### `GetCharacterSecurityStatus`

- **Client use:** The RPC wrapper is `crimewatchSvc.py:594-595`. Whole-client consumers are `crimewatchSvc.py:187-188` (slim item), `eve/client/script/dogma/clientDogmaLocation.py:746-749`, `eve/client/script/ui/shared/neocom/corporation/corp_dlg_edit_member.py:146`, `eve/client/script/ui/shared/info/characterInfoWindow.py:65`, and `eve/client/script/ui/shared/info/infoContentBase.py:413`.
- **Args/reply:** One positional character ID; Host accepts int/long/integral real. Missing, `None`, invalid or non-positive IDs fall back to the caller's character; missing caller status is 0 (`CrimewatchService.cs:80-87,150-152`). Reply is numeric and consumed as a status value.
- **State/errors/EveJS:** Reads the same persisted relation as the caller's own status; it is read-only and sends no notification. Read failures propagate. EveJS uses numeric conversion and falls back to the caller for falsey IDs; Host's positive-ID check additionally falls back for negative IDs, which are not sent by the client.

### `GetSecurityStatusTransactions`

- **Client use:** `crimewatchSvc.py:602` returns the moniker result; `eve/client/script/ui/shared/neocom/charsheet/securityStatusPanel.py:26-65` iterates rows and reads `eventTypeID`, `eventDate`, `otherOwnerID`, `otherTypeID`, `locationID`, `modification`, and `newValue`. Event 385 is specifically rendered as “hand in tags” at `:45-47`; the panel uses `modification` and `newValue` at `:48-65`.
- **Wire/state:** Host returns an Ex2 `carbon.common.script.sys.crowset.CRowset` with the seven named descriptor fields (`CrimewatchService.cs:26-42,192-217`). Dates are FILETIME `PyLong`; IDs are integer/long-or-`None`; modification/new value are numeric-or-`None`. It reads persisted relation history newest-first through `PersistenceCrimewatchCallbacks.cs:50-72`. Current Host writers record aggression event 379, NPC bounty recovery event 378, and tag hand-in event 385; fallback reason mapping covers those three reasons (and the legacy NPC reason), while unknown history reasons produce a `None` event ID rather than an invented one (`PgCrimewatchStore.cs:55-72`, `SecurityStatusPenalties.cs:89-94`, `NpcSecurityStatusRecoverySubscriber.cs:23-36`, `CrimewatchRpc.cs:178-188`).
- **Errors/EveJS:** No character produces an empty CRowset; read failures propagate. EveJS returns an empty list from `crimewatchService.js:116-119`, so its client loop is harmless only because it has no history rows; the Host provides persisted rows in the shape the panel dereferences.

### `ApplyTagsForSecStatus`

- **Client flow:** `securityOfficeWindow.py:90-110` gets current status, computes fee from each tag type's `securityProcessingFee` dogma attribute and disables submission when tags or ISK are insufficient. `:122-149` sends exactly `(targetSecurityStatus, currentSecurityStatus)` as two positional floats, always refreshes the tag bar in `finally`, and refreshes on tag `OnItemChange`, `OnSecurityStatusUpdate`, and cash `OnAccountChange`.
- **Mechanics sources:** Client tag bands/type IDs and 0.5 gains are `crimewatch/const.py:51-57`; required counts use `ceil` and tier-by-tier progression in `crimewatch/util.py:30-46`. The station check is CONCORD/DED plus low-security classification in `eve/client/script/ui/station/securityOfficeSvc.py:20-35`, `appConst.py:2135-2137`, and `eveuniverse/security.py:8-18,80-82`; owner IDs are in `inventorycommon/const.py:3218-3222`. Fee attribute ID is `dogma/const.py:915`.
- **Host route/args/errors:** It receives two positional finite int/long/real values; client method kwargs are unused. Missing, `None`, nonnumeric, NaN or infinity throws `ArgumentException("ApplyTagsForSecStatus requires two numeric status values.")`; without the Host callback it throws `InvalidOperationException("The Host has not configured security tag exchange.")` (`CrimewatchService.cs:89-96,219-231`). The Host callback throws `InvalidOperationException` with specific messages for unauthenticated sessions, ineligible stations, missing/invalid SDE fee data, stale status, and insufficient tags (`CrimewatchRpc.cs:103-161`). Invalid status ranges throw `ArgumentOutOfRangeException` from the shared calculation. Wallet absence/funds failures use `WalletNotFoundException` / `InsufficientFundsException`; database failures propagate. No client `UserError` is generated; in normal non-strict dispatch these exceptions become the generic `ERRORRESPONSE` described above. The caller does not catch them and always refreshes in `finally` (`securityOfficeWindow.py:122-149`).
- **Atomic state:** `CrimewatchRpc.cs:103-210` rechecks the client's current status under a wallet row lock, locks only that character's personal hangar tags at the current station, and applies the shared tier calculation. It derives fees from the loaded Dogma/SDE `securityProcessingFee` values, debits the character wallet with CONCORD as counterparty and journal ref 117, updates the security-status relation, and writes `hand_in_tags` history in the same transaction. Event 385 maps from that reason (`dotnet/src/Eve.Crimewatch/PgCrimewatchStore.cs:58-68`); history attrs include the client-visible modification and station location. The stale-status check prevents a repeated exchange from spending the same request twice.
- **Post-commit notifications:** For each changed tag stack, Host sends the standard `OnItemChange`; if the computed fee is positive it announces `OnAccountChange('cash', characterID, balance)`; then it reuses the existing security-status notification shape: optional `OnModuleAttributeChanges` followed by `OnSecurityStatusUpdate` (`CrimewatchRpc.cs:186-210`, `SecurityStatusPenalties.cs:119-130`). The client consumers are `securityOfficeWindow.py:128-149` and the cache/UI consumers cited above.
- **EveJS difference:** Neither `evejs-261018.1/src/server/src/services/security/crimewatchService.js:16-122` nor v0.12.9 implements `ApplyTagsForSecStatus`, `StartDuelChallenge`, or `RespondToDuelChallenge`. These three methods are implemented because the unmodified client calls them; their behavior follows the client mechanics and shared Host state owners.

## Bound lifecycle methods and test evidence

`MachoResolveObject` returns the configured proxy node ID (`CrimewatchService.cs:44-45`); `MachoBindObject` returns the bound-object descriptor plus nested-call result (`:58-78`). EveJS implements both lifecycle methods on its same `crimewatch` service (`evejs-261018.1/src/server/src/services/security/crimewatchService.js:16-122`). The Host wire regression exercises resolve, bind, `machoVersion` envelope kwargs, positional float args, int/long acceptance, `None` rejection and tag tier boundaries in `dotnet/tests/Eve.Host.Tests/CrimewatchHostWireTests.cs`.

**Checked:** `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` succeeded with no warnings/errors. The focused `Crimewatch_apply_tags_is_bound_and_preserves_the_client_argument_order` test passed (1 passed, 0 skipped). No PostgreSQL connection was opened.

**Not checked:** No database-backed transaction, concurrent exchange, restart/retry, full `Eve.Host.Tests` project run, or graphical V24.01 client session was performed. The local job rules prohibit using PostgreSQL; the focused wire test injects the handler and therefore verifies the actual Host dispatcher/moniker shape but not the production transaction's database effects.
