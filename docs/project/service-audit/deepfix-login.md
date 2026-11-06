# Login lifecycle deep fix: CEO biomass restriction

## Scope and finding

Audited the V24.01 character-selection deletion path end to end. A player can queue a character for biomass and finalize deletion. The server previously checked account ownership, offline state, queue state, and timer, but never checked whether the character was CEO of a player corporation. That allowed deletion of a corporation CEO while leaving the corporation's CEO reference dangling.

The EVE University [Accounts guide](https://wiki.eveuniversity.org/Accounts) says a CEO of a player-run corporation must resign or close the corporation before entering the biomass queue. eve.rs now enforces this at both queueing and finalization, so a character appointed CEO during the 10-hour wait is also protected. The guard uses the registered Host's authoritative CorporationStore lookup and blocks only an active, non-NPC corporation where CeoId matches the character.

## Client evidence and wire behavior

Whole-tree search of `/home/user/eve-logic` found the two production client consumers in `eve/client/script/ui/login/charSelection/characterSelection.py`:
- `SubmitToBiomassQueue`, lines 861-868, asks for confirmation, calls `PrepareCharacterForDelete(charID)`, displays the returned wall-clock deadline, then refreshes that slot.
- `DeleteCharacter`, lines 835-859, asks for confirmation, calls `DeleteCharacter(charID)`, displays the returned error prompt when present, and refreshes the list only after success.

No other client caller of either RPC was found by whole-tree exact-name search. The Host wire regression checks that queueing/finalization errors are sent as Macho ErrorResponse and that the method-level CustomInfo contains the player-facing CEO instruction. It also checks a non-CEO can queue, and a character promoted to CEO before timer expiry cannot finalize deletion; the queued and not-deleted DB state remains intact.

## Implementation evidence

- `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs:507-518`: queue RPC checks the CEO restriction before returning or setting the deadline.
- `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs:522-550`: finalization checks the restriction before queue/timer validation and deletion mutation.
- `dotnet/src/Eve.Host/Rpc/CoreRpc.cs:77-91`: production service registration supplies the persisted corporation lookup.
- `dotnet/tests/Eve.Host.Tests/CharUnboundManagerClientHostWireTests.cs:58-108`: Host wire and database regression coverage, including promotion during the timer.
- `dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:468-500`: existing queue persistence, expiry, cancellation, and deletion rejection coverage.
- `dotnet/tests/Eve.Services.CharacterSelect.Tests/CharUnboundMgrTests.cs:550-571`: existing ownership/offline/cancel queue coverage remained green.

## Reference comparison

EveJS 261028.1's `server/src/services/character/characterDeletionRuntime.js:672-693` queues after ownership/offline checks; `:716-799` finalizes deletion, but neither path checks CEO status. eve.rs intentionally differs based on the higher-priority EVE University mechanic. The normal 600-minute queue and returned deadline remain unchanged.

## Verification

Before the fix, the new Host wire regression failed because CEO queueing raised no error. After the fix, the focused Host wire regression passed on `eve_p6` (1 passed, 0 skipped), the `CharUnboundMgrTests` class passed on `eve_p6` (35 passed, 0 skipped), and the Host deletion/wire selection passed on `eve_p6` (22 passed, 0 skipped). The real graphical client was not used.

The login handshake, account-creation policy, and new-character creation/economic initialization were not audited end to end in this change. This record is limited to the biomass lifecycle path.
