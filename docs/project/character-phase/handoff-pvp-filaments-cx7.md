# PvP Filament Catalog and Queue Admission — CX7

## Scope and source data

This change implements the V24.01 PvP Filament catalog and admission behavior only. It does not add matchmaking, match creation, results, or history.

The event catalog is bundled from the matching client build 3396210 static resources:

- `res:/staticdata/pvpfilamentmatchtypes.fsdbinary`, SHA-256 `45573a326f325019629ef396224ca8db0f5b236c3665131fe8a80efbd063799a`
- `res:/staticdata/pvpfilamenteventdates.fsdbinary`, SHA-256 `23a52fb2542bb80db0ea0d8ea37fd8b194f7311890d78e928ba4434b25adb449`

The resources contain 86 match types and 118 schedules. Following EveJS `pvpEventCatalog.js:76-92`, the exposed all-events catalog includes the 88 CCP schedules; active, recent, and next are calculated from their dated schedule rows, QA flag, and the 11:00 UTC schedule boundary. In this pinned client data the latest CCP schedule ended on 2023-01-16, so the current active and next collections are empty while recent returns event `129_171`. This is the sourced data state, not a default empty catalog.

EVE University describes Proving Grounds filaments as time-limited PvP challenges with ship and equipment restrictions ([Filament](https://wiki.eveuniversity.org/Filament), [Triglavian Collective](https://wiki.eveuniversity.org/Triglavian_Collective)). The catalog carries the supplied schedule restrictions and the admission path enforces them from Host state and SDE type lists.

## Client and EveJS evidence

A whole-source V24.01 search found the catalog and queue service consumed in these client files:

- `eve-logic/pvpFilaments/client/pvpFilamentSvc.py:28-46,49-80,92-119`: join and leave argument order, notification consumption, event dictionaries, and active lookup by filament TypeID.
- `eve-logic/pvpFilaments/client/event_window/controller.py:17-53,61-103` and `event_window/window.py:35-81,116-177`: active/QA/recent/next selection, display fields, and the UI consumers of event, statistics, and leaderboard data.
- `eve-logic/pvpFilaments/client/activation_window/clientPVPfilamentValidator.py:38-68,92-145,151-178`: session fields and local eligibility checks; `pvpFilamentController.py:194-235,299-311` consumes the join estimate, leave result, `PVPJumpError`, and `OnMatchMakingFailure`.
- `eve-logic/pvpFilaments/client/activation_window/pvpFilamentActivationWindow.py:177-194` displays the queue estimate, with values below one shown as unknown by `pvpFilamentController.py:133-136`; error codes and labels are in `pvpFilaments/error.py:9-53`.
- `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:583` also calls `AbyssalPVPEndGateActivation`; this pre-existing end-gate refusal remains registered.

A whole-source `pvpFilamentMgr` search found the RPC consumers above and the end-gate call. The only notification receivers are `pvpFilamentSvc.py:49-90` and `pvpFilamentController.py:299-311`; the event window relays statistics and leaderboard updates at `event_window/window.py:67-81`.

EveJS references: `pvpEventCatalog.js:46-96` for schedules, `pvpFilamentMgrService.js:119-170` for RPC and fallback payloads, and `pvpFilamentQueueRuntime.js` plus `filamentAdmission.js` for admission, duplicate queue rejection, leave, disconnect, and live eligibility.

## Behavior

The Host registration now passes real admission to `PvpFilamentMgrService`. Admission checks the active sourced event, cargo key identity/type/quantity/location, registered live ships, fleet role and size, SDE ship/module restrictions, module and meta-level limits, fitted ship state, range, and each participant's docked, cloaked, invulnerable, self-destructing, tethered, warping, warp-scrambled, abyssal, session-change, and PvP-timer state. Queue records are transient: duplicate joins fail with the client error, leave removes the participating fleet and emits `OnMatchMakingFailure`, and disconnect clears the queue with the disconnect error. The service rechecks the same active schedule after awaited Host validation, so a schedule that expires during validation cannot be admitted; this race-safe difference from EveJS is recorded in `docs/reference/compat/DEVIATIONS.md`.

Unlike EveJS `tickScene`, the Host port keeps a successful queue entry until explicit leave or participant disconnect because there is no per-scene PvP queue tick hook and the task does not add match timing. The entry supports real duplicate detection and cancellation; no match or outcome is emitted. This queue lifetime difference is recorded in `DEVIATIONS.md`.

Leaderboard notifications retain `entries=[]`; statistics retain rank/wins/losses/draws at zero. No match history is invented.

## Checks and limits

`Eve.Services.Space.csproj`, `Eve.Host.csproj`, and both touched test projects build with zero warnings and errors. On the dedicated `eve_p7` database, the focused `Eve.Services.Space.Tests` selection (ActivityTravel RPC, SpaceService and PlayerDeath classes) passed 190/190 with no skips, including `Fleet_store_applies_saved_character_setup_atomically`; the exact V24 Host wire/dispatch test passed 1/1 with no skips. The broad Host class-name filter matched no tests; the exact PvP dispatch test above was then run and passed. A graphical V24.01 client session was not used.

The pinned catalog has no currently active CCP schedule, so a live Host admission using a current event was not possible. Service-level schedule boundaries and successful queue transitions use real historical row `105_145` under a controlled clock; the Host wire test exercises the sourced catalog and fallback shapes, not a successful Host admission.

The latest completed main CI run is #1482 (`8c71b923d04f`), green with no failed jobs. Run #1481 for the same SHA is also green. The earlier #1480 run failed the unrelated Windows Rust/SDE job and is superseded by these green runs.
