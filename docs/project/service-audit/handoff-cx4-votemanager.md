# voteManager coverage handoff — cx4

## Completed coverage

The Host wire regression in `dotnet/tests/Eve.Host.Tests/VoteManagerHostTests.cs` sends real RPC calls through `HostServer` to the registered `voteManager` service and a PostgreSQL-backed `CorporationStore`. It checks vote access and CEO eligibility, vote case rows and options, a personal ballot, the sanctioned-action mapping, and persisted ballot state.

The focused test passed locally on database `eve_p4` with zero skips:

`EVE_PG_URL=$(tools/dev/pg18.sh url eve_p4) tools/dev/db-lock.sh dotnet test dotnet/tests/Eve.Host.Tests --no-build --filter FullyQualifiedName~Vote_manager_rpc_flow_uses_the_host_wire_and_persists_cases_and_ballots`

The two existing `CorporationProtocolTests` vote regressions also passed on `eve_p4`: `Vote_manager_replaces_ballots_and_uses_live_share_weight` and `Outside_corporation_shareholders_can_read_and_vote_using_the_case_corporation_ledger`.

Coverage rows RPC-02797 through RPC-02807 are now `IMPLEMENTED-TESTED`, linked to that test. It exercises each read RPC by its client argument shape, including explicit corporation/status arguments for case listing.

## Rows still open

These remain `IMPLEMENTED-UNTESTED` in `docs/project/service-audit/EVE_RS_COVERAGE.csv`:

- RPC-02796 `ActivateSanctionedAction`
- RPC-02808 `InsertVote`
- RPC-02809 `InsertVoteCase`

The test uses the two insert RPCs to set up and persist a real ballot, but does not assert the notification flow, so those rows are intentionally still open. The Host vote service currently has no vote notification send call; add coverage only after the client notification contract is implemented and asserted. Sanction activation also needs a persisted eligible sanctioned vote and its resulting state transition.

## Whole-client paths

- `eve/client/script/ui/services/corporation/voting.py:46` binds `sm.RemoteSvc('voteManager')`. RPC wrappers: `GetVoteCasesByCorporation` at 64, 110-118; `GetVoteCase` at 122-130; `GetVoteCaseOptions` at 132-142; `GetVotes` at 143-149; `CanViewVotes` at 151-158; `CanRunForCEO` at 189-195; `InsertVoteCase` at 236; `InsertVote` at 239-241; `GetCorpSanctionedActions` at 247; `ActivateSanctionedAction` at 263.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_votes.py:342-347` reads open vote cases/options for blueprint unlocks; 793-795 and 1023-1025 gate CEO candidacy; 842-844 and 880-884 list votes; 911-930 reads options and personal ballots and submits votes; 950-984 displays closed cases and results; 1009-1013 submits ballots.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_sanctionableactions.py:89-103,139-140,157-180,217-218` uses vote access, cases, options, and sanctioned actions. `corporationWindow.py:307` uses vote access to show the voting panels.
- Client notification handlers are `voting.py:76-87` (`OnVoteCaseChanged(corporationID, voteCaseID)`, `OnVoteCast(corporationID, voteCaseID)`) and 105-107 (`OnSanctionedActionChanged`). Consumers include `corp_ui_votes.py:771-772,809-819`, `base_corporation_ui.py:60-61,189-200`, and `corp_ui_sanctionableactions.py:33,81-82`.

## Validation

The focused Host test passed with one test, zero failures, and zero skips; the existing corporation vote regressions passed with two tests, zero failures, and zero skips. A full graphical client run was not performed.
