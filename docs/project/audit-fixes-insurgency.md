# Insurgency audit findings and fixes

Audit report: `/home/user/reports/90-audit-eve-insurgency-r14.md`.

## Result

All three reported findings were confirmed against the current code and the V24.01 client contract. None was skipped as false. Regression tests were added and observed failing before the fixes. The inspected EveJS checkout contains the client-facing `insurgencySolarsystem` RPC surface, but no campaign progression implementation to validate these rules.

## Confirmed findings

### P1: Campaign coverage was based only on pirate ambition

Before the fix, `InsurgencyDirector.StartAsync` selected coverage using only the pirate ambition, while `InsurgencyCampaigns.StartAsync` stored separate pirate and antipirate requirements. `InsurgencyRules.RequiredSystems` maps a modifier to five plus that modifier. Each side scores at most once per covered system: `InsurgencyStore.TryScorePointAsync` inserts into a table whose primary key is `(campaign_id, solar_system_id, side)` (`Migrations/0002_insurgency_points.sql:4-10`).

The V24.01 client keeps the two requirements separately (`/home/user/eve-logic/pirateinsurgency/campaignClientSnapshot.py:12-23`), displays each side against its own required count (`/home/user/eve-logic/pirateinsurgency/client/dashboard/sidepanels/progress.py:36-48`, `/home/user/eve-logic/eve/client/script/ui/shared/theatersOfWar/landingPage/insurgency.py:54-61`), and uses them to render distinct ambition modifiers. With ambition `(0, 1)`, the old director created five covered systems for an Empire requirement of six, making the FOB exposure threshold unreachable under the one-point-per-system rule.

The director now covers `max(pirateRequired, antipirateRequired)` systems (`dotnet/src/Eve.Services.Space/Insurgency/InsurgencyDirector.cs:107-117`). The regression test drives the real director, completes all six Empire sites in a `(0, 1)` warzone, and verifies that the FOB becomes shield-vulnerable (`dotnet/tests/Eve.Services.Space.Tests/InsurgencyDirectorTests.cs:47-70`).

### P2: A late FOB event could bypass the campaign maximum deadline

`CampaignMachine.Advance` makes an active campaign a `NoWinner` at `MaxEndsMs` (`dotnet/src/Eve.Insurgency/InsurgencyCampaign.cs:64-70`). The previous `FobDestroyedAsync` passed the persisted campaign straight to `CampaignMachine.FobDestroyed`, so a destruction event handled after the maximum deadline but before the delayed deadline job could award an Empire win. The client recognizes `NoWinner` as a distinct terminal result (`/home/user/eve-logic/pirateinsurgency/const.py:6-13`, `/home/user/eve-logic/eve/client/script/ui/shared/theatersOfWar/landingPage/insurgency.py:31-45`).

`FobDestroyedAsync` now advances the campaign under its row lock first and applies the FOB win only if the resulting state is still `Active`; it persists and announces any deadline transition and reschedules the campaign in the same transaction (`dotnet/src/Eve.Insurgency/InsurgencyCampaigns.cs:129-146`). PostgreSQL regression tests hold the deadline job back, submit the FOB event after the maximum, and race that event against the scheduler; they verify `NoWinner`, the original end time, the wind-down job and unchanged ambition (`dotnet/tests/Eve.Insurgency.Tests/InsurgencyCampaignTests.cs:130-180`). The existing before-deadline FOB test still verifies a valid Empire win.

### P2: Delayed deadline notifications carried the final snapshot for every transition

`CampaignMachine.Advance` can return several ordered transitions after a delayed job. Before the fix, `ApplyTransitionsAsync` published each transition with the same final `Campaign`. The Host sends the event snapshot as `OnInsurgencyCampaignUpdatedForLocation` (`dotnet/src/Eve.Host/Composition/InsurgencyComposition.cs:88-100`), and the V24.01 client immediately stores that snapshot as its current local campaign (`/home/user/eve-logic/eve/client/script/ui/services/insurgencyCampaignSvc.py:70-78`). A delayed `Forecasting → Active → NoWinner` therefore delivered two `NoWinner` snapshots, contradicting the first notification's `Active` transition.

`MachineStep` now includes the campaign snapshot after each transition; the campaign service publishes the matching snapshot and rejects a transition/snapshot count mismatch (`dotnet/src/Eve.Insurgency/InsurgencyCampaign.cs:26-31,56-79`, `dotnet/src/Eve.Insurgency/InsurgencyCampaigns.cs:176-207`). A Host-composition PostgreSQL test exercises the registered event subscriber, decodes the Macho notification packets, checks the wire snapshots are `Active` then `NoWinner`, and reads the persisted final `NoWinner` state through `GetAllVisibleCampaigns` (`dotnet/tests/Eve.Host.Tests/InsurgencyCompositionTests.cs:170-239`).

## EveJS comparison and limits

In `/home/user/evejs-261028.1/server/src/services/map/insurgencySolarsystemService.js:17-70,112-133`, `GetAllVisibleCampaigns` returns two hard-coded snapshots and `GetLocalCampaignClientSnapshot` returns the no-campaign pair. A repository-wide search of `server/src` found no second insurgency server source implementing ambition updates, scoring, deadlines, FOB destruction, or transition notifications. One fixed example reports 20 pirate points required for 18 covered systems (`:24-45`), so its static snapshot data is not a reliable progression rule. The EveJS dynamic campaign flow could not be executed because that flow is absent from this checkout; no EveJS behavior was copied for these fixes.

The client-source search covered all references in `/home/user/eve-logic` to both point requirements, campaign update notifications, and `CAMPAIGN_STATE_NO_WINNER`; the listed files are the client callsites and consumers found. The client supplies the wire and presentation contract, while the current server state machine and the database regression tests establish deadline precedence and point capacity.

## Verification

The new tests were run against PostgreSQL database `eve_p5`. Before fixes they failed with: expected six covered systems but got five; late FOB destruction returned `true`; and Host notification snapshots were `[NoWinner, NoWinner]` instead of `[Active, NoWinner]`.

After fixes:

- `Eve.Insurgency.Tests`: 17 passed, 0 skipped.
- `Eve.Services.Space.Tests.InsurgencyDirectorTests`: 5 passed, 0 skipped.
- `Eve.Host.Tests.InsurgencyCompositionTests`: 2 passed, 0 skipped.
- Release builds of `Eve.Insurgency`, `Eve.Services.Space`, and `Eve.Host`: 0 warnings, 0 errors each.

Graphical-client behavior was not checked. The host-level test verifies Macho notification wire payloads and the next snapshot read, but does not replace a live V24.01 client walk.

The repository instructions request Codebase Memory MCP/Serena for source access, but neither MCP tools nor the `codebase-memory-mcp` CLI were available in this session. Source reading/searching used `rg` and `nl` as the fallback.
