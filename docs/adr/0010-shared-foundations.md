# 0010: Shared foundations for timers, events, relations, content, effects and read models

Status: accepted (owner's instruction, 2026-10-02)

## Context

Every new feature (missions, anomalies, incursions, crimewatch, structures, industry, fleet) needs the same things: do something later,
announce that something happened, store a relation between two owners, run objectives with a deadline, run what a module does each cycle,
and answer the client with lists of records. Left to the features, each would add its own timer, its own event hook, its own table of running
things and its own branch in a switch, and the code would stop being easy to change.

## Decision

Six shared foundations, each a module with its own PostgreSQL schema (ADR 0003), embedded migrations and an injected clock:

1. `Eve.Scheduler`: a durable scheduler and job runner (jobs are rows; claimed with `FOR UPDATE SKIP LOCKED` and a lease; run in a transaction that
   completes them; retried with a back-off; keyed, recurring, cancellable).
2. `Eve.Events`: domain events with a transactional outbox (written in the publisher's transaction; per-subscriber cursors over `(transaction, id)` read
   below the oldest running transaction, so a late commit is waited for, never skipped; handler writes commit with the cursor; dead letters).
3. `Eve.Relations`: one store for standings, wars, security status and affiliation (typed directed relations with history, change events, expiry).
4. `Eve.Content`: a runtime for missions, anomalies, incursions (definitions, instances, objectives advanced by events, deadlines, end actions and events).
5. `Eve.Dogma` `EffectRegistry` with the space host's handlers in `Eve.Services.Space/Effects` (replacing the branch in `ModulePackages.Of`).
6. `Eve.ReadSide`: named queries over the SDE and module tables, answered as client rowsets.

A new feature uses them and does not build a private timer, event list, running-content table or effect branch. `docs/FOUNDATIONS.md` is the guide.

## Consequences

* The host creates and starts them (`Eve.Host/Foundations.cs`), after the features have registered their handlers and subscribers.
* The can sweeper and the bounty payout moved onto the scheduler; the other private timers move when their features are next touched.
* Event delivery waits for transactions that are older than it: transactions must stay short.
* The foundations need PostgreSQL; without `EVE_PG_URL` the host starts without them (as without characters).
