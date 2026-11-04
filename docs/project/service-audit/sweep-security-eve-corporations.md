# eve.rs Corporation Security Sweep

Date: 2026-11-04  
Scope: `dotnet/src/Eve.Corporations`, its tests, and read-only tracing of the RPC, V24.01 client, and EveJS flows.

## Confirmed issue and fix

`CorporationStore.Shares.cs:390-409` treated every nonzero corporation role mask as vote-reading authority. Because `CorporationStore.Votes.cs:48,150,162,191,217` reuses this predicate for vote creation and case, ballot, and sanction reads, a member with only Personnel Manager could inspect vote data and create cases.

The predicate now requires the Director bit with a parameterized bitwise check. The active CEO, direct positive shareholding, and active CEO of a shareholding corporation paths remain intact. The RPC still returns the same `PyBool` shape through `CorporationWire.cs:332-338`.

`CorporationStore.Votes.cs:149-191` also resolved expired cases before checking the reader. Since the unauthorised read then returned an empty list or `null` normally, the transaction committed the resolution of a vote in a corporation supplied by the caller. `GetVoteCasesByCorporation` takes that corporation ID from RPC argument 0 (`CorporationWire.cs:261-284`). The resolver now locks the corporation and checks the reader in the same transaction before it resolves cases (`CorporationStore.Votes.cs:454-465`). Vote-case creation, ballot writes, and sanction activation pass their actor to the same guard; the trusted scheduled resolver still handles all due corporations.

## Client and reference evidence

A whole-client Serena search found these consumers:

- `eve/client/script/ui/services/corporation/voting.py:62-64,110-159` loads vote cases, caches them for the current corporation, and forwards an explicit corporation ID to the remote calls for another corporation.
- `eve/client/script/ui/shared/neocom/corporation/corporationWindow.py:307` gates the voting panels.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_votes.py:342-350,842-846,880-885,911-917,950-987` consumes vote cases, options, and ballots for locked blueprints and open/closed tabs.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_sanctionableactions.py:89-103,137-140,157-180,215-220` gates sanctionable-action data; its denial label is `MustBeCEODirectorOrShareholder` at line 90.
- `voting.py:266-272` checks open vote cases when assessing blueprint locks. The server keeps the access check on these reads.

EveJS `voteRuntimeState.js:771-791` allows the target corporation's CEO, a director, or a character with positive vote weight. `Handle_GetVoteCasesByCorporation` requires that authority before listing cases (`voteManagerService.js:165-189`); the shared guard also protects case, option, and sanction reads. The V24.01 denial hint names the CEO/director/shareholder audience. EVE University states that share owners can vote even when not corporation members, and that a share owner can propose a vote (`Corporation logistics#Corporation shares/#Corporation votes`, <https://wiki.eveuniversity.org/Corporation_votes>). The existing shareholder access path is therefore preserved.

## Other review notes

The corporation schema identifier is constrained before interpolation (`CorporationStore.cs:36-38`). Share transfers use locked balances, overflow checks, a transaction, and replay receipts (`CorporationStore.Shares.cs`). Vote text and option bounds are enforced at the RPC boundary (`CorporationWire.cs:348-363,430-443`). No additional confirmed issue was found in the reviewed paths. A real graphical-client session was not part of this check.

## Verification

- Before the role fix, the DB-backed test observed `PyBool.True` for a non-shareholding Personnel Manager where it expected false. Before the resolver guard, it also observed a committed resolution from an unauthorised read (expected 0 rows, got 1).
- After both fixes, `Vote_access_is_restricted_and_unauthorized_reads_do_not_resolve_votes` passed on `eve_p9`.
- The `Eve.Corporations` and `Eve.Corporations.Tests` projects built with 0 warnings and 0 errors.
- The full `Eve.Corporations.Tests` project passed on `eve_p9`: 181 passed, 0 skipped. The Host vote manager RPC flow test also passed on `eve_p9`: 1 passed, 0 skipped.
