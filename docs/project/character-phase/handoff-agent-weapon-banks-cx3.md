# Weapon-bank RPC handoff (cx3)

## State

DogmaIM now serves LinkWeapons, MergeModuleGroups, PeelAndLink, UnlinkModule, LinkAllWeapons, UnlinkAllModules and DestroyWeaponBank. Fitting-schema rows persist each ship's banks. GetAllInfo includes the saved map in shipState item 3 (zero-based index 2), and the Rust sim sends a bank's modules into activation at the same simulation time. This work is partly done until the database migration and restart path receive a database-backed check.

## V24.01 client evidence

- `eve/client/script/dogma/clientDogmaLocation.py:752-782`: LinkWeapons, MergeModuleGroups and PeelAndLink receive the bank dictionary and pass it to OnWeaponBanksChanged.
- `clientDogmaLocation.py:785-803`: UngroupModule removes the numeric slave ID returned by UnlinkModule; LinkAllWeapons consumes the returned dictionary; UnlinkAllWeapons clears its local map after the call.
- `clientDogmaLocation.py:243,256,263`: the fourth shipState tuple value initializes weapon banks; `eve/client/script/environment/godma.py:2431-2445` passes the returned shipState into the dogma location.
- `eve/common/script/dogma/baseDogmaLocation.py:926-949`: the map is master module ID to slave module IDs, used by GetMasterModuleID and GetSlaveModules.
- `clientDogmaLocation.py:864-887`: DestroyWeaponBank, SetWeaponBanks and OnWeaponBanksChanged define the client cache update and refresh event. UI readers include `eve/client/script/ui/inflight/shipHud/slotsContainer.py:110-125`, `eve/client/script/ui/inflight/shipHud/shipUI.py:889-892`, `eve/client/script/ui/inflight/shipHud/fittingSlotController.py:333-355` and `eve/client/script/ui/inflight/shipHud/moduleButton.py:684-686`.
- Whole-client search covered all callers and consumers of these methods and the shipState tuple. No server notification is required after these RPCs: their replies drive the client cache update.

## Mechanics and reference

EVE University describes identical weapons as activatable together and states their shots remain separate turret shots: [Power Grid, “Don't mix guns”](https://wiki.eveuniversity.org/Power_Grid) and [Falloff, “Will grouping guns change the damage?”](https://wiki.eveuniversity.org/Falloff). The V24.01 client evidence above fixes the reply shapes. EveJS 261028.1 `server/src/services/dogma/dogmaService/weaponBanks.js` is the executable RPC reference. The service validates identical types, compatible loaded charges, online high-slot weapon modules and bank capacity. The Rust simulation receives master IDs over SimLink and starts/stops each member with the same effect timing.

## Checks and gaps

- Passed: Host tests project build; registered Host wire test for all seven RPCs and their reply shapes; Dogma GetAllInfo shipState bank slot assertion.
- Passed: Eve.Space SimCodecTests (415); Rust focused bank activation test; Rust SimLink fixtures (11), including a nonzero bank-master wire roundtrip; Rust fmt check, eve-space cargo check and workspace clippy.
- CI follow-up: the first push failed on the existing tether host policy test because it still expected SimLink protocol version 82. Updated it to 83; the exact test, fmt check, eve-space cargo check and workspace clippy pass locally. CI run 1330 for `b0209df6` passed, including Rust, Windows, end-to-end and .NET jobs.
- The task explicitly forbids PostgreSQL use. No database migration, persistence/restart test or database-backed neighbor test was run.
- EveJS 261028.1 behavior was traced in source; the live EveJS flow was not executed.
- No graphical V24.01 client session was run.
