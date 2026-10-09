# Reference traces for client-visible work

## The rule

For any client-visible RPC or notification work, capture an EveJS reference trace through the MITM proxy and
diff eve.rs against it before declaring the work done.

"Client-visible" means the bytes the unmodified client sends or receives change: a new or changed service method,
a changed result or error, a notification, a session change, the order of answers. Unit tests of a handler do not
replace this check, because they cannot show what the client sees.

## How

1. Capture the EveJS run: start `tools/mitm/mitm capture --upstream 127.0.0.1:26000 --listen 127.0.0.1:26200 --out
   captures/evejs-<scenario>.jsonl`, then run the scenario through the proxy (`EVE_LIVE_HOST=127.0.0.1:26200`) with
   `tools/dev/run-evejs-live.sh`.
2. Capture the eve.rs run the same way (`--upstream 127.0.0.1:26100`, `tools/dev/run-eve-live.sh`, the same test
   selection through `EVE_RUN`).
3. `tools/mitm/mitm diff captures/evejs-<scenario>.jsonl captures/eve-rs-<scenario>.jsonl --ignore-volatile`.

The steps, the diff rules and what the proxy does not cover are in [`tools/mitm/README.md`](../../tools/mitm/README.md).
Captures stay local (`captures/` is git-ignored).

## Before declaring done

- `mitm diff` reports no divergence, or each divergence is either a bug to fix or a justified deviation recorded in
  `docs/reference/compat/DEVIATIONS.md` with the client evidence.
- If the headless client cannot drive the scenario, say so in the report and write the missing test first; a
  scenario that was not captured is not verified.
- Report the capture as what it is: a headless run, not a run with the graphical client.
