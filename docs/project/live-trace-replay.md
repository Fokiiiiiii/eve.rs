# Live MITM trace replay

`tools/mitm/mitm replay CAPTURE --conn N` reads the existing one-JSON-object-per-line format written by
`tools/mitm/mitm capture`. It replays client-to-server `CallReq` and `Notification` packets in recorded order through a
fresh headless-client session. The Host produces every reply; recorded replies are used only to pair requests and compare
the shapes of successful results.

Each `CallReq` waits up to 20 seconds by default. The report lists each `ErrorResponse`, each call that gets no reply
before its timeout, and every reply-shape difference for a recorded success. Client `Notification` packets are
one-way, so the replayer does not expect a normal reply for them. Unsupported records and requests that cannot be
replayed are reported as send issues. `mitm replay` exits nonzero for unexpected errors, missing replies, send issues,
or shape differences.

Reply-shape comparison follows the nested marshal structure, including `ObjectEx` record fields, `SubStruct`, and
`Checksummed` wrappers; it compares field types and container shape while ignoring leaf values.

## Run it

The live runner starts `eve-sim`, Eve.Host, and `eve-net` in the same order used by the live scenario tests. With the
pinned SDE available under `.cache/sde` and PostgreSQL ready:

```sh
EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p6 \
EVE_MARKET_SEED=skip \
EVE_RUN='tools/mitm/mitm replay tools/headless-client/fixtures/live-trace-conn4-first400.jsonl.gz --conn 4' \
tools/dev/run-eve-live.sh
```

For another capture, replace the fixture path and connection number. Input may be plain JSONL or `.jsonl.gz`. `--host
host:port` selects an already running server; otherwise `EVE_LIVE_HOST` supplies the address. `--timeout 30` changes
the per-call wait, `--time-scale 0.5` plays the gaps between requests at half duration, and `--allowlist path.json`
selects another error allowlist.

By default the command uses a unique local account and random password. If a trace selects a character without creating
one, it creates a real pilot on a setup connection using the `CreateCharacterWithDoll` arguments in
`oracle/fixtures/scenarios/login_to_character.json`. The replay connection then selects that persisted pilot and maps
the trace's character ID to its new ID. Captured handshake credentials are not used. `--user` and `--password` can
instead supply an account for a replay run.

The push CI live job runs `tests/test_trace_replay.py` through `tools/dev/run-eve-live.sh`, with the real Host and
simulation started. The offline Python run skips only the live replay test when no `EVE_LIVE_HOST` is set; that skip
does not substitute for the live CI run. The live test requires no unexpected `ErrorResponse`, no missing reply, no
send issue, and every current allowlist signature to remain observed. Shape differences are collected by the replay
report but remain report-only in this baseline test because the owner trace uses an existing pilot while CI creates a
fresh pilot. Running the CLI directly returns nonzero for a shape difference.

## Fixture and known errors

`live-trace-conn4-first400.jsonl.gz` contains the first 400 records for connection 4 from the owner's 2026-11-14
capture. It is 181,787 bytes compressed and uses the capture's existing fields; no replay-only fields were added to
the mitm recorder. The login `user_password_hash` is replaced by a zero-filled value of the same length. The owner
trace's `ClientHasReleasedTheseObjects` packet uses the older capture representation: the numeric broadcast address
is in `method`, and the undecoded payload is retained in `$unknown`. Replay recovers the actual method and released
object/ref pairs from that payload, derives the destination from the recorded broadcast address, and rebuilds the
`OID-` metadata.

The exact service, method, and UserError reason signatures in
`tools/headless-client/fixtures/live-trace-replay-allowlist.json` are reported as allowed failures while their
`60-fix-live-*` jobs land. The live test fails if an allowlist entry stops appearing, so a fixed signature must be
removed. The matcher does not allow different errors from those services. Missing replies, replay-send issues, and
shape differences are never allowlisted.

## Protocol evidence

- V24.01 loads character selection data through `charSelection.py:209,315-316` and `ccSvc.py:39-43`. Selecting a pilot
  runs `SelectCharacterID` inside `sessionMgr.PerformSessionChange` in
  `eve/client/script/ui/login/charSelection/characterSelection.py:705-714`. Character creation delegates to
  `charUnboundMgr.CreateCharacterWithDoll` in `/home/user/eve-logic/eve/client/script/ui/services/ccSvc.py:92-99`,
  called by the creation UI at `/home/user/eve-logic/eve/client/script/ui/login/characterCreationContainer.py:1840`;
  `_LoadGame` then performs the corresponding character-select transition at `ccSvc.py:185-187`.
- Bound-object calls carry `(objectID, method, args, kwargs)` in the call body, as V24.01 builds it in
  `/home/user/eve-logic/carbon/common/script/net/ObjectCallGPCS.py:387-416`. The client binds the object to the
  session in `/home/user/eve-logic/carbon/common/script/sys/service.py:606-617`. Existing mitm records omit the
  remote object handle, so replay learns handles from preceding bind replies and resolves service-less methods through
  the client-side moniker call paths. Examples include inventory in
  `/home/user/eve-logic/eve/client/script/environment/invCache.py:501-559`, skills in
  `/home/user/eve-logic/eve/client/script/ui/services/skillsvc.py:128-142` and
  `/home/user/eve-logic/eve/client/script/ui/services/skillQueueSvc.py:116-117`, and corporation registry calls in
  `/home/user/eve-logic/eve/client/script/ui/services/corporation/bco_applications.py:70-72`.
- V24.01's one-way release notification unpacks the method and `(objectID, refID)` pairs, then unregisters the matching
  session references in `/home/user/eve-logic/carbon/common/script/net/ObjectCallGPCS.py:182-195`. EveJS performs the
  same release handling from `OID-` metadata in
  `/home/user/evejs-261108.1/server/src/network/packetDispatcher.js:842-874`. It extracts a bound object's service
  from a service-less call's `remoteObject` at `packetDispatcher.js:541-550` and registers returned object handles at
  `packetDispatcher.js:1126-1165`.
- The setup and selection calls use EveJS's working character paths:
  `/home/user/evejs-261108.1/server/src/services/character/charService.js:1131-1155,1575-1609`. This keeps replay
  state real and account-owned; the harness does not send recorded success replies or fabricate a character ID.
- The trace includes the fitting errors on the owner flow. V24.01 requests corporation fittings through
  `corpFittingMgr.GetFittings` and `GetCommunityFittings` in
  `/home/user/eve-logic/eve/client/script/environment/fittingSvc.py:388-430`; those exact ErrorResponse signatures are
  the temporary allowlist entries. This verification uses the recorded V24.01 session and the cited client paths. No
  graphical-client run was performed for this harness.
