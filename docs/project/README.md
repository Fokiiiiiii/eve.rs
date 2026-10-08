# Project status and RPC coverage

**Verified source:** main commit `c6c3833f56fff65f0b1a47ed17576c85204f66de`
**Updated:** 2026-10-08

This is the current project summary. The dated migration log, old service backlog, and 2026-10-06 audit snapshot were removed; keep current status here and remaining work in [TODO.md](TODO.md).

## RPC inventory

[The service manifest](../reference/spec/services.tsv) has 654 rows, including 484 unique service/method pairs across 129 services. The read-only source audit found a handler for **484/484** pairs through a declared route, alias, or manual dispatch. True missing routes: **0**.

A route count does not prove wire-shape parity or a connected runtime provider. The checked routes and open provider/behavior work are separated in [TODO.md](TODO.md).

## Compatibility evidence

- Internal RPC behavior is compared with the client call paths in EveLogic 24.01 at commit `1dbb571` and EveJS v0.12.9 at commit `828823a`.
- CCP's public developer documentation describes ESI. It does not publish the internal MachoNet `RemoteSvc` method signatures; public protocol conformance therefore cannot be inferred from ESI.
- EVE University Wiki pages describe gameplay behavior, not RPC argument or return types. Use them for game-rule checks and EveLogic/EveJS for the internal client contract.
- The manifest contains service/method names and counts only. Method-level wire evidence that has not been recorded remains unknown; do not mark it compliant by inference.

## Verification baseline

On source commit `39fcd15224b78f94d4e2c68b0dcbc8eee2d88c6e`, with the configured PostgreSQL test connection:

- `dotnet test dotnet/EveRs.slnx -m:1`: **3,859 passed, 0 failed, 0 skipped**.
- Core build: succeeded, **0 warnings / 0 errors**.
- Host build: succeeded, **0 warnings / 0 errors**.
- `git diff --check`: passed before the source commit.
- `main` was pushed to `origin/main`; HEAD and remote main matched.

The documentation reorganization is a separate, docs-only change and is checked for local links and whitespace errors.

## Closed in the latest RPC work

- The FleetProxy callbacks and FleetObject `CreateFleet`/`LoadFleetSetup` paths use FleetStore and saved character settings. Migration 006 stores the full advert payload for new writes.
- `storeManager.buy_offer` and Insurance are connected to their existing persistence providers.
- Character entry now builds the full supported session state, restores a saved in-space ship pose, and orders selection, session, hangar, Dogma, and guest notifications. Real V24.01 client login has not been run; the PostgreSQL character-selection test was skipped without `EVE_PG_URL`.
- `station.GetGuests` resolves through the `stationSvc` alias; its four-field response, observer ledger seed, and station/structure join/leave broadcasts are implemented and unit-tested.
- `industryMonitor.ConnectJob` filters the input inventory flag and excludes blueprint-category items.
- SignalTracker session registration and its full-state publish hook are wired; the snapshot producer itself remains an open provider item.
- Abyssal/Void gate fallback error shapes and callback contracts are present; the runtime gate providers remain open.

See [TODO.md](TODO.md) for the remaining work and known data limits.
## Public gameplay references reviewed

These sources were used to check user-facing game rules. They do not define the internal RPC signatures or replace EveLogic/EveJS wire evidence.

| Domain | Public reference |
|---|---|
| Fleet | [Fleet Interface](https://wiki.eveuniversity.org/Fleet_interface) |
| ESS | [Encounter Surveillance System](https://wiki.eveuniversity.org/Encounter_Surveillance_System) |
| Abyssal Deadspace and mutaplasmids | [Abyssal Deadspace](https://wiki.eveuniversity.org/Abyssal_Deadspace), [Abyssal Modules](https://wiki.eveuniversity.org/Abyssal_modules), [CCP Mutaplasmids](https://support.eveonline.com/hc/en-us/articles/360000848365-Mutaplasmids) |
| Faction Warfare and Insurgencies | [Faction Warfare](https://wiki.eveuniversity.org/Faction_warfare), [Insurgency](https://wiki.eveuniversity.org/Insurgency) |
| Industry | [Industry](https://wiki.eveuniversity.org/Industry), [Blueprints](https://wiki.eveuniversity.org/Blueprint) |
| Structures and Asset Safety | [Structure Browser](https://wiki.eveuniversity.org/Structure_Browser), [Asset Safety](https://wiki.eveuniversity.org/Asset_Safety) |
| Scanning | [CCP Scanning](https://support.eveonline.com/hc/en-us/articles/203209902-Scanning) |
| Insurance | [Ship Insurance](https://wiki.eveuniversity.org/Ship_Insurance) |
| Shipcasters | [Shipcaster](https://wiki.eveuniversity.org/Shipcaster), [CCP Exordium](https://support.eveonline.com/hc/en-us/articles/27939780240924-Exordium-Starter-Space) |

CCP's [ESI documentation](https://developers.eveonline.com/docs/services/esi/endpoints/) and [API Explorer](https://developers.eveonline.com/api-explorer) describe the public ESI API. They do not provide a public specification for MachoNet RemoteSvc calls.
