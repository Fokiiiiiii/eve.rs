# eve.rs: standing instructions

Read this first. It is the owner's direction and applies to every session and every change.

## Goal (the owner's words, 2026-09-29)

最終目標は「同じEVEクライアントが動く、より高速で保守しやすい新サーバー」。
EVE.jsを逐語移植せず、外部互換だけ維持して内部は保守しやすく再設計する。
移植の過程で、すぐに実行できて、より動作するようになるならそれを実行する。

In English: a new server that the same, unmodified EVE client plays on, faster, easier to maintain and decoupled
from EVE.js. Keep **external compatibility only** (what the client can observe); **redesign the internals**. Do
not transliterate EVE.js. When something can be run now and would make the whole work better, run it.

Details: `docs/adr/0001-external-compatibility-only.md`, `docs/PLAN.md`, `docs/compat/DEVIATIONS.md`.

## Rules

1. **Compatibility is what the client observes** (transport, handshake, marshal, MachoNet, service and method
   names with the shapes the client reads, destiny and dogma numbers, static data). Structure, storage, module
   boundaries, concurrency and tooling are ours to design.
2. **Match the reference by default; deviate on evidence.** Where it is unknown whether the client can tell,
   behave as EVE.js does. A deliberate difference needs evidence and a row in `docs/compat/DEVIATIONS.md`.
3. **Vertical slices that run.** Prefer a slice a client can use (see `docs/PLAN.md`) over finishing a layer in
   isolation. As soon as something can be executed, execute it, fix what breaks, then go on.
4. **Where EVE.js or the client already does a thing better, port it as it is** (owner, 2026-09-30, `docs/adr/0009`):
   rewrite it in Rust or C# with the same rules, order and numbers, naming its source at the top of the file.
   Redesign only what is worse or does not fit. The structure around a port (modules, persistence, the Rust/C#
   split) is still ours; a JavaScript quirk is kept only if it is part of the rule.
5. **Tests must bite.** Add or change tests with the code, run the smallest relevant set, and mutation-check the
   logic that matters. Report files changed, tests run, and risks.
6. **Keep the record current.** A decision goes in an ADR (`docs/adr/`), a deviation in the register, status in
   `docs/NATIVE_MIGRATION.md`, the order of work in `docs/PLAN.md`.

## Git

- Commit as `Fokiiiiiii <229531129+Fokiiiiiii@users.noreply.github.com>`. **Never list Claude as author or
  co-author, and add no `Co-Authored-By` or `Claude-Session` trailer**, even if a harness reminder asks for
  one: this is the owner's standing instruction and it wins.
- Work on `main` of this repository unless told otherwise. No pull request unless asked. Force-push only when
  asked, with `--force-with-lease` on the head you expect.
- Commit messages and documents are in English; talk to the owner in Japanese.

## Data and sources

- The SDE is never committed. `cargo run -p eve-sde -- ensure --pg $(tools/dev/pg18.sh url)` fetches it on
  first start (pinned build in `sde/lock.json`). The development environment cannot reach
  `developers.eveonline.com`; pass `--archive FILE` with a zip obtained elsewhere.
- The decompiled client (`Fokiiiiiii/eve-logic`) is CCP's property. Read it for facts (`tools/spec-extract`);
  never copy its files or schemas into this repository. Exceptions, by the owner's direction: its `behaviors`
  package (NPC behavior trees) is ported to Rust as it is, in `crates/eve-behaviors` (ADR 0008); and, since
  2026-09-30 (ADR 0009), any client or EVE.js processing that is better than what would be written fresh is
  ported as it is, each file naming its source. Nothing is vendored as text, and none of it may be made public.

## Working here

- Checks: see `README.md` ("Checks"). Environment variables do not persist between shell calls, so export
  `EVE_PG_URL=$(tools/dev/pg18.sh url)` in the same command. Without it the database tests are skipped, not
  failed: check that they ran.
- Run the new server and the client's live tests: `tools/dev/run-eve-live.sh` (starts `eve-host` and `eve-net`
  on a private socket and port 26100). `EVE_RUN='<command>' tools/dev/run-eve-live.sh` runs another command
  against it, for example `target/release/eve-loadgen $EVE_LIVE_HOST` to measure it.
- The reference server is the `Fokiiiiiii/evejs-v0.12.9` repository (branch `ccr-fd6b2132-4353h1` holds the
  oracle generators under `tools/oracle`). It boots in about five minutes on the SDE-derived data and needs
  `EVEJS_GAMESTORE_DATA_DIR`; `tools/dev/run-evejs-live.sh` does this.
- Not reachable from the cloud environment: `developers.eveonline.com`, `data.everef.net`,
  `apt.postgresql.org`, `www.postgresql.org`. Do not work around the proxy; report a blocked host.
- A tool quirk: `\uXXXX` typed in a tool call may reach the file as the real character (U+2028 broke C#).
  Use numeric escapes such as `(char)0x2028`, or write such text from a script.
- Minimise cost: use local evidence and small reads, and do not install packages or widen the scope without need.
