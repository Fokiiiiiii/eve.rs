# ST-9b handoff: vulnerability window, reinforcement and owner notices

Worktree `/home/user/wt-st9b` (branch `wt-st9b`, from origin/main at `1a1d4aa`). Database `eve_p2` through `tools/dev/db-lock.sh`. Continues `handoff-ST-9.md`.

## Done
- **Vulnerability window (ST-9 e).** `EntosisWindow` (`dotnet/src/Eve.Services.Space/Entosis/EntosisCapture.cs`): 18 h / ADM (1 to 6), centred on the owner's prime hour, one-hour minimum. `EntosisRules.IsVulnerable`: a neutral hub has no window; a partly captured hub stays vulnerable. `EntosisCaptureService.SettleCycleAsync` refuses `SOVEREIGNTY_TARGET_NOT_VULNERABLE` before it stores anything. The reading now carries the prime hour (`StructureEntosisComposition`, `AlliancePrimeTimeAsync`).
- **Owned capture completes into reinforcement.** Phase `reinforced`, `handover = owned`, exit = not before 48 h, at a point of the first window that ends after that (`EntosisWindow.ReinforcementExit`, deterministic draw). Refusals after it: `SOVEREIGNTY_HUB_ALREADY_REINFORCED`. Migration `0002_entosis_reinforcement.sql` (prime hour, exit, the phase).
- **Owner notices.** Events `space.entosis.capture_started` (on the first attacker cycle of an idle owned hub) and `space.entosis.hub_reinforced`, published in the capture's transaction. `Eve.Host/Persistence/Sovereignty/EntosisOwnerNotifications.cs` writes V24.01 type 147 and 160 to the defending alliance's characters through the mailbox, idempotent per event id; wired in `MailComposition`, the event bus in `StructureComposition`.
- **Bug fixed (ST-9 d).** After a handover answer the hub returns to `idle` and its links are removed; before, a captured neutral hub refused every later cycle.

## Evidence (all green, this session)
- Baseline before the change: `dotnet test` filter `Entosis|SpaceSovHubs` in Space: 25 passed.
- After: Space `Entosis|SpaceSovHubs`: 30 passed, 0 skipped. Host `EntosisOwnerNotification|SovHub|Entosis|Sov`: 4 passed. Host `StructureCompositionTests|MoonOwnerNotificationTests|StructureAnchor`: 29 passed, 0 skipped.
- Full suites before the push: Space 887 passed, 0 failed, 0 skipped (20 m 36 s); Host 352 passed, 0 failed, 0 skipped (8 m 39 s).

## Open (TODO section 50)
- The wiki's midpoint lock of a day's window length; custom windows and inheritance.
- Command nodes (the next stage after reinforcement) and the exit's consumer.
- A claim notice for a neutral hub's new owner (V24.01 43/44): not written.
- Client view of notices 147/160 and of the window (MITM open).

## Gotchas
- Serena reads the main checkout only; C# was read and edited with python3 under `/home/user/wt-st9b`.
- Build once with `flock -o -w 1800 /tmp/eve-heavy.lock dotnet build ... -p:UseSharedCompilation=false`, then `dotnet test --no-build --filter`.
