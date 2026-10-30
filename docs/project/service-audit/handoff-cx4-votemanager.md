# voteManager coverage handoff — cx4

## Result

No voteManager coverage row was changed. The 14 entries below remain `IMPLEMENTED-UNTESTED`: this task explicitly forbids PostgreSQL use, while the real `CorporationStore` operations behind these RPCs require PostgreSQL. The existing end-to-end voting tests are `[PgFact]` tests and call service methods directly; they do not exercise a registered Host request. A passing Host-wire test with persistence and notification assertions therefore could not be produced within this constraint.

The current Host route is `VoteManagerService` registered as `voteManager` in `dotnet/src/Eve.Services.Corporations/CorporationWire.cs`. Existing database coverage in `dotnet/tests/Eve.Corporations.Tests/CorporationProtocolTests.cs:2111-2168` exercises access, CEO eligibility, vote case creation/listing, options, ballot replacement, and ballot reads via direct service calls.

## Client paths and consumers

- `eve/client/script/ui/services/corporation/voting.py:46` binds `sm.RemoteSvc('voteManager')`. The wrappers call `GetVoteCasesByCorporation` (lines 64, 110-118), `GetVoteCase` (120-130), `GetVoteCaseOptions` (132-141), `GetVotes` (143-149), `CanViewVotes` (151-158), `CanRunForCEO` (189-195), `InsertVoteCase` (236), `InsertVote` (239-241), `GetCorpSanctionedActions` (247), and `ActivateSanctionedAction` (263).
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_votes.py:842-950,983-1012,1024` consumes vote case lists, options, personal ballots, and vote/CEO actions. `eve/client/script/ui/shared/neocom/corporation/corp_ui_votes.py:344-347` also uses open vote cases and options to identify blueprint unlock votes.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_sanctionableactions.py:89-95,139-140,157-172,217-218` consumes vote visibility, vote cases, sanctioned actions, and general-vote option text. `eve/client/script/ui/shared/neocom/corporation/corporationWindow.py:307` uses vote visibility to show voting panels.
- Vote notification consumers: `voting.py:76-87` invalidates case/option/ballot caches for `OnVoteCaseChanged` and ballots for `OnVoteCast`; `voting.py:105-107` clears sanctioned-action cache. `corp_ui_votes.py:771-772,809-819` reloads voting content, `base_corporation_ui.py:60-61,189-200` forwards vote changes, and `corp_ui_sanctionableactions.py:33,81-82` refreshes the sanctioned-action panel.

## Rows still open

All are P1 and still `IMPLEMENTED-UNTESTED` in `docs/project/service-audit/EVE_RS_COVERAGE.csv`:

- RPC-02796 `ActivateSanctionedAction`
- RPC-02797 `CanRunForCEO`
- RPC-02798 and RPC-02799 `CanViewVotes`
- RPC-02800 `GetCorpSanctionedActions`
- RPC-02801 `GetVoteCase`
- RPC-02802 and RPC-02803 `GetVoteCaseOptions`
- RPC-02804 and RPC-02805 `GetVoteCasesByCorporation`
- RPC-02806 and RPC-02807 `GetVotes`
- RPC-02808 `InsertVote`
- RPC-02809 `InsertVoteCase`

## Validation

No tests or builds were run; no PostgreSQL database was used. No CSV status was advanced because no registered Host-wire test passed.
