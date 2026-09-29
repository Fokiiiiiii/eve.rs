# ADR 0003: Domain modules own their data; `Eve.GameStore` is for the legacy import

- Status: accepted
- Date: 2026-09-29
- Decided by: the repository owner's direction (ADR 0001); the details are the implementer's.

## Context

`Eve.GameStore` was built first (milestone M2) to make a direct port of EVE.js possible: one JSON document per
legacy table, a synchronous cache-first API, owner leases and a write-ahead outbox. `docs/PLAN.md` marked it
transitional and asked for a decision at the start of S2.

Slice S2 has since built three modules the other way: `identity`, `characters` and `inventory` each own a
PostgreSQL schema with typed tables and embedded migrations (`Eve.Data.Migrations`), and the static data is
read from the `sde` schema through typed catalogs. None of them touches `Eve.GameStore`. The only code that
did was one adapter that read an Omega expiry for `subscriptionMgr`, which the host never used.

## Decision

1. **A domain module owns its schema.** Typed tables where the shape is known, `jsonb` where it is not, and
   migrations that run at start. No module reads or writes another module's tables. It offers an interface
   (`IAccountLookup`, `PgCharacterStore`, `PgItemStore`), and the other side calls that.
2. **A change that must be atomic across modules runs in one transaction the caller owns and passes down.**
   All modules share one database, so `CharacterCreation` inserts a character and its ship in one
   transaction (the character store and the item store each take the connection and transaction). A module
   never opens a transaction that another module's write must join behind its back.
3. **`Eve.GameStore` and `Eve.GameStore.Import` are the bridge from EVE.js, and nothing else.** They stay for
   S6 (a player moves from EVE.js to eve.rs with their data): the importer reads `gamestore.sqlite`, and a
   module's own importer maps a legacy table into the module's tables. Their fixtures keep pinning the
   legacy format. No service, module or the host builds on them. The owner-lease and fencing ideas may be
   reused where a module needs a single writer (world state in S4), as an idea, not as a dependency.
4. **A domain module knows no protocol.** It does not reference `Eve.Marshal`, `Eve.Macho`, `Eve.Services*` or
   `Eve.Host`. Protocol adapters (`Eve.Services.*`) turn client calls into calls on modules, and back.
5. **What a module does not own yet is a constant marked as a gap**, listed in `docs/compat/DEVIATIONS.md`
   under "Not yet matched", and replaced when the owning module exists. It is never read from the legacy
   store as a shortcut.
6. **The rules are checked**: `dotnet/tests/Eve.Host.Tests/ArchitectureTests.cs` reads the project files and
   fails if a domain module references a protocol or service project, if the modules reference each other in
   a circle, or if anything but the importer references the legacy store.

## Consequences

- The Omega adapter moved to `Eve.GameStore.Import` (`LegacyOmegaAccounts`), so `Eve.Services.Core` no
  longer references the store. `subscriptionMgr` reads Omega expiry through `IOmegaAccountStore`; the subscription
  module will implement it.
- Each new module costs a migration, an interface and tests. In return it can be tested against a scratch
  schema alone, and the storage of one module can change without the others.
- S6 has to write a mapping per legacy table. The legacy tables that no module covers are simply not imported
  until the module exists.
- Decisions still open, each for its own ADR when a slice needs it: where dogma lives (a Rust library the host
  can also use, per `docs/PLAN.md`), how world state is owned and recovered in S4, and how the simulation
  talks to the host.

## Alternatives

- **Build the modules on `Eve.GameStore`.** Rejected: it keeps the JSON-blob-per-table model and a synchronous
  cache whose aliasing hazards are recorded in `docs/NATIVE_MIGRATION.md`; typed tables are what the goal
  (maintainable) asks for.
- **Delete `Eve.GameStore` now.** Rejected: S6 needs the importer, and the fixtures that pin the legacy
  format are the evidence it maps correctly.
