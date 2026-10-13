# Industry job lifecycle: Host integration contract

This contract accompanies `IndustryJobsService`'s typed `IIndustryJobOperations` port. It keeps the
industry RPC adapter in `Eve.Services.Core` independent of the Inventory, Wallet, SDE, and Events
projects. Host owns composition and one PostgreSQL transaction per lifecycle operation.

## Registration

`IndustryJobsService` now requires `IIndustryJobOperations` as its second constructor argument:

```csharp
new IndustryJobsService(readIndustryJobs, industryJobOperations, currentFileTime, ...)
```

**現状:** `Program.cs` applies migration `001_jobs`, constructs `PersistenceIndustryJobOperations`, and passes `ReadJobsAsync` plus the operations provider to `IndustryJobsService`. **コードあり:** DB transaction behavior for item custody, wallet settlement, job state, and outbox is not yet covered by DB/Host tests or a successful build. Keep using the existing inventory, wallet, and event stores; do not add parallel caches.

The existing `readJobs` callback must ultimately read/project rows from `industry.jobs`, so the existing
wire reader sees the same authoritative records the mutation port writes. A JSON projection for the existing
marshal adapter is acceptable; writing a second `GameStore` job table is not.

## Typed requests and RPC wire contract

The Core port receives `IndustryJobActor` from the authenticated session and a typed request parsed from the
client's single `job.dump()` payload. Only selections/identifiers/runs are accepted. The Host operation must
re-read the blueprint item, current SDE activity/material/product/skill rows, facility/service state, owner
access, current skills/range, wallet division rights, inventory custody, and price inputs. Client cost, duration,
materials, probability, and outcome values are never authoritative.

| RPC | Arguments | Successful reply | Host transaction |
|---|---|---|---|
| `InstallJob` | one job payload | committed `jobID` as `PyInt` | Revalidate and quote; lock blueprint/material custody; consume required materials; settle the selected personal or current corporation wallet; lock the blueprint; insert job + retry receipt; publish `industry.job.changed`. Return only after commit. |
| `CompleteJob` | `(jobID, solarSystemID)` | `None` | Lock and revalidate an installed/ready job at the requested system; create the activity result in the selected output location, apply blueprint/research outcome, unlock/return the blueprint, set terminal status/result, and publish the event in the same transaction. |
| `CompleteManyJobs` | one list of `(jobID, solarSystemID)` pairs | `None` | Preflight every job and output, then complete the entire list atomically. An empty list is a no-op. A failure on any entry rolls back all outputs, status changes, and events. |
| `CancelJob` | `(jobID, solarSystemID)` | `None` | Revalidate and lock the job, return/unlock its blueprint, mark it cancelled, and publish the event in the same transaction. No material or installation-fee refund. |

`OperationId` is `industry:{characterID}:{method}:{Macho CallId}`. Repeating an install with the same key
and same request returns the original job ID; reusing it with different request data is a conflict. Completion
and cancellation must use conditional status transitions while holding the job row lock, so a retry cannot
duplicate products or blueprint changes. Batch completion must use one transaction.

`IndustryJobUserErrorException.MessageKey` is translated by Core to the normal
`eveexceptions.UserError` Macho envelope. Host translates known business rejections (wrong owner, missing
role/skill, unavailable activity/service, missing materials/funds, wrong state/system) to the client-facing
industry error key; unexpected storage failures remain server errors and never return success.

## Existing transaction APIs are compatible

This was verified against the active implementations, not taken from their constructors:

- `PgItemStore.LockAndGetAsync`, `CreateAsync`, `MoveAsync`, and `SetQuantityAsync` accept the caller's
  `NpgsqlConnection` and `NpgsqlTransaction`; they do not open a separate connection for those operations.
- `WalletTransactions.AdjustAsync(connection, transaction, WalletAccount, ...)` delegates to
  `SettleAsync` with that same pair. `SettleAsync` preflights all wallet balances, enforces
  `WalletLimits.MaximumIsk`, writes journal/settlement receipts, and leaves commit/rollback to its caller.
- `EventBus.PublishAsync(connection, transaction, ...)` inserts into the outbox in the caller's transaction.
  The overload without connection/transaction creates its own transaction and must not be used for this
  lifecycle path.

Therefore Host can commit job state, existing item custody calls, wallet settlement, and the outbox record
atomically on a single connection/transaction without extending those APIs. After commit, call the existing
wallet notification hook as required; do not announce a pre-commit balance.

## Migration DDL

Apply once in the `industry` schema through the repository migration runner. The inventory item row remains
the blueprint lock/custody authority; there is no parallel blueprint lock table or material ledger. The
activity snapshot records the authoritative result recipe selected at install time, not a second inventory.

```sql
CREATE SCHEMA IF NOT EXISTS industry;

CREATE TABLE IF NOT EXISTS industry.jobs (
    job_id                      bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    install_operation_id        text NOT NULL UNIQUE,
    install_request_fingerprint text NOT NULL,
    activity_id                 integer NOT NULL,
    blueprint_item_id           bigint NOT NULL REFERENCES inventory.items(id) ON DELETE RESTRICT,
    blueprint_type_id           integer NOT NULL,
    blueprint_copy              boolean NOT NULL,
    blueprint_location_id       bigint NOT NULL,
    blueprint_location_flag_id  integer NOT NULL,
    facility_id                 bigint NOT NULL,
    owner_id                    bigint NOT NULL,
    installer_id                bigint NOT NULL,
    completed_character_id      bigint NOT NULL DEFAULT 0,
    solar_system_id             bigint NOT NULL,
    station_id                  bigint NOT NULL DEFAULT 0,
    input_location_id           bigint NOT NULL,
    input_flag_id               integer NOT NULL,
    output_location_id          bigint NOT NULL,
    output_flag_id              integer NOT NULL,
    status                      integer NOT NULL,
    start_date_filetime         bigint NOT NULL,
    end_date_filetime           bigint NOT NULL,
    pause_date_filetime         bigint NULL,
    runs                        integer NOT NULL CHECK (runs > 0),
    licensed_runs               integer NOT NULL DEFAULT 0 CHECK (licensed_runs >= 0),
    successful_runs             bigint NOT NULL DEFAULT 0,
    cost_isk                    bigint NOT NULL CHECK (cost_isk >= 0),
    time_in_seconds             integer NOT NULL CHECK (time_in_seconds >= 0),
    probability                 double precision NOT NULL DEFAULT 1,
    product_type_id             integer NULL,
    optional_type_id            integer NULL,
    optional_type_id2           integer NULL,
    payment_owner_id            bigint NOT NULL,
    payment_account_key         integer NOT NULL CHECK (payment_account_key BETWEEN 1000 AND 1006),
    activity_snapshot           jsonb NOT NULL,
    result_snapshot             jsonb NULL,
    output_item_ids             bigint[] NOT NULL DEFAULT '{}',
    created_at                  timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at                timestamptz NULL,
    CHECK (payment_owner_id = owner_id OR payment_owner_id = installer_id)
);

CREATE INDEX IF NOT EXISTS industry_jobs_owner_history
    ON industry.jobs (owner_id, job_id);
CREATE INDEX IF NOT EXISTS industry_jobs_installer_active
    ON industry.jobs (installer_id, status, job_id);
CREATE INDEX IF NOT EXISTS industry_jobs_facility_active
    ON industry.jobs (facility_id, status, end_date_filetime);
```

Migration/implementation notes:

- Keep `status` values compatible with the existing Core response mapper (`1` installed, `2` paused, `3`
  ready; terminal reference values are `>=100`). Do not add a second job-state table.
- `blueprint_locked` on `inventory.items` is authoritative. Lock/unlock it in the same transaction as the
  job transition; never infer the lock from a cache or only from `industry.jobs`.
- `activity_snapshot` contains the SDE-derived output recipe and activity-specific blueprint outcome needed
  at delivery (for example products, copy/research attributes, and invention result distribution). It does
  not contain material ownership or substitute for item custody.
- Material quantities are removed from existing inventory at installation. A user cancellation does not
  restore consumed materials or the installation fee; service-module removal/structure destruction also
  cancels jobs without material refund. Offline manufacturing/research services pause jobs until online.
- Install fee settlement and `industry.job.changed` outbox insert occur in the same transaction as item
  custody and job insertion. `CompleteJob`, each member of `CompleteManyJobs`, and `CancelJob` insert their
  event only with the committed transition. Event key should be `OperationId`.
- Publish payload as `IndustryJobChangedEvent` (`JobId`, `OwnerId`, `BlueprintId`, `InstallerId`, `Status`,
  `SuccessfulRuns`, `OperationId`). A Host outbox subscriber sends the client's `OnIndustryJob` positional
  values `(jobID, ownerID, blueprintID, installerID, status, successfulRuns)` to affected owner/installer
  sessions after commit. The client itself raises its ready notification when its local timer observes the
  persisted end date.

## Behavior references

CCP documentation has precedence for game rules: [Activities and Job Types](https://support.eveonline.com/hc/en-us/articles/203210272-Activities-and-Job-Types),
[Manufacturing](https://support.eveonline.com/hc/en-us/articles/203210292-Manufacturing),
[Roles Listing](https://support.eveonline.com/hc/en-us/articles/203217712-Roles-Listing),
[Upwell Structures Quick Facts](https://support.eveonline.com/hc/en-us/articles/209985225-Upwell-Structures-Quick-Facts),
and [Industry User Interface](https://support.eveonline.com/hc/en-us/articles/203270121-Industry-User-Interface).
The Wiki supplements manual-cancellation and delivery behavior:
[EVE University Manufacturing](https://wiki.eveuniversity.org/Manufacturing).

The pinned client contract is [`industrySvc.py` at `1dbb5711a072a146e1d228365668ac05655eefe1](https://github.com/Fokiiiiiii/eve-logic/blob/1dbb5711a072a146e1d228365668ac05655eefe1/eve/client/script/industry/industrySvc.py):
install passes `job.dump()`, single completion/cancellation pass `jobID, solarSystemID`, and batch completion
passes one list of those pairs.
