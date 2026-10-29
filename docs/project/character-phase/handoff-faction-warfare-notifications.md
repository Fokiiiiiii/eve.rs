# Faction warfare notification and enlistment audit handoff

## Client evidence

- Crimewatch: `eve/client/script/ui/services/crimewatchSvc.py:283-287` sets `myEngagements[otherCharId]` to `crimewatchEngagementTimeoutOngoing` and scatters `OnCrimewatchEngagementUpdated`. Whole-client consumers are timers (`eve/client/script/ui/crimewatch/crimewatchTimers.py:425-439`), hints (`crimewatchHints.py:228,336`), user entry (`eve/client/script/ui/shared/userentry.py:612`), target bar (`eve/client/script/ui/inflight/bracketsAndTargets/targetInBar.py:308`), tactical (`eve/client/script/parklife/tactical.py:329`), XMPP row (`xmppchatuserentry2.py:370`) and chat row (`chat/client/member_list_entry.py:378`). Current EveJS 261028.1 has no StopTimeout sender. Host persists the five-minute expiry and currently has no verified transition that means StopTimeout; see `docs/reference/compat/DEVIATIONS.md`.
- System LP: `eve/client/script/ui/services/facWarSvc.py:51-55` stores new points for `session.solarsystemid2` and refreshes the faction warfare panel. `eve/client/script/ui/shared/factionalWarfare/infrastructureHub.py:202-211` also updates its pool amount and donation bounds.
- Enlistment: `eve/client/script/ui/services/fwEnlistmentSvc.py:50-56` replaces faction/direct/corporation faction caches, resets cooldown and scatters a local event. `eve/client/script/ui/shared/factionalWarfare/enlistmentFlow/enlistCont.py:137-151,267-269` calls the RPC, waits for session faction state, then opens the dashboard. Other consumers are `factionwarfare/client/enlistmentChecks.py:31,48`, `jobboard/client/features/factional_warfare_enlistment/provider.py:19-21`, `jobboard/client/features/dungeons/factional_warfare/provider.py:19-36`, `jobboard/client/features/dungeons/pirate_insurgencies/provider.py:18-35`, and `jobboard/client/ui/adverts.py:135`.
- Character join: `eve/client/script/ui/services/facWarSvc.py:179-197` asks the player to leave their corporation before calling `JoinFactionAsCharacter`. Direct enlistment and corporation policy calls are in `fwEnlistmentSvc.py:85-108`.
- EVE University states unmodified faction standing >= 0.0 is required to enlist and direct-enlisted members must be allowed by their corporation and meet standing requirements ([Faction warfare](https://wiki.eveuniversity.org/Faction_warfare), [Faction warfare standings](https://wiki.eveuniversity.org/Faction_warfare_standings)).

## Implementation

- Direct enlistment validates an occupier faction, corporate allowed-faction policy, and unmodified standing >= 0 before persistence. The persisted corporation store already checks the Director role when setting allowed enlistment factions (`dotnet/src/Eve.Corporations/CorporationStore.FactionWarfare.cs:5-20`); no duplicate role check was added at the RPC layer.
- Character join now moves the character to the faction's militia NPC corporation, clears alliance session state, persists faction/cooldown, changes the session, and queues `OnEnlistmentStateUpdated(factionID, directFactionID, corpFactionID)` after the RPC response.
- Direct enlist and leave queue that same notification after persistence/session changes. LP pool updates are emitted after transaction commit and sent to same-system sessions; the donor receives its notification after its own response, other clients immediately.
- No database was used for this task.

## Verification

- Host project build passed: `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -p:UseSharedCompilation=false -m:2`.
- Core focused test passed: `Direct_enlistment_requires_an_occupier_faction_and_zero_unmodified_standing`.
- Host test build passed. All three focused Host wire tests passed: LP pool, direct enlistment and character enlistment (3 passed, 0 skipped).
- The full Host test project was also attempted without a database; it reported 617 passed, 233 skipped and 101 failed. Reported failures included `PgConnection.ToConnectionString` dereferencing the absent test connection string. No database was started or used. Re-run only the proper non-database Host shard selection before push.
- After merging `origin/main` at `3fe735a4`, `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -p:UseSharedCompilation=false -m:2` passed, the Core focused test passed (1/1), and all three focused Host wire tests passed (3/3, 0 skipped).
- At the pre-push gate check, newest completed main CI was green at `896c5946`; main CI for `3fe735a4` was still in progress.
