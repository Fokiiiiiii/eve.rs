# Development databases and parallel runs

One PostgreSQL 18 cluster (`tools/dev/pg18.sh start`, 127.0.0.1:5432) can hold several databases. Each database
has its own lock, so agents that use different databases run their PostgreSQL tests and live runs at the same time.

## The databases

| Database | Lock file | Live run port (`EVE_LIVE_PORT`) |
|----------|-----------|---------------------------------|
| `eve` (the default) | `/tmp/eve-heavy.lock` | 26100 (the default) |
| `eve_p2` | `/tmp/eve-heavy-eve_p2.lock` | 26101 |
| `eve_p3` | `/tmp/eve-heavy-eve_p3.lock` | 26102 |
| `eve_p4` | `/tmp/eve-heavy-eve_p4.lock` | 26103 |

Every database holds the static data (`sde` and `sde_mission`, copied from `eve`), which the tests and the live run
read. Tests create their own scratch schemas and drop them, so two runs on one database do not share their rows. The
live run creates `live_<pid>_*` schemas and drops them at its end.

## Pick a database

PostgreSQL tests take the database from `EVE_PG_URL`:

```sh
export EVE_PG_URL="$(tools/dev/pg18.sh url eve_p2)"                                   # Rust and eve-sde tests
export EVE_PG_URL='Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p2'        # .NET tests (not the postgres:// form)
```

Run every PostgreSQL test and live run under the lock of its database:

```sh
EVE_PG_URL=... tools/dev/db-lock.sh dotnet test <project> --no-build --blame-hang-timeout 120s
EVE_PG_URL=... tools/dev/db-lock.sh cargo test --workspace
```

`db-lock.sh` takes the lock of the database named in `EVE_PG_URL` (unset means `eve`), waits up to 2 hours for it and
kills the command after 30 minutes.

## Live run on another database

The live run has one fixed port, the front (`EVE_LIVE_PORT`, default 26100). The host and the simulation use private
sockets. A second live run needs its own port; the script refuses a port that already accepts connections:

```sh
EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p2 EVE_LIVE_PORT=26101 tools/dev/db-lock.sh tools/dev/run-eve-live.sh
```

The EveJS reference run (`tools/dev/run-evejs-live.sh`) also reads `EVE_LIVE_PORT` (default 26000). Give it its own
value, or run it alone.

The host seeds its NPC market from ESI by default (`EVE_MARKET_SEED=auto`). A machine without access to ESI needs
`EVE_MARKET_SEED=skip`; without it the host waits in the market seed and the script gives up after 60 s.

## Create another database

```sh
tools/dev/copy-sde-database.py "$(tools/dev/pg18.sh url)" eve_p5
```

It copies the `sde` and `sde_mission` schemas from `eve` with `psql` (about a minute, 0.7 GB of disk). It never changes
`eve`. Keep at least 8 GB of the disk free before you create one.

## Durability

`tools/dev/pg18.sh start` runs the development cluster with `fsync`, `full_page_writes` and `synchronous_commit` off:
the tests do not need durability. A crash can then corrupt the cluster; recover by `initdb`ing a new one.

## MITM listen ports per lane

The MITM capture (`tools/mitm`, `docs/project/MITM_COMPARE.md`) listens on two ports per lane. Lanes share the host, so each lane uses its own pair; take the pair from this table and do not use another lane's.

| Lane (`EVE_PG_URL` database) | MITM port A | MITM port B |
|---|---|---|
| `eve` | 26110 | 26210 |
| `eve_p2` | 26111 | 26211 |
| `eve_p3` | 26112 | 26212 |
| `eve_p4` | 26113 | 26213 |
