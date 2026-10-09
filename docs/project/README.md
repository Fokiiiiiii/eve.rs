# Project status and RPC coverage

**Verified source:** main commit `7379a70fdeee0e93d2ac38c1b7a7bd9ecd29da60`
**Updated:** 2026-10-09

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

- Before the final upstream fast-forward to `4c84224`, `dotnet test dotnet/EveRs.slnx -m:1` ran against PostgreSQL test database `eve_rs_test`: 4,282 passed, 0 failed, 0 skipped.
- After that fast-forward, affected suites passed: `Eve.Services.Space.Tests` 453/453, `Eve.Space.Tests` 217/217, and `Eve.Services.Dogma.Tests` 121/121. `cargo test --workspace --locked` passed.
- `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -c Release -m:1 --no-restore`: succeeded, 0 warnings / 0 errors.
- `git diff --check`: passed.
- Native V24.01 client verification was not run; graphical acceptance remains open.

## Closed in the latest RPC work

- The FleetProxy callbacks and FleetObject `CreateFleet`/`LoadFleetSetup` paths use FleetStore and saved character settings. Migration 006 stores the full advert payload for new writes.
- `storeManager.buy_offer` and Insurance are connected to their existing persistence providers.
- Character entry builds the supported session state, restores saved space pose, and orders selection, session, hangar, Dogma, and guest notifications. The first active-space ship List also primes visual AddBalls2 before the later full bind. PostgreSQL character-selection and TCP headless tests pass; the native V24.01 client was not launched in this verification, so graphical acceptance remains open.
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
